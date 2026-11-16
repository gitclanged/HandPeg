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

// ----- Project tabs: paused still overlay, live in-memory tabs, cache writes, crash recovery -----
public partial class MainWindow
{
    // ----- Paused still overlay -----
    // The player is a native HWND (airspace), so the still is drawn at the bottom of PlayerOverlay until the engine presents a scene
    // marked after activation (GpuPreviewEngine.MarkScene; never an in-flight scene of the outgoing tab): exact frames first, any frame
    // after a grace period, 2.5 s at most. A bounded per-frame watch, active only after a switch.

    private Image? _tabStill;
    private bool _stillWatching;
    private long _stillSince, _stillMark;
    private int _stillSeen;

    /// <summary>Still overlay timing in ms: grace period for exact frames before any frame is accepted, and maximum lifetime.</summary>
    private const long StillExactWaitMs = 600, StillLongestMs = 2500;

    /// <summary>Wraps BGRA8 pixels as a frozen Bgr32 bitmap (alpha ignored).</summary>
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

    /// <summary>Keeps the still up until the engine presents a scene marked after this call (see section header).</summary>
    private void WatchForFirstFrame()
    {
        if (_tabStill is not { Visibility: Visibility.Visible })
            return;

        (_stillMark, _stillSince, _stillSeen) = (_gpu?.MarkScene() ?? 0, Environment.TickCount64, 0);
        if (!_stillWatching)
        {
            // Polled on CompositionTarget.Rendering, subscribed only while the still is up.
            _stillWatching = true;
            CompositionTarget.Rendering += StillWatch_Rendering;
        }
    }

    // Removes the still one render tick after the viewport presented the tab's frame (IDXGISwapChain::Present, so DWM has composed it):
    // exact frames, or any frame after the grace period.
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

    // ----- Live in-memory tabs -----
    // A deactivated tab keeps a TabViewContext by id: the view model part (live Sequence, history, fingerprints, derived file data) and the
    // window part (still, overview sheet, selection, player zoom/pan, open Timeline stacks); AttachHydrated restores it by reference swap.
    // Its cache file is written after the switch from a frozen copy (QueueTabWrite, serialized per tab by ProjectTabs.Enqueue) for crash,
    // close and valve use. All tabs stay warm; the valve (LetGoForMemory) evicts the LRU tab to its file only when free RAM runs short.

    /// <summary>Max wait in ms for a pending tab cache write during app exit, save-from-memory or tab close.</summary>
    private const int IoWaitMs = 5000;

    /// <summary>Free physical memory (AvailPhys) floor below which the valve evicts live tabs to their files (2.5 GB).</summary>
    internal const long AvailableFloorBytes = Compositor.CompositorWorker.AvailableFloorBytes;

    /// <summary>Memory valve poll interval in ms while live tabs exist.</summary>
    private const int ValveTickMs = 30_000;

    /// <summary>Live inactive tabs by id (the active tab lives in the view model). UI thread only.</summary>
    private readonly Dictionary<string, TabViewContext> _hydrated = new(StringComparer.Ordinal);

    // Queued, not-yet-written cache writes; flushed by the crash handler (WriteUnwrittenTabs). Accessed from any thread.
    private readonly List<TabWrite> _unwritten = [];
    private bool _crashWritesHooked, _letGoing, _memoryHooked;
    private DispatcherTimer? _valveTimer;

    /// <summary>Capture context of a still: undo mark, playhead and 3D state (a stale still only covers the player until its first frame),
    /// and whether it is a reduced-resolution playback frame (kept only until a full one is read).</summary>
    private readonly record struct StillMark(object? UndoMark, double PositionMs, bool Shown3D, bool Soft = false);

    /// <summary>A live inactive tab (see section header). Created and used on the UI thread only.</summary>
    private sealed class TabViewContext(MainViewModel.TabProjectContext project)
    {
        /// <summary>View model part: live Sequence, history, fingerprints and frozen copy.</summary>
        public MainViewModel.TabProjectContext Project { get; } = project;

