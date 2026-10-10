using System.Globalization;
using System.Windows.Data;

namespace HandPegApp;

/// <summary>
/// An encoder's speed step as the Encoder Preset box shows it. The step itself is the word FFmpeg takes
/// ("p1", "speed"), which is what presets and projects store; only what is read on screen is dressed up:
/// AMF's steps get a capital, and NVENC's two ends say which end they are.
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
        _ => value,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
