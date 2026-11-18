using System.Windows.Controls;
using System.Windows.Input;
using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Customers;

public partial class CustomersView : UserControl
{
    public CustomersView() => InitializeComponent();

    /// <summary>주문 이력에서 두 번 누르면 주문 탭에서 연다</summary>
    private void OnOrderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Order o } && DataContext is CustomersViewModel vm)
            vm.OpenOrderCommand.Execute(o);
    }

    /// <summary>고객 화면의 문의를 두 번 누르면 문의 목록에서 연다</summary>
    private void OnInquiryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Inquiry q } && DataContext is CustomersViewModel vm)
            vm.ShowInquiryCommand.Execute(q);
    }
}
