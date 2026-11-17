using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BareunOrder.Core.Settings;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace BareunOrder.Core.Comm;

/// <summary>
/// 채팅 메시지 1건. 관리자 프로그램(DepalAdmin) 채팅과 같은 형식이다 → 같은 방에서 대화한다.
/// MQTT 토픽 chat/messages, JSON snake_case (msg_id, from_id, from_name, station, text, ref_kind, ref_text, mentions, ts).
/// ⚠ 형식을 바꾸면 관리자 프로그램 채팅도 같이 바꿔야 한다 (서버 담당과 상의).
/// </summary>
public sealed class ChatMessage
{
    public string MsgId { get; set; } = Guid.NewGuid().ToString("N");
    public string FromId { get; set; } = "";
    public string FromName { get; set; } = "";

    /// <summary>보낸 프로그램. 주문 관리 프로그램은 "주문 관리"</summary>
    public string Station { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>붙여 보낸 것의 종류. 주문 관리에서는 "order"</summary>
    public string? RefKind { get; set; }

    /// <summary>붙여 보낸 것의 내용. 주문 관리에서는 주문번호</summary>
    public string? RefText { get; set; }

    public List<ChatMention> Mentions { get; set; } = new();

    public string Ts { get; set; } = DateTime.Now.ToString(ChatJson.TimeFormat, CultureInfo.InvariantCulture);
}

public sealed class ChatMention
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public static class ChatJson
{
    public const string Topic = "chat/messages";
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DateTime ParseTs(string? s) =>
        DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.Now;
}

/// <summary>
/// 채팅 연결. 가짜(FakeChatClient)와 실제(MqttChatClient)가 같은 모양.
/// 이벤트는 백그라운드 스레드에서 올 수 있다 → 화면은 ChatStore를 통해서만 쓴다.
/// 내가 보낸 메시지도 MessageReceived로 한 번 돌아온다 (관리자 프로그램과 같음).
/// </summary>
public interface IChatClient
{
    event Action<ChatMessage>? MessageReceived;
    bool IsConnected { get; }
    Task StartAsync();
    Task StopAsync();
    Task SendAsync(ChatMessage message);
}

/// <summary>가짜 채팅: 라인 쪽에서 가끔 메시지가 오고, 보낸 메시지에는 잠시 뒤 답이 온다.</summary>
public sealed class FakeChatClient : IChatClient
{
    private static readonly string[] LineTalk =
    {
        "오늘 2회차 시작합니다. A 등급 나오는 대로 알려 드릴게요.",
        "측면 카메라 점검 때문에 10분 정도 라인 멈춥니다.",
        "방금 회차 끝났습니다. A 등급 6개 AGV로 보냈어요.",
        "@order 출고 대기 주문 있으면 알려 주세요.",   // @ 시험용 — 주문 관리를 부름
    };

    private readonly Random _rnd = new();
    private Timer? _timer;

    public event Action<ChatMessage>? MessageReceived;
    public bool IsConnected => true;

    public Task StartAsync()
    {
        MessageReceived?.Invoke(FromLine("라인 가동 중입니다. 출고 관련은 여기로 말씀 주세요.", DateTime.Now.AddMinutes(-40)));
        _timer = new Timer(_ => MessageReceived?.Invoke(FromLine(LineTalk[_rnd.Next(LineTalk.Length)], DateTime.Now)),
            null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    public async Task SendAsync(ChatMessage message)
    {
        MessageReceived?.Invoke(message);
        await Task.Delay(2500);
        var reply = message.RefKind == "order"
            ? $"확인했습니다. {message.RefText} 출고분 다음 회차에서 먼저 보내 드릴게요."
            : "확인했습니다.";
        MessageReceived?.Invoke(FromLine(reply, DateTime.Now));
    }

    private static ChatMessage FromLine(string text, DateTime at)
    {
        var mentions = new List<ChatMention>();
        if (text.StartsWith("@order "))   // 가짜: 주문 관리 프로그램(order_admin)을 부른 메시지
        {
            text = "@주문 관리 " + text["@order ".Length..];
            mentions.Add(new ChatMention { Id = "order_admin", Name = "주문 관리" });
        }
        return new ChatMessage
        {
            FromId = "line",
            FromName = "현장 관리자",
            Station = "관리자 프로그램",
            Text = text,
            Mentions = mentions,
            Ts = at.ToString(ChatJson.TimeFormat, CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>
/// 실제 채팅: 서버 PC Mosquitto의 chat/messages (관리자 프로그램과 같은 방).
/// 주문 연결과 따로 붙는다 (client_id 뒤에 -chat) → 채팅에 문제가 생겨도 주문은 그대로.
/// </summary>
public sealed class MqttChatClient : IChatClient
{
    private readonly AppSettings _settings;
    private readonly IMqttClient _client;
    private readonly CancellationTokenSource _cts = new();

    public event Action<ChatMessage>? MessageReceived;

    public MqttChatClient(AppSettings settings)
    {
        _settings = settings;
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            var seg = e.ApplicationMessage.PayloadSegment;
            if (seg.Array == null || seg.Count == 0) return Task.CompletedTask;
            try
            {
                var msg = JsonSerializer.Deserialize<ChatMessage>(Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count), ChatJson.Options);
                if (msg != null) MessageReceived?.Invoke(msg);
            }
            catch (JsonException) { /* 형식이 틀린 메시지는 버린다 */ }
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    public Task StartAsync()
    {
        _ = Task.Run(() => KeepConnectedAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_client.IsConnected)
        {
            try { await _client.DisconnectAsync(); } catch { /* 끄는 중 */ }
        }
    }

    public async Task SendAsync(ChatMessage message)
    {
        if (!_client.IsConnected)
            throw new InvalidOperationException("브로커에 연결되어 있지 않아 메시지를 보내지 못했습니다.");

        var json = JsonSerializer.Serialize(message, ChatJson.Options);
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(ChatJson.Topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
    }

    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    var b = new MqttClientOptionsBuilder()
                        .WithTcpServer(_settings.MqttHost, _settings.MqttPort)
                        .WithClientId(_settings.MqttClientId + "-chat")
                        .WithCleanSession(true);
                    if (!string.IsNullOrEmpty(_settings.MqttUser))
                        b = b.WithCredentials(_settings.MqttUser, _settings.MqttPassword);
                    await _client.ConnectAsync(b.Build(), ct);
                    await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(ChatJson.Topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    /* 3초 뒤 다시 */
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
