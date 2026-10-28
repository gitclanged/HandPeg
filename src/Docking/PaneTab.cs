using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp.Docking;

/// <summary>
/// A pane's tab among the others of its group: it can be tabbed to and chosen with Enter as with a click (Space stays
/// the player's play and pause, as everywhere in the window), and screen readers and UI Automation see it as a tab
/// item that can be selected (a tab control's tabs could, and the groups that were tab controls are drawn with these now).
/// </summary>
internal sealed class PaneTab : Border
{
    // Shows the tab's pane; true when it was chosen from the keyboard, whose focus then goes to the new tab.
    private readonly Action<bool> _choose;

    public PaneTab(Action<bool> choose, bool isActive)
    {
        (_choose, IsActive) = (choose, isActive);
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    /// <summary>Whether its pane is the one its group shows.</summary>
    public bool IsActive { get; }

    public void Choose(bool byKey = false) => _choose(byKey);

    /// <summary>
    /// A boxed tab of the main tab bar (4.1.1): outlined where the theme gives tab edges (TabEdgeBrush, ActiveTabEdgeBrush,
    /// lit "upper/lower"), drawn over the face so they take no room: the top and left in the upper colour, the right in
    /// the lower one; the tab shown keeps its trace along the top and is outlined at its sides. The bottom is open (it
    /// joins the panel under it). A change of theme changes the tab's background, which draws it again.
    /// </summary>
    public bool Outlined { get; init; }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (!Outlined || ActualWidth < 2 || ActualHeight < 2)
            return;

        var key = IsActive ? "ActiveTabEdgeBrush" : "TabEdgeBrush";
        if (TryFindResource(key) is not Brush upper || upper is SolidColorBrush { Color.A: 0 })
            return;
        var lower = TryFindResource(key[..^"Brush".Length] + "LowerBrush") as Brush ?? upper;
        var (w, h) = (ActualWidth, ActualHeight);
        var (light, dark) = (Frozen(new Pen(upper, 1)), Frozen(new Pen(lower, 1)));
        drawing.DrawLine(light, new Point(0.5, IsActive ? 2 : 0.5), new Point(0.5, h));
        if (!IsActive)
            drawing.DrawLine(light, new Point(0.5, 0.5), new Point(w - 0.5, 0.5));
        drawing.DrawLine(dark, new Point(w - 0.5, IsActive ? 2 : 0.5), new Point(w - 0.5, h));
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Choose(byKey: true);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PaneTabPeer(this);

    private sealed class PaneTabPeer(PaneTab owner) : FrameworkElementAutomationPeer(owner), ISelectionItemProvider, IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

        protected override string GetClassNameCore() => nameof(PaneTab);

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.SelectionItem or PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public bool IsSelected => owner.IsActive;

        public IRawElementProviderSimple? SelectionContainer => null;

        public void Select() => owner.Choose();

        public void AddToSelection() => owner.Choose();

        public void RemoveFromSelection()
        {
        }

        public void Invoke() => owner.Choose();
    }
}
