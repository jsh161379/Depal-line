using System.Windows;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Stats;

/// <summary>기간 하나 (오늘 / 최근 7일 / 이번 달)</summary>
public sealed record Period(string Label, Func<DateTime> From);

/// <summary>막대 한 줄 (상품별, 시간대별). Width는 화면에 그릴 막대 길이(0~1).</summary>
public sealed record Bar(string Label, int Value, string ValueText, double Ratio);

/// <summary>
/// 담당: (미정) — F4 통계.
/// 기간별 주문 수·매출·판매 팩·취소율, 상품별 매출, 시간대별 주문 수.
///
/// ── 할 일 (TODO) ──
/// - 지금은 프로그램이 켜진 뒤 메모리에 있는 주문만 센다 → 주문 DB가 생기면 DB에서 기간 조회 (서버 담당과 테이블 확인)
/// - 날짜 직접 고르기 (DatePicker)
/// - 엑셀(CSV) 내보내기, 마감 보고서 인쇄
/// - 자주 주문하는 고객 TOP 5
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly OrderStore _store;

    public StatsViewModel(OrderStore store)
    {
        _store = store;
        Periods = new[]
        {
            new Period("오늘", () => DateTime.Today),
            new Period("최근 7일", () => DateTime.Today.AddDays(-6)),
            new Period("이번 달", () => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)),
        };
        _period = Periods[0];
        _store.Changed += Recalc;
    }

    public IReadOnlyList<Period> Periods { get; }

    [ObservableProperty] private Period _period;
    partial void OnPeriodChanged(Period value) => Recalc();

    [ObservableProperty] private int _orderCount;
    [ObservableProperty] private int _sales;
    [ObservableProperty] private int _packCount;
    [ObservableProperty] private double _cancelRate;     // 0~100
    [ObservableProperty] private int _averageOrder;

    [ObservableProperty] private IReadOnlyList<Bar> _byProduct = Array.Empty<Bar>();
    [ObservableProperty] private IReadOnlyList<Bar> _byHour = Array.Empty<Bar>();

    [RelayCommand]
    private void Export() =>
        MessageBox.Show("엑셀 내보내기는 준비 중입니다.", "내보내기", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Recalc()
    {
        var from = Period.From();
        var all = _store.Orders.Where(o => o.ReceivedAt >= from).ToList();
        var valid = all.Where(o => o.Status != OrderStatus.Canceled).ToList();

        OrderCount = valid.Count;
        Sales = valid.Sum(o => o.Total);
        PackCount = valid.Sum(o => o.PackCount);
        CancelRate = all.Count == 0 ? 0 : 100.0 * (all.Count - valid.Count) / all.Count;
        AverageOrder = valid.Count == 0 ? 0 : Sales / valid.Count;

        // 상품별 매출
        var prod = Products.All
            .Select(p => (p.Name, Amount: valid.SelectMany(o => o.Items).Where(i => i.Id == p.Id).Sum(i => i.Amount)))
            .ToList();
        var maxP = Math.Max(1, prod.Max(x => x.Amount));
        ByProduct = prod.Select(x => new Bar(x.Name, x.Amount, $"{x.Amount:N0}원", (double)x.Amount / maxP)).ToList();

        // 시간대별 주문 수 (8시~21시)
        var hours = Enumerable.Range(8, 14)
            .Select(h => (h, Count: valid.Count(o => o.ReceivedAt.Hour == h)))
            .ToList();
        var maxH = Math.Max(1, hours.Max(x => x.Count));
        ByHour = hours.Select(x => new Bar($"{x.h}시", x.Count, x.Count.ToString(), (double)x.Count / maxH)).ToList();
    }
}
