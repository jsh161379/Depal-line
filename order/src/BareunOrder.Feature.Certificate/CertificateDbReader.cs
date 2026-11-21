using System.Globalization;
using System.Text.Json;
using MySqlConnector;

namespace BareunOrder.Feature.Certificate;

/// <summary>검사 성적서 DB 조회 결과 (주문 통신 규약 v1.0.5 · grading_rules 연동)</summary>
public sealed record CertificateDbSummary(
    string? DbCertNo,
    int ACount,
    int MeasuredCount,
    double? VOpenAvg,
    double? VLoadAvg,
    double? RIntAvg,
    int ElecPolarityNormal,
    int VisualInspected,
    int VisualPass,
    int VisualFail,
    int VisualError,
    double? VisualConfidence,
    int VisualMinusUp,
    int VisualPlusUp,
    int VisualPolarityUnknown,
    int ElectricalMissing,
    int VisualMissing,
    string? RuleVersion,
    double? RuleVOpenMin,
    double? RuleRIntMax,
    bool IsRuleFromDb,
    string? RuleNotice,
    IReadOnlyList<RunSummaryRow> Runs);

public sealed record RunSummaryRow(
    string SourceRunId,
    int BatteryCount,
    DateTime? StartedAt,
    int ACount,
    double? VOpen,
    double? VLoad,
    double? RInt,
    int VisualPass,
    double? VisualConfidence,
    int VisualMinusUp,
    int VisualPlusUp) {
    public string VOpenText => VOpen.HasValue ? $"{VOpen.Value:0.000} V" : "-";
    public string VLoadText => VLoad.HasValue ? $"{VLoad.Value:0.000} V" : "-";
    public string RIntText => RInt.HasValue ? $"{RInt.Value:0.000} Ω" : "-";
    public string VisualPassText => ACount > 0 ? $"{VisualPass}/{ACount}" : "-";
}

public static class CertificateDbReader {
    public static async Task<CertificateDbSummary> LoadAsync(
        string connectionString, string orderNo, CancellationToken ct = default) {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // 1. 주문의 현재 DB cert_no 조회
        string? dbCertNo = null;
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = "SELECT cert_no FROM orders WHERE order_no = @orderNo;";
            cmd.Parameters.AddWithValue("@orderNo", orderNo);
            var scalar = await cmd.ExecuteScalarAsync(ct);
            if (scalar != null && scalar != DBNull.Value)
                dbCertNo = Convert.ToString(scalar, CultureInfo.InvariantCulture);
        }

