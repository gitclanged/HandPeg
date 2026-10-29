using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// ----- Player: libmpv, Live Preview scheduling, the paused sound settle, J/K/L, scrubbing, solo and waveform seeks -----
public partial class MainWindow
{
    // ----- The player: libmpv -----
    // mpv draws into the video surface, reports where it is as it plays, and can run an FFmpeg filter graph on
    // the picture (its lavfi-complex property). That graph is Live Preview: with the box ticked it is the one
    // the export would use for the picture, rebuilt whenever a setting changes; unticked, there is none and
    // the source is shown as it is.

    private MpvPlayer? _mpv;
    private bool _startingPlayer;
    private bool _shellDrawn;

    // What to open once the player exists: a video loaded before it had started, or the one a reload is bringing back.
    private (string Path, long StartMs, bool Paused)? _pendingMedia;

    // The graph mpv is running. A graph holds the stretch of the sequence around the moment it was built for
    // (see FilterGraphBuilder.BuildLiveGraph): it was built with the settings as they were (_liveStamp) at
    // _liveBuiltAtMs, and is good until _liveRebuildAt while playing forwards.
    private string _liveGraph = "";
    private LiveStamp _liveStamp;
    private long _liveBuiltAtMs;
    private double _liveRebuildAt = double.PositiveInfinity;

    // The settings that mpv refused a graph for: no graph is offered for them again until they change.
    private LiveStamp? _failedLiveStamp;

    // Counts the changes to anything a graph is built from, so that a graph can tell it is still the one wanted.
    private int _liveStateVersion;

    /// <summary>What a live graph was built from, besides the moment: the settings (by their count of changes), the player's size, and what it shows.</summary>
    private readonly record struct LiveStamp(int Version, bool Live, int Width, int Height, LiveLayerMode Layers, long? AudioFrom)
    {
        public bool SameSettings(LiveStamp other) => (Version, Live, Width, Height, Layers) == (other.Version, other.Live, other.Width, other.Height, other.Layers);
    }

    // Sliders report every step of a drag; the graph is rebuilt once they have been still for a moment.
    private readonly System.Windows.Threading.DispatcherTimer _liveFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private bool PlayerIsPlaying => _mpv is { IsPlaying: true };

    // Whether the paused player was last sent to an exact moment by the user, which the playhead then keeps.
    private bool _pausedOnUsersMoment;

    private void PlayerSetPause(bool paused) => _mpv?.SetPause(paused);

    /// <summary>
    /// Sends the player to a moment. A live graph belongs to its moment (its video layers start reading their
    /// files there), so the graph for the new moment goes with the seek, and mpv runs it from there: see
    /// ApplyLiveFilter. A graph that comes out the same is not sent again, and the seek goes alone.
    /// </summary>
    private void PlayerSeek(long positionMs, bool exact = true)
    {
        if (_mpv is null)
            return;

        // A jump while paused is answered by the quick graph without sound; the one ready to play follows when it settles.
        // The moment it was sent to is the user's from then on (see OnPlayerTimeChanged).
        if (!PlayerIsPlaying)
            (_soundSettled, _pausedOnUsersMoment) = (false, exact);

        // The layers' sound is laid out in the graph from one moment on: after a jump it is laid out again, from
        // where the jump lands.
        if (LiveGraphHasSound)
            _liveAudioSeekMs = positionMs;
        CrashReporter.Note("seek", string.Create(CultureInfo.InvariantCulture, $"to {positionMs / 1000.0:0.0} s{(exact ? "" : " (to an I-frame)")}"));
        ApplyLiveFilter((positionMs, exact));
    }

    /// <summary>How video layers are shown while the playhead is dragged or wound back (Settings, "Video layers while scrubbing").</summary>
    private static LiveLayerMode ScrubLayerMode => AppSettings.Current.LayersWhileScrubbing switch
    {
        AppSettings.ScrubLayersLive => LiveLayerMode.Live,
        AppSettings.ScrubLayersHidden => LiveLayerMode.Hidden,
        _ => LiveLayerMode.Still,
    };

    /// <summary>
    /// The video surface (a native window, which nothing of WPF's can be drawn over) is shown only while there is a
    /// video and a player to draw it. Otherwise it is hidden (it keeps its window, and the player its surface), and
    /// what lies under it in the pane shows: the drop hint while nothing is loaded, the engine notice while the player
    /// is being downloaded. Being content of the pane, they belong to whichever window holds the player.
    /// </summary>
    private void UpdateVideoVisibility()
    {
        VideoView.Visibility = _viewModel.HasSource && _mpv is not null ? Visibility.Visible : Visibility.Hidden;
        DropHint.Visibility = _viewModel.HasSource ? Visibility.Collapsed : Visibility.Visible;
    }

    // Whether anything has asked for the player yet: a video to open, a file dragged over the window, the Source
    // box or Browse being used. Until then libmpv is not even loaded (it and the graphics driver it starts are
    // well over a hundred megabytes of an idle window).
    private bool _playerWanted;

    /// <summary>Starts the player ahead of need: something is about to be opened (a file is being dragged in, a path typed).</summary>
    private void WarmUpPlayer()
    {
        if (_mpv is not null || _playerWanted)
            return;

        _playerWanted = true;
        _ = EnsurePlayerAsync();
    }

