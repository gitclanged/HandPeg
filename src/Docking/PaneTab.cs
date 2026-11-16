using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static HandPegApp.UiBrushes;

namespace HandPegApp.Docking;

/// <summary>
/// Pane group tab: focusable and activated by Enter or click (Space stays play/pause window-wide), exposed to UI Automation as a
/// selectable TabItem (parity with the former TabControl groups).
/// </summary>
internal sealed class PaneTab : Border
{
    // Activates the tab's pane; true for keyboard activation, which moves focus to the new tab.
    private readonly Action<bool> _choose;

    public PaneTab(Action<bool> choose, bool isActive)
    {
        (_choose, IsActive) = (choose, isActive);
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    /// <summary>Whether its pane is the group's active pane.</summary>
    public bool IsActive { get; }

    public void Choose(bool byKey = false) => _choose(byKey);

    /// <summary>
    /// Boxed main-bar tab: theme edge brushes (TabEdgeBrush, ActiveTabEdgeBrush, "upper/lower" lighting) drawn over the face without
    /// layout cost: top/left upper colour, right lower colour; the active tab keeps its top trace and side outlines. Bottom open to join
    /// the panel. Theme changes alter the background, triggering a redraw.
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