        /// <summary>Latest paused still (MainWindow.TabStill.cs) and its capture context; null if none yet.</summary>
        public BitmapSource? Still { get; init; }

        /// <summary>
        /// Hover-card fallback when deactivated without a still (playing, or just edited): the overview sheet frame at the playhead.
        /// Never shown over the player.
        /// </summary>
        public BitmapSource? Preview { get; init; }

        /// <summary>Timeline rows at deactivation, reused on activation instead of rebuilt.</summary>
        public KeptRows? Rows { get; init; }

        public StillMark StillMark { get; init; }

        /// <summary>The full overview sheet and the Sequence's undo mark at that time.</summary>
        public ThumbnailSheet? ProgramSheet { get; init; }

        public object? ProgramSheetMark { get; init; }

        /// <summary>Timeline selection as the old views; re-resolved by clip id on activation.</summary>
        public object? SelectedClip { get; init; }

        public Layer? RangeAnchor { get; init; }

        public bool PlayheadVideoSelected { get; init; }

        /// <summary>Player zoom and pan (Keybinds navigation).</summary>
        public (double Zoom, double PanX, double PanY) PlayerView { get; init; }

        /// <summary>Open Timeline mini-stacks (row keys like "captions" or a track number repeat across projects).</summary>
        public IReadOnlyList<(object Key, ChildStack Stack)> Expansion { get; init; } = [];

        /// <summary>The post-deactivation cache write.</summary>
        public TabWrite? Write { get; set; }

        /// <summary>Estimated memory footprint in bytes (the valve's metric).</summary>
        public long ApproxBytes { get; set; }

        /// <summary>Cache write failed: never evict (memory holds the only copy).</summary>
        public bool MustStay { get; set; }
    }

    /// <summary>
    /// A pending post-switch cache write: frozen copy plus header at deactivation; the exact dot is computed on the thread pool. State
    /// changes on pool and crash threads, so all access goes through its lock.
    /// </summary>
    private sealed class TabWrite(ProjectTabHeader tab, TabViewContext live, string header, BitmapSource? thumbnailFrom)
    {
        private readonly Lock _gate = new();
        private bool _done, _failed, _cancelled, _superseded, _dirty, _running;

        public ProjectTabHeader Tab { get; } = tab;

        public string Id { get; } = tab.Id;

        public TabViewContext Live { get; } = live;

        /// <summary>Header at deactivation (with the fingerprint-free dot).</summary>
        public string Header { get; } = header;

        /// <summary>Flat still used as the thumbnail of a never-saved project, or null.</summary>
        public BitmapSource? ThumbnailFrom { get; } = thumbnailFrom;

        /// <summary>Pool-encoded thumbnail (null for none) and the written file length.</summary>
        public byte[]? Jpeg { get; set; }

        public long Bytes { get; set; }

        /// <summary>Last attempt finished, successfully or not (<see cref="Failed"/>); false again during a retry.</summary>
        public bool IsDone
        {
            get
            {
                lock (_gate)
                    return _done;
            }
        }

        /// <summary>Last attempt failed or faulted: no recovery copy exists yet (it will be retried).</summary>
        public bool Failed
        {
            get
            {
                lock (_gate)
                    return _failed;
            }
        }

        /// <summary>Cancelled (tab closed): must not be written, so nothing reappears at the next start.</summary>
        public bool IsCancelled
        {
            get
            {
                lock (_gate)
                    return _cancelled;
            }
        }

        /// <summary>Still pending: not fully written (failures stay pending) and not cancelled.</summary>
        public bool IsWanted
        {
            get
            {
                lock (_gate)
                    return !(_done && !_failed) && !_cancelled;
            }
        }

        /// <summary>Exact dirty flag once written; null before, or after a save made it stale.</summary>
        public bool? Dirty
        {
            get
            {
                lock (_gate)
                    return _done && !_failed && !_superseded ? _dirty : null;
            }
        }

