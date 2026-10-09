using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HandPegApp;

/// <summary>
/// Turns a position along something (0 to 1) and that thing's width into a pixel offset.
/// Used to place the I-frame lines on the timeline.
/// </summary>
/// <summary>Shows something while a condition does not hold: visible for false, collapsed for true.</summary>
public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FractionToOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [double fraction, double width] ? fraction * width : DependencyProperty.UnsetValue;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
