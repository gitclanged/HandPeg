using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Project tabs: the paused still over the player, and the tabs kept live in memory (split from MainWindow.ProjectTabs.cs, 5.4.1-M5) -----
public partial class MainWindow
{
    // ----- The paused still over the player -----
    // The player is a native window, which nothing of WPF's draws over but the window kept over it (PlayerOverlay): the still lies there,
    // under everything else of it, until the engine has presented a scene made since the tab came back (5.4.2A: GpuPreviewEngine.MarkScene,
    // never a scene of the tab put behind still on its way; its exact frames, or after a moment whatever it has), or two and a half seconds
    // at most. A bounded watch of a few frames, only after a switch.

    private Image? _tabStill;
    private bool _stillWatching;
    private long _stillSince, _stillMark;
    private int _stillSeen;

    /// <summary>How long the still waits for the engine's exact frames before any frame of the tab will do, and how long it stays at most, ms.</summary>
    private const long StillExactWaitMs = 600, StillLongestMs = 2500;

    /// <summary>A BGRA8 picture as a frozen bitmap (its alpha meaningless: Bgr32).</summary>
    private static BitmapSource StillOf(byte[] bgra, int width, int height)
    {
        var still = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        still.Freeze();
        return still;
    }

    private void ShowTabStill(BitmapSource? still)
    {
        if (still is null)
        {
            HideTabStill();
            return;
        }

        if (_tabStill is null)
        {
            _tabStill = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(_tabStill, BitmapScalingMode.HighQuality);
            PlayerOverlay.Children.Insert(0, _tabStill);
        }

        _tabStill.Source = still;
        _tabStill.Visibility = Visibility.Visible;
    }

    private void HideTabStill()
    {
        StopStillWatch();
        if (_tabStill is not { Visibility: Visibility.Visible })
            return;
        _tabStill.Source = null;
        _tabStill.Visibility = Visibility.Collapsed;
    }

    /// <summary>The still stays until the engine has presented a scene made since now (see the section above).</summary>
    private void WatchForFirstFrame()
    {
        if (_tabStill is not { Visibility: Visibility.Visible })
            return;

        (_stillMark, _stillSince, _stillSeen) = (_gpu?.MarkScene() ?? 0, Environment.TickCount64, 0);
        if (!_stillWatching)
        {
            // Looked at on every frame WPF renders while the still is up (5.4.3; a 16 ms timer before): hooked only for this watch.
            _stillWatching = true;
            CompositionTarget.Rendering += StillWatch_Rendering;
        }
    }

    // The still goes one render tick after the viewport put the tab's frame on the swap chain (5.4.3: IDXGISwapChain::Present, so the
    // desktop has composed it by then), its exact frames or, after a moment, whatever it has.
    private void StillWatch_Rendering(object? sender, EventArgs e)
    {
        var waited = Environment.TickCount64 - _stillSince;
        var presented = _gpu is { } engine && engine.PresentedSince(_stillMark) && (engine.PresentedExact || waited > StillExactWaitMs);
        _stillSeen = presented ? _stillSeen + 1 : 0;
        if (_stillSeen >= 2 || waited > StillLongestMs || _viewModel.IsPlaying)
            HideTabStill();
    }

    private void StopStillWatch()
    {
        if (!_stillWatching)
            return;
        _stillWatching = false;
        CompositionTarget.Rendering -= StillWatch_Rendering;
    }

    // ----- Tabs kept live in memory (Pass 5.4D-TabPerf) -----
    // A tab put behind keeps everything of it in memory (a TabViewContext, by its id): the view model's part (its live Sequence, history,
    // fingerprints, what was made of its file) and the window's (its still, overview sheet, selection, player zoom and pan, the Timeline's
    // open stacks). It comes back by a swap of references (AttachHydrated). Its cache file is written after the switch from a frozen copy
    // (QueueTabWrite), each tab's disk work after the last (ProjectTabs.Enqueue), so a crash, a close or the memory valve finds it there.
    // The valve (LetGoForMemory) lets the least recently used go back to its file only when the computer's free memory runs short (5.4.2A:
    // all tabs warm, however many).

    /// <summary>How long closing HandPeg, a save from memory or a close waits for a tab's cache file still being written, ms.</summary>
    private const int IoWaitMs = 5000;

    /// <summary>The computer's free memory (AvailPhys) under which the valve lets tabs kept live go back to their files (5.4.2A: 2.5 GB).</summary>
    internal const long AvailableFloorBytes = Compositor.CompositorWorker.AvailableFloorBytes;

    /// <summary>How often the memory valve looks again while tabs are kept live, ms.</summary>
    private const int ValveTickMs = 30_000;

    /// <summary>The tabs behind the one in front kept live in memory, by id (the one in front is the view model's).</summary>
    private readonly Dictionary<string, TabViewContext> _hydrated = new(StringComparer.Ordinal);

    // The cache files queued and not written yet: written as they are should HandPeg fall over first (WriteUnwrittenTabs). Any thread.
    private readonly List<TabWrite> _unwritten = [];
    private bool _crashWritesHooked, _letGoing, _memoryHooked;
    private DispatcherTimer? _valveTimer;

    /// <summary>A still's moment: the undo mark, the playhead and the 3D view it was taken at (a stale one only covers the player until its first frame),
    /// and whether it is the soft frame drawn smaller while playing (5.4.4: kept only until a whole one is read).</summary>
    private readonly record struct StillMark(object? UndoMark, double PositionMs, bool Shown3D, bool Soft = false);

    /// <summary>A tab put behind, kept live in memory (see the section above). Made on the window's thread and used only there.</summary>
    private sealed class TabViewContext(MainViewModel.TabProjectContext project)
    {
        /// <summary>The view model's part: its live Sequence, history and fingerprints, its frozen copy.</summary>
        public MainViewModel.TabProjectContext Project { get; } = project;

        /// <summary>Its paused picture as last kept fresh (MainWindow.TabStill.cs), and when it was taken; null for none yet.</summary>
        public BitmapSource? Still { get; init; }

        /// <summary>
        /// Its hover card's picture when it went behind with no still (5.4.1-M3: playing, or a moment after a change): the overview sheet's
        /// picture at its playhead, so the card has one at once. Never shown over the player.
        /// </summary>
        public BitmapSource? Preview { get; init; }

        /// <summary>Its Timeline's rows as they were (5.4.1-M3): given back as it comes back, not made again.</summary>
        public KeptRows? Rows { get; init; }

        public StillMark StillMark { get; init; }

