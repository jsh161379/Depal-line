using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Shipping;

public sealed record ShippingDocumentData(string No, DateTime ReceivedAt, string Name, string Phone,
    string Address, string Memo, string Courier, string Tracking, string Status,
    IReadOnlyList<OrderItem> Items, int Total, int BatteryCount, ShippingReviewRecord? Review = null)
{
    public static ShippingDocumentData From(ShippingRow row) => new(row.Order.No, row.Order.ReceivedAt,
        row.Order.Name, row.Order.Phone, row.Order.Address, row.Order.Memo, row.Courier,
        row.NormalizedTracking, row.Order.StatusText, row.Order.Items.ToArray(), row.Order.Total, row.Order.BatteryCount,
        ShippingReviewRecord.From(row.Checklist));
}

/// <summary>출고 지시서·거래명세서 A4 미리보기와 Windows 인쇄. PDF 프린터도 동일하게 사용한다.</summary>
public static class ShippingDocuments
{
    public static void Preview(IReadOnlyList<ShippingRow> rows, bool statement, string staff, bool demo)
    {
        if (rows.Count == 0) return;
        var title = statement ? "거래명세서" : "출고 지시서";
        var when = DateTime.Now;
        var work = SystemParameters.WorkArea;
        var window = new Window { Title = $"{title} 미리보기 · {rows.Count}건",
            Width = Math.Min(1120, work.Width - 40), Height = Math.Min(800, work.Height - 40),
            MinWidth = Math.Min(680, work.Width - 40), MinHeight = Math.Min(500, work.Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow, Background = new SolidColorBrush(Color.FromRgb(238, 240, 243)) };
        void Print()
        {
            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return;
                var copy = Build(rows.Select(ShippingDocumentData.From).ToArray(), statement, staff, demo, when);
                if (dialog.PrintableAreaWidth > 0) copy.PageWidth = dialog.PrintableAreaWidth;
                if (dialog.PrintableAreaHeight > 0) copy.PageHeight = dialog.PrintableAreaHeight;
                copy.ColumnWidth = copy.PageWidth;
                dialog.PrintDocument(((IDocumentPaginatorSource)copy).DocumentPaginator, $"{title}_{when:yyyyMMdd_HHmm}");
            }
            catch (Exception ex) { MessageBox.Show(window, "인쇄하지 못했습니다. " + ex.Message, title); }
        }
        window.Content = statement
            ? new ShippingDocumentPreview(BuildPreview(rows.Select(ShippingDocumentData.From).ToArray(), true, staff, demo, when), Print, rows.Count)
            : new ShippingInstructionReview(rows, staff, demo, Print, window.Close, when);
        window.ShowDialog();
    }

    public static FlowDocument BuildInstructionPreview(IReadOnlyList<ShippingRow> rows,
        string staff, bool demo, DateTime? issuedAt = null, Action? onAllCompleted = null)
    {
        var document = BuildPreview(rows.Select(ShippingDocumentData.From).ToArray(), false, staff, demo, issuedAt);
        var sections = document.Blocks.OfType<Section>().ToArray();
        for (var i = 0; i < sections.Length; i++)
        {
            // 화면에서는 인쇄용 확인란 대신 조작 가능한 주문별 체크리스트를 표시한다.
            foreach (var paragraph in sections[i].Blocks.OfType<Paragraph>().ToArray())
            {
                var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
                if (text.StartsWith("확인 항목") || text.StartsWith("출고 담당") || text.StartsWith("출고 지시서 확인"))
                    sections[i].Blocks.Remove(paragraph);
            }
            sections[i].Blocks.Add(new BlockUIContainer(new ShippingChecklistPanel(rows[i], staff, () =>
            {
                if (rows.All(r => r.Order.Status != OrderStatus.Inspect || r.Checklist.IsComplete))
                    onAllCompleted?.Invoke();
            })));
        }
        return document;
    }

    /// <summary>화면은 창 너비에 맞춰 흐르게 표시한다. 인쇄용 A4 문서와 분리해 작은 배율·빈 종이 여백을 없앤다.</summary>
    public static FlowDocument BuildPreview(IReadOnlyList<ShippingDocumentData> orders, bool statement,
        string staff, bool demo, DateTime? issuedAt = null)
    {
        var document = Build(orders, statement, staff, demo, issuedAt);
        document.PageWidth = double.NaN;
        document.PageHeight = double.NaN;
        document.ColumnWidth = 10000;
        document.PagePadding = new Thickness(28, 24, 28, 24);
        document.Background = Brushes.Transparent;
        document.FontSize = 16;
        foreach (var section in document.Blocks.OfType<Section>())
        {
            section.BreakPageBefore = false;
            section.Margin = new Thickness(0, 0, 0, 28);
            foreach (var paragraph in section.Blocks.OfType<Paragraph>())
            {
                paragraph.LineHeight = 26;
                if (paragraph.FontSize > 20) paragraph.FontSize = 30;
            }
            foreach (var table in section.Blocks.OfType<Table>())
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                        {
                            cell.Padding = new Thickness(12, 10, 12, 10);
                            cell.Background = cell.FontWeight == FontWeights.Bold
                                ? new SolidColorBrush(Color.FromRgb(220, 226, 234)) : Brushes.Transparent;
                            cell.BorderBrush = new SolidColorBrush(Color.FromRgb(190, 199, 211));
                        }
        }
        return document;
    }