        /// <summary>
        /// Claims the write for one writer (pool, crash writer or clean close; any thread). True when pending and unclaimed; the caller must
        /// then <see cref="Finish"/> or <see cref="Fail"/>. False when written, cancelled or in progress, so one file is never written twice concurrently.
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

        /// <summary>Marks the claimed attempt failed; a no-op after Finish (writers call it unconditionally on exit).</summary>
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

        /// <summary>An inactive save (SaveTabInPlace) supersedes this write's dot result.</summary>
        public void Superseded()
        {
            lock (_gate)
                _superseded = true;
        }
    }

    /// <summary>
    /// Detaches the active tab into a live TabViewContext (header refreshed first): MainViewModel.DetachForTab releases it, with the window
    /// part. <paramref name="write"/> is its pending cache write.
    /// </summary>
    private TabViewContext DetachActive(ProjectTabHeader tab, out TabWrite write)
    {
        TakeHeaderForTab(tab);
        var sheetMark = _viewModel.UndoMark;
        var (still, stillMark) = (_liveStill, _liveStillMark);
        // With a 3D still, also keep the flat overview frame at the playhead: 3D stills are never written to the cache file, so a cold 3D tab
        // (valve or restart) still has a hover-card image.
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

        // A never-saved project uses its paused still as thumbnail (JPEG-encoded on the pool with the write) so a cold tab has a hover image.
        // Never a 3D still: project thumbnails are flat.
        write = new TabWrite(tab, live, ProjectTabs.HeaderJson(tab), still is not null && !stillMark.Shown3D ? still : preview);
        live.Write = write;
        live.ApproxBytes = ApproxBytesOf(live, 0, _viewModel.ClipPictures);
        return live;
    }

    /// <summary>
    /// Synchronously reactivates a live tab (already active, layout applied): still overlay, MainViewModel.AttachTab, overview sheet,
    /// selection, open stacks, player view, playhead, then engine open. Its cache file is deleted after its write. Returns false when an
    /// operation or recording holds the view model (no change; caller reverts). A fault leaves it active as failed-resume, kept in memory.
    /// </summary>
    private bool AttachHydrated(ProjectTabHeader tab, TabViewContext live)
    {
        // The header's file and first-save name are newest (a save or rename while inactive).
        var project = live.Project;
        (project.ProjectPath, project.NewProjectName, project.NewProjectFolder) = (tab.ProjectPath, tab.NewProjectName, tab.NewProjectFolder);

        // Show the still only if it matches: within a frame of the playhead and the same flat/3D mode; otherwise it is skipped.
        var stillFits = StillFits(live.StillMark.PositionMs, live.StillMark.Shown3D, tab.PlayheadMs, tab.ExplodedOpen, tab.FrameRate);
        if (stillFits)
            ShowTabStill(live.Still);
        else
            ShowTabStill(tab.ExplodedOpen ? null : FallbackStill(live, tab.PlayheadMs));

        // Selection as the old views; Timeline_Restored re-resolves them by clip id.
        (_selectedClip, _rangeAnchor, _playheadVideoSelected) = (live.SelectedClip, live.RangeAnchor, live.PlayheadVideoSelected);

        // Reuse the saved rows for the layouts the attach triggers, unless the Sequence or row shape changed.
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
            // Partially attached: install an empty project (never touching its Sequence again); the context stays in memory for a retry.
            AppLog.Write("Project tabs: a tab kept in memory could not be brought back", ex);
            HideTabStill();
            _viewModel.ParkDocument();
            _viewModel.NewProject();
            _unresumed.Add(tab.Id);
            _viewModel.StatusText = $"\"{tab.Title}\" could not be brought back ({ex.Message}). Its tab keeps it: click it again to try once more.";
            return true;
        }

        // Active again: cancel a pending write and delete the file after any in-progress one. A fully written dirty file is kept as the active
        // crash-recovery copy instead (as ResumeTabAsync does; the tick rewrites it on history change and deletes it once clean).
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

