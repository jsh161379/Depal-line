using System.Globalization;

namespace BareunOrder.Feature.Stats;

/// <summary>보고서 기간 종류</summary>
public enum PeriodKind
{
    Day,     // 일일: 고른 날짜 하루
    Week,    // 주간: 고른 날짜가 들어 있는 주 (월~일)
    Month,   // 월간: 고른 날짜가 들어 있는 달 (1일~말일)
    Custom,  // 직접: 시작일~종료일을 직접 고름
}

/// <summary>화면 위쪽 [일일 / 주간 / 월간] 버튼 하나</summary>
public sealed record PeriodOption(string Label, PeriodKind Kind);

/// <summary>
/// 기간 계산 (일·주·월 시작일/끝일). 화면·엑셀·보고서가 모두 이 규칙을 쓴다.
/// ⚠ 현장 관리자 프로그램 보고서와 규칙을 같게 맞출 것:
///   - 주간 = 월요일 ~ 일요일 (ISO 방식)
///   - 월간 = 1일 ~ 그 달 말일
///   - 끝일은 그날 하루를 포함한다 (DB 조회는 "시작일 &lt;= 시각 &lt; 끝일 다음 날")
/// 이 파일은 WPF를 쓰지 않으므로 다른 탭·프로그램에서 그대로 가져다 쓸 수 있다.
/// </summary>
public static class StatsPeriod
{
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>기준 날짜가 들어 있는 기간 (시작일, 끝일 — 둘 다 포함)</summary>
    public static (DateTime Start, DateTime End) For(PeriodKind kind, DateTime anchor)
    {
        var d = anchor.Date;
        switch (kind)
        {
            case PeriodKind.Week:
                var monday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));   // 월=0 … 일=6
                return (monday, monday.AddDays(6));
            case PeriodKind.Month:
                var first = new DateTime(d.Year, d.Month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            default:
                return (d, d);
        }
    }

    /// <summary>앞(-1)·뒤(+1) 기간. 직접 고른 기간은 같은 길이만큼 옮긴다.</summary>
    public static (DateTime Start, DateTime End) Shift(PeriodKind kind, DateTime start, DateTime end, int direction)
    {
        switch (kind)
        {
            case PeriodKind.Day:
                return For(kind, start.AddDays(direction));
            case PeriodKind.Week:
                return For(kind, start.AddDays(7 * direction));
            case PeriodKind.Month:
                return For(kind, start.AddMonths(direction));
            default:
                var days = (end.Date - start.Date).Days + 1;
                return (start.Date.AddDays(days * direction), end.Date.AddDays(days * direction));
        }
    }

    /// <summary>비교할 이전 기간 (전일 / 전주 / 지난달 / 바로 앞 같은 길이)</summary>
    public static (DateTime Start, DateTime End) Previous(PeriodKind kind, DateTime start, DateTime end) =>
        Shift(kind, start, end, -1);

    /// <summary>"전일", "전주", "지난달", "이전 기간"</summary>
    public static string PreviousName(PeriodKind kind) => kind switch
    {
        PeriodKind.Day => "전일",
        PeriodKind.Week => "전주",
        PeriodKind.Month => "지난달",
        _ => "이전 기간",
    };

    /// <summary>"일일", "주간", "월간", "기간 지정"</summary>
    public static string KindName(PeriodKind kind) => kind switch
    {
        PeriodKind.Day => "일일",
        PeriodKind.Week => "주간",
        PeriodKind.Month => "월간",
        _ => "기간 지정",
    };

    /// <summary>"2026-11-16(월) ~ 2026-11-22(일) · 7일"</summary>
    public static string RangeText(DateTime start, DateTime end)
    {
        var days = (end.Date - start.Date).Days + 1;
        return start.Date == end.Date
            ? DayText(start)
            : $"{DayText(start)} ~ {DayText(end)} · {days}일";
    }

    /// <summary>"2026-11-19(목)"</summary>
    public static string DayText(DateTime d) => $"{d:yyyy-MM-dd}({Weekday(d)})";

    /// <summary>"목"</summary>
    public static string Weekday(DateTime d) => d.ToString("ddd", Ko);
}
