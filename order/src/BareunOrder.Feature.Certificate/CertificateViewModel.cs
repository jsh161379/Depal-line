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

public sealed record CheckRow(string Item, string Standard, string Result, bool Pass, bool IsReference = false) {
    public string StatusText => IsReference ? "참고" : (Pass ? "적합" : "부적합");
}

public sealed record CertProductRow(string Name, string PackSpec, int PackQty, int BatteryCount) {
    public string PackQtyText => $"{PackQty:N0}팩";
    public string BatteryCountText => $"{BatteryCount:N0}개";
}

public sealed record RunTotalSummary(
    int RunCount,
    int TotalShipped,
    int TotalACount,
    string VOpenText,
    string VLoadText,
    string RIntText,
    string VisualPassText) {
    public string Label => $"합계 ({RunCount}개 회차)";
}

/// <summary>관리자 화면 내부 참고용 정보 (고객용 성적서·인쇄물에는 표시하지 않음)</summary>
public sealed record AdminDetailInfo(
    string SourceLabel,
    int LinkedRunCount,
    string? RuleSourceText,
    string? VisualConfidenceText,
    string? CameraPolarityText,
    string? MissingText);

public sealed record ReportKindOption(string Label, PeriodKind Kind);

public enum PreviewTabKind {
    Certificate,
    Report,
}

public sealed record CertificateData(
    string CertNo,
    DateTime InspectedAt,
    string RunId,
    string RuleVersion,
    int Count,
    int TotalPacks,
    int LinkedACount,
    IReadOnlyList<CertProductRow> Products,
    IReadOnlyList<CheckRow> Rows,
    IReadOnlyList<RunSummaryRow> Runs,
    RunTotalSummary? RunTotal,
    AdminDetailInfo AdminInfo,
    bool IsExample,
    bool HasRuns) {
    public bool IsQuantitySufficient => IsExample || (HasRuns && LinkedACount >= Count);
    public bool ChecksAllPass => Rows.Count > 0 && Rows.Where(r => !r.IsReference).All(r => r.Pass);
    public bool AllPass => HasRuns && IsQuantitySufficient && ChecksAllPass;

    public string VerdictText => !HasRuns
        ? "미충족"
        : (!IsQuantitySufficient ? "수량 부족" : (ChecksAllPass ? "합격" : "불합격"));

    public string StampText => AllPass ? "합 격" : VerdictText;
}

/// <summary>
/// 담당: 유동호 — F6 검사 성적서 및 주문 관리 기간 보고서(일일/주간/월간)
/// </summary>
public sealed partial class CertificateViewModel : ObservableObject {
    // DB grading_rules 조회 불가·미등록 시 사용하는 폴백 기준 (11/20 임시 기준: A 무부하 ≥ 1.50 V · 내부저항 ≤ 1.20 Ω)
    private const double FallbackAGradeVOpenMin = 1.50;
    private const double FallbackAGradeRIntMax = 1.20;
    private const string FallbackRuleVersion = "임시 기준";

    private readonly OrderStore _store;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _loadCts;
    private bool _syncingRange;
    private DateTime _effectiveStart;
    private DateTime _effectiveEnd;

    public event Action? ReportBundleChanged;

    public string OperatorName => string.IsNullOrWhiteSpace(_settings.Operator) ? "담당자" : _settings.Operator.Trim();
    public string IssuedDateText => DateTime.Now.ToString("yyyy-MM-dd");

    public CertificateViewModel(OrderStore store, AppSettings settings) {
        _store = store;
        _settings = settings;

        PeriodOptions = new[]
        {
            new ReportKindOption("일일", PeriodKind.Day),
            new ReportKindOption("주간", PeriodKind.Week),
            new ReportKindOption("월간", PeriodKind.Month),
        };
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

    // ───────────────────────── 상단 기간 설정 ─────────────────────────

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
            OrderPeriodReportPrinter.PrintBundle(ReportBundle, OperatorName);
        } catch (Exception ex) {
            MessageBox.Show($"보고서 출력 중 오류가 발생했습니다.\n{ex.Message}", "주문 관리 보고서", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────────────── 성적서 조회 · 발급 · 인쇄 ─────────────────────────

    public ListCollectionView Shipped { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IssueButtonText))]
    [NotifyPropertyChangedFor(nameof(IssueStatusText))]
    [NotifyPropertyChangedFor(nameof(RecipientText))]
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
        !string.IsNullOrEmpty(ErrorMessage)
        || Data is { IsExample: true }
        || Data is { HasRuns: false }
        || Data is { IsQuantitySufficient: false }
        || Data is { ChecksAllPass: false };