        // Deactivated in 3D: mark it waiting for view restore, as a file resume does.
        if (tab.ExplodedOpen)
            _explodedWaitingTab = tab;
        RestoreTabView(tab);
        _viewModel.BackgroundSelected = _selectedClip is Layer { IsBackground: true };
        // Re-target the Effects pane to a selected audio clip (EffectsPane.SelectSound; null reverts to the key layer), after the attach
        // has restored the tab's views and key layer.
        EffectsView.SelectSound(_selectedClip as Layer);
        UpdateTimelineInspector();
        UpdatePlayerSelection();
        KeptTabsChanged();

        // No matching still: capture one once settled, so the next deactivation has a hover image.
        if (!stillFits)
            NoteStillStale();

        // Rows lay out on the next pass; the scroll offset is re-clamped to them then.
        _ = Dispatcher.BeginInvoke(() =>
        {
            _rowsToTakeBack = null;
            if (ReferenceEquals(tab, _tabs.Active) && Math.Abs(UnifiedTimeline.Surface.VerticalOffset - tab.VerticalOffset) > 0.5)
                UnifiedTimeline.Surface.VerticalOffset = tab.VerticalOffset;
        }, DispatcherPriority.Background);
        return true;
    }

    /// <summary>
    /// Fallback cover for a flat tab without a matching still: the overview sheet frame at the playhead, or the flat frame kept beside a 3D
    /// still, so the player is never bare before its first frame; null if neither exists.
    /// </summary>
    private static BitmapSource? FallbackStill(TabViewContext live, double playheadMs) =>
        live.ProgramSheet is { Count: > 0 } sheet ? sheet.Cell(sheet.IndexAt(playheadMs / 1000)) : live.Preview;

    /// <summary>
    /// Pure (golden project-tabs-tabperf-review): true when a kept still matches the resume state: within one frame of the playhead
    /// (<paramref name="frameRate"/> from Project Settings, 30 if unknown) and the same flat/3D mode.
    /// </summary>
    internal static bool StillFits(double stillMs, bool still3D, double playheadMs, bool exploded, double frameRate) =>
        still3D == exploded && Math.Abs(stillMs - playheadMs) <= 1000.0 / (frameRate > 0 ? frameRate : 30);

    /// <summary>Restores a tab's player zoom/pan (none = fit, as on open; the Canvas view is fitted too).</summary>
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
    /// Queues a deactivated tab's cache write on the pool after its other disk work: frozen copy (thumbnail from the still if none), saved
    /// fingerprint when dirty, header with the exact dot (MainViewModel.FrozenStateDiffers) for the strip. On failure it is logged, the tab
    /// stays in memory unevictable and listed as unwritten (crash writer, valve tick and next deactivation retry). No-op for null.
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

    /// <summary>Retries failed cache writes of live tabs (called by the valve tick and on deactivation).</summary>
    private void RetryFailedTabWrites()
    {
        foreach (var live in _hydrated.Values)
        {
            if (live.Write is { Failed: true, IsWanted: true } write)
                QueueTabWrite(write);
        }
    }

    /// <summary>
    /// Pool worker for one cache write (see <see cref="QueueTabWrite"/>), then posts the result to the UI thread. Claims first so it never
    /// races the crash writer or a retry.
    /// </summary>
    private void WriteTabNow(TabWrite write)
    {
        if (!write.TryStart())
            return;

        try
        {
            var project = write.Live.Project;

            // Never mutate the frozen copy (a save or the crash writer may be reading it): put the thumbnail into a clone.
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
            // Not fully written (failure or any fault): stays listed for the crash writer and later retries.
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

    /// <summary>UI-thread completion of a cache write: applies exact dot, thumbnail and size estimate for the valve.</summary>
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
        live.ApproxBytes = ApproxBytesOf(live, write.Bytes, _viewModel.ClipPictures);
        if (write.Dirty is { } dirty && dirty != tab.Dirty)
        {
            tab.Dirty = dirty;
            ShowTabs();
        }
    }

    /// <summary>
    /// Estimated bytes held by a live tab (valve metric): ~3x its cache file size for project objects (live Sequence, frozen copy, history;
    /// 4 MB before the file exists) plus its images (still, overview sheet, clip filmstrips/waveforms, track and timeline waveforms),
    /// each counted once (<see cref="PictureBytes"/>).
    /// </summary>
    private static long ApproxBytesOf(TabViewContext live, long fileBytes, ClipPictures clipPictures)
    {
        var project = live.Project;
        var pictures = PictureBytes(FilesOf(live).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(clipPictures.OfFile)
            .Concat(project.TrackWaveforms).Append(project.TimelineWaveform).Append(live.Still).Append(live.ProgramSheet?.Picture));
        return (fileBytes > 0 ? 3 * fileBytes : 4L << 20) + pictures;
    }

    /// <summary>Source files used by a live tab (clip views plus opened file); ClipPictures keeps their images while it is open.</summary>
    private static IEnumerable<string> FilesOf(TabViewContext live)
    {
        var project = live.Project;
        IEnumerable<Layer> views = project.Views is { } kept ? kept.Views.Values.Concat(kept.EmptyTrackViews.Values) : [];
        return views.Select(view => view.ImagePath).Append(project.LocalMediaPath).Where(file => file.Length > 0);
    }

    /// <summary>
    /// Pure (golden project-tabs-tabperf-review): sums width * height * 4 bytes per distinct bitmap (reference-deduplicated, e.g. one
    /// filmstrip shared by many clip views); nulls and drawings count 0.
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
    /// Queues deletion of a tab's three files after its other disk work (so a queued write is neither lost nor applied afterwards); its active
    /// crash-recovery file goes too (<see cref="KeepFrontRecovery"/> rewrites one when needed).
    /// </summary>
    private void DeleteTabFiles(string id)
    {
        if (_frontRecovery is { } look && look.Id == id)
            _frontRecovery = null;
        _ = _tabs.Enqueue(id, () => _tabs.DeleteFiles(id));
    }

    /// <summary>Tab closed: drops its live context and cancels any pending cache write.</summary>
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
    /// Cancels a write (tab reactivated or closed) and removes it from the unwritten list; otherwise a failed one would pin its frozen copy
    /// for the session. No-op for null.
    /// </summary>
    private void ForgetUnwritten(TabWrite? write)
    {
        if (write is null)
            return;
        lock (_unwritten)
            _unwritten.Remove(write);
    }

    /// <summary>An inactive tab's still: from its live context, else from the recent-still cache for file-only tabs.</summary>
    private BitmapSource? StillOfTab(ProjectTabHeader tab) =>
        _hydrated.TryGetValue(tab.Id, out var live) && (live.Still ?? live.Preview) is { } still ? still : _tabs.StillOf(tab);

    /// <summary>
    /// Live-tab set changed: resize the timeline waveform tile cache (256 + 128 per live tab, max 1024) and run the valve every 30 s
    /// while any are live.
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
    /// MemoryHygiene quiet gate (set once after Frame 1): not playing, recording, dragging on the Timeline or switching tabs. Callable
    /// from any thread; evaluated on the UI thread.
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

    /// <summary>Requests one compacting managed-heap collection at the next quiet point (tab closed/evicted, media browser idle, start-up
    /// warm-up done), optionally also compacting the process heap.</summary>
    private void CompactWhenQuiet(string reason, bool compactProcessHeap = false)
    {
        HookTabMemory();
        MemoryHygiene.RequestCompactOnce(reason, compactProcessHeap);
    }

    /// <summary>
    /// Called once from ContentRendered (after Frame 1, Law 1): routes the media browser's idle release through the same quiet gate (hooked
    /// lazily) so no blocking collection runs during play, scrub, record or tab switch.
    /// </summary>
    private void HookMediaMemory() => MediaBrowser.AskCompact = reason => CompactWhenQuiet(reason, compactProcessHeap: true);

    /// <summary>
    /// After all panes realize: one compacting collection (including LOH) plus process-heap compaction via the quiet gate. Little allocates
    /// after Frame 1, so start-up garbage would otherwise stay committed (~40 MB at Law 2's 10 s reading). No working-set trim (Law 2 reads private bytes).
    /// </summary>
    private void GiveBackStartupMemory() => CompactWhenQuiet("start-up warm-up done", compactProcessHeap: true);

    private bool QuietForMemory() =>
        !_viewModel.IsPlaying && _gpu is not { IsPlaying: true } && !_switchingTab && !TimelineDragHeld && !_viewModel.IsRecording;

    /// <summary>
    /// Pure (golden tab-perf-valve): the live tab to evict, or null. All tabs stay warm until AvailPhys drops below
    /// <see cref="AvailableFloorBytes"/> (<paramref name="memoryShort"/>); then the least recently active goes, never the two most recent
    /// (active and previous) nor one whose file is unwritten.
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
    /// Memory valve, run after each deactivation and every 30 s while tabs are live: evicts the <see cref="TabToLetGo"/> pick once its file
    /// is written (keeping its still while recent), requests a quiet-time heap compaction (MemoryHygiene), then re-evaluates.
    /// </summary>
    private void LetGoForMemory() => Faults.Forget(LetGoForMemoryAsync(), nameof(LetGoForMemory));

    private async Task LetGoForMemoryAsync()
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

            // Reactivated, closed or write failed meanwhile: skip.
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
    /// On exit: drains all tab disk work (5 s max) and applies the exact dots found by live tabs' writes (their UI-thread posts may be pending).
    /// </summary>
    private void FinishTabWrites()
    {
        if (!_tabs.WaitFor(null, IoWaitMs))
            AppLog.Write("Project tabs: a tab's cache file was still being written as HandPeg closed (waited 5 s)");
        TakeWrittenDots();
    }

    /// <summary>Applies the exact dots found by live tabs' writes (their UI-thread posts may be pending).</summary>
    private void TakeWrittenDots()
    {
        foreach (var live in _hydrated.Values)
        {
            if (live.Write is { Dirty: { } dirty } write && _tabs.Tabs.Contains(write.Tab))
                write.Tab.Dirty = dirty;
        }
    }

    /// <summary>
    /// Exit while keeping dirty inactive tabs: waits without timeout (title shows "Saving tabs...") for in-progress disk work of any tab
    /// that may be kept, so no file is double-written or truncated at process end; then applies exact dots.
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
    /// On exit, synchronously writes live tabs kept for next start (<paramref name="keep"/>) whose post-switch write failed or never ran
    /// (memory holds the only copy). Claims first, so a written or in-progress file is never written twice.
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

    /// <summary>On exit, cancels pending writes of live tabs not kept for next start (no write happens after this).</summary>
    private void CancelTabWrites(Func<ProjectTabHeader, bool> keep)
    {
        foreach (var (id, live) in _hydrated)
        {
            if (_tabs.Find(id) is not { } tab || !keep(tab))
                live.Write?.Cancel();
        }
    }

    /// <summary>Hooks the crash handler that flushes unwritten cache files (on first queue, never before Frame 1).</summary>
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

    /// <summary>Total ms the crash handler waits for in-progress pool cache writes.</summary>
    private const int CrashIoWaitMs = 3000;

    /// <summary>
    /// Crash handler (any thread): writes queued/failed cache files from frozen copies without touching UI-thread state, so the next start
    /// offers them. In-progress pool writes are awaited (3 s total) rather than duplicated; each write is claimed first
    /// (<see cref="TabWrite.TryStart"/>). Never throws.
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

                // Await in-progress disk work (this write or earlier queued work), then write here only if still unwritten.
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

    /// <summary>Waits for a tab's disk work until <paramref name="until"/> (Environment.TickCount64); failures are logged by the writer.</summary>
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

    // ----- Active-tab crash-recovery file -----
    // The active tab has no cache file of its own. On the 20 s tick (Autosave_Tick; regardless of the autosave setting; skipped while
    // playing, scrubbing, dragging or switching) a dirty active tab whose undo position moved is written like a deactivated tab (<id>.hptab,
    // header, saved fingerprint), lagged two actions when possible (MainViewModel.Recovery.cs: N-2 with N-1 and N on Redo). It uses the
    // tab's queue (ProjectTabs.Enqueue), so later exact writes and deletes always land after it. Clean tabs keep none; a failed-resume tab
    // is never touched. Exit applies its plan to this file; after a crash the next start imports it (TakeLeftBehind), opening at N-2.

    /// <summary>The tick's last observation of the active tab: tab, undo position, and whether a recovery file was written.</summary>
    private readonly record struct FrontLook(string Id, (object? Newest, int Undo, int Redo) At, bool Written);

    // Last observation (null: none yet, or the tab's files were deleted); an unchanged position skips the fingerprint.
    private FrontLook? _frontRecovery;

    /// <summary>Tick action for the active tab's crash-recovery file.</summary>
    internal enum FrontRecoveryStep
    {
        None,
        Write,
        Delete,
    }

    /// <summary>
    /// Pure (golden autosave-lag-front-tab): none for a failed-resume tab (its file is its work); clean tab: delete if one was written;
    /// dirty tab: write if none exists or the undo position moved.
    /// </summary>
    internal static FrontRecoveryStep FrontRecoveryFor(bool unresumed, bool written, bool dirty, bool moved) =>
        unresumed ? FrontRecoveryStep.None
        : !dirty ? (written ? FrontRecoveryStep.Delete : FrontRecoveryStep.None)
        : !written || moved ? FrontRecoveryStep.Write
        : FrontRecoveryStep.None;

    /// <summary>
    /// Tick handler for the active recovery file (Autosave_Tick skips it while playing, scrubbing, dragging, switching or
    /// <see cref="FrontRecoveryHeld"/>). Also skipped while the Sequence is being opened/loaded/rewritten (BusyWithDocument); still runs
    /// during export, queue or captions so edits made meanwhile are protected.
    /// </summary>
    private void KeepFrontRecovery()
    {
        try
        {
            var tab = _tabs.Active;

            // Observation is of another tab: it was deactivated (exact write queued after) or closed (files deleted); reset.
            if (_frontRecovery is { } last && last.Id != tab.Id)
                _frontRecovery = null;
            if (_viewModel.BusyWithDocument || _viewModel.IsRecording)
                return;

            // Fingerprint only if the history moved (a save clears MayHaveUnsavedChanges itself), so an idle project costs nothing.
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
    /// Queues the active recovery write: captures a state copy and packed lag steps on the UI thread plus a header copy (the strip's header
    /// is untouched), then rewinds two actions and writes on the tab's queue. Failures retry on the next tick.
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

            // Record as written (so a clean tab deletes any earlier file) at a never-matching position, forcing a retry.
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_frontRecovery is { } look && look.Id == id && Equals(look.At, position))
                    _frontRecovery = look with { At = (null, -1, -1) };
            }, DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// Writes a recovery file (any thread: tab queue or golden) via ProjectTabs.WriteTab: <paramref name="state"/> rewound two actions when
    /// <paramref name="lag"/> steps are readable, else as-is. As-is: with <paramref name="saved"/> fingerprint, header StepsBack 0. Lagged:
    /// no fingerprint (resumes dirty, since N-2 may equal the saved state), StepsBack 2. Returns true (lagged), false (as-is), null (failed, logged).
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

    /// <summary>Deletes the active recovery file immediately (tab saved, undone to the save point, or emptied).</summary>
    private void DropFrontRecovery(ProjectTabHeader tab)
    {
        if (_frontRecovery is { Written: true } look && look.Id == tab.Id && !_unresumed.Contains(tab.Id))
            DeleteTabFiles(tab.Id);
    }

    /// <summary>
    /// MainViewModel.ProjectSaved (every save, never an open): deletes the recovery file on the tab queue after any in-progress write, even
    /// while playing or dragging (a pre-save copy could otherwise be recovered over the saved file). Then stamps the file
    /// (<see cref="StampProjectFileAsync"/>, off the queue, so never queued after that delete).
    /// </summary>
    private void ViewModel_ProjectSaved()
    {
        DropFrontRecovery(_tabs.Active);
        if (!_unresumed.Contains(_tabs.Active.Id))
            _ = StampProjectFileAsync(_tabs.Active, _viewModel.CurrentProjectPath);
    }

    /// <summary>
    /// Syncs the active header's project file from the view model (RefreshActiveTab, TakeHeaderForTab), dropping its stamp when the file
    /// changed (New Project, open, Save As): a stamp always refers to the header's own file.
    /// </summary>
    private static void TakeProjectPath(ProjectTabHeader tab, string path)
    {
        if (!string.Equals(tab.ProjectPath, path, StringComparison.OrdinalIgnoreCase))
            (tab.ProjectFileTicks, tab.ProjectFileLength) = (0L, 0L);
        tab.ProjectPath = path;
    }

    /// <summary>
    /// After OpenProjectInTabAsync: applies <paramref name="stamp"/> of <paramref name="stamped"/> (taken off-thread before the read, as request
    /// <paramref name="asked"/>) to <paramref name="tab"/>. Taken before, not after (media probing can take seconds), so a concurrent change only
    /// yields an older stamp and a safe re-read on resume. Skipped when the open set no new project file (<paramref name="before"/>: failed,
    /// autosave, newer format), the tab is inactive, the project came from another file (bundle), or a newer stamp was requested.
    /// </summary>
    private void StampOpenedProject(ProjectTabHeader tab, string before, string stamped, (long Ticks, long Length) stamp, int asked)
    {
        if (ReferenceEquals(tab, _tabs.Active) && !_unresumed.Contains(tab.Id) && _viewModel.CurrentProjectPath is { Length: > 0 } opened
            && !string.Equals(opened, before, StringComparison.OrdinalIgnoreCase) && string.Equals(opened, stamped, StringComparison.OrdinalIgnoreCase)
            && asked == tab.StampsAsked)
            (tab.ProjectPath, tab.ProjectFileTicks, tab.ProjectFileLength) = (opened, stamp.Ticks, stamp.Length);
    }

    /// <summary>
    /// Stamps a tab's project file when project and file are known identical (open, active save via <see cref="ViewModel_ProjectSaved"/>,
    /// inactive SaveTabInPlace); resumes reuse the read's or carried stamp. The header persists it (ProjectTabs.Stamped never touches disk,
    /// so exit cannot stall on a dead share). Clears the old stamp at once; the new one is taken via Task.Run (not the tab queue, to avoid
    /// waiting on a recovery write while the file changes) and applied only if the file is unchanged and it is the latest request
    /// (<see cref="ProjectTabHeader.StampsAsked"/>). Inactive tabs rewrite their header file with it.
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

            // Also set the active header's file now so the next RefreshActiveTab sees it as its own.
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
    /// True when the tick defers the active recovery copy: a press or drag anywhere (player handles, 3D view, slider, key lanes; the capture
    /// is a full UI-thread state copy), a modal dialog, or a close in progress.
    /// </summary>
    private bool FrontRecoveryHeld =>
        System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed || System.Windows.Input.Mouse.Captured is not null
        || System.Windows.Interop.ComponentDispatcher.IsThreadModal || _closeAsked;

    /// <summary>Pure: status text for a tab restored from a recovery copy <paramref name="steps"/> actions back.</summary>
    internal static string StepsBackLine(int steps) =>
        $"Opened {steps} action{(steps == 1 ? "" : "s")} back from where HandPeg stopped: Ctrl+Y {steps switch { 1 => "once", 2 => "twice", _ => $"{steps} times" }} brings the last edits back.";
}
