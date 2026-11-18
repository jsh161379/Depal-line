using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Text.RegularExpressions;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepalAdmin.Core;
using DepalAdmin.Core.Alarms;
using DepalAdmin.Core.Auth;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;
using DepalAdmin.Core.Pages;
using DepalAdmin.Core.Settings;

namespace DepalAdmin.Feature.Chat;

/// <summary>
/// 담당: 조시현 — 채팅 (단톡방 하나 + @받는 사람 지정).
///   - 같은 브로커에 붙은 관리자 프로그램끼리 메시지 (통신 규약 v1.3 5-1)
///   - 알람·건전지 결과를 골라 붙여 보내기
///   - @이름으로 사람을 부르면 그 사람 화면에서 강조 + 소리 + "나를 부른 메시지" 안내
///   - 다른 탭을 보고 있을 때 온 메시지는 탭 배지로 안 읽은 수 표시
///   - 지난 7일 대화 불러오기
/// 1:1 대화방은 계정 저장 위치(DB)가 정해진 뒤 검토 (2026-11-11 결정: 단톡방 + @ 지정으로 진행)
/// </summary>
public partial class ChatViewModel : ObservableObject, IHasBadge
{
    private const int RecentMax = 5;
    private const int HistoryDays = 7;
    private const string Title = "채팅";

    // @ 뒤의 이름: 띄어쓰기·@·문장부호 앞까지
    private static readonly Regex MentionToken = new(@"@([^\s@,.!?:;()\[\]]+)", RegexOptions.Compiled);

    private readonly IChatClient _chat;
    private readonly Session _session;
    private readonly Navigator _navigator;
    private readonly ChatLogFile _log;
    private readonly AppSettings _settings;
    private readonly HashSet<string> _seen = new();   // QoS 1 중복 수신 막기
    private readonly List<BatteryResult> _recentResults = new();

