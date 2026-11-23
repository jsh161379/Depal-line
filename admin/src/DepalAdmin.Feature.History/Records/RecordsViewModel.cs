using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepalAdmin.Core;
using DepalAdmin.Core.Auth;
using DepalAdmin.Core.Data;
using DepalAdmin.Core.Export;

namespace DepalAdmin.Feature.History.Records;

/// <summary>
/// 지난 결과 조회. 두 가지로 본다:
///   건전지별 — 한 줄에 건전지 하나
///   회차별   — 한 줄에 회차(팔레트 한 판) 하나, 등급별 개수와 걸린 시간
/// 건전지별 표에서 한 줄을 고르면 오른쪽에 상세(외관 검사·불량 목록·전기 측정·판정 기준·시각)가 나온다.
/// </summary>
public partial class RecordsViewModel : ObservableObject
{
    private const string All = "전체";
    private const int PalletCols = 3;   // 팔레트 2줄 × 3칸 (메시지_형식 v1.4.5)

    private readonly IHistoryRepository _repo;
    private readonly HistoryDetailReader _detailReader;
    private readonly Session _session;
    private CancellationTokenSource? _detailCts;

    public ObservableCollection<HistoryRow> Rows { get; } = new();
    public ObservableCollection<RunSummary> Runs { get; } = new();
    public string[] Grades { get; } = new[] { All }.Concat(DepalAdmin.Core.Grades.All).ToArray();

