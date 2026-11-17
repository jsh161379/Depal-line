using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BareunOrder.Core.Models;

namespace BareunOrder.Core.Converters;

/// <summary>
/// 주문 상태 → 색. ConverterParameter="soft"면 옅은 배경색.
/// 상태 색은 모든 탭에서 같게 쓴다 (작업자가 색만 보고 알 수 있게).
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static readonly Dictionary<OrderStatus, (SolidColorBrush Strong, SolidColorBrush Soft)> Map = new()
    {
        [OrderStatus.Received] = (B("#1D5FD1"), B("#E3ECFB")),
        [OrderStatus.Inspect] = (B("#B86E00"), B("#FCF0DC")),
        [OrderStatus.Shipping] = (B("#0F7C8C"), B("#DDF1F3")),
        [OrderStatus.Delivered] = (B("#1F8A4C"), B("#E0F2E7")),
        [OrderStatus.Canceled] = (B("#7A8494"), B("#ECEEF1")),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not OrderStatus s || !Map.TryGetValue(s, out var c)) return Brushes.Transparent;
        return parameter as string == "soft" ? c.Soft : c.Strong;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>0이면 Collapsed (배지·빈 목록 안내에 씀). ConverterParameter="invert"면 반대.</summary>
public sealed class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var zero = value is int i ? i == 0 : value == null;
        if (parameter as string == "invert") zero = !zero;
        return zero ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null이면 Collapsed. ConverterParameter="invert"면 반대 (선택 안 했을 때 안내 문구).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value == null;
        if (parameter as string == "invert") isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