    public string StatusBannerText {
        get {
            if (Selected == null) return "";
            if (IsLoading) return "라인 DB에서 검사 데이터를 불러오는 중입니다...";
            if (!string.IsNullOrEmpty(ErrorMessage)) return ErrorMessage;
            if (Data == null) return "";
            if (Data.IsExample) return "예시 값 — Fake 모드 또는 DB 미연결 상태";
            if (!Data.HasRuns) return "연결된 생산 회차가 없습니다 — 출고 탭에서 라인 회차를 먼저 연결해 주세요";
            if (!Data.IsQuantitySufficient)
                return $"연결 수량 {Data.LinkedACount:N0}개 / 주문 수량 {Data.Count:N0}개 — 회차를 더 연결해 주세요";
            if (!Data.ChecksAllPass)
                return "검사 기준 미달 항목이 있어 성적서를 발급할 수 없습니다";
            return string.IsNullOrEmpty(Selected.CertNo)
                ? "검사 데이터 확인 완료 · 미발급 상태"
                : $"성적서 발급 완료 ({Selected.CertNo})";
        }
    }

    public string IssueButtonText =>
        Selected != null && !string.IsNullOrEmpty(Selected.CertNo) ? "성적서 재발급" : "성적서 발급";

    public string IssueStatusText =>
        Selected != null && !string.IsNullOrEmpty(Selected.CertNo) ? "발급 완료" : "미발급 (미리보기)";

