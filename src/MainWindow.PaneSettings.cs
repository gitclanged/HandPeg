using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- A pane's own settings on its tab's right-click menu (Pass 4.2B-2) -----
// Right-clicking a pane's tab (or its hover tab) opens with "<Pane> Settings": the settings that pane's work turns on,
// changed in place (each the same AppSettings value the Settings window shows, saved at once), then "More <Pane>
// Settings..." opening the Settings window at the tab that has the rest. Panes without settings of their own (the
// header, the preset bar, the settings tabs) get no section. The Timeline's is a menu of its own (5.3, Timeline Settings),
// the same from its tab and from the gear on its row of tools.
public partial class MainWindow
{
    /// <summary>The section for a pane, put at the top of its tab's menu (DockManager.ExtendTabMenu).</summary>
    private void AddPaneSettingsMenu(ContextMenu menu, string pane)
    {
        // The Timeline's (5.3): the Timeline Settings menu its gear opens too, at the top of its tab's menu.
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
                // The view (5.4C, as the pane's View box), and the tiles' size for the Grid (only there: the List and Filmstrip views have rows).
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

                // Tall is the tallest a drag gives it (120 px, ScrubberStep), not more.
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

    /// <summary>The tile size preset nearest a tile size (0 to 100), for the menu's check mark.</summary>
    private static double NearestTileSize(double size) => new[] { 30.0, 55, 85 }.MinBy(s => Math.Abs(s - size));

    // ----- The Timeline Settings menu (5.3) -----
    // One menu for the Timeline's own settings, opened from its tab's right-click and from the gear beside Snap on its row of tools
    // (the user's call): what is drawn on its lanes and blocks, how it snaps and follows the playhead, its bars and sizes, and how edits
    // spread, in four groups under headers of their own. It is made from one table (TimelineSettingGroups, which the golden test
    // timeline-settings-menu reads without a window); each switch is the very value the Settings window, the toolbar's toggles and the
    // Views menu show, saved at once, and the Timeline is drawn again with it.

    /// <summary>What an item of the Timeline Settings menu changes (5.3); <see cref="TimelineSettingGroups"/> places each once.</summary>
    internal enum TimelineSetting
    {
        EffectLanes, BuiltInMaskLanes, KeyframeDiamonds, Filmstrips, Waveforms, BeatGrid, IFrameLines, GainLines,
        Snapping, SnapToBeats, SnapToIFrames, FollowPlayhead, MomentumScrolling, TimesAsFrames,
        CreatorHotbar, UndoRedoButtons, Scrubber, TrackHeight,
        RippleAllTracks, DroppedClipsJoinTrack, ShowLinkedAudio,
    }

    /// <summary>
    /// An item of the Timeline Settings menu (5.3): what it changes, its words and its tooltip, the <see cref="AppSettings"/> property it
    /// is kept in (null: not kept by HandPeg's settings, the project's or the session's own), and the KeybindManager action whose key it
    /// shows beside it (null for none).
    /// </summary>
    internal sealed record TimelineSettingItem(TimelineSetting Setting, string Header, string About, string? Kept, string? KeyAction = null);

    /// <summary>A group of the Timeline Settings menu: its header, and its items in their order.</summary>
    internal sealed record TimelineSettingGroup(string Header, IReadOnlyList<TimelineSettingItem> Items);

    /// <summary>The Timeline Settings menu, group by group in order (5.3): the tab's right-click and the gear are both made from it.</summary>
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

    /// <summary>The Track Height presets of the Timeline Settings menu (MainViewModel.LayerTrackHeight, 1 to 50; 25 is HandPeg's own).</summary>
    internal static readonly IReadOnlyList<(string Label, int Height)> TrackHeightPresets = [("Compact", 8), ("Medium", 25), ("Large", 42), ("Extra Large", 50)];

    /// <summary>The track height preset nearest the height set, for the menu's check mark (the lower of two as near).</summary>
    internal static int NearestTrackHeight(double height) => TrackHeightPresets.MinBy(preset => Math.Abs(preset.Height - height)).Height;

    /// <summary>
    /// Fills the Timeline Settings menu (5.3) from <see cref="TimelineSettingGroups"/>: each group under a header of its own, separators
    /// between them, its switches ticked as they are now and changed in place (the menu stays open), Track Height's presets, then More
    /// Timeline Settings... Put into <paramref name="menu"/> from <paramref name="at"/> on, or after what it holds (-1).
    /// </summary>
    /// <returns>How many items were put in.</returns>
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
    /// The Timeline Settings menu from the gear on the Timeline's row of tools (5.3): over the gear, or at the pointer when the gear was
    /// chosen from the row's "more" menu (left out of a narrow row, it is laid out at no size).
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

    // One switch of the menu: ticked as it is now; a click sets it (guarded) and the Timeline is drawn again with it.
    private MenuItem TimelineSwitch(TimelineSettingItem entry)
    {
        var (on, set) = TimelineSettingState(entry.Setting);
        var item = new MenuItem
        {
            Header = entry.Header, IsCheckable = true, IsChecked = on, ToolTip = entry.About,

            // Showing or hiding a pane lays the panes out again: the menu closes with that one.
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

    // Track Height's presets: the one nearest the height set is ticked; a row with a height of its own keeps it.
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

    // What each switch of the menu shows and sets: the AppSettings value the Settings window shows (saved at once), or the view model's
    // copy of one, which saves it itself; Snap Cuts to I-Frames is the project's, Show Linked Audio the session's. The Scrubber is shown
    // or hidden as Tabs and Panes does it, the hotbar (its Quick FX button) and the Undo / Redo buttons as the hotbar's own menu does
    // (SaveHotbar: the row of tools shown again as the settings say).
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
