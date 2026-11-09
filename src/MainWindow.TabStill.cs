using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- The paused still kept fresh (Pass 5.4D-TabPerf) -----
// A tab put behind keeps a still of its paused picture, shown over the player at once as it comes back until the engine has drawn its
// first frame (MainWindow.ProjectTabs.cs). A switch never waits for one (the user's U1): the still is taken ahead, in the background, a
// moment (350 ms) after the playhead or the picture last changed, only while nothing plays, nothing is dragged on the Timeline and no
// switch is under way, and only once the project tabs have been shown (never before the first frame: Law 1). A still a little out of
// date is fine: it only covers the player until the first frame. The capture is the engine's (a GPU readback, Law 4), at most one at a
// time, never in place of one a save is waiting for.
public partial class MainWindow
{
    /// <summary>How long after the last change the still is taken, ms.</summary>
    private const int StillSettleMs = 350;

    // The tab in front's still as last taken, and its moment; null while there is none (a new project, a file opened, playback since).
    private BitmapSource? _liveStill;
    private StillMark _liveStillMark;

    private DispatcherTimer? _stillSettle;
    private int _stillAsked;

    /// <summary>The still kept fresh from now on (once: the strip has been shown, so there are tabs to switch between).</summary>
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
            // Playback started: the paused picture is gone from the player (5.4D-TabPerf review), and one being taken is dropped as it comes in.
            if (e.PropertyName is nameof(MainViewModel.IsPlaying) && _viewModel.IsPlaying)
                ForgetLiveStill();
            if (e.PropertyName is nameof(MainViewModel.PositionMs) or nameof(MainViewModel.IsPlaying))
                NoteStillStale();
        };
        NoteStillStale();
    }

    /// <summary>
    /// The picture may have changed: the still is taken again once it settles (never re-armed frame by frame while playing). The 5.4D-TabPerf
    /// review: also the 3D view entered or left and its camera moved (MainWindow.Exploded.cs).
    /// </summary>
    private void NoteStillStale()
    {
        if (_stillSettle is null || _viewModel.IsPlaying)
            return;
        _stillSettle.Stop();
        _stillSettle.Start();
    }

    /// <summary>Another project in front of the same tab (a file or project opened, a new one): the still of the one before goes.</summary>
    private void ForgetLiveStill()
    {
        (_liveStill, _liveStillMark) = (null, default);
        _stillAsked++;
    }

    /// <summary>The still taken now when it is out of date and the editor is quiet; kept only if the same tab is still in front, unchanged meanwhile.</summary>
    private async Task TakeLiveStillAsync()
    {
        if (_viewModel.IsPlaying || _switchingTab || TimelineDragHeld || !_viewModel.HasSource || _gpu is not { IsPlaying: false, IsThumbnailWaiting: false } engine)
            return;

        var mark = new StillMark(_viewModel.UndoMark, _viewModel.PositionMs, _explodedUi);
        if (_liveStill is not null && _liveStillMark == mark)
            return;

        var (asked, front) = (++_stillAsked, _tabs.Active);
        try
        {
            // In the 3D view, the still is the 3D picture as it is shown (5.4D): the tab comes back in it.
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
