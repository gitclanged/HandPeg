using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Holds still a time that changes many times a second (the position by the Sequence Overview Scrubber and in the playback
/// bar, the caption editor's time, the voiceover's count; 5.3.1): its TextBlock's digits all one width (tabular figures),
/// and the readout never narrower than the widest text of the same length and face it has shown, so neither the readout
/// nor what sits beside it moves sideways during playback. A text of another length (the frames format, an hour reached)
/// is measured afresh. A decorator, as TextBlock's own measure cannot be overridden.
/// </summary>
public sealed class TimeReadout : Decorator
{
    private (int Length, double Size, FontFamily? Face) _shape = (-1, 0, null);
    private double _widest;

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
            _shape = (-1, 0, null);
            return size;
        }

        var shape = child is TextBlock text ? (TextLength(text), text.FontSize, text.FontFamily) : (0, 0, null);
        if (shape != _shape)
            (_shape, _widest) = (shape, 0);

        _widest = Math.Max(_widest, size.Width);
        return new Size(Math.Min(_widest, constraint.Width), size.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    // The characters shown: the text, or its runs' (the playback bar's position and duration are runs of one readout).
    private static int TextLength(TextBlock text)
    {
        if (text.Inlines.Count == 0)
            return text.Text.Length;

        var length = 0;
        foreach (var inline in text.Inlines)
            length += inline is Run run ? run.Text.Length : 1;
        return length;
    }
}
