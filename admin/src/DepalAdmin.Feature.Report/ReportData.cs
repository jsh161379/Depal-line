using DepalAdmin.Core;
using DepalAdmin.Core.Data;
using DepalAdmin.Core.Handover;

namespace DepalAdmin.Feature.Report;

/// <summary>
/// 보고서 한 장에 들어갈 숫자를 모은다 (화면·인쇄 둘 다 이걸로 그림).
/// 항목 (11/18 확정, 11/19 월간 추가): 요약 · 지난달 비교(월간) · 측정값 · 불량·재분류 사유 · 일별(주간·월간) ·
///   시간대별 · 회차별 · 배출 실패 목록 · 장치·라인 이벤트 · 인수인계 메모 · 확인란
/// 모두 IHistoryRepository의 결과 행·이벤트 수와 인수인계 메모만으로 계산한다 (DB 조회 추가 없음).
/// </summary>
public sealed class ReportData
{
    public required string Title { get; init; }
    public required string Station { get; init; }
    public required string Period { get; init; }
    public required string MadeBy { get; init; }
    public DateTime MadeAt { get; init; } = DateTime.Now;
    /// <summary>"일일" / "주간" / "월간" / "회차"</summary>
    public required string Kind { get; init; }
    public bool IsDaily => Kind == "일일";
    public bool IsWeekly => Kind == "주간";
    public bool IsMonthly => Kind == "월간";
    public bool IsPerRun => Kind == "회차";

    /// <summary>보고서 기간 (To는 포함 안 함, ReportPeriod 규칙)</summary>
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    /// <summary>월간 보고서의 지난달 결과 (비교 줄용). 월간이 아니면 null</summary>
    public IReadOnlyList<HistoryRow>? PrevRows { get; init; }

    public required IReadOnlyList<HistoryRow> Rows { get; init; }
    public required IReadOnlyDictionary<string, int> EventCounts { get; init; }
    public required IReadOnlyList<HandoverNote> Notes { get; init; }

    public int Total => Rows.Count;
    public int Count(string grade) => Rows.Count(r => r.Grade == grade);
    public string Ratio(string grade) => Total == 0 ? "-" : $"{Count(grade) * 100.0 / Total:0.0}%";

    /// <summary>등급은 정해졌는데 분류함에 못 넣은 건 (sorted_destination = NULL). DB 문서: 등급 수와 실제 배출 수는 따로 본다</summary>
    public int SortFailed => Rows.Count(r => r.Dest == null);

    public string AvgCycle
    {
        get
        {
            var s = Rows.Where(r => r.StartedAt != null && r.FinishedAt != null)
                        .Select(r => (r.FinishedAt!.Value - r.StartedAt!.Value).TotalSeconds).ToList();
            return s.Count == 0 ? "-" : $"{s.Average():0.0}초";
        }
    }

    /// <summary>불량·재분류 사유 많은 순 5개 (B는 정상 등급이라 뺌)</summary>
    public IReadOnlyList<(string Reason, string Grade, int Count)> TopReasons =>
        Rows.Where(r => !string.IsNullOrWhiteSpace(r.Reason)
                        && (Grades.IsNg(r.Grade) || r.Grade == Grades.Resort))
            .GroupBy(r => r.Reason!)
            .Select(g => (g.Key, g.First().Grade, g.Count()))
            .OrderByDescending(x => x.Item3)
            .Take(5)
            .ToList();

    /// <summary>보고서 칸에 쓸 등급: 5종 + 기간에 이전 데이터(NG)가 있으면 NG도</summary>
    public IReadOnlyList<string> GradeColumns =>
        Rows.Any(r => r.Grade == Grades.Ng) ? Grades.All.Append(Grades.Ng).ToList() : (IReadOnlyList<string>)Grades.All;