        /// <summary>The overview sheet it showed (whole), and the undo mark its Sequence was at.</summary>
        public ThumbnailSheet? ProgramSheet { get; init; }

        public object? ProgramSheetMark { get; init; }

        /// <summary>What was selected on its Timeline: its views of before, followed by their clips' ids as it comes back.</summary>
        public object? SelectedClip { get; init; }

        public Layer? RangeAnchor { get; init; }

        public bool PlayheadVideoSelected { get; init; }

        /// <summary>The player's zoom and pan (Keybinds' navigation).</summary>
        public (double Zoom, double PanX, double PanY) PlayerView { get; init; }

        /// <summary>The Timeline's mini-stacks open (their rows' keys are any project's: "captions", a track's number).</summary>
        public IReadOnlyList<(object Key, ChildStack Stack)> Expansion { get; init; } = [];

        /// <summary>Its cache file's write after it went behind.</summary>
        public TabWrite? Write { get; set; }

        /// <summary>About how much memory it holds, in bytes (the memory valve's measure).</summary>
        public long ApproxBytes { get; set; }

        /// <summary>Its cache file could not be written: never let go to it (its work is only here).</summary>
        public bool MustStay { get; set; }
    }

    /// <summary>
    /// A tab's cache file to write after a switch (5.4D-TabPerf): from its frozen copy, with its header as it went behind; the exact dot
    /// worked out on the thread pool. Its state moves on the thread pool (and on a thread falling over): read under its own lock.
    /// </summary>
    private sealed class TabWrite(ProjectTabHeader tab, TabViewContext live, string header, BitmapSource? thumbnailFrom)
    {
        private readonly Lock _gate = new();
        private bool _done, _failed, _cancelled, _superseded, _dirty, _running;

        public ProjectTabHeader Tab { get; } = tab;

        public string Id { get; } = tab.Id;

        public TabViewContext Live { get; } = live;

        /// <summary>The header as the tab went behind (its dot the cheap one).</summary>
        public string Header { get; } = header;

        /// <summary>The flat still a project never saved takes its thumbnail from (5.4B), or null.</summary>
        public BitmapSource? ThumbnailFrom { get; } = thumbnailFrom;

        /// <summary>The thumbnail made of it on the thread pool (null for none), and the file's length once written.</summary>
        public byte[]? Jpeg { get; set; }

        public long Bytes { get; set; }

        /// <summary>Its last try is over: written, or failed (<see cref="Failed"/>); false again while it is tried once more.</summary>
        public bool IsDone
        {
            get
            {
                lock (_gate)
                    return _done;
            }
        }

        /// <summary>Its last try failed or faulted: the tab has no recovery copy yet (tried again: 5.4D-TabPerf review).</summary>
        public bool Failed
        {
            get
            {
                lock (_gate)
                    return _failed;
            }
        }

        /// <summary>Not to be written any more (its tab closed): nothing of it may come back at the next start.</summary>
        public bool IsCancelled
        {
            get
            {
                lock (_gate)
                    return _cancelled;
            }
        }

        /// <summary>Whether it is still to be written: not written whole (a failed one still is), not cancelled.</summary>
        public bool IsWanted
        {
            get
            {
                lock (_gate)
                    return !(_done && !_failed) && !_cancelled;
            }
        }

        /// <summary>The exact dot (whether it has unsaved changes), once written; null before, or once a save made it out of date.</summary>
        public bool? Dirty
        {
            get
            {
                lock (_gate)
                    return _done && !_failed && !_superseded ? _dirty : null;
            }
        }

        /// <summary>
        /// The write claimed by one writer (any thread, 5.4D-TabPerf review: the thread pool, the crash writer, the clean close): true when it is
        /// still wanted and nobody is writing it, and it is that writer's to <see cref="Finish"/> or <see cref="Fail"/> now (a failed one is
        /// tried once more). False when it is written, cancelled or being written: two copies of one tab's file are never written at once.
        /// </summary>
        public bool TryStart()
        {
            lock (_gate)
            {
                if (_running || _cancelled || (_done && !_failed))
                    return false;
                (_running, _done, _failed) = (true, false, false);
                return true;
            }
        }

        public void Finish(bool dirty)
        {
            lock (_gate)
                (_running, _done, _failed, _dirty) = (false, true, false, dirty);
        }

        /// <summary>The claimed try failed (nothing once it finished: a writer calls it on its way out whatever happened).</summary>
        public void Fail()
        {
            lock (_gate)
            {
                if (_running)
                    (_running, _done, _failed) = (false, true, true);
            }
        }

        public void Cancel()
        {
            lock (_gate)
                _cancelled = true;
        }

        /// <summary>A save while it was behind (SaveTabInPlace): its dot is the save's now, whatever this write found.</summary>
        public void Superseded()
        {
            lock (_gate)
                _superseded = true;
        }
    }

    /// <summary>
    /// The tab in front taken into a context of its own, kept live (5.4D-TabPerf; its header brought up to date first): the view model
    /// lets go of it (MainViewModel.DetachForTab), and the window's part goes with it. <paramref name="write"/> is its cache file to write.
    /// </summary>
    private TabViewContext DetachActive(ProjectTabHeader tab, out TabWrite write)
    {
        TakeHeaderForTab(tab);
        var sheetMark = _viewModel.UndoMark;
        var (still, stillMark) = (_liveStill, _liveStillMark);
        // Its overview's picture at the playhead as well when its still is the 3D one (5.4.2A): that is never written to its cache file (below),
        // so a tab left in 3D that goes cold (the valve, or a restart) still has a picture for its hover card.
        var preview = (still is null || stillMark.Shown3D) && OverviewSheet is { Count: > 0 } overview ? overview.Cell(overview.IndexAt(_viewModel.PositionMs / 1000)) : null;
        var rows = KeepRows();
        var live = new TabViewContext(_viewModel.DetachForTab(tab.Title))
        {
            Still = still,
            StillMark = stillMark,
            Preview = preview,
            Rows = rows,
            ProgramSheet = SheetToKeep(),
            ProgramSheetMark = sheetMark,
            SelectedClip = _selectedClip,
            RangeAnchor = _rangeAnchor,
            PlayheadVideoSelected = _playheadVideoSelected,
            PlayerView = (_playerZoom, _playerPanX, _playerPanY),
            Expansion = UnifiedTimeline.Surface.SaveExpansion(),
        };

        (_liveStill, _liveStillMark) = (null, default);

        // A project with no thumbnail yet (never saved) keeps its paused still as one (5.4B), so its hover card has a picture once it is
        // cold too: made a JPEG on the thread pool, with the write. Never a 3D still (5.4D): a project's picture is flat.
        write = new TabWrite(tab, live, ProjectTabs.HeaderJson(tab), still is not null && !stillMark.Shown3D ? still : preview);
        live.Write = write;
        live.ApproxBytes = ApproxBytesOf(live, 0);
        return live;
    }

