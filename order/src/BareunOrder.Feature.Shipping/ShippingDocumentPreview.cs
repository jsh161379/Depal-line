using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;

namespace BareunOrder.Feature.Shipping;

/// <summary>종이 전체를 축소하지 않고 문서를 큰 글자로 창 너비에 맞춰 보여 주는 미리보기.</summary>
public sealed class ShippingDocumentPreview : DockPanel
{
    public FlowDocumentScrollViewer Viewer { get; }

    public ShippingDocumentPreview(FlowDocument document, Action print, int count)
    {
        Background = new SolidColorBrush(Color.FromRgb(238, 240, 243));
        TextElement.SetFontFamily(this, new FontFamily("Malgun Gothic"));
        var toolbar = new WrapPanel { Margin = new Thickness(16, 12, 16, 8) };
        var zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 52,
            TextAlignment = TextAlignment.Center, FontSize = 14 };
        Viewer = new FlowDocumentScrollViewer { Document = document, IsToolBarVisible = false,
            Zoom = 100, MinZoom = 75, MaxZoom = 175, Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, BorderThickness = new Thickness(0) };
        var minus = Button("−", () => Viewer.Zoom = Math.Max(Viewer.MinZoom, Viewer.Zoom - 10));
        var plus = Button("＋", () => Viewer.Zoom = Math.Min(Viewer.MaxZoom, Viewer.Zoom + 10));
        var printButton = Button("인쇄 / PDF 저장", print);
        printButton.FontWeight = FontWeights.SemiBold;
        toolbar.Children.Add(minus);
        toolbar.Children.Add(zoomText);
        toolbar.Children.Add(plus);
        toolbar.Children.Add(printButton);
        toolbar.Children.Add(new TextBlock { Text = $"총 {count}건 · 아래로 스크롤해서 확인하세요.",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 13 });
        zoomText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Viewer.Zoom)) { Source = Viewer, StringFormat = "{0:0}%" });
        var head = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(205, 212, 222)),
            BorderThickness = new Thickness(0, 0, 0, 1), Child = toolbar };
        SetDock(head, Dock.Top);
        Children.Add(head);
        Children.Add(Viewer);
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, FontSize = 14, MinHeight = 38,
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 4) };
        button.Click += (_, _) => action();
        return button;
    }
}
