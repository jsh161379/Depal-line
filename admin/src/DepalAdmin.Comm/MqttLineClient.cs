using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;
using DepalAdmin.Core.Settings;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace DepalAdmin.Comm;

/// <summary>
/// 실제 Mosquitto 브로커에 붙는 클라이언트 (MQTTnet 4.x).
/// 끊기면 ReconnectDelay마다 다시 붙는다.
/// 연결된 동안 HeartbeatInterval마다 hb/ui를 보낸다 (통신 규약 1장: 모든 프로그램은 하트비트를 보냄
/// → 중앙 서버 장치 목록에 "관리자 화면"이 정상으로 보임).
/// </summary>
public sealed class MqttLineClient : LineClientBase
{
    // ── 수정할 값 ──────────────────────────────────────────
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private const string DeviceName = "ui";                 // 하트비트·status 토픽에 쓰는 이름
    // status/<장치>는 JSON이 아닌 글자 그대로 (central_server.py와 확인 2026-11-09)
    private const string OnlinePayload = "online";
    private const string OfflinePayload = "offline";
    // ──────────────────────────────────────────────────────

    private static readonly string[] SubscribeTopics =
    {
        Topics.ServerLine, Topics.ServerDevices, Topics.ServerEvents, Topics.ServerResults,
    };

    private readonly AppSettings _settings;
    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _heartbeat;
    private long _hbSeq;

    public MqttLineClient(AppSettings settings)
    {
        _settings = settings;
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += _ =>
        {
            SetState(ConnectionState.Disconnected);
            return Task.CompletedTask;
        };
    }

    public override Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => KeepConnectedAsync(_cts.Token));
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        _cts?.Cancel();
        if (_client.IsConnected)
        {
            try
            {
                // 정상 종료 때는 LWT가 안 나가므로 직접 offline을 남긴다
                await PublishAsync(Topics.UiStatus, OfflinePayload, retain: true, CancellationToken.None).ConfigureAwait(false);
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MQTT] 종료 중 오류: {ex.Message}");
            }
        }
        foreach (var task in new[] { _loop, _heartbeat })
        {
            if (task == null) continue;
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    public override async Task SendCommandAsync(string action, CancellationToken ct = default)
    {
        if (!_client.IsConnected)
            throw new InvalidOperationException("브로커에 연결되어 있지 않아 명령을 보내지 못했습니다.");

        var json = JsonSerializer.Serialize(new UiCommand { Action = action, Ts = Json.Now() }, Json.Options);
        await PublishAsync(Topics.UiCmd, json, retain: false, ct).ConfigureAwait(false);
    }

    // ── 내부 ──────────────────────────────────────────────

    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    SetState(ConnectionState.Connecting);
                    await ConnectAsync(ct).ConfigureAwait(false);
                    SetState(ConnectionState.Connected);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetState(ConnectionState.Disconnected);
                    Debug.WriteLine($"[MQTT] 연결 실패: {ex.Message}");
                }
            }

            try { await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// hb/ui 발행: {"device":"ui","seq":1,"ts":1794191234.5,"state":"run"}
    /// ts는 유닉스 시각(초, 소수) — 다른 장치 하트비트와 같은 형식. seq는 프로그램을 켤 때마다 1부터.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_client.IsConnected)
            {
                try
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        device = DeviceName,
                        seq = ++_hbSeq,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                        state = "run",
                    });
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(Topics.HeartbeatPrefix + DeviceName)
                        .WithPayload(body)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)   // 하트비트는 QoS 0 (규약)
                        .Build();
                    await _client.PublishAsync(message, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MQTT] 하트비트 실패: {ex.Message}");   // 끊김은 KeepConnectedAsync가 처리
                }
            }

            try { await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.BrokerHost, _settings.BrokerPort)
            .WithClientId(_settings.ClientId)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(KeepAlive)
            // 화면이 갑자기 꺼지면 브로커가 대신 offline을 남긴다 (LWT)
            .WithWillTopic(Topics.UiStatus)
            .WithWillPayload(Encoding.UTF8.GetBytes(OfflinePayload))
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.ConnectAsync(options, ct).ConfigureAwait(false);

        var subscribe = _factory.CreateSubscribeOptionsBuilder();
        foreach (var topic in SubscribeTopics)
            subscribe.WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        await _client.SubscribeAsync(subscribe.Build(), ct).ConfigureAwait(false);

        await PublishAsync(Topics.UiStatus, OnlinePayload, retain: true, ct).ConfigureAwait(false);
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        return _client.PublishAsync(message, ct);
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.PayloadSegment;
        var json = segment.Array == null ? "" : Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count);
        if (string.IsNullOrWhiteSpace(json)) return Task.CompletedTask;   // retained 지우기용 빈 메시지

        try
        {
            switch (topic)
            {
                case Topics.ServerLine:
                    var line = JsonSerializer.Deserialize<LineStatus>(json, Json.Options);
                    if (line != null) RaiseLine(line);
                    break;

                case Topics.ServerDevices:
                    RaiseDevices(DeviceParser.Parse(json));
                    break;

                case Topics.ServerEvents:
                    var ev = JsonSerializer.Deserialize<LineEvent>(json, Json.Options);
                    if (ev != null) RaiseEvent(ev);
                    break;

                case Topics.ServerResults:
                    var result = JsonSerializer.Deserialize<BatteryResult>(json, Json.Options);
                    if (result != null) RaiseResult(result);
                    break;
            }
        }
        catch (JsonException ex)
        {
            // 형식이 규약과 다르면 이벤트 로그에 바로 보이게 한다 (디버깅용)
            RaiseLocalEvent("WARN", $"{topic} 형식 오류: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
