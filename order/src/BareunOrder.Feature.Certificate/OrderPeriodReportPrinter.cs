using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BareunOrder.Core.Models;
using BareunOrder.Feature.Stats;

namespace BareunOrder.Feature.Certificate;

/// <summary>고객 유형·연령대·지역별 집계 한 줄</summary>
public sealed record CustomerSegmentRow(string Label, int Orders, int Sales);

/// <summary>
/// 담당: 유동호 — 주문 관리 기간 보고서(일일/주간/월간/지정 기간) 데이터 모델 및 FlowDocument 인쇄·PDF 출력.
/// </summary>
public sealed record PeriodReportBundle(
    PeriodKind Kind,
    bool IsCustomRange,
    DateTime Start,
    DateTime End,
    StatsResult Now,
    StatsResult Previous,
    bool HasPreviousData,
    int ShippedOrderCount,
    int UnshippedOrderCount,
    int IssuedCertCount,
    int UnissuedCertCount,
    double AvgPacksPerOrder,
    double PrevAvgPacksPerOrder,
    double AvgBatteriesPerOrder,
    int TotalShippedBatteries,
    int UnshippedBatteries,
    IReadOnlyList<CustomerSegmentRow> CustomerTypes,
    IReadOnlyList<CustomerSegmentRow> AgeGroups,
    IReadOnlyList<CustomerSegmentRow> Regions) {
    public int Days => (End.Date - Start.Date).Days + 1;
    public string KindLabel => IsCustomRange ? $"지정 기간 ({Days}일)" : OrderPeriodReportPrinter.KindLabel(Kind);
    public string FileKindLabel => IsCustomRange ? "지정기간" : OrderPeriodReportPrinter.KindLabel(Kind);
    public string PreviousLabel => IsCustomRange ? $"직전 {Days}일" : OrderPeriodReportPrinter.PreviousLabel(Kind);
    public string PeriodRangeText => OrderPeriodReportPrinter.FormatPeriodRange(Start, End);
    public string PreviousRangeText => OrderPeriodReportPrinter.FormatPeriodRange(Previous.Start, Previous.End);
}

