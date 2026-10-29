using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HandPegApp;

/// <summary>
/// A row of controls that never wraps: laid out on one line, in order, and when the line is too short for all of
/// them the least important (<see cref="PriorityProperty"/>; the later of two equal ones first) are left out of it
/// and listed instead in a menu that opens from its "more" button (the child marked <see cref="IsOverflowButtonProperty"/>,
/// shown only while something is left out). A control left out keeps its own visibility, which what the row shows is
/// chosen by: it is only given no room. From the menu, a button is pressed and a toggle toggled through their
/// automation peers, so that their commands run exactly as a click would run them.
/// </summary>
public sealed class OverflowStrip : Panel
{
    /// <summary>How much a control is wanted on the row: the higher, the later it is left out. 0 by default.</summary>
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(int), typeof(OverflowStrip), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static int GetPriority(UIElement element) => (int)element.GetValue(PriorityProperty);

    public static void SetPriority(UIElement element, int value) => element.SetValue(PriorityProperty, value);

    /// <summary>Marks the strip's "more" button: placed last, shown only while something does not fit, and opening the menu of what did not.</summary>
    public static readonly DependencyProperty IsOverflowButtonProperty = DependencyProperty.RegisterAttached(
        "IsOverflowButton", typeof(bool), typeof(OverflowStrip), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsOverflowButton(UIElement element) => (bool)element.GetValue(IsOverflowButtonProperty);

    public static void SetIsOverflowButton(UIElement element, bool value) => element.SetValue(IsOverflowButtonProperty, value);

    // What did not fit at the last measure, the "more" button, and how wide that is when shown: it is collapsed while
    // everything fits, so that it can neither be tabbed to nor found by a screen reader, and then measures 0.
    private readonly List<UIElement> _left = [];
    private ButtonBase? _more;
    private double _moreWidth;

    /// <summary>For a child that is not a button (a pane's tab): what its entry in the menu says, and what choosing it does.</summary>
    public Func<UIElement, (string Header, Action Choose)?>? MenuEntryFor { get; set; }

    /// <summary>The narrowest the strip can be, as of its last measure: its most wanted child, and the "more" button beside it when there are others.</summary>
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

        // Too long: the least wanted go first, until what is left fits beside the "more" button. (The first time it is
        // wanted its width is not known yet: showing it brings another measure, which leaves room for it.)
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

    /// <summary>The menu of what did not fit, in the row's order: each by its name, ticked when it is a toggle that is on.</summary>
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

            // A tooltip naming keys (KeyTip) is filled in with the keys as bound now, and the menu shows the action's key too.
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
