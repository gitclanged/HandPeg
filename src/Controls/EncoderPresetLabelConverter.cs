using System.Globalization;
using System.Windows.Data;

namespace HandPegApp;

/// <summary>
/// Display label for an encoder speed preset in the Encoder Preset box. The stored value stays the raw FFmpeg token ("p1",
/// "speed") as presets and projects persist it; only the label is prettified: AMF tokens title-cased, NVENC p1/p7 annotated
/// as fastest/highest quality, and AMF high_quality marked slowest.
/// </summary>
public sealed class EncoderPresetLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "p1" => "p1 (Fastest)",
        "p7" => "p7 (Highest Quality)",
        "speed" => "Speed",
        "balanced" => "Balanced",
        "quality" => "Quality",
        "high_quality" => "High Quality (Slowest)",
        _ => value,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
