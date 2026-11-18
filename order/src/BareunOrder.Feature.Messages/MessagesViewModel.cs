using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Messages;

/// <summary>
/// 담당: 조시현 — F7 라인 메시지.
/// 주문 관리자와 현장(라인) 관리자가 메시지를 주고받는다. 관리자 프로그램 채팅과 같은 방(chat/messages).
/// - 주문을 붙여 보내면 받는 쪽도 주문번호를 보고, 이쪽에서는 메시지의 [주문 열기]로 주문 탭이 열린다.
/// - @이름으로 사람 부르기 (관리자 프로그램과 같은 규칙). 나를 부른 메시지는 노란색 강조 + 소리.
/// - 탭을 안 보는 동안 나를 부른 메시지가 있으면 탭을 열 때 위에 안내.
/// - 대화 저장·불러오기(지난 7일)와 오른쪽 아래 알림은 Core(ChatStore)·메인 창이 한다.
/// </summary>
public sealed partial class MessagesViewModel : ObservableObject, IHasBadge
{
    private readonly ChatStore _chat;
    private readonly OrderStore _orders;
    private readonly Navigator _navigator;

    public MessagesViewModel(ChatStore chat, OrderStore orders, Navigator navigator)
    {
        _chat = chat;
        _orders = orders;
        _navigator = navigator;

        View = CollectionViewSource.GetDefaultView(_chat.Lines);
        View.Filter = o => o is ChatLine l && (!OnlyMentionsMe || l.MentionsMe);

        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatStore.Unread)) OnPropertyChanged(nameof(BadgeCount));
        };
        _orders.Changed += RefreshOpenOrders;

        // 탭을 열 때: 자리를 비운 사이 나를 부른 메시지가 있었으면 안내
        _navigator.CurrentChanged += title =>
        {
            if (title != ChatStore.TabTitle || _chat.UnseenMentions == 0) return;
            MentionNotice = $"자리를 비운 사이 나를 부른 메시지가 {_chat.UnseenMentions}건 있습니다.";
            _chat.UnseenMentions = 0;
        };
    }

    public ObservableCollection<ChatLine> Lines => _chat.Lines;

    /// <summary>화면 목록 (나를 부른 것만 보기 필터)</summary>
    public ICollectionView View { get; }

    /// <summary>@로 부를 수 있는 사람</summary>
    public ObservableCollection<ChatPerson> People => _chat.People;

    /// <summary>탭 배지 = 안 읽은 메시지 수</summary>
    public int BadgeCount => _chat.Unread;

    /// <summary>자주 쓰는 문구 (누르면 입력칸에 들어감)</summary>
    public IReadOnlyList<string> QuickTexts { get; } = new[]
    {
        "오늘 A 등급 몇 개 나왔나요?",
        "출고분 먼저 검사 부탁드립니다.",
        "다음 회차 시작 시간 알려 주세요.",
        "출고 완료했습니다.",
    };

    /// <summary>붙일 수 있는 주문 (주문 접수·출고 검사)</summary>
    [ObservableProperty] private IReadOnlyList<Order> _openOrders = Array.Empty<Order>();

    [ObservableProperty] private Order? _attachOrder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    /// <summary>"부를 사람" 고르기 (고르면 입력칸에 @이름이 들어가고 다시 비워짐)</summary>
    [ObservableProperty] private ChatPerson? _selectedPerson;

    [ObservableProperty] private bool _onlyMentionsMe;

    /// <summary>위 안내: "자리를 비운 사이 나를 부른 메시지 N건"</summary>
    [ObservableProperty] private string? _mentionNotice;   // null이면 안 보임

    /// <summary>보낸 뒤 한 줄 안내: "현장 관리자 님을 불렀습니다."</summary>
    [ObservableProperty] private string? _status;          // null이면 안 보임

    /// <summary>"부를 사람"을 고르면 입력칸으로 커서를 보내도록 화면에 알림</summary>
    public event Action? FocusInputRequested;

    partial void OnOnlyMentionsMeChanged(bool value) => View.Refresh();

    partial void OnSelectedPersonChanged(ChatPerson? value)
    {
        if (value == null) return;
        var tag = $"@{value.Name} ";
        if (!Input.Contains(tag.Trim()))
            Input = (Input.Length == 0 || Input.EndsWith(' ') ? Input : Input + " ") + tag;
        SelectedPerson = null;
        FocusInputRequested?.Invoke();
    }

    private bool CanSend() => Input.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Input;
        var no = AttachOrder?.No;
        var called = People.Where(p => text.Contains("@" + p.Name)).Select(p => p.Name).ToList();
        if (await _chat.SendAsync(text, no))
        {
            Input = "";
            AttachOrder = null;
            Status = called.Count == 0 ? null : $"{string.Join(", ", called)} 님을 불렀습니다.";
        }
    }

    [RelayCommand]
    private void UseQuick(string text) => Input = text;

    [RelayCommand]
    private void CloseNotice() => MentionNotice = null;

    [RelayCommand]
    private void ShowMentions()
    {
        OnlyMentionsMe = true;
        MentionNotice = null;
    }

    /// <summary>메시지에 붙은 주문을 주문 탭에서 열기</summary>
    [RelayCommand]
    private void OpenOrder(string? orderNo)
    {
        var o = _orders.Orders.FirstOrDefault(x => x.No == orderNo);
        if (o == null) return;
        _navigator.Navigate("주문");
        _orders.RequestFocus(o);
    }

    private void RefreshOpenOrders() =>
        OpenOrders = _orders.Orders.Where(o => o.Status is OrderStatus.Received or OrderStatus.Inspect).ToList();
}