    [ObservableProperty] private DateTime _from = DateTime.Today;
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty] private string _grade = All;
    [ObservableProperty] private string _runFilter = "";
    [ObservableProperty] private string _status = "날짜를 고르고 [조회]를 누르세요.";
    [ObservableProperty] private int _tabIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _canExport;

    private List<HistoryRow> _loaded = new();

    // ── 상세 패널 ──
    public ObservableCollection<DetailSection> DetailSections { get; } = new();
    [ObservableProperty] private HistoryRow? _selectedRow;
    [ObservableProperty] private bool _hasDetail;
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailGrade = "";
    [ObservableProperty] private string _detailGradeText = "";
    [ObservableProperty] private string _detailStatus = "표에서 건전지를 고르면 자세한 검사 기록이 나옵니다.";

    public RecordsViewModel(IHistoryRepository repo, HistoryDetailReader detailReader, Session session)
    {
        _repo = repo;
        _detailReader = detailReader;
        _session = session;
        _session.Changed += (_, _) => CanExport = _session.Can(Permission.ExportData);
        CanExport = _session.Can(Permission.ExportData);
    }

    partial void OnRunFilterChanged(string value) => Fill();

    [RelayCommand]
    private void Today() => (From, To) = (DateTime.Today, DateTime.Today);

    [RelayCommand]
    private void LastWeek() => (From, To) = (DateTime.Today.AddDays(-6), DateTime.Today);

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (To < From)
        {
            Status = "끝 날짜가 시작 날짜보다 빠릅니다.";
            return;
        }

        Status = "조회 중…";
        try
        {
            _loaded = (await _repo.GetResultsAsync(From.Date, To.Date.AddDays(1), Grade == All ? null : Grade)).ToList();
            Fill();
        }
        catch (Exception ex)
        {
            Status = $"조회하지 못했습니다: {ex.Message}  (settings.json의 MySQL 접속 정보를 확인하세요)";
        }
    }

    private void Fill()
    {
        var rows = string.IsNullOrWhiteSpace(RunFilter)
            ? _loaded
            : _loaded.Where(r => r.RunId.Contains(RunFilter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);

        Runs.Clear();
        foreach (var run in rows.GroupBy(r => r.RunId)
                                .Select(g => new RunSummary(g.Key, g.ToList()))
                                .OrderByDescending(s => s.Start))
        {
            Runs.Add(run);
        }

        Status = $"건전지 {Rows.Count}개, 회차 {Runs.Count}개";
    }

    // ───────────────────────── 상세 패널 ─────────────────────────

    partial void OnSelectedRowChanged(HistoryRow? value) => _ = LoadDetailAsync(value);

    private async Task LoadDetailAsync(HistoryRow? row)
    {
        _detailCts?.Cancel();
        DetailSections.Clear();
        if (row == null)
        {
            HasDetail = false;
            DetailStatus = "표에서 건전지를 고르면 자세한 검사 기록이 나옵니다.";
            return;
        }

        var cts = _detailCts = new CancellationTokenSource();
        HasDetail = true;
        DetailTitle = $"{row.RunId}  ·  {row.BatteryId}번";
        DetailGrade = row.Grade;
        DetailGradeText = row.GradeText;
        DetailStatus = "불러오는 중…";
        try
        {
            var d = await _detailReader.LoadAsync(row, cts.Token);
            if (cts.IsCancellationRequested) return;
            Show(d);
            DetailStatus = d.Found ? "" : "DB에서 이 건전지의 자세한 기록을 찾지 못했습니다. 표에 있는 값만 보여 줍니다.";
        }
        catch (OperationCanceledException)
        {
            // 다른 줄을 골랐음
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) DetailStatus = $"자세한 기록을 읽지 못했습니다: {ex.Message}";
        }
    }

    private void Show(BatteryDetail d)
    {
        DetailSections.Clear();

        // 건전지
        var battery = new DetailSection("건전지");
        battery.Add("회차", d.RunId);
        battery.Add("번호", $"{d.BatteryId}번");
        battery.Add("팔레트 칸", SlotText(d.PalletSlot));
        battery.Add("등급", Labels.Grade(d.Grade), strong: true);
        battery.Add("사유", string.IsNullOrWhiteSpace(d.Reason) ? "-" : d.Reason);
        battery.Add("배출 위치", Labels.Dest(d.Dest), strong: d.Dest == null);
        DetailSections.Add(battery);

        // 외관 검사 (YOLO)
        var vision = new DetailSection("외관 검사 (YOLO)");
        vision.Add("판정", d.VisualResult switch
        {
            "PASS" => "정상",
            "FAIL" => "불량",
            "ERROR" => "검사 실패 (카메라·모델 오류 또는 결과 없음)",
            null => "기록 없음",
            _ => d.VisualResult,
        }, strong: d.VisualResult is "FAIL" or "ERROR");
        vision.Add("신뢰도", d.VisualConfidence is double c ? $"{c * 100:0.0}%" : "-");
        vision.Add("극 방향 (카메라)", Labels.VisionPolarity(d.VisualPolarity), strong: d.VisualPolarity == "plus_up");
        vision.AddIf("모델", d.ModelVersion);
        vision.Defects = d.Defects;
        vision.DefectNote = d.DefectNote ?? (d.VisualResult == "FAIL" && d.Defects.Count == 0 ? "불량 종류 기록 없음" : null);
        DetailSections.Add(vision);

        // 전기 측정
        var elec = new DetailSection("전기 측정");
        elec.Add("측정", d.ElectricalStatus switch
        {
            "COMPLETED" => "완료",
            "SKIPPED" => "생략 (외관 불량·재분류는 측정 안 함)",
            "FAILED" => "측정 실패",
            "PENDING" => "대기",
            null => "-",
            _ => d.ElectricalStatus,
        });
        if (d.ElectricalStatus is "COMPLETED" or null || d.VOpen != null)
        {
            elec.Add("측정 극성", Labels.Polarity(d.MeasuredPolarity), strong: d.MeasuredPolarity is "reversed" or "abnormal");
            elec.Add("무부하 전압", Volt(d.VOpen));
            elec.Add("부하 전압", Volt(d.VLoad));
            elec.Add("부하 저항", Ohm(d.RLoad));
            elec.Add("내부저항", Ohm(d.RInt));
        }
        DetailSections.Add(elec);

        // 판정 기준
        var rule = new DetailSection("판정 기준");
        rule.Add("기준 버전", d.RuleVersion ?? "-");
        if (d.RuleA is { } ra) rule.Add("A", $"무부하 ≥ {ra.VOpenMin:0.00} V, 내부저항 ≤ {ra.RIntMax:0.0##} Ω");
        if (d.RuleB is { } rb) rule.Add("B", $"무부하 ≥ {rb.VOpenMin:0.00} V, 내부저항 ≤ {rb.RIntMax:0.0##} Ω");
        if (d.RuleOrder == "v_open_first") rule.Add("판정 순서", "무부하 전압 먼저 → 내부저항");
        if (Basis(d) is { } basis) rule.Add("이 건전지", basis, strong: true);
        rule.Note = d.RuleNote;
        DetailSections.Add(rule);

        // 시각
        var time = new DetailSection("시각");
        time.AddIf("집기 시작", Clock(d.PickedAt));
        time.AddIf("외관 검사", Clock(d.InspectedAt));
        time.AddIf("전압 측정", Clock(d.MeasuredAt));
        time.AddIf("판정", Clock(d.GradedAt));
        time.AddIf("배출", Clock(d.SortedAt));
        time.AddIf("결과 확정", Clock(d.FinishedAt));
        if (d.PickedAt is DateTime p && d.FinishedAt is DateTime f && f >= p)
            time.Add("걸린 시간", $"{(f - p).TotalSeconds:0.0}초");
        DetailSections.Add(time);
    }

    /// <summary>
    /// 메시지_형식 3장 판정 순서 5~8 (무부하 전압 먼저)로 이 건전지가 왜 그 등급인지 한 줄로.
    /// 측정값과 기준값이 다 있을 때만.
    /// </summary>
    private static string? Basis(BatteryDetail d)
    {
        if (d.VOpen is not double v || d.RInt is not double r || d.RuleA is not { } a || d.RuleB is not { } b) return null;
        if (v < b.VOpenMin) return $"무부하 {v:0.000} V < {b.VOpenMin:0.00} V → 전압 낮음";
        if (v >= a.VOpenMin && r <= a.RIntMax) return $"무부하 {v:0.000} V ≥ {a.VOpenMin:0.00} V, 내부저항 {r:0.000} Ω ≤ {a.RIntMax:0.0##} Ω → A";
        if (r <= b.RIntMax)
        {
            var why = v < a.VOpenMin ? $"무부하 {v:0.000} V < {a.VOpenMin:0.00} V" : $"내부저항 {r:0.000} Ω > {a.RIntMax:0.0##} Ω";
            return $"A 미달 ({why}), 내부저항 ≤ {b.RIntMax:0.0##} Ω → B";
        }
        return $"내부저항 {r:0.000} Ω > {b.RIntMax:0.0##} Ω → 내부저항 큼";
    }

    private static string SlotText(int? slot) => slot is int s && s > 0
        ? $"{s}번 ({(s - 1) / PalletCols + 1}줄 {(s - 1) % PalletCols + 1}칸)"
        : "-";

    private static string Volt(double? v) => v is double x ? $"{x:0.000} V" : "-";
    private static string Ohm(double? v) => v is double x ? $"{x:0.000} Ω" : "-";
    private static string? Clock(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm:ss.fff");

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        string? path;
        if (TabIndex == 0)
        {
            path = CsvExport.Save($"이력_{From:yyyyMMdd}-{To:yyyyMMdd}.csv", Rows,
                ("끝난 시각", r => r.FinishedAt),
                ("회차", r => r.RunId),
                ("번호", r => r.BatteryId),
                ("칸", r => r.PalletSlot),
                ("등급", r => r.GradeText),
                ("무부하 전압(V)", r => r.VOpen),
                ("내부저항(Ω)", r => r.RInt),
                ("배출 위치", r => r.DestText),
                ("사유", r => r.Reason));
        }
        else
        {
            path = CsvExport.Save($"회차_{From:yyyyMMdd}-{To:yyyyMMdd}.csv", Runs,
                ("회차", s => s.RunId),
                ("시작", s => s.Start),
                ("끝", s => s.End),
                ("걸린 시간", s => s.DurationText),
                ("개수", s => s.Count),
                ("A", s => s.A),
                ("B", s => s.B),
                ("불량", s => s.Ng),
                ("재분류", s => s.Resort),
                ("A 비율", s => s.ARatio));
        }
        if (path != null) Status = $"저장함: {path}";
    }
}

