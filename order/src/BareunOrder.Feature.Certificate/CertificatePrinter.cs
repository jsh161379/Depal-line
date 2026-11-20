using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Certificate;

/// <summary>검사 성적서 A4 FlowDocument 생성 및 인쇄·PDF 출력 (고객용 정제 문서)</summary>
public static class CertificatePrinter {
    private static readonly FontFamily Font = new("Malgun Gothic");
    private static readonly FontFamily Mono = new("Consolas, Malgun Gothic");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x30));
    private static readonly Brush Sub = new SolidColorBrush(Color.FromRgb(0x56, 0x61, 0x73));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE1));
    private static readonly Brush HeadFill = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
    private static readonly Brush SoftFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xFA));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1F, 0x8A, 0x4C));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC8, 0x36, 0x2F));

    private const double PageWidth = 793;
    private const double PageHeight = 1122;

    public static void Print(Order order, CertificateData data, string operatorName) {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var doc = Build(order, data, operatorName);
        doc.PageWidth = dlg.PrintableAreaWidth > 0 ? dlg.PrintableAreaWidth : PageWidth;
        doc.PageHeight = dlg.PrintableAreaHeight > 0 ? dlg.PrintableAreaHeight : PageHeight;
        doc.ColumnWidth = doc.PageWidth;

        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"검사성적서_{data.CertNo}");
    }

    public static FlowDocument Build(Order order, CertificateData data, string operatorName) {
        var staff = string.IsNullOrWhiteSpace(operatorName) ? "담당자" : operatorName.Trim();

        var doc = new FlowDocument {
            FontFamily = Font,
            FontSize = 11.5,
            Foreground = Ink,
            Background = Brushes.White,
            PagePadding = new Thickness(52, 46, 52, 46),
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            ColumnWidth = PageWidth,
        };

        // ── 헤더 ──
        var header = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        header.Columns.Add(new TableColumn { Width = new GridLength(3.6, GridUnitType.Star) });
        header.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
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
        var stampCell = new TableCell(new Paragraph(new Run(data.StampText)) {
            FontSize = 15,
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

        doc.Blocks.Add(new Paragraph {
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Margin = new Thickness(0, 4, 0, 12),
        });

        // ── 기본 정보 (고객용 정제) ──
        var issueText = string.IsNullOrEmpty(order.CertNo) ? "미발급 (미리보기)" : "발급 완료";
        doc.Blocks.Add(MakeMetaTable(new[]
        {
            new[] { "성적서 번호", data.CertNo, "주문번호", order.No },
            new[] { "받는 분", order.Name, "총 주문 수량", $"{data.TotalPacks:N0}팩 ({data.Count:N0}개)" },
            new[] { "검사 일시", $"{data.InspectedAt:yyyy-MM-dd HH:mm}", "검사 회차", data.RunId },
            new[] { "발급 상태", issueText, "발행일", $"{DateTime.Now:yyyy-MM-dd}" },
        }));

        // ── 주문 상품 내역 ──
        if (data.Products.Count > 0) {
            Heading(doc, "주문 상품 내역");
            var prodRows = data.Products
                .Select(p => new[] { p.Name, p.PackSpec, p.PackQtyText, p.BatteryCountText })
                .ToList();
            prodRows.Add(new[] { "합계", "", $"{data.TotalPacks:N0}팩", $"{data.Count:N0}개" });

            doc.Blocks.Add(MakeTable(
                new[] { "상품명", "팩 구성", "수량 (팩)", "건전지 수" },
                prodRows.ToArray(),
                widths: new[] { 2.8, 1.1, 1.0, 1.1 },
                rightAlignFromCol: 2,
                highlightLastRow: true));
        }

        // ── 검사 항목 및 결과 ──
        Heading(doc, "검사 항목 및 결과");
        var checkRows = data.Rows
            .Select(r => new[] { r.Item, r.Standard, r.Result, r.StatusText })
            .ToArray();
        doc.Blocks.Add(MakeTable(
            new[] { "검사 항목", "기준", "결과", "판정" },
            checkRows,
            widths: new[] { 2.3, 1.4, 2.0, 0.8 },
            lastColumnStatus: true));

        doc.Blocks.Add(Note("※ 판정 기준: 참고 항목을 제외한 모든 검사 항목이 '적합'이고, 검사 완료 수량이 주문 수량 이상일 때 '합격'으로 판정합니다."));

        // ── 검사 회차별 내역 (실DB 연동 시 + 합계 줄) ──
        if (data.Runs.Count > 0) {
            Heading(doc, "검사 회차별 내역");
            var runRows = data.Runs.Select(r => new[]
            {
                r.SourceRunId,
                $"{r.BatteryCount:N0}개",
                $"{r.ACount:N0}개",
                r.VOpenText,
                r.VLoadText,
                r.RIntText,
                r.VisualPassText,
            }).ToList();

            if (data.RunTotal != null) {
                runRows.Add(new[]
                {
                    data.RunTotal.Label,
                    $"{data.RunTotal.TotalShipped:N0}개",
                    $"{data.RunTotal.TotalACount:N0}개",
                    data.RunTotal.VOpenText,
                    data.RunTotal.VLoadText,
                    data.RunTotal.RIntText,
                    data.RunTotal.VisualPassText,
                });
            }

            doc.Blocks.Add(MakeTable(
                new[] { "회차 ID", "출하 수량", "A등급", "무부하", "부하", "내부저항", "외관 통과" },
                runRows.ToArray(),
                widths: new[] { 2.1, 0.9, 0.8, 1.0, 1.0, 1.0, 0.9 },
                highlightLastRow: data.RunTotal != null));
        }

        // ── 고객용 안내 문구 ──
        doc.Blocks.Add(new Paragraph(new Run(
            "본 제품은 외관·극 방향·전압·내부저항 검사를 통과한 A등급 제품입니다.")) {
            Foreground = Sub,
            FontSize = 10.5,
            Margin = new Thickness(0, 14, 0, 12),
        });

        // ── 책임자 확인란 (검사자 / 승인자) ──
        doc.Blocks.Add(MakeResponsibilityTable(staff));

        return doc;
    }

    private static void Heading(FlowDocument doc, string text) =>
        doc.Blocks.Add(new Paragraph(new Run(text)) {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 5),
            KeepWithNext = true,
        });

    private static Paragraph Note(string text) =>
        new(new Run(text)) { Foreground = Sub, FontSize = 10, Margin = new Thickness(0, 2, 0, 4) };

    private static Table MakeMetaTable(string[][] rows) {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };
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
                    Padding = new Thickness(4, 3, 4, 3),
                    Foreground = isLabel ? Sub : Ink,
                    FontSize = isLabel ? 10.5 : 11.5,
                });
            }
            group.Rows.Add(tr);
        }
        t.RowGroups.Add(group);
        return t;
    }

    private static Table MakeResponsibilityTable(string staff) {
        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 0),
        };
        t.Columns.Add(new TableColumn { Width = new GridLength(1.0, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.3, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.6, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });

        var group = new TableRowGroup();
        var row = new TableRow();

        static TableCell Cell(string text, Brush? bg, Brush fg, FontWeight weight, TextAlignment align = TextAlignment.Left) =>
            new(new Paragraph(new Run(text)) { Margin = new Thickness(0), TextAlignment = align }) {
                Background = bg ?? Brushes.Transparent,
                Foreground = fg,
                FontWeight = weight,
                FontSize = 11,
                Padding = new Thickness(8, 8, 8, 8),
                BorderBrush = Line,
                BorderThickness = new Thickness(0, 0, 1, 0),
            };

        row.Cells.Add(Cell("검사자", HeadFill, Sub, FontWeights.SemiBold, TextAlignment.Center));
        row.Cells.Add(Cell(staff, null, Ink, FontWeights.Normal, TextAlignment.Center));
        row.Cells.Add(Cell("승인자 (품질 담당)", HeadFill, Sub, FontWeights.SemiBold, TextAlignment.Center));
        row.Cells.Add(Cell($"{staff}   (인)", null, Ink, FontWeights.Normal, TextAlignment.Center));
        var last = Cell("바른전지 품질관리", HeadFill, Ink, FontWeights.SemiBold, TextAlignment.Center);
        last.BorderThickness = new Thickness(0);
        row.Cells.Add(last);

        group.Rows.Add(row);
        t.RowGroups.Add(group);
        return t;
    }

    private static Table MakeTable(
        string[] headers,
        string[][] rows,
        double[] widths,
        bool lastColumnStatus = false,
        int rightAlignFromCol = int.MaxValue,
        bool highlightLastRow = false) {
        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 4),
        };
        for (var i = 0; i < headers.Length; i++)
            t.Columns.Add(new TableColumn { Width = new GridLength(widths[i], GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(headers, head: true, highlight: false, lastColumnStatus: false, rightAlignFromCol: rightAlignFromCol));
        for (var idx = 0; idx < rows.Length; idx++) {
            var isLast = highlightLastRow && idx == rows.Length - 1;
            group.Rows.Add(MakeRow(rows[idx], head: false, highlight: isLast, lastColumnStatus: lastColumnStatus, rightAlignFromCol: rightAlignFromCol));
        }
        t.RowGroups.Add(group);
        return t;
    }

    private static TableRow MakeRow(
        string[] cells,
        bool head,
        bool highlight,
        bool lastColumnStatus,
        int rightAlignFromCol) {
        var row = new TableRow();
        if (head) row.Background = HeadFill;
        else if (highlight) row.Background = SoftFill;

        for (var i = 0; i < cells.Length; i++) {
            var isStatus = lastColumnStatus && !head && i == cells.Length - 1;
            var fg = Ink;
            if (isStatus) {
                fg = cells[i] switch {
                    "적합" => Green,
                    "참고" => Sub,
                    _ => Red,
                };
            }

            var align = i >= rightAlignFromCol ? TextAlignment.Right : TextAlignment.Left;
            row.Cells.Add(new TableCell(new Paragraph(new Run(cells[i])) {
                Margin = new Thickness(0),
                TextAlignment = align,
            }) {
                BorderBrush = Line,
                BorderThickness = new Thickness(0, head ? 0 : 1, 0, 0),
                Padding = new Thickness(6, 5, 6, 5),
                Foreground = fg,
                FontWeight = (head || highlight || isStatus) ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = head ? 11 : 11.5,
            });
        }
        return row;
    }
}