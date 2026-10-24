using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Lets the children of a vertical panel be put in another order by dragging them by a grip (an element inside a child
/// marked <see cref="IsGripProperty"/>): while dragging, a line shows where the child would go, and letting go moves it
/// there. The panel then raises <see cref="Reordered"/>, for whoever keeps the order. Built once, for the Summary
/// cards, and meant for any list of the same kind (an effect stack, the ordered lists of Settings).
/// </summary>
public static class ReorderBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ReorderBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(Panel panel) => (bool)panel.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Panel panel, bool value) => panel.SetValue(IsEnabledProperty, value);

    /// <summary>Marks what a child is taken hold of by to drag it: its header, say. Elsewhere in the child the mouse is its own.</summary>
    public static readonly DependencyProperty IsGripProperty = DependencyProperty.RegisterAttached(
        "IsGrip", typeof(bool), typeof(ReorderBehavior), new PropertyMetadata(false));

    public static bool GetIsGrip(DependencyObject element) => (bool)element.GetValue(IsGripProperty);

    public static void SetIsGrip(DependencyObject element, bool value) => element.SetValue(IsGripProperty, value);

    /// <summary>A panel's children were put in another order by dragging.</summary>
    public static event Action<Panel>? Reordered;

    // The drag under way: the panel, the child, where the press was, whether it has moved far enough to be a drag.
    private static (Panel Panel, UIElement Child, Point Start, bool Moving, InsertionLine? Line)? _drag;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel)
            return;

        panel.PreviewMouseLeftButtonDown -= Panel_PreviewMouseLeftButtonDown;
        panel.PreviewMouseMove -= Panel_PreviewMouseMove;
        panel.PreviewMouseLeftButtonUp -= Panel_PreviewMouseLeftButtonUp;
        panel.LostMouseCapture -= Panel_LostMouseCapture;
        if (e.NewValue is not true)
            return;

        panel.PreviewMouseLeftButtonDown += Panel_PreviewMouseLeftButtonDown;
        panel.PreviewMouseMove += Panel_PreviewMouseMove;
        panel.PreviewMouseLeftButtonUp += Panel_PreviewMouseLeftButtonUp;
        panel.LostMouseCapture += Panel_LostMouseCapture;
    }

    private static void Panel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Panel panel || e.OriginalSource is not DependencyObject source)
            return;

        // A grip, and the child of the panel it is in.
        var onGrip = false;
        for (var element = source; element is not null && !ReferenceEquals(element, panel); element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element))
        {
            onGrip |= GetIsGrip(element);
            if (onGrip && element is UIElement child && panel.Children.Contains(child))
            {
                _drag = (panel, child, e.GetPosition(panel), false, null);
                return;
            }
        }
    }

    private static void Panel_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } drag || !ReferenceEquals(sender, drag.Panel))
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            End(commit: false);
            return;
        }

        var point = e.GetPosition(drag.Panel);
        if (!drag.Moving)
        {
            if (Math.Abs(point.Y - drag.Start.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
                return;

            var line = AdornerLayer.GetAdornerLayer(drag.Panel) is { } layer ? new InsertionLine(drag.Panel) : null;
            if (line is not null)
                AdornerLayer.GetAdornerLayer(drag.Panel)!.Add(line);
            drag.Panel.CaptureMouse();
            _drag = drag = drag with { Moving = true, Line = line };
        }

        drag.Line?.ShowAt(InsertionY(drag.Panel, point.Y));
        e.Handled = true;
    }

    private static void Panel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is { Moving: true } drag && ReferenceEquals(sender, drag.Panel))
            e.Handled = true;
        End(commit: true, at: _drag is { } d ? e.GetPosition(d.Panel).Y : 0);
    }

    private static void Panel_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_drag is { Moving: true } drag && ReferenceEquals(sender, drag.Panel) && !drag.Panel.IsMouseCaptured)
            End(commit: false);
    }

    private static void End(bool commit, double at = 0)
    {
        if (_drag is not { } drag)
            return;

        _drag = null;
        if (drag.Line is { } line)
            AdornerLayer.GetAdornerLayer(drag.Panel)?.Remove(line);
        if (drag.Panel.IsMouseCaptured)
            drag.Panel.ReleaseMouseCapture();
        if (!commit || !drag.Moving)
            return;

        var from = drag.Panel.Children.IndexOf(drag.Child);
        var to = InsertionIndex(drag.Panel, at);
        if (to > from)
            to--;
        if (from < 0 || to == from)
            return;

        drag.Panel.Children.RemoveAt(from);
        drag.Panel.Children.Insert(to, drag.Child);
        Reordered?.Invoke(drag.Panel);
    }

    /// <summary>Where among the panel's shown children a child let go at this height goes: before the first whose middle is below it.</summary>
    private static int InsertionIndex(Panel panel, double y)
    {
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is FrameworkElement { IsVisible: true } child
                && child.TranslatePoint(new Point(0, child.ActualHeight / 2), panel).Y > y)
            {
                return i;
            }
        }

        return panel.Children.Count;
    }

    private static double InsertionY(Panel panel, double y)
    {
        var index = InsertionIndex(panel, y);
        if (index < panel.Children.Count && panel.Children[index] is FrameworkElement next)
            return next.TranslatePoint(new Point(0, 0), panel).Y - 4;

        var last = panel.Children.OfType<FrameworkElement>().LastOrDefault(c => c.IsVisible);
        return last is null ? 0 : last.TranslatePoint(new Point(0, last.ActualHeight), panel).Y;
    }

    /// <summary>The line drawn where the child being dragged would go.</summary>
    private sealed class InsertionLine(UIElement panel) : Adorner(panel)
    {
        private double _y = double.NaN;

        public void ShowAt(double y)
        {
            if (Math.Abs(y - _y) < 0.5)
                return;

            _y = y;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            if (double.IsNaN(_y))
                return;

            var brush = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            drawing.DrawRectangle(brush, null, new Rect(0, _y - 1, AdornedElement.RenderSize.Width, 2.5));
        }
    }
}
