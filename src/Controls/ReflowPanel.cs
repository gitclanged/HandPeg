using System.Windows;
using System.Windows.Controls;

namespace HandPegApp;

/// <summary>
/// Lays children horizontally while they fit, otherwise stacks them vertically at full width (e.g. a tab's columns in a pane narrower
/// than the layout they were designed for). Horizontally, each child gets its desired width and the <see cref="FillProperty"/> child
/// (the last when none is marked) takes the remainder; it stays horizontal while the other children, gaps and the fill child's minimum
/// (its MinWidth if set, else its desired width) fit. Children are never clipped at the edge.
/// </summary>
public sealed class ReflowPanel : Panel
{
    /// <summary>Horizontal gap between children in row layout.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ReflowPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Vertical gap between children in stacked layout.</summary>
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ReflowPanel), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Marks the child that takes the remaining width in row layout (defaults to the last child).</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(bool), typeof(ReflowPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetFill(UIElement element) => (bool)element.GetValue(FillProperty);

    public static void SetFill(UIElement element, bool value) => element.SetValue(FillProperty, value);

    /// <summary>True when the last measure chose stacked layout.</summary>
    public bool IsStacked { get; private set; }

    private List<UIElement> Parts() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private static UIElement FillOf(List<UIElement> parts) => parts.FirstOrDefault(GetFill) ?? parts[^1];

    protected override Size MeasureOverride(Size availableSize)
    {
        var parts = Parts();
        if (parts.Count == 0)
            return default;

        // Unconstrained desired widths; the fill child counts at its minimum width.
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

        // Stacked: each child at full panel width.
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
/// Soft minimum width: the child is at least <see cref="Value"/> wide when space allows (auto-sized column, wide pane), but shrinks
/// to the available width otherwise (narrow pane) instead of being clipped as with MinWidth. Without a child it just reserves the
/// column width.
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
