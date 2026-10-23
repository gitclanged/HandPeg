using System.Windows;
using System.Windows.Controls;

namespace HandPegApp.Docking;

/// <summary>How a pane is grabbed to be moved.</summary>
public enum PaneHandle
{
    /// <summary>By its title, which its content shows in a <see cref="PaneTitleSlot"/>: the tab is put there, in the title's place.</summary>
    TitleSlot,

    /// <summary>By a tab that appears when the pointer comes to the pane's top-left corner (a pane with no title of its own).</summary>
    Hover,

    /// <summary>By a tab in a row of tabs above it: one of the settings tabs (Summary, Video, ...), which have always had one.</summary>
    Tab,
}

/// <summary>A pane the windows can arrange: what it is called, what it shows, and how it behaves.</summary>
public sealed class DockPane
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Makes the pane's content, or gives the one made before: called once, when the pane is first shown.</summary>
    public required Func<FrameworkElement> Content { get; init; }

    public PaneHandle Handle { get; init; } = PaneHandle.TitleSlot;

    /// <summary>As tall as what is in it, in a column of panes (the master timeline, the playback controls...), rather than a share of the room.</summary>
    public bool FitsContent { get; init; }

    /// <summary>Whether the pane is to be shown now (its own setting, and the mode); hidden, it keeps its place.</summary>
    public Func<bool> IsShown { get; init; } = () => true;

    /// <summary>Hides the pane (its setting off); null for one that cannot be closed.</summary>
    public Action? Close { get; init; }

    /// <summary>A pane sized to its content that can still be made taller or shorter by the border under it: told by how many pixels.</summary>
    public Action<double>? ResizeBy { get; init; }

    /// <summary>The least width the pane can be given.</summary>
    public Func<double> MinWidth { get; init; } = () => 120;

    public double MinHeight { get; init; } = 60;

    /// <summary>The overlay the pane's hover tab is put in, when the pane draws it itself (the player: its tab has to lie over the video).</summary>
    public Panel? HoverHost { get; init; }
}

/// <summary>
/// Where a pane's tab goes, in the pane's own content: where its title used to be. The dock manager puts the tabs of
/// the pane's group here (one tab, for a pane on its own), so moving a pane adds nothing to its height.
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

    /// <summary>The pane the slot belongs to.</summary>
    public string PaneId
    {
        get => (string)GetValue(PaneIdProperty);
        set => SetValue(PaneIdProperty, value);
    }

    /// <summary>The slot of a pane, when its content has one.</summary>
    public static PaneTitleSlot? Of(string pane) => Slots.TryGetValue(pane, out var slot) && slot.TryGetTarget(out var found) ? found : null;
}
