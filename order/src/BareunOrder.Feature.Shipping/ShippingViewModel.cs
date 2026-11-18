using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Shipping;

/// <summary>
/// 담당: (미정) — F2 출고·배송.
/// 출고 검사 상태인 주문을 골라 택배사·송장번호를 넣고 출고 처리(→ 배송 중)한다.
///
/// ── 할 일 (TODO) ──
/// - 라인 A 등급 준비 수: 지금은 예시 숫자. 라인 MQTT server/results에서 오늘 A 등급 개수를 세서 넣기
///   (서버 담당과 방법 확인. 관리자 프로그램 Results 탭이 같은 데이터를 씀)
/// - 여러 주문 한꺼번에 출고 처리 (체크박스)
/// - 출고 지시서·거래명세서 인쇄 (PrintDialog 또는 PDF)
/// - 송장번호 형식 검사 (택배사마다 자릿수)
/// - [라인에 요청]: 지금은 메시지만 보낸다. 라인 쪽에서 우선 처리하는 기능은 서버 담당과 상의
/// </summary>
public sealed partial class ShippingViewModel : ObservableObject, IHasBadge
{
    private readonly OrderStore _store;
    private readonly ChatStore _chat;

    public ShippingViewModel(OrderStore store, ChatStore chat)
    {
        _store = store;
        _chat = chat;

        Pending = new ListCollectionView(store.Orders) { Filter = o => ((Order)o).Status == OrderStatus.Inspect };
        Pending.SortDescriptions.Add(new SortDescription(nameof(Order.ReceivedAt), ListSortDirection.Ascending));   // 오래된 것부터

        OnTheWay = new ListCollectionView(store.Orders) { Filter = o => ((Order)o).Status == OrderStatus.Shipping };

        _store.Changed += Refresh;
    }

    /// <summary>출고 대기 (출고 검사 상태) — 오래된 주문부터</summary>
    public ListCollectionView Pending { get; }

    /// <summary>배송 중</summary>
    public ListCollectionView OnTheWay { get; }

    public IReadOnlyList<string> Couriers { get; } = new[] { "CJ대한통운", "한진택배", "롯데택배", "우체국택배", "로젠택배" };

    public int BadgeCount => _store.CountOf(OrderStatus.Inspect);

    // ── 위쪽 숫자 ──
    public int PendingCount => Pending.Count;
    public int NeededBatteries => _store.Orders.Where(o => o.Status == OrderStatus.Inspect).Sum(o => o.BatteryCount);

    /// <summary>TODO: 라인에서 오늘 검사 통과한 A 등급 개수 (지금은 예시 값)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShort))]
    private int _lineReadyA = 60;

    public bool IsShort => LineReadyA < NeededBatteries;

    // ── 오른쪽 입력 ──
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShipCommand))]
    private Order? _selected;

    [ObservableProperty] private string _courier = "CJ대한통운";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShipCommand))]
    private string _tracking = "";

    partial void OnSelectedChanged(Order? value) => Tracking = "";

    private bool CanShip() => Selected != null && Tracking.Trim().Length >= 10;

    /// <summary>출고 처리: 송장 저장 → 배송 중으로</summary>
    [RelayCommand(CanExecute = nameof(CanShip))]
    private async Task Ship()
    {
        if (Selected is not { } o) return;
        await _store.SetShippingInfoAsync(o, Courier, Tracking.Trim());
        await _store.ChangeStatusAsync(o, OrderStatus.Shipping);
        Selected = null;
    }

    /// <summary>
    /// 라인(현장 관리자)에 A 등급을 요청하는 메시지를 보낸다 (F7 라인 메시지 탭에 보임).
    /// 주문을 골랐으면 그 주문을 붙이고, 아니면 출고 대기 전체 기준으로 보낸다.
    /// </summary>
    [RelayCommand]
    private async Task RequestLine()
    {
        string text;
        if (Selected is { } o)
            text = $"주문 {o.No} 출고용 A 등급 건전지 {o.BatteryCount}개 필요합니다. (지금 준비 {LineReadyA}개)";
        else
            text = $"출고 대기 {PendingCount}건, A 등급 건전지 {NeededBatteries}개 필요합니다. (지금 준비 {LineReadyA}개)";

        if (await _chat.SendAsync(text, Selected?.No))
            _store.Write("라인에 A 등급 요청 메시지 보냄");
    }

    [RelayCommand]
    private void Print() =>
        MessageBox.Show("출고 지시서·거래명세서 인쇄는 준비 중입니다.", "인쇄", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Refresh()
    {
        Pending.Refresh();
        OnTheWay.Refresh();
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(NeededBatteries));
        OnPropertyChanged(nameof(IsShort));
    }
}
