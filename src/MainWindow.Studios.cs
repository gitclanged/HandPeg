using System.Windows;
using HandPegApp.Docking;
using HandPegApp.Views;

namespace HandPegApp;

// ----- Studio dock panes -----
// Subtitle Studio, Voiceover Studio, Render Queue and Video Scopes are first-class panes: listed in Tabs and Panes, dockable anywhere or
// torn off into a side window. As optional panes (DockPane.Optional) they show exactly while the arrangement contains them, are never
// re-added by a load or layout that omits them, open beside their HomeWith mates, and close via tab menu, Tabs and Panes, or an arrangement
// change. Content is built by factories on first show (never in PaneLibrary, which is parsed before Frame 1 regardless of arrangement:
// Law 1); constructors stay light.
public partial class MainWindow
{
    /// <summary>Studio pane ids, persisted in saved arrangements: never rename.</summary>
    internal const string SubtitleStudioId = "SubtitleStudio", VoiceoverStudioId = "VoiceoverStudio", RenderQueueId = "RenderQueue", VideoScopesId = "VideoScopes";

    /// <summary>
    /// Studio table: id, title, HomeWith panes (opens as a tab in the first one found; else the window's right column) and the Tabs and
    /// Panes description. Window-free for the dock-optional-panes golden.
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

    /// <summary>Builds a studio DockPane: optional, homed with its mates, always shown while arranged. Window-free (goldens register it too).</summary>
    internal static DockPane StudioPane(string id, Func<FrameworkElement> content, Action? close, double minWidth, double minHeight)
    {
        var studio = Studios.First(s => s.Id == id);
        return new DockPane
        {
            Id = id, Title = studio.Title, Content = content, Handle = PaneHandle.TitleSlot, IsShown = () => true, Close = close,
            MinWidth = () => minWidth, MinHeight = minHeight, Optional = true, HomeWith = studio.HomeWith,
        };
    }

    // Voiceover Studio instance once built; closing it finishes any active recording.
    private VoiceoverStudioPane? _voiceoverPane;

    /// <summary>Registers the studio panes with the dock (from InitializeDocking, before the single dock build).</summary>
    private void RegisterStudioPanes()
    {
        // Wrap each in a "Panel"-styled border, like PaneLibrary panes.
        FrameworkElement Panel(FrameworkElement child)
        {
            var panel = new System.Windows.Controls.Border { Child = child };
            panel.SetResourceReference(StyleProperty, "Panel");
            return panel;
        }

        // Minimum widths (splitter range): 240 for Subtitle Studio, Voiceover Studio and Render Queue, where their rows (cue text and
        // times, recorder buttons, job name and buttons) still fit; 180 (the standard pane minimum) for Video Scopes.
        _dock.Register(StudioPane(SubtitleStudioId, () => Panel(new SubtitleStudioPane(_viewModel, new StudioHost(this))), () => CloseStudio(SubtitleStudioId), 240, 180));
        _dock.Register(StudioPane(VoiceoverStudioId, () => Panel(_voiceoverPane = new VoiceoverStudioPane(_viewModel)), () => CloseStudio(VoiceoverStudioId), 240, 160));
        _dock.Register(StudioPane(RenderQueueId, () => Panel(new RenderQueuePane()), () => CloseStudio(RenderQueueId), 240, 140));
        _dock.Register(StudioPane(VideoScopesId, () => Panel(new VideoScopesPane(_viewModel, CaptureForScopes)), () => CloseStudio(VideoScopesId), 180, 140));
    }

    /// <summary>
    /// Small paused-player frame for the Video Scopes (null while playing or without a player; GpuPreviewEngine.CaptureThumbnailAsync).
    /// A pending save or tab thumbnail goes first: the engine holds one request and a newer one cancels the older, so the scopes wait
    /// (up to one second) instead.
    /// </summary>
    private async Task<(byte[] Bgra, int Width, int Height)?> CaptureForScopes()
    {
        for (var waited = 0; _gpu is { IsThumbnailWaiting: true } && waited < 1000; waited += 50)
            await Task.Delay(50);
        return _gpu is { IsThumbnailWaiting: false } engine ? await engine.CaptureThumbnailAsync(256) : null;
    }

    /// <summary>Studio entries for Tabs and Panes (appended to PaneToggles), checked while the pane is arranged.</summary>
    private IEnumerable<ViewToggle> StudioToggles() => Studios.Select(s => new ViewToggle(s.Title, () => _dock.Contains(s.Id), on =>
    {
        if (on)
            OpenStudio(s.Id);
        else
            CloseStudio(s.Id);
    }, s.About));

    /// <summary>Shows a studio: adds it beside its mates if not arranged, then activates its tab.</summary>
    private void OpenStudio(string id) => _dock.Open(id);

    /// <summary>
    /// After a layout is applied (kept card, first-run choice, Reset Panes Layout; never a card preview): finishes any recording whose
    /// Voiceover Studio the new arrangement omits, so the microphone never records without a visible Stop.
    /// </summary>
    private void FinishRecordingUnlessShown()
    {
        if (!_dock.Contains(VoiceoverStudioId))
            _voiceoverPane?.FinishRecording();
    }

    /// <summary>Removes a studio from the arrangement (tab Close, Tabs and Panes); Voiceover Studio finishes recording first.</summary>
    private void CloseStudio(string id)
    {
        if (id == VoiceoverStudioId)
            _voiceoverPane?.FinishRecording();
        _dock.Close(id);
    }

    /// <summary>Opens the Render Queue (from the work flyout's Queue or the Export tab).</summary>
    private void OpenRenderQueue() => OpenStudio(RenderQueueId);

    /// <summary>Opens the Voiceover Studio (Add Track's Record Voiceover, Audio tab button); a pane so recording happens alongside the video.</summary>
    private void OpenVoiceoverStudio() => OpenStudio(VoiceoverStudioId);

    /// <summary>Subtitle Studio host adapter: player Play/Pause, window Undo/Redo, and timeline redraw.</summary>
    private sealed class StudioHost(MainWindow window) : ISubtitleStudioHost
    {
        public void TogglePlay() => window.PlayPause_Click(window, new RoutedEventArgs());

        public void Undo(bool redo) => window.RunUndo(redo);

        public void SubtitlesCommitted() => window.ScheduleClipRedraw();
    }
}
