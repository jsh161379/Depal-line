using System.IO;
using System.Windows;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
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
/// - [완료] 기간을 고르면 DB에서 그 기간 주문을 불러온다 (StatsDbReader.cs). 가짜 모드·DB 없음이면 메모리 주문만
/// - 날짜 직접 고르기 (DatePicker)
/// - [완료] 엑셀(.xlsx) 내보내기 — 선택한 기간의 요약·상품별·시간대별·주문 목록 (StatsExcelReport.cs)
/// - 마감 보고서 인쇄
/// - 자주 주문하는 고객 TOP 5
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly OrderStore _store;
    private readonly AppSettings _settings;

    /// <summary>마지막으로 DB에서 읽은 기간의 주문. 없으면 null (가짜 모드, DB 없음, 실패)</summary>
    private List<Order>? _dbOrders;
    private CancellationTokenSource? _loadCts;

    public StatsViewModel(OrderStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
        Periods = new[]
        {
            new Period("오늘", () => DateTime.Today),
            new Period("최근 7일", () => DateTime.Today.AddDays(-6)),
            new Period("이번 달", () => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)),
        };
        _period = Periods[0];
        _store.Changed += Recalc;
        _ = LoadAsync();
    }

    public IReadOnlyList<Period> Periods { get; }

    [ObservableProperty] private Period _period;
    partial void OnPeriodChanged(Period value)
    {
        _dbOrders = null;   // 다른 기간의 DB 결과가 섞이지 않게
        Recalc();
        _ = LoadAsync();
    }

    /// <summary>화면에 보일 데이터 출처: "DB 기준 · 15:02 불러옴" 등</summary>
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private bool _isLoading;

    private bool UseDb => !_settings.UseFakeOrders && !string.IsNullOrWhiteSpace(_settings.MySqlConnection);

    /// <summary>고른 기간의 주문을 DB에서 다시 읽는다 (새로고침 버튼)</summary>
    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();   // 앞에서 불러오던 것은 취소 (Dispose는 안 함: 돌고 있는 작업이 아직 토큰을 씀)
        _loadCts = null;

        if (!UseDb)
        {
            SourceText = _settings.UseFakeOrders ? "가짜 주문 (켠 뒤 들어온 주문만)" : "DB 연결 설정 없음 (켠 뒤 들어온 주문만)";
            Recalc();
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var period = Period;
        var (from, to) = (period.From(), DateTime.Today.AddDays(1));
        IsLoading = true;
        SourceText = "DB에서 불러오는 중…";
        try
        {
            var orders = await Task.Run(() => StatsDbReader.LoadAsync(_settings.MySqlConnection!, from, to, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(period, Period)) return;
            _dbOrders = orders;
            SourceText = $"DB 기준 · {DateTime.Now:HH:mm} 불러옴";
        }
        catch (OperationCanceledException)
        {
            return;   // 다른 기간을 골라서 취소됨
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(period, Period)) return;
            _dbOrders = null;
            SourceText = $"DB 조회 실패 — 켠 뒤 들어온 주문만 ({ex.Message})";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
        Recalc();
    }

    [ObservableProperty] private int _orderCount;
    [ObservableProperty] private int _sales;
    [ObservableProperty] private int _packCount;
    [ObservableProperty] private double _cancelRate;     // 0~100
    [ObservableProperty] private int _averageOrder;

    [ObservableProperty] private IReadOnlyList<Bar> _byProduct = Array.Empty<Bar>();
    [ObservableProperty] private IReadOnlyList<Bar> _byHour = Array.Empty<Bar>();

    /// <summary>
    /// 지금 고른 기간의 통계를 서식 있는 엑셀 파일(.xlsx)로 저장한다 (시트: 요약, 주문 목록).
    /// 만드는 방법은 StatsExcelReport.cs. 연락처는 가린 번호만, 주소는 넣지 않는다.
    /// </summary>
    [RelayCommand]
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "통계 엑셀로 내보내기",
            Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"바른전지_통계_{Period.Label.Replace(" ", "")}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;

        var orders = OrdersInPeriod();
        var summary = new StatsExcelReport.Summary(
            Period.Label, Period.From(), DateTime.Today,
            OrderCount, Sales, AverageOrder, PackCount,
            orders.Count(o => o.Status == OrderStatus.Canceled), CancelRate);
        try
        {
            StatsExcelReport.Save(dialog.FileName, summary, orders, ByHour);
            MessageBox.Show($"저장했습니다.\n{dialog.FileName}", "내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"저장하지 못했습니다. 같은 파일이 엑셀에 열려 있으면 닫고 다시 시도하세요.\n\n{ex.Message}",
                "내보내기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 고른 기간의 주문 (취소 포함, 접수 시각 순).
    /// DB에서 읽은 주문 + 프로그램에 있는 주문을 주문번호로 합친다.
    /// 같은 번호면 프로그램 쪽(상태가 가장 최신)을 쓴다 → DB에 아직 저장 안 된 새 주문도 바로 들어간다.
    /// </summary>
    private List<Order> OrdersInPeriod()
    {
        var from = Period.From();
        var to = DateTime.Today.AddDays(1);
        var byNo = new Dictionary<string, Order>();
        foreach (var o in _dbOrders ?? Enumerable.Empty<Order>()) byNo[o.No] = o;
        foreach (var o in _store.Orders.Where(o => o.ReceivedAt >= from && o.ReceivedAt < to)) byNo[o.No] = o;
        return byNo.Values.OrderBy(o => o.ReceivedAt).ToList();
    }

    private void Recalc()
    {
        var all = OrdersInPeriod();
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
