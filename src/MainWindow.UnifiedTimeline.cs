using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Timeline pane host: rows, tools, inspector, surface callbacks -----
// Views/UnifiedTimelinePane is one drawn surface with ruler, scroll bars and tools. This partial builds rows from the view model
// (LayerRows, AudioRows, folds, caption anchor) at the view scale and handles surface/tool requests: select, seek, edit via view-model
// verbs, track menus (FillTrackMenu), unfold, close gaps, split. _selectedClip is window-owned, shared with Canvas, Keyframes and playback bar.
public partial class MainWindow
{
    // Row heights: video/audio follow the global Track Height (1..50, LayerTrackHeight; set by the height pill/slider, Alt+Shift+wheel,
    // Shift+edge drag, Pane Settings), video 32..120 px and audio slightly lower; a track may override it (edge drag,
    // TimelineTrackModel.Height). Captions and background rows are thin.
    private const double CaptionRowHeight = 30;
    private const double BackgroundRowHeight = 26;
    private const double LowestPictureRow = 32, TallestPictureRow = 120, LowestSoundRow = 28, TallestSoundRow = 92;

    /// <summary>Default Track Height (restored by double-clicking a row's lower edge or the height pill).</summary>
    private const double UsualTrackHeight = 25;

    // Track Height steps per Alt+Shift+wheel notch, and the setting at the start of an edge drag.
    private const int TrackHeightStepsPerNotch = 2;
    private double _trackHeightAtDrag;

    // Per-row resize in progress: row key and live height until release commits it.
    private (object Key, double Height)? _rowHeightDrag;

    private static readonly object CaptionsRowKey = "captions";
    private static readonly object BackgroundRowKey = "background";

    private double PictureRowHeight => RowHeightAt(LowestPictureRow, TallestPictureRow);

    private double SoundRowHeight => RowHeightAt(LowestSoundRow, TallestSoundRow);

    private double RowHeightAt(double lowest, double tallest) => Math.Round(lowest + (Math.Clamp(_viewModel.LayerTrackHeight, 1, 50) - 1) * (tallest - lowest) / 49);

    /// <summary>Effective row height: live drag height, else the track override, else the global Track Height.</summary>
    private double RowHeightOf(object key, TimelineTrackModel? track, TimelineRowKind kind)
    {
        var (lowest, tallest, shared) = kind == TimelineRowKind.Audio
            ? (LowestSoundRow, TallestSoundRow, SoundRowHeight)
            : (LowestPictureRow, TallestPictureRow, PictureRowHeight);
        var own = _rowHeightDrag is { } drag && Equals(drag.Key, key) ? drag.Height : track?.Height;
        return own is { } height ? Math.Round(Math.Clamp(height, lowest, tallest)) : shared;
    }

    /// <summary>Track owning a row's height override (track rows only, never a stack lane showing the same layer).</summary>
    private TimelineTrackModel? HeightTrackOf(TimelineRow row) =>
        row.Data is Layer layer && Equals(row.Key, layer.TrackId) ? _viewModel.TrackOfRow(layer) : null;

    /// <summary>Converts a video-row DIP delta to Track Height steps.</summary>
    private static double TrackHeightSteps(double pixels) => pixels * 49 / (TallestPictureRow - LowestPictureRow);

    // Reentrancy guard: a scale change during a rebuild does not trigger another.
    private bool _rebuildingRows;

    // Row whose audio stack auto-expands after the next rebuild (a clip just unlinked, so its lane is draggable).
    private object? _expandRowKey;

    // Last-built track rows by track id, used to find rows that slide with a fold (the track row and its stack).
    private Dictionary<Guid, TimelineRow> _rowByTrack = [];

    /// <summary>
    /// Rows captured at tab deactivation, reused on activation while the Sequence (undo mark, views), scale and row-shaping switches are unchanged.
    /// </summary>
    private sealed record KeptRows(TimelineRow[] Rows, Dictionary<Guid, TimelineRow> ByTrack, double Scale, object? UndoMark, (bool, bool, bool) Shape);

    // Pending reusable rows of the activating tab, until consumed or the switch's layouts finish.
    private KeptRows? _rowsToTakeBack;

    // Row-shaping switches (BuildTimelineRows): waveforms, Show Linked Audio, effect lanes.
    private (bool, bool, bool) RowShape() =>
        (AppSettings.Current.ShowClipWaveforms, _viewModel.ShowLinkedAudio, AdoptsEffectLanes(AppSettings.Current));

    /// <summary>Current rows for the deactivating tab; null while stale or rebuilding.</summary>
    private KeptRows? KeepRows() =>
        _rowsStale || _rebuildingRows || UnifiedTimeline.Surface.IsDragging ? null
            : new(UnifiedTimeline.Surface.Rows.ToArray(), _rowByTrack, UnifiedTimeline.Surface.PixelsPerSecond, _viewModel.UndoMark, RowShape());

    /// <summary>The activating tab's saved rows if still valid at <paramref name="scale"/> (see <see cref="KeptRows"/>).</summary>
    private TimelineRow[]? TakeBackRows(double scale)
    {
        if (_rowsToTakeBack is not { } kept || kept.Scale != scale || !ReferenceEquals(kept.UndoMark, _viewModel.UndoMark) || kept.Shape != RowShape())
            return null;

        // Every track row must still reference a current view (never a recreated one).
        var layers = _viewModel.Layers.ToHashSet();
        if (kept.Rows.Any(r => r.Data is Layer layer && !layers.Contains(layer)) || kept.Rows.Count(r => r.Data is Layer) > layers.Count)
            return null;

        _rowsToTakeBack = null;
        _rowByTrack = kept.ByTrack;
        return kept.Rows;
    }

    // Watched clip views and audio streams (strip/waveform arrival, name, mute) while on the timeline; two swapped sets avoid
    // per-rebuild allocation.
    private HashSet<INotifyPropertyChanged> _rowSources = [];
    private HashSet<INotifyPropertyChanged> _rowSourcesNext = [];

