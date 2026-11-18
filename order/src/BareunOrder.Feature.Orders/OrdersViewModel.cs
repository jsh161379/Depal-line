using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Orders;

/// <summary>위쪽 상태 거르기 탭 하나 (전체 / 주문 접수 / …)</summary>
public sealed partial class StatusFilter : ObservableObject
{
    public StatusFilter(string label, OrderStatus? status)
    {
        Label = label;
        Status = status;
    }

    public string Label { get; }
    public OrderStatus? Status { get; }

    [ObservableProperty] private int _count;
}

/// <summary>
/// 담당: (미정) — F1 주문.
/// 목록(상태별 거르기·검색) + 오른쪽 상세 + 다음 단계·취소.
/// 다른 탭을 만들 때 이 파일을 참고하면 된다 (OrderStore 쓰는 법, 배지, 다른 탭에서 주문 열기).
/// </summary>
public sealed partial class OrdersViewModel : ObservableObject, IHasBadge
{
    private readonly OrderStore _store;

    public OrdersViewModel(OrderStore store)
    {
        _store = store;

        Filters = new[]
        {
            new StatusFilter("전체", null),
            new StatusFilter("주문 접수", OrderStatus.Received),
            new StatusFilter("출고 검사", OrderStatus.Inspect),
            new StatusFilter("배송 중", OrderStatus.Shipping),
            new StatusFilter("배송 완료", OrderStatus.Delivered),
            new StatusFilter("취소", OrderStatus.Canceled),
        };
        _currentFilter = Filters[0];

        // 기본 보기(GetDefaultView)는 다른 탭과 같이 쓰게 되므로, 이 탭 전용 보기를 만든다
        View = new ListCollectionView(store.Orders) { Filter = o => Match((Order)o) };

        _store.Changed += OnStoreChanged;
        _store.FocusRequested += o =>
        {
            CurrentFilter = Filters[0];
            Search = "";
            Selected = o;
        };
    }

    public IReadOnlyList<StatusFilter> Filters { get; }
    public ICollectionView View { get; }

    /// <summary>탭 배지 = 아직 확인 안 한 새 주문 (주문 접수 상태) 수</summary>
    public int BadgeCount => _store.CountOf(OrderStatus.Received);

    [ObservableProperty] private StatusFilter _currentFilter;
    [ObservableProperty] private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhoneText))]
    [NotifyCanExecuteChangedFor(nameof(AdvanceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private Order? _selected;

    /// <summary>연락처는 기본으로 가려서 보이고, [번호 보기]를 누르면 전체가 보인다</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhoneText))]
    private bool _showPhone;

    public string PhoneText => Selected == null ? "" : ShowPhone ? Selected.Phone : Selected.MaskedPhone;

    partial void OnCurrentFilterChanged(StatusFilter value) => RefreshView();
    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnSelectedChanged(Order? value) => ShowPhone = false;

    // ── 버튼 ──

    private bool CanAdvance() => Selected?.CanAdvance == true;

    /// <summary>다음 단계로 (주문 접수 → 출고 검사 → 배송 중 → 배송 완료). 목록에서 Enter로도 된다.</summary>
    [RelayCommand(CanExecute = nameof(CanAdvance))]
    private async Task Advance()
    {
        if (Selected is not { } o || OrderStatusText.Next(o.Status) is not { } next) return;

        // 배송 중으로 넘길 때는 송장이 있어야 한다 → 출고·배송 탭에서 처리
        if (next == OrderStatus.Shipping && string.IsNullOrEmpty(o.Tracking))
        {
            MessageBox.Show("송장번호가 없습니다.\n출고·배송 탭(F2)에서 택배사와 송장번호를 넣고 출고 처리해 주세요.",
                "출고 처리", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _store.ChangeStatusAsync(o, next);
    }

    private bool CanCancel() => Selected?.CanCancel == true;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task Cancel()
    {
        if (Selected is not { } o) return;
        var ok = MessageBox.Show($"{o.No} ({o.Name}) 주문을 취소할까요?\n홈페이지 주문 조회에도 '취소'로 보입니다.",
            "주문 취소", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok == MessageBoxResult.Yes) await _store.ChangeStatusAsync(o, OrderStatus.Canceled);
    }

    // ── 내부 ──

    private bool Match(Order o)
    {
        if (CurrentFilter.Status is { } s && o.Status != s) return false;
        var q = Search.Trim();
        if (q.Length == 0) return true;
        var digits = q.Replace("-", "");
        return o.No.Contains(q, StringComparison.OrdinalIgnoreCase)
            || o.Name.Contains(q)
            || (digits.Length >= 4 && o.Phone.Replace("-", "").Contains(digits));
    }

    private void OnStoreChanged()
    {
        foreach (var f in Filters)
            f.Count = f.Status is { } s ? _store.CountOf(s) : _store.Orders.Count;
        OnPropertyChanged(nameof(BadgeCount));
        RefreshView();
        AdvanceCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>목록을 다시 거르되, 고른 주문은 그대로 둔다</summary>
    private void RefreshView()
    {
        var keep = Selected;
        View.Refresh();
        if (keep != null && Match(keep)) Selected = keep;
    }
}
