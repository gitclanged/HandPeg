using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HandPegApp;

/// <summary>
/// Single-line, non-wrapping control row. When space runs out, the lowest-<see cref="PriorityProperty"/> controls (later first on ties)
/// overflow into a menu opened by the "more" button (the child marked <see cref="IsOverflowButtonProperty"/>, visible only while
/// something overflows). Overflowed controls keep their own Visibility (which still decides what the row offers); they just get zero
/// size. Menu items invoke buttons and toggles through their automation peers, so commands run exactly as on a click.
/// </summary>
public sealed class OverflowStrip : Panel
{
    /// <summary>Keep priority on the row: higher values overflow later. Default 0.</summary>
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(int), typeof(OverflowStrip), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static int GetPriority(UIElement element) => (int)element.GetValue(PriorityProperty);

    public static void SetPriority(UIElement element, int value) => element.SetValue(PriorityProperty, value);

    /// <summary>Marks the "more" button: placed last, visible only while something overflows, and opens the overflow menu.</summary>
    public static readonly DependencyProperty IsOverflowButtonProperty = DependencyProperty.RegisterAttached(
        "IsOverflowButton", typeof(bool), typeof(OverflowStrip), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsOverflowButton(UIElement element) => (bool)element.GetValue(IsOverflowButtonProperty);

    public static void SetIsOverflowButton(UIElement element, bool value) => element.SetValue(IsOverflowButtonProperty, value);

    // Overflowed children from the last measure, the "more" button and its shown width (cached because the button is collapsed,
    // measuring 0, while everything fits, which also keeps it out of tab order and screen readers).
    private readonly List<UIElement> _left = [];
    private ButtonBase? _more;
    private double _moreWidth;

    /// <summary>Menu entry factory for non-button children (e.g. pane tabs): header text and choose action.</summary>
    public Func<UIElement, (string Header, Action Choose)?>? MenuEntryFor { get; set; }

    /// <summary>Minimum width at the last measure: the highest-priority child plus the "more" button when others exist.</summary>
    public double MinimumWidth { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        _left.Clear();
        _more = null;
        var shown = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (GetIsOverflowButton(child) && child is ButtonBase button)
            {
                if (_more is null)
                {
                    _more = button;
                    button.Click -= More_Click;
                    button.Click += More_Click;
                }
            }
            else if (child.Visibility != Visibility.Collapsed)
            {
                shown.Add(child);
            }
        }

        if (_more is { DesiredSize.Width: > 0 })
            _moreWidth = _more.DesiredSize.Width;

        var total = shown.Sum(c => c.DesiredSize.Width);
        var height = shown.Count > 0 ? shown.Max(c => c.DesiredSize.Height) : 0;
        var first = shown.Select((c, i) => (Child: c, Index: i)).OrderByDescending(p => GetPriority(p.Child)).ThenBy(p => p.Index).Select(p => p.Child).FirstOrDefault();
        MinimumWidth = (first?.DesiredSize.Width ?? 0) + (shown.Count > 1 && _more is not null ? _moreWidth : 0);
        if (total <= availableSize.Width)
        {
            ShowMore(false);
            return new Size(total, height);
        }

        // Overflow lowest priority first until the rest fits beside the "more" button. (On first overflow its width is unknown;
        // showing it triggers a re-measure that then reserves its room.)
        ShowMore(true);
        var room = availableSize.Width - (_more is null ? 0 : _moreWidth);
        foreach (var child in shown.Select((c, i) => (Child: c, Index: i)).OrderBy(p => GetPriority(p.Child)).ThenByDescending(p => p.Index).Select(p => p.Child))
        {
            if (total <= room)
                break;

            _left.Add(child);
            total -= child.DesiredSize.Width;
        }

        if (_more is not null)
            height = Math.Max(height, _more.DesiredSize.Height);
        return new Size(Math.Max(total, 0) + (_more is null ? 0 : _moreWidth), height);
    }

    private void ShowMore(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_more is not null && _more.Visibility != visibility)
            _more.Visibility = visibility;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (ReferenceEquals(child, _more) || child.Visibility == Visibility.Collapsed || _left.Contains(child))
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }

        if (_more is not null && _left.Count > 0)
            _more.Arrange(new Rect(x, 0, _more.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }

    /// <summary>Opens the overflow menu in row order: one item per control by name, checked for toggles that are on.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase more)
            return;

        var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Top };
        foreach (var child in InternalChildren.Cast<UIElement>().Where(_left.Contains))
        {
            if (child is not ButtonBase && MenuEntryFor?.Invoke(child) is var (header, choose))
            {
                var entry = new MenuItem { Header = header };
                entry.Click += (_, _) => choose();
                menu.Items.Add(entry);
                continue;
            }

            // KeyTip tooltips resolve with current bindings; the item also shows the action's shortcut.
            var tip = KeyTip.Render(child) ?? (child as FrameworkElement)?.ToolTip;
            var name = AutomationProperties.GetName(child) is { Length: > 0 } named ? named
                : tip as string ?? (child as ContentControl)?.Content as string ?? "";
            if (name.Length == 0)
                continue;

            var item = new MenuItem { Header = name, IsEnabled = child.IsEnabled, ToolTip = tip };
            if (KeyTip.GetAction(child) is { } action)
                item.InputGestureText = Services.KeybindManager.GestureText(action);
            if (child is ToggleButton toggle)
            {
                (item.IsCheckable, item.IsChecked) = (true, toggle.IsChecked == true);
                item.Click += (_, _) => (UIElementAutomationPeer.CreatePeerForElement(toggle)?.GetPattern(PatternInterface.Toggle) as IToggleProvider)?.Toggle();
            }
            else if (child is ButtonBase button)
            {
                item.Click += (_, _) => (UIElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
            }
            else
            {
                continue;
            }

            menu.Items.Add(item);
        }

        if (more is ToggleButton moreToggle)
            menu.Closed += (_, _) => moreToggle.IsChecked = false;
        menu.IsOpen = menu.Items.Count > 0;
    }
}
