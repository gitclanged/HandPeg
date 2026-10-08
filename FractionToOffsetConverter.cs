using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HandPegApp;

/// <summary>
/// Turns a position along something (0 to 1) and that thing's width into a pixel offset.
/// Used to place the keyframe lines on the timeline.
/// </summary>
public sealed class FractionToOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [double fraction, double width] ? fraction * width : DependencyProperty.UnsetValue;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
