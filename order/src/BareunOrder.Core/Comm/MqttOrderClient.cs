using System.Text;
using BareunOrder.Core.Settings;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using BareunOrder.Core.Models;

namespace BareunOrder.Core.Comm;

/// <summary>
/// 실제 연결: 서버 PC Mosquitto(1883)에 client_id order_admin 으로 붙는다 (주문_통신_규약 v1.0).
/// - 받기: order/new, order/cancel, inquiry/new  → 원문 그대로 OrderStore로 넘김 (검사는 OrderStore)
/// - 보내기: PublishAsync(토픽, JSON, retained) — 전부 QoS 1
/// - CleanSession=false: 프로그램이 꺼져 있던 동안 들어온 주문·문의도 다시 켜면 받음
/// - 연결이 끊기면 3초마다 다시 붙고, 끊긴 동안 보낼 것은 모아 뒀다가 다시 붙으면 순서대로 보냄
/// </summary>
public sealed class MqttOrderClient : IOrderClient
{
    private readonly AppSettings _settings;
    private readonly IMqttClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<(string Topic, string Json, bool Retain)> _pending = new();
    private readonly object _lock = new();
    private bool _stopping;

    public event Action<string>? NewOrderJson;
    public event Action<string>? CancelJson;
    public event Action<string>? NewInquiryJson;
    public event Action<bool>? ConnectionChanged;

    public MqttOrderClient(AppSettings settings)
    {
        _settings = settings;
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.ConnectedAsync += async _ =>
        {
            ConnectionChanged?.Invoke(true);
            await FlushPendingAsync();
        };
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public bool IsConnected => _client.IsConnected;
    public string Description => $"MQTT {_settings.MqttHost}:{_settings.MqttPort}";
    public IReadOnlyList<Order> InitialOrders { get; } = Array.Empty<Order>();   // TODO(DB): 켤 때 DB에서 불러오기

    public Task StartAsync()
    {
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _cts.Cancel();
        if (_client.IsConnected)
        {
            try { await _client.DisconnectAsync(); } catch { /* 끄는 중이라 무시 */ }
        }
    }

    public async Task PublishAsync(string topic, string json, bool retain)
    {
        if (!_client.IsConnected)
        {
            lock (_lock) _pending.Enqueue((topic, json, retain));   // 다시 붙으면 보냄
            return;
        }
        try
        {
            await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(json))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain)
                .Build());
        }
        catch (Exception)
        {
            lock (_lock) _pending.Enqueue((topic, json, retain));   // 끊긴 순간이면 다음에 다시
        }
    }

    private async Task FlushPendingAsync()
    {
        int count;
        lock (_lock) count = _pending.Count;
        for (var i = 0; i < count; i++)                 // 지금 쌓인 것만 (보내다 또 실패하면 다음 연결 때)
        {
            (string Topic, string Json, bool Retain) m;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                m = _pending.Dequeue();
            }
            await PublishAsync(m.Topic, m.Json, m.Retain);
            if (!_client.IsConnected) return;
        }
    }

    private MqttClientOptions BuildOptions()
    {
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.MqttHost, _settings.MqttPort)
            .WithClientId(_settings.MqttClientId)
            .WithCleanSession(false);
        if (!string.IsNullOrEmpty(_settings.MqttUser))
            b = b.WithCredentials(_settings.MqttUser, _settings.MqttPassword);
        return b.Build();
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_client.IsConnected)
        {
            try
            {
                await _client.ConnectAsync(BuildOptions(), ct);
                var sub = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(OrderTopics.New).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .WithTopicFilter(f => f.WithTopic(OrderTopics.Cancel).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .WithTopicFilter(f => f.WithTopic(OrderTopics.InquiryNew).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build();
                await _client.SubscribeAsync(sub, ct);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        ConnectionChanged?.Invoke(false);
        if (!_stopping)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token); }
                catch (OperationCanceledException) { return; }
                await ConnectLoopAsync(_cts.Token);
            });
        }
        return Task.CompletedTask;
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var seg = e.ApplicationMessage.PayloadSegment;
        if (seg.Array == null || seg.Count == 0) return Task.CompletedTask;
        var json = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);

        switch (e.ApplicationMessage.Topic)
        {
            case OrderTopics.New: NewOrderJson?.Invoke(json); break;
            case OrderTopics.Cancel: CancelJson?.Invoke(json); break;
            case OrderTopics.InquiryNew: NewInquiryJson?.Invoke(json); break;
        }
        return Task.CompletedTask;
    }
}
