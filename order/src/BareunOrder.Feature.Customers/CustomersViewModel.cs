using System.Windows;
using BareunOrder.Core.Comm;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Customers;

/// <summary>고객 한 명 (비회원이라 연락처로 묶는다)</summary>
public sealed record Customer(string Name, string Phone, string MaskedPhone, IReadOnlyList<Order> Orders)
{
    public int OrderCount => Orders.Count;
    public int TotalSpent => Orders.Where(o => o.Status != OrderStatus.Canceled).Sum(o => o.Total);
    public DateTime LastOrderAt => Orders.Max(o => o.ReceivedAt);
}

/// <summary>문의 목록 거르기 한 칸</summary>
public sealed record InquiryFilter(string Label, Func<Inquiry, bool> Match)
{
    public override string ToString() => Label;
}

/// <summary>
/// 담당: 조시현 — F5 고객·문의.
/// - 고객: 비회원 주문을 연락처로 묶은 목록, 주문 이력, 고객의 문의, 고객 메모
/// - 문의: 받기·답변 보내기는 Core(OrderStore.Inquiries, AnswerAsync), 이 탭은 목록·답변 화면과
///   껐다 켜도 남게 파일 저장(InquiryArchive). 탭 배지 = 답변 대기 수
///
/// ── 할 일 (TODO) ──
/// - 문의·고객 메모를 DB에서 읽기 (공통에 MySQL 패키지가 붙은 뒤. 지금은 data/*.json)
/// - 새 문의 알림 소리·오른쪽 아래 알림 (메인 창 = 공통 담당과 상의)
/// </summary>
public sealed partial class CustomersViewModel : ObservableObject, IHasBadge
{
    private readonly OrderStore _store;
    private readonly InquiryArchive _archive;
    private readonly CustomerMemoStore _memos;
    private readonly Navigator _navigator;

    public CustomersViewModel(OrderStore store, InquiryArchive archive, CustomerMemoStore memos, Navigator navigator)
    {
        _store = store;
        _archive = archive;
        _memos = memos;
        _navigator = navigator;
        _store.Changed += Rebuild;
        _archive.Changed += RebuildInquiries;
        CurrentFilter = Filters[0];
        Rebuild();
    }

    // ═════ 왼쪽 목록 전환 ═════

    /// <summary>true = 문의 목록, false = 고객 목록</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomerMode))]
    private bool _isInquiryMode;

    public bool IsCustomerMode
    {
        get => !IsInquiryMode;
        set => IsInquiryMode = !value;
    }

    /// <summary>탭 배지 = 답변 대기 문의 수</summary>
    public int BadgeCount => UnansweredCount;

    // ═════ 고객 ═════