    /// <summary>
    /// Starts the player when there is none yet and something wants it (see _playerWanted). Loading libmpv and
    /// starting it take a moment, and happen on the thread pool: the window is never held up by them. Without
    /// libmpv (not downloaded yet) there is simply no player; everything else works, and this is tried again
    /// when the dependencies change.
    /// </summary>
    private async Task EnsurePlayerAsync()
    {
        if (_mpv is not null || _startingPlayer || !_shellDrawn)
            return;

        // Not there yet: it is fetched by itself, with a notice in the empty player saying so.
        if (!MpvPlayer.IsInstalled)
        {
            _ = FetchPlayerAsync();
            return;
        }

        if (!_playerWanted && _pendingMedia is null && !_viewModel.HasSource)
            return;

        var surface = VideoView.SurfaceHandle;
        if (surface == IntPtr.Zero)
            return;

        EngineNotice.Visibility = Visibility.Collapsed;

        _startingPlayer = true;
        try
        {
            var acceleration = AppSettings.Current.PlayerHardwareAcceleration;
            var startedAt = Environment.TickCount64;
            var player = await Task.Run(() => MpvPlayer.Create(surface, acceleration));
            AppLog.Write($"Player started in {Environment.TickCount64 - startedAt} ms.");

            // mpv raises its events on a thread of its own; they are passed on only while this is still the player.
            void OnUi(Action action) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(player, _mpv))
                    action();
            });

            // The position: while playing it is read once per screen refresh (see FollowPlayerByFrame); otherwise
            // one look is asked for when it moves, however many reports come in before the UI thread gets to it.
            player.PositionPending += () =>
            {
                if (!_followingByFrame)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
                    {
                        if (ReferenceEquals(player, _mpv))
                            TakePlayerPosition();
                    });
            };
            player.GraphRefused += reason => OnUi(() => OnPlayerError($"lavfi-complex: {reason}"));

            // The timeline's length is the sequence's, which the view model works out; the player's word is
            // only needed for a file that could not be inspected.
            player.DurationChanged += length => OnUi(() => _viewModel.ReportPlayerDuration(length / 1000.0, _playerSource == _viewModel.LocalMediaPath));
            player.StateChanged += () => OnUi(() =>
            {
                var wasPlaying = _viewModel.IsPlaying;
                _viewModel.IsPlaying = player.IsPlaying;
                FollowPlayerByFrame(player.IsPlaying);
                if (wasPlaying && player.IsEnded)
                    OnPlaybackEnded();

                // The layers' sound is in the graph while it plays: it comes in as playback starts (unless the paused
                // player has it already, see ArmSoundSettle), from where it starts. Paused, the graph with sound is
                // kept as it is, ready to play on: it goes only with the next jump or edit.
                if (wasPlaying && !player.IsPlaying && LiveGraphHasSound)
                    _soundSettled = true;
                if (wasPlaying != player.IsPlaying && _viewModel.LivePreview && (player.IsPlaying || LiveGraphHasSound))
                    ApplyLiveFilter();
            });
            player.FileLoaded += () => OnUi(() =>
            {
                // Opening a file takes the graph off; it is given again, for this file.
                ForgetLiveGraph();
                player.SetAudioFilter(_viewModel.PlayerAudioFilter);
                ApplyAudioTrack();
                if (!SyncPlayerSource())
                    ApplyLiveFilter();
            });
            player.ErrorLogged += text => OnUi(() => OnPlayerError(text));

            _mpv = player;
            UpdateVideoVisibility();
            ForgetLiveGraph();
            _failedLiveStamp = null;
            player.SetVolume(_viewModel.Volume);
            player.SetMute(_viewModel.IsPlayerMuted);
            player.SetSpeed(_viewModel.PlaybackRate);

            // A video may have been loaded while the player was still starting.
            if (_pendingMedia is null && _viewModel.HasSource && File.Exists(_viewModel.LocalMediaPath))
                _pendingMedia = (_viewModel.PlayerSource, (long)_viewModel.PositionMs, true);
            if (_pendingMedia is { } media)
            {
                _pendingMedia = null;
                _playerSource = media.Path;
                player.Open(media.Path, media.StartMs, media.Paused);
            }
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Write("The player could not be started", ex);
            _viewModel.StatusText = $"The video player could not be started: {ex.Message}";
        }
        finally
        {
            _startingPlayer = false;
        }
    }

    private readonly CancellationTokenSource _windowClosing = new();
    private bool _fetchingPlayer;
    private bool _playerFetchFailed;

    /// <summary>
    /// Downloads libmpv, the video engine, when it is missing: by itself, in the background, with a notice
    /// over the empty player so that it is clear why there is no picture yet. Tried once per session; when
    /// it fails the notice says so, and the dependency list in the settings is the way to try again.
    /// </summary>
    private async Task FetchPlayerAsync()
    {
        if (_fetchingPlayer || _playerFetchFailed)
            return;

        _fetchingPlayer = true;
        (EngineNotice.Visibility, EngineProgress.Visibility, EngineProgress.IsIndeterminate) = (Visibility.Visible, Visibility.Visible, true);
        EngineNoticeTitle.Text = "Downloading Video Engine...";
        EngineNoticeText.Text = "The player (libmpv, about 31 MB) is fetched once. Everything else can be used meanwhile.";
        try
        {
            var token = _windowClosing.Token;
            var latest = await DependencyUpdater.GetLatestAsync(DependencyUpdater.Mpv, token);
            var progress = new Progress<InstallProgress>(report =>
            {
                EngineProgress.IsIndeterminate = report.Fraction < 0;
                EngineProgress.Value = Math.Clamp(report.Fraction, 0, 1);
                EngineNoticeText.Text = report.Text;
            });
            await Task.Run(() => DependencyUpdater.InstallToolAsync(DependencyUpdater.Mpv, latest, progress, token), token);
            _viewModel.OnDependenciesChanged();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
        {
            AppLog.Write("The video engine could not be downloaded", ex);
            _playerFetchFailed = true;
            EngineProgress.Visibility = Visibility.Collapsed;
            EngineNoticeTitle.Text = "Video Engine Not Installed";
            EngineNoticeText.Text = $"It could not be downloaded ({ex.Message}). Check the connection, then open Settings (the gear button) and press Install / Update All Dependencies.";
            return;
        }
        finally
        {
            _fetchingPlayer = false;
        }

        await EnsurePlayerAsync();
    }

    /// <summary>
    /// Opens a newly loaded source in the player, paused on its first frame (a project or a file opened is looked at
    /// before it is played, Pass 4.1C).
    /// </summary>
    private void PlayMedia(string path)
    {
        if (!File.Exists(path))
        {
            _viewModel.StatusText = $"The file to play is missing: {path}";
            return;
        }

        StopShuttle();
        OpenInPlayer(_viewModel.PlayerSource, 0, paused: true);

        // A new video is seen whole on the time bars.
        FitTimeline(announce: false);
    }

    // What the player has open: the main video's file, or the sequence written out for it (see PlayerSource).
    private string _playerSource = "";

    /// <summary>
    /// Has the player open the sequence as it is now, when that is no longer what it has open: the main video
    /// was moved or trimmed, or a clip now reaches past its end. It carries on from where it was. Returns
    /// whether it had to.
    /// </summary>
    private bool SyncPlayerSource()
    {
        if (_mpv is not { IsLoading: false } player || !_viewModel.HasSource || _reloadingPlayer)
            return false;

        var source = _viewModel.PlayerSource;
        if (source == _playerSource || !FileState.Exists(_viewModel.LocalMediaPath))
            return false;

        OpenInPlayer(source, (long)_viewModel.PositionMs, paused: !player.IsPlaying);
        return true;
    }

    private void OpenInPlayer(string path, long startMs, bool paused)
    {
        _playerSource = path;
        if (_mpv is not { } player)
        {
            _pendingMedia = (path, startMs, paused);
            _ = EnsurePlayerAsync();
            return;
        }

        // The graph belongs to the file that was playing; the new one gets its own once it is open.
        _liveFilterTimer.Stop();
        ForgetLiveGraph();
        _failedLiveStamp = null;
        player.DropFilterGraph();
        player.SetFiltering(false);
        player.Open(path, startMs, paused);
    }

    private bool _reloadingPlayer;

    /// <summary>
    /// Reload Player: for when the player has locked up and the timeline no longer responds. The player is
    /// replaced with a new one, and the video is opened again where it was. The old one is told to quit and
    /// is not waited for: a player that is truly stuck would otherwise take the window down with it.
    /// </summary>
    private async void ReloadPlayer_Click(object sender, RoutedEventArgs e)
    {
        var path = _viewModel.LocalMediaPath;
        if (_reloadingPlayer || !_viewModel.HasSource || !File.Exists(path))
        {
            if (!_reloadingPlayer)
                _viewModel.StatusText = "There is no video loaded to reload.";
            return;
        }

        _reloadingPlayer = true;
        var (resumeAt, wasPlaying) = ((long)_viewModel.PositionMs, _viewModel.IsPlaying);
        _viewModel.StatusText = "Reloading the player...";

        var retired = _mpv;
        _mpv = null;
        _isScrubbing = false;
        _viewModel.IsPlaying = false;
        FollowPlayerByFrame(false);
        retired?.Close();
        UpdateVideoVisibility();

        StopShuttle();
        _pendingMedia = (_viewModel.PlayerSource, resumeAt, !wasPlaying);
        await EnsurePlayerAsync();
        if (_mpv is not null)
            _viewModel.StatusText = $"Player reloaded at {_viewModel.PositionText}.";
        _reloadingPlayer = false;
    }

    // ----- Live Preview -----

    /// <summary>A setting changed: the graph is rebuilt shortly, once the changes have stopped coming.</summary>
    private void ScheduleLiveFilter()
    {
        // Also with Live Preview off: what the player has open follows the sequence either way.
        // An edit while paused is shown by the quick graph first, as a jump is; whether it changed the graph at all is
        // known only once it is built (ApplyLiveFilterNow), so the graph with sound is not given up here (4.1.1: a
        // finished waveform, a refreshed row or a proxy made swapped to the quick graph and back, each swap moving the
        // paused frame on).
        if (_mpv is null)
            return;

        _liveFilterTimer.Stop();
        _liveFilterTimer.Start();
    }

    private void ClearLiveFilter(MpvPlayer player)
    {
        _liveFilterTimer.Stop();
        _failedLiveStamp = null;
        if (_liveGraph.Length == 0)
            return;

        ForgetLiveGraph();
        player.SetFilterGraph("");
        player.SetFiltering(false);
    }

    /// <summary>The player has no graph of ours (a file was just opened, or it was taken off).</summary>
    private void ForgetLiveGraph()
    {
        (_liveGraph, _liveStamp, _liveRebuildAt) = ("", default, double.PositiveInfinity);
        _liveAudioFromMs = null;
    }

    private void ApplyLiveFilter() => ApplyLiveFilter(null);

    /// <summary>Whether a graph for <paramref name="stamp"/> differs from the running one only in the player's size.</summary>
    private bool SameButSize(LiveStamp stamp) =>
        _liveGraph.Length > 0 && stamp with { Width = _liveStamp.Width, Height = _liveStamp.Height } == _liveStamp;

    // ----- Paused, ready to play -----
    // A graph with the layers' sound in it (an [ao]) is what playing needs; one without jumps about far faster. So a
    // seek or an edit while paused gets the graph without sound at once, and once it has been still for a moment the
    // graph with sound replaces it, run from the paused frame: pressing Play then finds the graph it wants already
    // running and only unpauses (no graph change, no file opened again on the click).

    private const int SoundSettleMs = 300;
    private readonly System.Windows.Threading.DispatcherTimer _liveSoundSettle = new() { Interval = TimeSpan.FromMilliseconds(SoundSettleMs) };
    private bool _liveSoundSettleHooked;

    // Whether the paused player has settled, so the graph may carry the sound as it would while playing; and whether
    // the graph being given is that one, made ahead of need (a seek or an edit asked meanwhile does not wait for it).
    private bool _soundSettled, _readyingToPlay;

    /// <summary>After a graph was given while paused without the sound playing would want: the one with sound follows, once nothing moves for a moment.</summary>
    private void ArmSoundSettle()
    {
        _liveSoundSettle.Stop();
        if (_soundSettled || _mpv is not { IsLoading: false, IsPlaying: false } || !_viewModel.HasSource || !_viewModel.LivePreview
            || _isScrubbing || _shuttle != 0 || _viewModel.SoloTrack is not null || _liveSoundRefused || LiveGraphHasSound)
            return;

        if (!_liveSoundSettleHooked)
        {
            _liveSoundSettle.Tick += (_, _) =>
            {
                _liveSoundSettle.Stop();
                if (_mpv is not { IsPlaying: false } || _isScrubbing || _shuttle != 0)
                    return;

                _soundSettled = true;
                _readyingToPlay = true;
                try
                {
                    ApplyLiveFilter();
                }
                finally
                {
                    _readyingToPlay = false;
                }
            };
            _liveSoundSettleHooked = true;
        }

        _liveSoundSettle.Start();
    }

    /// <summary>
    /// Hands mpv the filter graph for the settings as they are now, when it is not the one already running:
    /// the export's graph while Live Preview is ticked, none while it is not.
    ///
    /// A graph is built for a moment (see FilterGraphBuilder.BuildLiveGraph): what is on screen around it, its
    /// video layers reading their files from there. With <paramref name="seekTo"/> the player is being sent
    /// somewhere, and the graph is built for that moment and goes with the seek (mpv runs it from there). Without,
    /// it is built for where the player is, and only sent when something it is built from has changed, or the
    /// player has moved out of what the running graph holds; a graph that is still good is left running, though
    /// built now it would read its files from a later moment.
    /// </summary>
    private void ApplyLiveFilter((long Ms, bool Exact)? seekTo)
    {
        try
        {
            ApplyLiveFilterNow(seekTo);
        }
        finally
        {
            ArmSoundSettle();
        }
    }

    private void ApplyLiveFilterNow((long Ms, bool Exact)? seekTo)
    {
        _liveFilterTimer.Stop();

        // A file still being opened gets its graph when it is ready (see FileLoaded), not before.
        if (_mpv is not { IsLoading: false } player || !_viewModel.HasSource)
        {
            if (seekTo is { } jump)
                _mpv?.Seek(jump.Ms, jump.Exact);
            return;
        }

        // The sequence has changed shape: it is opened afresh, and gets its graph when that is done.
        if (SyncPlayerSource())
            return;

        // Preview Subtitles: the player draws the captions itself, with Live Preview on or off.
        player.SetSubtitleFile(_viewModel.LiveCaptionsPath);

        // Built for the player as large as it is on screen, in real pixels.
        var dpi = VisualTreeHelper.GetDpi(this);
        var (width, height) = (VideoView.ActualWidth * dpi.DpiScaleX, VideoView.ActualHeight * dpi.DpiScaleY);

        // While a rectangle is being drawn on the video (the crop, or a layer's place in the source) the
        // source itself is shown: what is drawn is a part of it, not of the finished frame.
        var live = _viewModel.LivePreview && CurrentOverlayMode == OverlayMode.None;

        // Dragged or wound back, the video layers are shown as the setting says (one frame each, by default).
        var scrubbing = _isScrubbing || _shuttle < 0;
        var layers = scrubbing ? ScrubLayerMode : LiveLayerMode.Live;
        var at = seekTo?.Ms ?? (long)_viewModel.PositionMs;
        LiveGraph Build(long? audioFromMs) => live
            ? _viewModel.BuildLiveFilterGraph(width, height, audioFromMs, at, layers, scrubbing && AppSettings.Current.DraftScrubSkipHeavyEffects)
            : new LiveGraph("", double.PositiveInfinity);

        // The sound of the layers is part of the graph while the video plays forwards, and while it is paused and
        // has settled (see ArmSoundSettle), ready to play: dragged, wound back or just sent somewhere, nothing is
        // heard anyway, and a graph without sound in it jumps about far faster.
        // It is laid out from one moment on. While nothing calls for another, that moment is kept, so that the
        // graph compares equal to the one running; a jump, or any change to the graph, lays it out afresh
        // from where playback is.
        // Not while one track is played on its own either, nor after mpv has turned such a graph down.
        // Paused and settled with the sound in, and no seek: a graph that would come out the same is left running (the
        // version of the settings is taken up); only a change to the picture or the sound goes to the quick graph first.
        if (seekTo is null && _soundSettled && !player.IsPlaying && !_readyingToPlay && !scrubbing && LiveGraphHasSound
            && _liveAudioSeekMs is null && _liveAudioFromMs is { } settledFrom)
        {
            var settled = new LiveStamp(_liveStateVersion, live, (int)width, (int)height, layers, settledFrom);
            if (SameButSize(settled) || Build(settledFrom).Text == _liveGraph)
            {
                _liveStamp = settled with { Width = _liveStamp.Width, Height = _liveStamp.Height };
                ApplyAudioTrack();
                return;
            }

            _soundSettled = false;
        }

        var wantsSound = (player.IsPlaying || _soundSettled) && !scrubbing && _viewModel.SoloTrack is null && !_liveSoundRefused;
        var (jumpedTo, now) = (_liveAudioSeekMs ?? seekTo?.Ms, at);
        _liveAudioSeekMs = null;
        long? kept = wantsSound && jumpedTo is null && LiveGraphHasSound ? _liveAudioFromMs : null;
        long? from = wantsSound ? kept ?? jumpedTo ?? now : null;

        // Nothing it is built from has changed, and the player is still within what the running graph holds:
        // it stays. (Built afresh it would differ only in where its layers start reading.)
        // Nor for the player's size alone while paused or while the Layout selector previews layouts (4.1.1): mpv scales
        // the running graph's frames to the surface, and a new graph moved the paused frame on with every card the
        // pointer crossed. The new size is taken up by the next graph that is built for anything else.
        var stamp = new LiveStamp(_liveStateVersion, live, (int)width, (int)height, layers, from);
        var sameOrSize = stamp == _liveStamp || SameButSize(stamp) && (!player.IsPlaying || LayoutSelector.IsOpen);
        if (seekTo is null && _liveGraph.Length > 0 && sameOrSize && at >= _liveBuiltAtMs - FilterGraphBuilder.LiveLookBehind * 1000 && at < _liveRebuildAt * 1000)
        {
            ApplyAudioTrack();
            return;
        }

        // Settings that mpv could not run a graph for: the source is shown until they change.
        if (live && _failedLiveStamp is { } failed && failed.SameSettings(stamp))
        {
            if (seekTo is { } jump)
                player.Seek(jump.Ms, jump.Exact);
            return;
        }

        var graph = Build(from);

        // With the layers' sound in the graph, the silence where the main video is not there is part of the
        // graph too; as a filter of the player's it would silence the layers with it.
        var hasSound = graph.Text.Contains(FilterGraphBuilder.LiveAudioOutput, StringComparison.Ordinal);
        player.SetAudioFilter(hasSound ? "" : _viewModel.PlayerAudioFilter);
        if (graph.Text == _liveGraph)
        {
            // A track muted or heard again changes which one the player plays, graph or no graph.
            ApplyAudioTrack();
            (_liveStamp, _liveBuiltAtMs, _liveRebuildAt) = (stamp, at, graph.RebuildAt);
            if (seekTo is { } jump)
                player.Seek(jump.Ms, jump.Exact);
            return;
        }

        if (hasSound && kept is not null && kept != now)
        {
            from = now;
            graph = Build(from);
            stamp = stamp with { AudioFrom = from };
        }

        // Filters work on frames in main memory, so decoding hands them over there while a graph is running.
        if (graph.Text.Length > 0)
            player.SetFiltering(true);

        (_liveGraph, _liveAudioFromMs) = (graph.Text, hasSound ? from : null);
        (_liveStamp, _liveBuiltAtMs, _liveRebuildAt) = (stamp, at, graph.RebuildAt);

        // Where the graph is run from: where the player is being sent; with sound, the moment the sound was laid
        // out from, so that the main video's own sound and the layers' set off together; otherwise where it is
        // (mpv runs a new graph from where it is by itself).
        (long Ms, bool Exact)? start = seekTo;
        if (hasSound && from is { } soundFrom)
            start = (soundFrom, true);
        player.SetFilterGraph(graph.Text, start, mayBeOvertaken: _readyingToPlay);
        if (graph.Text.Length == 0)
            player.SetFiltering(false);

        // For a crash report: the graph's size and its filters' names, never what is in them.
        CrashReporter.SetContext("graph", graph.Text.Length == 0 ? "none" : CrashSummaries.Graph(graph.Text) + (hasSound ? ", with sound" : ""));

        // Without sound in the graph the player plays a track of the file itself. After a graph that had sound
        // it has none selected at all, so the track is given again.
        ApplyAudioTrack();
    }

    // Whether the graph that is running mixes the layers' sound in, the moment it was laid out from, and a
    // jump made since that it has yet to be laid out for.
    private bool LiveGraphHasSound => _liveGraph.Contains(FilterGraphBuilder.LiveAudioOutput, StringComparison.Ordinal);
    private long? _liveAudioFromMs;
    private long? _liveAudioSeekMs;

    // mpv turned down a graph with the layers' sound in it: the picture's graph is run without, until a setting changes.
    private bool _liveSoundRefused;

    /// <summary>
    /// mpv logged an error. When it is about the filters, the graph is taken off again, so the source keeps
    /// playing, and is not tried a second time until the settings have changed.
    /// </summary>
    private void OnPlayerError(string text)
    {
        AppLog.Write($"Player: {text}");
        CrashReporter.Note("player", text);
        if (_mpv is not { } player || _liveGraph.Length == 0
            || !(text.Contains("lavfi", StringComparison.OrdinalIgnoreCase) || text.Contains("filter", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // A graph that mixes the layers' sound in is tried again without it first: the picture may be fine.
        if (LiveGraphHasSound)
        {
            _liveSoundRefused = true;
            _viewModel.StatusText = $"Live Preview could not mix the layers' sound in, and plays the main video's sound alone ({text}).";
            ApplyLiveFilter();
            return;
        }

        var refused = _liveStamp;
        ClearLiveFilter(player);
        _failedLiveStamp = refused;
        CrashReporter.SetContext("graph", "refused by the player: the source is shown");
        _viewModel.StatusText = $"Live Preview could not run the filters as they are set, and shows the source instead ({text}).";
    }

    // ----- Following the player -----
    // While it plays, the position is read once per screen refresh (CompositionTarget.Rendering), whatever mpv's
    // frame rate: one look per frame drawn, and nothing queued behind a busy UI thread. Paused, the refresh is not
    // followed (it would keep WPF drawing for nothing); a moved position asks for one look by itself.

    private volatile bool _followingByFrame;

    private void FollowPlayerByFrame(bool on)
    {
        if (on == _followingByFrame)
            return;

        _followingByFrame = on;
        if (on)
        {
            CompositionTarget.Rendering += FollowPlayer_Rendering;
        }
        else
        {
            CompositionTarget.Rendering -= FollowPlayer_Rendering;

            // A position that came in while it was being followed by frame was not asked about: it is taken now.
            TakePlayerPosition();
        }
    }

    private void FollowPlayer_Rendering(object? sender, EventArgs e) => TakePlayerPosition();

    private void TakePlayerPosition()
    {
        if (_mpv is { } player && player.TryTakePosition(out var positionMs))
            OnPlayerTimeChanged(positionMs);
    }

    // ----- One audio track on its own -----

    /// <summary>
    /// Has the player play the track chosen on the Audio tab, or else the first one that is not muted (none when
    /// all are), while no graph mixes the sound. mpv refuses a track change while a graph holds the tracks.
    /// </summary>
    private void ApplyAudioTrack()
    {
        if (_mpv is { } player && !LiveGraphHasSound)
            player.SetAudioTrack(_viewModel.PlayerAudioTrack);
    }

    private void OnSoloTrackChanged()
    {
        if (_mpv is null || !_viewModel.HasSource)
            return;

        ApplyAudioTrack();

        // A track played on its own is played without the layers' sound.
        ScheduleLiveFilter();

        // The button is a play button: choosing a track starts it, and letting go of the last one pauses.
        if (_viewModel.SoloedTracks.Count > 0 && !PlayerIsPlaying)
            PlayPause_Click(this, new RoutedEventArgs());
        else if (_viewModel.SoloedTracks.Count == 0 && PlayerIsPlaying)
            PlayerSetPause(true);
    }

    // ----- Seeking from a waveform -----
    // A track's waveform on the Audio tab covers the whole video from edge to edge, so a point on it is a time.

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement waveform || _viewModel.DurationMs <= 0 || !waveform.CaptureMouse())
            return;

        // Handled, so the list the track's row is in does not take the mouse for itself and end the drag.
        e.Handled = true;
        SeekToWaveformPoint(waveform, e);
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } waveform)
            SeekToWaveformPoint(waveform, e);
    }

    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => (sender as UIElement)?.ReleaseMouseCapture();

    private void SeekToWaveformPoint(FrameworkElement waveform, MouseEventArgs e)
    {
        // Setting the position moves the timeline, which seeks the player. A track's row shows the time bars' view
        // (it holds one); the master mix shows the whole sequence.
        var x = e.GetPosition(waveform).X;
        if (waveform is Border { Child: Grid grid } && grid.Children.OfType<TimelineTrack>().Any())
            _viewModel.PositionMs = Math.Clamp(TimeAt(waveform, x) * 1000, 0, _viewModel.DurationMs);
        else if (waveform.ActualWidth > 0)
            _viewModel.PositionMs = Math.Clamp(x / waveform.ActualWidth, 0, 1) * _viewModel.DurationMs;
    }

    // ----- Playback / scrubbing -----

    // ----- J, K, L -----
    // L plays; pressed again it plays twice as fast, then four and eight times. J does the same backwards.
    // K stops. mpv does not play a filtered picture backwards, so going back is done here: the player is
    // paused and sent back along the timeline, step by step, as fast as was asked.

    // 0 while not shuttling; otherwise the speed, negative for backwards.
    private int _shuttle;
    private double _shuttlePositionMs;
    private readonly System.Windows.Threading.DispatcherTimer _shuttleTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void Shuttle(int direction)
    {
        if (!_viewModel.HasSource || _mpv is not { } player)
            return;

        if (direction == 0)
        {
            StopShuttle();
            player.SetPause(true);
            _viewModel.StatusText = "Paused.";
            return;
        }

        // The same key again doubles the speed; the other key turns round, at normal speed. L while it is
        // already playing is the second press.
        var speed = Math.Sign(_shuttle) == direction ? Math.Min(Math.Abs(_shuttle) * 2, 8)
            : direction > 0 && _shuttle == 0 && player.IsPlaying ? 2
            : 1;
        _shuttle = direction * speed;

        if (direction > 0)
        {
            _shuttleTimer.Stop();
            if (player.IsEnded)
                PlayerSeek(0);
            player.SetSpeed(speed);
            player.SetPause(false);
        }
        else
        {
            player.SetPause(true);
            _shuttlePositionMs = _viewModel.PositionMs;
            _shuttleTimer.Start();
        }

        _viewModel.StatusText = $"{(direction > 0 ? "Forward" : "Reverse")} {speed}x. K stops; {(direction > 0 ? "L" : "J")} again goes faster.";
    }

    private void ShuttleBack()
    {
        if (_shuttle >= 0 || _mpv is null)
        {
            _shuttleTimer.Stop();
            return;
        }

        _shuttlePositionMs = Math.Max(_shuttlePositionMs + _shuttle * _shuttleTimer.Interval.TotalMilliseconds, 0);
        _updatingFromPlayer = true;
        _viewModel.PositionMs = _shuttlePositionMs;
        _updatingFromPlayer = false;

        // At speed the player keeps up by landing on I-frames; slowly, it shows every step exactly.
        PlayerSeek((long)_shuttlePositionMs, exact: _shuttle >= -2);
        if (_shuttlePositionMs <= 0)
            StopShuttle();
    }

    /// <summary>Ends J/K/L shuttling, and puts the playback speed back to the one chosen in the speed box.</summary>
    private void StopShuttle()
    {
        _shuttleTimer.Stop();
        if (_shuttle == 0)
            return;

        var wasWindingBack = _shuttle < 0;
        _shuttle = 0;
        _mpv?.SetSpeed(_viewModel.PlaybackRate);

        // Wound back, the video layers were shown as while scrubbing: they play live again from here.
        if (wasWindingBack)
            ApplyLiveFilter();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        StopShuttle();
        if (!_viewModel.HasSource)
        {
            _viewModel.LoadSourceCommand.Execute(null);
        }
        else if (_mpv is not { } player)
        {
            _ = EnsurePlayerAsync();
        }
        else if (player.IsEnded)
        {
            // Held on the last frame: play starts again from the beginning.
            PlayerSeek(0);
            player.SetPause(false);
        }
        else
        {
            // Paused by hand: a Play Start to Stop under way is over.
            if (!player.IsPaused)
                _playUntilMs = null;
            player.SetPause(!player.IsPaused);
        }
    }

    private void OnPlayerTimeChanged(long timeMs)
    {
        // Don't fight the user for the thumb while they are dragging it, or the shuttle while it is winding back.
        if (_isScrubbing || _shuttle < 0)
            return;

        // Paused on a moment the user sent it to (a seek, a frame or key step, a click on the timeline), the playhead
        // stays exactly there: the frame mpv shows for it is stamped a few milliseconds off (58.434 for 58.420), and a
        // graph swapped in while paused restarts the player, so its reports would move the playhead off a keyframe it
        // was put on (4.2D). Paused by Pause, the position it stopped at is still taken.
        if (PlayerIsPlaying)
            _pausedOnUsersMoment = false;
        else if (_pausedOnUsersMoment)
            return;

        _updatingFromPlayer = true;
        _viewModel.PositionMs = timeMs;
        _updatingFromPlayer = false;

        // While playing: a graph made in time for what comes on next, and Play In to Out and Loop kept to their stretch.
        if (!PlayerIsPlaying)
            return;

        // Something the running graph left out is about to come on: a graph that holds it is made now, so its
        // file is open and waiting when it does.
        if (_liveGraph.Length > 0 && timeMs >= _liveRebuildAt * 1000)
            ApplyLiveFilter();

        // Play Start to Stop, and Loop.
        KeepPlaybackInRange(timeMs);
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromPlayer || _mpv is null)
            return;

        // Not a move of the playhead (4.1.1): the value pushed down by a timeline that became shorter (the slider
        // coerces it to its new end; what the sequence does about it is RefreshSequence's), or a step of less than a
        // millisecond (a position given back in other units). Each was a seek, and while paused a swap to the quick
        // graph and back, that moved the frame on.
        if (e.NewValue == TimelineSlider.Maximum && e.OldValue > TimelineSlider.Maximum)
            return;
        if (!_isScrubbing && Math.Abs(e.NewValue - e.OldValue) < 1)
            return;

        // Magnetism: while dragging, a thumb that comes close enough to an I-frame is pulled onto it.
        // The drag continues from wherever the thumb is, so it sticks until the pointer has moved clear.
        var settings = AppSettings.Current;
        if (_isScrubbing && !_applyingMagnet && settings.SnapTimelineToIFrames
            && TimelineSlider.ActualWidth > 0 && _viewModel.DurationMs > 0
            && _viewModel.GetNearestIFrameMs(e.NewValue) is { } iFrame && iFrame != e.NewValue)
        {
            const double pixelsPerStep = 4;
            var reach = Math.Clamp(settings.TimelineMagnetism, 1, 5) * pixelsPerStep / TimelineSlider.ActualWidth * _viewModel.DurationMs;
            if (Math.Abs(iFrame - e.NewValue) <= reach)
            {
                _applyingMagnet = true;
                TimelineSlider.Value = iFrame;
                _applyingMagnet = false;
                return;
            }
        }

        // While the thumb is being dragged the player jumps from I-frame to I-frame, and is asked to no more
        // than thirty times a second; the exact frame follows when the thumb is let go.
        if (_isScrubbing)
            QueueScrubSeek((long)e.NewValue);
        else
            PlayerSeek((long)e.NewValue, exact: true);
    }


    // ----- Scrubbing: fast seeks, paced -----
    // A drag of the playhead is a stream of positions, far more of them than a player with several videos to
    // decode can answer. Each is a fast seek (to the nearest I-frame, never frame-exact), and they are paced:
    // the first goes at once, and after it at most one every 33 ms, always to the newest position. Whatever
    // came in between is never sent. Letting go sends one exact seek to where the playhead was left.

    private readonly System.Windows.Threading.DispatcherTimer _scrubPace = new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
    private long _scrubWanted = -1, _scrubSent = -1;
    private int _scrubSeeks, _scrubAsked;
    private DateTime _scrubBegan;

    private void QueueScrubSeek(long positionMs)
    {
        _scrubWanted = positionMs;
        _scrubAsked++;
        if (_scrubPace.IsEnabled)
            return;

        SendScrubSeek();
        _scrubPace.Start();
    }

    private void ScrubPace_Tick(object? sender, EventArgs e)
    {
        if (!_isScrubbing || _scrubWanted == _scrubSent)
            _scrubPace.Stop();
        else
            SendScrubSeek();
    }

    private void SendScrubSeek()
    {
        _scrubSent = _scrubWanted;
        _scrubSeeks++;
        PlayerSeek(_scrubSent, exact: false);
    }

    /// <summary>The end of a scrub: the pacing stops, and what it came to is noted in the log.</summary>
    private void EndScrubPacing()
    {
        _scrubPace.Stop();
        if (_scrubAsked > 0)
            AppLog.Write($"Scrub: {_scrubAsked} positions over {(DateTime.UtcNow - _scrubBegan).TotalMilliseconds:0} ms, {_scrubSeeks} fast seeks sent, then one exact seek.");
        (_scrubAsked, _scrubSeeks, _scrubWanted, _scrubSent) = (0, 0, -1, -1);
    }

    /// <summary>
    /// A press anywhere on the master timeline, not only on the playhead: the playhead goes to where the
    /// pointer is, and the press is then handed to it, so that it is held from that moment exactly as if it
    /// had been pressed itself. Holding and dragging scrubs; letting go ends it, by the same steps as any
    /// drag of the playhead (DragStarted, DragCompleted). The slider has usually moved the playhead to the
    /// pointer already by the time this runs; what it does not do by itself is take hold of it.
    /// </summary>
    private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineSlider.Template?.FindName("PART_Track", TimelineSlider) is not System.Windows.Controls.Primitives.Track { Thumb: { } thumb } track
            || thumb.IsMouseOver || thumb.IsDragging || TimelineSlider.Maximum <= TimelineSlider.Minimum)
        {
            return;
        }

        // The track works out a value from where its playhead was last laid out. The slider may just have moved
        // the playhead (it does, for a press beside it), so the track is laid out first: asked before that, it
        // would add the distance to the pointer a second time.
        TimelineSlider.UpdateLayout();
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsFinite(value))
            return;

        TimelineSlider.Value = Math.Clamp(value, TimelineSlider.Minimum, TimelineSlider.Maximum);

        // The playhead is laid out where it now is before it is pressed: it measures the drag from there.
        TimelineSlider.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent, Source = thumb });
        e.Handled = true;
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isScrubbing = true;
        (_scrubBegan, _scrubAsked, _scrubSeeks) = (DateTime.UtcNow, 0, 0);

        // Dragged while it plays: the layers' sound comes out of the graph for as long as the drag lasts.
        if (LiveGraphHasSound)
            ApplyLiveFilter();
    }

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isScrubbing = false;
        EndScrubPacing();

        // Let go (and a click on the timeline is a press let go at once): the exact frame, straight away.
        PlayerSeek((long)TimelineSlider.Value);

        // And goes back in, from where the thumb was let go, if it is still playing.
        if (PlayerIsPlaying && _viewModel.LivePreview)
        {
            _liveAudioSeekMs = (long)TimelineSlider.Value;
            ScheduleLiveFilter();
        }
    }
}
