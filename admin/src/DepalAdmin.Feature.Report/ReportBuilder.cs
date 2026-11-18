using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DepalAdmin.Core;

namespace DepalAdmin.Feature.Report;

/// <summary>
/// ReportData → A4 한 장짜리 FlowDocument. 화면 미리보기와 인쇄가 같은 모양을 쓴다.
/// 인쇄 대화상자에서 "Microsoft Print to PDF"를 고르면 PDF로 저장된다 (따로 라이브러리 필요 없음).
/// 보고서는 흰 종이에 인쇄하므로 화면 색(회색 바탕)이 아니라 흑백 위주.
/// </summary>
public static class ReportBuilder
{
    private static readonly FontFamily Font = new("Malgun Gothic");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1C));
    private static readonly Brush Sub = new SolidColorBrush(Color.FromRgb(0x5B, 0x5A, 0x54));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x8F, 0x8E, 0x87));
    private static readonly Brush HeadFill = new SolidColorBrush(Color.FromRgb(0xE6, 0xE5, 0xE0));

    // A4 (96 dpi 기준 793 × 1122)
    private const double PageWidth = 793;
    private const double PageHeight = 1122;

    public static FlowDocument Build(ReportData d)
    {
        var doc = new FlowDocument
        {
            FontFamily = Font,
            FontSize = 12,
            Foreground = Ink,
            Background = Brushes.White,
            PagePadding = new Thickness(56, 52, 56, 52),
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            ColumnWidth = PageWidth,   // 한 단
        };

        // ── 머리 ──
        doc.Blocks.Add(new Paragraph(new Run(d.Title)) { FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"{d.Station}   {d.Period}")) { Foreground = Sub, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"작성 {d.MadeBy}   {d.MadeAt:yyyy-MM-dd HH:mm}")) { Foreground = Sub, FontSize = 11, Margin = new Thickness(0, 0, 0, 14) });

        // ── 요약 ──
        Heading(doc, "요약");
        var grades = d.GradeColumns;
        doc.Blocks.Add(MakeTable(
            new[] { "처리" }.Concat(grades.Select(ShortGrade)).Concat(new[] { "배출 실패", "1개당 평균" }).ToArray(),
            new[]
            {
                new[] { $"{d.Total}개" }.Concat(grades.Select(g => $"{d.Count(g)} ({d.Ratio(g)})"))
                                        .Concat(new[] { d.SortFailed.ToString(), d.AvgCycle }).ToArray(),
            }));
        if (d.FirstAt != null)
            doc.Blocks.Add(Note($"처리 시간 {d.FirstAt:MM-dd HH:mm} ~ {d.LastAt:MM-dd HH:mm},  회차 {d.Runs.Count}개"));
        doc.Blocks.Add(Note("등급 기준값은 아직 임시값입니다. 기준이 바뀌면 결과의 기준 버전(rule_version)이 달라집니다. 배출 실패는 등급은 정해졌지만 분류함에 넣지 못한 건(등급 개수에 포함)."));

        // ── 측정값 ──
        Heading(doc, "측정값");
        var v = d.VOpens;
        var r = d.RInts;
        if (v.Count == 0 && r.Count == 0) doc.Blocks.Add(Note("측정 자리까지 간 건전지가 없습니다."));
        else
        {
            doc.Blocks.Add(MakeTable(
                new[] { "측정 건수", "무부하 전압 평균", "최소", "최대", "내부저항 평균", "최대" },
                new[]
                {
                    new[]
                    {
                        $"{v.Count}개",
                        ReportData.Fmt(v, x => x.Average(), "0.000") + " V",
                        ReportData.Fmt(v, x => x.Min(), "0.000") + " V",
                        ReportData.Fmt(v, x => x.Max(), "0.000") + " V",
                        ReportData.Fmt(r, x => x.Average(), "0.000") + " Ω",
                        ReportData.Fmt(r, x => x.Max(), "0.000") + " Ω",
                    },
                }));
            doc.Blocks.Add(Note("외관 불량·거꾸로 놓인 건전지는 측정하지 않아 값이 없습니다."));
        }

        // ── 불량·재분류 사유 ──
        Heading(doc, "불량·재분류 사유 (많은 순 5개)");
        var reasons = d.TopReasons;
        if (reasons.Count == 0) doc.Blocks.Add(Note("불량·재분류 건이 없습니다."));
        else doc.Blocks.Add(MakeTable(new[] { "사유", "등급", "개수" },
                 reasons.Select(x => new[] { x.Reason, Labels.Grade(x.Grade), x.Count.ToString() }).ToArray(),
                 widths: new[] { 4.0, 1.0, 1.0 }));

        // ── 일별 (주간 보고서만) ──
        if (d.IsWeekly)
        {
            Heading(doc, "일별");
            var days = d.ByDay;
            if (days.Count == 0) doc.Blocks.Add(Note("이 기간에 처리한 건전지가 없습니다."));
            else doc.Blocks.Add(MakeTable(
                     new[] { "날짜", "개수" }.Concat(grades.Select(ShortGrade)).Concat(new[] { "A 비율" }).ToArray(),
                     days.Select(x => new[] { x.Day.ToString("MM-dd (ddd)"), x.Count.ToString() }
                                      .Concat(x.ByGrade.Select(n => n.ToString()))
                                      .Concat(new[] { Percent(x.ByGrade[0], x.Count) }).ToArray()).ToArray(),
                     widths: new[] { 1.6, 0.8 }.Concat(grades.Select(_ => 1.0)).Concat(new[] { 1.0 }).ToArray()));
        }

        // ── 시간대별 (일일·주간) ──
        if (!d.IsPerRun)
        {
            Heading(doc, d.IsWeekly ? "시간대별 (7일 합계)" : "시간대별");
            var hours = d.ByHour;
            if (hours.Count == 0) doc.Blocks.Add(Note("처리한 시간이 없습니다."));
            else doc.Blocks.Add(MakeTable(
                     new[] { "시간", "처리", "A", "불량", "재분류", "A 비율" },
                     hours.Select(x => new[]
                     {
                         $"{x.Hour:00}시", x.Count.ToString(), x.A.ToString(), x.Ng.ToString(),
                         x.Resort.ToString(), Percent(x.A, x.Count),
                     }).ToArray()));
        }

        // ── 회차별 (일일·주간) ──
        if (!d.IsPerRun)
        {
            Heading(doc, "회차별");
            var runs = d.Runs;
            if (runs.Count == 0) doc.Blocks.Add(Note("이 기간에 회차가 없습니다."));
            else doc.Blocks.Add(MakeTable(
                     new[] { "회차", "시작", "소요", "개수" }.Concat(grades.Select(ShortGrade)).ToArray(),
                     runs.Select(x => new[]
                         {
                             x.RunId, x.Start?.ToString(d.IsWeekly ? "MM-dd HH:mm" : "HH:mm") ?? "-",
                             d.RunDuration(x.RunId), x.Count.ToString(),
                         }
                         .Concat(x.ByGrade.Select(n => n.ToString())).ToArray()).ToArray(),
                     widths: new[] { 2.6, d.IsWeekly ? 1.3 : 0.8, 1.0, 0.7 }.Concat(grades.Select(_ => 1.1)).ToArray()));
        }

        // ── 배출 실패 목록 (있을 때만) ──
        var failed = d.SortFailedRows;
        if (failed.Count > 0)
        {
            Heading(doc, $"배출 실패 ({failed.Count}건)");
            doc.Blocks.Add(MakeTable(
                new[] { "시각", "회차", "건전지", "등급", "사유" },
                failed.Take(20).Select(x => new[]
                {
                    (x.FinishedAt ?? x.StartedAt)?.ToString("MM-dd HH:mm:ss") ?? "-",
                    x.RunId, $"#{x.BatteryId}", Labels.Grade(x.Grade), x.Reason ?? "-",
                }).ToArray(),
                widths: new[] { 1.4, 2.4, 0.8, 1.0, 2.4 }));
            if (failed.Count > 20) doc.Blocks.Add(Note($"앞의 20건만 표시했습니다 (전체 {failed.Count}건은 이력 조회에서)."));
            doc.Blocks.Add(Note("분류함에 넣지 못한 건전지는 벨트 끝이나 재분류함을 확인하세요."));
        }

        // ── 장치·라인 이벤트 ──
        Heading(doc, "장치·라인 이벤트");
        doc.Blocks.Add(MakeTable(new[] { "고장 (ERROR)", "주의 (WARN)", "정보 (INFO)" }, new[]
        {
            new[] { d.Events("ERROR").ToString(), d.Events("WARN").ToString(), d.Events("INFO").ToString() },
        }));
        if (d.Events("ERROR") > 0)
            doc.Blocks.Add(Note("고장 내용은 이벤트 로그(F5)에서 같은 기간으로 확인하세요."));

        // ── 인수인계 메모 ──
        Heading(doc, "인수인계 메모");
        if (d.Notes.Count == 0) doc.Blocks.Add(Note("남긴 메모가 없습니다."));
        foreach (var n in d.Notes.OrderBy(n => n.WrittenAt))
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8), BorderBrush = Line, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0) };
            p.Inlines.Add(new Run($"{n.WrittenAt:MM-dd HH:mm}  {n.AuthorName}") { FontWeight = FontWeights.SemiBold });
            p.Inlines.Add(new Run($"   {Labels.Mode(n.LineMode)}, 미확인 알람 {n.UnackedAlarms}건") { Foreground = Sub, FontSize = 11 });
            p.Inlines.Add(new LineBreak());
            p.Inlines.Add(new Run(n.Text));
            doc.Blocks.Add(p);
        }

        // ── 확인란 (인쇄해서 서명) ──
        Heading(doc, "확인");
        var sign = MakeTable(new[] { "작성", "확인", "비고" },
            new[] { new[] { d.MadeBy, "", "" } }, widths: new[] { 1.0, 1.0, 2.0 });
        foreach (var cell in sign.RowGroups[0].Rows[1].Cells) cell.Padding = new Thickness(6, 14, 6, 14);
        doc.Blocks.Add(sign);

        return doc;
    }

    private static string Percent(int part, int total) => total == 0 ? "-" : $"{part * 100.0 / total:0}%";

    private static void Heading(FlowDocument doc, string text) =>
        doc.Blocks.Add(new Paragraph(new Run(text))
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6),
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 3),
        });

    private static Paragraph Note(string text) =>
        new(new Run(text)) { Foreground = Sub, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>표 머리용 짧은 등급 이름 (칸이 좁아 "외관 불량"이 두 줄로 꺾이지 않게)</summary>
    private static string ShortGrade(string grade) => grade switch
    {
        "NG_VISUAL" => "외관불량",
        "NG_VOLTAGE" => "전압불량",
        "NG" => "이전 NG",
        _ => Labels.Grade(grade),
    };

    private static Table MakeTable(string[] headers, string[][] rows, double[]? widths = null)
    {
        var t = new Table { CellSpacing = 0, BorderBrush = Line, BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < headers.Length; i++)
            t.Columns.Add(new TableColumn { Width = new GridLength(widths?[i] ?? 1, GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(headers, head: true));
        foreach (var r in rows) group.Rows.Add(MakeRow(r, head: false));
        t.RowGroups.Add(group);
        return t;
    }

    private static TableRow MakeRow(string[] cells, bool head)
    {
        var row = new TableRow();
        if (head) row.Background = HeadFill;
        foreach (var c in cells)
        {
            row.Cells.Add(new TableCell(new Paragraph(new Run(c)) { Margin = new Thickness(0) })
            {
                BorderBrush = Line,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(6, 3, 6, 3),
                FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
        return row;
    }
}
