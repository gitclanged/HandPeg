using System.Windows;
using System.Windows.Controls;

namespace HandPegApp.Docking;

/// <summary>Drag handle style of a pane.</summary>
public enum PaneHandle
{
    /// <summary>Tab placed in the content's <see cref="PaneTitleSlot"/>, replacing the title.</summary>
    TitleSlot,

    /// <summary>Hover tab at the top-left corner (titleless panes).</summary>
    Hover,

    /// <summary>Tab in a tab row above (settings tabs).</summary>
    Tab,
}

/// <summary>Layout shape of a reshapeable pane (the header).</summary>
public enum PaneShape
{
    /// <summary>Horizontal row across its part.</summary>
    Row,

    /// <summary>Narrow vertical icon strip (side-docked, or in a tall/narrow slot).</summary>
    Column,

    /// <summary>Joined to the right end of the boxed tab row of the pane below (one combined bar).</summary>
    Joined,
}

/// <summary>Dockable pane descriptor: id, title, content factory and layout behaviour.</summary>
public sealed class DockPane
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Content factory, invoked once on first show (lazy creation).</summary>
    public required Func<FrameworkElement> Content { get; init; }

    public PaneHandle Handle { get; init; } = PaneHandle.TitleSlot;

    /// <summary>
    /// When alone in its group (Timeline): no title row, content to the top edge, dragged via the hover tab (<see cref="PaneHandle.Hover"/>).
    /// With group-mates, tabs follow <see cref="Handle"/>.
    /// </summary>
    public bool HeaderlessAlone { get; init; }

    /// <summary>Content-sized height in a vertical split (Overview Scrubber, playback controls) instead of a star share.</summary>
    public bool FitsContent { get; init; }

    /// <summary>Current visibility (own setting and mode); hidden panes keep their tree position.</summary>
    public Func<bool> IsShown { get; init; } = () => true;

    /// <summary>Close action (turns its setting off); null when not closable.</summary>
    public Action? Close { get; init; }

    /// <summary>
    /// Resize callback for content-sized panes adjustable via adjacent borders (plain drag below, Ctrl+drag above), given the pointer
    /// delta in pixels since the last call.
    /// </summary>
    public Action<double>? ResizeBy { get; init; }

    /// <summary>
    /// Border double-click override instead of EvenOut (e.g. the Overview Scrubber's lower edge resets its height); null for default.
    /// </summary>
    public Action? ResetSize { get; init; }

    /// <summary>
    /// Minimum pane width (default 180 DIP): small enough for every border to move in a 1280 DIP window, large enough to keep title
    /// rows and tools intact.
    /// </summary>
    public Func<double> MinWidth { get; init; } = () => 180;

    public double MinHeight { get; init; } = 60;

    /// <summary>Pane-owned overlay for its hover tab (the player, so the tab sits above the native video).</summary>
    public Panel? HoverHost { get; init; }

    /// <summary>
    /// Shape callback, invoked each layout before width measurement and on slot reshape; null for fixed-shape panes. Alone in a
    /// horizontal split or in a tall/narrow slot: <see cref="PaneShape.Column"/>; with <see cref="JoinsPaneBelow"/> above a titled/tabbed
    /// pane: <see cref="PaneShape.Joined"/> to its boxed tab row.
    /// </summary>
    public Action<PaneShape>? Reshape { get; init; }

    /// <summary>Joins the right end of the tab row below instead of being its own row.</summary>
    public bool JoinsPaneBelow { get; init; }

    /// <summary>
    /// Hosts a native HWND (player): never re-parented on relayout (that flashes black); it lives on an overlay tracking a stand-in
    /// placed wherever the layout puts the pane.
    /// </summary>
    public bool Floats { get; init; }

    /// <summary>
    /// Optional studio pane: visible only while in the layout; never re-added by loads, placed by <see cref="DockManager.Open"/> and
    /// removed by <see cref="DockManager.Close"/>.
    /// </summary>
    public bool Optional { get; init; }

    /// <summary>Fallback group-mates (tab into the first found) when the mode default has no slot for it.</summary>
    public IReadOnlyList<string> HomeWith { get; init; } = [];
}

/// <summary>
/// Title placeholder in pane content where the dock manager places the group's tabs (one tab when alone), so docking adds no height.
/// </summary>
public sealed class PaneTitleSlot : ContentControl
{
    private static readonly Dictionary<string, WeakReference<PaneTitleSlot>> Slots = [];

    public static readonly DependencyProperty PaneIdProperty = DependencyProperty.Register(
        nameof(PaneId), typeof(string), typeof(PaneTitleSlot), new PropertyMetadata("", (d, e) =>
        {
            if (e.NewValue is string id && id.Length > 0)
                Slots[id] = new WeakReference<PaneTitleSlot>((PaneTitleSlot)d);
        }));

    public PaneTitleSlot()
    {
        Focusable = false;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>Owning pane id.</summary>
    public string PaneId
    {
        get => (string)GetValue(PaneIdProperty);
        set => SetValue(PaneIdProperty, value);
    }

    /// <summary>Registered title slot of a pane, if its content has one.</summary>
    public static PaneTitleSlot? Of(string pane) => Slots.TryGetValue(pane, out var slot) && slot.TryGetTarget(out var found) ? found : null;

    /// <summary>
    /// Restores a pane's slot registration after a second content instance outside the dock (Media Browser picker in New Project)
    /// claimed it via its XAML id, which would otherwise route the pane's tab into the dialog. Null clears the registration.
    /// </summary>
    internal static void Restore(string pane, PaneTitleSlot? slot)
    {
        if (slot is null)
            Slots.Remove(pane);
        else
            Slots[pane] = new WeakReference<PaneTitleSlot>(slot);
    }
}
