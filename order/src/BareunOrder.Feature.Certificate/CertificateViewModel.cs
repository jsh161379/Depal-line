using System.IO;
using System.Windows;
using System.Windows.Data;
using BareunOrder.Core.Comm;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
using BareunOrder.Feature.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Certificate;

public sealed record CheckRow(string Item, string Standard, string Result, bool Pass);

public sealed record ReportKindOption(string Label, PeriodKind Kind);

public enum PreviewTabKind {
    Certificate,
    Report,
}

public sealed record CertificateData(
    string CertNo,
    DateTime InspectedAt,
    string RunId,
    int Count,
    IReadOnlyList<CheckRow> Rows,
    IReadOnlyList<RunSummaryRow> Runs,
    bool IsExample,
    bool HasRuns,
    string? MissingNotice = null) {
    public bool AllPass => HasRuns && Rows.Count > 0 && Rows.All(r => r.Pass);
    public string VerdictText => AllPass ? "합격" : (HasRuns ? "불합격" : "미충족");
    public string StampText => AllPass ? "합 격" : (HasRuns ? "불합격" : "미충족");
}

/// <summary>
/// 담당: 유동호 — F6 검사 성적서 및 주문 관리 기간 보고서(일일/주간/월간)
/// </summary>
public sealed partial class CertificateViewModel : ObservableObject {
    // TODO(유동호): 하드웨어 실측 후 서버 grading_rules A등급 확정 기준과 동기화 (현재 최신 구성도 임시 기준: 1.50 V / 0.35 Ω)
    private const double AGradeVOpenMin = 1.50;
    private const double AGradeRIntMax = 0.35;

    private readonly OrderStore _store;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _loadCts;
    private bool _syncingRange;
    private DateTime _effectiveStart;
    private DateTime _effectiveEnd;

    public event Action? ReportBundleChanged;

    public string OperatorName => _settings.Operator;

    public CertificateViewModel(OrderStore store, AppSettings settings) {
        _store = store;
        _settings = settings;

        PeriodOptions = new[]
        {
            new ReportKindOption("일일", PeriodKind.Day),
            new ReportKindOption("주간", PeriodKind.Week),
            new ReportKindOption("월간", PeriodKind.Month),
        };
        // 초기 화면에서 이번 달 출고 주문이 바로 보이도록 월간으로 시작
        _selectedPeriodOption = PeriodOptions[2];

        var today = DateTime.Today;
        var (s, e) = OrderPeriodReportPrinter.ResolveRange(PeriodKind.Month, today);
        _effectiveStart = s;
        _effectiveEnd = e;
        _startDate = s;
        _endDate = e;
        UpdatePeriodSummary();

        Shipped = new ListCollectionView(store.Orders) {
            Filter = FilterShippedOrder,
        };

        _store.Changed += () =>
        {
            Shipped.Refresh();
            RefreshReportBundle();
        };

        RefreshReportBundle();
    }

    private bool FilterShippedOrder(object obj) {
        if (obj is not Order order) return false;
        if (order.Status is not (OrderStatus.Shipping or OrderStatus.Delivered)) return false;
        if (ShowAllShipped) return true;

        var d = order.ReceivedAt.Date;
        var s = _effectiveStart;
        var e = _effectiveEnd;
        if (s > e) (s, e) = (e, s);
        return d >= s && d <= e;
    }

    // ───────────────────────── 프리뷰 탭(성적서 / 보고서) ─────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCertificateTab))]
    [NotifyPropertyChangedFor(nameof(IsReportTab))]
    private PreviewTabKind _activePreviewTab = PreviewTabKind.Certificate;

    public bool IsCertificateTab => ActivePreviewTab == PreviewTabKind.Certificate;
    public bool IsReportTab => ActivePreviewTab == PreviewTabKind.Report;

    [RelayCommand]
    private void ShowCertificateTab() => ActivePreviewTab = PreviewTabKind.Certificate;

    [RelayCommand]
    private void ShowReportTab() {
        ActivePreviewTab = PreviewTabKind.Report;
        RefreshReportBundle(forceNotify: true);
    }

    // ───────────────────────── 상단 기간 설정 (전체 날짜 선택 가능 + 시작일 기준 재계산) ─────────────────────────

    public IReadOnlyList<ReportKindOption> PeriodOptions { get; }

