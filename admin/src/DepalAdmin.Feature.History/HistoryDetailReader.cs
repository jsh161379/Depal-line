using System.Text.Json;
using DepalAdmin.Core;
using DepalAdmin.Core.Data;
using DepalAdmin.Core.Settings;
using MySqlConnector;

namespace DepalAdmin.Feature.History;

/// <summary>
/// 이력 조회 상세 패널용: 건전지 1개의 외관·전기 측정·판정 기준을 읽는다 (읽기 전용 계정 ui_reader).
/// Core의 IHistoryRepository/HistoryRow(공통)를 바꾸지 않으려고 이력 조회 안에 따로 둔다.
///
///   battery_cycles ─ runs(source_run_id)            표에서 고른 회차·번호로 1행
///                  ├ visual_inspections (cycle_id)  외관 판정·신뢰도·극 방향
///                  │   └ visual_defects             불량 종류·카메라·신뢰도 (여러 행)
///                  ├ electrical_tests (cycle_id)    측정 극성·전압·저항
///                  └ grading_rules (rule_id)        그 건전지에 쓴 판정 기준값 (JSON)
///
/// visual_defects·grading_rules는 읽기 권한이 없어도 나머지는 보이도록 따로 읽는다.
/// settings의 UseFakeDb = true면 표의 값으로 그럴듯한 가짜 상세를 만든다.
/// </summary>
public sealed class HistoryDetailReader
{
    private const string MainSql = @"
SELECT c.cycle_id, c.rule_id, c.started_at, c.graded_at, c.sorted_at, c.finished_at, c.electrical_status,
       v.visual_inspection_id, v.visual_result, v.confidence, v.polarity, v.model_version, v.inspected_at,
       e.polarity, e.no_load_voltage_v, e.loaded_voltage_v, e.load_resistance_ohm, e.estimated_internal_resistance_ohm, e.measured_at
FROM battery_cycles c
JOIN runs r ON r.run_id = c.run_id
LEFT JOIN visual_inspections v ON v.cycle_id = c.cycle_id
LEFT JOIN electrical_tests e ON e.cycle_id = c.cycle_id
WHERE r.source_run_id = @run AND c.source_battery_id = @battery
LIMIT 1;";

    private const string DefectsSql = @"
SELECT defect_type, confidence, camera
FROM visual_defects
WHERE visual_inspection_id = @id
ORDER BY confidence DESC;";

    private const string RuleSql = @"
SELECT rule_version, voltage_and_resistance_thresholds
FROM grading_rules
WHERE rule_id = @id;";

    private readonly AppSettings _settings;

    public HistoryDetailReader(AppSettings settings) => _settings = settings;

    public async Task<BatteryDetail> LoadAsync(HistoryRow row, CancellationToken ct = default)
    {
        if (_settings.UseFakeDb)
        {
            await Task.Delay(120, ct);   // DB를 기다리는 느낌
            return FakeDetail(row);
        }

        var d = FromRow(row);
        await using var conn = new MySqlConnection(_settings.MySqlConnection);
        await conn.OpenAsync(ct);

        long? visualId = null, ruleId = null;
        await using (var cmd = new MySqlCommand(MainSql, conn))
        {
            cmd.Parameters.AddWithValue("@run", row.RunId);
            cmd.Parameters.AddWithValue("@battery", row.BatteryId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return d;   // 못 찾음 → 표의 값만

            d.Found = true;
            ruleId = Long(rd, 1);
            d.PickedAt = Time(rd, 2);
            d.GradedAt = Time(rd, 3);
            d.SortedAt = Time(rd, 4);
            d.FinishedAt = Time(rd, 5) ?? d.FinishedAt;
            d.ElectricalStatus = Text(rd, 6);
            visualId = Long(rd, 7);
            d.VisualResult = Text(rd, 8);
            d.VisualConfidence = Number(rd, 9);
            d.VisualPolarity = Text(rd, 10);
            d.ModelVersion = Text(rd, 11);
            d.InspectedAt = Time(rd, 12);
            d.MeasuredPolarity = Text(rd, 13);
            d.VOpen = Number(rd, 14);
            d.VLoad = Number(rd, 15);
            d.RLoad = Number(rd, 16);
            d.RInt = Number(rd, 17);
            d.MeasuredAt = Time(rd, 18);
        }

        if (visualId is long vid)
        {
            try
            {
                await using var cmd = new MySqlCommand(DefectsSql, conn);
                cmd.Parameters.AddWithValue("@id", vid);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    d.Defects.Add(new DefectItem(Text(rd, 0) ?? "-", Number(rd, 1), Text(rd, 2)));
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.TableAccessDenied)
            {
                d.DefectNote = "불량 목록을 읽을 권한이 없습니다 (ui_reader에 visual_defects SELECT 필요).";
            }
        }

        if (ruleId is long rid)
        {
            try
            {
                await using var cmd = new MySqlCommand(RuleSql, conn);
                cmd.Parameters.AddWithValue("@id", rid);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    d.RuleVersion = Text(rd, 0);
                    ReadThresholds(d, Text(rd, 1));
                }
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.TableAccessDenied)
            {
                d.RuleNote = "판정 기준을 읽을 권한이 없습니다 (ui_reader에 grading_rules SELECT 필요).";
            }
        }
        return d;
    }

