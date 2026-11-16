using System.Windows;
using System.Windows.Controls;

namespace HandPegApp.Views;

/// <summary>
/// Fixed-aspect decorator: takes the available width and sets height to width x <see cref="Ratio"/>. Media Browser tiles stretch to
/// fill their columns while their pictures keep 16:9 at the resulting width.
/// </summary>
public sealed class AspectBox : Decorator
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(AspectBox),
        new FrameworkPropertyMetadata(9.0 / 16, FrameworkPropertyMetadataOptions.AffectsMeasure), ratio => ratio is double r && r > 0);

    /// <summary>Height / width ratio (default 9/16).</summary>
    public double Ratio
    {
        get => (double)GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Unconstrained width: fall back to the child's desired size.
        if (double.IsInfinity(constraint.Width))
        {
            Child?.Measure(constraint);
            return Child?.DesiredSize ?? default;
        }

        var size = new Size(constraint.Width, Math.Round(constraint.Width * Ratio));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