    public PeriodKind CurrentPeriodKind => SelectedPeriodOption?.Kind ?? PeriodKind.Month;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPeriodKind))]
    [NotifyPropertyChangedFor(nameof(PeriodSummaryText))]
    private ReportKindOption? _selectedPeriodOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodSummaryText))]
    private DateTime? _startDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodSummaryText))]
    private DateTime? _endDate;

    [ObservableProperty]
    private bool _showAllShipped;

    [ObservableProperty]
    private string _periodSummaryText = "";

    [ObservableProperty]
    private PeriodReportBundle? _reportBundle;

    partial void OnSelectedPeriodOptionChanged(ReportKindOption? value) {
        if (_syncingRange || value == null) return;
        ApplyPeriodFromStartDate(value.Kind, _effectiveStart);
    }

    partial void OnStartDateChanged(DateTime? value) {
        if (_syncingRange || value == null) return;
        _effectiveStart = value.Value.Date;
        UpdatePeriodSummary();
        Shipped.Refresh();
        RefreshReportBundle();
    }

    partial void OnEndDateChanged(DateTime? value) {
        if (_syncingRange || value == null) return;
        _effectiveEnd = value.Value.Date;
        UpdatePeriodSummary();
        Shipped.Refresh();
        RefreshReportBundle();
    }

    partial void OnShowAllShippedChanged(bool value) {
        Shipped.Refresh();
    }

    private void ApplyPeriodFromStartDate(PeriodKind kind, DateTime baseStart) {
        var (s, e) = OrderPeriodReportPrinter.ResolveRange(kind, baseStart);
        ApplyExactRange(s, e);
    }

    private void ApplyExactRange(DateTime start, DateTime end) {
        var s = start.Date;
        var e = end.Date;
        if (s > e) (s, e) = (e, s);

        _syncingRange = true;
        try {
            _effectiveStart = s;
            _effectiveEnd = e;
            _startDate = s;
            _endDate = e;
            OnPropertyChanged(nameof(StartDate));
            OnPropertyChanged(nameof(EndDate));
        } finally {
            _syncingRange = false;
        }
        UpdatePeriodSummary();
        Shipped.Refresh();
        RefreshReportBundle();
    }

    [RelayCommand]
    private void PrevPeriod() {
        var kind = CurrentPeriodKind;
        var s = _effectiveStart;
        var e = _effectiveEnd;
        if (s > e) (s, e) = (e, s);

        if (!OrderPeriodReportPrinter.IsStandardRange(kind, s, e)) {
            var days = (e - s).Days + 1;
            ApplyExactRange(s.AddDays(-days), s.AddDays(-1));
            return;
        }

        var nextBase = kind switch {
            PeriodKind.Week => s.AddDays(-7),
            PeriodKind.Month => new DateTime(s.Year, s.Month, 1).AddMonths(-1),
            _ => s.AddDays(-1),
        };
        ApplyPeriodFromStartDate(kind, nextBase);
    }

    [RelayCommand]
    private void NextPeriod() {
        var kind = CurrentPeriodKind;
        var s = _effectiveStart;
        var e = _effectiveEnd;
        if (s > e) (s, e) = (e, s);

        if (!OrderPeriodReportPrinter.IsStandardRange(kind, s, e)) {
            var days = (e - s).Days + 1;
            ApplyExactRange(e.AddDays(1), e.AddDays(days));
            return;
        }

        var nextBase = kind switch {
            PeriodKind.Week => s.AddDays(7),
            PeriodKind.Month => new DateTime(s.Year, s.Month, 1).AddMonths(1),
            _ => s.AddDays(1),
        };
        ApplyPeriodFromStartDate(kind, nextBase);
    }

    [RelayCommand]
    private void SelectTodayPeriod() {
        ApplyPeriodFromStartDate(CurrentPeriodKind, DateTime.Today);
    }

    private void UpdatePeriodSummary() {
        var kind = CurrentPeriodKind;
        var s = _effectiveStart;
        var e = _effectiveEnd;
        if (s > e) (s, e) = (e, s);
        var days = (e - s).Days + 1;
        var label = OrderPeriodReportPrinter.IsStandardRange(kind, s, e)
            ? StatsPeriod.KindName(kind)
            : "지정 기간";

        PeriodSummaryText = s == e
            ? $"{label} · {StatsPeriod.DayText(s)}"
            : $"{label} · {StatsPeriod.DayText(s)} ~ {StatsPeriod.DayText(e)} · {days}일";
    }

    private void RefreshReportBundle(bool forceNotify = false) {
        ReportBundle = OrderPeriodReportPrinter.BuildBundle(_store.Orders, CurrentPeriodKind, _effectiveStart, _effectiveEnd);
        if (IsReportTab || forceNotify)
            ReportBundleChanged?.Invoke();
    }

    [RelayCommand]
    private void PrintPeriodReport() {
        RefreshReportBundle();
        if (ReportBundle == null) return;
        try {
            OrderPeriodReportPrinter.PrintBundle(ReportBundle, _settings.Operator);
        } catch (Exception ex) {
            MessageBox.Show($"보고서 출력 중 오류가 발생했습니다.\n{ex.Message}", "주문 관리 보고서", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────────────── 성적서 조회 · 발급 · 인쇄 ─────────────────────────

    public ListCollectionView Shipped { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IssueButtonText))]
    [NotifyPropertyChangedFor(nameof(StatusBannerText))]
    [NotifyPropertyChangedFor(nameof(IsWarningBanner))]
    private Order? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBannerText))]
    [NotifyPropertyChangedFor(nameof(IsWarningBanner))]
    private CertificateData? _data;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBannerText))]
    [NotifyPropertyChangedFor(nameof(IsWarningBanner))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBannerText))]
    [NotifyPropertyChangedFor(nameof(IsWarningBanner))]
    private string? _errorMessage;

    partial void OnSelectedChanged(Order? value) {
        RefreshCommand.NotifyCanExecuteChanged();
        IssueCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
        if (value != null)
            ActivePreviewTab = PreviewTabKind.Certificate;
        _ = LoadSelectedAsync(value);
    }

    partial void OnDataChanged(CertificateData? value) {
        IssueCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value) {
        RefreshCommand.NotifyCanExecuteChanged();
        IssueCommand.NotifyCanExecuteChanged();
    }

    public bool IsWarningBanner =>
        !string.IsNullOrEmpty(ErrorMessage) || Data is { IsExample: true } or { HasRuns: false };

    public string StatusBannerText {
        get {
            if (Selected == null) return "";
            if (IsLoading) return "라인 DB에서 검사 데이터를 불러오는 중입니다...";
            if (!string.IsNullOrEmpty(ErrorMessage)) return ErrorMessage;
            if (Data == null) return "";
            if (Data.IsExample) return "예시 값 — Fake 모드 또는 DB 미연결 상태";
            if (!Data.HasRuns) return "연결된 생산 회차(order_runs)가 없습니다 — 출고 시 라인 회차를 먼저 연결하세요";
            return string.IsNullOrEmpty(Selected.CertNo)
                ? "라인 DB 실측 데이터 조회 완료 · 미발급 상태"
                : $"성적서 발급 완료 ({Selected.CertNo})";
        }
    }

    public string IssueButtonText =>
        Selected != null && !string.IsNullOrEmpty(Selected.CertNo) ? "성적서 재발급" : "성적서 발급";

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadSelectedAsync(Selected);

    private bool CanRefresh() => Selected != null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanIssue))]
    private async Task IssueAsync() {
        if (Selected == null || Data == null) return;
        if (!Data.IsExample && !Data.HasRuns) {
            MessageBox.Show("연결된 생산 회차가 없어 성적서를 발급할 수 없습니다.", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string certNo;
        try {
            certNo = OrderJson.CertNoFor(Selected.No)
                     ?? throw new InvalidOperationException($"주문번호 형식({Selected.No})이 올바르지 않습니다.");
        } catch (Exception ex) {
            MessageBox.Show(ex.Message, "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var msg = string.IsNullOrEmpty(Selected.CertNo)
            ? $"주문 {Selected.No}의 검사 성적서({certNo})를 발급하시겠습니까?\n발급 후 홈페이지 주문 조회에도 반영됩니다."
            : $"이미 발급된 성적서({Selected.CertNo})를 다시 발행하시겠습니까?";
        if (MessageBox.Show(msg, "성적서 발급", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try {
            await _store.SetCertNoAsync(Selected, certNo);
            Data = Data with { CertNo = certNo };
            OnPropertyChanged(nameof(IssueButtonText));
            OnPropertyChanged(nameof(StatusBannerText));
            Shipped.Refresh();
            RefreshReportBundle();
        } catch (Exception ex) {
            MessageBox.Show($"성적서 발급 중 오류가 발생했습니다.\n{ex.Message}", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanIssue() =>
        Selected != null && Data != null && !IsLoading && (Data.IsExample || Data.HasRuns);

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private void Print() {
        if (Selected == null || Data == null) return;
        try {
            CertificatePrinter.Print(Selected, Data);
        } catch (Exception ex) {
            MessageBox.Show($"인쇄 중 오류가 발생했습니다.\n{ex.Message}", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private void ExportCsv() {
        if (Selected == null || Data == null) return;
        try {
            var path = CertificateCsvExporter.Export(Selected, Data);
            if (path != null) {
                _store.Write($"{Selected.No} 성적서 CSV 저장 ({Path.GetFileName(path)})");
                MessageBox.Show($"CSV 파일을 저장했습니다.\n{path}", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        } catch (Exception ex) {
            MessageBox.Show($"CSV 저장 중 오류가 발생했습니다.\n{ex.Message}", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanExportCsv() => Selected != null && Data != null;
    private bool CanPrint() => Selected != null && Data != null;

    private async Task LoadSelectedAsync(Order? order) {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        ErrorMessage = null;

        if (order == null) {
            Data = null;
            return;
        }

        var certNo = OrderJson.CertNoFor(order.No) ?? $"CERT-{order.No}";

        if (_settings.UseFakeOrders || string.IsNullOrWhiteSpace(_settings.MySqlConnection)) {
            Data = MakeExample(order, string.IsNullOrEmpty(order.CertNo) ? certNo : order.CertNo);
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;

        try {
            var summary = await Task.Run(
                () => CertificateDbReader.LoadAsync(_settings.MySqlConnection!, order.No, cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested || Selected?.No != order.No) return;

            if (!string.IsNullOrEmpty(summary.DbCertNo) && order.CertNo != summary.DbCertNo) {
                order.CertNo = summary.DbCertNo;
                OnPropertyChanged(nameof(IssueButtonText));
                Shipped.Refresh();
                RefreshReportBundle();
            }

            var displayCertNo = !string.IsNullOrEmpty(order.CertNo) ? order.CertNo : certNo;
            Data = BuildFromDb(order, displayCertNo, summary);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            if (Selected?.No == order.No) {
                ErrorMessage = $"DB 조회 실패 ({ex.Message})";
                Data = MakeEmpty(order, !string.IsNullOrEmpty(order.CertNo) ? order.CertNo : certNo);
            }
        } finally {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    private static CertificateData BuildFromDb(Order o, string certNo, CertificateDbSummary s) {
        if (s.Runs.Count == 0)
            return MakeEmpty(o, certNo);

        var runText = s.Runs.Count == 1
            ? s.Runs[0].SourceRunId
            : $"{s.Runs[0].SourceRunId} 외 {s.Runs.Count - 1}건";
        var inspectedAt = s.Runs.Max(r => r.StartedAt) ?? o.ReceivedAt;

        string visualResult;
        bool visualOk;
        if (s.VisualInspected == 0) {
            visualResult = "미측정";
            visualOk = false;
        } else {
            var confText = s.VisualConfidence.HasValue ? $" (신뢰도 {s.VisualConfidence.Value:P1})" : "";
            var failPart = (s.VisualFail + s.VisualError) > 0 ? $", 불량·오류 {s.VisualFail + s.VisualError}건" : "";
            visualResult = $"{s.VisualPass}/{s.VisualInspected} 통과{failPart}{confText}";
            visualOk = s.VisualFail == 0 && s.VisualError == 0;
        }

        string polarityResult;
        bool polarityOk;
        if (s.VisualInspected == 0 && s.MeasuredCount == 0) {
            polarityResult = "미측정";
            polarityOk = false;
        } else {
            var parts = new List<string>();
            if (s.VisualMinusUp > 0) parts.Add($"-상 {s.VisualMinusUp}");
            if (s.VisualPlusUp > 0) parts.Add($"+상 {s.VisualPlusUp}");
            if (s.VisualPolarityUnknown > 0) parts.Add($"미상 {s.VisualPolarityUnknown}");
            var camText = parts.Count > 0 ? string.Join(" · ", parts) : "기록 없음";
            polarityResult = s.MeasuredCount > 0
                ? $"{camText} / 전기 정극성 {s.ElecPolarityNormal}/{s.MeasuredCount}"
                : camText;
            polarityOk = s.VisualPolarityUnknown == 0
                         && (s.MeasuredCount == 0 || s.ElecPolarityNormal == s.MeasuredCount);
        }

        var vOpenResult = s.VOpenAvg.HasValue
            ? $"{s.VOpenAvg.Value:0.000} V ({s.VOpenMin:0.00}~{s.VOpenMax:0.00})"
            : "미측정";
        var vOpenOk = s.VOpenAvg is >= AGradeVOpenMin;

        var vLoadResult = s.VLoadAvg.HasValue ? $"{s.VLoadAvg.Value:0.000} V" : "미측정";
        var vLoadOk = s.VLoadAvg is > 0;

        var rIntResult = s.RIntAvg.HasValue
            ? $"{s.RIntAvg.Value:0.000} Ω (최대 {s.RIntMax:0.000})"
            : "미측정";
        var rIntOk = s.RIntAvg is <= AGradeRIntMax;

        var rows = new List<CheckRow>
        {
            new("외관 (스크래치·스티커·훼손·부식)", "불량 없음", visualResult, visualOk),
            new("극 방향 (카메라·전기)", "정극성 / 방향 확인", polarityResult, polarityOk),
            new("무부하 전압 (평균)", $"{AGradeVOpenMin:0.00} V 이상", vOpenResult, vOpenOk),
            new("부하 전압 (평균)", "측정 완료 (참고)", vLoadResult, vLoadOk),
            new("내부저항 (평균)", $"{AGradeRIntMax:0.00} Ω 이하", rIntResult, rIntOk),
            new("등급 (A등급 출하)", "A", s.ACount > 0 ? $"A ({s.ACount}개)" : "기록 없음", s.ACount > 0),
        };

        string? notice = (s.ElectricalMissing > 0 || s.VisualMissing > 0)
            ? $"참고: 연결된 회차 A등급 {s.ACount}개 기준 집계이며, 누락 건수(전기 {s.ElectricalMissing}개 / 외관 {s.VisualMissing}개)는 평균에서 제외되었습니다."
            : "참고: 주문에 연결된 생산 회차(order_runs)의 A등급 전체 집계 기준입니다.";

        return new CertificateData(
            CertNo: certNo,
            InspectedAt: inspectedAt,
            RunId: runText,
            Count: o.BatteryCount,
            Rows: rows,
            Runs: s.Runs,
            IsExample: false,
            HasRuns: true,
            MissingNotice: notice);
    }

    private static CertificateData MakeEmpty(Order o, string certNo) {
        var rows = new List<CheckRow>
        {
            new("외관 (스크래치·스티커·훼손·부식)", "불량 없음", "-", false),
            new("극 방향 (카메라·전기)", "정극성 / 방향 확인", "-", false),
            new("무부하 전압 (평균)", $"{AGradeVOpenMin:0.00} V 이상", "-", false),
            new("부하 전압 (평균)", "측정 완료 (참고)", "-", false),
            new("내부저항 (평균)", $"{AGradeRIntMax:0.00} Ω 이하", "-", false),
            new("등급 (A등급 출하)", "A", "-", false),
        };
        return new CertificateData(certNo, o.ReceivedAt, "연결 없음", o.BatteryCount, rows, Array.Empty<RunSummaryRow>(), IsExample: false, HasRuns: false);
    }

    private static CertificateData MakeExample(Order o, string certNo) {
        var rnd = new Random(o.No.GetHashCode());
        var vOpen = 1.580 + rnd.NextDouble() * 0.030;
        var vLoad = vOpen - 0.08 - rnd.NextDouble() * 0.02;
        var rInt = 0.17 + rnd.NextDouble() * 0.06;
        var at = o.ReceivedAt.Date.AddHours(9).AddMinutes(rnd.Next(0, 300));

        var rows = new List<CheckRow>
        {
            new("외관 (스크래치·스티커·훼손·부식)", "불량 없음", "이상 없음", true),
            new("극 방향 (카메라·전기)", "정극성 / 방향 확인", "정방향", true),
            new("무부하 전압 (평균)", $"{AGradeVOpenMin:0.00} V 이상", $"{vOpen:0.000} V", vOpen >= AGradeVOpenMin),
            new("부하 전압 (평균)", "측정 완료 (참고)", $"{vLoad:0.000} V", vLoad > 0),
            new("내부저항 (평균)", $"{AGradeRIntMax:0.00} Ω 이하", $"{rInt:0.000} Ω", rInt <= AGradeRIntMax),
            new("등급 (A등급 출하)", "A", "A", true),
        };

        return new CertificateData(
            CertNo: certNo,
            InspectedAt: at,
            RunId: $"{at:yyyyMMdd-HHmmss}-{rnd.Next(0x1000, 0xFFFF):x4}",
            Count: o.BatteryCount,
            Rows: rows,
            Runs: Array.Empty<RunSummaryRow>(),
            IsExample: true,
            HasRuns: true);
    }
}