    [ObservableProperty] private IReadOnlyList<Customer> _customers = Array.Empty<Customer>();
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMemoCommand))]
    private Customer? _selected;
    [ObservableProperty] private string _search = "";

    /// <summary>고른 고객이 남긴 문의 (연락처로 찾음)</summary>
    [ObservableProperty] private IReadOnlyList<Inquiry> _selectedInquiries = Array.Empty<Inquiry>();

    [ObservableProperty] private string _memoText = "";

    [ObservableProperty] private string _memoMessage = "";

    public int CustomerCount => Customers.Count;

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnSelectedChanged(Customer? value)
    {
        MemoText = value == null ? "" : _memos.Get(value.Phone);
        MemoMessage = "";
        RefreshSelectedInquiries();
    }

    private void RefreshSelectedInquiries() =>
        SelectedInquiries = Selected == null
            ? Array.Empty<Inquiry>()
            : _store.Inquiries.Where(q => Digits(q.Phone) == Digits(Selected.Phone)).ToList();

    private bool CanSaveMemo() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanSaveMemo))]
    private void SaveMemo()
    {
        if (Selected == null) return;
        var error = _memos.Set(Selected.Phone, MemoText);
        MemoMessage = error == null ? $"저장했습니다 ({DateTime.Now:HH:mm})" : "저장 실패: " + error;
        if (error == null) _store.Write($"고객 메모 ({Selected.Name} {Selected.MaskedPhone})");
    }

    /// <summary>주문 이력의 주문을 주문 탭에서 열기</summary>
    [RelayCommand]
    private void OpenOrder(Order? order)
    {
        if (order == null) return;
        _navigator.Navigate("주문");
        _store.RequestFocus(order);
    }

    /// <summary>고객 화면의 문의를 누르면 문의 목록에서 연다</summary>
    [RelayCommand]
    private void ShowInquiry(Inquiry? q)
    {
        if (q == null) return;
        CurrentFilter = Filters[1];   // 전체
        IsInquiryMode = true;
        SelectedInquiry = q;
    }

    private void Rebuild()
    {
        var keepPhone = Selected?.Phone;
        var q = Search.Trim();
        var qd = Digits(q);

        Customers = _store.Orders
            .GroupBy(o => Digits(o.Phone))
            .Select(g => new Customer(g.First().Name, g.First().Phone, g.First().MaskedPhone,
                                      g.OrderByDescending(o => o.ReceivedAt).ToList()))
            .Where(c => q.Length == 0 || c.Name.Contains(q) || (qd.Length > 0 && Digits(c.Phone).Contains(qd)))
            .OrderByDescending(c => c.LastOrderAt)
            .ToList();

        OnPropertyChanged(nameof(CustomerCount));
        Selected = Customers.FirstOrDefault(c => c.Phone == keepPhone);
    }

    // ═════ 문의 ═════

    public IReadOnlyList<InquiryFilter> Filters { get; } = new[] { new InquiryFilter("답변 대기", q => !q.IsAnswered), new InquiryFilter("전체", _ => true) }
        .Concat(InquiryTypes.All.Select(code => new InquiryFilter(InquiryTypes.ToKorean(code), q => q.Type == code)))
        .ToList();

    [ObservableProperty] private InquiryFilter? _currentFilter;
    [ObservableProperty] private IReadOnlyList<Inquiry> _inquiryList = Array.Empty<Inquiry>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendAnswerCommand), nameof(OpenInquiryOrderCommand))]
    private Inquiry? _selectedInquiry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendAnswerCommand))]
    private string _answerDraft = "";

    [ObservableProperty] private string _answerMessage = "";
    [ObservableProperty] private bool _isSending;

    public string SendButtonText => SelectedInquiry?.IsAnswered == true ? "답변 고쳐 보내기" : "답변 보내기";

    /// <summary>고른 문의 유형에 맞는 자주 쓰는 답변</summary>
    public IReadOnlyList<string> QuickAnswers =>
        QuickAnswerTable.TryGetValue(SelectedInquiry?.Type ?? "", out var list) ? list.Concat(Common).ToList() : Common.ToList();

    private static readonly string[] Common =
    {
        "문의 주셔서 감사합니다. 확인 후 다시 안내드리겠습니다.",
    };

    private static readonly Dictionary<string, string[]> QuickAnswerTable = new()
    {
        ["delivery"] = new[]
        {
            "출고 검사를 마치고 오늘 중 출고될 예정입니다. 출고되면 주문 조회에서 송장번호를 보실 수 있습니다.",
            "현재 출고 검사 중입니다. 보통 1~2일 안에 출고됩니다.",
        },
        ["cancel"] = new[]
        {
            "요청하신 주문을 취소했습니다. 주문 조회에서 '주문 취소'로 바뀐 것을 확인해 주세요.",
            "이미 출고 검사가 시작되어 바로 취소가 어렵습니다. 받으신 뒤 7일 안에 교환해 드리겠습니다.",
        },
        ["product"] = new[]
        {
            "바른전지 건전지는 외관·극 방향·전압·내부저항 검사를 모두 통과한 A 등급만 출고합니다.",
            "리모컨·시계처럼 조금씩 쓰는 기기는 알카라인 4개입, 자주 바꾸는 기기는 12개입을 추천드립니다.",
        },
        ["bulk"] = new[]
        {
            "대량 구매 문의 감사합니다. 필요한 수량과 희망 날짜를 알려 주시면 라인 일정에 맞춰 견적을 드리겠습니다.",
        },
    };

    public int UnansweredCount => _store.Inquiries.Count(q => !q.IsAnswered);
    public string InquiryConnection => _store.ConnectionText;

    partial void OnCurrentFilterChanged(InquiryFilter? value) => RebuildInquiries();

    partial void OnSelectedInquiryChanged(Inquiry? value)
    {
        AnswerDraft = value?.Answer ?? "";
        AnswerMessage = "";
        OnPropertyChanged(nameof(QuickAnswers));
        OnPropertyChanged(nameof(SendButtonText));
    }

    [RelayCommand]
    private void UseQuickAnswer(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        AnswerDraft = string.IsNullOrWhiteSpace(AnswerDraft) ? text : AnswerDraft.TrimEnd() + "\n" + text;
    }

    private bool CanSendAnswer() => SelectedInquiry != null && AnswerDraft.Trim().Length is > 0 and <= 1000 && !IsSending;

    [RelayCommand(CanExecute = nameof(CanSendAnswer))]
    private async Task SendAnswer()
    {
        var q = SelectedInquiry;
        if (q == null) return;
        IsSending = true;
        SendAnswerCommand.NotifyCanExecuteChanged();
        try
        {
            var again = q.IsAnswered;
            await _store.AnswerAsync(q, AnswerDraft);   // Core: inquiry/answer/<no> (retained). 끊겨 있으면 모아 뒀다가 붙으면 보냄
            AnswerMessage = _store.IsConnected
                ? $"답변을 {(again ? "고쳐 " : "")}보냈습니다 ({DateTime.Now:HH:mm}). 홈페이지 '내 문의 확인'에 바로 보입니다."
                : "브로커 연결이 끊겨 있어 다시 붙으면 보냅니다.";
        }
        catch (Exception ex)
        {
            AnswerMessage = ex.Message;
            MessageBox.Show(ex.Message, "답변 보내기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsSending = false;
            SendAnswerCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SendButtonText));
        }
    }

    private bool CanOpenInquiryOrder() => SelectedInquiry?.OrderNo != null;

    /// <summary>문의에 적힌 주문번호를 주문 탭에서 열기</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInquiryOrder))]
    private void OpenInquiryOrder()
    {
        var no = SelectedInquiry?.OrderNo;
        var o = _store.Orders.FirstOrDefault(x => x.No == no);
        if (o == null)
        {
            AnswerMessage = $"{no} 주문을 이 프로그램에서 찾지 못했습니다 (번호를 잘못 적었거나, 프로그램을 켜기 전 주문).";
            return;
        }
        OpenOrder(o);
    }

    private void RebuildInquiries()
    {
        var keep = SelectedInquiry;
        var match = CurrentFilter?.Match ?? (_ => true);
        InquiryList = _store.Inquiries
            .Where(q => match(q) || q == keep)      // 방금 답변해서 거르기에서 빠지는 문의도 고른 동안은 남겨 둠
            .OrderBy(q => q.IsAnswered)                 // 답변 대기가 위로
            .ThenByDescending(q => q.AskedAt)
            .ToList();
        SelectedInquiry = keep != null && InquiryList.Contains(keep) ? keep : null;
        if (keep != null && SelectedInquiry == keep) OnPropertyChanged(nameof(SendButtonText));

        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(UnansweredCount));
        RefreshSelectedInquiries();   // 고객 화면의 문의 목록도 다시
    }

    private static string Digits(string? s) => OrderJson.Digits(s);
}