        // 2. 회차별 요약 조회 (LINE 회차 + 분류 완료 A등급 기준)
        var runs = new List<RunSummaryRow>();
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = """
                SELECT r.source_run_id, r.battery_count, ru.started_at,
                       COUNT(b.cycle_id)                        AS a_grade,
                       AVG(e.no_load_voltage_v)                 AS v_open,
                       AVG(e.loaded_voltage_v)                  AS v_load,
                       AVG(e.estimated_internal_resistance_ohm) AS r_int,
                       SUM(v.visual_result = 'PASS')            AS visual_pass,
                       AVG(v.confidence)                        AS visual_confidence,
                       SUM(v.polarity = 'minus_up')             AS visual_minus_up,
                       SUM(v.polarity = 'plus_up')              AS visual_plus_up
                FROM order_runs r
                JOIN runs ru                   ON ru.source_run_id = r.source_run_id
                                              AND ru.run_type = 'LINE'
                LEFT JOIN battery_cycles b     ON b.run_id = ru.run_id
                                              AND b.final_grade = 'A'
                                              AND b.sorted_destination IS NOT NULL
                LEFT JOIN electrical_tests e   ON e.cycle_id = b.cycle_id
                LEFT JOIN visual_inspections v ON v.cycle_id = b.cycle_id
                WHERE r.order_no = @orderNo
                GROUP BY r.source_run_id, r.battery_count, ru.started_at
                ORDER BY ru.started_at;
                """;
            cmd.Parameters.AddWithValue("@orderNo", orderNo);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) {
                runs.Add(new RunSummaryRow(
                    SourceRunId: r.GetString(0),
                    BatteryCount: ToInt(r, 1),
                    StartedAt: r.IsDBNull(2) ? null : r.GetDateTime(2),
                    ACount: ToInt(r, 3),
                    VOpen: ToDouble(r, 4),
                    VLoad: ToDouble(r, 5),
                    RInt: ToDouble(r, 6),
                    VisualPass: ToInt(r, 7),
                    VisualConfidence: ToDouble(r, 8),
                    VisualMinusUp: ToInt(r, 9),
                    VisualPlusUp: ToInt(r, 10)));
            }
        }

        // 3. 주문에 연결된 LINE 회차 전체의 분류 완료 A등급 평균 요약 조회
        int cnt = 0, measured = 0, elecNorm = 0;
        int visInsp = 0, visPass = 0, visFail = 0, visErr = 0, visMinus = 0, visPlus = 0, visUnk = 0;
        int elecMiss = 0, visMiss = 0;
        double? vOpen = null, vLoad = null, rInt = null, visConf = null;

        if (runs.Count > 0) {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)                                          AS cnt,
                       COUNT(e.cycle_id)                                 AS measured,
                       AVG(e.no_load_voltage_v)                          AS v_open,
                       AVG(e.loaded_voltage_v)                           AS v_load,
                       AVG(e.estimated_internal_resistance_ohm)          AS r_int,
                       SUM(e.polarity = 'normal')                        AS elec_polarity_normal,
                       COUNT(v.cycle_id)                                 AS visual_inspected,
                       SUM(v.visual_result = 'PASS')                     AS visual_pass,
                       SUM(v.visual_result = 'FAIL')                     AS visual_fail,
                       SUM(v.visual_result = 'ERROR')                    AS visual_error,
                       AVG(v.confidence)                                 AS visual_confidence,
                       SUM(v.polarity = 'minus_up')                      AS visual_minus_up,
                       SUM(v.polarity = 'plus_up')                       AS visual_plus_up,
                       SUM(v.cycle_id IS NOT NULL AND (v.polarity = 'unknown' OR v.polarity IS NULL)) AS visual_polarity_unknown,
                       COUNT(*) - COUNT(e.cycle_id)                      AS electrical_missing,
                       COUNT(*) - COUNT(v.cycle_id)                      AS visual_missing
                FROM order_runs r
                JOIN runs ru                   ON ru.source_run_id = r.source_run_id
                                              AND ru.run_type = 'LINE'
                JOIN battery_cycles b          ON b.run_id = ru.run_id
                                              AND b.final_grade = 'A'
                                              AND b.sorted_destination IS NOT NULL
                LEFT JOIN electrical_tests e   ON e.cycle_id = b.cycle_id
                LEFT JOIN visual_inspections v ON v.cycle_id = b.cycle_id
                WHERE r.order_no = @orderNo;
                """;
            cmd.Parameters.AddWithValue("@orderNo", orderNo);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) {
                cnt = ToInt(r, 0);
                measured = ToInt(r, 1);
                vOpen = ToDouble(r, 2);
                vLoad = ToDouble(r, 3);
                rInt = ToDouble(r, 4);
                elecNorm = ToInt(r, 5);
                visInsp = ToInt(r, 6);
                visPass = ToInt(r, 7);
                visFail = ToInt(r, 8);
                visErr = ToInt(r, 9);
                visConf = ToDouble(r, 10);
                visMinus = ToInt(r, 11);
                visPlus = ToInt(r, 12);
                visUnk = ToInt(r, 13);
                elecMiss = ToInt(r, 14);
                visMiss = ToInt(r, 15);
            }
        }

        // 4. grading_rules 조회 (연결 회차 적용 기준 우선 -> 없으면 최신 유효 기준 -> 권한/데이터 없으면 안전 폴백)
        var (ruleVersion, ruleVOpenMin, ruleRIntMax, isRuleFromDb, ruleNotice) =
            await LoadGradingRuleAsync(conn, orderNo, runs.Count > 0, ct);

        return new CertificateDbSummary(
            DbCertNo: dbCertNo,
            ACount: cnt,
            MeasuredCount: measured,
            VOpenAvg: vOpen,
            VLoadAvg: vLoad,
            RIntAvg: rInt,
            ElecPolarityNormal: elecNorm,
            VisualInspected: visInsp,
            VisualPass: visPass,
            VisualFail: visFail,
            VisualError: visErr,
            VisualConfidence: visConf,
            VisualMinusUp: visMinus,
            VisualPlusUp: visPlus,
            VisualPolarityUnknown: visUnk,
            ElectricalMissing: elecMiss,
            VisualMissing: visMiss,
            RuleVersion: ruleVersion,
            RuleVOpenMin: ruleVOpenMin,
            RuleRIntMax: ruleRIntMax,
            IsRuleFromDb: isRuleFromDb,
            RuleNotice: ruleNotice,
            Runs: runs);
    }

    private static async Task<(string? Version, double? VOpenMin, double? RIntMax, bool IsFromDb, string? Notice)>
        LoadGradingRuleAsync(MySqlConnection conn, string orderNo, bool hasRuns, CancellationToken ct) {
        try {
            // 4-1. 주문에 연결된 LINE 회차 A등급 배터리에 실제 적용된 판정 기준 조회
            if (hasRuns) {
                var linkedRules = new List<(string Version, string? Json)>();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT g.rule_version,
                           CAST(g.voltage_and_resistance_thresholds AS CHAR) AS thresholds_json
                    FROM order_runs r
                    JOIN runs ru          ON ru.source_run_id = r.source_run_id
                                         AND ru.run_type = 'LINE'
                    JOIN battery_cycles b ON b.run_id = ru.run_id
                                         AND b.final_grade = 'A'
                                         AND b.sorted_destination IS NOT NULL
                    JOIN grading_rules g  ON g.rule_id = b.rule_id
                    WHERE r.order_no = @orderNo
                    GROUP BY g.rule_id, g.rule_version, g.voltage_and_resistance_thresholds, g.effective_at
                    ORDER BY MAX(COALESCE(b.graded_at, b.started_at)) DESC, g.rule_id DESC;
                    """;
                cmd.Parameters.AddWithValue("@orderNo", orderNo);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) {
                    var version = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    var json = r.IsDBNull(1) ? null : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(version))
                        linkedRules.Add((version, json));
                }

                if (linkedRules.Count > 0) {
                    var versionText = linkedRules.Count == 1
                        ? linkedRules[0].Version
                        : $"{linkedRules[0].Version} 외 {linkedRules.Count - 1}건";

                    if (TryParseAGradeThresholds(linkedRules[0].Json, out var vMin, out var rMax)) {
                        string? notice = linkedRules.Count > 1
                            ? $"연결 회차에 기준 {linkedRules.Count}개 혼재({string.Join(", ", linkedRules.Select(x => x.Version))}) — 최신 기준({linkedRules[0].Version}) 적용"
                            : null;
                        return (versionText, vMin, rMax, true, notice);
                    }

                    return (
                        versionText,
                        null,
                        null,
                        false,
                        $"DB 기준({versionText})의 임계값 JSON이 비어 있어 임시 기준을 적용함");
                }
            }

            // 4-2. 연결된 rule_id가 없거나 회차 미연결 시: 최신 유효 grading_rules 조회
            await using (var cmd = conn.CreateCommand()) {
                cmd.CommandText = """
                    SELECT rule_version,
                           CAST(voltage_and_resistance_thresholds AS CHAR) AS thresholds_json
                    FROM grading_rules
                    WHERE voltage_and_resistance_thresholds IS NOT NULL
                    ORDER BY COALESCE(effective_at, '1970-01-01 00:00:00') DESC, rule_id DESC
                    LIMIT 1;
                    """;

                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) {
                    var version = r.IsDBNull(0) ? null : r.GetString(0).Trim();
                    var json = r.IsDBNull(1) ? null : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(version) && TryParseAGradeThresholds(json, out var vMin, out var rMax)) {
                        var notice = hasRuns ? "연결 회차에 rule_id가 없어 최신 grading_rules 기준을 적용함" : null;
                        return (version, vMin, rMax, true, notice);
                    }
                }
            }

            return (null, null, null, false, "DB grading_rules에 유효한 기준이 없어 임시 기준을 적용함");
        } catch (MySqlException ex) {
            return (null, null, null, false, $"grading_rules 조회 생략 — 임시 기준 적용 ({ex.Message})");
        }
    }

    private static bool TryParseAGradeThresholds(string? json, out double? vOpenMin, out double? rIntMax) {
        vOpenMin = null;
        rIntMax = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            JsonElement aObj = default;
            var foundA = false;
            foreach (var prop in root.EnumerateObject()) {
                if (string.Equals(prop.Name, "A", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Object) {
                    aObj = prop.Value;
                    foundA = true;
                    break;
                }
            }
            if (!foundA) return false;

            vOpenMin = ReadDouble(aObj, "v_open_min");
            rIntMax = ReadDouble(aObj, "r_int_max");
            return vOpenMin.HasValue && rIntMax.HasValue;
        } catch (JsonException) {
            return false;
        }
    }

    private static double? ReadDouble(JsonElement obj, string name) {
        foreach (var prop in obj.EnumerateObject()) {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var d))
                return d;
            if (prop.Value.ValueKind == JsonValueKind.String
                && double.TryParse(prop.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static int ToInt(MySqlDataReader r, int i) =>
        r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

    private static double? ToDouble(MySqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
}