public sealed class RunSummary
{
    public RunSummary(string runId, IReadOnlyList<HistoryRow> rows)
    {
        RunId = runId;
        Start = rows.Min(r => r.StartedAt ?? r.FinishedAt);
        End = rows.Max(r => r.FinishedAt);
        Count = rows.Count;
        A = rows.Count(r => r.Grade == "A");
        B = rows.Count(r => r.Grade == "B");
        Ng = rows.Count(r => DepalAdmin.Core.Grades.IsNg(r.Grade));
        Resort = rows.Count(r => r.Grade == "RESORT");
    }

    public string RunId { get; }
    public DateTime? Start { get; }
    public DateTime? End { get; }
    public int Count { get; }
    public int A { get; }
    public int B { get; }
    public int Ng { get; }
    public int Resort { get; }

    public string ARatio => Count == 0 ? "-" : $"{A * 100.0 / Count:0}%";

    public string DurationText => Start is DateTime s && End is DateTime e ? (e - s).ToString(@"mm\:ss") : "-";
}

/// <summary>상세 패널의 한 묶음 (제목 + 이름·값 줄들 + 외관 불량 목록)</summary>
public sealed class DetailSection
{
    public DetailSection(string title) => Title = title;

    public string Title { get; }
    public List<DetailItem> Items { get; } = new();
    public IReadOnlyList<DefectItem> Defects { get; set; } = Array.Empty<DefectItem>();
    public string? DefectNote { get; set; }
    public string? Note { get; set; }

    public bool HasDefects => Defects.Count > 0;

    public void Add(string label, string value, bool strong = false) => Items.Add(new DetailItem(label, value, strong));

    /// <summary>값이 없으면 줄을 넣지 않는다 (DB에 아직 채워지지 않는 칸)</summary>
    public void AddIf(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Add(label, value);
    }
}

public sealed record DetailItem(string Label, string Value, bool Strong)
{
    public FontWeight Weight => Strong ? FontWeights.SemiBold : FontWeights.Normal;
}
