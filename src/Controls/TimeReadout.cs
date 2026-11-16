using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Width-stable decorator for rapidly changing time readouts (Overview Scrubber and playback bar position, Subtitle Studio time,
/// Voiceover Studio counter): forces tabular figures on its TextBlock and never measures narrower than <see cref="ReserveText"/> in that
/// font (plus <see cref="Spare"/>) or than the widest text shown so far, so frame-digit growth (e.g. two to three digits above 100 fps)
/// or a format switch never shifts the readout or its neighbours during playback. A font change, or collapse and re-show, resets to the
/// reserve. Implemented as a decorator because TextBlock's measure cannot be overridden.
/// </summary>
public sealed class TimeReadout : Decorator
{
    private (double Size, FontFamily? Face, FontWeight Weight) _shape = (-1, null, default);
    private double _widest;

    /// <summary>Minimum-width template: a twelve-character time (HH:MM:SS:FFF or HH:MM:SS.mmm).</summary>
    public string ReserveText { get; set; } = "00:00:00:000";

    /// <summary>Extra width beyond the text in DIPs (the scrubber's readouts use 8).</summary>
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

    // Measures ReserveText in the TextBlock's font family, size and weight with tabular figures.
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
