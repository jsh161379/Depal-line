namespace DepalAdmin.Feature.Report;

/// <summary>
/// 보고서 기간 계산 — 주문 관리 프로그램 보고서와 같은 규칙 (팀 제안 11/19).
///   일일: 고른 날짜 00:00 ~ 다음 날 00:00
///   주간: 고른 날짜가 들어 있는 주의 월요일 00:00 ~ 다음 주 월요일 00:00 (월~일)
///   월간: 고른 날짜가 들어 있는 달 1일 00:00 ~ 다음 달 1일 00:00 (1일~말일)
/// To는 포함하지 않는다 (DB 조회의 "from 이상 to 미만"과 같음). 화면에 끝 날짜를 쓸 때는 To.AddDays(-1).
/// </summary>
public static class ReportPeriod
{
    public static (DateTime From, DateTime To) Day(DateTime date) => (date.Date, date.Date.AddDays(1));

    public static (DateTime From, DateTime To) Week(DateTime date)
    {
        // DayOfWeek: 일=0, 월=1 … 토=6 → 월요일까지 거슬러 갈 일수: 월 0, 화 1 … 일 6
        var monday = date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return (monday, monday.AddDays(7));
    }

    public static (DateTime From, DateTime To) Month(DateTime date)
    {
        var first = new DateTime(date.Year, date.Month, 1);
        return (first, first.AddMonths(1));
    }
}