    /// <summary>
    /// A tab kept live brought back to the front (5.4D-TabPerf; it is in front already, its layout laid out), at once: its still over the
    /// player, its project swapped into the view model (MainViewModel.AttachTab), its overview sheet, selection, open stacks and player view
    /// as it left them, its view and playhead restored now (not after an open), the engine opened on it. Its cache file goes, after its
    /// write (the tab in front keeps none). False when the view model is held by an operation or a recording (nothing changed: the caller
    /// takes the tab put behind back). A fault leaves the tab in front as one not brought back (what it holds kept in memory to try again).
    /// </summary>
    private bool AttachHydrated(ProjectTabHeader tab, TabViewContext live)
    {
        // What its header says of its file and first save is the latest (a save or a rename while it was behind).
        var project = live.Project;
        (project.ProjectPath, project.NewProjectName, project.NewProjectFolder) = (tab.ProjectPath, tab.NewProjectName, tab.NewProjectFolder);

        // Its still only when it is the picture it comes back to (5.4D-TabPerf review): taken at its playhead (within a frame), flat or in
        // 3D as it is now; a still from before playback or from the other view covers nothing.
        var stillFits = StillFits(live.StillMark.PositionMs, live.StillMark.Shown3D, tab.PlayheadMs, tab.ExplodedOpen, tab.FrameRate);
        if (stillFits)
            ShowTabStill(live.Still);
        else
            ShowTabStill(tab.ExplodedOpen ? null : FallbackStill(live, tab.PlayheadMs));

        // Its selection as its views of before: the restore follows them by their clips' ids (Timeline_Restored).
        (_selectedClip, _rangeAnchor, _playheadVideoSelected) = (live.SelectedClip, live.RangeAnchor, live.PlayheadVideoSelected);

        // Its rows given back as they were (5.4.1-M3) by the layouts the attach asks for, unless its Sequence or their shape changed.
        _rowsToTakeBack = live.Rows;
        try
        {
            if (!_viewModel.AttachTab(project))
            {
                _rowsToTakeBack = null;
                HideTabStill();
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _rowsToTakeBack = null;
            // Part of it may be the view model's now: an empty project instead (its own Sequence never touched again), and the tab keeps
            // what it holds in memory to try once more.
            AppLog.Write("Project tabs: a tab kept in memory could not be brought back", ex);
            HideTabStill();
            _viewModel.ParkDocument();
            _viewModel.NewProject();
            _unresumed.Add(tab.Id);
            _viewModel.StatusText = $"\"{tab.Title}\" could not be brought back ({ex.Message}). Its tab keeps it: click it again to try once more.";
            return true;
        }

        // In front again: its cache file is not written now (a write still to come is let go) and goes after any under way. Pass 5.5A final
        // review (AD-1): one written whole with unsaved changes stays as the tab in front's crash-recovery copy instead (the work as it comes
        // back, as ResumeTabAsync keeps one: the tick writes it anew once the history moves, and lets it go once the tab is clean).
        _hydrated.Remove(tab.Id);
        _unresumed.Remove(tab.Id);
        tab.WaitingTemplate = null;
        live.Write?.Cancel();
        ForgetUnwritten(live.Write);
        if (live.Write?.Dirty == true)
            _frontRecovery = new FrontLook(tab.Id, _viewModel.HistoryPosition, Written: true);
        else
            DeleteTabFiles(tab.Id);

        if (live.ProgramSheet is { } sheet)
            AdoptProgramSheet(sheet, live.ProgramSheetMark);
        UnifiedTimeline.Surface.RestoreExpansion(live.Expansion);
        RestorePlayerView(live.PlayerView);
        (_liveStill, _liveStillMark) = stillFits ? (live.Still, live.StillMark) : (null, default);

        // Left in the 3D view: waiting for its view to be restored (5.4D review), as a tab read from its file does.
        if (tab.ExplodedOpen)
            _explodedWaitingTab = tab;
        RestoreTabView(tab);
        _viewModel.BackgroundSelected = _selectedClip is Layer { IsBackground: true };
        UpdateTimelineInspector();
        UpdatePlayerSelection();
        KeptTabsChanged();

        // No still that fits: one is taken once it settles (5.4.1-M3), so its hover card has one as soon as it goes behind again.
        if (!stillFits)
            NoteStillStale();

        // Its rows are laid out at the next layout: the scroll is looked at again then (it is held to the rows there are).
        _ = Dispatcher.BeginInvoke(() =>
        {
            _rowsToTakeBack = null;
            if (ReferenceEquals(tab, _tabs.Active) && Math.Abs(UnifiedTimeline.Surface.VerticalOffset - tab.VerticalOffset) > 0.5)
                UnifiedTimeline.Surface.VerticalOffset = tab.VerticalOffset;
        }, DispatcherPriority.Background);
        return true;
    }

    /// <summary>
    /// A flat tab coming back with no still of its playhead (5.4.3): its overview sheet's picture there, or the one kept beside a 3D still, so
    /// the player is never uncovered before its first frame; null when it has neither.
    /// </summary>
    private static BitmapSource? FallbackStill(TabViewContext live, double playheadMs) =>
        live.ProgramSheet is { Count: > 0 } sheet ? sheet.Cell(sheet.IndexAt(playheadMs / 1000)) : live.Preview;

    /// <summary>
    /// Whether a tab's kept still is the picture it comes back to (pure, 5.4D-TabPerf review: the goldens' project-tabs-tabperf-review): taken
    /// within a frame of its playhead (<paramref name="frameRate"/> its Project Settings', 30 fps while it is not known), in the 3D view
    /// exactly when it comes back in it.
    /// </summary>
    internal static bool StillFits(double stillMs, bool still3D, double playheadMs, bool exploded, double frameRate) =>
        still3D == exploded && Math.Abs(stillMs - playheadMs) <= 1000.0 / (frameRate > 0 ? frameRate : 30);

    /// <summary>The player's zoom and pan a tab kept, put back (none: fitted, as a project opened is; the Canvas's view fitted with it).</summary>
    private void RestorePlayerView((double Zoom, double PanX, double PanY) view)
    {
        ResetNavigationViews();
        if (view.Zoom <= 0)
            return;

        (_playerZoom, _playerPanX, _playerPanY) = view;
        _navPicture = PictureSize();
        ApplyPlayerView();
    }

    /// <summary>
    /// A tab put behind's cache file written on the thread pool (5.4D-TabPerf), after the switch and after the tab's other disk work: its
    /// frozen copy (a thumbnail made of its still first when it has none), its saved state's fingerprint when it has unsaved changes, and
    /// its header with the exact dot (worked out there: MainViewModel.FrozenStateDiffers), which the strip then shows. A failure is written
    /// down, and the tab stays in memory (never let go to a file it has not got) and listed as one with no recovery copy yet: the crash
    /// writer writes it too, and the valve's tick and the next tab put behind try it again (5.4D-TabPerf review). Nothing for null.
    /// </summary>
    private void QueueTabWrite(TabWrite? write)
    {
        if (write is not { IsWanted: true })
            return;

        HookTabCrashWrites();
        lock (_unwritten)
        {
            if (!_unwritten.Contains(write))
                _unwritten.Add(write);
        }

        _ = _tabs.Enqueue(write.Id, () => WriteTabNow(write));
    }

    /// <summary>The tabs kept live whose cache file could not be written, tried again (5.4D-TabPerf review: the valve's tick, a tab put behind).</summary>
    private void RetryFailedTabWrites()
    {
        foreach (var live in _hydrated.Values)
        {
            if (live.Write is { Failed: true, IsWanted: true } write)
                QueueTabWrite(write);
        }
    }

    /// <summary>
    /// One tab's cache file written (the thread pool; see <see cref="QueueTabWrite"/>), then told to the window's thread. Claimed first
    /// (5.4D-TabPerf review): one the crash writer took, or a retry under way, is never written beside it.
    /// </summary>
    private void WriteTabNow(TabWrite write)
    {
        if (!write.TryStart())
            return;

        try
        {
            var project = write.Live.Project;

            // The frozen copy itself is never written to here (5.4D-TabPerf review: a save or the crash writer may be reading it this
            // moment): the thumbnail made of the still goes into a copy of it.
            var frozen = project.Frozen;
            if (write.ThumbnailFrom is { } still && frozen.ThumbnailJpeg is null)
            {
                if (write.Jpeg is null)
                {
                    var (width, height) = (still.PixelWidth, still.PixelHeight);
                    var pixels = new byte[width * height * 4];
                    still.CopyPixels(pixels, width * 4, 0);
                    write.Jpeg = ProjectThumbnail.EncodeJpeg(pixels, width, height);
                }

                frozen = frozen.ShallowCopy();
                frozen.ThumbnailJpeg = write.Jpeg;
            }

            var dirty = project.MayBeDirty && MainViewModel.FrozenStateDiffers(frozen, project.SavedFingerprint);
            var header = ProjectTabs.ReadHeader(write.Header) is { } read && read.Dirty != dirty ? ProjectTabs.HeaderJson(read with { Dirty = dirty }) : write.Header;
            _tabs.WriteTab(write.Id, frozen, dirty ? project.SavedFingerprint : null, header);
            write.Bytes = new FileInfo(_tabs.StateFile(write.Id)).Length;
            write.Finish(dirty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: a tab put behind could not be written to the cache (it stays in memory, with no recovery copy yet)", ex);
        }
        finally
        {
            // Not written whole (failed, or faulted with anything at all): still listed, to be written as HandPeg falls over and tried again.
            write.Fail();
            if (!write.IsWanted)
            {
                lock (_unwritten)
                    _unwritten.Remove(write);
            }
        }

        if (write.IsDone)
            _ = Dispatcher.BeginInvoke(() => TabWritten(write), DispatcherPriority.Background);
    }

    /// <summary>A tab's cache file written (or not), on the window's thread: its exact dot, its thumbnail, its size for the memory valve.</summary>
    private void TabWritten(TabWrite write)
    {
        var (tab, live) = (write.Tab, write.Live);
        if (!_tabs.Tabs.Contains(tab) || !ReferenceEquals(_hydrated.GetValueOrDefault(tab.Id), live))
            return;

        if (write.Failed)
        {
            live.MustStay = true;
            _viewModel.StatusText = $"\"{tab.Title}\" has no recovery copy yet: its cache file could not be written. It stays in memory, and HandPeg tries again.";
            return;
        }

        live.MustStay = false;
        if (write.Jpeg is { } jpeg)
            live.Project.Thumbnail ??= jpeg;
        live.ApproxBytes = ApproxBytesOf(live, write.Bytes);
        if (write.Dirty is { } dirty && dirty != tab.Dirty)
        {
            tab.Dirty = dirty;
            ShowTabs();
        }
    }

    /// <summary>
    /// About how much memory a tab kept live holds, in bytes (the valve's measure, 5.4D-TabPerf): its project's objects about three times
    /// its cache file (its live Sequence, its frozen copy, its history; 4 MB until the file is written), and the pictures it keeps: its
    /// still and overview sheet, and (the 5.4D-TabPerf review) its clips' views' filmstrips and waveforms, its tracks' waveforms and the
    /// timeline's, each picture once (<see cref="PictureBytes"/>).
    /// </summary>
    private static long ApproxBytesOf(TabViewContext live, long fileBytes)
    {
        var project = live.Project;
        IEnumerable<Layer> views = project.Views is { } kept ? kept.Views.Values.Concat(kept.EmptyTrackViews.Values) : [];
        var pictures = PictureBytes(views.SelectMany(view => new[] { view.Filmstrip, view.Waveform })
            .Concat(project.TrackWaveforms.Values).Append(project.TimelineWaveform).Append(live.Still).Append(live.ProgramSheet?.Picture));
        return (fileBytes > 0 ? 3 * fileBytes : 4L << 20) + pictures;
    }

    /// <summary>
    /// What pictures hold in memory (pure, 5.4D-TabPerf review: the goldens' project-tabs-tabperf-review): each bitmap's width x height x 4
    /// bytes, each object once however many hold it (one file's filmstrip on all its clips' views); nulls and drawings count nothing.
    /// </summary>
    internal static long PictureBytes(IEnumerable<ImageSource?> pictures)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var picture in pictures)
        {
            if (picture is BitmapSource bitmap && seen.Add(bitmap))
                bytes += 4L * bitmap.PixelWidth * bitmap.PixelHeight;
        }

        return bytes;
    }