    public string RecipientText =>
        Selected == null ? "" : $"{Selected.Name} ({CustomerInfo.TypeText(Selected.CustomerType)})";

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
        if (!Data.IsExample && !Data.IsQuantitySufficient) {
            MessageBox.Show(
                $"연결 수량({Data.LinkedACount:N0}개)이 주문 수량({Data.Count:N0}개)보다 부족합니다.\n회차를 더 연결한 뒤 발급해 주세요.",
                "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Data.IsExample && !Data.ChecksAllPass) {
            MessageBox.Show("검사 기준 미달 항목이 있어 성적서를 발급할 수 없습니다.", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            OnPropertyChanged(nameof(IssueStatusText));
            OnPropertyChanged(nameof(StatusBannerText));
            Shipped.Refresh();
            RefreshReportBundle();
        } catch (Exception ex) {
            MessageBox.Show($"성적서 발급 중 오류가 발생했습니다.\n{ex.Message}", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanIssue() =>
        Selected != null && Data != null && !IsLoading && (Data.IsExample || Data.AllPass);

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private void Print() {
        if (Selected == null || Data == null) return;
        try {
            CertificatePrinter.Print(Selected, Data, OperatorName);
        } catch (Exception ex) {
            MessageBox.Show($"인쇄 중 오류가 발생했습니다.\n{ex.Message}", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private void ExportCsv() {
        if (Selected == null || Data == null) return;
        try {
            var path = CertificateCsvExporter.Export(Selected, Data, OperatorName);
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
                OnPropertyChanged(nameof(IssueStatusText));
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

    private static IReadOnlyList<CertProductRow> BuildProductRows(Order o) =>
        o.Items
            .Select(i => new CertProductRow(
                Name: i.Name,
                PackSpec: $"{Products.PackSize(i.Id)}개입",
                PackQty: i.Qty,
                BatteryCount: i.BatteryCount))
            .ToList();

    private static string BuildRuleSourceText(string ruleVersion, double vOpenMin, double rIntMax, bool isFromDb, string? notice) {
        var prefix = isFromDb
            ? $"판정 기준: DB grading_rules ({ruleVersion} · 무부하 ≥ {vOpenMin:0.00} V, 내부저항 ≤ {rIntMax:0.00} Ω)"
            : $"판정 기준: 임시 기본값 ({ruleVersion} · 무부하 ≥ {vOpenMin:0.00} V, 내부저항 ≤ {rIntMax:0.00} Ω)";
        return string.IsNullOrWhiteSpace(notice) ? prefix : $"{prefix} — {notice}";
    }

    private static CertificateData BuildFromDb(Order o, string certNo, CertificateDbSummary s) {
        var vOpenMin = s.RuleVOpenMin ?? FallbackAGradeVOpenMin;
        var rIntMax = s.RuleRIntMax ?? FallbackAGradeRIntMax;
        var ruleVersion = !string.IsNullOrWhiteSpace(s.RuleVersion) ? s.RuleVersion! : FallbackRuleVersion;

        if (s.Runs.Count == 0)
            return MakeEmpty(o, certNo, s);

        var runText = s.Runs.Count == 1
            ? s.Runs[0].SourceRunId
            : $"{s.Runs[0].SourceRunId} 외 {s.Runs.Count - 1}건";
        var inspectedAt = s.Runs.Max(r => r.StartedAt) ?? o.ReceivedAt;

        // 외관 (고객용: 신뢰도% 노출 제거, 관리자 내부 정보로 분리)
        string visualResult;
        bool visualOk;
        if (s.VisualInspected == 0) {
            visualResult = "미측정";
            visualOk = false;
        } else {
            var failPart = (s.VisualFail + s.VisualError) > 0 ? $" (불량·오류 {s.VisualFail + s.VisualError}건)" : "";
            visualResult = $"{s.VisualPass}/{s.VisualInspected} 통과{failPart}";
            visualOk = s.VisualFail == 0 && s.VisualError == 0;
        }

        // 극 방향 (고객용: "정상 N/N"으로 명확 표기)
        string polarityResult;
        bool polarityOk;
        if (s.VisualInspected == 0 && s.MeasuredCount == 0) {
            polarityResult = "미측정";
            polarityOk = false;
        } else {
            polarityOk = s.VisualPolarityUnknown == 0
                         && (s.MeasuredCount == 0 || s.ElecPolarityNormal == s.MeasuredCount);
            if (polarityOk) {
                var okCount = s.MeasuredCount > 0 ? s.ElecPolarityNormal : (s.VisualInspected - s.VisualPolarityUnknown);
                var totalCount = s.MeasuredCount > 0 ? s.MeasuredCount : s.VisualInspected;
                polarityResult = $"정상 {okCount}/{totalCount}";
            } else {
                var issues = new List<string>();
                if (s.MeasuredCount > 0 && s.ElecPolarityNormal < s.MeasuredCount)
                    issues.Add($"전기 정극성 {s.ElecPolarityNormal}/{s.MeasuredCount}");
                if (s.VisualPolarityUnknown > 0)
                    issues.Add($"방향 미확인 {s.VisualPolarityUnknown}건");
                polarityResult = $"확인 필요 ({string.Join(", ", issues)})";
            }
        }

        // 11/20 결정 ⑧: 연결 회차 A 평균값만 표시 (최소~최대 / 최대 표기 제거)
        var vOpenResult = s.VOpenAvg.HasValue ? $"{s.VOpenAvg.Value:0.000} V" : "미측정";
        var vOpenOk = s.VOpenAvg.HasValue && s.VOpenAvg.Value >= vOpenMin;

        var vLoadResult = s.VLoadAvg.HasValue ? $"{s.VLoadAvg.Value:0.000} V" : "미측정";

        var rIntResult = s.RIntAvg.HasValue ? $"{s.RIntAvg.Value:0.000} Ω" : "미측정";
        var rIntOk = s.RIntAvg.HasValue && s.RIntAvg.Value <= rIntMax;

        var qtyOk = s.ACount >= o.BatteryCount;
        var gradeText = s.ACount > 0
            ? $"A ({s.ACount:N0}개 / 주문 {o.BatteryCount:N0}개)"
            : "기록 없음";

        var rows = new List<CheckRow>
        {
            new("외관 (스크래치·스티커·훼손·부식)", "불량 없음", visualResult, visualOk),
            new("극 방향 (외관·전기)", "정극성 / 방향 정상", polarityResult, polarityOk),
            new("무부하 전압 (평균)", $"{vOpenMin:0.00} V 이상", vOpenResult, vOpenOk),
            new("부하 전압 (평균)", "참고 측정", vLoadResult, true, IsReference: true),
            new("내부저항 (평균)", $"{rIntMax:0.00} Ω 이하", rIntResult, rIntOk),
            new("등급 및 검사 수량", $"A ({o.BatteryCount:N0}개 이상)", gradeText, s.ACount > 0 && qtyOk),
        };

        var runTotal = new RunTotalSummary(
            RunCount: s.Runs.Count,
            TotalShipped: s.Runs.Sum(r => r.BatteryCount),
            TotalACount: s.ACount,
            VOpenText: s.VOpenAvg.HasValue ? $"{s.VOpenAvg.Value:0.000} V" : "-",
            VLoadText: s.VLoadAvg.HasValue ? $"{s.VLoadAvg.Value:0.000} V" : "-",
            RIntText: s.RIntAvg.HasValue ? $"{s.RIntAvg.Value:0.000} Ω" : "-",
            VisualPassText: s.ACount > 0 ? $"{s.VisualPass}/{s.ACount}" : "-");

        // 관리자 내부 참고 정보
        var camParts = new List<string>();
        if (s.VisualMinusUp > 0) camParts.Add($"음극 상향 {s.VisualMinusUp}개");
        if (s.VisualPlusUp > 0) camParts.Add($"양극 상향 {s.VisualPlusUp}개");
        if (s.VisualPolarityUnknown > 0) camParts.Add($"미확인 {s.VisualPolarityUnknown}개");
        var missingText = (s.ElectricalMissing > 0 || s.VisualMissing > 0)
            ? $"누락 건수(전기 {s.ElectricalMissing}개 / 외관 {s.VisualMissing}개)는 평균 계산에서 제외됨"
            : null;

        var adminInfo = new AdminDetailInfo(
            SourceLabel: $"라인 DB 실측 (LINE 회차 {s.Runs.Count}건 · 분류 완료 A등급 집계)",
            LinkedRunCount: s.Runs.Count,
            RuleSourceText: BuildRuleSourceText(ruleVersion, vOpenMin, rIntMax, s.IsRuleFromDb, s.RuleNotice),
            VisualConfidenceText: s.VisualConfidence.HasValue ? $"YOLO 외관 평균 신뢰도 {s.VisualConfidence.Value:P1}" : null,
            CameraPolarityText: camParts.Count > 0 ? $"카메라 극 방향 상세: {string.Join(" · ", camParts)}" : null,
            MissingText: missingText);

        return new CertificateData(
            CertNo: certNo,
            InspectedAt: inspectedAt,
            RunId: runText,
            RuleVersion: ruleVersion,
            Count: o.BatteryCount,
            TotalPacks: o.PackCount,
            LinkedACount: s.ACount,
            Products: BuildProductRows(o),
            Rows: rows,
            Runs: s.Runs,
            RunTotal: runTotal,
            AdminInfo: adminInfo,
            IsExample: false,
            HasRuns: true);
    }

    private static CertificateData MakeEmpty(Order o, string certNo, CertificateDbSummary? s = null) {
        var vOpenMin = s?.RuleVOpenMin ?? FallbackAGradeVOpenMin;
        var rIntMax = s?.RuleRIntMax ?? FallbackAGradeRIntMax;
        var ruleVersion = !string.IsNullOrWhiteSpace(s?.RuleVersion) ? s!.RuleVersion! : FallbackRuleVersion;
        var isFromDb = s?.IsRuleFromDb ?? false;

        var rows = new List<CheckRow>
        {
            new("외관 (스크래치·스티커·훼손·부식)", "불량 없음", "-", false),
            new("극 방향 (외관·전기)", "정극성 / 방향 정상", "-", false),
            new("무부하 전압 (평균)", $"{vOpenMin:0.00} V 이상", "-", false),
            new("부하 전압 (평균)", "참고 측정", "-", true, IsReference: true),
            new("내부저항 (평균)", $"{rIntMax:0.00} Ω 이하", "-", false),
            new("등급 및 검사 수량", $"A ({o.BatteryCount:N0}개 이상)", "-", false),
        };
        return new CertificateData(
            CertNo: certNo,
            InspectedAt: o.ReceivedAt,
            RunId: "연결 없음",
            RuleVersion: ruleVersion,
            Count: o.BatteryCount,
            TotalPacks: o.PackCount,
            LinkedACount: 0,
            Products: BuildProductRows(o),
            Rows: rows,
            Runs: Array.Empty<RunSummaryRow>(),
            RunTotal: null,
            AdminInfo: new AdminDetailInfo(
                SourceLabel: "연결된 LINE 회차 없음 (order_runs 미등록)",
                LinkedRunCount: 0,
                RuleSourceText: BuildRuleSourceText(ruleVersion, vOpenMin, rIntMax, isFromDb, s?.RuleNotice),
                VisualConfidenceText: null,
                CameraPolarityText: null,
                MissingText: null),
            IsExample: false,
            HasRuns: false);
    }

    private static CertificateData MakeExample(Order o, string certNo) {
        var rnd = new Random(o.No.GetHashCode());
        var vOpen = 1.580 + rnd.NextDouble() * 0.030;
        var vLoad = vOpen - 0.08 - rnd.NextDouble() * 0.02;
        var rInt = 0.17 + rnd.NextDouble() * 0.06;
        var at = o.ReceivedAt.Date.AddHours(9).AddMinutes(rnd.Next(0, 300));

        var rows = new List<CheckRow>
        {
            new("외관 (스크래치·스티커·훼손·부식)", "불량 없음", $"{o.BatteryCount}/{o.BatteryCount} 통과", true),
            new("극 방향 (외관·전기)", "정극성 / 방향 정상", $"정상 {o.BatteryCount}/{o.BatteryCount}", true),
            new("무부하 전압 (평균)", $"{FallbackAGradeVOpenMin:0.00} V 이상", $"{vOpen:0.000} V", vOpen >= FallbackAGradeVOpenMin),
            new("부하 전압 (평균)", "참고 측정", $"{vLoad:0.000} V", true, IsReference: true),
            new("내부저항 (평균)", $"{FallbackAGradeRIntMax:0.00} Ω 이하", $"{rInt:0.000} Ω", rInt <= FallbackAGradeRIntMax),
            new("등급 및 검사 수량", $"A ({o.BatteryCount:N0}개 이상)", $"A ({o.BatteryCount:N0}개 / 주문 {o.BatteryCount:N0}개)", true),
        };

        return new CertificateData(
            CertNo: certNo,
            InspectedAt: at,
            RunId: $"{at:yyyyMMdd-HHmmss}-{rnd.Next(0x1000, 0xFFFF):x4}",
            RuleVersion: $"{FallbackRuleVersion} (예시)",
            Count: o.BatteryCount,
            TotalPacks: o.PackCount,
            LinkedACount: o.BatteryCount,
            Products: BuildProductRows(o),
            Rows: rows,
            Runs: Array.Empty<RunSummaryRow>(),
            RunTotal: null,
            AdminInfo: new AdminDetailInfo(
                SourceLabel: "예시 데이터 (Fake 모드 또는 DB 미연결)",
                LinkedRunCount: 0,
                RuleSourceText: BuildRuleSourceText(FallbackRuleVersion, FallbackAGradeVOpenMin, FallbackAGradeRIntMax, isFromDb: false, notice: null),
                VisualConfidenceText: null,
                CameraPolarityText: null,
                MissingText: null),
            IsExample: true,
            HasRuns: true);
    }
}