    /// <summary>
    /// One-time wiring from the constructor: surface events and queries (selection, snap points, frame rate), ruler, scroll bar and zoom
    /// to the shared view, and tools. Builds no rows (they arrive once media opens).
    /// </summary>
    private void InitializeUnifiedTimeline()
    {
        var pane = UnifiedTimeline;
        var surface = pane.Surface;
        pane.ToolClick += TimelineTool_Click;
        pane.HotbarMenuRequested += (index, at) => RunGuarded("a hotbar slot's menu", () => OpenHotbarSlotMenu(index, at));
        ShowHotbar();

        surface.IsSelected = IsSelected;
        surface.SnapTimes = SnapTimes;
        surface.FrameRate = () => _viewModel.SequenceFps;
        surface.BlockPressed += Timeline_BlockPressed;
        surface.EmptyPressed += Timeline_EmptyPressed;
        surface.DragRefused += Timeline_DragRefused;

        // Flush stale rows before hit-testing a press and immediately at drag end (success, refusal or failure); surface edit faults
        // become a status line.
        surface.PressStarting += FlushClipRedraw;
        surface.DragEnded += Timeline_DragEnded;
        surface.CallbackFailed += ex => TimelineFault("an edit on the timeline", ex);
        surface.RevealHideRequested += Timeline_RevealHideRequested;
        _viewModel.TimelineRestored += Timeline_Restored;

        surface.ZoomRequested += (factor, x) => ZoomTimeline(factor, surface.TimeAreaWidth > 0 ? x / surface.TimeAreaWidth : 0.5);
        surface.PanRequested += pixels => PanTimeline(surface.PixelsPerSecond > 0 ? pixels / surface.PixelsPerSecond : 0);
        // Width changes (splitter, window resize) rebuild rows once movement pauses (debounced).
        surface.ScaleChanged += ScheduleClipRedraw;
        surface.HeaderSwitchToggled += Timeline_HeaderSwitchToggled;
        surface.MenuRequested += Timeline_MenuRequested;
        surface.SeamClicked += Timeline_SeamClicked;
        surface.CloseGapRequested += Timeline_CloseGapRequested;
        surface.RazorRequested += Timeline_RazorRequested;

        // Alt+wheel track height, gap double-click, Ghost Playhead (timeline gestures host, MainWindow.TimelineView.cs).
        InitializeTimelineGestures(surface);

        // Global track height from the surface: Alt+Shift+wheel, lower-edge drag or double-click.
        surface.TrackHeightStepped += notches => _viewModel.LayerTrackHeight = Math.Clamp(_viewModel.LayerTrackHeight + notches * TrackHeightStepsPerNotch, 1, 50);
        surface.TrackHeightDragStarted += () => _trackHeightAtDrag = _viewModel.LayerTrackHeight;
        surface.TrackHeightDragged += pixels => _viewModel.LayerTrackHeight = Math.Clamp(_trackHeightAtDrag + TrackHeightSteps(pixels), 1, 50);
        surface.TrackHeightReset += () => _viewModel.LayerTrackHeight = UsualTrackHeight;

        // Per-track height: live while dragging, one Undo step on release.
        surface.RowHeightDragged += (row, height) =>
        {
            _rowHeightDrag = (row.Key, height);
            ScheduleClipRedraw();
        };
        surface.RowHeightDragEnded += (row, kept) =>
        {
            var height = _rowHeightDrag is { } drag ? RowHeightOf(drag.Key, null, row.Kind) : (double?)null;
            _rowHeightDrag = null;
            if (kept && height is not null && HeightTrackOf(row) is { } track)
                _viewModel.SetTrackHeight(track, height);
            ScheduleClipRedraw();
        };
        surface.RowHeightReset += row =>
        {
            if (HeightTrackOf(row) is { } track)
                _viewModel.SetTrackHeight(track, null);
        };

        // Multi-selection (click release resolves Ctrl/Shift on selected clips; group drag = one Undo step) and ruler pointer time
        // forwarded like the rows' (Instant Seek).
        surface.BlockClicked += Timeline_BlockClicked;
        surface.HeaderClicked += Timeline_HeaderClicked;

        // Preset badge opens the Game Preset list; rubber band selects touched clips.
        surface.BadgePressed += Timeline_BadgePressed;
        surface.MarqueeSelected += Timeline_MarqueeSelected;
        surface.Together = _viewModel.EditTogether;

        // Video track header drags: to a seam (move with Sub-Lanes, or un-nest), or onto the lower half of another top-level video
        // header (nest as its Sub-Lane). One Undo step each.
        surface.CanDragRow = row => TrackLayerOf(row) is { } layer && _viewModel.CanDragTrack(layer);
        surface.CanMoveRowTo = (row, beside, above) => TrackLayerOf(row) is { } layer && TrackLayerOf(beside) is { } besideLayer && _viewModel.CanMoveTrackToSeam(layer, besideLayer, above);
        surface.CanNestRow = (row, parent) => TrackLayerOf(row) is { } layer && TrackLayerOf(parent) is { } parentLayer && _viewModel.CanNestTrack(layer, parentLayer);
        surface.RowMoveRequested += Timeline_RowMoveRequested;
        surface.RowNestRequested += Timeline_RowNestRequested;
        _viewModel.ClipSelectionChanged += ClipGroup_Changed;
        pane.Ruler.PointerSecondsChanged += surface.NotePointerOverRuler;
        RegisterTimelineChrome(pane.Ruler, pane.TimeScroll, pane.ZoomSlider);
        HookTimelineMenuKey(this);

        _viewModel.PropertyChanged += UnifiedTimeline_ViewModelChanged;
        _viewModel.LayerRows.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.AudioRows.CollectionChanged += (_, _) => ScheduleClipRedraw();

        // Clip image cached or released (strip, waveform): coalesced block redraw.
        _viewModel.ClipPictures.Changed += ScheduleClipRedraw;
        _viewModel.ClipPictures.OtherFilesInUse = () => _hydrated.Values.SelectMany(FilesOf);

        // Corner Play/Pause shown only while neither the playback bar nor the Scrubber is visible.
        TimelinePane.IsVisibleChanged += (_, _) => ShowTimelinePlayButton();
        pane.Loaded += (_, _) =>
        {
            ShowTimelinePlayButton();
            UpdateTimelineChrome();
        };
    }

