using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using BareunOrder.Core.Comm;
using BareunOrder.Core.Pages;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BareunOrder.Core.Data;

/// <summary>화면에 보일 메시지 한 줄</summary>
public sealed record ChatLine(string Who, string Station, string Text, DateTime Time, bool Mine, string? OrderNo)
{
    /// <summary>다른 사람이 @로 나(이 주문 관리 프로그램)를 부른 메시지</summary>
    public bool MentionsMe { get; init; }

    /// <summary>"→ 조시현, 장성찬" (부른 사람이 없으면 빈 글자)</summary>
    public string MentionText { get; init; } = "";
}

/// <summary>@로 부를 수 있는 사람 (채팅에서 말한 적 있는 사람)</summary>
public sealed record ChatPerson(string Id, string Name, string Station)
{
    public string Text => $"{Name} ({Station})";
}

/// <summary>
/// 라인(현장 관리자)과 주고받는 메시지 보관함.
/// 관리자 프로그램 채팅과 같은 방(chat/messages)을 쓴다.
/// - 다른 탭에서 라인에 요청할 때: chatStore.SendAsync("…", order.No) — 주문번호를 붙이면 메시지에서 주문을 바로 열 수 있다.
/// - 안 읽은 수(Unread)는 라인 메시지 탭을 열면 0이 된다.
/// - @이름: 관리자 프로그램 채팅과 같은 규칙 (통신 규약 v1.3 5-1). 보낼 때 글 속 @이름을 찾아 mentions에 넣고,
///   받은 메시지의 mentions에 내 아이디(MqttClientId)나 이름(Operator)이 있으면 "나를 부름" → 소리 + 강조.
/// - 저장: 관리자 프로그램 채팅과 같은 방식으로 실행 폴더 data/chat/날짜.jsonl (한 줄 = 메시지 하나),
///   켤 때 지난 7일을 불러온다 (주문_통신_규약 5장).
/// </summary>
public sealed partial class ChatStore : ObservableObject
{
    public const string TabTitle = "라인 메시지";
    public const string Station = "주문 관리";

    /// <summary>메시지 안의 주문번호 (주문_통신_규약 2-1: 끝 4자 = 영문 대문자·숫자, 0 O 1 I 제외)</summary>
    private static readonly Regex OrderNoPattern = new(@"BR-\d{8}-[A-HJ-NP-Z2-9]{4}", RegexOptions.Compiled);

    /// <summary>@ 뒤의 이름: 띄어쓰기·@·문장부호 앞까지 (관리자 프로그램과 같음)</summary>
    private static readonly Regex MentionToken = new(@"@([^\s@,.!?:;()\[\]]+)", RegexOptions.Compiled);

    private const int KeepDays = 7;
    private static readonly string ChatDir = Path.Combine(AppContext.BaseDirectory, "data", "chat");

    private readonly IChatClient _client;
    private readonly AppSettings _settings;
    private readonly Navigator _navigator;
    private readonly HashSet<string> _seen = new();

    public ChatStore(IChatClient client, AppSettings settings, Navigator navigator)
    {
        _client = client;
        _settings = settings;
        _navigator = navigator;

        _client.MessageReceived += m => OnUi(() => Add(m));
        _navigator.CurrentChanged += title =>
        {
            if (title == TabTitle) Unread = 0;
        };
    }

    public ObservableCollection<ChatLine> Lines { get; } = new();

    /// <summary>부를 수 있는 사람: 지금까지 채팅에서 말한 사람 (나 빼고)</summary>
    public ObservableCollection<ChatPerson> People { get; } = new();

    /// <summary>안 읽은 메시지 수 (탭 배지)</summary>
    [ObservableProperty] private int _unread;

    /// <summary>라인 메시지 탭을 안 보는 동안 나를 부른 메시지 수 (탭을 열면 화면이 안내하고 0으로)</summary>
    [ObservableProperty] private int _unseenMentions;

    /// <summary>라인에서 새 메시지가 옴 (내가 보낸 것은 빼고)</summary>
    public event Action<ChatLine>? NewMessage;

    public Task StartAsync()
    {
        LoadRecent();
        return _client.StartAsync();
    }
    public Task StopAsync() => _client.StopAsync();