    /// <summary>
    /// A tab's three files deleted after its other disk work (5.4D-TabPerf: a write still queued is never undone by it, nor done after it).
    /// Pass 5.5A: a crash-recovery file written of it in front goes with them (<see cref="KeepFrontRecovery"/> writes one again when wanted).
    /// </summary>
    private void DeleteTabFiles(string id)
    {
        if (_frontRecovery is { } look && look.Id == id)
            _frontRecovery = null;
        _ = _tabs.Enqueue(id, () => _tabs.DeleteFiles(id));
    }

    /// <summary>A tab closed: what it kept in memory let go, and its cache file's write, if it was still to come, never made.</summary>
    private void ForgetKeptTab(string id)
    {
        ForgetLayoutNotTaken(id);
        if (!_hydrated.Remove(id, out var live))
            return;
        live.Write?.Cancel();
        ForgetUnwritten(live.Write);
        KeptTabsChanged();
    }

    /// <summary>
    /// A write cancelled (its tab came back or closed) taken off the list of those not written yet (5.4D-TabPerf review): one that failed
    /// stays listed until written, and would otherwise hold its tab's frozen copy for the rest of the session. Nothing for null.
    /// </summary>
    private void ForgetUnwritten(TabWrite? write)
    {
        if (write is null)
            return;
        lock (_unwritten)
            _unwritten.Remove(write);
    }