    private void UnifiedTimeline_ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.LayerTrackHeight) or nameof(MainViewModel.AudioTrackHeight) or nameof(MainViewModel.ShowClipKeyframes)
                or nameof(MainViewModel.HideDroppedTracks) or nameof(MainViewModel.AutoCaptions):
                ScheduleClipRedraw();
                break;
            case nameof(MainViewModel.ShowSimplePlayback):
                ShowTimelinePlayButton();
                break;
        }
    }

    private void ShowTimelinePlayButton() =>
        UnifiedTimeline.PlayButton.Visibility = _viewModel.ShowSimplePlayback && !TimelinePane.IsVisible ? Visibility.Visible : Visibility.Collapsed;

    // ----- Surface view -----

    /// <summary>Pushes the shared view to the surface (60 s span while empty, matching the ruler); scale derives from it.</summary>
    private void ShowViewOnSurface()
    {
        ClampView();
        var span = ViewSpan;
        UnifiedTimeline.Surface.SetView(_viewStart, span > 0 ? span : EmptyRulerSeconds, _viewModel.DurationMs / 1000);
    }

    /// <summary>
    /// Applies a view change: pans translate; zooms rescale and rebuild rows (never mid-drag; drags keep their start scale).
    /// </summary>
    private void ShowTimelineView()
    {
        var surface = UnifiedTimeline.Surface;
        var scale = surface.PixelsPerSecond;
        ShowViewOnSurface();
        if (surface.PixelsPerSecond != scale && !surface.IsDragging)
            RebuildTimelineRows();
    }

    // ----- Rows -----

    /// <summary>
    /// Rebuilds rows at the surface scale and hands them over (expansion persists by key). Never mid-drag; drag end requests a rebuild.
    /// </summary>
    private void RebuildTimelineRows()
    {
        var surface = UnifiedTimeline.Surface;
        if (_rebuildingRows || surface.IsDragging)
            return;

        _rebuildingRows = true;

        // Clear the stale flag first: any change during the build re-schedules.
        _rowsStale = false;
        try
        {
            ShowViewOnSurface();
            surface.SetRows((IReadOnlyList<TimelineRow>?)TakeBackRows(surface.PixelsPerSecond) ?? BuildTimelineRows(surface.PixelsPerSecond));
            surface.PlayheadSeconds = _viewModel.DurationMs > 0 ? _viewModel.PositionMs / 1000 : double.NaN;
            if (_expandRowKey is { } key)
            {
                _expandRowKey = null;
                if (FindRow(surface.Rows, key) is { } row)
                    surface.SetExpanded(row, ChildStack.Audio, true);
            }

            WatchRowSources();
            UpdateTimelineInspector();
        }
        finally
        {
            _rebuildingRows = false;
        }
    }

    /// <summary>
    /// Builds rows top to bottom at <paramref name="scale"/> px/s: video tracks in stacking order (front first, LayerRows) with children in
    /// their mini-stacks (child tracks, HUD Cutouts, unlinked audio, stream Sub-Lanes); captions in their source track's stack (else top);
    /// the blur fill row below the video; audio tracks last. Keys are undo-stable (Layer.TrackId, "captions", "background"), preserving expansion.
    /// </summary>
    private List<TimelineRow> BuildTimelineRows(double scale)
    {
        var rows = new List<TimelineRow>();
        var seconds = _viewModel.DurationMs / 1000;
        if (_viewModel.Layers.Count == 0 && seconds <= 0)
        {
            // Empty timeline (new project, last clip removed): also release cached combined waveforms.
            GiveStacksTheirSound([], 0, 0);
            return rows;
        }

        // Track rows: video in stacking order, then audio tracks (video audio lives on its clip's row, or its lane when unlinked).
        var pictures = new List<(Layer Layer, TimelineRow Row)>();
        TimelineRow? captionRow = null, backgroundRow = null;
        foreach (var layer in _viewModel.LayerRows)
        {
            if (layer.IsCaptions)
                captionRow = CaptionRow(layer, scale, seconds);
            else if (layer.IsBackground)
                backgroundRow = BackgroundRow(layer, scale, seconds);
            else
                pictures.Add((layer, TrackRow(layer, scale, seconds)));
        }

        // File streams are audio-clip Sub-Lanes in their video track's stack; output audio tracks belong to the Audio tab, not the timeline.
        var sounds = new List<(Layer Layer, TimelineRow Row)>();
        foreach (var item in _viewModel.AudioRows)
        {
            if (item is Layer { IsAudio: true } sound)
                sounds.Add((sound, TrackRow(sound, scale, seconds)));
        }

        // Show captions even when their layer is not listed (generated or imported).
        if (captionRow is null && seconds > 0 && _viewModel.GetSubtitleSegments().Count > 0)
            captionRow = CaptionRow(_viewModel.CaptionLayer, scale, seconds);

        // Child tracks (ParentTrackId) go into their parent's mini-stack.
        var byLayer = new Dictionary<Layer, TimelineRow>(pictures.Count + sounds.Count);
        var byTrack = new Dictionary<Guid, TimelineRow>(pictures.Count + sounds.Count);
        foreach (var (layer, row) in pictures.Concat(sounds))
        {
            byLayer[layer] = row;
            if (_viewModel.TrackIdOf(layer) is { } id)
                byTrack[id] = row;
        }

        foreach (var (layer, row) in pictures.Concat(sounds))
        {
            if (_viewModel.ParentRowOf(layer) is { } parent && byLayer.TryGetValue(parent, out var parentRow) && !Within(parentRow, row))
                Adopt(parentRow, row);
        }

        // Video track effect lanes (EffectRows) in its stack, taking space only while open; omitted when effect lanes are off (only the
        // "fx N" badge shows, AdoptsEffectLanes).
        if (AdoptsEffectLanes(AppSettings.Current))
        {
            foreach (var (layer, row) in pictures)
            {
                foreach (var lane in EffectRows(layer, scale))
                    Adopt(row, lane);
            }
        }

        // Captions sit in their source track's visual stack (beside HUD Cutouts and masks, never audio lanes) when drawn directly over
        // that track; otherwise their row follows draw order: under the track above them, or at the very top.
        TimelineRow? overCaptions = null;
        if (captionRow is not null)
        {
            var anchor = _viewModel.CaptionAnchorTrackId is { } anchorId ? byTrack.GetValueOrDefault(anchorId) : null;
            if (anchor is not null && _viewModel.CaptionsAreOverTheirTrack)
                Adopt(anchor, captionRow);
            else if (_viewModel.Document.CaptionsUnderTrack is { } over && byTrack.TryGetValue(over.Id, out var overRow))
                overCaptions = overRow;
            else
                rows.Add(captionRow);
        }

        // Blur fill settings row below the video: view-only, sequence-level look for per-video fills; no clip, no links.
        rows.AddRange(pictures.Where(p => p.Row.Parent is null).Select(p => p.Row));
        if (overCaptions is not null)
        {
            var top = overCaptions;
            while (top.Parent is { } up)
                top = up;
            rows.Insert(rows.IndexOf(top) + 1, captionRow!);
        }

        // Background Blur draws beneath all video: its row is last in the lowest family's stack when only that family has blur fills,
        // else last among all video rows.
        if (backgroundRow is { Parent: null } && BlurFamilyRow(byTrack) is { } family)
            Adopt(family, backgroundRow);
        if (backgroundRow is { Parent: null })
            rows.Add(backgroundRow);
        rows.AddRange(sounds.Where(s => s.Row.Parent is null).Select(s => s.Row));

        // Each stack has two independently expandable parts: visual (captions first as topmost, child tracks, blur fill row last) then
        // audio lanes (stream lanes named "Stream 1", "Stream 2", ...).
        OrderStacks(rows, row => row.Data is Layer lane && _viewModel.StreamOrdinalOf(lane) is { } ordinal ? ordinal : int.MaxValue);
        MarkInFront(rows);

        // Combined stack waveforms need lanes already stacked; skipped (and released) while waveforms or Show Linked Audio are off.
        GiveStacksTheirSound(AppSettings.Current.ShowClipWaveforms && _viewModel.ShowLinkedAudio ? rows : [], scale, seconds);
        _rowByTrack = byTrack;

        // Dead-air seams on their track's row, with sliding rows (that track and its stack; all unlocked rows for an all-tracks fold).
        Dictionary<TimelineRow, List<SeamMark>>? seams = null;
        foreach (var (trackId, at, fold) in _viewModel.FoldSeams())
        {
            if (!byTrack.TryGetValue(trackId, out var row))
                continue;

            seams ??= new Dictionary<TimelineRow, List<SeamMark>>();
            if (!seams.TryGetValue(row, out var marks))
                seams[row] = marks = [];
            marks.Add(new SeamMark(at, fold.Length, fold, fold.AllTracks ? null : RowKeysOf(row)));
        }

        if (seams is not null)
        {
            foreach (var (row, marks) in seams)
            {
                marks.Sort(static (a, b) => a.Seconds.CompareTo(b.Seconds));
                row.Seams = marks;
            }
        }

        // Unfolded dead-air ranges: shaded on their row with a Hide pill.
        Dictionary<TimelineRow, List<RevealMark>>? reveals = null;
        foreach (var (trackId, from, to, restored) in _viewModel.RestoredStretches())
        {
            if (!byTrack.TryGetValue(trackId, out var row) || !(to - from > 0))
                continue;

            reveals ??= new Dictionary<TimelineRow, List<RevealMark>>();
            if (!reveals.TryGetValue(row, out var marks))
                reveals[row] = marks = [];
            marks.Add(new RevealMark(from, to, restored, restored.AllTracks ? null : RowKeysOf(row)));
        }

        if (reveals is not null)
        {
            foreach (var (row, marks) in reveals)
            {
                marks.Sort(static (a, b) => a.From.CompareTo(b.From));
                row.Reveals = marks;
            }
        }

        return rows;

        // Adds a child row to the visual part, or (audio track, stream lane) to the audio part.
        static void Adopt(TimelineRow parent, TimelineRow child)
        {
            child.Parent = parent;
            child.Stack = child.Kind == TimelineRowKind.Audio ? ChildStack.Audio : ChildStack.Visual;
            parent.Children.Add(child);
        }

        // Each stack = visual part then audio part, stable insertion order within each; visual: captions first, blur fill row last;
        // audio: streams by file index.
        static void OrderStacks(List<TimelineRow> rows, Func<TimelineRow, int> stream)
        {
            foreach (var row in rows)
            {
                if (row.Children.Count == 0)
                    continue;

                var ordered = row.Children.OrderBy(static child => child.Stack == ChildStack.Audio ? 3
                    : child.Kind == TimelineRowKind.Captions ? 0 : child.Kind == TimelineRowKind.Background ? 2 : 1)
                    .ThenBy(child => child.Stack == ChildStack.Audio ? stream(child) : 0).ToList();
                row.Children.Clear();
                row.Children.AddRange(ordered);
                OrderStacks(row.Children, stream);
            }
        }

        // Cycle guard: true if a row is the other or is (at any depth) in its stack.
        static bool Within(TimelineRow row, TimelineRow other)
        {
            for (var at = row; at is not null; at = at.Parent)
            {
                if (ReferenceEquals(at, other))
                    return true;
            }

            return false;
        }

        // Marks visual children composited above their parent (InFront), from the compositor's draw order: a track above its parent
        // (e.g. a Cutout; one sent backward is below), captions above all, the blur fill row below all. Open stacks lay InFront
        // children above the parent row.
        void MarkInFront(List<TimelineRow> level)
        {
            foreach (var row in level)
            {
                if (row.Children.Count == 0)
                    continue;

                var parentPlace = StackPlaceOf(row);
                foreach (var child in row.Children)
                {
                    child.InFront = child.Stack == ChildStack.Visual && child.Kind switch
                    {
                        TimelineRowKind.Captions => true,
                        TimelineRowKind.Background => false,
                        _ => StackPlaceOf(child) is >= 0 and var place && parentPlace >= 0 && place > parentPlace,
                    };
                }

                MarkInFront(row.Children);
            }
        }

        // Stacking index of a track row (Document.Tracks, bottom first); -1 for non-track rows.
        int StackPlaceOf(TimelineRow row) =>
            row.Data is Layer layer && _viewModel.TrackOfRow(layer) is { } track ? _viewModel.Document.Tracks.IndexOf(track) : -1;
    }

    // Header-drag capability of a video track row.
    private const string TrackDragTip = "Drag this header up or down to move the track, its Sub-Lanes with it; drag it right onto the lower half of another track's header to make it that track's Sub-Lane. Esc cancels the drag.";
    private const string EmptyTrackDragTip = "An empty track: drop videos and pictures onto it. " + TrackDragTip;
    private const string SubLaneDragTip = "A Sub-Lane: drag this header past the top or bottom of its stack, to a seam between the tracks, to take it out of its stack, or right onto another track's header to move it into that one's. Let go over its own stack, or press Esc, and nothing changes.";

    /// <summary>Builds a track row (video, image, empty or audio) with its clips, plus an unlinked-audio lane in its stack when needed.</summary>
    private TimelineRow TrackRow(Layer layer, double scale, double seconds)
    {
        var kind = layer.IsEmptyTrack ? TimelineRowKind.Empty : layer.IsAudio ? TimelineRowKind.Audio : TimelineRowKind.Video;
        var track = _viewModel.TrackOfRow(layer);
        var row = new TimelineRow
        {
            Key = layer.TrackId,
            Data = layer,
            Kind = kind,
            Name = track is { Name.Length: > 0 } ? track.Name : layer.Name,
            Height = RowHeightOf(layer.TrackId, track, kind),
            SizedAlone = track is not null,
            HasOwnHeight = track?.Height is not null || (_rowHeightDrag is { } drag && Equals(drag.Key, layer.TrackId)),
            Accent = layer.IsAudio ? SoundClipBrush : LayerClipBrush,

            // Video track headers are draggable (move, nest, un-nest).
            Tip = track is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch }
                ? track.ParentTrackId is null ? layer.IsEmptyTrack ? EmptyTrackDragTip : TrackDragTip : SubLaneDragTip
                : layer.IsEmptyTrack ? "An empty track: drop videos and pictures onto it." : null,

            // Gap pills only on unlocked tracks (unlinked-audio lanes, captions and the blur fill row have none).
            ClosesGaps = track is { IsLocked: false },

            // Track Preset badge on top-level video tracks (empty ones too, so it can be set before dropping media).
            Badge = _viewModel.TrackPresetOf(layer),
            OffersBadge = _viewModel.CanHaveTrackPreset(layer),
        };

        // Header switches: eye (audio tracks mute instead), audio mute, solo (track-level in the document; Alt = exclusive), voice, lock.
        var (offered, state) = (RowSwitches.None, RowSwitches.None);
        if (layer.IsAudio)
        {
            offered |= RowSwitches.Muted | RowSwitches.Voice | (track is not null ? RowSwitches.Soloed : 0);
            state |= (layer.IsHidden ? RowSwitches.Muted : 0) | (layer.IsVoice ? RowSwitches.Voice : 0) | (track is { IsSoloed: true } ? RowSwitches.Soloed : 0);
        }
        else
        {
            // The eye is the track's (whole family's) switch, not its first clip's.
            if (layer.CanHide)
            {
                offered |= RowSwitches.Hidden;
                state |= _viewModel.IsTrackHidden(layer) ? RowSwitches.Hidden : 0;
            }

            // Master mute (all of a video track's audio at once): only on audible tracks, never on Cutout/mask child tracks or silent video.
            if (layer.IsVideoFile && _viewModel.RowHasOwnSound(layer) && !(_viewModel.ParentRowOf(layer) is not null && CountsAsCutout(_viewModel, layer)))
            {
                offered |= RowSwitches.Muted;
                state |= _viewModel.IsRowSoundMuted(layer) ? RowSwitches.Muted : 0;
            }
        }

        if (track is not null)
        {
            offered |= RowSwitches.Locked;
            state |= track.IsLocked ? RowSwitches.Locked : 0;
        }

        (row.Offered, row.State) = (offered, state);

        // Unlinked clip audio: its own lane in the row's audio stack, each block independently draggable.
        if (BuildRowBlocks(row, scale, seconds) is { } unlinked)
        {
            var link = KeybindManager.GestureText("clip.toggleLink") is { Length: > 0 } keys ? $"{keys} links" : "Toggle A/V Link on its menu links";
            var lane = new TimelineRow
            {
                Key = string.Concat("sound:", layer.TrackId.ToString(CultureInfo.InvariantCulture)),
                Data = layer,
                Kind = TimelineRowKind.Audio,
                Stack = ChildStack.Audio,
                Name = "Sound (unlinked)",
                Height = SoundRowHeight,
                Accent = LayerAudioBrush,
                Offered = RowSwitches.Muted,
                State = layer.IsMuted ? RowSwitches.Muted : RowSwitches.None,
                Tip = $"The sound of this track's clips, unlinked from their pictures: drag it to move it on its own ({link} the selected clip's again).",
            };
            lane.Blocks.AddRange(unlinked);
            lane.Parent = row;
            row.Children.Add(lane);
        }

        return row;
    }


    private TimelineRow CaptionRow(Layer captions, double scale, double seconds)
    {
        var row = new TimelineRow
        {
            Key = CaptionsRowKey,
            Data = captions,
            Kind = TimelineRowKind.Captions,
            Name = "Captions",
            Height = CaptionRowHeight,
            Accent = SubtitleClipBrush,
            Offered = captions.CanHide ? RowSwitches.Hidden : RowSwitches.None,
            State = captions.IsHidden ? RowSwitches.Hidden : RowSwitches.None,
            Tip = "The captions: they follow the sound they were heard in through every cut. Right-click for the Subtitle Studio and their style.",
        };
        BuildRowBlocks(row, scale, seconds);
        return row;
    }

    /// <summary>
    /// Row of the single track family (TimelineLowering.FamilyOf) holding all blur fills, if it is the lowest video family (blur twins
    /// draw under all video, so nesting is only valid with nothing below); null if none, several, or not the lowest.
    /// </summary>
    private TimelineRow? BlurFamilyRow(Dictionary<Guid, TimelineRow> byTrack)
    {
        var document = _viewModel.Document;
        TimelineTrackModel? family = null;
        foreach (var view in _viewModel.Layers)
        {
            if (!_viewModel.IsBlurFilled(view) || _viewModel.ClipOf(view) is not { } clip || document.TrackOf(clip) is not { } track)
                continue;

            var root = Services.Gpu.TimelineLowering.FamilyOf(track, document);
            if (family is not null && !ReferenceEquals(family, root))
                return null;
            family = root;
        }

        if (family is null || document.Tracks.FirstOrDefault(t => t.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch && t.ParentTrackId is null) is not { } lowest
            || !ReferenceEquals(Services.Gpu.TimelineLowering.FamilyOf(lowest, document), family))
        {
            return null;
        }

        return byTrack.TryGetValue(family.Id, out var row) ? row : null;
    }

    private TimelineRow BackgroundRow(Layer background, double scale, double seconds)
    {
        var row = new TimelineRow
        {
            Key = BackgroundRowKey,
            Data = background,
            Kind = TimelineRowKind.Background,
            Name = background.Name,
            Height = BackgroundRowHeight,
            Accent = MainUnderBrush,
            Offered = RowSwitches.Hidden,
            State = background.IsHidden ? RowSwitches.Hidden : RowSwitches.None,
            Tip = "The blur fill: a video's blurred copy under it, wherever it leaves the frame uncovered (each video's own: its menu's Blur Fill). The eye gives every video one, or takes it from every video. Click it for its blur.",
        };
        BuildRowBlocks(row, scale, seconds);
        return row;
    }

    /// <summary>
    /// Keys of a row and all rows in its stack (any depth): the rows that slide with a fold on its track. Excludes the blur fill row,
    /// which always spans the sequence.
    /// </summary>
    private static List<object> RowKeysOf(TimelineRow row)
    {
        var keys = new List<object>();
        Collect(row);
        return keys;

        void Collect(TimelineRow at)
        {
            if (at.Kind != TimelineRowKind.Background)
                keys.Add(at.Key);
            foreach (var child in at.Children)
                Collect(child);
        }
    }

    private static TimelineRow? FindRow(IReadOnlyList<TimelineRow> rows, object key)
    {
        foreach (var row in rows)
        {
            if (Equals(row.Key, key))
                return row;
            if (row.Children.Count > 0 && FindRow(row.Children, key) is { } child)
                return child;
        }

        return null;
    }

    /// <summary>Re-targets change subscriptions to exactly the clip views currently on the timeline.</summary>
    private void WatchRowSources()
    {
        var next = _rowSourcesNext;
        next.Clear();
        foreach (var layer in _viewModel.Layers)
            next.Add(layer);
        foreach (var layer in _viewModel.LayerRows)
            next.Add(layer);

        foreach (var gone in _rowSources)
        {
            if (!next.Contains(gone))
                gone.PropertyChanged -= RowSource_PropertyChanged;
        }

        foreach (var source in next)
        {
            if (!_rowSources.Contains(source))
                source.PropertyChanged += RowSource_PropertyChanged;
        }

        (_rowSources, _rowSourcesNext) = (next, _rowSources);
        _rowSourcesNext.Clear();
    }

    // Clip view property changes that affect rows: strip, waveform, name, mute.
    private void RowSource_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Layer.IsLoading) or nameof(Layer.MediaDuration) or nameof(Layer.Name)
                or nameof(Layer.IsHidden) or nameof(Layer.IsMuted) or nameof(Layer.IsVoice) or nameof(Layer.GainDb) or "AudioGainDb":
                ScheduleClipRedraw();
                break;
        }
    }

    // ----- Surface callbacks -----

    // Header switch: eye, mute, solo (Alt = exclusive), voice, lock; one Undo step each when it edits the timeline.
    private void Timeline_HeaderSwitchToggled(TimelineRow row, RowSwitches which, bool alone)
    {
        switch (row.Data, which)
        {
            case (Layer sound, RowSwitches.Soloed):
                // The command reads Alt itself (exclusive solo).
                _viewModel.ToggleSoloCommand.Execute(sound);
                break;
            case (Layer { IsAudio: true } sound, RowSwitches.Muted):
                _viewModel.ToggleHidden(sound);
                break;
            case (Layer layer, RowSwitches.Hidden):
                _viewModel.ToggleHidden(layer);
                break;
            case (Layer layer, RowSwitches.Muted):
                // Mutes/unmutes all of the track's audio in one Undo step.
                var mute = !_viewModel.IsRowSoundMuted(layer);
                _viewModel.SetTrackSoundMuted(layer, mute);
                _viewModel.StatusText = mute ? $"The sound of {layer.Name} is muted; its picture stays." : $"The sound of {layer.Name} is heard again.";
                break;
            case (Layer layer, RowSwitches.Voice):
                var voice = !layer.IsVoice;
                foreach (var clip in _viewModel.GetTrackClips(layer))
                    clip.IsVoice = voice;
                break;
            case (Layer layer, RowSwitches.Locked):
                _viewModel.SetRowLocked(layer, !_viewModel.IsRowLocked(layer));
                break;
        }

        ScheduleClipRedraw();
    }

    // Context menu for a row, clip, fade handle or header (filled by FillTrackMenu).
    private void Timeline_MenuRequested(TimelineMenuRequest request)
    {
        (object Clip, bool In)? fade = request.FadeIn is { } fadeIn && request.Block?.Clip is { } clip ? (clip, fadeIn) : null;
        FillTrackMenu(request.Menu, request.Row?.Data, request.Block?.Clip, fade, request.OnHeader);
    }

    // Unhandled Menu key press (bound shortcuts run first): open the menu on key-up, matching Windows.
    private bool _menuKeyDown;

    /// <summary>
    /// Makes a window (main or side) open the selected clip's timeline menu on Menu or Shift+F10 (the surface takes no focus). Uses
    /// bubbling key events so the focused control and shortcuts (MainWindow_PreviewKeyDown) run first.
    /// </summary>
    private void HookTimelineMenuKey(Window window)
    {
        window.KeyDown += TimelineMenuKey_KeyDown;
        window.KeyUp += TimelineMenuKey_KeyUp;
    }

    private void TimelineMenuKey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps)
        {
            _menuKeyDown = true;
            return;
        }

        _menuKeyDown = false;
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.F10 && !e.IsRepeat
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.Shift)
        {
            e.Handled = OpenSelectedClipMenu();
        }
    }

    private void TimelineMenuKey_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Apps || !_menuKeyDown)
            return;

        _menuKeyDown = false;
        e.Handled = OpenSelectedClipMenu();
    }

    // Opens the selected clip's menu unless the focused element has its own (text box, drop-down, Media list).
    private bool OpenSelectedClipMenu()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
            return false;

        for (var at = focused; at is not null; at = (at is Visual ? VisualTreeHelper.GetParent(at) : null) ?? LogicalTreeHelper.GetParent(at))
        {
            if (ContextMenuService.GetContextMenu(at) is not null)
                return false;
        }

        return UnifiedTimeline.Surface.OpenSelectedMenu();
    }

    // Seam click: unfold (one Undo step), sliding later content out. Re-resolve the fold by id (the seam may predate an undo that
    // replaced the record).
    private void Timeline_SeamClicked(TimelineRow row, SeamMark seam)
    {
        if (seam.Fold is not FoldedSegment shown)
            return;

        if (_viewModel.Document.Folds?.FirstOrDefault(f => f.Id == shown.Id) is not { } fold)
        {
            _viewModel.StatusText = "That folded silence is no longer on the timeline (an undo or another edit put it back), so nothing was changed.";
            CrashReporter.Note("timeline", "refused: unfold of a fold that is gone");
            ScheduleClipRedraw();
            return;
        }

        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"unfold {fold.Length:0.00} s"));
        AnimatedFoldChange(() => _viewModel.UnfoldSegment(fold));
    }

    // Hide pill click: refold (one Undo step), sliding later content in.
    private void Timeline_RevealHideRequested(TimelineRow row, RevealMark mark)
    {
        if (mark.Restored is not RestoredFold restored)
            return;

        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"hide {mark.To - mark.From:0.00} s of dead air"));
        AnimatedFoldChange(() => _viewModel.HideRestoredStretch(restored));
    }

    /// <summary>
    /// Applies a fold/unfold edit (seam, Hide pill, Restore Folded Silence, Hide / Fold Dead Air Segment) and animates later content
    /// (~120 ms). Model and rows update immediately; only drawing animates. Same-id fold-to-range = opening, range-to-fold = closing;
    /// multi-changes (Restore All) do not animate.
    /// </summary>
    private void AnimatedFoldChange(Action change)
    {
        var seams = new Dictionary<Guid, (Guid TrackId, double At, double Length, bool AllTracks)>();
        foreach (var (trackId, at, fold) in _viewModel.FoldSeams())
            seams.TryAdd(fold.Id, (trackId, at, fold.Length, fold.AllTracks));
        var stretches = new Dictionary<Guid, (double From, double To)>();
        foreach (var (_, from, to, restored) in _viewModel.RestoredStretches())
            stretches.TryAdd(restored.Id, (from, to));

        change();

        (double At, double Length, bool Opening, Guid TrackId, bool AllTracks)? slide = null;
        var count = 0;
        var seamsNow = new HashSet<Guid>();
        foreach (var (trackId, _, fold) in _viewModel.FoldSeams())
        {
            seamsNow.Add(fold.Id);
            if (stretches.TryGetValue(fold.Id, out var was) && !seams.ContainsKey(fold.Id))
            {
                slide = (was.From, was.To - was.From, false, trackId, fold.AllTracks);
                count++;
            }
        }

        var opened = new HashSet<Guid>();
        foreach (var (trackId, from, to, restored) in _viewModel.RestoredStretches())
        {
            if (seams.ContainsKey(restored.Id) && !stretches.ContainsKey(restored.Id) && opened.Add(restored.Id))
            {
                slide = (from, to - from, true, trackId, restored.AllTracks);
                count++;
            }
        }

        // Unfold that left no refoldable range: animate an opening at the seam by its length.
        foreach (var (id, was) in seams)
        {
            if (!seamsNow.Contains(id) && !opened.Contains(id))
            {
                slide = (was.At, was.Length, true, was.TrackId, was.AllTracks);
                count++;
            }
        }

        // Rebuild rows to the final state before starting the slide.
        _rowsStale = true;
        FlushClipRedraw();
        if (count == 1 && slide is { } s && _rowByTrack.TryGetValue(s.TrackId, out var row))
            UnifiedTimeline.Surface.AnimateFold(s.At, s.Length, s.Opening, s.AllTracks ? null : RowKeysOf(row));
    }

    // Drag ended: rebuild rows immediately so the drawing matches the model (edited, refused with a status reason, or failed).
    // Row name click: captions and background rows select as a whole, like clicking their blocks (inspector shows them).
    private void Timeline_HeaderClicked(TimelineRow row)
    {
        if (row.Data is Layer { IsCaptions: true } or Layer { IsBackground: true })
            SelectClip(row.Data);
    }

    // Track of a header-drag row: only real track rows; never captions, blur fill, effect lanes (Data not a Layer) or unlinked-audio
    // lanes (same Layer under another key).
    private static Layer? TrackLayerOf(TimelineRow row) =>
        row.Data is Layer layer && Equals(row.Key, layer.TrackId) && !layer.IsCaptions && !layer.IsBackground ? layer : null;

    // Header released at a seam: move the track (with Sub-Lanes) there, or un-nest it.
    private void Timeline_RowMoveRequested(TimelineRow row, TimelineRow beside, bool above)
    {
        if (TrackLayerOf(row) is not { } layer || TrackLayerOf(beside) is not { } besideLayer)
            return;

        CrashReporter.Note("timeline", above ? "header drag: a track moved above another" : "header drag: a track moved below another");
        _viewModel.MoveTrackToSeam(layer, besideLayer, above);
    }

    // Header released onto another header: nest as its Sub-Lane.
    private void Timeline_RowNestRequested(TimelineRow row, TimelineRow parent)
    {
        if (TrackLayerOf(row) is not { } layer || TrackLayerOf(parent) is not { } parentLayer)
            return;

        CrashReporter.Note("timeline", "header drag: a track made a Sub-Lane");
        _viewModel.NestTrackUnder(layer, parentLayer);
    }

    // Preset badge click: Game Preset list (current one ticked) plus Clear Track Preset.
    private void Timeline_BadgePressed(TimelineRow row, Rect badge)
    {
        if (row.Data is not Layer layer || !_viewModel.CanHaveTrackPreset(layer))
            return;

        var menu = new ContextMenu { PlacementTarget = UnifiedTimeline.Surface, Placement = PlacementMode.Bottom, PlacementRectangle = badge };
        AddTrackPresetItems(menu, layer, flat: true);
        TidySeparators(menu, 0);
        menu.IsOpen = true;
    }

    // Rubber band release: touched clips replace (or with Ctrl/Shift extend) the selection; the last becomes primary. Captions, blur fill
    // and effect lanes are excluded.
    private void Timeline_MarqueeSelected(IReadOnlyList<TimelineBlock> blocks, bool add)
    {
        var touched = blocks.Select(b => b.Clip switch { Layer clip => clip, LayerSound sound => sound.Layer, _ => null })
            .OfType<Layer>().Where(_viewModel.CanGroup).Distinct().ToList();
        if (touched.Count == 0)
        {
            if (!add)
                ClearClipSelection();
            UnifiedTimeline.Surface.Redraw();
            return;
        }

        var group = add ? SelectedGroup().Concat(touched).Distinct().ToList() : touched;
        _rangeAnchor = null;
        _viewModel.SetClipSelection(group);
        SelectClip(touched[^1], keepGroup: true);
        _viewModel.StatusText = group.Count == 1 ? $"{group[0].Name} selected." : $"{group.Count} clips selected: Ctrl+C copies them, Ctrl+D duplicates them, Delete deletes them.";
        CrashReporter.Note("timeline", $"select: a rubber band of {group.Count} clips");
    }

    private void Timeline_DragEnded()
    {
        // Any deferred click was already raised before drag end; clear it so a moved drag cannot fire it later.
        _clickGesture = null;
        _rowsStale = true;
        FlushClipRedraw();
        TakeOwedZoom();
    }

    // Timeline replaced (undo, redo, project load): cancel any active drag, re-resolve the selection by clip id (cleared if gone),
    // and rebuild rows once.
    private void Timeline_Restored()
    {
        var surface = UnifiedTimeline.Surface;
        if (surface.IsDragging)
        {
            surface.AbortDrag();
            CrashReporter.Note("timeline", "drag let go: the timeline was put back under it");
        }

        _selectedClip = _selectedClip switch
        {
            Layer { ClipId: var id } when id != Guid.Empty => ViewOfClip(id),
            LayerSound { Layer.ClipId: var id } when id != Guid.Empty => ViewOfClip(id) is { } view ? new LayerSound(view) : null,
            _ => _selectedClip,
        };
        if (_selectedClip is null)
            _playheadVideoSelected = false;

        // Re-resolve multi-selection and the range anchor the same way.
        _viewModel.RemapClipSelection();
        _rangeAnchor = _rangeAnchor is { ClipId: var anchorId } && anchorId != Guid.Empty ? ViewOfClip(anchorId) : null;
        _clickGesture = null;

        ScheduleClipRedraw();
    }

    // Current clip view by id; null if not on the timeline.
    private Layer? ViewOfClip(Guid id) => _viewModel.Document.FindClip(id) is { } clip ? _viewModel.ViewOf(clip) : null;

    // Gap pill click: ripple-close on the row's track (all unlocked tracks with Ripple All Tracks).
    private void Timeline_CloseGapRequested(TimelineRow row, double from, double to)
    {
        if (row.Data is Layer track)
            _viewModel.CloseGapOn(track, from, to);
    }

    // Alt+click: razor at that time on the clip re-resolved by id.
    private void Timeline_RazorRequested(TimelineRow row, TimelineBlock block, double seconds)
    {
        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"split (Alt+click) at {seconds:0.000} s"));
        switch (block.Clip)
        {
            case Layer layer when LiveClip(layer) is { } live:
                _viewModel.SplitLayer(live, seconds);
                break;
            case LayerSound sound when LiveClip(sound.Layer) is { } live:
                _viewModel.SplitLayer(live, seconds);
                break;

            // Caption block: split at the word under the click.
            case SubtitleRef:
                SplitCaptionAt(seconds);
                return;
            default:
                ScheduleClipRedraw();
                return;
        }

        ClearClipSelection();
        ScheduleClipRedraw();
    }

    // ----- Tools -----

    /// <summary>
    /// Cutout Studio pick from the Timeline: the selected video clip, else null (the video at the playhead); the Studio and the heat
    /// lamp both resolve its base clip (<see cref="MainViewModel.CutoutStudioBase"/>).
    /// </summary>
    private Layer? CutoutTarget() => _selectedClip is Layer { IsVideoFile: true } clip ? clip : null;

    /// <summary>Opens the Cutout Studio on the picked clip's base video with all its Cutouts (Cutouts and Game Presets).</summary>
    private void OpenCutoutStudio()
    {
        if (_viewModel.HasSource)
            new MaskDrawWindow(_viewModel, null, CutoutOpening(CutoutTarget())) { Owner = this }.ShowDialog();
    }

    private MaskDrawWindow.Opening CutoutOpening(Layer? picked) => new(picked, _heatLamp?.TakeWarmCutoutStill(), FilmstripFrameAt);

    /// <summary>
    /// The Timeline filmstrip's frame of <paramref name="file"/> nearest a file time: the Cutout Studio's frame-0 picture while its
    /// still decodes. Null before the filmstrip or the file's probe exists.
    /// </summary>
    private ImageSource? FilmstripFrameAt(string file, double seconds)
    {
        if (_viewModel.ClipPictures.Film(file) is not System.Windows.Media.Imaging.BitmapSource strip
            || MediaProbe.KnownInfo(file) is not { Video: { Width: > 0, Height: > 0 } video } info
            || FramesOf(strip, (double)video.Width / video.Height) is not { Length: > 0 } frames)
        {
            return null;
        }

        var each = info.DurationSeconds > 0.05 ? info.DurationSeconds / frames.Length : double.PositiveInfinity;
        return frames[Math.Clamp((int)(seconds / each), 0, frames.Length - 1)];
    }

    /// <summary>
    /// "Open Cutout &amp; Mask Studio..." for one video clip: starts from its drawn shape; Apply to This Clip stores the drawn outlines
    /// (no image file) as its mask in one Undo step, like Draw Custom Shape. Cutouts and Game Presets remain available.
    /// </summary>
    private void OpenCutoutStudio(Layer clip)
    {
        _viewModel.Checkpoint($"change the mask of {clip.Name}");
        var visit = _viewModel.UndoMark;
        var window = new MaskDrawWindow(_viewModel, clip, CutoutOpening(clip)) { Owner = this };
        if (window.ShowDialog() == true && MaskStore.DrawnKey(window.Shapes).Length > 0)
        {
            (clip.CustomMask, clip.MaskPath, clip.MaskFrame, clip.MaskShapes) = (true, "", true, window.Shapes);
            if (window.ForAllLayers)
            {
                var count = _viewModel.ShareFrameMask(clip);
                _viewModel.StatusText = count == 0
                    ? "There are no other Cutouts of the video to give the mask to."
                    : $"The mask is now on {count + 1} Cutouts of the video: each takes its own part of it.";
            }
        }

        _viewModel.DropStepIfUnchanged(visit);
    }

    /// <summary>
    /// Dispatches every timeline pane button by Tag (title row, corners, inspector, bin, Add Layer kinds); faults become a status line
    /// and a log entry, never an unhandled exception.
    /// </summary>
    private void TimelineTool_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        RunGuarded(tag ?? "a timeline tool", () => RunTimelineTool(tag, sender, e));
    }

    private void RunTimelineTool(string? tag, object sender, RoutedEventArgs e)
    {
        switch (tag)
        {
            case "Split": SplitSelectedClip(); break;
            case "TrimStart": TrimSelectedClip(start: true); break;
            case "TrimEnd": TrimSelectedClip(start: false); break;
            case "Delete": DeleteSelectedClip(); break;
            case "Undo": Undo_Click(sender, e); break;
            case "Redo": Redo_Click(sender, e); break;
            case "LinkAudio": LinkAudio_Click(sender, e); break;
            case "Timecode": ManualCut_Click(sender, e); break;
            case "DeadAir": RemoveDeadAir_Click(sender, e); break;
            case "CutoutStudio": OpenCutoutStudio(); break;
            case "PreviousKey": PreviousKeyframe_Click(sender, e); break;
            case "ToggleKey": ToggleKeyframe_Click(sender, e); break;
            case "NextKey": NextKeyframe_Click(sender, e); break;
            case "ZoomFit": FitTimeline(); break;
            case "PlayPause": PlayPause_Click(sender, e); break;

            // Gear beside Snap: Timeline Settings menu (same as the Timeline tab's context menu).
            case "TimelineSettings": OpenTimelineSettings(sender as FrameworkElement); break;

            // Creator Hotbar slots.
            case "Hotbar0" or "Hotbar1" or "Hotbar2" or "Hotbar3" or "Hotbar4": ApplyHotbar(tag[^1] - '0'); break;

            // Inspector buttons act on their group's DataContext.
            case "Properties" when sender is FrameworkElement { DataContext: Layer element }: ShowInEffectsPane(element); break;
            case "BlurSettings": ShowBlurFillSettings(); break;
            case "TrackFilters": TrackFilters_Click(sender, e); break;

            // Recycle bin and Text Settings colour buttons.
            case "RestoreDeleted": RestoreDeleted_Click(sender, e); break;
            case "EmptyBin": EmptyBin_Click(sender, e); break;
            case "Font" or "Outline": TextColor_Click(sender, e); break;

            // Add Track kinds (the flyout already closed).
            case "builtin.vfx.explosion" or "builtin.vfx.sparks" or "builtin.vfx.animelines" or "builtin.vfx.shield" or "builtin.vfx.saber" or "builtin.gpu.shockwave":
            case "Empty" or "Video" or "Image" or "Text" or "Adjustment" or "Region" or "MaskLayers" or "Audio" or "Voiceover"
                or "Captions" or "ImportSubtitles" or "EditCaptions" or "CaptionStyle":
                AddLayerMenu_Click(sender, e);
                break;
        }
    }

    /// <summary>
    /// U / Toggle A/V Link: links or unlinks the selected clip's audio (one Undo step); unlinked audio gets an expanded lane in its row's
    /// stack. For main-video clips (audio = file streams) this acts as the chain button across all audible clips.
    /// </summary>
    internal void ToggleSelectedClipLink()
    {
        var clip = _selectedClip switch
        {
            LayerSound sound when _viewModel.Layers.Contains(sound.Layer) => sound.Layer,
            Layer { IsBackground: false } layer when _viewModel.Layers.Contains(layer) => layer,
            _ => null,
        };
        if (clip is null)
        {
            var key = KeybindManager.GestureText("clip.toggleLink") is { Length: > 0 } keys ? keys : "Toggle A/V Link";
            _viewModel.StatusText = $"Select a clip with sound first: {key} links or unlinks its sound alone (the chain button among the timeline's tools does every clip).";
            return;
        }

        var wasLinked = _viewModel.IsClipAudioLinked(clip);
        if (!_viewModel.ToggleClipAudioLink(clip))
            return;

        // Expand that row (keyed by track number) after the next rebuild.
        if (wasLinked)
            _expandRowKey = clip.TrackId;
        ScheduleClipRedraw();
    }

    // ----- Inspector -----

    /// <summary>Binds the inspector to the selection: video position/size, audio gain/mute, or background blur.</summary>
    private void UpdateTimelineInspector()
    {
        object? picture = null, sound = null, background = null;
        var name = "";
        switch (_selectedClip)
        {
            case Layer { IsBackground: true } layer:
                (background, name) = (_viewModel, layer.Name);
                break;

            // Captions: box position/size, Properties (position, look) and Caption Style.
            case Layer { IsCaptions: true } captions:
                (picture, name) = (captions, "Captions");
                break;
            case Layer layer when _viewModel.Layers.Contains(layer):
                name = layer.Name;
                if (layer.IsAudio)
                {
                    sound = layer;
                }
                else
                {
                    picture = layer;
                    if (layer.CarriesSound)
                        sound = layer;
                }

                break;
            case LayerSound clipSound when _viewModel.Layers.Contains(clipSound.Layer):
                (sound, name) = (clipSound.Layer, $"{clipSound.Layer.Name}: sound");
                break;
        }

        // Multi-selection: edits the primary clip only and shows how many others are selected.
        if (GroupHolds && _viewModel.SelectedClips.Count > 1)
            name = string.Create(CultureInfo.InvariantCulture, $"{name} (+{_viewModel.SelectedClips.Count - 1} selected)");

        UnifiedTimeline.Inspect(name, picture, sound, background);
    }
}
