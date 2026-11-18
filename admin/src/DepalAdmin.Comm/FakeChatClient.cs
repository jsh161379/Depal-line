using System.Windows;
using System.Windows.Threading;
using DepalAdmin.Core.Messaging;

namespace DepalAdmin.Comm;

/// <summary>
/// 브로커 없이 채팅 화면을 만들 수 있게 하는 가짜.
///   - 내가 보낸 메시지는 바로 되돌려 준다 (실제 MQTT처럼)
///   - 가끔 '현장 작업자(가짜)'가 말을 건다. 절반은 마지막으로 말한 사람을 @로 부른다 (멘션 시험용)
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    // ── 수정할 값 ──────────────────────────────────────────
    private static readonly TimeSpan BotEvery = TimeSpan.FromSeconds(45);
    // ──────────────────────────────────────────────────────

    private static readonly string[] BotLines =
    {
        "재분류함 거의 찼어요. 비우고 올게요.",
        "팔레트 새로 올렸습니다.",
        "검사 부스 조명 한 번 확인 부탁드려요.",
        "AGV 복귀 확인했습니다.",
        "불량함 비웠습니다.",
    };

    private readonly Random _rnd = new();
    private readonly DispatcherTimer _timer = new();
    private ChatMention? _lastSpeaker;   // 가짜 작업자가 부를 사람

    public event EventHandler<ChatMessage>? MessageReceived;

    public FakeChatClient()
    {
        _timer.Interval = BotEvery;
        _timer.Tick += (_, _) =>
        {
            var msg = new ChatMessage
            {
                FromId = "worker",
                FromName = "현장작업자",
                Station = "MAIN-01",
                Text = BotLines[_rnd.Next(BotLines.Length)],
            };
            if (_lastSpeaker != null && _rnd.Next(2) == 0)
            {
                msg.Text = $"@{_lastSpeaker.Name} {msg.Text}";
                msg.Mentions.Add(_lastSpeaker);
            }
            Raise(msg);
        };
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _timer.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _timer.Stop();
        return Task.CompletedTask;
    }

    public Task SendAsync(ChatMessage message, CancellationToken ct = default)
    {
        _lastSpeaker = new ChatMention { Id = message.FromId, Name = message.FromName };
        Raise(message);
        return Task.CompletedTask;
    }

    private void Raise(ChatMessage message)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) MessageReceived?.Invoke(this, message);
        else d.InvokeAsync(() => MessageReceived?.Invoke(this, message));
    }
}
