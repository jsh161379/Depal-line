using System.IO;
using System.Windows;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Stats;

/// <summary>막대 한 줄 (상품별, 시간대별, 일별). Ratio는 화면에 그릴 막대 길이(0~1).</summary>
public sealed record Bar(string Label, int Value, string ValueText, double Ratio, string Tip = "", string SubLabel = "");

/// <summary>
/// 담당: 장성찬 — F4 통계.
/// 기간(일일/주간/월간/직접)별 주문 수·매출·판매 팩·취소율, 이전 기간 대비 증감,
/// 일별 주문·매출, 상품별 매출, 시간대별 주문 수, 주문 상태별 건수(출고·배송), 엑셀 내보내기.
///
/// - 기간 계산: StatsPeriod.cs (주간 = 월~일, 월간 = 1일~말일)
/// - 계산: StatsCalculator.cs (화면·엑셀·보고서가 같은 값을 씀)
/// - DB 조회: StatsDbReader.cs (가짜 모드·DB 설정 없음이면 프로그램에 있는 주문만)
/// - 엑셀: StatsExcelReport.cs
///
/// - 마감 보고서(PDF): StatsClosingReport.cs (페이지를 그림으로 그려 PDF 파일로 저장)
///
/// - [완료] 자주 주문한 고객 TOP 5, 문의 유형별 건수, 마감 보고서 PDF
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly OrderStore _store;
    private readonly AppSettings _settings;

    /// <summary>DB에서 읽은 주문과 그 범위 (시작일 포함 ~ 끝 다음 날 미포함). 없으면 null.</summary>
    private List<Order>? _dbOrders;
    private DateTime _dbFrom, _dbTo;
    private CancellationTokenSource? _loadCts;

    /// <summary>코드에서 기간을 바꾸는 동안 날짜 변경 알림으로 "직접"이 되지 않게</summary>
    private bool _settingRange;

    public StatsViewModel(OrderStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
        PeriodOptions = new[]
        {
            new PeriodOption("일일", PeriodKind.Day),
            new PeriodOption("주간", PeriodKind.Week),
            new PeriodOption("월간", PeriodKind.Month),
        };
        _selectedOption = PeriodOptions[0];
        (_startDate, _endDate) = StatsPeriod.For(PeriodKind.Day, DateTime.Today);
        _store.Changed += Recalc;
        _store.Inquiries.CollectionChanged += (_, _) => Recalc();   // 문의 유형별 건수
        UpdatePeriodText();
        Recalc();
        _ = LoadAsync();
    }

    // ───────────────────────── 기간 ─────────────────────────

    public IReadOnlyList<PeriodOption> PeriodOptions { get; }

    /// <summary>지금 기간 종류. 날짜를 직접 고치면 Custom.</summary>
    public PeriodKind Kind { get; private set; } = PeriodKind.Day;

    /// <summary>[일일/주간/월간] 중 고른 것. 직접 고른 기간이면 null.</summary>
    [ObservableProperty] private PeriodOption? _selectedOption;

    /// <summary>시작일 (DatePicker)</summary>
    [ObservableProperty] private DateTime _startDate;

    /// <summary>종료일 (DatePicker, 그날 포함)</summary>
    [ObservableProperty] private DateTime _endDate;

    /// <summary>"주간 · 2026-11-16(월) ~ 2026-11-22(일) · 7일"</summary>
    [ObservableProperty] private string _periodText = "";

    /// <summary>"전주 대비" 같은 비교 이름</summary>
    [ObservableProperty] private string _previousName = "전일";

    partial void OnSelectedOptionChanged(PeriodOption? value)
    {
        if (value == null || _settingRange) return;
        // 지금 시작일이 들어 있는 일/주/월로 맞춤
        SetRange(value.Kind, StatsPeriod.For(value.Kind, StartDate));
    }

    partial void OnStartDateChanged(DateTime value) => OnDatePicked();
    partial void OnEndDateChanged(DateTime value) => OnDatePicked();

    private void OnDatePicked()
    {
        if (_settingRange) return;
        var (s, e) = StartDate <= EndDate ? (StartDate, EndDate) : (EndDate, StartDate);
        SetRange(PeriodKind.Custom, (s, e));
    }

    /// <summary>◀ 이전 기간</summary>
    [RelayCommand]
    private void Prev() => SetRange(Kind, StatsPeriod.Shift(Kind, StartDate, EndDate, -1));

    /// <summary>▶ 다음 기간</summary>
    [RelayCommand]
    private void Next() => SetRange(Kind, StatsPeriod.Shift(Kind, StartDate, EndDate, +1));

    /// <summary>[오늘] 오늘이 들어 있는 기간 (직접 고른 기간이면 오늘 하루)</summary>
    [RelayCommand]
    private void Today()
    {
        var kind = Kind == PeriodKind.Custom ? PeriodKind.Day : Kind;
        SetRange(kind, StatsPeriod.For(kind, DateTime.Today));
    }

    private void SetRange(PeriodKind kind, (DateTime Start, DateTime End) range)
    {
        _settingRange = true;
        try
        {
            Kind = kind;
            StartDate = range.Start.Date;
            EndDate = range.End.Date;
            SelectedOption = PeriodOptions.FirstOrDefault(o => o.Kind == kind);   // 직접이면 null
        }
        finally
        {
            _settingRange = false;
        }
        UpdatePeriodText();
        Recalc();          // 프로그램에 있는 주문으로 먼저 보여 주고
        _ = LoadAsync();   // DB 결과가 오면 다시 계산
    }

    private void UpdatePeriodText()
    {
        PeriodText = $"{StatsPeriod.KindName(Kind)} · {StatsPeriod.RangeText(StartDate, EndDate)}";
        PreviousName = StatsPeriod.PreviousName(Kind);
    }

    private (DateTime Start, DateTime End) PreviousRange => StatsPeriod.Previous(Kind, StartDate, EndDate);

    // ───────────────────────── DB 조회 ─────────────────────────

    /// <summary>화면에 보일 데이터 출처: "DB 기준 · 15:02 불러옴" 등</summary>
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private bool _isLoading;

    private bool UseDb => !_settings.UseFakeOrders && !string.IsNullOrWhiteSpace(_settings.MySqlConnection);

    /// <summary>고른 기간(+비교할 이전 기간)의 주문을 DB에서 다시 읽는다</summary>
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
        // 이전 기간 비교까지 한 번에 읽음
        var from = PreviousRange.Start.Date;
        var to = EndDate.Date.AddDays(1);
        IsLoading = true;
        SourceText = "DB에서 불러오는 중…";
        try
        {
            var orders = await Task.Run(() => StatsDbReader.LoadAsync(_settings.MySqlConnection!, from, to, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            (_dbOrders, _dbFrom, _dbTo) = (orders, from, to);
            SourceText = $"DB 기준 · {DateTime.Now:HH:mm} 불러옴";
        }
        catch (OperationCanceledException)
        {
            return;   // 다른 기간을 골라서 취소됨
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _dbOrders = null;
            SourceText = $"DB 조회 실패 — 켠 뒤 들어온 주문만 ({ex.Message})";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
        Recalc();
    }

    /// <summary>
    /// 계산에 쓸 주문: DB에서 읽은 주문 + 프로그램에 있는 주문을 주문번호로 합친다.
    /// 같은 번호면 프로그램 쪽(상태가 가장 최신)을 쓴다 → DB에 아직 저장 안 된 새 주문도 바로 들어간다.
    /// DB 결과가 지금 기간을 다 덮지 못하면(기간을 바꾼 직후) DB 결과는 쓰지 않는다.
    /// </summary>
    private List<Order> SourceOrders(DateTime from, DateTime to)
    {
        var byNo = new Dictionary<string, Order>();
        if (_dbOrders != null && _dbFrom <= from && to <= _dbTo)
            foreach (var o in _dbOrders) byNo[o.No] = o;
        foreach (var o in _store.Orders.Where(o => o.ReceivedAt >= from && o.ReceivedAt < to)) byNo[o.No] = o;
        return byNo.Values.ToList();
    }

    // ───────────────────────── 결과 ─────────────────────────

    /// <summary>지금 기간 결과 (보고서·엑셀이 그대로 씀)</summary>
    public StatsResult? CurrentResult { get; private set; }

    /// <summary>이전 기간 결과 (전일·전주·지난달 비교)</summary>
    public StatsResult? PreviousResult { get; private set; }

    [ObservableProperty] private int _orderCount;
    [ObservableProperty] private int _sales;
    [ObservableProperty] private int _packCount;
    [ObservableProperty] private double _cancelRate;     // 0~100
    [ObservableProperty] private int _averageOrder;
    [ObservableProperty] private int _shippedCount;      // 배송 중 + 배송 완료
    [ObservableProperty] private int _pendingCount;      // 주문 접수 + 출고 검사

    // 이전 기간 대비: "전주 대비 +3건 (▲25.0%)"
    [ObservableProperty] private string _orderDiffText = "";
    [ObservableProperty] private string _salesDiffText = "";
    [ObservableProperty] private string _packDiffText = "";
    [ObservableProperty] private string _cancelDiffText = "";

    [ObservableProperty] private IReadOnlyList<Bar> _byProduct = Array.Empty<Bar>();
    [ObservableProperty] private IReadOnlyList<Bar> _byHour = Array.Empty<Bar>();
    [ObservableProperty] private IReadOnlyList<Bar> _byDay = Array.Empty<Bar>();
    [ObservableProperty] private IReadOnlyList<StatusRow> _byStatus = Array.Empty<StatusRow>();
    [ObservableProperty] private IReadOnlyList<CustomerRow> _topCustomers = Array.Empty<CustomerRow>();
    [ObservableProperty] private IReadOnlyList<InquiryRow> _inquiryRows = Array.Empty<InquiryRow>();

    /// <summary>문의는 켤 때 DB에서 읽은 기간(최근 DbLoadDays일 + 답변 안 한 것) + 새로 받은 것만 센다</summary>
    public string InquiryNote => $"켤 때 불러온 최근 {_settings.DbLoadDays}일 문의 + 새 문의 기준";

    private void Recalc()
    {
        var (ps, pe) = PreviousRange;
        var source = SourceOrders(ps.Date, EndDate.Date.AddDays(1));
        var now = StatsCalculator.Compute(source, StartDate, EndDate);
        var before = StatsCalculator.Compute(source, ps, pe);
        CurrentResult = now;
        PreviousResult = before;

        OrderCount = now.OrderCount;
        Sales = now.Sales;
        PackCount = now.PackCount;
        CancelRate = now.CancelRate;
        AverageOrder = now.AverageOrder;
        ShippedCount = now.ShippedCount;
        PendingCount = now.PendingCount;

        var p = PreviousName;
        OrderDiffText = $"{p} 대비 {StatsCalculator.DiffText(now.OrderCount, before.OrderCount, "건")}";
        SalesDiffText = $"{p} 대비 {StatsCalculator.DiffText(now.Sales, before.Sales, "원")}";
        PackDiffText = $"{p} 대비 {StatsCalculator.DiffText(now.PackCount, before.PackCount, "팩")}";
        var pt = now.CancelRate - before.CancelRate;
        CancelDiffText = $"{p} 대비 {(pt > 0 ? "+" : pt < 0 ? "−" : "±")}{Math.Abs(pt):0.0}%p";

        // 상품별 매출
        var maxP = Math.Max(1, now.ByProduct.Max(x => x.Sales));
        ByProduct = now.ByProduct
            .Select(x => new Bar(x.Name, x.Sales, $"{x.Sales:N0}원", (double)x.Sales / maxP, $"{x.Name}: {x.Packs}팩 · {x.Sales:N0}원"))
            .ToList();

        // 시간대별 주문 수
        var maxH = Math.Max(1, now.ByHour.Max(x => x.Orders));
        ByHour = now.ByHour
            .Select(x => new Bar($"{x.Hour}시", x.Orders, x.Orders.ToString(), (double)x.Orders / maxH, $"{x.Hour}시: {x.Orders}건"))
            .ToList();

        // 일별 주문·매출 (막대 길이 = 매출, 위 숫자 = 주문 수)
        var maxD = Math.Max(1, now.ByDay.Max(x => x.Sales));
        ByDay = now.ByDay
            .Select(x => new Bar(
                x.Date.Day.ToString(), x.Sales, x.Orders.ToString(), (double)x.Sales / maxD,
                $"{StatsPeriod.DayText(x.Date)}: 주문 {x.Orders}건 · {x.Sales:N0}원 · {x.Packs}팩 · 취소 {x.Canceled}건",
                StatsPeriod.Weekday(x.Date)))
            .ToList();

        ByStatus = now.ByStatus;
        TopCustomers = StatsCalculator.TopCustomers(now);
        InquiryRows = StatsCalculator.InquiriesByType(_store.Inquiries.ToList(), StartDate, EndDate);
    }

    // ───────────────────────── 마감 보고서 (PDF) ─────────────────────────

    /// <summary>
    /// 지금 기간의 주문 마감 보고서를 PDF로 저장한다 (A4, 관리자 프로그램 보고서와 같은 모양).
    /// 파일 이름 기본값: 바른전지_마감보고서_주간_20261116-20261122.pdf (엑셀 내보내기와 같은 규칙)
    /// </summary>
    [RelayCommand]
    private void ClosingReport()
    {
        if (CurrentResult == null || PreviousResult == null) Recalc();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "마감 보고서 PDF로 저장",
            Filter = "PDF 문서 (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            // 엑셀과 같은 모양: 바른전지_통계_주간_20261116-20261122.xlsx → 바른전지_마감보고서_주간_20261116-20261122.pdf
            FileName = $"바른전지_마감보고서_{StatsPeriod.KindName(Kind).Replace(" ", "")}_{StartDate:yyyyMMdd}-{EndDate:yyyyMMdd}.pdf",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var doc = StatsClosingReport.Build(Kind, CurrentResult!, PreviousResult!, InquiryRows, _settings.Operator);
            StatsClosingReport.SavePdf(doc, dialog.FileName);
            MessageBox.Show($"저장했습니다.\n{dialog.FileName}", "마감 보고서", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"저장하지 못했습니다. 같은 파일이 PDF 보기 프로그램에 열려 있으면 닫고 다시 시도하세요.\n\n{ex.Message}",
                "마감 보고서", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ───────────────────────── 엑셀 ─────────────────────────

    /// <summary>
    /// 지금 기간의 통계를 서식 있는 엑셀 파일(.xlsx)로 저장한다 (시트: 요약, 일별, 주문 목록).
    /// 만드는 방법은 StatsExcelReport.cs. 연락처는 가린 번호만, 주소는 넣지 않는다.
    /// </summary>
    [RelayCommand]
    private void Export()
    {
        if (CurrentResult == null || PreviousResult == null) Recalc();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "통계 엑셀로 내보내기",
            Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"바른전지_통계_{StatsPeriod.KindName(Kind).Replace(" ", "")}_{StartDate:yyyyMMdd}-{EndDate:yyyyMMdd}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            StatsExcelReport.Save(dialog.FileName, Kind, CurrentResult!, PreviousResult!, TopCustomers, InquiryRows, InquiryNote);
            MessageBox.Show($"저장했습니다.\n{dialog.FileName}", "내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"저장하지 못했습니다. 같은 파일이 엑셀에 열려 있으면 닫고 다시 시도하세요.\n\n{ex.Message}",
                "내보내기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