    /// <summary>server/rules thresholds JSON: {"A":{"v_open_min":1.5,"r_int_max":1.2},"B":{…},"order":"v_open_first"}</summary>
    private static void ReadThresholds(BatteryDetail d, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            d.RuleNote = "기준값이 아직 저장되지 않았습니다 (결과가 server/rules보다 먼저 온 경우).";
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            RuleGrade? Grade(string key) =>
                root.TryGetProperty(key, out var g)
                && g.TryGetProperty("v_open_min", out var v) && v.ValueKind == JsonValueKind.Number
                && g.TryGetProperty("r_int_max", out var r) && r.ValueKind == JsonValueKind.Number
                    ? new RuleGrade(v.GetDouble(), r.GetDouble())
                    : null;
            d.RuleA = Grade("A");
            d.RuleB = Grade("B");
            d.RuleOrder = root.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
        }
        catch (JsonException)
        {
            d.RuleNote = "기준값 형식을 읽지 못했습니다.";
        }
    }

    private static BatteryDetail FromRow(HistoryRow row) => new()
    {
        RunId = row.RunId,
        BatteryId = row.BatteryId,
        PalletSlot = row.PalletSlot,
        Grade = row.Grade,
        Reason = row.Reason,
        Dest = row.Dest,
        PickedAt = row.StartedAt,
        FinishedAt = row.FinishedAt,
        VOpen = row.VOpen,
        RInt = row.RInt,
    };

    private static string? Text(MySqlDataReader rd, int i) => rd.IsDBNull(i) ? null : Convert.ToString(rd.GetValue(i));
    private static double? Number(MySqlDataReader rd, int i) => rd.IsDBNull(i) ? null : Convert.ToDouble(rd.GetValue(i));
    private static long? Long(MySqlDataReader rd, int i) => rd.IsDBNull(i) ? null : Convert.ToInt64(rd.GetValue(i));
    private static DateTime? Time(MySqlDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetDateTime(i);

    // ───────────────────────── 가짜 상세 (UseFakeDb) ─────────────────────────

    /// <summary>
    /// 표의 등급·사유·전압에 맞춰 메시지_형식 3장 "경우별 값"대로 만든다. 같은 건전지는 항상 같은 값.
    /// 가짜 이력(FakeHistoryRepository)의 판정 기준(A 0.9Ω·B 1.5Ω)에 맞춰 temp-2026-11-10 기준을 보여 준다.
    /// </summary>
    private static BatteryDetail FakeDetail(HistoryRow row)
    {
        var d = FromRow(row);
        d.Found = true;
        var seed = row.BatteryId * 7919;
        foreach (var ch in row.RunId) seed = seed * 31 + ch;
        var rnd = new Random(seed);

        d.ModelVersion = "yolo11n-side-v2";
        d.InspectedAt = row.StartedAt?.AddSeconds(6);
        d.RuleVersion = "temp-2026-11-10";
        d.RuleA = new RuleGrade(1.50, 0.9);
        d.RuleB = new RuleGrade(1.30, 1.5);
        d.RuleOrder = "v_open_first";

        // 외관
        switch (row.Grade, row.Reason)
        {
            case (Grades.Resort, "외관 검사 실패"):
                (d.VisualResult, d.VisualConfidence, d.VisualPolarity) = ("ERROR", null, null);
                break;
            case (Grades.NgVisual, _):
                (d.VisualResult, d.VisualConfidence, d.VisualPolarity) = ("FAIL", 0.80 + rnd.NextDouble() * 0.18, "minus_up");
                string[] types = { "scratch", "dent", "sticker" };
                string[] cams = { "front", "back", "top" };
                var n = rnd.NextDouble() < 0.3 ? 2 : 1;
                for (var i = 0; i < n; i++)
                    d.Defects.Add(new DefectItem(types[rnd.Next(types.Length)], Math.Round(0.70 + rnd.NextDouble() * 0.28, 2), cams[rnd.Next(2)]));
                break;
            case (Grades.Resort, "+극이 위"):
                (d.VisualResult, d.VisualConfidence, d.VisualPolarity) = ("PASS", 0.88 + rnd.NextDouble() * 0.11, "plus_up");
                break;
            default:
                (d.VisualResult, d.VisualConfidence, d.VisualPolarity) = ("PASS", 0.88 + rnd.NextDouble() * 0.11, "minus_up");
                break;
        }
        d.VisualConfidence = d.VisualConfidence is double c ? Math.Round(c, 2) : null;

        // 전기 측정
        if (row.VOpen is double v && row.RInt is double ri)
        {
            const double rLoad = 3.31;
            d.ElectricalStatus = "COMPLETED";
            d.MeasuredPolarity = "normal";
            d.RLoad = rLoad;
            d.VLoad = Math.Round(v * rLoad / (rLoad + ri), 3);
            d.MeasuredAt = row.StartedAt?.AddSeconds(12);
        }
        else
        {
            switch (row.Reason)
            {
                case "측정 극성 반대":
                    (d.ElectricalStatus, d.MeasuredPolarity) = ("COMPLETED", "reversed");
                    d.MeasuredAt = row.StartedAt?.AddSeconds(12);
                    break;
                case "접촉 불량":
                    (d.ElectricalStatus, d.MeasuredPolarity, d.VOpen) = ("COMPLETED", "abnormal", 0.02);
                    d.MeasuredAt = row.StartedAt?.AddSeconds(12);
                    break;
                case "측정 실패":
                    d.ElectricalStatus = "FAILED";
                    break;
                default:
                    d.ElectricalStatus = "SKIPPED";
                    break;
            }
        }
        d.GradedAt = row.FinishedAt?.AddSeconds(-4);
        d.SortedAt = row.Dest == null ? null : row.FinishedAt?.AddSeconds(-0.5);
        return d;
    }
}
