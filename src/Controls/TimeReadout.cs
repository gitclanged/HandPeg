using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Holds still a time that changes many times a second (the position by the Sequence Overview Scrubber and in the playback
/// bar, the caption editor's time, the voiceover's count; 5.3.1): its TextBlock's digits all one width (tabular figures),
/// and the readout never narrower than <see cref="ReserveText"/> in its face (plus <see cref="Spare"/>) nor than the widest
/// text it has shown, whatever its length (5.3.2: a frame number going from two digits to three above 100 fps, or the format
/// switched, moved the scrubber beside it), so neither the readout nor what sits beside it moves sideways during playback.
/// Another face or size, or collapsed and shown again, starts again from the reserve. A decorator, as TextBlock's own measure
/// cannot be overridden.
/// </summary>
public sealed class TimeReadout : Decorator
{
    private (double Size, FontFamily? Face, FontWeight Weight) _shape = (-1, null, default);
    private double _widest;

    /// <summary>The text whose width the readout always keeps room for: a twelve-character time (HH:MM:SS:FFF or HH:MM:SS.mmm).</summary>
    public string ReserveText { get; set; } = "00:00:00:000";

    /// <summary>Room kept beyond the text, in DIPs (the scrubber's readouts keep 8, 5.3.2).</summary>
    public double Spare { get; set; }

    protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        if (added is TextBlock text)
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
            return default;

        child.Measure(constraint);
        var size = child.DesiredSize;
        if (child.Visibility == Visibility.Collapsed)
        {
            _shape = (-1, null, default);
            return size;
        }

        var text = child as TextBlock;
        var shape = text is null ? (0, null, default) : (text.FontSize, text.FontFamily, text.FontWeight);
        if (shape != _shape)
            (_shape, _widest) = (shape, text is null ? 0 : Reserved(text) + Spare);

        _widest = Math.Max(_widest, size.Width + Spare);
        return new Size(Math.Min(_widest, constraint.Width), size.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    // The reserve's width in the TextBlock's face, size and weight, with tabular figures as the TextBlock has them.
    private double Reserved(TextBlock text)
    {
        var reserve = new TextBlock
        {
            Text = ReserveText, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
            FontStyle = text.FontStyle, FontStretch = text.FontStretch,
        };
        Typography.SetNumeralAlignment(reserve, FontNumeralAlignment.Tabular);
        reserve.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return reserve.DesiredSize.Width;
    }
}