    /// <summary>
    /// 메시지 보내기. 주문번호를 주면 메시지에 주문이 붙는다. 글 속 @이름은 자동으로 mentions에 들어간다.
    /// 실패하면 false.
    /// </summary>
    public async Task<bool> SendAsync(string text, string? orderNo = null)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        try
        {
            await _client.SendAsync(new ChatMessage
            {
                FromId = _settings.MqttClientId,
                FromName = _settings.Operator,
                Station = Station,
                Text = text,
                RefKind = orderNo == null ? null : "order",
                RefText = orderNo,
                Mentions = FindMentions(text),
            });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "메시지", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Add(ChatMessage m, bool fromFile = false)
    {
        if (!_seen.Add(m.MsgId)) return;   // 같은 메시지가 두 번 오면 무시
        if (!fromFile) Save(m);

        var mine = m.Station == Station && m.FromId == _settings.MqttClientId;
        var orderNo = m.RefKind == "order" ? m.RefText : OrderNoPattern.Match(m.Text) is { Success: true } x ? x.Value : null;
        var mentions = m.Mentions ?? new();                 // 예전 기록은 mentions가 없을 수 있음
        var line = new ChatLine(m.FromName, m.Station, m.Text, ChatJson.ParseTs(m.Ts), mine, orderNo)
        {
            MentionsMe = !mine && mentions.Any(IsMe),
            MentionText = mentions.Count == 0 ? "" : "→ " + string.Join(", ", mentions.Select(x => x.Name)),
        };
        Lines.Add(line);
        if (!mine) RememberPerson(m);

        if (!mine && !fromFile)
        {
            var away = _navigator.CurrentTitle != TabTitle;
            if (away) Unread++;
            if (line.MentionsMe)
            {
                if (away) UnseenMentions++;
                if (_settings.PlaySound) System.Media.SystemSounds.Asterisk.Play();
            }
            NewMessage?.Invoke(line);
        }
    }

    /// <summary>
    /// 이 @가 나인가. 아이디가 있으면 아이디(주문 관리 = MqttClientId)나 이름으로, 없으면 이름(Operator)으로 본다
    /// (관리자 프로그램 ChatMention.Is와 같은 규칙).
    /// </summary>
    private bool IsMe(ChatMention x) =>
        x.Id != ""
            ? string.Equals(x.Id, _settings.MqttClientId, StringComparison.OrdinalIgnoreCase) || x.Name == _settings.Operator
            : x.Name == _settings.Operator;

    private void RememberPerson(ChatMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.FromName)) return;
        if (People.Any(p => p.Name == m.FromName)) return;
        People.Add(new ChatPerson(m.FromId, m.FromName, m.Station));
    }

    /// <summary>
    /// 글 속의 @이름 찾기 (관리자 프로그램과 같은 방식).
    ///   1) 아는 사람 이름이 "@이름" 그대로 있으면 → 아이디까지 채움 (이름에 띄어쓰기가 있어도 됨)
    ///   2) 그 밖의 "@글자" → 이름만 채움 (받는 쪽이 이름으로 비교). "@장성찬님" → 장성찬
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
            if (token.Length > 1 && token.EndsWith('님')) token = token[..^1];
            if (found.Any(f => token.StartsWith(f.Name) || f.Name.StartsWith(token))) continue;
            var known = People.FirstOrDefault(p => token.StartsWith(p.Name));
            found.Add(known != null ? new ChatMention { Id = known.Id, Name = known.Name } : new ChatMention { Name = token });
        }
        return found;
    }

    /// <summary>data/chat/날짜.jsonl 에 한 줄 추가 (실패해도 채팅은 계속)</summary>
    private static void Save(ChatMessage m)
    {
        try
        {
            Directory.CreateDirectory(ChatDir);
            var day = ChatJson.ParseTs(m.Ts).ToString("yyyy-MM-dd");
            File.AppendAllText(Path.Combine(ChatDir, day + ".jsonl"),
                JsonSerializer.Serialize(m, ChatJson.Options) + "\n", new UTF8Encoding(false));
        }
        catch (Exception) { /* 저장 실패는 무시 */ }
    }

    /// <summary>지난 7일 메시지 불러오기 (오래된 것부터)</summary>
    private void LoadRecent()
    {
        if (!Directory.Exists(ChatDir)) return;
        for (var d = KeepDays - 1; d >= 0; d--)
        {
            var path = Path.Combine(ChatDir, DateTime.Today.AddDays(-d).ToString("yyyy-MM-dd") + ".jsonl");
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ChatMessage>(line, ChatJson.Options) is { } m) Add(m, fromFile: true);
                }
                catch (JsonException) { /* 깨진 줄은 건너뜀 */ }
            }
        }
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
