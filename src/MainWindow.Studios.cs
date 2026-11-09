using System.Windows;
using HandPegApp.Docking;
using HandPegApp.Views;

namespace HandPegApp;

// ----- The studios as dock panes (5.4C) -----
// The Subtitle Studio, the Voiceover Studio, the Render Queue and the Video Scopes, each a first-class pane: listed in Tabs and Panes,
// docked anywhere in the main window or torn off into a side window. They are optional panes (DockPane.Optional): shown exactly while the
// arrangement holds one, never put back by a load or a layout that leaves it out, opened where their HomeWith mates are and closed from
// their tab's menu, Tabs and Panes, or the arrangement changing. Their contents are made by their factories when first shown (never in
// PaneLibrary, which is parsed before the first frame whatever the arrangement says: Law 1), and their constructors stay light.
public partial class MainWindow
{
    /// <summary>The studios' pane ids, kept in saved arrangements: never rename them.</summary>
    internal const string SubtitleStudioId = "SubtitleStudio", VoiceoverStudioId = "VoiceoverStudio", RenderQueueId = "RenderQueue", VideoScopesId = "VideoScopes";

    /// <summary>
    /// The four studios: id, title, the panes each goes in with when opened (a tab of the first one's group found; the right-hand
    /// column of the window when none is there) and what Tabs and Panes says of it. Window-free, for the goldens (dock-optional-panes).
    /// </summary>
    internal static readonly IReadOnlyList<(string Id, string Title, string[] HomeWith, string About)> Studios =
    [
        (SubtitleStudioId, "Subtitle Studio", ["Effects", "Keyframes", "Layout"],
            "The subtitle track's cues on a strip of their own: their words, their times and where each word begins, retimed by dragging. Its playhead is the player's, which shows the captions."),
        (VoiceoverStudioId, "Voiceover Studio", ["Tab:Audio", TimelinePaneId],
            "Records the voiceover from a microphone (or takes an audio file), then trims it, places it and sets its level; the encode mixes it in."),
        (RenderQueueId, "Render Queue", [ExportId, TimelinePaneId],
            "The batch of encodes: started, paused and cleared here, a pending job loaded back into the window with Edit."),
        (VideoScopesId, "Video Scopes", ["Meter", "Effects"],
            "The Histogram, the Luma Waveform or the RGB Parade of the frame in the player, read while it is paused."),
    ];

    /// <summary>A studio's pane: optional, homed with its mates, always shown while the arrangement holds it. Window-free (the goldens register it too).</summary>
    internal static DockPane StudioPane(string id, Func<FrameworkElement> content, Action? close, double minWidth, double minHeight)
    {
        var studio = Studios.First(s => s.Id == id);
        return new DockPane
        {
            Id = id, Title = studio.Title, Content = content, Handle = PaneHandle.TitleSlot, IsShown = () => true, Close = close,
            MinWidth = () => minWidth, MinHeight = minHeight, Optional = true, HomeWith = studio.HomeWith,
        };
    }

    // The Voiceover Studio once made: closing it finishes a recording under way.
    private VoiceoverStudioPane? _voiceoverPane;

    /// <summary>Registers the studio panes with the dock (called from InitializeDocking, before the one dock build).</summary>
    private void RegisterStudioPanes()
    {
        // Each in a panel of its own, as the panes in PaneLibrary are.
        FrameworkElement Panel(FrameworkElement child)
        {
            var panel = new System.Windows.Controls.Border { Child = child };
            panel.SetResourceReference(StyleProperty, "Panel");
            return panel;
        }

        // Their least widths (5.4D, the splitter's range): the Subtitle Studio, the Voiceover Studio and the Render Queue 240, where their rows
        // (a cue's words and times, the recorder's buttons, a job's name and its buttons) still stand whole; the Video Scopes the panes' 180.
        _dock.Register(StudioPane(SubtitleStudioId, () => Panel(new SubtitleStudioPane(_viewModel, new StudioHost(this))), () => CloseStudio(SubtitleStudioId), 240, 180));
        _dock.Register(StudioPane(VoiceoverStudioId, () => Panel(_voiceoverPane = new VoiceoverStudioPane(_viewModel)), () => CloseStudio(VoiceoverStudioId), 240, 160));
        _dock.Register(StudioPane(RenderQueueId, () => Panel(new RenderQueuePane()), () => CloseStudio(RenderQueueId), 240, 140));
        _dock.Register(StudioPane(VideoScopesId, () => Panel(new VideoScopesPane(_viewModel, CaptureForScopes)), () => CloseStudio(VideoScopesId), 180, 140));
    }

    /// <summary>
    /// The paused player's frame, small, for the Video Scopes (null while playing or with no player: GpuPreviewEngine.CaptureThumbnailAsync).
    /// A save's or a tab's thumbnail still waiting goes first (5.4C review): the engine keeps one request, and a newer one gives the older
    /// none; the scopes wait their turn (a second at most) instead.
    /// </summary>
    private async Task<(byte[] Bgra, int Width, int Height)?> CaptureForScopes()
    {
        for (var waited = 0; _gpu is { IsThumbnailWaiting: true } && waited < 1000; waited += 50)
            await Task.Delay(50);
        return _gpu is { IsThumbnailWaiting: false } engine ? await engine.CaptureThumbnailAsync(256) : null;
    }

    /// <summary>The studios' lines in Tabs and Panes (appended to PaneToggles): ticked while the arrangement holds the pane.</summary>
    private IEnumerable<ViewToggle> StudioToggles() => Studios.Select(s => new ViewToggle(s.Title, () => _dock.Contains(s.Id), on =>
    {
        if (on)
            OpenStudio(s.Id);
        else
            CloseStudio(s.Id);
    }, s.About));

    /// <summary>Shows a studio: put in with its mates when the arrangement does not hold it, then brought to the front of its group.</summary>
    private void OpenStudio(string id) => _dock.Open(id);

    /// <summary>
    /// A layout taken up (a card kept, the first-run choice, Reset Panes Layout; never a card's preview): a recording whose Voiceover Studio
    /// the new arrangement leaves out is finished, so the microphone is never left recording with no Stop on screen (5.4C review).
    /// </summary>
    private void FinishRecordingUnlessShown()
    {
        if (!_dock.Contains(VoiceoverStudioId))
            _voiceoverPane?.FinishRecording();
    }

    /// <summary>Takes a studio out of the arrangement (its tab's Close, Tabs and Panes): the Voiceover Studio finishes a recording first.</summary>
    private void CloseStudio(string id)
    {
        if (id == VoiceoverStudioId)
            _voiceoverPane?.FinishRecording();
        _dock.Close(id);
    }

    /// <summary>The Render Queue (the work flyout's Queue, the Export tab's).</summary>
    private void OpenRenderQueue() => OpenStudio(RenderQueueId);

    /// <summary>The Voiceover Studio (Add Layer's Record Voiceover, the Audio tab's button): recording goes with watching the video, so it is a pane.</summary>
    private void OpenVoiceoverStudio() => OpenStudio(VoiceoverStudioId);

    /// <summary>What the Subtitle Studio asks of the window: the player's Play, the window's Undo and Redo, the timeline drawn again.</summary>
    private sealed class StudioHost(MainWindow window) : ISubtitleStudioHost
    {
        public void TogglePlay() => window.PlayPause_Click(window, new RoutedEventArgs());

        public void Undo(bool redo) => window.RunUndo(redo);

        public void SubtitlesCommitted() => window.ScheduleClipRedraw();
    }
}