    public static FlowDocument Build(IReadOnlyList<ShippingDocumentData> orders, bool statement,
        string staff, bool demo, DateTime? issuedAt = null)
    {
        var date = issuedAt ?? DateTime.Now;
        var document = new FlowDocument { FontFamily = new FontFamily("Malgun Gothic"), FontSize = 12,
            PageWidth = 793.7, PageHeight = 1122.5, ColumnWidth = 793.7,
            PagePadding = new Thickness(48), Background = Brushes.White, Foreground = Brushes.Black };
        for (var i = 0; i < orders.Count; i++)
        {
            var order = orders[i];
            var section = new Section { BreakPageBefore = i > 0 };
            section.Blocks.Add(new Paragraph(new Run(statement ? "거래명세서" : "출고 지시서"))
            { FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
            section.Blocks.Add(P($"바른전지  |  발행 {date:yyyy-MM-dd HH:mm}  |  담당 {staff}"));
            if (demo) section.Blocks.Add(P("연습 출력 · 예시 주문", true));
            section.Blocks.Add(P($"주문번호  {order.No}", true));
            section.Blocks.Add(P($"접수  {order.ReceivedAt:yyyy-MM-dd HH:mm}  |  상태  {order.Status}"));
            section.Blocks.Add(P($"받는 분  {order.Name}  |  연락처  {order.Phone}"));
            section.Blocks.Add(P($"배송지  {order.Address}"));
            section.Blocks.Add(P($"택배사  {order.Courier}  |  송장  {(string.IsNullOrEmpty(order.Tracking) ? "미입력" : order.Tracking)}"));
            if (!string.IsNullOrWhiteSpace(order.Memo)) section.Blocks.Add(P($"요청 사항  {order.Memo}"));

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 16, 0, 12) };
            double[] widths = statement ? new[] { 2.8, 0.7, 1.0, 1.2 } : new[] { 3.5, 1.0, 1.2 };
            foreach (var width in widths) table.Columns.Add(new TableColumn { Width = new GridLength(width, GridUnitType.Star) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            AddRow(group, statement ? new[] { "상품", "팩 수", "단가", "금액" } : new[] { "상품", "팩 수", "건전지 수" }, true);
            foreach (var item in order.Items)
                AddRow(group, statement ? new[] { item.Name, item.Qty.ToString("N0"), $"{item.Price:N0}원", $"{item.Amount:N0}원" }
                    : new[] { item.Name, item.Qty.ToString("N0"), $"{item.BatteryCount:N0}개" }, false);
            section.Blocks.Add(table);
            section.Blocks.Add(P($"합계  {order.Items.Sum(x => x.Qty):N0}팩 · 건전지 {order.BatteryCount:N0}개"
                + (statement ? $"  |  주문 금액 {order.Total:N0}원" : ""), true));
            if (!statement)
            {
                var review = order.Review;
                section.Blocks.Add(P($"확인 항목  상품·수량 [{(review?.Product == true ? "완료" : "미확인")}]  포장 [{(review?.Packaging == true ? "완료" : "미확인")}]  송장·배송지 [{(review?.Destination == true ? "완료" : "미확인")}]"));
                section.Blocks.Add(P(review?.IsComplete == true
                    ? $"출고 지시서 확인 완료  {review.By} · {review.At:yyyy-MM-dd HH:mm}"
                    : "출고 지시서 확인 미완료", true));
            }
            section.Blocks.Add(P("출고 담당 ____________________    확인 ____________________"));
            document.Blocks.Add(section);
        }
        return document;
    }

    private static Paragraph P(string text, bool bold = false) => new(new Run(text))
    { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 8), LineHeight = 19 };

    private static void AddRow(TableRowGroup group, IReadOnlyList<string> values, bool header)
    {
        var row = new TableRow();
        for (var i = 0; i < values.Count; i++)
            row.Cells.Add(new TableCell(new Paragraph(new Run(values[i])) { Margin = new Thickness(0),
                TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right })
            { Padding = new Thickness(8), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5),
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                Background = header ? Brushes.WhiteSmoke : Brushes.White });
        group.Rows.Add(row);
    }
}
