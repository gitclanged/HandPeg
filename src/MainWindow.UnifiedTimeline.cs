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

// ----- The timeline pane (4.4): its rows, its tools, its inspector, and what its surface asks for -----
// The pane (Views/UnifiedTimelinePane) is one drawn surface of every track with its ruler, scroll bars and tools around it.
// This side makes its rows from what is open (the view model's LayerRows and AudioRows, the document's folds, the captions'
// anchor), gives them to the surface at the view's scale, and does what the surface and the tools ask: select, seek, edit
// through the view model's verbs, open a track's menu (FillTrackMenu), put a fold back, close a gap, split at a click.
// The selection (_selectedClip) is the window's, shared with the Canvas, the Keyframes pane and the playback bar.
public partial class MainWindow
{
    // How tall rows are: pictures and sounds by the one Track Height (1 to 50, LayerTrackHeight: the pane's height pill and
    // slider, Alt + the wheel over the tracks, Shift + a row's lower edge dragged in the headers, and its Pane Settings), a
    // picture's from 32 to 120 px (5.1-Polish, the user's directive), a sound's a little lower; a track's row can have a
    // height of its own (its lower edge dragged, TimelineTrackModel.Height, the user's Q2); the captions and the background
    // are thin, having little to show.
    private const double CaptionRowHeight = 30;
    private const double BackgroundRowHeight = 26;
    private const double LowestPictureRow = 32, TallestPictureRow = 120, LowestSoundRow = 28, TallestSoundRow = 92;

    /// <summary>The Track Height HandPeg starts with (a double-click on a row's lower edge, or on the height pill, puts it back).</summary>
    private const double UsualTrackHeight = 25;

    // How many steps of the Track Height one notch of Alt + the wheel is, and the setting when a row's edge drag began.
    private const int TrackHeightStepsPerNotch = 2;
    private double _trackHeightAtDrag;

    // A track's row being sized by its lower edge: its key and the height it has until the drag is let go of (the edit).
    private (object Key, double Height)? _rowHeightDrag;

    private static readonly object CaptionsRowKey = "captions";
    private static readonly object BackgroundRowKey = "background";

    private double PictureRowHeight => RowHeightAt(LowestPictureRow, TallestPictureRow);

    private double SoundRowHeight => RowHeightAt(LowestSoundRow, TallestSoundRow);

    private double RowHeightAt(double lowest, double tallest) => Math.Round(lowest + (Math.Clamp(_viewModel.LayerTrackHeight, 1, 50) - 1) * (tallest - lowest) / 49);

    /// <summary>How tall a track's row is: what its lower edge is being dragged to, its own height, or the one Track Height.</summary>
    private double RowHeightOf(object key, TimelineTrackModel? track, TimelineRowKind kind)
    {
        var (lowest, tallest, shared) = kind == TimelineRowKind.Audio
            ? (LowestSoundRow, TallestSoundRow, SoundRowHeight)
            : (LowestPictureRow, TallestPictureRow, PictureRowHeight);
        var own = _rowHeightDrag is { } drag && Equals(drag.Key, key) ? drag.Height : track?.Height;
        return own is { } height ? Math.Round(Math.Clamp(height, lowest, tallest)) : shared;
    }

    /// <summary>The track of a row its own height is kept on (the row of a track, never a lane of its stack that shows the same layer).</summary>
    private TimelineTrackModel? HeightTrackOf(TimelineRow row) =>
        row.Data is Layer layer && Equals(row.Key, layer.TrackId) ? _viewModel.TrackOfRow(layer) : null;

    /// <summary>Steps of the Track Height a picture row's height changes by for so many DIPs.</summary>
    private static double TrackHeightSteps(double pixels) => pixels * 49 / (TallestPictureRow - LowestPictureRow);

    // Rows are being made (a change of scale while they are is not another reason to make them).
    private bool _rebuildingRows;

    // A row whose sound's stack opens once the rows are made again: the row of a clip whose sound was just unlinked, so its
    // lane can be dragged.
    private object? _expandRowKey;

    // The rows of the tracks as last made, by the track's id: which rows slide with a fold (a track's row and those in its stack).
    private Dictionary<Guid, TimelineRow> _rowByTrack = [];

