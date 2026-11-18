using System.Globalization;
using System.IO;
using System.Text;
using BareunOrder.Core.Models;
using Microsoft.Win32;

namespace BareunOrder.Feature.Certificate;

/// <summary>검사 성적서 Excel(CSV, UTF-8 BOM) 내보내기</summary>
public static class CertificateCsvExporter {
    /// <summary>저장한 파일 경로. 사용자가 취소하면 null.</summary>
    public static string? Export(Order order, CertificateData data) {
        var dialog = new SaveFileDialog {
            FileName = $"검사성적서_{data.CertNo}.csv",
            Filter = "CSV 파일 (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog() != true) return null;

        var sb = new StringBuilder();

        var issueText = string.IsNullOrEmpty(order.CertNo) ? "미발급 (미리보기)" : "발급 완료";
        var passText = data.AllPass ? "합격" : (data.HasRuns ? "불합격" : "미충족");
        var sourceText = data.IsExample ? "예시 데이터" : "실측 데이터";

        // ── 기본 정보 ──
        Row(sb, "건전지 출고 검사 성적서");
        Row(sb, "항목", "내용", "항목", "내용");
        Row(sb, "성적서 번호", data.CertNo, "주문번호", order.No);
        Row(sb, "고객명", order.Name, "주문 수량", $"{data.Count}개");
        Row(sb, "검사 일시", data.InspectedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "검사 회차", data.RunId);
        Row(sb, "발급 상태", issueText, "종합 판정", passText);
        Row(sb, "데이터 구분", sourceText, "발행일", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.AppendLine();

        // ── 검사 항목 및 결과 ──
        Row(sb, "[검사 항목 및 결과]");
        Row(sb, "검사 항목", "기준", "결과", "판정");
        foreach (var r in data.Rows)
            Row(sb, r.Item, r.Standard, r.Result, r.Pass ? "적합" : "부적합");

        // ── 연결된 생산 회차 내역 (실DB 연동 시) ──
        if (data.Runs.Count > 0) {
            sb.AppendLine();
            Row(sb, "[연결된 생산 회차 내역]");
            Row(sb, "회차 ID", "시작 시각", "출하 수량", "A등급", "무부하", "부하", "내부저항", "외관 통과", "외관 신뢰도", "-상", "+상");
            foreach (var run in data.Runs) {
                Row(
                    sb,
                    run.SourceRunId,
                    run.StartedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-",
                    $"{run.BatteryCount}개",
                    $"{run.ACount}개",
                    run.VOpenText,
                    run.VLoadText,
                    run.RIntText,
                    run.VisualPassText,
                    run.VisualConfidence.HasValue ? $"{run.VisualConfidence.Value:P1}" : "-",
                    run.VisualMinusUp.ToString(CultureInfo.InvariantCulture),
                    run.VisualPlusUp.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (!string.IsNullOrEmpty(data.MissingNotice)) {
            sb.AppendLine();
            Row(sb, "비고", data.MissingNotice);
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return dialog.FileName;
    }

    private static void Row(StringBuilder sb, params string?[] cells) =>
        sb.AppendLine(string.Join(",", cells.Select(c => Escape(c ?? ""))));

    private static string Escape(string text) =>
        text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
}