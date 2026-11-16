using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Per-pane settings in the tab context menu -----
// Right-clicking a pane tab (or hover tab) starts with a "<Pane> Settings" section: that pane's settings, edited in place (the same
// AppSettings values the Settings window shows, saved immediately), then "More <Pane> Settings..." opening Settings at the relevant
// tab. Panes without settings (header, preset bar, settings tabs) get no section. The Timeline uses the dedicated Timeline Settings
// menu, shared by its tab and its toolbar gear.
public partial class MainWindow
{
    /// <summary>Inserts a pane's settings section at the top of its tab menu (DockManager.ExtendTabMenu).</summary>
    private void AddPaneSettingsMenu(ContextMenu menu, string pane)
    {
        // Timeline: the same Timeline Settings menu its gear opens, at the top of the tab menu.
        if (pane == TimelinePaneId)
        {
            var count = FillTimelineSettings(menu, at: 0);
            menu.Items.Insert(count, new Separator());
            return;
        }

        var (title, settingsTab) = pane switch
        {
            "Media" => ("Media", "Preview"),
            "Timeline" => (ScrubberTitle, "Timeline"),
            "Player" or "Layout" => (pane == "Layout" ? "Canvas" : "Player", "Timeline"),
            "Effects" or "Keyframes" => (pane, "Timeline"),
            _ => ("", ""),
        };
        if (title.Length == 0)
            return;

        var items = new List<object> { new MenuItem { Header = $"{title} Settings", IsEnabled = false, FontWeight = FontWeights.SemiBold } };
        void Toggle(string header, bool on, Action<bool> set, string about)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = on, ToolTip = about, StaysOpenOnClick = true };
            item.Click += (_, _) => set(item.IsChecked);
            items.Add(item);
        }

        void Choice<T>(string header, IEnumerable<(string Label, T Value)> choices, T current, Action<T> set, string about)
        {
            var root = new MenuItem { Header = header, ToolTip = about };
            foreach (var (label, value) in choices)
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = EqualityComparer<T>.Default.Equals(value, current) };
                item.Click += (_, _) => set(value);
                root.Items.Add(item);
            }

            items.Add(root);
        }

        void Save(Action<AppSettings> set, params string[] properties) => _viewModel.SetViewSetting(set, properties);
        var settings = AppSettings.Current;
        switch (title)
        {
            case "Media":
                Toggle("Hover Previews", settings.GenerateHoverPreviews, on => Save(s => s.GenerateHoverPreviews = on, nameof(MainViewModel.ShowHoverPreviews)),
                    "Pictures of a video's inside, made in the background, shown as the pointer moves across a tile.");
                // View (mirrors the pane's View box) and Grid tile size (disabled for List and Filmstrip, which use rows).
                Choice("View", [("Grid", Views.MediaBrowserView.Grid), ("List", Views.MediaBrowserView.List), ("Filmstrip", Views.MediaBrowserView.Filmstrip)],
                    MediaBrowser.ViewMode, view => MediaBrowser.ViewMode = view,
                    "How the files are shown (as the View box in the pane's title row): tiles, a table of their details, or a preview and sub-clip cutter beside the list.");
                Choice("Tile Size", [("Small Tiles", 30.0), ("Medium Tiles", 55.0), ("Large Tiles", 85.0)], NearestTileSize(settings.BrowserTileSize),
                    size => MediaBrowser.TileSize = size, "How large the Grid view's tiles are.");
                if (items[^1] is MenuItem tileSize)
                    tileSize.IsEnabled = MediaBrowser.ViewMode == Views.MediaBrowserView.Grid;
                Choice("Indexer Delay", [("10 Seconds", 10), ("40 Seconds", 40), ("2 Minutes", 120), ("5 Minutes", 300)], settings.BrowserIndexDelaySeconds,
                    seconds => Save(s => s.BrowserIndexDelaySeconds = seconds),
                    "How long the media pane rests before it makes previews of the folder's files in the background.");
                items.Add(MenuAction("Clear Browser Cache", () =>
                {
                    PreviewCache.Browser.Clear();
                    _viewModel.StatusText = "The media browser's previews were cleared: they are made again as they are needed.";
                }, "Deletes the media pane's kept pictures and strips (the Media browser previews cache)."));
                break;

            case ScrubberTitle:
                Toggle("Snap to I-Frames", _viewModel.SnapToIFrames, on => _viewModel.SnapToIFrames = on, "Cuts and the playhead snap to the video's I-frames, so stream copy cuts cleanly.");
                Toggle($"Sync {ScrubberTitle} Stretch", _viewModel.SyncMasterTimeline, on => _viewModel.SyncMasterTimeline = on,
                    $"On: the {ScrubberTitle} is zoomed and moved along with the timeline. Off: it always shows the whole sequence.");

                // Tall matches the maximum drag height (120 px via ScrubberStep).
                Choice($"{ScrubberTitle} Height",
                    [("Compact", ScrubberStep(MainViewModel.CompactMasterTimeline)), ("Medium", ScrubberStep(MainViewModel.MediumMasterTimeline)), ("Tall", ScrubberStep(MainViewModel.TallMasterTimeline))],
                    _viewModel.MasterTimelineHeight, height => _viewModel.MasterTimelineHeight = height,
                    $"How tall the {ScrubberTitle} is (drag its lower edge for any height between, 16 to 120 px).");
                break;

            case "Player" or "Canvas":
                Toggle("Edit Clips on Video", _viewModel.EditLayersOnPlayer, on => _viewModel.EditLayersOnPlayer = on, "Move, resize and turn the selected clip on the player itself.");
                Toggle("Snap Guides", settings.SnapLayoutGuides, on => Save(s => s.SnapLayoutGuides = on), "A dragged clip snaps to the frame's edges, centre and the other clips (Alt while dragging: free).");
                Toggle("Motion Path", settings.ShowMotionPath, on =>
                {
                    Save(s => s.ShowMotionPath = on);
                    RefreshMotionPath();
                }, "Draws the selected clip's path between its position keyframes.");
                break;

            default:
                Toggle("Value Graph", _viewModel.ShowKeyframeGraph, on => _viewModel.ShowKeyframeGraph = on, "Draws each keyframe lane's values as a curve.");
                Toggle("Key Changed Only", _viewModel.KeyChangedOnly, on => _viewModel.KeyChangedOnly = on, "A new keyframe is put only on the values that changed, not on every value of the clip.");
                break;
        }

        items.Add(MenuAction($"More {title} Settings...", () => OpenSettings(settingsTab), "Opens Settings at the tab with the rest."));
        items.Add(new Separator());
        for (var i = 0; i < items.Count; i++)
            menu.Items.Insert(i, items[i]);
    }

    private static MenuItem MenuAction(string header, Action action, string about)
    {
        var item = new MenuItem { Header = header, ToolTip = about };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Nearest tile size preset to a size (0 to 100), for the menu check mark.</summary>
    private static double NearestTileSize(double size) => new[] { 30.0, 55, 85 }.MinBy(s => Math.Abs(s - size));

    // ----- Timeline Settings menu -----
    // Single menu for Timeline settings, opened from the tab context menu and the gear beside Snap on the tool row: lane and block
    // overlays, snapping and playhead follow, bars and sizes, and edit propagation, in four headed groups. Built from one table
    // (TimelineSettingGroups, read window-less by the timeline-settings-menu golden); each switch is the same value shown by the
    // Settings window, toolbar toggles and Views menu, saved immediately, and the Timeline redraws.

    /// <summary>Timeline Settings menu item identity; <see cref="TimelineSettingGroups"/> places each exactly once.</summary>
    internal enum TimelineSetting
    {
        EffectLanes, BuiltInMaskLanes, KeyframeDiamonds, Filmstrips, Waveforms, BeatGrid, IFrameLines, GainLines,
        Snapping, SnapToBeats, SnapToIFrames, FollowPlayhead, MomentumScrolling, TimesAsFrames,
        CreatorHotbar, UndoRedoButtons, Scrubber, TrackHeight,
        RippleAllTracks, DroppedClipsJoinTrack, ShowLinkedAudio,
    }

    /// <summary>
    /// Timeline Settings menu item: setting, header, tooltip, backing <see cref="AppSettings"/> property (null when stored by the
    /// project or session instead), and the KeybindManager action whose shortcut is shown beside it (null for none).
    /// </summary>
    internal sealed record TimelineSettingItem(TimelineSetting Setting, string Header, string About, string? Kept, string? KeyAction = null);

    /// <summary>Timeline Settings menu group: header and ordered items.</summary>
    internal sealed record TimelineSettingGroup(string Header, IReadOnlyList<TimelineSettingItem> Items);

    /// <summary>Ordered Timeline Settings groups; both the tab context menu and the gear menu are built from this table.</summary>
    internal static readonly IReadOnlyList<TimelineSettingGroup> TimelineSettingGroups =
    [
        new("Lanes & Overlays",
        [
            new(TimelineSetting.EffectLanes, "Show Effect Sub-Lanes on Expanded Tracks",
                "Each track's effects get lanes of their own in its stack, one block per effect over the stretch it works in, shown while that track's stack is open (its chevron). Off: a clip's effects show only as the fx badge on its block.",
                nameof(AppSettings.ShowTimelineEffectLanes)),
            new(TimelineSetting.BuiltInMaskLanes, "Show Built-In Mask & Edge Lanes on Cutouts",
                "A Cutout's own Mask and Rounded & Feathered Edges cards, which every Cutout is made with, get effect lanes and count in its fx badge too. Off: they are in the Effects pane only.",
                nameof(AppSettings.ShowBuiltInMaskEffectLanes)),
            new(TimelineSetting.KeyframeDiamonds, "Show Keyframe Diamonds on Clips", "Draws a diamond on a clip's block at each of its keyframes.",
                nameof(AppSettings.ShowClipKeyframes)),
            new(TimelineSetting.Filmstrips, "Show Clip Filmstrip Thumbnails",
                "Video clips' blocks show their file's frames along them. Off: plain blocks (the Sequence Overview Scrubber's strip of pictures is a setting of its own).",
                nameof(AppSettings.ShowClipFilmstrips)),
            new(TimelineSetting.Waveforms, "Show Audio Waveforms",
                "Clips with sound draw their waveforms on their blocks, and a closed sound stack the mix of its lanes in its one bar.",
                nameof(AppSettings.ShowClipWaveforms)),
            new(TimelineSetting.BeatGrid, "Show Audio Beat Grid", "Draws the beats found in a clip's sound as faint ticks along its block.",
                nameof(AppSettings.ShowBeatGrid)),
            new(TimelineSetting.IFrameLines, "Show I-Frame Lines", "Draws the video's I-frames as thin lines on the Sequence Overview Scrubber.",
                nameof(AppSettings.ShowIFrames)),
            new(TimelineSetting.GainLines, "Gain and Fade Lines", "Draws each clip's gain line and fade handles over its waveform.",
                nameof(AppSettings.ShowClipGainLines)),
        ]),
        new("Snapping & Playback",
        [
            new(TimelineSetting.Snapping, "Enable Timeline Snapping",
                "Clips and edges snap to the playhead, markers and other clips' edges. Shift while dragging is never snapped; Shift on a move onto another track keeps the clip's time exactly.",
                nameof(AppSettings.SnapClips), "view.toggleSnap"),
            new(TimelineSetting.SnapToBeats, "Snap to Audio Beats",
                "With snapping on, edges also catch on the beats found in clips' sound (a clip's menu: Audio & Voice, Detect & Snap to Audio Beats).",
                nameof(AppSettings.SnapToBeats)),
            new(TimelineSetting.SnapToIFrames, "Snap Cuts to I-Frames",
                "Cuts and the playhead snap to the video's I-frames, so stream copy cuts cleanly. Kept with the project (Settings has what a new window starts with).",
                null),
            new(TimelineSetting.FollowPlayhead, "Follow Playhead During Playback",
                "While playing, the Timeline turns the page as the playhead reaches its edge, so what is playing stays in sight. Off: the view stays where you put it.",
                nameof(AppSettings.TimelineFollowsPlayhead)),
            new(TimelineSetting.MomentumScrolling, "Enable Momentum Scrolling on Timeline & Filmstrips",
                "A quick flick or a fast turn of the wheel coasts on and slows to a stop; a slow drag, or a pause of 40 ms before letting go, never coasts, and any press stops it. The same switch as Settings > Timeline & Player.",
                nameof(AppSettings.EnableMomentumScrolling)),
            new(TimelineSetting.TimesAsFrames, "Show Sub-Second Time in Frames (HH:MM:SS:FF)",
                "Times read hours, minutes, seconds and the frame into the second (on by default). Off: milliseconds, HH:MM:SS.mmm. The same choice as Settings > Timeline & Player, Sub-second Time Format.",
                nameof(AppSettings.ShowTimesAsFrames)),
        ]),
        new("Bars & Sizing",
        [
            new(TimelineSetting.CreatorHotbar, "Show Quick FX Button",
                "The Quick FX button on the row of tools: rest the pointer on it, or click it, for five slots, each putting an Effect, a Transition, a Game Preset or a one-click tool on the selected clips. Right-click a slot to choose what it holds.",
                nameof(AppSettings.ShowCreatorHotbar)),
            new(TimelineSetting.UndoRedoButtons, "Show Undo / Redo Buttons",
                "Undo and Redo buttons on the Timeline's row of tools. Off by default: Ctrl+Z and Ctrl+Y undo and redo as ever.",
                nameof(AppSettings.ShowTimelineUndoRedo)),
            new(TimelineSetting.Scrubber, "Show Sequence Overview Scrubber",
                "The Sequence Overview Scrubber under the player: the whole sequence at a glance, scrubbed by a click or a drag (as in Tabs and Panes).",
                nameof(AppSettings.ShowMasterTimeline)),
            new(TimelineSetting.TrackHeight, "Track Height",
                "How tall each track's row is (the slider in the Timeline's row of tools sets any height between; a track sized by its own lower edge keeps its height).",
                nameof(AppSettings.LayerHeight)),
        ]),
        new("Editing",
        [
            new(TimelineSetting.RippleAllTracks, "Ripple All Tracks", "A ripple (Ctrl-drag, Delete with ripple) moves what follows on every track, not only the clip's own.",
                nameof(AppSettings.RippleAllTracks)),
            new(TimelineSetting.DroppedClipsJoinTrack, "Dropped Clips Join Track", "A picture dropped on a track with room goes onto that track instead of a new one.",
                nameof(AppSettings.DroppedClipsJoinTrack)),
            new(TimelineSetting.ShowLinkedAudio, "Show Linked Audio", "Draws a video clip's own sound, or its sound stack's, over its pictures, across its block (for this session).",
                null),
        ]),
    ];

    /// <summary>Track Height presets (MainViewModel.LayerTrackHeight, 1 to 50; 25 is the default).</summary>
    internal static readonly IReadOnlyList<(string Label, int Height)> TrackHeightPresets = [("Compact", 8), ("Medium", 25), ("Large", 42), ("Extra Large", 50)];

    /// <summary>Nearest track height preset, for the check mark; ties resolve to the lower preset.</summary>
    internal static int NearestTrackHeight(double height) => TrackHeightPresets.MinBy(preset => Math.Abs(preset.Height - height)).Height;

    /// <summary>
    /// Builds the Timeline Settings menu from <see cref="TimelineSettingGroups"/>: headed groups separated, switches reflecting current
    /// state and toggled in place (menu stays open), Track Height presets, then More Timeline Settings... Inserted into
    /// <paramref name="menu"/> starting at <paramref name="at"/>, or appended when -1.
    /// </summary>
    /// <returns>Number of items inserted.</returns>
    private int FillTimelineSettings(ItemsControl menu, int at = -1)
    {
        var count = 0;
        void Add(object item)
        {
            if (at < 0)
                menu.Items.Add(item);
            else
                menu.Items.Insert(at + count, item);
            count++;
        }

        foreach (var group in TimelineSettingGroups)
        {
            if (count > 0)
                Add(new Separator());
            Add(new MenuItem { Header = group.Header, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var entry in group.Items)
                Add(entry.Setting == TimelineSetting.TrackHeight ? TrackHeightMenu(entry) : TimelineSwitch(entry));
        }

        Add(new Separator());
        Add(MenuAction("More Timeline Settings...", () => OpenSettings("Timeline"), "Opens Settings at the tab with the rest."));
        return count;
    }

    /// <summary>
    /// Opens the Timeline Settings menu from the tool-row gear: above the gear, or at the pointer when invoked from the row's overflow
    /// menu (an overflowed gear has zero size).
    /// </summary>
    private void OpenTimelineSettings(FrameworkElement? button)
    {
        var menu = new ContextMenu();
        if (button is { IsVisible: true, ActualWidth: > 0 })
            (menu.PlacementTarget, menu.Placement) = (button, PlacementMode.Top);
        else
            (menu.PlacementTarget, menu.Placement) = (UnifiedTimeline, PlacementMode.MousePoint);
        FillTimelineSettings(menu);
        menu.IsOpen = true;
    }

    // Checkable switch reflecting current state; a click applies it (guarded) and schedules a Timeline redraw.
    private MenuItem TimelineSwitch(TimelineSettingItem entry)
    {
        var (on, set) = TimelineSettingState(entry.Setting);
        var item = new MenuItem
        {
            Header = entry.Header, IsCheckable = true, IsChecked = on, ToolTip = entry.About,

            // Toggling the Scrubber pane triggers a dock re-layout, so that item closes the menu.
            StaysOpenOnClick = entry.Setting != TimelineSetting.Scrubber,
        };
        if (entry.KeyAction is { } action)
            item.InputGestureText = KeybindManager.GestureText(action);
        item.Click += (_, _) => RunGuarded(entry.Header, () =>
        {
            set(item.IsChecked);
            ScheduleClipRedraw();
        });
        return item;
    }

    // Track Height presets with the nearest one checked; rows with a custom height keep it.
    private MenuItem TrackHeightMenu(TimelineSettingItem entry)
    {
        var root = new MenuItem { Header = entry.Header, ToolTip = entry.About };
        var nearest = NearestTrackHeight(_viewModel.LayerTrackHeight);
        foreach (var (label, height) in TrackHeightPresets)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = height == nearest };
            item.Click += (_, _) => RunGuarded(entry.Header, () => _viewModel.LayerTrackHeight = height);
            root.Items.Add(item);
        }

        return root;
    }

    // State and setter per switch: an AppSettings value (saved immediately) or its view-model mirror (which saves itself); Snap to
    // I-Frames is per project, Show Linked Audio per session. The Scrubber toggles like Tabs and Panes does; the hotbar (Quick FX) and
    // Undo/Redo buttons go through SaveHotbar, which re-applies the tool row from settings.
    private (bool On, Action<bool> Set) TimelineSettingState(TimelineSetting setting)
    {
        var settings = AppSettings.Current;
        void Save(Action<AppSettings> set) => _viewModel.SetViewSetting(set);
        return setting switch
        {
            TimelineSetting.EffectLanes => (settings.ShowTimelineEffectLanes, on => Save(s => s.ShowTimelineEffectLanes = on)),
            TimelineSetting.BuiltInMaskLanes => (settings.ShowBuiltInMaskEffectLanes, on => Save(s => s.ShowBuiltInMaskEffectLanes = on)),
            TimelineSetting.KeyframeDiamonds => (_viewModel.ShowClipKeyframes, on => _viewModel.ShowClipKeyframes = on),
            TimelineSetting.Filmstrips => (settings.ShowClipFilmstrips, on => Save(s => s.ShowClipFilmstrips = on)),
            TimelineSetting.Waveforms => (settings.ShowClipWaveforms, on => Save(s => s.ShowClipWaveforms = on)),
            TimelineSetting.BeatGrid => (_viewModel.ShowBeatGrid, on => _viewModel.ShowBeatGrid = on),
            TimelineSetting.IFrameLines => (_viewModel.ShowIFrames, on => _viewModel.ShowIFrames = on),
            TimelineSetting.GainLines => (settings.ShowClipGainLines, on => Save(s => s.ShowClipGainLines = on)),
            TimelineSetting.Snapping => (_viewModel.SnapClips, on => _viewModel.SnapClips = on),
            TimelineSetting.SnapToBeats => (_viewModel.SnapToBeats, on => _viewModel.SnapToBeats = on),
            TimelineSetting.SnapToIFrames => (_viewModel.SnapToIFrames, on => _viewModel.SnapToIFrames = on),
            TimelineSetting.FollowPlayhead => (settings.TimelineFollowsPlayhead, on => Save(s => s.TimelineFollowsPlayhead = on)),
            TimelineSetting.MomentumScrolling => (settings.EnableMomentumScrolling, on => Save(s => s.EnableMomentumScrolling = on)),
            TimelineSetting.TimesAsFrames => (settings.ShowTimesAsFrames, on => _viewModel.SetTimesAsFrames(on)),
            TimelineSetting.CreatorHotbar => (settings.ShowCreatorHotbar, on => SaveHotbar(s => s.ShowCreatorHotbar = on)),
            TimelineSetting.UndoRedoButtons => (settings.ShowTimelineUndoRedo, on => SaveHotbar(s => s.ShowTimelineUndoRedo = on)),
            TimelineSetting.Scrubber => (settings.ShowMasterTimeline, on => SetScrubberShown(on)),
            TimelineSetting.RippleAllTracks => (settings.RippleAllTracks, on => Save(s => s.RippleAllTracks = on)),
            TimelineSetting.DroppedClipsJoinTrack => (settings.DroppedClipsJoinTrack, on => Save(s => s.DroppedClipsJoinTrack = on)),
            TimelineSetting.ShowLinkedAudio => (_viewModel.ShowLinkedAudio, on => _viewModel.ShowLinkedAudio = on),
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Track Height is a choice of heights, not a switch (TrackHeightMenu)."),
        };
    }
}
