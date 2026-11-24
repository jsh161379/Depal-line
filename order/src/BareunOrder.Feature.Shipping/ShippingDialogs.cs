using System.Windows;

namespace BareunOrder.Feature.Shipping;

public interface IShippingDialogs
{
    bool Confirm(string text);
    void Show(string text);
}

public sealed class ShippingDialogs : IShippingDialogs
{
    public bool Confirm(string text) => MessageBox.Show(text, "출고 확인",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public void Show(string text) => MessageBox.Show(text, "출고·배송",
        MessageBoxButton.OK, MessageBoxImage.Information);
}
