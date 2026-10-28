using System.Windows;
using System.Windows.Controls;

namespace HandPegApp;

/// <summary>
/// Parts laid side by side while there is room for them, and one over the other (each the whole width) when there is
/// not: a tab's columns, in a pane narrower than the tab bar they were laid out for. Side by side, each part is as wide
/// as it wants to be, and the one marked <see cref="FillProperty"/> (the last, when none is) has what is left; they stay
/// side by side while the other parts, the gaps and the filling part's least width fit (its MinWidth when it has one,
/// otherwise the width it wants). A part that does not fit is never cut off at the edge.
/// </summary>
public sealed class ReflowPanel : Panel
{
    /// <summary>The gap between two parts side by side.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ReflowPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The gap between two parts one over the other.</summary>
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ReflowPanel), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Marks the part that takes the room the others leave, side by side (the last part, when none is marked).</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(bool), typeof(ReflowPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetFill(UIElement element) => (bool)element.GetValue(FillProperty);

    public static void SetFill(UIElement element, bool value) => element.SetValue(FillProperty, value);

    /// <summary>Whether the parts stood one over the other at the last measure.</summary>
    public bool IsStacked { get; private set; }

    private List<UIElement> Parts() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private static UIElement FillOf(List<UIElement> parts) => parts.FirstOrDefault(GetFill) ?? parts[^1];

    protected override Size MeasureOverride(Size availableSize)
    {
        var parts = Parts();
        if (parts.Count == 0)
            return default;

        // What each part wants with no limit; the filling one counts with its least width.
        foreach (var part in parts)
            part.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var fill = FillOf(parts);
        var least = fill is FrameworkElement { MinWidth: > 0 } sized ? sized.MinWidth + sized.Margin.Left + sized.Margin.Right : fill.DesiredSize.Width;
        var others = parts.Where(p => !ReferenceEquals(p, fill)).Sum(p => p.DesiredSize.Width) + Spacing * (parts.Count - 1);

        IsStacked = others + least > availableSize.Width;
        if (!IsStacked)
        {
            fill.Measure(new Size(double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width - others, availableSize.Height));
            return new Size(others + fill.DesiredSize.Width, parts.Max(p => p.DesiredSize.Height));
        }

        // One over the other, each as wide as the panel.
        foreach (var part in parts)
            part.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(parts.Max(p => p.DesiredSize.Width), parts.Sum(p => p.DesiredSize.Height) + RowSpacing * (parts.Count - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var parts = Parts();
        if (parts.Count == 0)
            return finalSize;

        if (IsStacked)
        {
            var y = 0.0;
            foreach (var part in parts)
            {
                part.Arrange(new Rect(0, y, finalSize.Width, part.DesiredSize.Height));
                y += part.DesiredSize.Height + RowSpacing;
            }

            return finalSize;
        }

        var fill = FillOf(parts);
        var rest = Math.Max(finalSize.Width - parts.Where(p => !ReferenceEquals(p, fill)).Sum(p => p.DesiredSize.Width) - Spacing * (parts.Count - 1), 0);
        var x = 0.0;
        foreach (var part in parts)
        {
            var width = ReferenceEquals(part, fill) ? rest : part.DesiredSize.Width;
            part.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }
}

/// <summary>
/// A least width that gives way: what it holds is at least <see cref="Value"/> wide where the room is there (a column
/// sized to its content, a wide pane), and only as wide as the room where it is not (a narrow pane), instead of being
/// cut off at the edge as a MinWidth would have it. Without a child it only holds its column open.
/// </summary>
public sealed class SoftMinWidth : Decorator
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SoftMinWidth), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var wanted = Math.Min(Value, constraint.Width);
        if (Child is not { } child)
            return new Size(wanted, 0);

        child.Measure(constraint);
        return new Size(Math.Min(Math.Max(child.DesiredSize.Width, wanted), constraint.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
