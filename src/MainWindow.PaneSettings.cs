using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- A pane's own settings on its tab's right-click menu (Pass 4.2B-2) -----
// Right-clicking a pane's tab (or its hover tab) opens with "<Pane> Settings": the settings that pane's work turns on,
// changed in place (each the same AppSettings value the Settings window shows, saved at once), then "More <Pane>
// Settings..." opening the Settings window at the tab that has the rest. Panes without settings of their own (the
// header, the preset bar, the settings tabs) get no section.
public partial class MainWindow
{
    /// <summary>The section for a pane, put at the top of its tab's menu (DockManager.ExtendTabMenu).</summary>
    private void AddPaneSettingsMenu(ContextMenu menu, string pane)
    {
        var (title, settingsTab) = pane switch
        {
            "Media" => ("Media", "Preview"),
            TimelinePaneId => ("Timeline", "Timeline"),
            "Timeline" => ("Overview Bar", "Timeline"),
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
                Choice("Tile Size", [("List", 0.0), ("Small Tiles", 30.0), ("Medium Tiles", 55.0), ("Large Tiles", 85.0)], NearestTileSize(settings.BrowserTileSize),
                    size => MediaBrowser.TileSize = size, "How large the files are shown (as the size slider in the pane's title row).");
                Choice("Indexer Delay", [("10 Seconds", 10), ("40 Seconds", 40), ("2 Minutes", 120), ("5 Minutes", 300)], settings.BrowserIndexDelaySeconds,
                    seconds => Save(s => s.BrowserIndexDelaySeconds = seconds),
                    "How long the media pane rests before it makes previews of the folder's files in the background.");
                items.Add(MenuAction("Clear Browser Cache", () =>
                {
                    PreviewCache.Browser.Clear();
                    _viewModel.StatusText = "The media browser's previews were cleared: they are made again as they are needed.";
                }, "Deletes the media pane's kept pictures and strips (the Media browser previews cache)."));
                break;

            case "Timeline":
                Toggle("Snap Clips", _viewModel.SnapClips, on => _viewModel.SnapClips = on, "Clips and edges snap to the playhead, markers and other clips' edges. Shift while dragging is never snapped.");
                Toggle("Snap to I-Frames", _viewModel.SnapToIFrames, on => _viewModel.SnapToIFrames = on, "Cuts and the playhead snap to the video's I-frames, so stream copy cuts cleanly.");
                Toggle("Gain and Fade Lines", settings.ShowClipGainLines, on =>
                {
                    Save(s => s.ShowClipGainLines = on);
                    ScheduleClipRedraw();
                }, "Draws each clip's gain line and fade handles over its waveform.");
                Toggle("Ripple All Tracks", settings.RippleAllTracks, on => Save(s => s.RippleAllTracks = on), "A ripple (Ctrl-drag, Delete with ripple) moves what follows on every track, not only the clip's own.");
                Toggle("Dropped Clips Join Track", settings.DroppedClipsJoinTrack, on => Save(s => s.DroppedClipsJoinTrack = on), "A picture dropped on a track with room goes onto that track instead of a new one.");
                Toggle("Show Linked Audio", _viewModel.ShowLinkedAudio, on => _viewModel.ShowLinkedAudio = on, "Draws a video clip's own sound under its pictures, in the lower part of its block.");
                Choice("Track Height",
                    [("Compact", (double)CompactTrackHeight), ("Medium", MediumTrackHeight), ("Tall", TallTrackHeight)],
                    NearestTrackHeight(_viewModel.LayerTrackHeight), height => _viewModel.LayerTrackHeight = height,
                    "How tall each track's row is (the slider in the timeline's toolbar sets any height between).");
                break;

            case "Overview Bar":
                Toggle("Snap to I-Frames", _viewModel.SnapToIFrames, on => _viewModel.SnapToIFrames = on, "Cuts and the playhead snap to the video's I-frames, so stream copy cuts cleanly.");
                Toggle("Sync Overview Bar Stretch", _viewModel.SyncMasterTimeline, on => _viewModel.SyncMasterTimeline = on,
                    "On: the overview bar is zoomed and moved along with the timeline. Off: it always shows the whole sequence.");
                Choice("Overview Bar Height",
                    [("Compact", (double)MainViewModel.CompactMasterTimeline), ("Medium", MainViewModel.MediumMasterTimeline), ("Tall", MainViewModel.TallMasterTimeline)],
                    _viewModel.MasterTimelineHeight, height => _viewModel.MasterTimelineHeight = height,
                    "How tall the overview bar is (Ctrl-drag the border beside it for any height between).");
                break;

            case "Player" or "Canvas":
                Toggle("Edit Layers on Video", _viewModel.EditLayersOnPlayer, on => _viewModel.EditLayersOnPlayer = on, "Move, resize and turn the selected layer on the player itself (with Live Preview).");
                Toggle("Snap Guides", settings.SnapLayoutGuides, on => Save(s => s.SnapLayoutGuides = on), "A dragged layer snaps to the frame's edges, centre and the other layers (Alt while dragging: free).");
                Toggle("Motion Path", settings.ShowMotionPath, on =>
                {
                    Save(s => s.ShowMotionPath = on);
                    RefreshMotionPath();
                }, "Draws the selected layer's path between its position keyframes.");
                Choice("Layers While Scrubbing", AppSettings.ScrubLayerChoices.Select(c => (c, c)), settings.LayersWhileScrubbing,
                    choice => Save(s => s.LayersWhileScrubbing = choice), "What the layers do while the playhead is dragged: play live, hold a still frame, or hide.");
                break;

            default:
                Toggle("Draft Scrub", settings.DraftScrubSkipHeavyEffects, on => Save(s => s.DraftScrubSkipHeavyEffects = on), "While scrubbing, the heaviest effects are left out of the preview so it keeps up; the export always has them.");
                Toggle("Value Graph", _viewModel.ShowKeyframeGraph, on => _viewModel.ShowKeyframeGraph = on, "Draws each keyframe lane's values as a curve.");
                Toggle("Key Changed Only", _viewModel.KeyChangedOnly, on => _viewModel.KeyChangedOnly = on, "A new keyframe is put only on the values that changed, not on every value of the layer.");
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

    /// <summary>The tile size preset nearest a slider value, for the menu's check mark.</summary>
    private static double NearestTileSize(double size) => new[] { 0.0, 30, 55, 85 }.MinBy(s => Math.Abs(s - size));

    // The timeline's track height presets (MainViewModel.LayerTrackHeight, 1 to 50; 25 is HandPeg's own).
    private const int CompactTrackHeight = 8, MediumTrackHeight = 25, TallTrackHeight = 42;

    /// <summary>The track height preset nearest the height set, for the menu's check mark.</summary>
    private static double NearestTrackHeight(double height) =>
        new double[] { CompactTrackHeight, MediumTrackHeight, TallTrackHeight }.MinBy(h => Math.Abs(h - height));
}
