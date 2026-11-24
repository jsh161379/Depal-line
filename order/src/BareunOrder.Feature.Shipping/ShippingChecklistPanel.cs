using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace BareunOrder.Feature.Shipping;

/// <summary>출고 지시서 안에서 실제로 조작하는 주문별 체크리스트.</summary>
public sealed class ShippingChecklistPanel : Border
{
    public ShippingChecklistPanel(ShippingRow row, string staff, Action? onCompleted = null)
    {
        Background = new SolidColorBrush(Color.FromRgb(227, 236, 251));
        BorderBrush = new SolidColorBrush(Color.FromRgb(190, 207, 235));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(18);
        Margin = new Thickness(0, 8, 0, 8);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "출고 전 확인 · " + row.Order.No,
            FontSize = 17, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "아래 3개를 체크하고 ‘확인 완료’를 누르면 이 주문을 출고할 수 있습니다.",
            FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        void AddCheck(string label, string property)
        {
            var check = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                FontSize = 15, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3) };
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Source = row.Checklist, Mode = BindingMode.TwoWay });
            check.SetBinding(IsEnabledProperty, new Binding(nameof(ShippingChecklist.CanEdit)) { Source = row.Checklist });
            body.Children.Add(check);
        }
        AddCheck($"상품·수량이 맞습니다. ({row.Order.PackCount}팩 · 건전지 {row.Order.BatteryCount}개)", nameof(ShippingChecklist.ProductChecked));
        AddCheck("포장 상태와 파손 여부를 확인했습니다.", nameof(ShippingChecklist.PackagingChecked));
        AddCheck("송장번호와 받는 분·배송지가 맞습니다.", nameof(ShippingChecklist.DestinationChecked));
        var actions = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var complete = new Button { Content = "확인 완료", FontSize = 15, MinHeight = 40, Padding = new Thickness(22, 6, 22, 6) };
        complete.SetBinding(IsEnabledProperty, new Binding(nameof(ShippingChecklist.CanComplete)) { Source = row.Checklist });
        complete.Click += (_, _) =>
        {
            if (row.Checklist.Complete(staff)) onCompleted?.Invoke();
        };
        DockPanel.SetDock(complete, Dock.Right);
        actions.Children.Add(complete);
        var status = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShippingChecklist.StatusText)) { Source = row.Checklist });
        actions.Children.Add(status);
        body.Children.Add(actions);
        Child = body;
    }
}
