using System.Globalization;
using System.IO;
using System.Text;
using BareunOrder.Core.Models;
using Microsoft.Win32;

namespace BareunOrder.Feature.Certificate;

/// <summary>검사 성적서 Excel(CSV, UTF-8 BOM) 내보내기</summary>
public static class CertificateCsvExporter {
    /// <summary>저장한 파일 경로. 사용자가 취소하면 null.</summary>
    public static string? Export(Order order, CertificateData data, string operatorName) {
        var dialog = new SaveFileDialog {
            FileName = $"검사성적서_{data.CertNo}.csv",
            Filter = "CSV 파일 (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog() != true) return null;

        var staff = string.IsNullOrWhiteSpace(operatorName) ? "담당자" : operatorName.Trim();
        var sb = new StringBuilder();
        var issueText = string.IsNullOrEmpty(order.CertNo) ? "미발급 (미리보기)" : "발급 완료";

        // ── 기본 정보 ──
        Row(sb, "건전지 출고 검사 성적서");
        Row(sb, "항목", "내용", "항목", "내용");
        Row(sb, "성적서 번호", data.CertNo, "주문번호", order.No);
        Row(sb, "받는 분", order.Name, "총 주문 수량", $"{data.TotalPacks:N0}팩 ({data.Count:N0}개)");
        Row(sb, "검사 일시", data.InspectedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "검사 회차", data.RunId);
        Row(sb, "발급 상태", issueText, "종합 판정", data.VerdictText);
        Row(sb, "검사자 / 승인자", staff, "발행일", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.AppendLine();

        // ── 주문 상품 내역 ──
        if (data.Products.Count > 0) {
            Row(sb, "[주문 상품 내역]");
            Row(sb, "상품명", "팩 구성", "수량(팩)", "건전지 수");
            foreach (var p in data.Products)
                Row(sb, p.Name, p.PackSpec, p.PackQtyText, p.BatteryCountText);
            Row(sb, "합계", "", $"{data.TotalPacks:N0}팩", $"{data.Count:N0}개");
            sb.AppendLine();
        }

        // ── 검사 항목 및 결과 ──
        Row(sb, "[검사 항목 및 결과]");
        Row(sb, "검사 항목", "기준", "결과", "판정");
        foreach (var r in data.Rows)
            Row(sb, r.Item, r.Standard, r.Result, r.StatusText);
        Row(sb, "판정 기준", "참고 항목을 제외한 모든 검사 항목이 적합이고 검사 수량이 주문 수량 이상일 때 합격");

        // ── 검사 회차별 내역 (합계 포함) ──
        if (data.Runs.Count > 0) {
            sb.AppendLine();
            Row(sb, "[검사 회차별 내역]");
            Row(sb, "회차 ID", "시작 시각", "출하 수량", "A등급", "무부하", "부하", "내부저항", "외관 통과");
            foreach (var run in data.Runs) {
                Row(
                    sb,
                    run.SourceRunId,
                    run.StartedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-",
                    $"{run.BatteryCount:N0}개",
                    $"{run.ACount:N0}개",
                    run.VOpenText,
                    run.VLoadText,
                    run.RIntText,
                    run.VisualPassText);
            }
            if (data.RunTotal != null) {
                Row(
                    sb,
                    data.RunTotal.Label,
                    "",
                    $"{data.RunTotal.TotalShipped:N0}개",
                    $"{data.RunTotal.TotalACount:N0}개",
                    data.RunTotal.VOpenText,
                    data.RunTotal.VLoadText,
                    data.RunTotal.RIntText,
                    data.RunTotal.VisualPassText);
            }
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