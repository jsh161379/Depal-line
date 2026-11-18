using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Certificate;

/// <summary>검사 성적서 A4 FlowDocument 생성 및 인쇄·PDF 출력</summary>
public static class CertificatePrinter {
    private static readonly FontFamily Font = new("Malgun Gothic");
    private static readonly FontFamily Mono = new("Consolas, Malgun Gothic");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x30));
    private static readonly Brush Sub = new SolidColorBrush(Color.FromRgb(0x56, 0x61, 0x73));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE1));
    private static readonly Brush HeadFill = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1F, 0x8A, 0x4C));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC8, 0x36, 0x2F));

    private const double PageWidth = 793;
    private const double PageHeight = 1122;

    public static void Print(Order order, CertificateData data) {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var doc = Build(order, data);
        doc.PageWidth = dlg.PrintableAreaWidth > 0 ? dlg.PrintableAreaWidth : PageWidth;
        doc.PageHeight = dlg.PrintableAreaHeight > 0 ? dlg.PrintableAreaHeight : PageHeight;
        doc.ColumnWidth = doc.PageWidth;

        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"검사성적서_{data.CertNo}");
    }

    public static FlowDocument Build(Order order, CertificateData data) {
        var doc = new FlowDocument {
            FontFamily = Font,
            FontSize = 12,
            Foreground = Ink,
            Background = Brushes.White,
            PagePadding = new Thickness(56, 52, 56, 52),
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            ColumnWidth = PageWidth,
        };

        // ── 헤더 ──
        var header = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };
        header.Columns.Add(new TableColumn { Width = new GridLength(4, GridUnitType.Star) });
        header.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var hg = new TableRowGroup();
        var hr = new TableRow();

        var titleCell = new TableCell();
        titleCell.Blocks.Add(new Paragraph(new Run("건전지 출고 검사 성적서")) {
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        titleCell.Blocks.Add(new Paragraph(new Run("바른전지 · AA 건전지 자동 검사 라인")) {
            Foreground = Sub,
            FontSize = 11,
            Margin = new Thickness(0),
        });
        hr.Cells.Add(titleCell);

        var stampBrush = data.AllPass ? Green : Red;
        var stampCell = new TableCell(new Paragraph(new Run(data.AllPass ? "합 격" : "불합격")) {
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = stampBrush,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0),
        }) {
            BorderBrush = stampBrush,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(8, 6, 8, 6),
        };
        hr.Cells.Add(stampCell);
        hg.Rows.Add(hr);
        header.RowGroups.Add(hg);
        doc.Blocks.Add(header);

        // 구분선
        doc.Blocks.Add(new Paragraph {
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Margin = new Thickness(0, 4, 0, 14),
        });

        // ── 기본 정보 ──
        var issueText = string.IsNullOrEmpty(order.CertNo) ? "미발급 (미리보기)" : "발급 완료";
        doc.Blocks.Add(MakeMetaTable(new[]
        {
            new[] { "성적서 번호", data.CertNo, "주문번호", order.No },
            new[] { "고객명", order.Name, "주문 수량", $"{data.Count}개" },
            new[] { "검사 일시", $"{data.InspectedAt:yyyy-MM-dd HH:mm}", "검사 회차", data.RunId },
            new[] { "발급 상태", issueText, "비고", data.IsExample ? "예시 데이터" : "실측 데이터" },
        }));

        // ── 검사 항목 표 ──
        Heading(doc, "검사 항목 및 결과");
        var checkRows = data.Rows
            .Select(r => new[] { r.Item, r.Standard, r.Result, r.Pass ? "적합" : "부적합" })
            .ToArray();
        doc.Blocks.Add(MakeTable(
            new[] { "검사 항목", "기준", "결과", "판정" },
            checkRows,
            widths: new[] { 2.4, 1.5, 2.0, 0.8 },
            lastColumnStatus: true));

        if (!string.IsNullOrEmpty(data.MissingNotice))
            doc.Blocks.Add(Note(data.MissingNotice));

        // ── 연결 회차 내역 (실DB 연동 시) ──
        if (data.Runs.Count > 0) {
            Heading(doc, "연결된 생산 회차 내역");
            var runRows = data.Runs.Select(r => new[]
            {
                r.SourceRunId,
                $"{r.BatteryCount}개",
                $"{r.ACount}개",
                r.VOpenText,
                r.VLoadText,
                r.RIntText,
                r.VisualPassText,
            }).ToArray();

            doc.Blocks.Add(MakeTable(
                new[] { "회차 ID", "출하 수량", "A등급", "무부하", "부하", "내부저항", "외관 통과" },
                runRows,
                widths: new[] { 2.1, 0.9, 0.8, 1.0, 1.0, 1.0, 0.9 }));
        }

        // ── 하단 안내 및 서명 ──
        doc.Blocks.Add(new Paragraph(new Run(
            "본 성적서는 출고 주문에 연결된 라인 회차의 A등급 건전지 외관·극 방향(YOLO) 및 전기 측정(무부하·부하 전압, 내부저항) 데이터를 기준으로 작성되었습니다.")) {
            Foreground = Sub,
            FontSize = 10.5,
            Margin = new Thickness(0, 16, 0, 0),
        });

        doc.Blocks.Add(new Paragraph(new Run($"발행일: {DateTime.Now:yyyy-MM-dd}     바른전지 품질관리")) {
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
        });

        return doc;
    }

    private static void Heading(FlowDocument doc, string text) =>
        doc.Blocks.Add(new Paragraph(new Run(text)) {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6),
        });

    private static Paragraph Note(string text) =>
        new(new Run(text)) { Foreground = Sub, FontSize = 10.5, Margin = new Thickness(0, 2, 0, 4) };

    private static Table MakeMetaTable(string[][] rows) {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        t.Columns.Add(new TableColumn { Width = new GridLength(1.0, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.8, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.0, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.8, GridUnitType.Star) });

        var group = new TableRowGroup();
        foreach (var r in rows) {
            var tr = new TableRow();
            for (var i = 0; i < r.Length; i++) {
                var isLabel = i % 2 == 0;
                tr.Cells.Add(new TableCell(new Paragraph(new Run(r[i])) {
                    Margin = new Thickness(0),
                    FontFamily = isLabel ? Font : Mono,
                }) {
                    Padding = new Thickness(4, 4, 4, 4),
                    Foreground = isLabel ? Sub : Ink,
                    FontSize = isLabel ? 11 : 12,
                });
            }
            group.Rows.Add(tr);
        }
        t.RowGroups.Add(group);
        return t;
    }

    private static Table MakeTable(string[] headers, string[][] rows, double[] widths, bool lastColumnStatus = false) {
        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 6),
        };
        for (var i = 0; i < headers.Length; i++)
            t.Columns.Add(new TableColumn { Width = new GridLength(widths[i], GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(headers, head: true, lastColumnStatus: false));
        foreach (var r in rows) group.Rows.Add(MakeRow(r, head: false, lastColumnStatus: lastColumnStatus));
        t.RowGroups.Add(group);
        return t;
    }

    private static TableRow MakeRow(string[] cells, bool head, bool lastColumnStatus) {
        var row = new TableRow();
        if (head) row.Background = HeadFill;
        for (var i = 0; i < cells.Length; i++) {
            var isStatus = lastColumnStatus && !head && i == cells.Length - 1;
            var fg = Ink;
            if (isStatus) fg = cells[i] == "적합" ? Green : Red;

            row.Cells.Add(new TableCell(new Paragraph(new Run(cells[i])) { Margin = new Thickness(0) }) {
                BorderBrush = Line,
                BorderThickness = new Thickness(0, head ? 0 : 1, 0, 0),
                Padding = new Thickness(6, 5, 6, 5),
                Foreground = fg,
                FontWeight = (head || isStatus) ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = head ? 11.5 : 12,
            });
        }
        return row;
    }
}