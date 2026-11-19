using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Stats;

/// <summary>하루치 집계 (일별 표 한 줄). 주문 수·매출·판매 팩은 취소 제외, Canceled는 취소 건수.</summary>
public sealed record DayRow(DateTime Date, int Orders, int Sales, int Packs, int Canceled);

/// <summary>상품 하나의 집계 (취소 제외)</summary>
public sealed record ProductRow(string Id, string Name, int Packs, int Sales);

/// <summary>주문 상태 하나의 건수 (취소 포함 전체 주문 기준)</summary>
public sealed record StatusRow(OrderStatus Status, string Name, int Count);

/// <summary>시간대 하나의 주문 수 (취소 제외)</summary>
public sealed record HourRow(int Hour, int Orders);

/// <summary>
/// 한 기간의 통계 결과. 화면(통계 탭), 엑셀 내보내기, 주문 관리 보고서가 같은 값을 쓴다.
/// Start·End는 날짜만 의미가 있고 둘 다 포함한다.
/// </summary>
public sealed record StatsResult(
    DateTime Start,
    DateTime End,
    IReadOnlyList<Order> Orders,          // 기간 안의 주문 전체 (취소 포함, 접수 시각 순)
    int OrderCount,                       // 주문 수 (취소 제외)
    int Sales,                            // 매출 (취소 제외, 원)
    int PackCount,                        // 판매 팩 수 (취소 제외)
    int BatteryCount,                     // 판매 건전지 수 (취소 제외)
    int CancelCount,                      // 취소 건수
    IReadOnlyList<ProductRow> ByProduct,
    IReadOnlyList<HourRow> ByHour,        // 8시~21시
    IReadOnlyList<DayRow> ByDay,          // 시작일~끝일 하루씩
    IReadOnlyList<StatusRow> ByStatus)    // 주문 접수 / 출고 검사 / 배송 중 / 배송 완료 / 취소
{
    /// <summary>주문당 평균 매출 (원)</summary>
    public int AverageOrder => OrderCount == 0 ? 0 : Sales / OrderCount;

    /// <summary>취소율 (%) = 취소 / 전체 주문</summary>
    public double CancelRate => Orders.Count == 0 ? 0 : 100.0 * CancelCount / Orders.Count;

    /// <summary>출고·배송 건수 = 배송 중 + 배송 완료</summary>
    public int ShippedCount => CountOf(OrderStatus.Shipping) + CountOf(OrderStatus.Delivered);

    /// <summary>아직 처리 중인 건수 = 주문 접수 + 출고 검사</summary>
    public int PendingCount => CountOf(OrderStatus.Received) + CountOf(OrderStatus.Inspect);

    public int CountOf(OrderStatus s) => ByStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;
}

/// <summary>
/// 주문 목록 → 기간 통계. WPF를 쓰지 않는 순수 계산이라 보고서 등 다른 곳에서도 그대로 쓸 수 있다.
/// </summary>
public static class StatsCalculator
{
    public const int FirstHour = 8;
    public const int LastHour = 21;

    private static readonly OrderStatus[] StatusOrder =
    {
        OrderStatus.Received, OrderStatus.Inspect, OrderStatus.Shipping, OrderStatus.Delivered, OrderStatus.Canceled,
    };

    /// <summary>source에서 접수 시각이 start(포함)~end(포함, 그날 끝까지)인 주문만 골라 계산한다.</summary>
    public static StatsResult Compute(IEnumerable<Order> source, DateTime start, DateTime end)
    {
        var from = start.Date;
        var to = end.Date.AddDays(1);
        var all = source.Where(o => o.ReceivedAt >= from && o.ReceivedAt < to)
                        .OrderBy(o => o.ReceivedAt)
                        .ToList();
        var valid = all.Where(o => o.Status != OrderStatus.Canceled).ToList();
        var items = valid.SelectMany(o => o.Items).ToList();

        var byProduct = Products.All
            .Select(p =>
            {
                var mine = items.Where(i => i.Id == p.Id).ToList();
                return new ProductRow(p.Id, p.Name, mine.Sum(i => i.Qty), mine.Sum(i => i.Amount));
            })
            .ToList();

        var byHour = Enumerable.Range(FirstHour, LastHour - FirstHour + 1)
            .Select(h => new HourRow(h, valid.Count(o => o.ReceivedAt.Hour == h)))
            .ToList();

        var byDay = new List<DayRow>();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var day = all.Where(o => o.ReceivedAt.Date == d).ToList();
            var ok = day.Where(o => o.Status != OrderStatus.Canceled).ToList();
            byDay.Add(new DayRow(d, ok.Count, ok.Sum(o => o.Total), ok.Sum(o => o.PackCount), day.Count - ok.Count));
        }

        var byStatus = StatusOrder
            .Select(s => new StatusRow(s, OrderStatusText.ToKorean(s), all.Count(o => o.Status == s)))
            .ToList();

        return new StatsResult(
            from, end.Date, all,
            valid.Count, valid.Sum(o => o.Total), valid.Sum(o => o.PackCount), valid.Sum(o => o.BatteryCount),
            all.Count - valid.Count,
            byProduct, byHour, byDay, byStatus);
    }

    /// <summary>"+3건 (▲25.0%)" 같은 증감 글자. 이전 값이 0이면 비율은 빼고 차이만.</summary>
    public static string DiffText(double now, double before, string unit, string format = "N0")
    {
        var diff = now - before;
        var sign = diff > 0 ? "+" : diff < 0 ? "−" : "±";
        var abs = Math.Abs(diff).ToString(format);
        if (before == 0) return $"{sign}{abs}{unit}";
        var rate = 100.0 * diff / before;
        var arrow = rate > 0 ? "▲" : rate < 0 ? "▼" : "";
        return $"{sign}{abs}{unit} ({arrow}{Math.Abs(rate):0.0}%)";
    }
}
