using System.Windows.Controls;

namespace BareunOrder.Feature.Shipping;

public partial class ShippingView : UserControl
{
    public ShippingView() => InitializeComponent();

    private void OnOrderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ShippingRow row && DataContext is ShippingViewModel vm)
            vm.Selected = row;
    }
}