    // The clips' views and the audio streams whose changes (a strip or a waveform arriving, a name, a mute) the rows show:
    // watched while they are on the timeline. Two sets, swapped, so that watching costs nothing new each time.
    private HashSet<INotifyPropertyChanged> _rowSources = [];
    private HashSet<INotifyPropertyChanged> _rowSourcesNext = [];

    /// <summary>
    /// Hooks the timeline pane up (called once, from the constructor): its surface's events and questions (what is
    /// selected, what to snap to, the frame rate), its ruler, scroll bar and zoom to the shared view, its tools. Nothing is
    /// made here: the surface has rows once there is something open.
    /// </summary>
    private void InitializeUnifiedTimeline()
    {
        var pane = UnifiedTimeline;
        var surface = pane.Surface;
        pane.ToolClick += TimelineTool_Click;

        surface.IsSelected = IsSelected;
        surface.SnapTimes = SnapTimes;
        surface.FrameRate = () => _viewModel.SourceFrameRate;
        surface.BlockPressed += Timeline_BlockPressed;
        surface.EmptyPressed += Timeline_EmptyPressed;
        surface.DragRefused += Timeline_DragRefused;

        // 4.4 BugHunt: the rows owed are laid out before a press is looked at, and at once when a drag ends (what it drew
        // moved is the timeline again, edited, refused or failed); a fault in an edit the surface asked for is a status line.
        surface.PressStarting += FlushClipRedraw;
        surface.DragEnded += Timeline_DragEnded;
        surface.CallbackFailed += ex => TimelineFault("an edit on the timeline", ex);
        surface.RevealHideRequested += Timeline_RevealHideRequested;
        _viewModel.TimelineRestored += Timeline_Restored;

        surface.ZoomRequested += (factor, x) => ZoomTimeline(factor, surface.TimeAreaWidth > 0 ? x / surface.TimeAreaWidth : 0.5);
        surface.PanRequested += pixels => PanTimeline(surface.PixelsPerSecond > 0 ? pixels / surface.PixelsPerSecond : 0);
        // A change of width (a splitter or the window dragged) lays the rows out again once the moves pause, not at every one.
        surface.ScaleChanged += ScheduleClipRedraw;
        surface.HeaderSwitchToggled += Timeline_HeaderSwitchToggled;
        surface.MenuRequested += Timeline_MenuRequested;
        surface.SeamClicked += Timeline_SeamClicked;
        surface.CloseGapRequested += Timeline_CloseGapRequested;
        surface.RazorRequested += Timeline_RazorRequested;

        // The tracks' height from the surface (5.1-Polish): Alt + the wheel, and a row's lower edge dragged or double-clicked.
        surface.TrackHeightStepped += notches => _viewModel.LayerTrackHeight = Math.Clamp(_viewModel.LayerTrackHeight + notches * TrackHeightStepsPerNotch, 1, 50);
        surface.TrackHeightDragStarted += () => _trackHeightAtDrag = _viewModel.LayerTrackHeight;
        surface.TrackHeightDragged += pixels => _viewModel.LayerTrackHeight = Math.Clamp(_trackHeightAtDrag + TrackHeightSteps(pixels), 1, 50);
        surface.TrackHeightReset += () => _viewModel.LayerTrackHeight = UsualTrackHeight;

        // One track's height (5.1-AutoHud-Part1, the user's Q2): drawn as it is dragged, one step of Undo when it is let go of.
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

        // 5.0: clips selected together (a click let go decides what a Ctrl / Shift press on a selected clip does; a group's
        // drag is one step of Undo), and the moment under the pointer over the ruler told as the rows' is (Instant Seek).
        surface.BlockClicked += Timeline_BlockClicked;
        surface.HeaderClicked += Timeline_HeaderClicked;

        // 5.2A: a track's preset badge drops the Game Presets; a rubber band over empty space selects what it touches.
        surface.BadgePressed += Timeline_BadgePressed;
        surface.MarqueeSelected += Timeline_MarqueeSelected;
        surface.Together = _viewModel.EditTogether;
        _viewModel.ClipSelectionChanged += ClipGroup_Changed;
        pane.Ruler.PointerSecondsChanged += surface.NotePointerOverRuler;
        RegisterTimelineChrome(pane.Ruler, pane.TimeScroll, pane.ZoomSlider);
        HookTimelineMenuKey(this);

        _viewModel.PropertyChanged += UnifiedTimeline_ViewModelChanged;
        _viewModel.LayerRows.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.AudioRows.CollectionChanged += (_, _) => ScheduleClipRedraw();

        // Play / Pause in the pane's corner while neither the playback bar's nor the Sequence Overview Scrubber's is on screen.
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

    // ----- The view on the surface -----

    /// <summary>Gives the surface the shared view (a minute's room while nothing is open, as the ruler shows); its scale follows from it.</summary>
    private void ShowViewOnSurface()
    {
        ClampView();
        var span = ViewSpan;
        UnifiedTimeline.Surface.SetView(_viewStart, span > 0 ? span : EmptyRulerSeconds, _viewModel.DurationMs / 1000);
    }

    /// <summary>
    /// Shows the view's change on the surface: a pan slides what is drawn; a zoom changes the scale, and the rows are laid
    /// out again for it (never under a drag, which goes on at the scale it began at).
    /// </summary>
    private void ShowTimelineView()
    {
        var surface = UnifiedTimeline.Surface;
        var scale = surface.PixelsPerSecond;
        ShowViewOnSurface();
        if (surface.PixelsPerSecond != scale && !surface.IsDragging)
            RebuildTimelineRows();
    }

    // ----- The rows -----

    /// <summary>
    /// Makes the timeline's rows again from what is open, at the surface's scale, and gives them to it (it keeps which
    /// rows are open by their keys). Never under a drag: the drag's end asks again.
    /// </summary>
    private void RebuildTimelineRows()
    {
        var surface = UnifiedTimeline.Surface;
        if (_rebuildingRows || surface.IsDragging)
            return;

        _rebuildingRows = true;

        // What is made now is what the timeline is: anything that changes while it is made asks again.
        _rowsStale = false;
        try
        {
            ShowViewOnSurface();
            surface.SetRows(BuildTimelineRows(surface.PixelsPerSecond));
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
    /// The timeline's rows, top to bottom, with their blocks laid out at <paramref name="scale"/> pixels a second. Each
    /// track's row in the stacking order (the front first: LayerRows), its children (the tracks made to go with it, a Game
    /// Preset's HUD Cutouts; its clips' unlinked sound; its streams' Sub-Lanes) in its mini-stack; the captions in the stack of the
    /// track whose sound they were heard in (else on top); the blur fill's settings row under the pictures; the sound clips'
    /// tracks last. Each row is known by a key that an undo leaves as it was (Layer.TrackId, "captions", "background"), so a
    /// row that was open stays open.
    /// </summary>
    private List<TimelineRow> BuildTimelineRows(double scale)
    {
        var rows = new List<TimelineRow>();
        var seconds = _viewModel.DurationMs / 1000;
        if (_viewModel.Layers.Count == 0 && seconds <= 0)
            return rows;

        // The tracks' rows: pictures in the stacking order, then the sound clips' tracks (a video clip's sound is on its
        // own clip's row, or its lane while unlinked).
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

        // (5.2-AuditB: a file's streams are Sub-Lanes of sound clips, rows of their own in their video track's stack; the output
        // audio tracks are the Audio tab's, no rows of the timeline.)
        var sounds = new List<(Layer Layer, TimelineRow Row)>();
        foreach (var item in _viewModel.AudioRows)
        {
            if (item is Layer { IsAudio: true } sound)
                sounds.Add((sound, TrackRow(sound, scale, seconds)));
        }

        // Captions made or imported while their layer is not listed are shown all the same.
        if (captionRow is null && seconds > 0 && _viewModel.GetSubtitleSegments().Count > 0)
            captionRow = CaptionRow(_viewModel.CaptionLayer, scale, seconds);

        // A track made to go with another (its ParentTrackId) is in that one's mini-stack.
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

        // Each picture track's effects (5.2): its clips' effects as blocks on lanes of its own stack, among its Sub-Lanes (EffectRows).
        foreach (var (layer, row) in pictures)
        {
            foreach (var lane in EffectRows(layer, scale))
                Adopt(row, lane);
        }

        // The captions follow the sound they were heard in: in the stack of its track, among its layers (4.4 BugHunt: beside
        // the HUD cut-outs and masks, never among its sound's lanes).
        // Strict order (the user's directive): that is where they are drawn, right over that track; drawn anywhere else, their row is
        // where they are drawn, under the track over them, or at the very top over everything.
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

        // The blur fill's settings (5.2-AuditB: each video's own blur fill, this row their look) under the pictures: a row of
        // the view only, the sequence's setting, no clip, and nothing links it.
        rows.AddRange(pictures.Where(p => p.Row.Parent is null).Select(p => p.Row));
        if (overCaptions is not null)
        {
            var top = overCaptions;
            while (top.Parent is { } up)
                top = up;
            rows.Insert(rows.IndexOf(top) + 1, captionRow!);
        }

        // Where the Background Blur is drawn (5.2.0's re-cut, the user's P0): under one clip family's pictures, its row the last of that
        // family's stack, under its Cutouts; under several families', the last of the picture rows, under them all.
        if (backgroundRow is { Parent: null } && BlurFamilyRow(byTrack) is { } family)
            Adopt(family, backgroundRow);
        if (backgroundRow is { Parent: null })
            rows.Add(backgroundRow);
        rows.AddRange(sounds.Where(s => s.Row.Parent is null).Select(s => s.Row));

        // Each row's stack in its two parts, each opened by itself: its layers (the captions first, as they are drawn in
        // front of everything, then the tracks made to go with it, and last the blur fill's settings row under them all) and under
        // them its sound's lanes, a video track's stream lanes by stream (5.2: "Stream 1", "Stream 2"...).
        OrderStacks(rows, row => row.Data is Layer lane && _viewModel.StreamOrdinalOf(lane) is { } ordinal ? ordinal : int.MaxValue);
        MarkInFront(rows);
        _rowByTrack = byTrack;

        // The seams of folded dead air, on the row of the track each was folded at, with the rows that slide with each
        // (that track's and its stack's; every unlocked row for a fold made on every track).
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

        // The stretches of folded dead air that were put back (4.4 BugHunt): each shaded on its row, with its Hide pill.
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

        // A row taken into another's stack: among its layers, or (a sound: an audio track, a stream's lane) among its sound's lanes.
        static void Adopt(TimelineRow parent, TimelineRow child)
        {
            child.Parent = parent;
            child.Stack = child.Kind == TimelineRowKind.Audio ? ChildStack.Audio : ChildStack.Visual;
            parent.Children.Add(child);
        }

        // Every stack laid as its two parts, in the order they were taken in within each (a stable order); among the layers the
        /// captions first and the blur fill's settings row last, among the sound's lanes the streams by their place in the file.
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

        // Whether a row is the other or lies (at any depth) in its stack: it cannot then be put in that one's stack.
        static bool Within(TimelineRow row, TimelineRow other)
        {
            for (var at = row; at is not null; at = at.Parent)
            {
                if (ReferenceEquals(at, other))
                    return true;
            }

            return false;
        }

        // Which children of each stack of layers are drawn in front of their parent, by the very order the compositor draws
        // in (5.0-Release, the user's directive 4): a track over its parent's in the stacking order (a Cutout over the
        // video; one moved behind it, in the 3D view or by Send Backward, is under it), the captions over everything, the
        /// blur fill's settings row under everything. Open, the surface lays those in front above the parent's row.
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

        // A track's row's place in the stacking order (Document.Tracks, bottom first); -1 for a row that is no track's.
        int StackPlaceOf(TimelineRow row) =>
            row.Data is Layer layer && _viewModel.TrackOfRow(layer) is { } track ? _viewModel.Document.Tracks.IndexOf(track) : -1;
    }

    /// <summary>A track's row (a picture's, a video's, an empty one, a sound clip's), with its clips and, while any of them is unlinked from its sound, that sound's lane in its stack.</summary>
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
            Tip = layer.IsEmptyTrack ? "An empty track: drop videos and pictures onto it." : null,

            // A gap's pill closes it on this track: never on a locked one (an unlinked sound's lane, the captions and the
            // blur fill's settings row have none either).
            ClosesGaps = track is { IsLocked: false },

            // Its Track Preset (5.2A): the badge in its header, on a picture track of its own (an empty one too: set before anything is dropped).
            Badge = _viewModel.TrackPresetOf(layer),
            OffersBadge = _viewModel.CanHaveTrackPreset(layer),
        };

        // Its header's switches: the eye (a sound clip's track is muted instead), its sound's mute, the solo (5.2-AuditB: the
        // track's own, the document's: Alt plays it alone), the voice, the lock.
        var (offered, state) = (RowSwitches.None, RowSwitches.None);
        if (layer.IsAudio)
        {
            offered |= RowSwitches.Muted | RowSwitches.Voice | (track is not null ? RowSwitches.Soloed : 0);
            state |= (layer.IsHidden ? RowSwitches.Muted : 0) | (layer.IsVoice ? RowSwitches.Voice : 0) | (track is { IsSoloed: true } ? RowSwitches.Soloed : 0);
        }
        else
        {
            // The eye is the track's own switch (5.2: its whole family's), not its first clip's.
            if (layer.CanHide)
            {
                offered |= RowSwitches.Hidden;
                state |= _viewModel.IsTrackHidden(layer) ? RowSwitches.Hidden : 0;
            }

            // The master mute: on every video track's header, all of its sound at once (the user's directive).
            if (layer.IsVideoFile)
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

        // The clips' sound that is unlinked from their pictures: a lane of its own in the row's sound stack, each block
        // dragged by itself.
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
            Tip = "The captions: they follow the sound they were heard in through every cut. Right-click for the Subtitle Editor and their style.",
        };
        BuildRowBlocks(row, scale, seconds);
        return row;
    }

    /// <summary>
    /// The row of the one track family (a track and the Sub-Lanes made to go with it, TimelineLowering.FamilyOf) whose clips have every
    /// blur fill there is; null when none has one, or several do.
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

        return family is not null && byTrack.TryGetValue(family.Id, out var row) ? row : null;
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
    /// The keys of a row and of every row in its stack, at any depth: what slides with a fold made on its track. Never the
    /// blur fill's settings row's: it lasts as long as the sequence, whatever is folded.
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

    /// <summary>Watches the clips' views that are on the timeline now (and no others) for what the rows show of them.</summary>
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

    // What arrives or changes on a clip that its row shows: its strip of frames, its sound, its name, a mute.
    private void RowSource_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Layer.Filmstrip) or nameof(Layer.Waveform) or nameof(Layer.IsLoading) or nameof(Layer.MediaDuration) or nameof(Layer.Name)
                or nameof(Layer.IsHidden) or nameof(Layer.IsMuted) or nameof(Layer.IsVoice) or nameof(Layer.GainDb) or "AudioGainDb":
                ScheduleClipRedraw();
                break;
        }
    }

    // ----- What the surface asks for -----

    // A switch on a row's header: the eye, a mute, the solo (Alt: this one alone), the voice, the lock. One step of Undo each
    // where it is an edit of the timeline.
    private void Timeline_HeaderSwitchToggled(TimelineRow row, RowSwitches which, bool alone)
    {
        switch (row.Data, which)
        {
            case (Layer sound, RowSwitches.Soloed):
                // The command reads Alt itself: held, the track is played alone.
                _viewModel.ToggleSoloCommand.Execute(sound);
                break;
            case (Layer { IsAudio: true } sound, RowSwitches.Muted):
                _viewModel.ToggleHidden(sound);
                break;
            case (Layer layer, RowSwitches.Hidden):
                _viewModel.ToggleHidden(layer);
                break;
            case (Layer layer, RowSwitches.Muted):
                // All the track is heard by at once, in one step of Undo.
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

    // A right-click: the menu of the row, the clip, its fade handle or the header (P10's FillTrackMenu fills it).
    private void Timeline_MenuRequested(TimelineMenuRequest request)
    {
        (object Clip, bool In)? fade = request.FadeIn is { } fadeIn && request.Block?.Clip is { } clip ? (clip, fadeIn) : null;
        FillTrackMenu(request.Menu, request.Row?.Data, request.Block?.Clip, fade, request.OnHeader);
    }

    // The Menu key went down and nothing took it (a shortcut bound to it is run first): its menu opens as it is let go, as
    // Windows opens one.
    private bool _menuKeyDown;

    /// <summary>
    /// Has a window answer the Menu key and Shift+F10 with the selected clip's menu on the timeline, as a right-click on it
    /// would (the surface takes no keyboard focus, where the old Layers list's rows did): this window and the side window.
    /// The bubbling key events, so the control with the keyboard and the shortcuts (MainWindow_PreviewKeyDown) come first.
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

    // The selected clip's menu on the timeline, unless what has the keyboard has a menu of its own (a text box, a drop-down,
    // the Media pane's list), which the key is left to open.
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

    // A seam of folded dead air clicked: what was folded there is put back (one step of Undo), what follows sliding out to
    // make room. The fold is the timeline's own, found by its id (the seam may have been drawn from the timeline before an
    // undo, whose copy of it is another record).
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

    // A stretch put back's Hide pill clicked: folded out again (one step of Undo), what follows sliding in over it.
    private void Timeline_RevealHideRequested(TimelineRow row, RevealMark mark)
    {
        if (mark.Restored is not RestoredFold restored)
            return;

        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"hide {mark.To - mark.From:0.00} s of dead air"));
        AnimatedFoldChange(() => _viewModel.HideRestoredStretch(restored));
    }

    /// <summary>
    /// Makes a change that folds dead air out or puts it back (a seam clicked, a Hide pill, Restore Folded Silence or Hide /
    /// Fold Dead Air Segment on a clip's menu), then slides what follows into place over about 120 ms (4.4 BugHunt). The
    /// model changes at once and the rows are made again from it at once; only the drawing moves. A fold that became a
    /// stretch put back (the same id) is an opening, a stretch that became a fold again a closing; several at once (Restore
    /// All) slide nothing.
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

        // A fold put back that left no stretch to fold again: it opens where its seam was, by its length.
        foreach (var (id, was) in seams)
        {
            if (!seamsNow.Contains(id) && !opened.Contains(id))
            {
                slide = (was.At, was.Length, true, was.TrackId, was.AllTracks);
                count++;
            }
        }

        // The rows as the timeline is now, before anything slides on them.
        _rowsStale = true;
        FlushClipRedraw();
        if (count == 1 && slide is { } s && _rowByTrack.TryGetValue(s.TrackId, out var row))
            UnifiedTimeline.Surface.AnimateFold(s.At, s.Length, s.Opening, s.AllTracks ? null : RowKeysOf(row));
    }

    // A drag on the timeline ended (4.4 BugHunt): the rows are laid out again from the timeline at once, so what it drew
    // moved is what the timeline is (the edit made, refused with the model's reason in the status bar, or failed).
    // A row's name clicked (5.0-Verify): the captions' and the background's rows are one thing each, selected by it as a
    // click on their blocks selects them (their place, size and style, or the blur, in the inspector).
    private void Timeline_HeaderClicked(TimelineRow row)
    {
        if (row.Data is Layer { IsCaptions: true } or Layer { IsBackground: true })
            SelectClip(row.Data);
    }

    // A track's preset badge clicked (5.2A): its Game Presets drop from it, the track's own ticked, and Clear Track Preset under them.
    private void Timeline_BadgePressed(TimelineRow row, Rect badge)
    {
        if (row.Data is not Layer layer || !_viewModel.CanHaveTrackPreset(layer))
            return;

        var menu = new ContextMenu { PlacementTarget = UnifiedTimeline.Surface, Placement = PlacementMode.Bottom, PlacementRectangle = badge };
        AddTrackPresetItems(menu, layer, flat: true);
        TidySeparators(menu, 0);
        menu.IsOpen = true;
    }

    // A rubber band let go over the rows (5.2A): the clips it touched are the selection, or join it with Ctrl or Shift; the last one is the one the
    // panes show. Captions, the blur fill's row and an effect's lane are not clips to select together.
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
        // What the press left to its click is decided by now (a click was told before the drag's end): a drag that moved
        // leaves none to fire on a later click (5.0-Verify).
        _clickGesture = null;
        _rowsStale = true;
        FlushClipRedraw();
        TakeOwedZoom();
    }

    // An undo, a redo or a project put a timeline in place (4.4 BugHunt): a drag still held is let go (nothing it drew is
    // committed onto the timeline that replaced it), the selection follows its clip into the views made afresh (by its id;
    // gone with it, nothing is selected), and the rows are made again once.
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

        // Clips selected together follow theirs the same way (5.0); where the range began, too.
        _viewModel.RemapClipSelection();
        _rangeAnchor = _rangeAnchor is { ClipId: var anchorId } && anchorId != Guid.Empty ? ViewOfClip(anchorId) : null;
        _clickGesture = null;

        ScheduleClipRedraw();
    }

    // The view of a clip on the timeline now, by its id; null when it is not on it.
    private Layer? ViewOfClip(Guid id) => _viewModel.Document.FindClip(id) is { } clip ? _viewModel.ViewOf(clip) : null;

    // A gap's pill clicked: what follows on the row's track closes up (Ripple All Tracks: on every unlocked track).
    private void Timeline_CloseGapRequested(TimelineRow row, double from, double to)
    {
        if (row.Data is Layer track)
            _viewModel.CloseGapOn(track, from, to);
    }

    // Alt + click on a block: the razor, there (on the clip as the timeline has it now, by its id).
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

            // A caption block (5.1-Polish, the user's directive): its words are split at the word under the click.
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

    // ----- The tools -----

    /// <summary>
    /// Every button of the timeline pane: what it does, by its Tag (the pane's title row, corners, inspector, bin and Add
    /// Layer kinds). A fault in it is a status line and a note in the log (4.4 BugHunt), never an exception that ends the app.
    /// </summary>
    /// <summary>The Cutout Studio on the video, for Cutouts and Game Presets.</summary>
    private void OpenCutoutStudio()
    {
        if (_viewModel.HasSource)
            new MaskDrawWindow(_viewModel, null) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// The Cutout Studio for one video clip (5.2: "Open Cutout &amp; Mask Studio..." on every video clip's menu): it starts
    /// from the clip's own drawn shape, and Apply to This Clip cuts the clip to what is drawn, kept as its outlines alone (no
    /// picture file), one step of Undo, as the mask dialog's Draw Custom Shape does. Its Cutouts and Game Presets are there as
    /// ever.
    /// </summary>
    private void OpenCutoutStudio(Layer clip)
    {
        _viewModel.Checkpoint($"change the mask of {clip.Name}");
        var visit = _viewModel.UndoMark;
        var window = new MaskDrawWindow(_viewModel, clip) { Owner = this };
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
            case "ZoomIn": ZoomAboutPlayhead(1.6); break;
            case "ZoomOut": ZoomAboutPlayhead(1 / 1.6); break;
            case "ZoomFit": FitTimeline(); break;
            case "PlayPause": PlayPause_Click(sender, e); break;

            // The inspector's: they act on what their group shows (the button's DataContext).
            case "Properties" when sender is FrameworkElement { DataContext: Layer element }: ShowInEffectsPane(element); break;
            case "BlurSettings": ShowBlurFillSettings(); break;
            case "TrackFilters": TrackFilters_Click(sender, e); break;

            // The recycle bin's, and Text Settings' colours.
            case "RestoreDeleted": RestoreDeleted_Click(sender, e); break;
            case "EmptyBin": EmptyBin_Click(sender, e); break;
            case "Font" or "Outline": TextColor_Click(sender, e); break;

            // Add Track's kinds (the flyout has closed itself).
            case "builtin.vfx.explosion" or "builtin.vfx.sparks" or "builtin.vfx.animelines" or "builtin.vfx.shield" or "builtin.vfx.saber" or "builtin.gpu.shockwave":
            case "Empty" or "Video" or "Image" or "Text" or "Adjustment" or "Region" or "MaskLayers" or "Audio" or "Voiceover"
                or "Captions" or "ImportSubtitles" or "EditCaptions" or "CaptionStyle":
                AddLayerMenu_Click(sender, e);
                break;
        }
    }

    /// <summary>
    /// U (and Toggle A/V Link on a clip's menu): the selected clip's sound is linked to its picture, or unlinked from it
    /// (one step of Undo); unlinked, it gets a lane of its own in its row's stack, opened, to be dragged by itself. The main
    /// video's sound is its file's audio streams: for its clips this is the chain button (every clip with sound at once).
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

        // Its row is known by its track's number (every clip of a track has it): opened once the rows are made again.
        if (wasLinked)
            _expandRowKey = clip.TrackId;
        ScheduleClipRedraw();
    }

    // ----- The inspector -----

    /// <summary>Shows the selection's values under the tracks: a picture clip's place and size, a sound's gain and mute, the background's blur.</summary>
    private void UpdateTimelineInspector()
    {
        object? picture = null, sound = null, background = null;
        var name = "";
        switch (_selectedClip)
        {
            case Layer { IsBackground: true } layer:
                (background, name) = (_viewModel, layer.Name);
                break;

            // The captions (5.0-Verify): their box's place and size, Properties (their position and look) and Caption Style.
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

        // One of clips selected together (5.0): the inspector edits it alone, and says how many more there are.
        if (GroupHolds && _viewModel.SelectedClips.Count > 1)
            name = string.Create(CultureInfo.InvariantCulture, $"{name} (+{_viewModel.SelectedClips.Count - 1} selected)");

        UnifiedTimeline.Inspect(name, picture, sound, background);
    }
}
