namespace DepalAdmin.Core.Data;

/// <summary>DB 없이 화면을 만들 때 쓰는 가짜 이력. 같은 날짜는 항상 같은 값이 나온다.</summary>
public sealed class FakeHistoryRepository : IHistoryRepository
{
    private const int RunsPerDay = 3;
    private const int BatteriesPerRun = 8;    // 팔레트 한 판 2줄 × 4칸 (11/20 변경, 규약 v1.5.1)

    // 사유는 메시지_형식 v1.4의 고정 문장
    private static readonly string[] VoltageReasons = { "전압 낮음", "내부저항 큼", "전압 낮음, 내부저항 큼" };
    private static readonly string[] ResortReasons = { "+극이 위", "측정 극성 반대", "접촉 불량", "외관 검사 실패" };

    public async Task<IReadOnlyList<HistoryRow>> GetResultsAsync(DateTime from, DateTime to, string? grade, CancellationToken ct = default)
    {
        await Task.Delay(250, ct);   // DB를 기다리는 느낌
        return Generate(from, to)
            .Where(r => grade == null || r.Grade == grade)
            .OrderByDescending(r => r.FinishedAt)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetEventCountsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await Task.Delay(100, ct);
        var counts = new Dictionary<string, int> { ["INFO"] = 0, ["WARN"] = 0, ["ERROR"] = 0 };
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            var rnd = new Random(day.Year * 1000 + day.DayOfYear + 7);
            counts["INFO"] += 20 + rnd.Next(15);
            counts["WARN"] += rnd.Next(6);
            counts["ERROR"] += rnd.Next(3);
        }
        return counts;
    }

    private static IEnumerable<HistoryRow> Generate(DateTime from, DateTime to)
    {
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            var rnd = new Random(day.Year * 1000 + day.DayOfYear);
            for (var run = 0; run < RunsPerDay; run++)
            {
                var t = day.AddHours(9 + run * 2 + rnd.Next(2)).AddMinutes(rnd.Next(60));
                var runId = $"{t:yyyyMMdd-HHmmss}-{rnd.Next(0x10000):x4}";

                for (var i = 1; i <= BatteriesPerRun; i++)
                {
                    t = t.AddSeconds(18 + rnd.Next(10));
                    var row = new HistoryRow
                    {
                        RunId = runId,
                        BatteryId = i,
                        PalletSlot = i,
                        StartedAt = t.AddSeconds(-18),
                        FinishedAt = t,
                    };

                    var p = rnd.NextDouble();
                    if (p < 0.10)
                    {
                        (row.Grade, row.Dest, row.Reason) = (Grades.NgVisual, "NG_VISUAL_BOX", "외관 불량");
                    }
                    else if (p < 0.16)
                    {
                        (row.Grade, row.Dest, row.Reason) = (Grades.Resort, "RESORT_BOX", ResortReasons[rnd.Next(ResortReasons.Length)]);
                    }
                    else
                    {
                        var v = Math.Round(1.25 + rnd.NextDouble() * 0.40, 3);
                        var ri = Math.Round(0.10 + Math.Pow(rnd.NextDouble(), 2), 3);
                        row.VOpen = v;
                        row.RInt = ri;
                        (row.Grade, row.Dest) = v >= 1.50 && ri <= 0.9 ? (Grades.A, "AGV")
                                              : v >= 1.30 && ri <= 1.5 ? (Grades.B, "B_BOX")
                                              : (Grades.NgVoltage, "NG_VOLTAGE_BOX");
                        if (row.Grade == Grades.NgVoltage)
                            row.Reason = v < 1.30 && ri > 1.5 ? VoltageReasons[2] : v < 1.30 ? VoltageReasons[0] : VoltageReasons[1];
                    }
                    yield return row;
                }
            }
        }
    }
}