    /// <summary>A tab behind's paused picture: the one it keeps in memory, else the one the tabs keep for the most recent of those in their files.</summary>
    private BitmapSource? StillOfTab(ProjectTabHeader tab) =>
        _hydrated.TryGetValue(tab.Id, out var live) && (live.Still ?? live.Preview) is { } still ? still : _tabs.StillOf(tab);

    /// <summary>
    /// The tabs kept live changed (5.4D-TabPerf): the timeline keeps waveform tiles for them too (256, and 128 more for each, at most
    /// 1024), and the memory valve looks again every 30 s while any is kept.
    /// </summary>
    private void KeptTabsChanged()
    {
        UnifiedTimelineControl.WaveTileCapacity = 256 + 128 * _hydrated.Count;
        HookTabMemory();
        if (_hydrated.Count == 0)
        {
            _valveTimer?.Stop();
            return;
        }

        if (_valveTimer is null)
        {
            _valveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(ValveTickMs) };
            _valveTimer.Tick += (_, _) =>
            {
                RetryFailedTabWrites();
                if (!_switchingTab && !_viewModel.IsPlaying)
                    LetGoForMemory();
            };
        }

        if (!_valveTimer.IsEnabled)
            _valveTimer.Start();
    }

    /// <summary>
    /// What the memory hygiene asks before a compacting collection (5.4D-TabPerf, set once, after the first frame): nothing playing,
    /// recording, being dragged on the Timeline or switching tabs. Asked from any thread: looked at on the window's.
    /// </summary>
    private void HookTabMemory()
    {
        if (_memoryHooked)
            return;
        _memoryHooked = true;
        MemoryHygiene.IsQuiet = () => Dispatcher.CheckAccess()
            ? QuietForMemory()
            : Dispatcher.Invoke(QuietForMemory, DispatcherPriority.Background, CancellationToken.None, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>One compacting collection of the managed heap asked for, once the editor is quiet (a tab closed or let go to its file).</summary>
    private void CompactWhenQuiet(string reason)
    {
        HookTabMemory();
        MemoryHygiene.RequestCompactOnce(reason);
    }

    private bool QuietForMemory() =>
        !_viewModel.IsPlaying && _gpu is not { IsPlaying: true } && !_switchingTab && !TimelineDragHeld && !_viewModel.IsRecording;

    /// <summary>
    /// Which tab kept live the memory valve lets go back to its cache file now (pure, 5.4D-TabPerf: the goldens' tab-perf-valve), or null.
    /// 5.4.2A, all tabs warm (the user's rule): every open tab stays in memory, however many, until the computer's free memory (AvailPhys)
    /// falls under <see cref="AvailableFloorBytes"/> (<paramref name="memoryShort"/>); then the least recently in front of them goes, never
    /// one of the two used last (the tab in front and the one before it) nor one that must stay (its file not written).
    /// </summary>
    internal static string? TabToLetGo(IReadOnlyList<ProjectTabHeader> tabs, IReadOnlyDictionary<string, long> keptBytes, IReadOnlySet<string> mustStay,
        bool memoryShort)
    {
        if (keptBytes.Count == 0 || !memoryShort)
            return null;

        var recent = tabs.OrderByDescending(t => t.LastActive).Take(2).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return tabs.Where(t => keptBytes.ContainsKey(t.Id) && !recent.Contains(t.Id) && !mustStay.Contains(t.Id))
            .OrderBy(t => t.LastActive).Select(t => t.Id).FirstOrDefault();
    }

    /// <summary>
    /// The memory valve (5.4D-TabPerf), after a tab went behind and every 30 s while tabs are kept live: the tab <see cref="TabToLetGo"/>
    /// picks goes back to its cache file once that is written (its still kept while it is among the most recent), and the heap is compacted
    /// once the editor is quiet (MemoryHygiene). Then it looks again.
    /// </summary>
    private async void LetGoForMemory()
    {
        if (_letGoing || _hydrated.Count == 0)
            return;

        try
        {
            var memoryShort = Compositor.SystemCommit.TryRead(out _, out _, out var available) && available < AvailableFloorBytes;
            var kept = _hydrated.ToDictionary(p => p.Key, p => p.Value.ApproxBytes, StringComparer.Ordinal);
            var staying = _hydrated.Where(p => p.Value.MustStay).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            if (TabToLetGo(_tabs.Tabs, kept, staying, memoryShort) is not { } id || _tabs.Find(id) is not { } tab)
                return;

            _letGoing = true;
            var live = _hydrated[id];
            try
            {
                await _tabs.IoOf(id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("Project tabs: a tab's disk work failed before it was let go to its file", ex);
            }

            // Brought back, closed or written badly meanwhile: kept as it is.
            if (!ReferenceEquals(_hydrated.GetValueOrDefault(id), live) || ReferenceEquals(tab, _tabs.Active))
                return;
            if (live.Write is not { IsDone: true, Failed: false })
            {
                live.MustStay = true;
                return;
            }

            _hydrated.Remove(id);
            _tabs.SetStill(tab, live.Still);
            KeptTabsChanged();
            AppLog.Write("Project tabs: a tab kept in memory was let go to its cache file (the computer's free memory is short).");
            CompactWhenQuiet("a project tab let go to its cache file");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the memory valve failed", ex);
            return;
        }
        finally
        {
            _letGoing = false;
        }

        LetGoForMemory();
    }

    /// <summary>
    /// As HandPeg closes (5.4D-TabPerf): every tab's disk work done first (5 s at most), and the dots of the tabs kept live made the exact
    /// ones their writes found (their tell to the window's thread may not have come yet).
    /// </summary>
    private void FinishTabWrites()
    {
        if (!_tabs.WaitFor(null, IoWaitMs))
            AppLog.Write("Project tabs: a tab's cache file was still being written as HandPeg closed (waited 5 s)");
        TakeWrittenDots();
    }

    /// <summary>The dots of the tabs kept live made the exact ones their writes found (their tell to the window's thread may not have come yet).</summary>
    private void TakeWrittenDots()
    {
        foreach (var live in _hydrated.Values)
        {
            if (live.Write is { Dirty: { } dirty } write && _tabs.Tabs.Contains(write.Tab))
                write.Tab.Dirty = dirty;
        }
    }

    /// <summary>
    /// As HandPeg closes keeping the tabs behind with unsaved changes (5.4D-TabPerf review): the disk work still under way for any tab that
    /// may be kept waited for to its end, however long (the title says "Saving tabs..."), so its file is never written a second time beside
    /// it nor cut off as the process ends; their dots the exact ones then.
    /// </summary>
    private void WaitForKeptTabWrites()
    {
        var busy = _tabs.Tabs.Where(t => KeptInFile(t) && !t.Empty && _tabs.IsBusyWith(t.Id)).ToList();
        if (busy.Count == 0)
            return;

        _viewModel.StatusText = "Saving tabs...";
        Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        foreach (var tab in busy)
            _tabs.WaitFor(tab.Id, Timeout.Infinite);
        TakeWrittenDots();
    }

    /// <summary>
    /// The tabs kept live that HandPeg keeps for the next start as it closes (<paramref name="keep"/>), written now on this thread when their
    /// write after the switch failed or never ran (5.4D-TabPerf): their work is only in memory otherwise. Claimed first (the review): one
    /// written, or being written, is never written again beside it.
    /// </summary>
    private void WriteKeptTabsNow(Func<ProjectTabHeader, bool> keep)
    {
        foreach (var tab in _tabs.Tabs.Where(keep))
        {
            if (!_hydrated.TryGetValue(tab.Id, out var live) || live.Write is { } claimed && !claimed.TryStart())
                continue;
            try
            {
                var project = live.Project;
                _tabs.WriteTab(tab.Id, project.Frozen, project.MayBeDirty ? project.SavedFingerprint : null, ProjectTabs.HeaderJson(tab));
                live.Write?.Finish(project.MayBeDirty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("Project tabs: a tab kept for the next start could not be written as HandPeg closed", ex);
            }
            finally
            {
                live.Write?.Fail();
            }
        }
    }

    /// <summary>The writes of the tabs kept live that HandPeg does not keep for the next start let go as it closes (none is made after now).</summary>
    private void CancelTabWrites(Func<ProjectTabHeader, bool> keep)
    {
        foreach (var (id, live) in _hydrated)
        {
            if (_tabs.Find(id) is not { } tab || !keep(tab))
                live.Write?.Cancel();
        }
    }

    /// <summary>The cache files not written yet are written should HandPeg fall over (hooked with the first one queued, never before the first frame).</summary>
    private void HookTabCrashWrites()
    {
        if (_crashWritesHooked)
            return;
        _crashWritesHooked = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.IsTerminating)
                WriteUnwrittenTabs();
        };
    }

    /// <summary>How long HandPeg falling over waits, in all, for tabs' cache files being written on the thread pool, ms.</summary>
    private const int CrashIoWaitMs = 3000;

    /// <summary>
    /// HandPeg falling over (any thread, 5.4D-TabPerf): the tabs put behind whose cache file was queued and not written yet (or failed) are
    /// written now from their frozen copies (nothing of the window's thread is touched), so the next start offers them. The review: one
    /// being written on the thread pool is waited for (3 s at most in all), never written a second time beside it; a write is claimed
    /// first (<see cref="TabWrite.TryStart"/>). Nothing escapes from here.
    /// </summary>
    private void WriteUnwrittenTabs()
    {
        TabWrite[] left;
        lock (_unwritten)
            left = [.. _unwritten];
        var until = Environment.TickCount64 + CrashIoWaitMs;
        foreach (var write in left)
        {
            try
            {
                if (!write.IsWanted)
                    continue;

                // Its disk work under way (this write, or work asked before it): waited for, then written here only if it still is not.
                if (_tabs.IsBusyWith(write.Id))
                    WaitForTabIo(write.Id, until);
                if (!write.TryStart())
                    continue;

                try
                {
                    var project = write.Live.Project;
                    _tabs.WriteTab(write.Id, project.Frozen, project.MayBeDirty ? project.SavedFingerprint : null, write.Header);
                    write.Finish(project.MayBeDirty);
                }
                finally
                {
                    write.Fail();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("Project tabs: a tab could not be written to the cache as HandPeg fell over", ex);
            }
        }
    }

    /// <summary>A tab's disk work waited for until <paramref name="until"/> (Environment.TickCount64) at most; a failure is its writer's to note.</summary>
    private void WaitForTabIo(string id, long until)
    {
        try
        {
            _tabs.IoOf(id).Wait((int)Math.Max(0, Math.Min(until - Environment.TickCount64, int.MaxValue)));
        }
        catch (AggregateException)
        {
        }
    }

    // ----- The tab in front's crash-recovery file (Pass 5.5A) -----
    // The tab in front lives in the view model and keeps no cache file (5.4A): a crash or a power cut took its unsaved work with it unless
    // autosave was on (off at first). On the 20 s autosave tick (Autosave_Tick), whatever the autosave setting says, and never while playing,
    // scrubbing, dragging or switching tabs, a tab in front with unsaved changes whose undo position moved since the last such write is
    // written to its cache file as a tab put behind is (<id>.hptab, its header, its saved state's fingerprint), two actions back when it can
    // be (MainViewModel.Recovery.cs: N-2, with N-1 and N on its Redo). It goes on the tab's own queue (ProjectTabs.Enqueue) like every other
    // write of its files, so a later exact write (put behind, kept for the next start) always lands after it, and a delete (brought back to
    // the front, closed) after it too. A clean tab in front (saved, or undone back to the save) keeps none. A tab in front that could not be
    // brought back is never touched: its file is its work. Closing HandPeg carries its plan out over this file as over any tab's; a crash
    // leaves it, and the next start takes it in as a tab of its own (TakeLeftBehind) that opens at N-2.

    /// <summary>The tick's last look at the tab in front (Pass 5.5A): which tab, its undo position then, and whether a recovery file of it was written.</summary>
    private readonly record struct FrontLook(string Id, (object? Newest, int Undo, int Redo) At, bool Written);

    // The last look (null: none yet, or the tab's files went since): a position looked at already costs no fingerprint again.
    private FrontLook? _frontRecovery;

    /// <summary>What the tick does with the tab in front's crash-recovery file (Pass 5.5A).</summary>
    internal enum FrontRecoveryStep
    {
        None,
        Write,
        Delete,
    }

    /// <summary>
    /// What the tick does with the tab in front's crash-recovery file (pure: autosave-lag-front-tab): nothing for a tab in front that could
    /// not be brought back (its file is its work); for a clean tab, the file deleted when one was written; for a tab with unsaved changes, the
    /// file written when none is yet or its undo position moved since.
    /// </summary>
    internal static FrontRecoveryStep FrontRecoveryFor(bool unresumed, bool written, bool dirty, bool moved) =>
        unresumed ? FrontRecoveryStep.None
        : !dirty ? (written ? FrontRecoveryStep.Delete : FrontRecoveryStep.None)
        : !written || moved ? FrontRecoveryStep.Write
        : FrontRecoveryStep.None;

    /// <summary>
    /// The 20 s tick's look at the tab in front's crash-recovery file (Autosave_Tick, which skips it while playing, scrubbing, dragging or switching,
    /// and, Pass 5.5A review, while <see cref="FrontRecoveryHeld"/>). Skipped while the view model opens, loads or rewrites the Sequence
    /// (BusyWithDocument: never caught half done); 5.5.0 (the user's Q9 b): written during an export, the queue or captions, so what is
    /// edited while they run is kept too.
    /// </summary>
    private void KeepFrontRecovery()
    {
        try
        {
            var tab = _tabs.Active;

            // A look at another tab: that one went behind (its exact write followed on its queue) or closed (its files went with it).
            if (_frontRecovery is { } last && last.Id != tab.Id)
                _frontRecovery = null;
            if (_viewModel.BusyWithDocument || _viewModel.IsRecording)
                return;

            // The fingerprint only when the history moved since the last look: unmoved, the tab is as that look found it (a save since
            // clears MayHaveUnsavedChanges by itself), so an idle project costs nothing.
            var position = _viewModel.HistoryPosition;
            var written = _frontRecovery is { Written: true };
            var moved = _frontRecovery is not { } seen || !Equals(seen.At, position);
            var dirty = _viewModel.MayHaveUnsavedChanges && (moved ? _viewModel.HasUnsavedChanges : written);
            switch (FrontRecoveryFor(_unresumed.Contains(tab.Id), written, dirty, moved))
            {
                case FrontRecoveryStep.Write:
                    WriteFrontRecovery(tab, position);
                    return;

                case FrontRecoveryStep.Delete:
                    DeleteTabFiles(tab.Id);
                    written = false;
                    break;
            }

            _frontRecovery = new FrontLook(tab.Id, position, written);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the tab in front's recovery copy could not be taken", ex);
        }
    }

    /// <summary>
    /// The tab in front's crash-recovery file queued (Pass 5.5A): its state (a copy of its own) and the lag's packed steps taken here on the
    /// window's thread, its header as it would go behind now (on a copy: the strip's own is left as the settling look keeps it), then made two
    /// actions back and written on the tab's queue. One that fails is tried again at the next tick.
    /// </summary>
    private void WriteFrontRecovery(ProjectTabHeader tab, (object? Newest, int Undo, int Redo) position)
    {
        var header = tab with { };
        TakeHeaderForTab(header);
        (header.Dirty, header.Empty) = (true, false);
        var (id, state, lag) = (tab.Id, _viewModel.CaptureStateForTab(header.Title), _viewModel.TakeRecoveryLag());
        var (saved, json) = (_viewModel.SavedFingerprintForTab, ProjectTabs.HeaderJson(header));
        _frontRecovery = new FrontLook(id, position, Written: true);
        _ = _tabs.Enqueue(id, () =>
        {
            if (WriteRecoveryFile(_tabs, id, state, lag, saved, json) is not null)
                return;

            // Still taken as written (what an earlier write left is deleted once the tab is clean), at a position that never matches: tried again.
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_frontRecovery is { } look && look.Id == id && Equals(look.At, position))
                    _frontRecovery = look with { At = (null, -1, -1) };
            }, DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// A tab's crash-recovery file written (Pass 5.5A; any thread: the tab's queue, or a golden): <paramref name="state"/> two actions back when
    /// <paramref name="lag"/> is given and its steps can be read, else as it is, with its header (ProjectTabs.WriteTab). True when written two
    /// actions back, false when written as it is, null when it could not be written (logged).
    /// Pass 5.5A review: written as it is, with its saved state's fingerprint (<paramref name="saved"/>) and a header saying it is exact
    /// (StepsBack 0). Written two actions back, with no fingerprint at all, so it comes back unsaved until it is saved (N-2 can be the saved state
    /// itself: two edits since the save, and the last two then live on its Redo alone), and a header saying how far back (StepsBack 2).
    /// </summary>
    internal static bool? WriteRecoveryFile(ProjectTabs tabs, string id, ProjectState state, MainViewModel.RecoveryLag? lag, string? saved, string header)
    {
        try
        {
            var lagged = lag is not null ? MainViewModel.Lagged(state, lag) : null;
            var back = lagged is not null;
            tabs.WriteTab(id, lagged ?? state, back ? null : saved, ProjectTabs.WithStepsBack(header, back ? MainViewModel.RecoveryLagSteps : 0));
            if (back)
                AppLog.Write("Project tabs: the tab in front's recovery copy was written two actions back (N-2, with N-1 and N on its Redo)");
            return back;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the tab in front's recovery copy could not be written", ex);
            return null;
        }
    }

    /// <summary>The tab in front's crash-recovery file let go at once (Pass 5.5A): the tab was saved, undone back to the save, or emptied.</summary>
    private void DropFrontRecovery(ProjectTabHeader tab)
    {
        if (_frontRecovery is { Written: true } look && look.Id == tab.Id && !_unresumed.Contains(tab.Id))
            DeleteTabFiles(tab.Id);
    }

    /// <summary>
    /// The project in front written to its project file (Pass 5.5A review: MainViewModel.ProjectSaved, raised by every save of it, never by an
    /// open): its crash-recovery file goes at once, on its queue after any write of it still under way, whatever holds back the settling look
    /// and the tick (playing, a drag). Left there, a copy older than the save would be taken in after a crash and could be saved over the file.
    /// Pass 5.5A final review (C-4a): the project and its file are the same now, so the tab carries the file's stamp (<see cref="StampProjectFileAsync"/>:
    /// Pass 5.5A fix round 3, S-R1, taken off the tab's queue, so never behind the recovery write let go here).
    /// </summary>
    private void ViewModel_ProjectSaved()
    {
        DropFrontRecovery(_tabs.Active);
        if (!_unresumed.Contains(_tabs.Active.Id))
            _ = StampProjectFileAsync(_tabs.Active, _viewModel.CurrentProjectPath);
    }

    /// <summary>
    /// The tab in front's project file as the view model has it now (RefreshActiveTab, TakeHeaderForTab), and, Pass 5.5A final review (C-4a), the
    /// stamp it carried let go when that is another file (a New Project, an open, a Save As): a stamp is only ever of the header's own file.
    /// </summary>
    private static void TakeProjectPath(ProjectTabHeader tab, string path)
    {
        if (!string.Equals(tab.ProjectPath, path, StringComparison.OrdinalIgnoreCase))
            (tab.ProjectFileTicks, tab.ProjectFileLength) = (0L, 0L);
        tab.ProjectPath = path;
    }

    /// <summary>
    /// A project just opened into <paramref name="tab"/> (Pass 5.5A final review, C-4a: OpenProjectInTabAsync): the project and its file are the
    /// same now, so the tab carries the file's stamp. Nothing when the open put no project file in the view model (<paramref name="before"/>: the
    /// one there before; it failed, or opened work tied to no file: an autosave, a newer format) or the tab is no longer in front. Pass 5.5A fix
    /// round 3 (S-R1): the stamp <paramref name="stamp"/> of <paramref name="stamped"/> taken before the open read it (off this thread; asked as
    /// <paramref name="asked"/>), never after the open, whose media looks can take seconds: a file saved over from another PC meanwhile would
    /// be stamped as the project read. A change between the stamp and the read only leaves an older stamp, so the tab reads its file as it
    /// comes back (the safe side). None either for a project the open took from another file (a bundle opens the project unpacked beside it:
    /// its tab reads that file as it comes back), nor once another stamp was asked for the tab (a converted project written back as it opened:
    /// that save's own stamp is the one).
    /// </summary>
    private void StampOpenedProject(ProjectTabHeader tab, string before, string stamped, (long Ticks, long Length) stamp, int asked)
    {
        if (ReferenceEquals(tab, _tabs.Active) && !_unresumed.Contains(tab.Id) && _viewModel.CurrentProjectPath is { Length: > 0 } opened
            && !string.Equals(opened, before, StringComparison.OrdinalIgnoreCase) && string.Equals(opened, stamped, StringComparison.OrdinalIgnoreCase)
            && asked == tab.StampsAsked)
            (tab.ProjectPath, tab.ProjectFileTicks, tab.ProjectFileLength) = (opened, stamp.Ticks, stamp.Length);
    }

    /// <summary>
    /// The stamp of a tab's project file (Pass 5.5A final review, C-4a, C-4c), taken as the tab's project and that file are known to be the same: a
    /// project opened into it, the tab in front saved (<see cref="ViewModel_ProjectSaved"/>), a tab behind saved from its cache (SaveTabInPlace).
    /// A tab read back from its file takes the stamp that read took (ResumeTabAsync), and one read back from its cache keeps the one it carried.
    /// The header then carries it as its file is written (ProjectTabs.Stamped never looks at the disk: the close's waited write cannot stall on
    /// a share gone away, nor take the stamp of a file changed since for the project kept). The stamp it carried goes at once (it was of the
    /// file before); the new one is taken away from this thread, and dropped when the tab's project file is another by then (or the tab is
    /// gone). A tab behind has its header file written again with it. Pass 5.5A fix round 3 (S-R1): taken off the tab's queue (Task.Run): it
    /// needs no order with the tab's cache writes (each file write it follows is done before it is asked), so it never waits behind a
    /// recovery write under way, a wait in which the file could change and be stamped as the one saved; and, as two can then land out of
    /// order, it lands only while it is still the last asked for the tab (<see cref="ProjectTabHeader.StampsAsked"/>).
    /// </summary>
    private async Task StampProjectFileAsync(ProjectTabHeader tab, string path)
    {
        (tab.ProjectFileTicks, tab.ProjectFileLength) = (0L, 0L);
        var asked = ++tab.StampsAsked;
        if (path.Length == 0)
            return;

        try
        {
            var (ticks, length) = await Task.Run(() => ProjectTabs.FileStamp(path));
            var now = KeptInFile(tab) ? tab.ProjectPath : _viewModel.CurrentProjectPath;
            if (asked != tab.StampsAsked || !_tabs.Tabs.Contains(tab) || !string.Equals(now, path, StringComparison.OrdinalIgnoreCase))
                return;

            // (The tab in front's header takes its file now too, so the next look at it, RefreshActiveTab, finds it its own.)
            (tab.ProjectPath, tab.ProjectFileTicks, tab.ProjectFileLength) = (now, ticks, length);
            if (KeptInFile(tab))
                WriteTabHeader(tab);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: a project file's stamp could not be taken (its tab reads that file as it comes back)", ex);
        }
    }

    /// <summary>
    /// Whether the 20 s tick leaves the tab in front's crash-recovery copy for the next one (Pass 5.5A review): a press or a drag anywhere (the
    /// player's handles, the 3D view, a slider, the key lanes: the capture is the whole state, taken on this thread), a modal dialog up, or a
    /// close under way.
    /// </summary>
    private bool FrontRecoveryHeld =>
        System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed || System.Windows.Input.Mouse.Captured is not null
        || System.Windows.Interop.ComponentDispatcher.IsThreadModal || _closeAsked;

    /// <summary>The status line for a tab brought back from a crash-recovery copy written <paramref name="steps"/> actions back (pure, Pass 5.5A review).</summary>
    internal static string StepsBackLine(int steps) =>
        $"Opened {steps} action{(steps == 1 ? "" : "s")} back from where HandPeg stopped: Ctrl+Y {steps switch { 1 => "once", 2 => "twice", _ => $"{steps} times" }} brings the last edits back.";
}
