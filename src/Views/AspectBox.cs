using System.Windows;
using System.Windows.Controls;

namespace HandPegApp.Views;

/// <summary>
/// Holds its child at a fixed shape: as wide as it is given, and <see cref="Ratio"/> of that high. The media browser's
/// tiles are stretched to fill their columns (5.3.1), and their pictures keep 16:9 at whatever width that gives them.
/// </summary>
public sealed class AspectBox : Decorator
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(AspectBox),
        new FrameworkPropertyMetadata(9.0 / 16, FrameworkPropertyMetadataOptions.AffectsMeasure), ratio => ratio is double r && r > 0);

    /// <summary>Height over width.</summary>
    public double Ratio
    {
        get => (double)GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Given no width (measured on its own), it falls back to what its child wants.
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