    public ObservableCollection<ChatRow> Messages { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<Attachment> Attachments { get; } = new();

    /// <summary>부를 수 있는 사람: 지금까지 채팅에서 말한 사람 (나 빼고)</summary>
    public ObservableCollection<ChatPerson> People { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = "";

    [ObservableProperty] private Attachment? _selectedAttachment;
    [ObservableProperty] private ChatPerson? _selectedPerson;
    [ObservableProperty] private int _badgeCount;
    [ObservableProperty] private string _status = "";

    /// <summary>탭을 안 보고 있는 동안 나를 부른 메시지 수</summary>
    private int _unseenMentions;

    /// <summary>채팅 탭 위 안내: "자리를 비운 사이 나를 부른 메시지 N건"</summary>
    [ObservableProperty] private string _mentionNotice = "";

    [ObservableProperty] private bool _onlyMentionsMe;

    /// <summary>새 메시지가 오면 화면이 맨 아래로 스크롤하도록 알림</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>"부를 사람"을 고르면 입력칸 끝으로 커서를 보내도록 알림</summary>
    public event EventHandler? FocusDraftRequested;

    public ChatViewModel(IChatClient chat, Session session, Navigator navigator, ChatLogFile log,
                         IAlarmService alarms, ILineClient line, AppSettings settings)
    {
        _chat = chat;
        _session = session;
        _navigator = navigator;
        _log = log;
        _settings = settings;

        View = CollectionViewSource.GetDefaultView(Messages);
        View.Filter = o => o is ChatRow r && (!OnlyMentionsMe || r.MentionsMe);

        _chat.MessageReceived += (_, m) => Receive(m, live: true);

        // 탭을 열면 읽은 것으로. 그동안 나를 부른 게 있었으면 위에 안내
        _navigator.CurrentChanged += (_, title) =>
        {
            if (title != Title) return;
            BadgeCount = 0;
            if (_unseenMentions > 0) MentionNotice = $"자리를 비운 사이 나를 부른 메시지가 {_unseenMentions}건 있습니다.";
            _unseenMentions = 0;
        };

        // 로그인한 사람이 바뀌면 "나를 부름" 판단도 다시
        _session.Changed += (_, _) => Refresh();

        // 붙여 보낼 후보: 확인 안 된 알람, 최근 건전지 결과
        alarms.Changed += (_, _) => RebuildAttachments(alarms);
        line.ResultReceived += (_, r) =>
        {
            _recentResults.Insert(0, r);
            if (_recentResults.Count > RecentMax) _recentResults.RemoveAt(RecentMax);
            RebuildAttachments(alarms);
        };
        RebuildAttachments(alarms);
    }

    partial void OnOnlyMentionsMeChanged(bool value) => View.Refresh();

    /// <summary>"부를 사람"을 고르면 입력칸에 @이름을 넣고 목록은 다시 비워 둔다</summary>
    partial void OnSelectedPersonChanged(ChatPerson? value)
    {
        if (value == null) return;
        var tag = $"@{value.Name} ";
        if (!Draft.Contains(tag.Trim())) Draft = (Draft.Length == 0 || Draft.EndsWith(' ') ? Draft : Draft + " ") + tag;
        SelectedPerson = null;
        FocusDraftRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── 받기 ─────────────────────────────────────────────

    private void Receive(ChatMessage m, bool live)
    {
        if (!_seen.Add(m.MsgId)) return;
        if (live) _log.Append(m);

        var row = MakeRow(m);
        Messages.Add(row);
        RememberPerson(m);

        if (live && _navigator.CurrentTitle != Title) BadgeCount++;
        if (live && row.MentionsMe)
        {
            SystemSounds.Asterisk.Play();
            if (_navigator.CurrentTitle != Title) _unseenMentions++;
        }
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private ChatRow MakeRow(ChatMessage m)
    {
        var me = _session.User;
        var isMine = me != null && m.FromId == me.Id && m.Station == _settings.StationName;
        var mentionsMe = me != null && !isMine && (m.Mentions ?? new()).Any(x => x.Is(me.Id, me.Name));   // 예전 기록은 mentions가 없을 수 있음
        return new ChatRow(m, isMine, mentionsMe);
    }

    private void Refresh()
    {
        var raws = Messages.Select(r => r.Raw).ToList();
        Messages.Clear();
        foreach (var m in raws) Messages.Add(MakeRow(m));
        People.Clear();
        foreach (var m in raws) RememberPerson(m);
    }

    private void RememberPerson(ChatMessage m)
    {
        var me = _session.User;
        if (string.IsNullOrWhiteSpace(m.FromName)) return;
        if (me != null && m.FromName == me.Name && m.FromId == me.Id) return;   // 나는 빼고
        if (People.Any(p => p.Name == m.FromName)) return;
        People.Add(new ChatPerson(m.FromId, m.FromName, m.Station));
    }

    // ── 보내기 ───────────────────────────────────────────

    private void RebuildAttachments(IAlarmService alarms)
    {
        var keep = SelectedAttachment?.Text;
        Attachments.Clear();
        Attachments.Add(Attachment.None);
        foreach (var a in alarms.Alarms.Where(a => !a.Acked).Take(RecentMax))
            Attachments.Add(new Attachment("alarm", $"[{Labels.Level(a.Level)}] {Labels.Device(a.Device)} {a.Message}"));
        foreach (var r in _recentResults)
            Attachments.Add(new Attachment("battery", $"건전지 #{r.BatteryId} ({Labels.Grade(r.Grade)}) 회차 {r.RunId}"));
        SelectedAttachment = Attachments.FirstOrDefault(x => x.Text == keep) ?? Attachment.None;
    }

    /// <summary>
    /// 글 속의 @이름을 찾는다.
    ///   1) 아는 사람(People)의 이름이 "@이름" 그대로 있으면 → 아이디까지 채움 (이름에 띄어쓰기가 있어도 됨)
    ///   2) 그 밖의 "@글자" → 이름만 채움 (받는 쪽이 이름으로 비교)
    /// "@조시현님"처럼 뒤에 글자가 붙으면 아는 이름으로 시작하는지 보고 그 이름으로 본다.
    /// </summary>
    private List<ChatMention> FindMentions(string text)
    {
        var found = new List<ChatMention>();
        foreach (var p in People.OrderByDescending(p => p.Name.Length))
            if (text.Contains("@" + p.Name) && found.All(f => f.Name != p.Name))
                found.Add(new ChatMention { Id = p.Id, Name = p.Name });

        foreach (Match m in MentionToken.Matches(text))
        {
            var token = m.Groups[1].Value;
            if (token.Length > 1 && token.EndsWith('님')) token = token[..^1];   // "@장성찬님" → 장성찬
            if (found.Any(f => token.StartsWith(f.Name) || f.Name.StartsWith(token))) continue;
            var known = People.FirstOrDefault(p => token.StartsWith(p.Name));
            found.Add(known != null ? new ChatMention { Id = known.Id, Name = known.Name } : new ChatMention { Name = token });
        }
        return found;
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var user = _session.User;
        if (user == null) return;

        var text = Draft.Trim();
        var att = SelectedAttachment ?? Attachment.None;
        var msg = new ChatMessage
        {
            FromId = user.Id,
            FromName = user.Name,
            Station = _settings.StationName,
            Text = text,
            RefKind = att.Kind,
            RefText = att.Kind == null ? null : att.Text,
            Mentions = FindMentions(text),
        };

        try
        {
            await _chat.SendAsync(msg);
            Draft = "";
            SelectedAttachment = Attachment.None;
            Status = msg.Mentions.Count == 0 ? "" : $"{string.Join(", ", msg.Mentions.Select(x => x.Name))} 님을 불렀습니다.";
        }
        catch (Exception ex)
        {
            Status = $"보내지 못했습니다: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CloseNotice() => MentionNotice = "";

    [RelayCommand]
    private void ShowMentions()
    {
        OnlyMentionsMe = true;
        MentionNotice = "";
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        Status = "불러오는 중…";
        var old = await _log.ReadAsync(DateTime.Today.AddDays(-(HistoryDays - 1)), DateTime.Today.AddDays(1));
        var merged = old.Concat(Messages.Select(r => r.Raw))
                        .GroupBy(m => m.MsgId).Select(g => g.First())
                        .OrderBy(m => m.Ts, StringComparer.Ordinal)
                        .ToList();
        Messages.Clear();
        _seen.Clear();
        foreach (var m in merged) Receive(m, live: false);
        Status = $"최근 {HistoryDays}일 대화 {merged.Count}건";
    }
}

public sealed class ChatRow
{
    public ChatRow(ChatMessage raw, bool isMine, bool mentionsMe)
    {
        Raw = raw;
        IsMine = isMine;
        MentionsMe = mentionsMe;
    }

    public ChatMessage Raw { get; }
    public bool IsMine { get; }

    /// <summary>다른 사람이 나를 @로 부른 메시지</summary>
    public bool MentionsMe { get; }

    public string Who => IsMine ? "나" : $"{Raw.FromName} ({Raw.Station})";
    public string Time => Json.ParseTs(Raw.Ts)?.ToString("MM-dd HH:mm") ?? Raw.Ts;
    public string Text => Raw.Text;
    public string? RefText => Raw.RefText;
    public string RefKind => Raw.RefKind ?? "";

    /// <summary>
    /// 붙여 보낸 것을 화면에 보일 글자. 주문 관리 프로그램이 주문을 붙이면 ref_kind "order", ref_text 주문번호
    /// (주문 통신 규약 5장) → "주문 BR-…". 알람·건전지는 원래 글자 그대로.
    /// </summary>
    public string? RefDisplay => RefKind == "order" && !string.IsNullOrEmpty(RefText) ? $"주문 {RefText}" : RefText;

    /// <summary>"→ 조시현, 장성찬" (부른 사람이 없으면 빈 글자)</summary>
    public string MentionText => Raw.Mentions is not { Count: > 0 } ? "" : "→ " + string.Join(", ", Raw.Mentions.Select(m => m.Name));
}

/// <summary>부를 수 있는 사람 한 명 (채팅에서 말한 적 있는 사람)</summary>
public sealed record ChatPerson(string Id, string Name, string Station)
{
    public string Text => $"{Name} ({Station})";
}

public sealed record Attachment(string? Kind, string Text)
{
    public static readonly Attachment None = new(null, "붙이지 않음");
}
