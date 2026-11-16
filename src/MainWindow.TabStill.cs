using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Pre-captured paused still for tab switches -----
// A backgrounded tab keeps a still of its paused picture, shown over the player on reactivation until the engine renders its first frame
// (MainWindow.ProjectTabs.cs). Switches never wait for one: the still is captured ahead in the background, 350 ms after the playhead or
// picture last changed, only while paused, with no Timeline drag and no switch in progress, and only after the project tab strip has been
// shown (never before Frame 1: Law 1). Slight staleness is acceptable since it only covers the player until the first frame. Capture
// is an engine GPU readback (Law 4), at most one at a time, and never displaces a pending save capture.
public partial class MainWindow
{
    /// <summary>Settle delay after the last change before capturing the still, ms.</summary>
    private const int StillSettleMs = 350;

    // Active tab's latest still and its state mark; null when none (new project, file opened, or playback since).
    private BitmapSource? _liveStill;
    private StillMark _liveStillMark;

    private DispatcherTimer? _stillSettle;
    private int _stillAsked;

    /// <summary>Starts keeping the still fresh (idempotent; called once the tab strip is shown, i.e. tabs exist to switch between).</summary>
    private void KeepTabStillFresh()
    {
        if (_stillSettle is not null)
            return;

        _stillSettle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(StillSettleMs) };
        _stillSettle.Tick += (_, _) =>
        {
            _stillSettle.Stop();
            _ = TakeLiveStillAsync();
        };
        _viewModel.PreviewStateInvalidated += NoteStillStale;
        _viewModel.TimelineChanged += NoteStillStale;
        _viewModel.MediaLoaded += _ => ForgetLiveStill();
        _viewModel.SessionCleared += ForgetLiveStill;
        _viewModel.PropertyChanged += (_, e) =>
        {
            // Playback started: the paused picture is gone; drop the still and invalidate any capture in flight.
            if (e.PropertyName is nameof(MainViewModel.IsPlaying) && _viewModel.IsPlaying)
                ForgetLiveStill();
            if (e.PropertyName is nameof(MainViewModel.PositionMs) or nameof(MainViewModel.IsPlaying))
                NoteStillStale();
        };
        NoteStillStale();
    }

    /// <summary>
    /// Picture may have changed: re-arm the settle timer (not while playing, to avoid per-frame re-arming). Also called on 3D view
    /// entry/exit and camera moves (MainWindow.Exploded.cs).
    /// </summary>
    private void NoteStillStale()
    {
        if (_stillSettle is null || _viewModel.IsPlaying)
            return;
        _stillSettle.Stop();
        _stillSettle.Start();
    }

    /// <summary>Discards the still when the active tab's content changes (file or project opened, new project).</summary>
    private void ForgetLiveStill()
    {
        (_liveStill, _liveStillMark) = (null, default);
        _stillAsked++;
    }

    /// <summary>Max wait for an on-screen readback when a tab is backgrounded without a current still, ms (a few frames).</summary>
    private const int StillOnScreenWaitMs = 40;

    /// <summary>
    /// Active tab is being backgrounded without a current still (it was playing, or the still had not settled): read back the presented
    /// frame immediately (GpuPreviewEngine.SnapshotPresented) so reactivation never shows an uncovered player. A soft snapshot (the reduced
    /// playback-resolution frame, used when the full one could not be read in time) covers the player like any still, but the next quiet
    /// capture or switch replaces it with a full one, except while that soft still is itself displayed (a tab passed through before its first
    /// frame: the screen then shows the emptied player, not the tab).
    /// </summary>
    private void TakeStillOnScreen()
    {
        var mark = new StillMark(_viewModel.UndoMark, _viewModel.PositionMs, _explodedUi);
        if (_liveStill is not null && (!_liveStillMark.Soft || _tabStill is { Visibility: System.Windows.Visibility.Visible }) && ReferenceEquals(_liveStillMark.UndoMark, mark.UndoMark)
            && StillFits(_liveStillMark.PositionMs, _liveStillMark.Shown3D, mark.PositionMs, mark.Shown3D, _viewModel.CurrentProjectSettings.FrameRate.Fps))
            return;
        if (_gpu?.SnapshotPresented(StillShortSide, StillOnScreenWaitMs, out var soft) is { } frame)
            (_liveStill, _liveStillMark) = (StillOf(frame.Bgra, frame.Width, frame.Height), mark with { Soft = soft });
    }

    /// <summary>Captures a fresh still when stale and the editor is idle; kept only if the same tab stays active and unchanged meanwhile.</summary>
    private async Task TakeLiveStillAsync()
    {
        if (_viewModel.IsPlaying || _switchingTab || TimelineDragHeld || !_viewModel.HasSource || _gpu is not { IsPlaying: false, IsThumbnailWaiting: false } engine)
            return;

        // A soft still's mark has Soft set, so it never matches and is recaptured at full resolution.
        var mark = new StillMark(_viewModel.UndoMark, _viewModel.PositionMs, _explodedUi);
        if (_liveStill is not null && _liveStillMark == mark)
            return;

        var (asked, front) = (++_stillAsked, _tabs.Active);
        try
        {
            // In the 3D view, capture the displayed 3D picture so the tab reactivates in it.
            if (await engine.CaptureThumbnailAsync(StillShortSide, asShown: mark.Shown3D) is not { } frame)
                return;
            if (asked != _stillAsked || _switchingTab || !ReferenceEquals(front, _tabs.Active) || !ReferenceEquals(mark.UndoMark, _viewModel.UndoMark))
                return;
            (_liveStill, _liveStillMark) = (StillOf(frame.Bgra, frame.Width, frame.Height), mark);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the paused picture could not be kept", ex);
        }
    }
}
