using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BareunOrder.Core.Models;
using BareunOrder.Feature.Stats;

namespace BareunOrder.Feature.Certificate;

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
    int ShippedOrderCount,
    int IssuedCertCount,
    int UnissuedCertCount,
    double AvgPacksPerOrder,
    double PrevAvgPacksPerOrder,
    double AvgBatteriesPerOrder,
    int TotalShippedBatteries) {
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
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE1));
    private static readonly Brush HeadFill = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
    private static readonly Brush SoftFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xFA));
    private static readonly Brush BarFill = new SolidColorBrush(Color.FromRgb(0x1D, 0x5F, 0xD1));
    private static readonly Brush BarTrack = new SolidColorBrush(Color.FromRgb(0xEA, 0xEE, 0xF4));

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

        var shippedOrders = now.Orders
            .Where(o => o.Status is OrderStatus.Shipping or OrderStatus.Delivered)
            .ToList();

        var issuedCount = shippedOrders.Count(o => !string.IsNullOrWhiteSpace(o.CertNo));
        var unissuedCount = shippedOrders.Count - issuedCount;
        var totalShippedBatteries = shippedOrders.Sum(o => o.BatteryCount);

        var avgPacks = now.OrderCount > 0 ? (double)now.PackCount / now.OrderCount : 0.0;
        var prevAvgPacks = prev.OrderCount > 0 ? (double)prev.PackCount / prev.OrderCount : 0.0;
        var avgBatteries = shippedOrders.Count > 0
            ? (double)totalShippedBatteries / shippedOrders.Count
            : 0.0;

        return new PeriodReportBundle(
            Kind: kind,
            IsCustomRange: isCustom,
            Start: s,
            End: e,
            Now: now,
            Previous: prev,
            ShippedOrderCount: shippedOrders.Count,
            IssuedCertCount: issuedCount,
            UnissuedCertCount: unissuedCount,
            AvgPacksPerOrder: avgPacks,
            PrevAvgPacksPerOrder: prevAvgPacks,
            AvgBatteriesPerOrder: avgBatteries,
            TotalShippedBatteries: totalShippedBatteries);
    }

    public static void PrintBundle(PeriodReportBundle bundle, string operatorName = "관리자") {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var doc = BuildDocument(bundle, operatorName);
        doc.PageWidth = dlg.PrintableAreaWidth > 0 ? dlg.PrintableAreaWidth : PageWidth;
        doc.PageHeight = dlg.PrintableAreaHeight > 0 ? dlg.PrintableAreaHeight : PageHeight;
        doc.ColumnWidth = doc.PageWidth;

        var title = $"바른전지_주문보고서_{bundle.FileKindLabel}_{bundle.Start:yyyyMMdd}-{bundle.End:yyyyMMdd}";
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, title);
    }

    public static FlowDocument BuildDocument(PeriodReportBundle bundle, string operatorName = "관리자") {
        var now = bundle.Now;
        var previous = bundle.Previous;
        var kindLabel = bundle.KindLabel;
        var prevLabel = bundle.PreviousLabel;
        var section = 1;

        var doc = new FlowDocument {
            FontFamily = Font,
            FontSize = 11.5,
            Foreground = Ink,
            Background = Brushes.White,
            PagePadding = new Thickness(44, 40, 44, 40),
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            ColumnWidth = PageWidth,
        };

        doc.Blocks.Add(new Paragraph(new Run($"바른전지 주문 관리 보고서 ({kindLabel})")) {
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3),
        });
        doc.Blocks.Add(new Paragraph(new Run("기간별 주문·매출·출고·성적서 통합 보고서")) {
            Foreground = Sub,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 10),
        });
        doc.Blocks.Add(new Paragraph {
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Margin = new Thickness(0, 0, 0, 12),
        });

        doc.Blocks.Add(MakeMetaTable(new[]
        {
            new[] { "보고서 구분", kindLabel, "조회 기간", bundle.PeriodRangeText },
            new[] { "비교 기간", $"{prevLabel} ({bundle.PreviousRangeText})", "작성자", operatorName },
            new[] { "집계 기준", "접수일 기준 (취소 주문 매출·팩 제외)", "발행 시각", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Ko) },
        }));

        // ── 기간 요약(합계·평균·출고) ──
        Heading(doc, $"{section++}. 기간 요약 (합계 · 평균)");
        var summaryRows = new[]
        {
            new[] { "주문 수 (취소 제외, 합계)", $"{now.OrderCount:N0}건", $"{previous.OrderCount:N0}건", StatsCalculator.DiffText(now.OrderCount, previous.OrderCount, "건") },
            new[] { "매출 (합계)", $"{now.Sales:N0}원", $"{previous.Sales:N0}원", StatsCalculator.DiffText(now.Sales, previous.Sales, "원") },
            new[] { "주문당 평균 매출", $"{now.AverageOrder:N0}원", $"{previous.AverageOrder:N0}원", StatsCalculator.DiffText(now.AverageOrder, previous.AverageOrder, "원") },
            new[] { "판매 팩 수 (합계)", $"{now.PackCount:N0}팩", $"{previous.PackCount:N0}팩", StatsCalculator.DiffText(now.PackCount, previous.PackCount, "팩") },
            new[] { "주문당 평균 팩 수", $"{bundle.AvgPacksPerOrder:0.0}팩", $"{bundle.PrevAvgPacksPerOrder:0.0}팩", FormatDecimalDiff(bundle.AvgPacksPerOrder, bundle.PrevAvgPacksPerOrder, "팩") },
            new[] { "판매 건전지 수 (합계)", $"{now.BatteryCount:N0}개", $"{previous.BatteryCount:N0}개", StatsCalculator.DiffText(now.BatteryCount, previous.BatteryCount, "개") },
            new[] { "취소 건수 / 취소율", $"{now.CancelCount:N0}건 ({now.CancelRate:0.0}%)", $"{previous.CancelCount:N0}건 ({previous.CancelRate:0.0}%)", FormatRateDiff(now.CancelRate, previous.CancelRate) },
            new[] { "출고·배송 건수 (배송 중 + 배송 완료)", $"{now.ShippedCount:N0}건", $"{previous.ShippedCount:N0}건", StatsCalculator.DiffText(now.ShippedCount, previous.ShippedCount, "건") },
        };
        doc.Blocks.Add(MakeTable(
            new[] { "요약 항목", "선택 기간", prevLabel, "전기 대비" },
            summaryRows,
            new[] { 2.3, 1.4, 1.4, 1.4 },
            rightAlignFromCol: 1));

        // ── 성적서 발급 현황 요약(평균·합계) ──
        Heading(doc, $"{section++}. 검사 성적서 현황 (출고 주문 기준)");
        var certRows = new[]
        {
            new[] { "기간 내 출고 주문 수", $"{bundle.ShippedOrderCount:N0}건", "성적서 발급 완료", $"{bundle.IssuedCertCount:N0}건" },
            new[] { "성적서 미발급 건수", $"{bundle.UnissuedCertCount:N0}건", "출고 건전지 총합", $"{bundle.TotalShippedBatteries:N0}개" },
            new[] { "출고 주문당 평균 건전지", $"{bundle.AvgBatteriesPerOrder:0.0}개", "성적서 발급률", bundle.ShippedOrderCount == 0 ? "0.0%" : $"{100.0 * bundle.IssuedCertCount / bundle.ShippedOrderCount:0.0}%" },
        };
        doc.Blocks.Add(MakeTable(
            new[] { "성적서 항목", "값", "성적서 항목", "값" },
            certRows,
            new[] { 1.7, 1.4, 1.7, 1.5 },
            rightAlignFromCol: 1));

        // ── 상품별 매출 표 + 막대 그래프 ──
        Heading(doc, $"{section++}. 상품별 매출 (취소 제외)");
        var totalSales = Math.Max(1, now.Sales);
        var productRows = now.ByProduct
            .Select(p => new[]
            {
                p.Name,
                $"{p.Packs:N0}팩",
                $"{p.Sales:N0}원",
                now.Sales == 0 ? "0.0%" : $"{100.0 * p.Sales / totalSales:0.0}%",
            })
            .ToList();
        productRows.Add(new[]
        {
            "합계",
            $"{now.PackCount:N0}팩",
            $"{now.Sales:N0}원",
            now.Sales == 0 ? "0.0%" : "100.0%",
        });
        doc.Blocks.Add(MakeTable(
            new[] { "상품명", "판매 팩 수", "매출", "매출 비중" },
            productRows.ToArray(),
            new[] { 2.6, 1.2, 1.5, 1.2 },
            rightAlignFromCol: 1,
            highlightLastRow: true));
        doc.Blocks.Add(MakeProductChartBlock(now.ByProduct));

        // ── 월간 보고서 전용: 지난달 비교 ──
        if (!bundle.IsCustomRange && bundle.Kind == PeriodKind.Month) {
            Heading(doc, $"{section++}. 지난달 비교 (월간)");
            var lastMonthRows = new[]
            {
                new[] { "주문 수 (취소 제외)", $"{now.OrderCount:N0}건", $"{previous.OrderCount:N0}건", StatsCalculator.DiffText(now.OrderCount, previous.OrderCount, "건") },
                new[] { "매출", $"{now.Sales:N0}원", $"{previous.Sales:N0}원", StatsCalculator.DiffText(now.Sales, previous.Sales, "원") },
                new[] { "판매 팩 수", $"{now.PackCount:N0}팩", $"{previous.PackCount:N0}팩", StatsCalculator.DiffText(now.PackCount, previous.PackCount, "팩") },
                new[] { "취소율", $"{now.CancelRate:0.0}%", $"{previous.CancelRate:0.0}%", FormatRateDiff(now.CancelRate, previous.CancelRate) },
                new[] { "출고·배송 건수", $"{now.ShippedCount:N0}건", $"{previous.ShippedCount:N0}건", StatsCalculator.DiffText(now.ShippedCount, previous.ShippedCount, "건") },
            };
            doc.Blocks.Add(MakeTable(
                new[] { "비교 지표", "이번 달", "지난달", "지난달 대비" },
                lastMonthRows,
                new[] { 2.3, 1.3, 1.3, 1.6 },
                rightAlignFromCol: 1));
        }

        // ── 2일 이상(주간·월간·지정 기간): 일별 그래프 + 일별 표 ──
        if (bundle.Days > 1) {
            var scopeTag = bundle.IsCustomRange
                ? $"지정 기간 · {bundle.Days}일"
                : bundle.Kind == PeriodKind.Week ? "주간 · 월~일" : "월간";

            Heading(doc, $"{section++}. 일별 매출 그래프 ({scopeTag})");
            doc.Blocks.Add(MakeDailyChartBlock(now.ByDay));

            Heading(doc, $"{section++}. 일별 주문·매출 표 ({scopeTag})");
            var dayRows = now.ByDay
                .Select(d => new[]
                {
                    d.Date.ToString("yyyy-MM-dd", Ko),
                    d.Date.ToString("ddd", Ko),
                    $"{d.Orders:N0}건",
                    $"{d.Sales:N0}원",
                    $"{d.Packs:N0}팩",
                    $"{d.Canceled:N0}건",
                })
                .ToList();

            dayRows.Add(new[]
            {
                "합계",
                "",
                $"{now.OrderCount:N0}건",
                $"{now.Sales:N0}원",
                $"{now.PackCount:N0}팩",
                $"{now.CancelCount:N0}건",
            });

            doc.Blocks.Add(MakeTable(
                new[] { "날짜", "요일", "주문 수", "매출", "판매 팩", "취소" },
                dayRows.ToArray(),
                new[] { 1.5, 0.8, 1.0, 1.5, 1.0, 0.9 },
                rightAlignFromCol: 2,
                highlightLastRow: true));
        }

        doc.Blocks.Add(new Paragraph(new Run(
            "※ 일별 표와 그래프는 주문이 없는 날짜도 0으로 포함합니다. PDF 저장은 인쇄 창에서 'Microsoft Print to PDF'를 선택해 주세요.")) {
            Foreground = Sub,
            FontSize = 10,
            Margin = new Thickness(0, 14, 0, 0),
        });

        return doc;
    }

    private static BlockUIContainer MakeProductChartBlock(IReadOnlyList<ProductRow> products) {
        if (products.Count == 0) {
            return new BlockUIContainer(new TextBlock {
                Text = "표시할 상품 데이터가 없습니다.",
                FontFamily = Font,
                FontSize = 10.5,
                Foreground = Sub,
            }) { Margin = new Thickness(0, 2, 0, 6) };
        }

        var maxSales = Math.Max(1, products.Max(p => p.Sales));
        var container = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };

        foreach (var p in products) {
            var ratio = Math.Clamp((double)p.Sales / maxSales, 0.0, 1.0);
            var rowGrid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });

            var nameTb = new TextBlock {
                Text = p.Name,
                FontFamily = Font,
                FontSize = 10.5,
                Foreground = Ink,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var trackBorder = new Border {
                Height = 14,
                Background = BarTrack,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(6, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, ratio), GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 1.0 - ratio), GridUnitType.Star) });

            if (p.Sales > 0) {
                var fillBorder = new Border {
                    Background = BarFill,
                    CornerRadius = new CornerRadius(2),
                };
                Grid.SetColumn(fillBorder, 0);
                barGrid.Children.Add(fillBorder);
            }
            trackBorder.Child = barGrid;

            var valTb = new TextBlock {
                Text = $"{p.Sales:N0}원 ({p.Packs}팩)",
                FontFamily = Mono,
                FontSize = 10.5,
                Foreground = Ink,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(nameTb, 0);
            Grid.SetColumn(trackBorder, 1);
            Grid.SetColumn(valTb, 2);

            rowGrid.Children.Add(nameTb);
            rowGrid.Children.Add(trackBorder);
            rowGrid.Children.Add(valTb);
            container.Children.Add(rowGrid);
        }

        return new BlockUIContainer(container) { Margin = new Thickness(0, 2, 0, 6) };
    }

    private static BlockUIContainer MakeDailyChartBlock(IReadOnlyList<DayRow> days) {
        if (days.Count == 0) {
            return new BlockUIContainer(new TextBlock {
                Text = "표시할 일별 데이터가 없습니다.",
                FontFamily = Font,
                FontSize = 10.5,
                Foreground = Sub,
            }) { Margin = new Thickness(0, 2, 0, 6) };
        }

        if (days.Count > MaxDailyChartDays) {
            return new BlockUIContainer(new TextBlock {
                Text = $"※ 조회 기간이 {days.Count}일({MaxDailyChartDays}일 초과)이어서 일별 막대 그래프는 생략하고 아래 일별 표로 표시합니다.",
                FontFamily = Font,
                FontSize = 10.5,
                Foreground = Sub,
                TextWrapping = TextWrapping.Wrap,
            }) { Margin = new Thickness(0, 2, 0, 6) };
        }

        var maxSales = Math.Max(1, days.Max(d => d.Sales));
        var isCompact = days.Count > 14;
        var barMaxHeight = isCompact ? 68.0 : 92.0;

        var outerBorder = new Border {
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 8, 8, 6),
            Background = Brushes.White,
        };

        var grid = new Grid();
        for (var i = 0; i < days.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < days.Count; i++) {
            var d = days[i];
            var ratio = Math.Clamp((double)d.Sales / maxSales, 0.0, 1.0);
            var barHeight = d.Sales > 0 ? Math.Max(4.0, barMaxHeight * ratio) : 0.0;

            var colStack = new StackPanel {
                Margin = new Thickness(isCompact ? 0.5 : 2, 0, isCompact ? 0.5 : 2, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            if (!isCompact) {
                colStack.Children.Add(new TextBlock {
                    Text = d.Orders > 0 ? $"{d.Orders}건" : "0",
                    FontFamily = Mono,
                    FontSize = 9,
                    Foreground = Sub,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2),
                });
            }

            var chartArea = new Border {
                Height = barMaxHeight,
                Background = BarTrack,
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
                Foreground = Ink,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            });

            Grid.SetColumn(colStack, i);
            grid.Children.Add(colStack);
        }

        outerBorder.Child = grid;
        return new BlockUIContainer(outerBorder) { Margin = new Thickness(0, 2, 0, 6) };
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

    private static string FormatRateDiff(double nowRate, double prevRate) {
        var diff = nowRate - prevRate;
        if (Math.Abs(diff) < 0.05) return "±0.0%p";
        var sign = diff > 0 ? "+" : "−";
        return $"{sign}{Math.Abs(diff):0.0}%p";
    }

    private static string FormatDecimalDiff(double nowVal, double prevVal, string unit) {
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
        });

    private static Table MakeMetaTable(string[][] rows) {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };
        t.Columns.Add(new TableColumn { Width = new GridLength(1.0, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(2.1, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.0, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(2.1, GridUnitType.Star) });

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
        group.Rows.Add(MakeRow(headers, head: true, highlight: false, rightAlignFromCol: rightAlignFromCol));

        for (var idx = 0; idx < rows.Length; idx++) {
            var isLast = highlightLastRow && idx == rows.Length - 1;
            group.Rows.Add(MakeRow(rows[idx], head: false, highlight: isLast, rightAlignFromCol: rightAlignFromCol));
        }

        t.RowGroups.Add(group);
        return t;
    }

    private static TableRow MakeRow(
        string[] cells,
        bool head,
        bool highlight,
        int rightAlignFromCol) {
        var row = new TableRow();
        if (head) row.Background = HeadFill;
        else if (highlight) row.Background = SoftFill;

        for (var i = 0; i < cells.Length; i++) {
            var align = i >= rightAlignFromCol ? TextAlignment.Right : TextAlignment.Left;

            var p = new Paragraph(new Run(cells[i])) {
                Margin = new Thickness(0),
                TextAlignment = align,
                FontFamily = (!head && i >= rightAlignFromCol) ? Mono : Font,
            };

            row.Cells.Add(new TableCell(p) {
                BorderBrush = Line,
                BorderThickness = new Thickness(0, head ? 0 : 1, 0, 0),
                Padding = new Thickness(6, 4, 6, 4),
                Foreground = Ink,
                FontWeight = (head || highlight) ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = 11,
            });
        }
        return row;
    }
}