    /// <summary>회차별: 등급 개수는 GradeColumns 순서</summary>
    public IReadOnlyList<(string RunId, DateTime? Start, int Count, int[] ByGrade)> Runs =>
        Rows.GroupBy(r => r.RunId)
            .Select(g => (g.Key, g.Min(r => r.StartedAt ?? r.FinishedAt), g.Count(),
                          GradeColumns.Select(grade => g.Count(r => r.Grade == grade)).ToArray()))
            .OrderBy(x => x.Item2)
            .ToList();

    public int Events(string level) => EventCounts.TryGetValue(level, out var n) ? n : 0;

    // ── 가동 시간 ──

    public DateTime? FirstAt => Rows.Min(r => r.StartedAt ?? r.FinishedAt);
    public DateTime? LastAt => Rows.Max(r => r.FinishedAt ?? r.StartedAt);

    // ── 측정값 (측정 자리까지 간 건전지만. 외관 불량·거꾸로는 값이 없음) ──

    public IReadOnlyList<double> VOpens => Rows.Where(r => r.VOpen != null).Select(r => r.VOpen!.Value).ToList();
    public IReadOnlyList<double> RInts => Rows.Where(r => r.RInt != null).Select(r => r.RInt!.Value).ToList();

    public static string Fmt(IReadOnlyList<double> v, Func<IReadOnlyList<double>, double> f, string fmt) =>
        v.Count == 0 ? "-" : f(v).ToString(fmt);

    // ── 시간대별 (처리한 시간만) ──

    public IReadOnlyList<(int Hour, int Count, int A, int Ng, int Resort)> ByHour =>
        Rows.Where(r => (r.FinishedAt ?? r.StartedAt) != null)
            .GroupBy(r => (r.FinishedAt ?? r.StartedAt)!.Value.Hour)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count(), g.Count(r => r.Grade == Grades.A),
                          g.Count(r => Grades.IsNg(r.Grade)), g.Count(r => r.Grade == Grades.Resort)))
            .ToList();

    // ── 일별 (주간 7줄 · 월간 28~31줄, 처리 없는 날도 0으로) ──

    public IReadOnlyList<(DateTime Day, int Count, int[] ByGrade)> ByDay
    {
        get
        {
            var byDay = Rows.Where(r => (r.FinishedAt ?? r.StartedAt) != null)
                            .GroupBy(r => (r.FinishedAt ?? r.StartedAt)!.Value.Date)
                            .ToDictionary(g => g.Key, g => g.ToList());
            var list = new List<(DateTime, int, int[])>();
            for (var day = From.Date; day < To; day = day.AddDays(1))
            {
                var g = byDay.TryGetValue(day, out var x) ? x : new List<HistoryRow>();
                list.Add((day, g.Count, GradeColumns.Select(grade => g.Count(r => r.Grade == grade)).ToArray()));
            }
            return list;
        }
    }

    // ── 지난달 비교 (월간) ──

    /// <summary>불량률 = 외관 불량 + 전압 불량(+이전 NG) / 처리 수. 재분류는 불량이 아니라 따로</summary>
    public static (int Total, int A, int Ng, int Resort) Summary(IReadOnlyList<HistoryRow> rows) =>
        (rows.Count, rows.Count(r => r.Grade == Grades.A), rows.Count(r => Grades.IsNg(r.Grade)),
         rows.Count(r => r.Grade == Grades.Resort));

    // ── 회차별 소요 시간 ──

    public string RunDuration(string runId)
    {
        var g = Rows.Where(r => r.RunId == runId).ToList();
        var a = g.Min(r => r.StartedAt ?? r.FinishedAt);
        var b = g.Max(r => r.FinishedAt ?? r.StartedAt);
        if (a == null || b == null) return "-";
        var t = b.Value - a.Value;
        return t.TotalMinutes < 1 ? $"{t.TotalSeconds:0}초" : $"{(int)t.TotalMinutes}분 {t.Seconds}초";
    }

    // ── 배출 실패 (등급은 정했지만 분류함에 못 넣음) ──

    public IReadOnlyList<HistoryRow> SortFailedRows =>
        Rows.Where(r => r.Dest == null).OrderBy(r => r.FinishedAt ?? r.StartedAt).ToList();
}
