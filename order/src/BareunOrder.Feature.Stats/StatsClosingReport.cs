using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Stats;

/// <summary>
/// 주문 마감 보고서 (일일·주간·월간) → A4 FlowDocument → PDF.
/// 숫자는 모두 StatsCalculator 결과(StatsResult)를 그대로 쓴다 → 화면·엑셀·보고서 숫자가 같다.
/// 모양은 관리자 프로그램 보고서(조시현 ReportBuilder)와 맞춤: 흰 종이, 흑백 위주, 맑은 고딕.
/// PDF는 페이지를 그림으로 그려 직접 만든다 (SavePdf) → 외부 라이브러리·프린터 없이 파일 이름을 정해 저장.
///
/// 항목: 요약(이번 / 이전 / 증감) · 처리 현황(상태별) · 상품별 매출 · 일별(주간·월간) ·
///       미처리 주문(이월) · 취소 주문 · 고객 문의 · 확인란
/// 개인정보: 고객 이름과 가린 연락처만, 주소는 넣지 않는다.
/// </summary>
internal static class StatsClosingReport
{
    private static readonly FontFamily Font = new("Malgun Gothic");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1C));
    private static readonly Brush Sub = new SolidColorBrush(Color.FromRgb(0x5B, 0x5A, 0x54));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x8F, 0x8E, 0x87));
    private static readonly Brush HeadFill = new SolidColorBrush(Color.FromRgb(0xE6, 0xE5, 0xE0));

    // A4 (96 dpi 기준 793 × 1122)
    private const double PageWidth = 793;
    private const double PageHeight = 1122;

    private const int ListLimit = 20;
    private const string HeadingTag = "section";   // 미처리·취소 목록은 앞의 20건만

    // ───────────────────────── 보고서 만들기 ─────────────────────────

    public static FlowDocument Build(PeriodKind kind, StatsResult now, StatsResult before,
                                     IReadOnlyList<InquiryRow> inquiries, string madeBy)
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
        var prevName = StatsPeriod.PreviousName(kind);

        // ── 머리 ──
        doc.Blocks.Add(new Paragraph(new Run($"주문 마감 보고서 ({StatsPeriod.KindName(kind)})"))
        { FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"바른전지 주문 관리   {StatsPeriod.RangeText(now.Start, now.End)}"))
        { Foreground = Sub, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"작성 {madeBy}   {DateTime.Now:yyyy-MM-dd HH:mm}   비교: {prevName} {StatsPeriod.RangeText(before.Start, before.End)}"))
        { Foreground = Sub, FontSize = 11, Margin = new Thickness(0, 0, 0, 14) });

        // ── 요약 ──
        Heading(doc, "요약");
        doc.Blocks.Add(MakeTable(
            new[] { "항목", "이번 기간", prevName, "증감" },
            new[]
            {
                Kpi("주문 수 (취소 제외)", now.OrderCount, before.OrderCount, "건"),
                Kpi("매출", now.Sales, before.Sales, "원"),
                Kpi("주문당 평균", now.AverageOrder, before.AverageOrder, "원"),
                Kpi("판매 팩 수", now.PackCount, before.PackCount, "팩"),
                Kpi("판매 건전지 수", now.BatteryCount, before.BatteryCount, "개"),
                Kpi("취소 건수", now.CancelCount, before.CancelCount, "건"),
                new[]
                {
                    "취소율", $"{now.CancelRate:0.0}%", $"{before.CancelRate:0.0}%",
                    Signed(now.CancelRate - before.CancelRate, "0.0") + "%p",
                },
            },
            widths: new[] { 2.0, 1.4, 1.4, 1.6 },
            rightFrom: 1));
        doc.Blocks.Add(Note("주문 수·매출·팩 수는 취소 주문을 뺀 값입니다. 취소율 = 취소 건수 ÷ 전체 주문."));

        // ── 처리 현황 ──
        Heading(doc, "처리 현황 (주문 상태별, 취소 포함)");
        doc.Blocks.Add(MakeTable(
            now.ByStatus.Select(s => s.Name).Concat(new[] { "합계" }).ToArray(),
            new[] { now.ByStatus.Select(s => $"{s.Count}건").Concat(new[] { $"{now.Orders.Count}건" }).ToArray() },
            center: true));
        doc.Blocks.Add(Note($"출고·배송 {now.ShippedCount}건 (배송 중 + 배송 완료),  처리 대기 {now.PendingCount}건 (주문 접수 + 출고 검사)"));

        // ── 상품별 매출 ──
        Heading(doc, "상품별 매출 (취소 제외)");
        var totalSales = Math.Max(1, now.Sales);
        doc.Blocks.Add(MakeTable(
            new[] { "상품", "판매 팩", "매출", "비중" },
            now.ByProduct.Select(p => new[]
                {
                    p.Name, $"{p.Packs:N0}팩", $"{p.Sales:N0}원", $"{100.0 * p.Sales / totalSales:0.0}%",
                })
                .Append(new[] { "합계", $"{now.PackCount:N0}팩", $"{now.Sales:N0}원", now.Sales == 0 ? "-" : "100%" })
                .ToArray(),
            widths: new[] { 2.4, 1.0, 1.4, 0.8 },
            rightFrom: 1, lastRowBold: true));

        // ── 일별 (주간·월간) ──
        if (now.ByDay.Count > 1)
        {
            Heading(doc, "일별");
            doc.Blocks.Add(MakeTable(
                new[] { "날짜", "주문 수", "매출", "판매 팩", "취소" },
                now.ByDay.Select(d => new[]
                    {
                        StatsPeriod.DayText(d.Date), $"{d.Orders}건", $"{d.Sales:N0}원", $"{d.Packs}팩", $"{d.Canceled}건",
                    })
                    .Append(new[] { "합계", $"{now.OrderCount}건", $"{now.Sales:N0}원", $"{now.PackCount}팩", $"{now.CancelCount}건" })
                    .ToArray(),
                widths: new[] { 1.6, 0.9, 1.4, 0.9, 0.8 },
                rightFrom: 1, lastRowBold: true));
        }

        // ── 미처리 주문 (다음으로 넘어가는 주문) ──
        var pending = now.Orders.Where(o => o.Status is OrderStatus.Received or OrderStatus.Inspect).ToList();
        Heading(doc, $"미처리 주문 ({pending.Count}건, 다음으로 이월)");
        if (pending.Count == 0) doc.Blocks.Add(Note("이 기간에 들어온 주문은 모두 출고했거나 취소했습니다."));
        else
        {
            doc.Blocks.Add(OrderTable(pending.Take(ListLimit)));
            if (pending.Count > ListLimit) doc.Blocks.Add(Note($"앞의 {ListLimit}건만 표시했습니다 (전체는 주문 탭에서)."));
        }

        // ── 취소 주문 ──
        var canceled = now.Orders.Where(o => o.Status == OrderStatus.Canceled).ToList();
        Heading(doc, $"취소 주문 ({canceled.Count}건)");
        if (canceled.Count == 0) doc.Blocks.Add(Note("취소된 주문이 없습니다."));
        else
        {
            doc.Blocks.Add(OrderTable(canceled.Take(ListLimit)));
            if (canceled.Count > ListLimit) doc.Blocks.Add(Note($"앞의 {ListLimit}건만 표시했습니다."));
        }

        // ── 고객 문의 ──
        Heading(doc, "고객 문의 (유형별)");
        var asked = inquiries.Sum(q => q.Count);
        if (asked == 0) doc.Blocks.Add(Note("이 기간에 들어온 문의가 없습니다."));
        else
        {
            doc.Blocks.Add(MakeTable(
                new[] { "유형", "문의 수", "답변 대기" },
                inquiries.Where(q => q.Count > 0)
                    .Select(q => new[] { q.Name, $"{q.Count}건", $"{q.Unanswered}건" })
                    .Append(new[] { "합계", $"{asked}건", $"{inquiries.Sum(q => q.Unanswered)}건" })
                    .ToArray(),
                widths: new[] { 2.0, 1.0, 1.0 },
                rightFrom: 1, lastRowBold: true));
            if (inquiries.Sum(q => q.Unanswered) > 0)
                doc.Blocks.Add(Note("답변 대기 문의는 고객·문의 탭(F5)에서 처리하세요."));
        }

        // ── 확인란 (인쇄해서 서명) ──
        Heading(doc, "확인");
        var sign = MakeTable(new[] { "작성", "확인", "비고" }, new[] { new[] { madeBy, "", "" } },
                             widths: new[] { 1.0, 1.0, 2.0 });
        foreach (var cell in sign.RowGroups[0].Rows[1].Cells) cell.Padding = new Thickness(6, 14, 6, 14);
        doc.Blocks.Add(sign);

        return doc;
    }

    // ───────────────────────── PDF로 저장 ─────────────────────────

    /// <summary>
    /// 보고서를 PDF 파일로 바로 저장한다 (파일 이름을 우리가 정할 수 있게 프린터를 거치지 않음).
    /// 방법: 페이지마다 그림(JPEG, 192dpi)으로 그린 뒤 PDF 한 장에 한 그림씩 넣는다 → 외부 라이브러리 없음.
    /// 그림으로 넣으므로 PDF 안의 글자를 드래그해서 복사할 수는 없다 (인쇄·보기는 선명함).
    /// </summary>
    public static void SavePdf(FlowDocument doc, string path)
    {
        const double scale = 2.0;   // 96dpi × 2 = 192dpi
        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.PageSize = new Size(PageWidth, PageHeight);
        KeepSectionsTogether(doc, (DynamicDocumentPaginator)paginator);
        paginator.ComputePageCount();

        var pages = new List<(byte[] Jpeg, int W, int H)>();
        for (var i = 0; i < paginator.PageCount; i++)
        {
            using var page = paginator.GetPage(i);
            var w = (int)Math.Ceiling(PageWidth * scale);
            var h = (int)Math.Ceiling(PageHeight * scale);
            var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

            // 흰 바탕 먼저 (투명한 곳이 JPEG에서 검게 나오지 않게) → 그 위에 페이지
            var white = new DrawingVisual();
            using (var dc = white.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
            bmp.Render(white);
            bmp.Render(page.Visual);

            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pages.Add((ms.ToArray(), w, h));
        }

        WritePdf(path, pages);
    }

    /// <summary>
    /// 구역(제목 + 표 + 안내 글)이 두 페이지에 걸치면 그 구역을 다음 페이지로 넘긴다.
    /// 앞에서부터 하나씩 보고, 넘길 때마다 페이지를 다시 나눈다 (뒤 구역 위치가 바뀌므로).
    /// 한 페이지보다 긴 구역이거나 이미 페이지 맨 위에서 시작하면 그대로 둔다 (빈 페이지 방지).
    /// </summary>
    private static void KeepSectionsTogether(FlowDocument doc, DynamicDocumentPaginator paginator)
    {
        var blocks = doc.Blocks.ToList();
        var starts = blocks.Select((b, i) => (b, i)).Where(x => HeadingTag.Equals(x.b.Tag)).Select(x => x.i).ToList();

        for (var k = 0; k < starts.Count; k++)
        {
            var first = blocks[starts[k]];
            var last = blocks[(k + 1 < starts.Count ? starts[k + 1] : blocks.Count) - 1];
            if (starts[k] == 0) continue;
            var before = blocks[starts[k] - 1];

            paginator.ComputePageCount();
            var startPage = paginator.GetPageNumber(first.ContentStart);
            var endPage = paginator.GetPageNumber(last.ContentEnd);
            var prevPage = paginator.GetPageNumber(before.ContentEnd);
            if (startPage < 0 || endPage < 0) continue;

            var spans = endPage > startPage;
            var notAtTop = prevPage == startPage;          // 같은 페이지에 앞 내용이 있음
            var fitsOnePage = endPage - startPage <= 1;     // 세 페이지 이상 걸치면 넘겨도 소용없음
            if (spans && notAtTop && fitsOnePage)
                first.BreakPageBefore = true;
        }
    }

    /// <summary>JPEG 그림들을 A4 페이지에 한 장씩 넣은 가장 단순한 PDF</summary>
    private static void WritePdf(string path, IReadOnlyList<(byte[] Jpeg, int W, int H)> pages)
    {
        const string a4 = "595.28 841.89";   // A4 (pt)
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();

        void Ascii(string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            fs.Write(b, 0, b.Length);
        }
        void BeginObj() { offsets.Add(fs.Position); Ascii($"{offsets.Count} 0 obj\n"); }

        // 번호: 1 카탈로그, 2 페이지 목록, 페이지마다 3개 (페이지, 그림, 내용)
        int PageObj(int i) => 3 + i * 3;

        Ascii("%PDF-1.4\n");
        BeginObj(); Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj();
        Ascii($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ", pages.Select((_, i) => $"{PageObj(i)} 0 R"))}] >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var (jpeg, w, h) = pages[i];
            var content = $"q {a4.Split(' ')[0]} 0 0 {a4.Split(' ')[1]} 0 0 cm /Im0 Do Q";

            BeginObj();
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {a4}] " +
                  $"/Resources << /XObject << /Im0 {PageObj(i) + 1} 0 R >> >> /Contents {PageObj(i) + 2} 0 R >>\nendobj\n");

            BeginObj();
            Ascii($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB " +
                  $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            fs.Write(jpeg, 0, jpeg.Length);
            Ascii("\nendstream\nendobj\n");

            BeginObj();
            Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        var xref = fs.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Ascii($"{o:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    // ───────────────────────── 표·글자 도우미 ─────────────────────────

    private static string[] Kpi(string label, int now, int before, string unit) =>
        new[] { label, $"{now:N0}{unit}", $"{before:N0}{unit}", Signed(now - before, "N0") + unit };

    private static string Signed(double v, string format) =>
        (v > 0 ? "+" : v < 0 ? "−" : "±") + Math.Abs(v).ToString(format);

    private static Table OrderTable(IEnumerable<Order> orders) => MakeTable(
        new[] { "주문번호", "접수 시각", "상태", "고객", "연락처", "팩", "금액" },
        orders.Select(o => new[]
        {
            o.No, $"{o.ReceivedAt:MM-dd HH:mm}", o.StatusText, o.Name, o.MaskedPhone, $"{o.PackCount}", $"{o.Total:N0}원",
        }).ToArray(),
        widths: new[] { 1.9, 1.1, 0.9, 0.8, 1.4, 0.5, 1.1 },
        rightFrom: 5);

    private static void Heading(FlowDocument doc, string text) =>
        doc.Blocks.Add(new Paragraph(new Run(text))
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6),
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 3),
            Tag = HeadingTag,   // KeepSectionsTogether가 구역 시작으로 알아봄
        });

    private static Paragraph Note(string text) =>
        new(new Run(text)) { Foreground = Sub, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>
    /// 표 하나. rightFrom: 이 열부터 숫자라 오른쪽 정렬 (-1이면 없음). center: 모든 칸 가운데.
    /// lastRowBold: 마지막 줄(합계)을 굵게, 머리 색으로.
    /// </summary>
    private static Table MakeTable(string[] headers, string[][] rows, double[]? widths = null,
                                   int rightFrom = -1, bool center = false, bool lastRowBold = false)
    {
        var t = new Table { CellSpacing = 0, BorderBrush = Line, BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < headers.Length; i++)
            t.Columns.Add(new TableColumn { Width = new GridLength(widths?[i] ?? 1, GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(headers, head: true, rightFrom: -1, center: true));
        for (var r = 0; r < rows.Length; r++)
        {
            var total = lastRowBold && r == rows.Length - 1;
            group.Rows.Add(MakeRow(rows[r], head: total, rightFrom, center));
        }
        t.RowGroups.Add(group);
        return t;
    }

    private static TableRow MakeRow(string[] cells, bool head, int rightFrom, bool center)
    {
        var row = new TableRow();
        if (head) row.Background = HeadFill;
        for (var i = 0; i < cells.Length; i++)
        {
            var align = center ? TextAlignment.Center
                      : rightFrom >= 0 && i >= rightFrom ? TextAlignment.Right
                      : TextAlignment.Left;
            row.Cells.Add(new TableCell(new Paragraph(new Run(cells[i])) { Margin = new Thickness(0), TextAlignment = align })
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