public static class OrderPeriodReportPrinter {
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");
    private static readonly FontFamily Font = new("Malgun Gothic");
    private static readonly FontFamily Mono = new("Consolas, Malgun Gothic");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x30));
    private static readonly Brush Sub = new SolidColorBrush(Color.FromRgb(0x56, 0x61, 0x73));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE1));
    private static readonly Brush HeadFill = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
    private static readonly Brush SoftFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xFA));
    private static readonly Brush BarFill = new SolidColorBrush(Color.FromRgb(0x1D, 0x5F, 0xD1));
    private static readonly Brush BarTrack = new SolidColorBrush(Color.FromRgb(0xEA, 0xEE, 0xF4));
    private static readonly Brush FutureTrack = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA));

    private const double PageWidth = 793;
    private const double PageHeight = 1122;
    private const int MaxDailyChartDays = 31;

    public static (DateTime Start, DateTime End) ResolveRange(PeriodKind kind, DateTime anchor) {
        var d = anchor.Date;
        return kind switch {
            PeriodKind.Week => GetWeekRange(d),
            PeriodKind.Month => GetMonthRange(d),
            _ => (d, d),
        };
    }

    internal static bool IsStandardRange(PeriodKind kind, DateTime start, DateTime end) {
        var s = start.Date;
        var e = end.Date;
        if (s > e) (s, e) = (e, s);
        return kind switch {
            PeriodKind.Day => s == e,
            PeriodKind.Week => GetWeekRange(s) == (s, e),
            PeriodKind.Month => GetMonthRange(s) == (s, e),
            _ => false,
        };
    }

    public static (DateTime Start, DateTime End) ResolvePreviousRange(PeriodKind kind, DateTime start, DateTime end) {
        var s = start.Date;
        var e = end.Date;
        if (s > e) (s, e) = (e, s);

        if (!IsStandardRange(kind, s, e)) {
            var days = (e - s).Days + 1;
            return (s.AddDays(-days), s.AddDays(-1));
        }

        return kind switch {
            PeriodKind.Day => (s.AddDays(-1), s.AddDays(-1)),
            PeriodKind.Week => GetWeekRange(s.AddDays(-7)),
            PeriodKind.Month => GetMonthRange(new DateTime(s.Year, s.Month, 1).AddMonths(-1)),
            _ => StatsPeriod.Previous(kind, s, e),
        };
    }

    public static PeriodReportBundle BuildBundle(
        IEnumerable<Order> orders,
        PeriodKind kind,
        DateTime start,
        DateTime end) {
        var s = start.Date;
        var e = end.Date;
        if (s > e) (s, e) = (e, s);

        var isCustom = !IsStandardRange(kind, s, e);
        var (prevStart, prevEnd) = ResolvePreviousRange(kind, s, e);
        var allList = orders.ToList();
        var now = StatsCalculator.Compute(allList, s, e);
        var prev = StatsCalculator.Compute(allList, prevStart, prevEnd);

        var validOrders = now.Orders
            .Where(o => o.Status != OrderStatus.Canceled)
            .ToList();

        var shippedOrders = validOrders
            .Where(o => o.Status is OrderStatus.Shipping or OrderStatus.Delivered)
            .ToList();

        var issuedCount = shippedOrders.Count(o => !string.IsNullOrWhiteSpace(o.CertNo));
        var unissuedCount = shippedOrders.Count - issuedCount;
        var totalShippedBatteries = shippedOrders.Sum(o => o.BatteryCount);
        var unshippedOrders = Math.Max(0, now.OrderCount - shippedOrders.Count);
        var unshippedBatteries = Math.Max(0, now.BatteryCount - totalShippedBatteries);

        var avgPacks = now.OrderCount > 0 ? (double)now.PackCount / now.OrderCount : 0.0;
        var prevAvgPacks = prev.OrderCount > 0 ? (double)prev.PackCount / prev.OrderCount : 0.0;
        var avgBatteries = shippedOrders.Count > 0
            ? (double)totalShippedBatteries / shippedOrders.Count
            : 0.0;

        // 고객 정보 집계 (취소 제외, CustomerInfo.Normalize 기준)
        var normalized = validOrders
            .Select(o => (Order: o, Info: CustomerInfo.Normalize(o.CustomerType, o.AgeGroup, o.Region)))
            .ToList();

        var customerTypes = new List<CustomerSegmentRow>
        {
            new(
                Label: "개인",
                Orders: normalized.Count(x => x.Info.Type == CustomerInfo.Personal),
                Sales: normalized.Where(x => x.Info.Type == CustomerInfo.Personal).Sum(x => x.Order.Total)),
            new(
                Label: "기업",
                Orders: normalized.Count(x => x.Info.Type == CustomerInfo.Company),
                Sales: normalized.Where(x => x.Info.Type == CustomerInfo.Company).Sum(x => x.Order.Total)),
        };

        var ageGroups = CustomerInfo.AgeGroups
            .Select(code => new CustomerSegmentRow(
                Label: AgeLabel(code),
                Orders: normalized.Count(x => x.Info.Age == code),
                Sales: normalized.Where(x => x.Info.Age == code).Sum(x => x.Order.Total)))
            .ToList();

        var regionOrderIndex = CustomerInfo.Regions
            .Select((name, idx) => (name, idx))
            .ToDictionary(x => x.name, x => x.idx);

        var regions = normalized
            .GroupBy(x => x.Info.Region)
            .Select(g => new {
                Code = g.Key,
                Row = new CustomerSegmentRow(
                    Label: g.Key == CustomerInfo.None ? "선택 안 함" : g.Key,
                    Orders: g.Count(),
                    Sales: g.Sum(x => x.Order.Total)),
            })
            .OrderBy(x => x.Code == CustomerInfo.None ? 1 : 0)
            .ThenByDescending(x => x.Row.Sales)
            .ThenByDescending(x => x.Row.Orders)
            .ThenBy(x => regionOrderIndex.GetValueOrDefault(x.Code, int.MaxValue))
            .Select(x => x.Row)
            .ToList();

        return new PeriodReportBundle(
            Kind: kind,
            IsCustomRange: isCustom,
            Start: s,
            End: e,
            Now: now,
            Previous: prev,
            HasPreviousData: prev.Orders.Count > 0,
            ShippedOrderCount: shippedOrders.Count,
            UnshippedOrderCount: unshippedOrders,
            IssuedCertCount: issuedCount,
            UnissuedCertCount: unissuedCount,
            AvgPacksPerOrder: avgPacks,
            PrevAvgPacksPerOrder: prevAvgPacks,
            AvgBatteriesPerOrder: avgBatteries,
            TotalShippedBatteries: totalShippedBatteries,
            UnshippedBatteries: unshippedBatteries,
            CustomerTypes: customerTypes,
            AgeGroups: ageGroups,
            Regions: regions);
    }

    public static void PrintBundle(PeriodReportBundle bundle, string operatorName) {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var doc = BuildDocument(bundle, operatorName);
        doc.PageWidth = dlg.PrintableAreaWidth > 0 ? dlg.PrintableAreaWidth : PageWidth;
        doc.PageHeight = dlg.PrintableAreaHeight > 0 ? dlg.PrintableAreaHeight : PageHeight;
        doc.ColumnWidth = doc.PageWidth;

        var title = $"바른전지_주문보고서_{bundle.FileKindLabel}_{bundle.Start:yyyyMMdd}-{bundle.End:yyyyMMdd}";
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, title);
    }

    public static FlowDocument BuildDocument(PeriodReportBundle bundle, string operatorName) {
        var now = bundle.Now;
        var previous = bundle.Previous;
        var hasPrev = bundle.HasPreviousData;
        var kindLabel = bundle.KindLabel;
        var prevLabel = bundle.PreviousLabel;
        var writer = string.IsNullOrWhiteSpace(operatorName) ? "담당자" : operatorName.Trim();
        var today = DateTime.Today;
        var section = 1;

        var doc = new FlowDocument {
            FontFamily = Font,
            FontSize = 11.5,
            Foreground = Ink,
            Background = Brushes.White,
            PagePadding = new Thickness(44, 38, 44, 38),
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            ColumnWidth = PageWidth,
        };

        doc.Blocks.Add(new Paragraph(new Run($"바른전지 주문 관리 보고서 ({kindLabel})")) {
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3),
        });
        doc.Blocks.Add(new Paragraph(new Run("기간별 주문·매출·고객·출고·성적서 통합 보고서")) {
            Foreground = Sub,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 10),
        });
        doc.Blocks.Add(new Paragraph {
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Margin = new Thickness(0, 0, 0, 10),
        });

        doc.Blocks.Add(MakeMetaTable(new[]
        {
            new[] { "보고서 구분", kindLabel, "조회 기간", bundle.PeriodRangeText },
            new[] { "비교 기간", $"{prevLabel} ({bundle.PreviousRangeText})", "작성자", writer },
            new[] { "집계 기준", "접수일 기준 (취소 주문 매출·팩 제외)", "발행 시각", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Ko) },
        }));

        // ── 1. 기간 요약 (합계 · 평균 · 전기 대비 통합) ──
        Heading(doc, $"{section++}. 기간 요약 (합계 · 평균 · {prevLabel} 대비)");
        var cancelNowText = $"취소 {now.CancelCount:N0}건 / 전체 {now.Orders.Count:N0}건 ({now.CancelRate:0.0}%)";
        var cancelPrevText = hasPrev
            ? $"취소 {previous.CancelCount:N0}건 / 전체 {previous.Orders.Count:N0}건 ({previous.CancelRate:0.0}%)"
            : "-";

        var summaryRows = new[]
        {
            new[] { "주문 수 (취소 제외, 합계)", $"{now.OrderCount:N0}건", hasPrev ? $"{previous.OrderCount:N0}건" : "-", FormatIntDiff(now.OrderCount, previous.OrderCount, "건", hasPrev) },
            new[] { "매출 (합계)", $"{now.Sales:N0}원", hasPrev ? $"{previous.Sales:N0}원" : "-", FormatIntDiff(now.Sales, previous.Sales, "원", hasPrev) },
            new[] { "주문당 평균 매출", $"{now.AverageOrder:N0}원", hasPrev ? $"{previous.AverageOrder:N0}원" : "-", FormatIntDiff(now.AverageOrder, previous.AverageOrder, "원", hasPrev) },
            new[] { "판매 팩 수 (합계)", $"{now.PackCount:N0}팩", hasPrev ? $"{previous.PackCount:N0}팩" : "-", FormatIntDiff(now.PackCount, previous.PackCount, "팩", hasPrev) },
            new[] { "주문당 평균 팩 수", $"{bundle.AvgPacksPerOrder:0.0}팩", hasPrev ? $"{bundle.PrevAvgPacksPerOrder:0.0}팩" : "-", FormatDecimalDiff(bundle.AvgPacksPerOrder, bundle.PrevAvgPacksPerOrder, "팩", hasPrev) },
            new[] { "판매 건전지 수 (합계)", $"{now.BatteryCount:N0}개", hasPrev ? $"{previous.BatteryCount:N0}개" : "-", FormatIntDiff(now.BatteryCount, previous.BatteryCount, "개", hasPrev) },
            new[] { "취소 건수 / 취소율", cancelNowText, cancelPrevText, FormatRateDiff(now.CancelRate, previous.CancelRate, hasPrev) },
            new[] { "출고·배송 건수 (배송 중 + 완료)", $"{now.ShippedCount:N0}건", hasPrev ? $"{previous.ShippedCount:N0}건" : "-", FormatIntDiff(now.ShippedCount, previous.ShippedCount, "건", hasPrev) },
        };
        doc.Blocks.Add(MakeTable(
            new[] { "요약 항목", "선택 기간", prevLabel, $"{prevLabel} 대비" },
            summaryRows,
            new[] { 2.0, 1.8, 1.5, 1.5 },
            rightAlignFromCol: 1));
        if (!hasPrev)
            doc.Blocks.Add(Note($"※ 비교 기간({prevLabel})에 집계된 주문 자료가 없어 대비 수치를 '비교 자료 없음'으로 표시했습니다."));

        // ── 2. 출고 및 검사 성적서 현황 ──
        Heading(doc, $"{section++}. 출고 및 검사 성적서 현황");
        var issueRateText = bundle.ShippedOrderCount == 0
            ? "0.0%"
            : $"{100.0 * bundle.IssuedCertCount / bundle.ShippedOrderCount:0.0}%";
        var certRows = new[]
        {
            new[] { "판매 건전지 수 (취소 제외)", $"{now.BatteryCount:N0}개", "출고 건전지 수", $"{bundle.TotalShippedBatteries:N0}개" },
            new[] { "미출고 건전지 수 (출고 대기)", $"{bundle.UnshippedBatteries:N0}개", "출고 주문당 평균 건전지", $"{bundle.AvgBatteriesPerOrder:0.0}개" },
            new[] { "출고 주문 수 (배송 중 + 완료)", $"{bundle.ShippedOrderCount:N0}건", "미출고 주문 수 (접수·검사)", $"{bundle.UnshippedOrderCount:N0}건" },
            new[] { "성적서 발급 완료 / 미발급", $"{bundle.IssuedCertCount:N0}건 / {bundle.UnissuedCertCount:N0}건", "성적서 발급률", issueRateText },
        };
        doc.Blocks.Add(MakeTable(
            new[] { "항목", "값", "항목", "값" },
            certRows,
            new[] { 1.8, 1.3, 1.8, 1.4 },
            rightAlignFromCol: 1));

        // ── 3. 상품별 매출 ──
        Heading(doc, $"{section++}. 상품별 매출 (취소 제외)");
        doc.Blocks.Add(MakeProductTableWithBars(now));

        // ── 4. 고객 분석 (개인·기업 비율 / 연령대별 / 지역별) ──
        var customerSec = section++;
        Heading(doc, $"{customerSec}. 고객 분석 (취소 제외)");

        SubHeading(doc, $"{customerSec}-1. 개인 · 기업 비율");
        doc.Blocks.Add(MakeCustomerTypeTable(bundle.CustomerTypes, now.OrderCount, now.Sales));

        SubHeading(doc, $"{customerSec}-2. 연령대별 주문 수 · 매출");
        doc.Blocks.Add(MakeSegmentTable("연령대", bundle.AgeGroups, now.OrderCount, now.Sales, emptyMessage: "집계된 주문이 없습니다."));
        doc.Blocks.Add(Note("※ 연령대 미입력 주문과 기업 고객 주문은 '선택 안 함'으로 집계됩니다."));

        SubHeading(doc, $"{customerSec}-3. 지역별 주문 수 · 매출");
        doc.Blocks.Add(MakeSegmentTable("지역", bundle.Regions, now.OrderCount, now.Sales, emptyMessage: "집계된 지역 주문이 없습니다."));
        doc.Blocks.Add(Note("※ 주문이 있는 지역만 매출 순으로 표시하며, 지역 미입력 주문은 '선택 안 함'으로 집계됩니다."));

        // ── 5~6. 2일 이상: 일별 매출 그래프 + 기간 요약 표(월간=주차별, 주간=일별) ──
        if (bundle.Days > 1) {
            var scopeTag = bundle.IsCustomRange
                ? $"지정 기간 · {bundle.Days}일"
                : bundle.Kind == PeriodKind.Week ? "주간 · 월~일" : "월간";

            doc.Blocks.Add(MakeDailyChartSection($"{section++}. 일별 매출 그래프 ({scopeTag})", now.ByDay, today));

            if (bundle.Days > 14) {
                Heading(doc, $"{section++}. 주차별 주문·매출 요약 ({scopeTag})");
                doc.Blocks.Add(MakeWeeklySummaryTable(now, today));
            } else {
                Heading(doc, $"{section++}. 일별 주문·매출 표 ({scopeTag})");
                doc.Blocks.Add(MakeDailyTable(now, today));
            }
        }

        doc.Blocks.Add(new Paragraph(new Run(
            "※ 집계 기준: 접수일 기준(취소 주문은 매출·팩·건전지 수·고객 분석 합계에서 제외) · 발행일 이후 날짜는 '집계 전'으로 표시됩니다.")) {
            Foreground = Sub,
            FontSize = 9.5,
            Margin = new Thickness(0, 12, 0, 0),
        });

        return doc;
    }

    private static Table MakeProductTableWithBars(StatsResult now) {
        // 판매 중지 상품은 과거 매출이 있을 때만 "(판매 중지)" 표기로 노출
        var visibleProducts = now.ByProduct
            .Select(p => {
                var meta = Products.All.FirstOrDefault(x => x.Name == p.Name);
                var isDiscontinued = meta != null && !meta.OnSale;
                var displayName = meta != null ? meta.Label : p.Name;
                return (DisplayName: displayName, p.Packs, p.Sales, IsDiscontinued: isDiscontinued);
            })
            .Where(x => !x.IsDiscontinued || x.Packs > 0 || x.Sales > 0)
            .ToList();

        var totalSales = Math.Max(1, now.Sales);
        var maxSales = visibleProducts.Count > 0 ? Math.Max(1, visibleProducts.Max(p => p.Sales)) : 1;

        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 4),
        };
        t.Columns.Add(new TableColumn { Width = new GridLength(2.2, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(0.95, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.25, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(2.1, GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(
            new[] { "상품명", "판매 팩 수", "매출", "매출 비중" },
            head: true, highlight: false, muted: false, rightAlignFromCol: 1));

        foreach (var p in visibleProducts) {
            var pct = now.Sales == 0 ? 0.0 : 100.0 * p.Sales / totalSales;
            var ratio = Math.Clamp((double)p.Sales / maxSales, 0.0, 1.0);

            var row = new TableRow();
            row.Cells.Add(MakeTextCell(p.DisplayName, TextAlignment.Left, Font, p.IsDiscontinued ? Sub : Ink, FontWeights.Normal, isHead: false));
            row.Cells.Add(MakeTextCell($"{p.Packs:N0}팩", TextAlignment.Right, Mono, Ink, FontWeights.Normal, isHead: false));
            row.Cells.Add(MakeTextCell($"{p.Sales:N0}원", TextAlignment.Right, Mono, Ink, FontWeights.Normal, isHead: false));
            row.Cells.Add(MakeBarCell(ratio, pct, hasValue: p.Sales > 0));
            group.Rows.Add(row);
        }

        group.Rows.Add(MakeRow(
            new[] { "합계", $"{now.PackCount:N0}팩", $"{now.Sales:N0}원", now.Sales == 0 ? "0.0%" : "100.0%" },
            head: false, highlight: true, muted: false, rightAlignFromCol: 1));

        t.RowGroups.Add(group);
        return t;
    }

    private static Table MakeCustomerTypeTable(IReadOnlyList<CustomerSegmentRow> rows, int totalOrders, int totalSales) {
        var orderDenom = Math.Max(1, totalOrders);
        var salesDenom = Math.Max(1, totalSales);

        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 4),
        };
        t.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(0.9, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.9, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.25, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(0.95, GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(
            new[] { "고객 유형", "주문 수", "주문 비율", "매출", "매출 비중" },
            head: true, highlight: false, muted: false, rightAlignFromCol: 1));

        foreach (var r in rows) {
            var orderPct = totalOrders == 0 ? 0.0 : 100.0 * r.Orders / orderDenom;
            var salesPct = totalSales == 0 ? 0.0 : 100.0 * r.Sales / salesDenom;
            var barRatio = totalOrders == 0 ? 0.0 : Math.Clamp((double)r.Orders / orderDenom, 0.0, 1.0);

            var tr = new TableRow();
            tr.Cells.Add(MakeTextCell(r.Label, TextAlignment.Left, Font, Ink, FontWeights.Normal, isHead: false));
            tr.Cells.Add(MakeTextCell($"{r.Orders:N0}건", TextAlignment.Right, Mono, Ink, FontWeights.Normal, isHead: false));
            tr.Cells.Add(MakeBarCell(barRatio, orderPct, hasValue: r.Orders > 0));
            tr.Cells.Add(MakeTextCell($"{r.Sales:N0}원", TextAlignment.Right, Mono, Ink, FontWeights.Normal, isHead: false));
            tr.Cells.Add(MakeTextCell($"{salesPct:0.0}%", TextAlignment.Right, Mono, Ink, FontWeights.Normal, isHead: false));
            group.Rows.Add(tr);
        }

        group.Rows.Add(MakeRow(
            new[]
            {
                "합계",
                $"{totalOrders:N0}건",
                totalOrders == 0 ? "0.0%" : "100.0%",
                $"{totalSales:N0}원",
                totalSales == 0 ? "0.0%" : "100.0%",
            },
            head: false, highlight: true, muted: false, rightAlignFromCol: 1));

        t.RowGroups.Add(group);
        return t;
    }

    private static Table MakeSegmentTable(
        string firstHeader,
        IReadOnlyList<CustomerSegmentRow> rows,
        int totalOrders,
        int totalSales,
        string emptyMessage) {
        var salesDenom = Math.Max(1, totalSales);
        var maxSales = rows.Count > 0 ? Math.Max(1, rows.Max(r => r.Sales)) : 1;

        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 4),
        };
        t.Columns.Add(new TableColumn { Width = new GridLength(1.6, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.0, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(2.1, GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(
            new[] { firstHeader, "주문 수", "매출", "매출 비중" },
            head: true, highlight: false, muted: false, rightAlignFromCol: 1));

        if (rows.Count == 0) {
            group.Rows.Add(MakeRow(
                new[] { emptyMessage, "-", "-", "-" },
                head: false, highlight: false, muted: true, rightAlignFromCol: 1));
        } else {
            foreach (var r in rows) {
                var pct = totalSales == 0 ? 0.0 : 100.0 * r.Sales / salesDenom;
                var ratio = Math.Clamp((double)r.Sales / maxSales, 0.0, 1.0);
                var isNone = r.Label == "선택 안 함";

                var tr = new TableRow();
                tr.Cells.Add(MakeTextCell(r.Label, TextAlignment.Left, Font, isNone ? Sub : Ink, FontWeights.Normal, isHead: false));
                tr.Cells.Add(MakeTextCell($"{r.Orders:N0}건", TextAlignment.Right, Mono, Ink, FontWeights.Normal, isHead: false));
                tr.Cells.Add(MakeTextCell($"{r.Sales:N0}원", TextAlignment.Right, Mono, Ink, FontWeights.Normal, isHead: false));
                tr.Cells.Add(MakeBarCell(ratio, pct, hasValue: r.Sales > 0));
                group.Rows.Add(tr);
            }
        }

        group.Rows.Add(MakeRow(
            new[] { "합계", $"{totalOrders:N0}건", $"{totalSales:N0}원", totalSales == 0 ? "0.0%" : "100.0%" },
            head: false, highlight: true, muted: false, rightAlignFromCol: 1));

        t.RowGroups.Add(group);
        return t;
    }

    private static TableCell MakeBarCell(double ratio, double pct, bool hasValue) {
        var barRow = new Grid { VerticalAlignment = VerticalAlignment.Center };
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46, GridUnitType.Pixel) });

        var track = new Border {
            Height = 11,
            Background = BarTrack,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, ratio), GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 1.0 - ratio), GridUnitType.Star) });
        if (hasValue) {
            var fill = new Border { Background = BarFill, CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(fill, 0);
            inner.Children.Add(fill);
        }
        track.Child = inner;

        var pctText = new TextBlock {
            Text = $"{pct:0.0}%",
            FontFamily = Mono,
            FontSize = 10.5,
            Foreground = Ink,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(track, 0);
        Grid.SetColumn(pctText, 1);
        barRow.Children.Add(track);
        barRow.Children.Add(pctText);

        return new TableCell(new BlockUIContainer(barRow) { Margin = new Thickness(0) }) {
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
        };
    }

    private static BlockUIContainer MakeDailyChartSection(string headingText, IReadOnlyList<DayRow> days, DateTime today) {
        var wrapper = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        wrapper.Children.Add(new TextBlock {
            Text = headingText,
            FontFamily = Font,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = Ink,
            Margin = new Thickness(0, 0, 0, 5),
        });

        if (days.Count == 0) {
            wrapper.Children.Add(new TextBlock {
                Text = "표시할 일별 데이터가 없습니다.",
                FontFamily = Font,
                FontSize = 10.5,
                Foreground = Sub,
            });
            return new BlockUIContainer(wrapper) { Margin = new Thickness(0) };
        }

        if (days.Count > MaxDailyChartDays) {
            wrapper.Children.Add(new TextBlock {
                Text = $"※ 조회 기간이 {days.Count}일({MaxDailyChartDays}일 초과)이어서 일별 막대 그래프는 생략하고 아래 표로 표시합니다.",
                FontFamily = Font,
                FontSize = 10.5,
                Foreground = Sub,
                TextWrapping = TextWrapping.Wrap,
            });
            return new BlockUIContainer(wrapper) { Margin = new Thickness(0) };
        }

        var maxSales = Math.Max(1, days.Max(d => d.Sales));
        var hasAnySales = days.Any(d => d.Sales > 0);
        var isCompact = days.Count > 14;
        var barMaxHeight = isCompact ? 82.0 : 104.0;
        var labelTopHeight = isCompact ? 13.0 : 22.0;

        var outerBorder = new Border {
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 8, 8, 6),
            Background = Brushes.White,
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Pixel) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 왼쪽 Y축 금액 눈금 (최대 / 절반 / 0원)
        var yAxis = new Grid {
            Margin = new Thickness(0, labelTopHeight, 6, 16),
            Height = barMaxHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        yAxis.Children.Add(new TextBlock {
            Text = hasAnySales ? FormatAxisWon(maxSales) : "-",
            FontFamily = Mono,
            FontSize = 8.5,
            Foreground = Sub,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        });
        yAxis.Children.Add(new TextBlock {
            Text = hasAnySales ? FormatAxisWon(maxSales / 2) : "",
            FontFamily = Mono,
            FontSize = 8.5,
            Foreground = Sub,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        yAxis.Children.Add(new TextBlock {
            Text = "0원",
            FontFamily = Mono,
            FontSize = 8.5,
            Foreground = Sub,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        Grid.SetColumn(yAxis, 0);
        layout.Children.Add(yAxis);

        // 오른쪽 막대 영역
        var chartGrid = new Grid();
        for (var i = 0; i < days.Count; i++)
            chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < days.Count; i++) {
            var d = days[i];
            var isFuture = d.Date > today;
            var ratio = isFuture ? 0.0 : Math.Clamp((double)d.Sales / maxSales, 0.0, 1.0);
            var barHeight = (!isFuture && d.Sales > 0) ? Math.Max(4.0, barMaxHeight * ratio) : 0.0;

            var colStack = new StackPanel {
                Margin = new Thickness(isCompact ? 0.5 : 2, 0, isCompact ? 0.5 : 2, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            // 막대 위 금액 라벨 (월간 15일 초과는 겹침 방지를 위해 만원 단위 반올림 축약)
            var topLabel = "";
            if (!isFuture && d.Sales > 0)
                topLabel = isCompact ? FormatUltraCompactWon(d.Sales) : $"{FormatCompactWon(d.Sales)}\n({d.Orders}건)";
            else if (isFuture && !isCompact)
                topLabel = "집계 전";

            colStack.Children.Add(new TextBlock {
                Text = topLabel,
                FontFamily = Mono,
                FontSize = isCompact ? 6.5 : 8.5,
                Foreground = isFuture ? Muted : Ink,
                TextAlignment = TextAlignment.Center,
                Height = labelTopHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
            });

            var chartArea = new Border {
                Height = barMaxHeight,
                Background = isFuture ? FutureTrack : BarTrack,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            var innerGrid = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            innerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0.0, barMaxHeight - barHeight), GridUnitType.Pixel) });
            innerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(barHeight, GridUnitType.Pixel) });

            if (barHeight > 0) {
                var bar = new Border {
                    Background = BarFill,
                    CornerRadius = new CornerRadius(2, 2, 0, 0),
                };
                Grid.SetRow(bar, 1);
                innerGrid.Children.Add(bar);
            }
            chartArea.Child = innerGrid;
            colStack.Children.Add(chartArea);

            colStack.Children.Add(new TextBlock {
                Text = isCompact ? d.Date.Day.ToString(CultureInfo.InvariantCulture) : $"{d.Date:MM/dd}",
                FontFamily = Mono,
                FontSize = isCompact ? 8.5 : 9.5,
                Foreground = isFuture ? Muted : Ink,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            });

            Grid.SetColumn(colStack, i);
            chartGrid.Children.Add(colStack);
        }

        Grid.SetColumn(chartGrid, 1);
        layout.Children.Add(chartGrid);
        outerBorder.Child = layout;
        wrapper.Children.Add(outerBorder);

        if (isCompact) {
            wrapper.Children.Add(new TextBlock {
                Text = "※ 막대 상단 숫자는 반올림 축약 표기(만원·천원 단위)이며, 정확한 금액은 아래 요약 표를 참고하세요.",
                FontFamily = Font,
                FontSize = 9.0,
                Foreground = Sub,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        return new BlockUIContainer(wrapper) { Margin = new Thickness(0) };
    }

    private static Table MakeWeeklySummaryTable(StatsResult now, DateTime today) {
        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var widths = new[] { 0.85, 1.75, 1.0, 1.45, 0.95, 0.85 };
        foreach (var w in widths)
            t.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(
            new[] { "주차", "기간", "주문 수", "매출", "판매 팩", "취소" },
            head: true, highlight: false, muted: false, rightAlignFromCol: 2));

        var days = now.ByDay;
        for (var idx = 0; idx < days.Count; idx += 7) {
            var slice = days.Skip(idx).Take(7).ToList();
            var weekNo = (idx / 7) + 1;
            var wStart = slice[0].Date;
            var wEnd = slice[^1].Date;
            var allFuture = wStart > today;
            var isOngoing = !allFuture && wEnd > today;
            var rangeText = wStart == wEnd ? $"{wStart:MM-dd}" : $"{wStart:MM-dd} ~ {wEnd:MM-dd}";
            if (isOngoing) rangeText += " (진행 중)";

            if (allFuture) {
                group.Rows.Add(MakeRow(
                    new[] { $"{weekNo}주차", rangeText, "집계 전", "-", "-", "-" },
                    head: false, highlight: false, muted: true, rightAlignFromCol: 2));
            } else {
                var validSlice = slice.Where(d => d.Date <= today).ToList();
                group.Rows.Add(MakeRow(
                    new[]
                    {
                        $"{weekNo}주차",
                        rangeText,
                        $"{validSlice.Sum(d => d.Orders):N0}건",
                        $"{validSlice.Sum(d => d.Sales):N0}원",
                        $"{validSlice.Sum(d => d.Packs):N0}팩",
                        $"{validSlice.Sum(d => d.Canceled):N0}건",
                    },
                    head: false, highlight: false, muted: false, rightAlignFromCol: 2));
            }
        }

        group.Rows.Add(MakeRow(
            new[] { "합계", "", $"{now.OrderCount:N0}건", $"{now.Sales:N0}원", $"{now.PackCount:N0}팩", $"{now.CancelCount:N0}건" },
            head: false, highlight: true, muted: false, rightAlignFromCol: 2));

        t.RowGroups.Add(group);
        return t;
    }

    private static Table MakeDailyTable(StatsResult now, DateTime today) {
        var t = new Table {
            CellSpacing = 0,
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var widths = new[] { 1.5, 0.8, 1.0, 1.5, 1.0, 0.9 };
        foreach (var w in widths)
            t.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(
            new[] { "날짜", "요일", "주문 수", "매출", "판매 팩", "취소" },
            head: true, highlight: false, muted: false, rightAlignFromCol: 2));

        foreach (var d in now.ByDay) {
            var isFuture = d.Date > today;
            if (isFuture) {
                group.Rows.Add(MakeRow(
                    new[] { d.Date.ToString("yyyy-MM-dd", Ko), d.Date.ToString("ddd", Ko), "집계 전", "-", "-", "-" },
                    head: false, highlight: false, muted: true, rightAlignFromCol: 2));
            } else {
                group.Rows.Add(MakeRow(
                    new[]
                    {
                        d.Date.ToString("yyyy-MM-dd", Ko),
                        d.Date.ToString("ddd", Ko),
                        $"{d.Orders:N0}건",
                        $"{d.Sales:N0}원",
                        $"{d.Packs:N0}팩",
                        $"{d.Canceled:N0}건",
                    },
                    head: false, highlight: false, muted: false, rightAlignFromCol: 2));
            }
        }

        group.Rows.Add(MakeRow(
            new[] { "합계", "", $"{now.OrderCount:N0}건", $"{now.Sales:N0}원", $"{now.PackCount:N0}팩", $"{now.CancelCount:N0}건" },
            head: false, highlight: true, muted: false, rightAlignFromCol: 2));

        t.RowGroups.Add(group);
        return t;
    }

    private static string AgeLabel(string code) => code switch {
        "10s" => "10대",
        "20s" => "20대",
        "30s" => "30대",
        "40s" => "40대",
        "50s+" => "50대 이상",
        _ => "선택 안 함",
    };

    private static string FormatAxisWon(int amount) {
        if (amount >= 100_000_000) return $"{amount / 100_000_000.0:0.#}억원";
        if (amount >= 10_000) return $"{amount / 10_000.0:0.#}만원";
        return $"{amount:N0}원";
    }

    private static string FormatCompactWon(int amount) {
        if (amount >= 100_000_000) return $"{amount / 100_000_000.0:0.#}억";
        if (amount >= 10_000) return $"{amount / 10_000.0:0.#}만";
        if (amount >= 1_000) return $"{amount / 1_000.0:0.#}천";
        return $"{amount:N0}";
    }

    private static string FormatUltraCompactWon(int amount) {
        if (amount >= 100_000_000) return $"{Math.Round(amount / 100_000_000.0):0}억";
        if (amount >= 10_000) return $"{Math.Round(amount / 10_000.0):0}만";
        if (amount >= 1_000) return $"{Math.Round(amount / 1_000.0):0}천";
        return $"{amount:N0}";
    }

    private static (DateTime Start, DateTime End) GetWeekRange(DateTime d) {
        var offsetFromMonday = ((int)d.DayOfWeek + 6) % 7;
        var monday = d.Date.AddDays(-offsetFromMonday);
        return (monday, monday.AddDays(6));
    }

    private static (DateTime Start, DateTime End) GetMonthRange(DateTime d) {
        var first = new DateTime(d.Year, d.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    internal static string FormatPeriodRange(DateTime start, DateTime end) {
        if (start.Date == end.Date)
            return $"{start:yyyy-MM-dd} ({start.ToString("ddd", Ko)})";
        return $"{start:yyyy-MM-dd} ({start.ToString("ddd", Ko)}) ~ {end:yyyy-MM-dd} ({end.ToString("ddd", Ko)})";
    }

    internal static string KindLabel(PeriodKind kind) => kind switch {
        PeriodKind.Week => "주간",
        PeriodKind.Month => "월간",
        _ => "일일",
    };

    internal static string PreviousLabel(PeriodKind kind) => kind switch {
        PeriodKind.Week => "전주",
        PeriodKind.Month => "지난달",
        _ => "전일",
    };

    private static string FormatIntDiff(int nowVal, int prevVal, string unit, bool hasPrevData) {
        if (!hasPrevData) return "비교 자료 없음";
        return StatsCalculator.DiffText(nowVal, prevVal, unit);
    }

    private static string FormatRateDiff(double nowRate, double prevRate, bool hasPrevData) {
        if (!hasPrevData) return "비교 자료 없음";
        var diff = nowRate - prevRate;
        if (Math.Abs(diff) < 0.05) return "±0.0%p";
        var sign = diff > 0 ? "+" : "−";
        return $"{sign}{Math.Abs(diff):0.0}%p";
    }

    private static string FormatDecimalDiff(double nowVal, double prevVal, string unit, bool hasPrevData) {
        if (!hasPrevData) return "비교 자료 없음";
        var diff = nowVal - prevVal;
        if (Math.Abs(diff) < 0.05) return $"±0.0{unit}";
        var sign = diff > 0 ? "+" : "−";
        return $"{sign}{Math.Abs(diff):0.0}{unit}";
    }

    private static void Heading(FlowDocument doc, string text) =>
        doc.Blocks.Add(new Paragraph(new Run(text)) {
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 12, 0, 5),
            KeepWithNext = true,
        });

    private static void SubHeading(FlowDocument doc, string text) =>
        doc.Blocks.Add(new Paragraph(new Run(text)) {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Sub,
            Margin = new Thickness(0, 8, 0, 4),
            KeepWithNext = true,
        });

    private static Paragraph Note(string text) =>
        new(new Run(text)) {
            Foreground = Sub,
            FontSize = 9.5,
            TextAlignment = TextAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4),
        };

    private static Table MakeMetaTable(string[][] rows) {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };
        t.Columns.Add(new TableColumn { Width = new GridLength(0.9, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(2.2, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(0.9, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(2.2, GridUnitType.Star) });

        var group = new TableRowGroup();
        foreach (var r in rows) {
            var tr = new TableRow();
            for (var i = 0; i < r.Length; i++) {
                var isLabel = i % 2 == 0;
                tr.Cells.Add(new TableCell(new Paragraph(new Run(r[i])) {
                    Margin = new Thickness(0),
                    FontFamily = isLabel ? Font : Mono,
                    TextAlignment = TextAlignment.Left,
                }) {
                    Padding = new Thickness(4, 3, 4, 3),
                    Foreground = isLabel ? Sub : Ink,
                    FontSize = isLabel ? 10.5 : 11,
                });
            }
            group.Rows.Add(tr);
        }
        t.RowGroups.Add(group);
        return t;
    }

    private static Table MakeTable(
        string[] headers,
        string[][] rows,
        double[] widths,
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
        group.Rows.Add(MakeRow(headers, head: true, highlight: false, muted: false, rightAlignFromCol: rightAlignFromCol));

        for (var idx = 0; idx < rows.Length; idx++) {
            var isLast = highlightLastRow && idx == rows.Length - 1;
            group.Rows.Add(MakeRow(rows[idx], head: false, highlight: isLast, muted: false, rightAlignFromCol: rightAlignFromCol));
        }

        t.RowGroups.Add(group);
        return t;
    }

    private static TableRow MakeRow(
        string[] cells,
        bool head,
        bool highlight,
        bool muted,
        int rightAlignFromCol) {
        var row = new TableRow();
        if (head) row.Background = HeadFill;
        else if (highlight) row.Background = SoftFill;

        var fg = muted ? Muted : Ink;
        var weight = (head || highlight) ? FontWeights.SemiBold : FontWeights.Normal;

        for (var i = 0; i < cells.Length; i++) {
            var align = i >= rightAlignFromCol ? TextAlignment.Right : TextAlignment.Left;
            var family = (!head && i >= rightAlignFromCol) ? Mono : Font;
            row.Cells.Add(MakeTextCell(cells[i], align, family, fg, weight, head));
        }
        return row;
    }

    private static TableCell MakeTextCell(
        string text,
        TextAlignment align,
        FontFamily family,
        Brush fg,
        FontWeight weight,
        bool isHead) =>
        new(new Paragraph(new Run(text)) {
            Margin = new Thickness(0),
            TextAlignment = align,
            FontFamily = family,
        }) {
            BorderBrush = Line,
            BorderThickness = new Thickness(0, isHead ? 0 : 1, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Foreground = fg,
            FontWeight = weight,
            FontSize = 10.5,
        };
}