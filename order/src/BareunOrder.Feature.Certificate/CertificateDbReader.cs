using MySqlConnector;

namespace BareunOrder.Feature.Certificate;

/// <summary>검사 성적서 DB 조회 결과 (주문관리_조회쿼리.sql 기준)</summary>
public sealed record CertificateDbSummary(
    string? DbCertNo,
    int ACount,
    int MeasuredCount,
    double? VOpenAvg,
    double? VOpenMin,
    double? VOpenMax,
    double? VLoadAvg,
    double? RIntAvg,
    double? RIntMax,
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
                dbCertNo = Convert.ToString(scalar);
        }

        // 2. 회차별 요약 조회 (연결된 order_runs 기준)
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
                JOIN runs ru            ON ru.source_run_id = r.source_run_id
                LEFT JOIN battery_cycles b     ON b.run_id = ru.run_id AND b.final_grade = 'A'
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

        // 3. 주문에 연결된 회차 전체의 A등급 요약 조회
        int cnt = 0, measured = 0, elecNorm = 0;
        int visInsp = 0, visPass = 0, visFail = 0, visErr = 0, visMinus = 0, visPlus = 0, visUnk = 0;
        int elecMiss = 0, visMiss = 0;
        double? vOpen = null, vOpenMin = null, vOpenMax = null, vLoad = null, rInt = null, rIntMax = null, visConf = null;

        if (runs.Count > 0) {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)                                          AS cnt,
                       COUNT(e.cycle_id)                                 AS measured,
                       AVG(e.no_load_voltage_v)                          AS v_open,
                       MIN(e.no_load_voltage_v)                          AS v_open_min,
                       MAX(e.no_load_voltage_v)                          AS v_open_max,
                       AVG(e.loaded_voltage_v)                           AS v_load,
                       AVG(e.estimated_internal_resistance_ohm)          AS r_int,
                       MAX(e.estimated_internal_resistance_ohm)          AS r_int_max,
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
                JOIN runs ru            ON ru.source_run_id = r.source_run_id
                JOIN battery_cycles b   ON b.run_id = ru.run_id AND b.final_grade = 'A'
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
                vOpenMin = ToDouble(r, 3);
                vOpenMax = ToDouble(r, 4);
                vLoad = ToDouble(r, 5);
                rInt = ToDouble(r, 6);
                rIntMax = ToDouble(r, 7);
                elecNorm = ToInt(r, 8);
                visInsp = ToInt(r, 9);
                visPass = ToInt(r, 10);
                visFail = ToInt(r, 11);
                visErr = ToInt(r, 12);
                visConf = ToDouble(r, 13);
                visMinus = ToInt(r, 14);
                visPlus = ToInt(r, 15);
                visUnk = ToInt(r, 16);
                elecMiss = ToInt(r, 17);
                visMiss = ToInt(r, 18);
            }
        }

        return new CertificateDbSummary(
            DbCertNo: dbCertNo,
            ACount: cnt,
            MeasuredCount: measured,
            VOpenAvg: vOpen,
            VOpenMin: vOpenMin,
            VOpenMax: vOpenMax,
            VLoadAvg: vLoad,
            RIntAvg: rInt,
            RIntMax: rIntMax,
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
            Runs: runs);
    }

    private static int ToInt(MySqlDataReader r, int i) =>
        r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));

    private static double? ToDouble(MySqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
}