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
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

// ----- Tracks: the timeline's blocks, the editing tools (split, trim, delete, undo) and a track's right-click menus -----
public partial class MainWindow
{
    // ----- Blocks in time -----
    // Each UnifiedTimelineControl row (built in MainWindow.UnifiedTimeline.cs) holds blocks for its model items: a track's clips (file
    // streams are ordinary audio Sub-Lane clips), an unlinked video clip's own audio, and captions. x = seconds * pixels-per-second.
    // A drag moves only its block; the model, command, player and Undo change once on release.
    // Rows rebuild after edits and zoom changes; playback moves only the playhead. Block thumbnails and waveforms are cut from cached
    // images in file time, so they stay fixed in the media under trims and slide under slips.

    private static readonly Brush LayerClipBrush = Frozen(Color.FromArgb(0xD0, 0x8E, 0x5A, 0xB8));
    private static readonly Brush LayerAudioBrush = Frozen(Color.FromArgb(0xD0, 0x3C, 0x9A, 0x5F));
    private static readonly Brush SoundClipBrush = Frozen(Color.FromArgb(0xD0, 0x2B, 0x7A, 0x6B));
    private static readonly Brush OffClipBrush = Frozen(Color.FromArgb(0xC0, 0x55, 0x55, 0x55));
    private static readonly Brush EffectClipBrush = Frozen(Color.FromArgb(0xD0, 0x5E, 0x4B, 0xC4));
    private static readonly Brush SubtitleClipBrush = Frozen(Color.FromArgb(0xD0, 0x2E, 0x7D, 0x6B));
    private static readonly Brush MainUnderBrush = Frozen(Color.FromArgb(0x48, 0x2F, 0x6F, 0xB5));

    /// <summary>Keyframe x offsets along a clip's block in pixels; empty while keyframe diamonds are hidden.</summary>
    /// <param name="shift">Offset of the keyframe time origin from the block start, in seconds.</param>
    private IEnumerable<double>? ClipKeys(Layer clip, double shift, double scale) =>
        _viewModel.ShowClipKeyframes && clip.IsAnimated ? clip.KeyTimes.Select(t => (t + shift) * scale) : null;

    /// <summary>Selectable wrapper for a video layer's own audio.</summary>
    private sealed record LayerSound(Layer Layer);

    // Primary selection: a Layer (one clip, or the blur fill's settings row) or a LayerSound. Multi-selection (Ctrl/Shift+click) lives in
    // the view model's SelectedClips; this item drives the inspector, Crop boxes and Keyframes pane, and must belong to that group.
    private object? _selectedClip;

    // Empty selection = the video clip at the playhead (SelectClip(null)).
    private bool _playheadVideoSelected;
    private bool _clipRedrawQueued;

    // Selection change deferred to a no-move release: Ctrl/Shift+click on a selected clip, or a plain click narrowing a group to one clip;
    // a drag cancels it. Ctrl on an unselected clip's edge ripple-trims it alone when dragged and only joins the selection (Add) on a
    // no-move release.
    private enum ClickGesture { Toggle, Range, Narrow, Add }

    private (Layer Clip, ClickGesture Gesture)? _clickGesture;

    // Shift+click range anchor: the clip selected before the first Shift+click (valid while the group is the range it made).
    private Layer? _rangeAnchor;

    // Rows are stale (edit, undo or scale change since built): rebuilt at Background priority, or synchronously by the next timeline
    // press or drag end.
    private bool _rowsStale;

    // Block press (either button; right-click selects so its menu targets the block). Ctrl+click toggles a clip in the selection,
    // Shift+click selects the range from the anchor across time and tracks; pressing a group member keeps the group (a drag moves all).
    private void Timeline_BlockPressed(TimelineRow row, TimelineBlock block, MouseButtonEventArgs e)
    {
        _clickGesture = null;

        // Effect block: selects its clip and shows its effects in the Effects pane; an unlocked one starts dragging on press.
        if (block.Clip is EffectBlock effectBlock)
        {
            if (LiveClip(effectBlock.Clip) is { } owner && !IsSelected(owner))
                SelectClip(owner);
            return;
        }

        // Caption blocks are not clips: any press selects the caption track (position, size and style in the inspector; box on the player
        // and Canvas). Text and timing belong to the Subtitle Studio, opened by double-click.
        if (block.Clip is SubtitleRef)
        {
            SelectClip(_viewModel.CaptionLayer);
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
                Dispatcher.BeginInvoke(EditCaptions, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        if (block.Clip is Layer clip && _viewModel.CanGroup(clip))
        {
            var modifiers = Keyboard.Modifiers;
            var (ctrl, shift) = (modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Shift));
            if (e.ChangedButton == MouseButton.Left && !modifiers.HasFlag(ModifierKeys.Alt) && (ctrl || shift))
            {
                // Unselected: add now so a following drag includes it. Selected: decide on release. Ctrl on an edge ripple-trims the
                // clip alone and joins the selection only on a no-move release.
                if (!IsSelected(clip))
                {
                    if (ctrl && UnifiedTimeline.Surface.PressRipples)
                        _clickGesture = (clip, ClickGesture.Add);
                    else if (ctrl)
                        AddToClipGroup(clip);
                    else
                        SelectClipRange(clip);
                }
                else
                {
                    _clickGesture = (clip, ctrl ? ClickGesture.Toggle : ClickGesture.Range);
                }

                return;
            }

            if (_viewModel.IsInClipGroup(clip) && GroupHolds)
            {
                // Group member: keep the group (drag and context menu apply to all) and make it primary; a no-move release narrows to it.
                SelectClip(clip, keepGroup: true);
                if (e.ChangedButton == MouseButton.Left)
                    _clickGesture = (clip, ClickGesture.Narrow);
                return;
            }
        }

        SelectClip(block.Clip);
    }

    private void Timeline_DragRefused(string why)
    {
        _viewModel.StatusText = why;
        CrashReporter.Note("timeline", "refused: a drag");
    }

    /// <summary>Block Data for one subtitle on the caption row.</summary>
    private sealed record SubtitleRef(double Start);

    // Click on empty row space or below all rows: seek the playhead to that time.
    private void Timeline_EmptyPressed(TimelineRow? row, double seconds, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(seconds * 1000, 0, _viewModel.DurationMs);
    }

    /// <summary>
    /// Coalesces row rebuilds into one Background-priority pass (never mid-drag; drag end rebuilds). Until it runs the rows are stale and
    /// the next timeline press rebuilds first (<see cref="FlushClipRedraw"/>).
    /// </summary>
    private void ScheduleClipRedrawUnlessLook()
    {
        if (_viewModel.LastChange == PreviewChange.Look)
            return;
        ScheduleClipRedraw();
    }

    private void ScheduleClipRedraw()
    {
        _rowsStale = true;
        if (_clipRedrawQueued || UnifiedTimeline.Surface.IsDragging)
            return;

        _clipRedrawQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _clipRedrawQueued = false;
            FlushClipRedraw();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Rebuilds stale rows synchronously before a timeline press is hit-tested (the queued rebuild runs after input, so a just-split clip
    /// would still be one block and drag as a whole) and at drag end. Never mid-drag.
    /// </summary>
    private void FlushClipRedraw()
    {
        if (!_rowsStale || UnifiedTimeline.Surface.IsDragging)
            return;

        try
        {
            RebuildTimelineRows();
            UpdateTimelineChrome();
            UpdateMasterStretch();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Report and log once; retry on the next change (never a loop of failing rebuilds).
            _rowsStale = false;
            AppLog.Write($"Timeline: laying out the rows failed: {ex}");
            CrashReporter.Note("timeline", $"rows failed: {ex.GetType().Name}");
            _viewModel.StatusText = "The timeline could not be drawn as it is now (the details are in the log). The next change draws it again.";
        }
    }

    /// <summary>
    /// Runs a timeline menu/tool action; a fault becomes a status line and a log entry instead of crashing. Rows rebuild either way.
    /// </summary>
    private void RunGuarded(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TimelineFault(what, ex);
        }
    }

    /// <summary>Handles a timeline edit fault: logged with its site, recorded for crash reports (type only), shown in the status bar.</summary>
    private void TimelineFault(string what, Exception ex)
    {
        AppLog.Write($"Timeline: {what} failed: {ex}");
        CrashReporter.Note("timeline", $"failed: {ex.GetType().Name}");
        _viewModel.StatusText = "Something went wrong with that edit, so it was stopped (the details are in the log). If anything changed, Undo puts it back.";
        ScheduleClipRedraw();
    }

    /// <summary>
    /// Re-resolves a block's clip by id (undo recreates views) and verifies it still matches the block's <paramref name="built"/> times.
    /// Null when gone, changed (undo, another edit) or during an undo restore; the status explains unless <paramref name="quiet"/>.
    /// </summary>
    private Layer? LiveClip(Layer view, (double Start, double End)? built = null, bool quiet = false)
    {
        string? why = null;
        Layer? live = null;
        if (_viewModel.IsRestoringTimeline)
            why = "The timeline is still being put back (Undo or Redo), so nothing was changed. Try again.";
        else if (view.ClipId == Guid.Empty)
            live = view;
        else if (_viewModel.Document.FindClip(view.ClipId) is not { } clip)
            why = $"{view.Name} is no longer on the timeline (an undo or another edit took it away), so nothing was changed.";
        else if (built is { } was && (Math.Abs(clip.Start - was.Start) > 1e-6 || Math.Abs(clip.End - was.End) > 1e-6))
            why = $"{view.Name} changed while it was being dragged (an undo or another edit), so nothing was changed.";
        else
            live = _viewModel.ViewOf(clip) ?? view;

        if (why is not null && !quiet)
        {
            _viewModel.StatusText = why;
            CrashReporter.Note("timeline", "refused: the clip changed under the edit");
        }

        return live;
    }

    // Edge-sharing tolerance in seconds: a child within this of a parent edge shares it (matches the model).
    private const double FamilySlack = 1e-6;

    /// <summary>
    /// A block's family info from the model (seconds): clip id, parent id while the parent is on the timeline, whether parent moves carry
    /// it (overlaps the parent on an unlocked track: the model's rule), and which parent edges it shares.
    /// </summary>
    private BlockFamily? FamilyOf(Models.Timeline.TimelineClip? clip)
    {
        if (clip is null)
            return null;

        var document = _viewModel.Document;
        if (clip.ParentClipId is not { } parentId || document.FindClip(parentId) is not { } parent)
            return new BlockFamily(clip.Id, null, false, BlockEdges.None);

        var rides = Math.Min(clip.End, parent.End) - Math.Max(clip.Start, parent.Start) > 2 * FamilySlack && document.TrackOf(clip) is { IsLocked: false };
        var edges = (Math.Abs(clip.Start - parent.Start) <= FamilySlack ? BlockEdges.Start : BlockEdges.None)
            | (Math.Abs(clip.End - parent.End) <= FamilySlack ? BlockEdges.End : BlockEdges.None);
        return new BlockFamily(clip.Id, parent.Id, rides, edges);
    }

    // Shared drag capability for every clip block.
    private static readonly Func<bool> AlwaysDraggable = static () => true;

    /// <summary>
    /// Fills a row with blocks at the surface scale (px/s) according to its Data: a track's clips, captions, or the blur fill row. Returns
    /// the track's unlinked-audio blocks (independently draggable) for a lane beneath the row, or null.
    /// </summary>
    private List<TimelineBlock>? BuildRowBlocks(TimelineRow row, double scale, double seconds)
    {
        row.Blocks.Clear();
        if (scale <= 0 || seconds <= 0)
            return null;

        var perPixel = 1 / scale;
        switch (row.Data)
        {
            // Captions span their display times. Not draggable: they follow their source audio through cuts and are edited in the
            // Subtitle Studio (right-click, Edit Captions...).
            case Layer { IsCaptions: true }:
                foreach (var (from, to, text, _) in _viewModel.GetSubtitleSegments())
                    row.Blocks.Add(new TimelineBlock { Left = from * scale, Width = Math.Max((to - from) * scale, 3), Name = text, Fill = SubtitleClipBrush, Clip = new SubtitleRef(from) });

                return null;

            // Blur fill settings row (per-video fills share this look): spans the sequence; selecting it shows the blur in the inspector.
            case Layer { IsBackground: true } background:
                row.Blocks.Add(new TimelineBlock
                {
                    Width = Math.Max(seconds * scale, 3),
                    Name = background.IsHidden ? "off: videos opened get no blur fill" : "",
                    Fill = background.IsHidden ? OffClipBrush : MainUnderBrush,
                    Clip = background,
                    CannotDrag = "The blur fill's settings last as long as the sequence: they are not moved.",
                });

                return null;

            // Empty track: no blocks (the row shows a drop hint).
            case Layer { IsEmptyTrack: true }:
                return null;

            // Track clips: movable, trimmable from either edge, rollable, slippable, rippleable, and movable across tracks.
            case Layer layer:
                return AddTrackBlocks(row, layer, scale, perPixel, seconds);
        }

        return null;
    }

    /// <summary>Adds a track's clip blocks to its row; returns their unlinked-audio blocks (see BuildRowBlocks).</summary>
    private List<TimelineBlock>? AddTrackBlocks(TimelineRow row, Layer layer, double scale, double perPixel, double seconds)
    {
        List<TimelineBlock>? sounds = null;
        var clipsOnTrack = _viewModel.GetTrackClips(layer);
        var settings = AppSettings.Current;
        var gainLines = settings.ShowClipGainLines;

        // Timeline Settings toggles: when off, blocks omit thumbnails / waveforms (audio clips, video clips' own, unlinked audio, and hence
        // the closed audio stack's summary bar).
        var (filmstrips, waveforms) = (settings.ShowClipFilmstrips, settings.ShowClipWaveforms);

        // Waveform height = clip gain * track gain, as mixed, so a lane's waveform matches its share of the video's StackSound.
        var trackModel = _viewModel.TrackOfRow(layer);
        var trackGainDb = trackModel?.GainDb ?? 0;
        foreach (var clip in clipsOnTrack)
        {
            // Resolve the clip's timeline view once for all per-block checks below.
            var model = _viewModel.ClipOf(clip);
            var (from, to) = clip.GetSpan(seconds);
            var duration = to - from;
            var speed = ClipSpeed(clip);
            var linked = MainViewModel.IsClipAudioLinked(model);
            var slip = linked ? 0 : MainViewModel.ClipAudioSlip(model);
            var split = MainViewModel.ClipAudioSplit(model);

            // Linked audio draws inside the block (always for audio clips); unlinked video audio gets its own block (below).
            var showsSound = !clip.IsHidden && (clip.IsAudio || (linked && _viewModel.ShowLinkedAudio && clip.CarriesSound));
            var gainLine = showsSound && !clip.IsLoading && gainLines;
            var name = clip.IsLoading ? "Loading..." : clip.IsHidden ? "hidden" : clip.IsAudio ? clip.Name : "";

            // Block identity and build-time position: a release re-resolves the clip by id, edits only if it is unchanged, and applies the
            // block's delta (so a block drawn wider than its clip, min 3 px, never stretches it).
            (double Start, double End) built = model is null ? (from, to) : (model.Start, model.End);
            var (left, width) = (from * scale, Math.Max(duration * scale, 3));
            var cutout = CountsAsCutout(_viewModel, clip);
            row.Blocks.Add(new TimelineBlock
            {
                Left = left,
                Width = width,
                Name = name,
                Fill = clip.IsLoading || clip.IsHidden ? OffClipBrush : clip.IsAudio ? SoundClipBrush : LayerClipBrush,
                Clip = clip,
                Family = FamilyOf(model),

                // Thumbnails (from the file's strip) and waveform, both in file time. A Cutout's tiles show its own region of the shared
                // strip (CutoutFilmCrop), so HUD cutout rows show their zoomed piece with no extra decoding.
                Film = clip.IsHidden || !filmstrips ? null
                    : clip.IsVideoFile && !clip.IsLoading ? FilmOf(_viewModel.FilmstripOf(clip), clip.ImageWidth, clip.ImageHeight, clip.MediaOffset, clip.MediaDuration, speed,
                        cutout ? CutoutFilmCrop(clip) : null)
                    : null,
                SoundWindow = showsSound && waveforms ? SoundOf(_viewModel.WaveformOf(clip), clip.MediaOffset, duration, speed, clip.GainDb + trackGainDb) : null,
                Keys = ClipKeys(clip, 0, scale)?.ToList(),
                Beats = BeatTicks(model, from, scale),
                Badge = BadgeOf(speed, slip, BadgeEffects(clip, cutout, settings)),
                CanDrag = AlwaysDraggable,
                Resizable = true,

                // Per-clip switches: On/Off for this clip only (children keep theirs) and a child's Lock to its parent.
                IsOn = model is null || clip.IsLoading ? null : MainViewModel.IsClipOn(model),
                SwitchIsSound = clip.IsAudio,
                IsLocked = model is null || clip.IsLoading || (trackModel ?? _viewModel.Document.TrackOf(model)) is not { } onTrack ? null : _viewModel.ClipLock(model, onTrack),
                Switched = which => BlockSwitched(clip, which),

                // Released slightly overlapping the adjacent clip: creates a crossfade instead (see CommitClipMove).
                Commit = (newLeft, newWidth) => CommitClip(clip, built, left, width, newLeft, newWidth, perPixel),

                // Edge trims, shared cut with the next clip (roll), Alt inside (slip), Ctrl on an edge (ripple).
                TrimmableStart = clip.Duration > 0.001 && !clip.IsLoading,
                Slippable = clip.Duration > 0.001 && (clip.IsVideoFile || clip.IsAudio),
                Edit = done => EditClip(clip, built, done, left, width, perPixel),
                Gain = gainLine ? (clip.GainDb, clip.FadeInSeconds, clip.FadeOutSeconds) : null,
                GainEdit = done =>
                {
                    if (LiveClip(clip) is { } live)
                        _viewModel.SetClipGain(live, done.GainDb);
                },

                // Fade handles at the top corners (video and audio over lower layers), plus a crossfade pill across the cut with the
                // previous clip on the track.
                Fades = clip.IsLoading || clip.IsHidden ? null : (clip.FadeInSeconds, clip.FadeOutSeconds, clip.FadeInStyle, clip.FadeOutStyle),
                FadeEdit = (fadeIn, fadeSeconds) =>
                {
                    if (LiveClip(clip) is { } live)
                        _viewModel.SetClipFade(live, fadeIn, fadeSeconds);
                },
                CrossfadeIn = clip.TransitionIn > 0.01 && _viewModel.ClipBefore(clip) is not null ? clip.TransitionIn : 0,
                CrossfadeEdit = seconds =>
                {
                    if (LiveClip(clip) is { } live)
                        _viewModel.SetCrossfade(live, seconds);
                },

                // Vertical drag onto another row: moves to that track (empty, or same kind with room). Audio (incl. unpacked streams) may
                // also drop on a video row, becoming a lane in its audio stack at the same time.
                CanMoveTo = (target, at) =>
                    target is Layer track && LiveClip(clip, built, quiet: true) is { } live
                    && (_viewModel.CanMoveClipToTrack(live, track, StartOf(built, left, at, perPixel)) || (clip.IsAudio && _viewModel.CanMoveSoundUnder(live, track))),
                MoveTo = (target, at) =>
                {
                    if (target is not Layer track || LiveClip(clip, built) is not { } live)
                        return;

                    CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: onto another track, {(at - left) * perPixel:+0.000;-0.000} s"));
                    if (clip.IsAudio && _viewModel.CanMoveSoundUnder(live, track))
                        _viewModel.MoveSoundToOwnLane(live, track);
                    else
                        _viewModel.MoveClipToTrack(live, track, StartOf(built, left, at, perPixel));
                },

                // Drag onto a seam above, below or between video tracks: creates a new track there and moves the clip and its family.
                CanMoveToNewTrack = clip.IsAudio ? null : (beside, above, at) =>
                    beside is Layer track && LiveClip(clip, built, quiet: true) is { } live
                    && _viewModel.CanMoveClipToNewTrack(live, track, above, StartOf(built, left, at, perPixel)),
                MoveToNewTrack = clip.IsAudio ? null : (beside, above, at) =>
                {
                    if (beside is not Layer track || LiveClip(clip, built) is not { } live)
                        return;

                    CrashReporter.Note("timeline", above ? "drag: onto a new track above" : "drag: onto a new track below");
                    _viewModel.MoveClipToNewTrack(live, track, above, StartOf(built, left, at, perPixel));
                },

                // Tether: released on the bottom edge of an overlapped clip on another lane (with Alt, anywhere on it) becomes that clip's
                // child at the release time. The pill shows the parent's name (a video block's own Name is empty).
                CanAttachTo = model is null || clip.IsLoading ? null : (parentBlock, at) =>
                    parentBlock.Clip is Layer parentView && LiveClip(clip, built, quiet: true) is { } live && LiveClip(parentView, quiet: true) is { } parent
                    && _viewModel.CanAttachClip(live, parent, StartOf(built, left, at, perPixel)) ? parent.Name : null,
                AttachTo = model is null || clip.IsLoading ? null : (parentBlock, at) =>
                {
                    if (parentBlock.Clip is not Layer parentView || LiveClip(clip, built) is not { } live || LiveClip(parentView) is not { } parent)
                        return;

                    CrashReporter.Note("timeline", "drag: attached as a child");
                    _viewModel.AttachClip(live, parent, StartOf(built, left, at, perPixel));
                },

                // J/L cut: audio extending past the video edges; Alt on an edge's lower half creates or adjusts it.
                AudioSplit = split is { } cut && (Math.Abs(cut.LeadIn) > 1e-6 || Math.Abs(cut.TailOut) > 1e-6) ? cut : null,
                AudioSplitEdit = split is null || clip.IsLoading ? null : (lead, tail) =>
                {
                    if (LiveClip(clip) is { } live)
                        _viewModel.SetClipAudioSplit(live, lead, tail);
                },

                // Edge at a folded dead-air seam: dragging outward unfolds, inward folds more.
                SeamRange = model is null || clip.IsLoading ? null : startEdge => SeamRangeOf(clip, startEdge),
                SeamTrim = model is null || clip.IsLoading ? null : (startEdge, outward) => TrimAtSeam(clip, built, startEdge, outward),
            });

            // Unlinked video audio: starts with its video or at its slipped offset; follows the video's moves and edge trims.
            if (clip is { IsVideoFile: true, CarriesSound: true, IsLoading: false } && !linked)
            {
                (sounds ??= []).Add(new TimelineBlock
                {
                    Left = (from + slip) * scale,
                    Width = Math.Max(duration * scale, 3),
                    Name = "sound",
                    Fill = clip.IsHidden || clip.IsMuted ? OffClipBrush : LayerAudioBrush,
                    Clip = new LayerSound(clip),
                    Family = model is null ? null : new BlockFamily(("sound", model.Id), model.Id, true, BlockEdges.Start | BlockEdges.End),
                    SoundWindow = waveforms ? SoundOf(_viewModel.WaveformOf(clip), clip.MediaOffset, duration, speed, clip.GainDb + trackGainDb) : null,
                    Beats = BeatTicks(model, from + slip, scale),
                    Badge = BadgeOf(1, slip),
                    CanDrag = AlwaysDraggable,
                    MayStartBeforeZero = true,
                    Commit = (newLeft, _) =>
                    {
                        if (LiveClip(clip, built) is not { } live)
                            return;

                        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: slip a clip's sound to {newLeft * perPixel - built.Start:+0.000;-0.000} s"));
                        _viewModel.SlipClipSound(live, newLeft * perPixel - built.Start);
                    },
                });
            }
        }

        return sounds;
    }

    /// <summary>
    /// A block's waveform slice: the file's audio from <paramref name="mediaFrom"/> for <paramref name="seconds"/> of timeline at
    /// <paramref name="speed"/>, scaled by gain. Null until the waveform is built (rows rebuild when it arrives).
    /// </summary>
    private static WaveWindow? SoundOf(ImageSource? picture, double mediaFrom, double seconds, double speed, double gainDb) =>
        Waveforms.DataOf(picture) is { } data ? new WaveWindow(data, mediaFrom, mediaFrom + Math.Max(seconds, 0) * speed, speed, DecibelScale(gainDb)) : null;

    /// <summary>Converts a dB gain to a clamped waveform height multiplier (1 at 0 dB).</summary>
    private static double DecibelScale(double gainDb) => Math.Clamp(Math.Pow(10, gainDb / 20), 0, 8);

    // ----- Combined audio-stack waveform -----
    // A video clip with an audio stack (file streams on Sub-Lanes, unlinked audio) draws one waveform: the per-sample max of its audible
    // lanes (WaveformData.Combine), each at clip * track gain, using the closed stack bar's rules (lane not muted by itself or the video
    // track's master mute). Hidden/disabled parents still show it (their lanes keep playing). Computed in the parent's file time: when
    // every lane spans the whole clip, all clips of that file share one result across splits and zooms (tiles redraw, never rebuild);
    // otherwise only the clip's own file range is combined (Law 2: no clip holds a whole file's audio).

    // Combined waveforms keyed by their inputs: the previous layout's set (checked first, so large timelines never rebuild all per edit),
    // this layout's set, and a small LRU (about 2.6 MB per hour of audio, Law 2).
    private static Dictionary<StackSoundKey, WaveformData> _stackSoundsDrawn = [], _stackSoundsDrawing = [];
    private static readonly LruCache<StackSoundKey, WaveformData> StackSounds = new(12);

    /// <summary>
    /// Cache-key part of a combined waveform: peaks (by reference), gain in thousandths, shift in peak pairs, and the played pair range
    /// [First, End) (int.MinValue/int.MaxValue for a lane spanning the whole block).
    /// </summary>
    private readonly record struct StackSoundPart(WaveformData Data, int GainMilli, int ShiftPairs, int First, int End);

    /// <summary>
    /// Cache key of a combined waveform: ordered parts plus the parent-file pair window [From, To) (null when every lane spans the whole
    /// block). Equal when all parts and the window are equal.
    /// </summary>
    private sealed class StackSoundKey(StackSoundPart[] parts, (int From, int To)? window) : IEquatable<StackSoundKey>
    {
        public StackSoundPart[] Parts { get; } = parts;

        public (int From, int To)? Window { get; } = window;

        public bool Equals(StackSoundKey? other) => other is not null && Window == other.Window && Parts.AsSpan().SequenceEqual(other.Parts);

        public override bool Equals(object? obj) => Equals(obj as StackSoundKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Window);
            foreach (var part in Parts)
                hash.Add(part);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Sets <see cref="TimelineBlock.StackSound"/> on each video block with an audio stack, after BuildTimelineRows stacks the rows (only
    /// with waveforms and Show Linked Audio on), from its audible lane blocks matched by Family parent, at <paramref name="scale"/> px/s
    /// over <paramref name="seconds"/>. Mute/gain/waveform changes already trigger ScheduleClipRedraw, so no extra subscriptions. Entries
    /// unused by this pass are evicted (all of them with no rows).
    /// </summary>
    internal static void GiveStacksTheirSound(IReadOnlyList<TimelineRow> rows, double scale, double seconds)
    {
        if (scale > 0)
        {
            foreach (var row in rows)
                Give(row);
        }

        (_stackSoundsDrawn, _stackSoundsDrawing) = (_stackSoundsDrawing, _stackSoundsDrawn);
        _stackSoundsDrawing.Clear();

        void Give(TimelineRow row)
        {
            // Audible lane blocks grouped by parent clip: lane not muted (a muted video track mutes all), block has audio and is not
            // individually muted (unlinked audio's switch).
            Dictionary<object, List<(double Start, WaveWindow Sound)>>? heard = null;
            if (row.Kind == TimelineRowKind.Video && (row.State & RowSwitches.Muted) == 0)
            {
                foreach (var lane in row.Children)
                {
                    if (lane.Stack != ChildStack.Audio || (lane.State & RowSwitches.Muted) != 0)
                        continue;

                    foreach (var block in lane.Blocks)
                    {
                        if (block.Family?.Parent is not { } parent || block.SoundWindow is not { } sound || block.Clip is LayerSound { Layer.IsMuted: true })
                            continue;

                        heard ??= new();
                        if (!heard.TryGetValue(parent, out var sounds))
                            heard[parent] = sounds = [];
                        sounds.Add((block.Left / scale, sound));
                    }
                }
            }

            // Every parent video block, including hidden or disabled ones (their lanes still play).
            if (heard is not null)
            {
                foreach (var block in row.Blocks)
                {
                    if (block.Clip is not Layer { IsLoading: false } clip || block.Family?.Key is not { } key || !heard.TryGetValue(key, out var sounds))
                        continue;

                    var (from, to) = clip.GetSpan(seconds);
                    block.StackSound = StackSoundOf(block.Left / scale, clip.MediaOffset, ClipSpeed(clip), to - from, sounds);
                }
            }

            foreach (var child in row.Children)
                Give(child);
        }
    }

    /// <summary>
    /// Computes a video block's <see cref="TimelineBlock.StackSound"/> in its file time: file from <paramref name="mediaFrom"/> for
    /// <paramref name="duration"/> timeline seconds at <paramref name="speed"/>, block at <paramref name="start"/>. Children at another speed
    /// (never resampled) or not overlapping are skipped. Full-span lanes combine whole files (shared per file); partial lanes combine only
    /// the block's file range, each lane clipped (Law 2). Results are cached by parts (StackSounds); null when nothing is audible.
    /// </summary>
    internal static WaveWindow? StackSoundOf(double start, double mediaFrom, double speed, double duration, IReadOnlyList<(double Start, WaveWindow Sound)> children)
    {
        if (!(speed > 0) || !(duration > 0) || children.Count == 0)
            return null;

        var mediaTo = mediaFrom + duration * speed;
        List<StackSoundPart>? parts = null;
        foreach (var (childStart, sound) in children)
        {
            var data = sound.Data;
            var pairsPerSecond = data.PairsPerSecond;
            if (data.Count == 0 || Math.Abs(sound.Speed - speed) > 1e-6 || !(sound.GainScale > 0))
                continue;

            // Parent file time = lane file time + shift; the lane's played range mapped to parent file time.
            var shift = sound.MediaFrom - mediaFrom - (childStart - start) * speed;
            var (playsFrom, playsTo) = (sound.MediaFrom - shift, sound.MediaTo - shift);
            if (!(playsTo > mediaFrom && playsFrom < mediaTo) || !(Math.Abs(shift * pairsPerSecond) < 1 << 30))
                continue;

            var pair = 1 / pairsPerSecond;
            var whole = playsFrom <= mediaFrom + pair && playsTo >= mediaTo - pair;
            (parts ??= []).Add(new StackSoundPart(data, (int)Math.Round(sound.GainScale * 1000), (int)Math.Round(shift * pairsPerSecond),
                whole ? int.MinValue : (int)Math.Floor(sound.MediaFrom * pairsPerSecond), whole ? int.MaxValue : (int)Math.Ceiling(sound.MediaTo * pairsPerSecond)));
        }

        if (parts is null)
            return null;

        // Partial lane: window = block's file range, pairs [mediaFrom, mediaTo) at the first part's rate; combined pair 0 = window start.
        (int From, int To)? window = null;
        var laidFrom = 0.0;
        if (parts.Exists(p => p.First != int.MinValue))
        {
            var pairsPerSecond = parts[0].Data.PairsPerSecond;
            if (!(Math.Abs(mediaFrom * pairsPerSecond) < 1 << 30 && Math.Abs(mediaTo * pairsPerSecond) < 1 << 30))
                return null;
            window = ((int)Math.Floor(mediaFrom * pairsPerSecond), (int)Math.Ceiling(mediaTo * pairsPerSecond));
            laidFrom = window.Value.From / pairsPerSecond;
        }

        var key = new StackSoundKey([.. parts], window);
        if (!_stackSoundsDrawing.TryGetValue(key, out var combined) && !_stackSoundsDrawn.TryGetValue(key, out combined) && !StackSounds.TryGetValue(key, out combined))
        {
            if (CombinedStackSound(key) is not { } made)
                return null;
            StackSounds[key] = combined = made;
        }

        _stackSoundsDrawing[key] = combined;
        return new WaveWindow(combined, mediaFrom - laidFrom, mediaTo - laidFrom, speed);
    }

    /// <summary>
    /// Builds a combined waveform: each part at its gain and shift; with a key Window (partial lane), each part is first clipped to the
    /// pairs it plays inside that window.
    /// </summary>
    private static WaveformData? CombinedStackSound(StackSoundKey key)
    {
        var parts = new List<(WaveformData Data, double GainFactor, double OffsetShift)>(key.Parts.Length);
        foreach (var (data, gainMilli, shiftPairs, first, end) in key.Parts)
        {
            var pairsPerSecond = data.PairsPerSecond;
            if (key.Window is not { } window)
            {
                parts.Add((data, gainMilli / 1000.0, shiftPairs / pairsPerSecond));
                continue;
            }

            // Part pairs [first, end) inside the window: combined pair i = parent pair window.From + i = own pair window.From + shift + i.
            // A part at a different rate is skipped, matching Combine.
            if (pairsPerSecond != key.Parts[0].Data.PairsPerSecond)
                continue;

            var lows = data.LowsAt(0);
            var highs = data.HighsAt(0);
            var count = Math.Min(lows.Length, highs.Length);
            var at = (long)window.From + shiftPairs;
            var (from, to) = ((int)Math.Clamp(Math.Max(first, at), 0, count), (int)Math.Clamp(Math.Min(end, (long)window.To + shiftPairs), 0, count));
            if (to > from)
                parts.Add((new WaveformData(lows[from..to].ToArray(), highs[from..to].ToArray(), data.Fill, pairsPerSecond), gainMilli / 1000.0, (at - from) / pairsPerSecond));
        }

        return WaveformData.Combine(parts);
    }

    /// <summary>Clip playback speed multiplier; 1 when it has none.</summary>
    private static double ClipSpeed(Layer clip) => clip.HasSpeed ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    /// <summary>
    /// A block's badge text: speed when not 1 ("2x"), unlinked-audio slip ("+120 ms"), and effect count ("fx 2", from
    /// <see cref="BadgeEffects"/>, shown whether or not effect lanes are visible); null for none.
    /// </summary>
    internal static string? BadgeOf(double speed, double slip, int effects = 0)
    {
        var speedText = Math.Abs(speed - 1) > 0.001 ? string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x") : null;
        var slipText = Math.Abs(slip) >= 0.0005 ? string.Create(CultureInfo.InvariantCulture, $"{(slip > 0 ? "+" : "-")}{Math.Abs(slip) * 1000:0} ms") : null;
        var fxText = effects > 0 ? string.Create(CultureInfo.InvariantCulture, $"fx {effects}") : null;
        var parts = new[] { speedText, slipText, fxText }.Where(p => p is not null);
        return string.Join("  ", parts) is { Length: > 0 } text ? text : null;
    }

    // ----- Effect lane and badge card filtering -----
    // Every Cutout carries two structural cards, Mask and Rounded & Feathered Edges (Layer.EnsureEffect). They are excluded from its lanes
    // and "fx N" badge unless Show Built-In Mask & Edge Lanes on Cutouts is on, and remain editable in the Effects pane. The same cards on
    // any non-Cutout clip count as ordinary effects.

    /// <summary>The structural cards every Cutout is created with: Mask and Rounded / Feathered Edges.</summary>
    internal static bool IsStructuralCard(EffectInstance effect) => effect.Type is EffectRegistry.Mask or EffectRegistry.Edges;

    /// <summary>
    /// Cutout test for lane filtering: a video clip sharing its parent's file, one freed from it but still in its stack
    /// (MainViewModel.IsCutoutOrFreed: unlocking or trimming outside must not resurface its Mask/Edges), or a whole-frame mask piece
    /// (Layer.UsesFrameMask).
    /// </summary>
    internal static bool CountsAsCutout(MainViewModel viewModel, Layer clip) => viewModel.IsCutoutOrFreed(clip) || clip.UsesFrameMask;

    /// <summary>
    /// A clip's lane cards in stack order: none with effect lanes off or for audio/captions; Cutout Mask/Edges only with
    /// <see cref="AppSettings.ShowBuiltInMaskEffectLanes"/>. Lane k of a track shows each clip's k-th card.
    /// </summary>
    internal static List<EffectInstance> LaneCards(Layer clip, bool isCutout, AppSettings settings) =>
        !settings.ShowTimelineEffectLanes || clip.IsAudio || clip.IsCaptions ? []
        : clip.Effects.Where(effect => ShowsCard(effect, isCutout, settings.ShowBuiltInMaskEffectLanes)).ToList();

    /// <summary>
    /// "fx N" badge count: the cards its lanes would show if enabled (a plain Cutout has no badge unless its Mask/Edges get lanes); the
    /// badge remains while lanes are off.
    /// </summary>
    internal static int BadgeEffects(Layer clip, bool isCutout, AppSettings settings) =>
        clip.IsAudio || clip.IsCaptions ? 0 : clip.Effects.Count(effect => ShowsCard(effect, isCutout, settings.ShowBuiltInMaskEffectLanes));

    // Lane-visible card: a known effect, excluding Cutout Mask/Edges unless enabled.
    private static bool ShowsCard(EffectInstance effect, bool isCutout, bool builtIns) => !effect.IsMissing && (builtIns || !isCutout || !IsStructuralCard(effect));

    // Block switch pressed: On/Off for this clip only, or a child's Lock to its parent.
    private void BlockSwitched(Layer clip, BlockSwitch which)
    {
        if (LiveClip(clip) is not { } live)
            return;

        RunGuarded(which == BlockSwitch.Lock ? "Lock / Unlock" : "On / Off", () =>
        {
            if (which == BlockSwitch.Lock)
                _viewModel.SetClipLocked(live, _viewModel.ClipLock(live) != true);
            else
                _viewModel.SetClipOn(live, !_viewModel.IsClipOn(live));
        });
        ScheduleClipRedraw();
    }

    // ----- Effect lanes -----

    /// <summary>Row Data for a track's effect lane: the owning track row and the card index it shows per clip.</summary>
    internal sealed record EffectLane(Layer Track, int Index);

    /// <summary>Block Data for one effect card: stable for the card's lifetime so a rebuilt block resolves to the same object.</summary>
    internal sealed class EffectBlock(Layer clip, EffectInstance effect)
    {
        public Layer Clip { get; } = clip;
        public EffectInstance Effect { get; } = effect;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EffectInstance, EffectBlock> _effectBlocks = [];

    private EffectBlock EffectBlockOf(Layer clip, EffectInstance effect)
    {
        if (_effectBlocks.TryGetValue(effect, out var known) && ReferenceEquals(known.Clip, clip))
            return known;
        var made = new EffectBlock(clip, effect);
        _effectBlocks.AddOrUpdate(effect, made);
        return made;
    }

    /// <summary>
    /// Builds a video track's effect lanes in its stack: lane k holds each clip's k-th card over its active range (the clip span, or once
    /// unlocked its own range dragged/trimmed within the clip); the block's switches are the card's On/Off and Lock. Crossfades stay on the
    /// clip row. Lanes collapse until the stack opens (HidesWhenClosed), vanish with effect lanes off, and filter via LaneCards.
    /// </summary>
    private List<TimelineRow> EffectRows(Layer track, double scale)
    {
        var rows = new List<TimelineRow>();
        if (track.IsAudio || track.IsCaptions || track.IsBackground || track.IsEmptyTrack)
            return rows;

        var settings = AppSettings.Current;
        var shown = _viewModel.GetTrackClips(track).Where(c => !c.IsLoading && _viewModel.ClipOf(c) is not null)
            .Select(c => (Clip: c, Cards: LaneCards(c, CountsAsCutout(_viewModel, c), settings))).ToList();
        var most = shown.Count == 0 ? 0 : shown.Max(c => c.Cards.Count);
        for (var k = 0; k < most; k++)
        {
            var row = EffectLaneRow(track, k);
            foreach (var (clip, cards) in shown)
            {
                if (k >= cards.Count || _viewModel.ClipOf(clip) is not { } model)
                    continue;

                var effect = cards[k];
                var locked = effect.Range is null;
                var (from, to) = effect.Range is { } range ? (model.Start + range.Start, model.Start + Math.Min(range.End, model.Duration)) : (model.Start, model.End);
                var start = model.Start;
                row.Blocks.Add(new TimelineBlock
                {
                    Left = from * scale,
                    Width = Math.Max((to - from) * scale, 3),
                    Name = effect.Definition?.Name ?? effect.Type,
                    Fill = effect.Enabled && !clip.IsHidden ? EffectClipBrush : OffClipBrush,
                    Clip = EffectBlockOf(clip, effect),
                    IsOn = effect.Enabled,
                    IsLocked = locked,
                    Switched = which => EffectSwitched(clip, effect, which),

                    // Locked: spans its clip and follows it. Unlocked: draggable and trimmable within the clip.
                    CanDrag = locked ? null : AlwaysDraggable,
                    Resizable = !locked,
                    TrimmableStart = !locked,
                    // Dragged a few pixels past its clip: detaches into a standalone Effect Clip.
                    Commit = locked ? null : (newLeft, newWidth) =>
                    {
                        if (LiveClip(clip) is not { } live)
                            return;
                        var (newFrom, newTo) = (newLeft / scale, (newLeft + newWidth) / scale);
                        var slack = 6 / Math.Max(scale, 1e-6);
                        if (newFrom < model.Start - slack || newTo > model.End + slack)
                            RunGuarded("Take an effect out of its clip", () => _viewModel.DetachEffect(live, effect, newFrom, newTo));
                        else
                            _viewModel.SetEffectRange(live, effect, newFrom - start, newTo - start);
                    },

                    // Released on a video track row: becomes an Effect Clip on that track.
                    CanMoveTo = locked ? null : (target, _) => target is Layer { IsAudio: false, IsCaptions: false, IsBackground: false },
                    MoveTo = locked ? null : (target, at) =>
                    {
                        if (target is Layer track && LiveClip(clip) is { } live)
                            RunGuarded("Take an effect out of its clip", () => _viewModel.DetachEffect(live, effect, at / scale, at / scale + (to - from), track));
                    },
                });
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Creates the row for a track's k-th effect lane (0-based) before EffectRows fills its blocks: Data, name, height, and
    /// <see cref="TimelineRow.HidesWhenClosed"/> so it takes no room until the stack opens.
    /// </summary>
    internal static TimelineRow EffectLaneRow(Layer track, int k) => new()
    {
        Key = ("effects", track.TrackId, k),
        Data = new EffectLane(track, k),
        Kind = TimelineRowKind.Video,
        Name = EffectLaneName(k),
        Height = 26,
        HidesWhenClosed = true,
        Accent = EffectClipBrush,
        Tip = "The effects of this track's clips, each block one effect over the stretch it works in. Its switches turn it on or off, and lock it to its clip or free it to a stretch of its own.",
    };

    /// <summary>
    /// Whether BuildTimelineRows adds effect lanes to video track stacks: false with <see cref="AppSettings.ShowTimelineEffectLanes"/> off
    /// (effects then show only as the "fx N" badge).
    /// </summary>
    internal static bool AdoptsEffectLanes(AppSettings settings) => settings.ShowTimelineEffectLanes;

    /// <summary>Name of effect lane k (0-based): "Effects", "Effects 2", "Effects 3", ...</summary>
    internal static string EffectLaneName(int k) => k == 0 ? "Effects" : string.Create(CultureInfo.InvariantCulture, $"Effects {k + 1}");

    // Effect block switch pressed: the card's On/Off or its Lock to the clip span.
    private void EffectSwitched(Layer clip, EffectInstance effect, BlockSwitch which)
    {
        if (LiveClip(clip) is not { } live)
            return;

        RunGuarded(which == BlockSwitch.Lock ? "Lock / Unlock an effect" : "Turn an effect on or off", () =>
        {
            if (which == BlockSwitch.Lock)
                _viewModel.SetEffectLocked(live, effect, effect.Range is not null);
            else
                _viewModel.SetEffectOn(live, effect, !effect.Enabled);
        });
        ScheduleClipRedraw();
    }

    // Effect block context menu: switches, open in Effects pane, remove.
    private void FillEffectMenu(ItemsControl menu, EffectBlock? block)
    {
        if (block is null || LiveClip(block.Clip) is not { } clip)
            return;

        var effect = block.Effect;
        AddTrackItem(menu, effect.Enabled ? "Turn Off" : "Turn On", () => _viewModel.SetEffectOn(clip, effect, !effect.Enabled));
        AddTrackItem(menu, effect.Range is null ? "Unlock from Clip (a Stretch of Its Own)" : "Lock to Clip (All of It)", () => _viewModel.SetEffectLocked(clip, effect, effect.Range is not null),
            tip: effect.Range is null ? "Gives it a stretch of its own in its clip: drag or trim its block along the clip." : "It works over the whole clip again, and goes with every trim and split of it.");
        AddTrackItem(menu, "Edit in the Effects Pane", () => SelectClip(clip));
        if (_viewModel.ClipOf(clip) is { } model && !effect.IsMissing)
        {
            var (from, to) = effect.Range is { } range ? (model.Start + range.Start, model.Start + Math.Min(range.End, model.Duration)) : (model.Start, model.End);
            AddTrackItem(menu, "Make an Effect Clip of Its Own", () => _viewModel.DetachEffect(clip, effect, from, to),
                tip: "Takes it out of this clip onto a track of its own, over the same stretch: an Effect Clip that works on everything under it.");
        }

        AddSeparator(menu);
        AddTrackItem(menu, "Remove Effect", () => _viewModel.RemoveEffect(clip, effect));
    }

    // Per-strip frame splits, cached for the strip's lifetime.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, ImageSource[]> StripFrames = [];

    /// <summary>
    /// A video block's thumbnails: the file's filmstrip split into evenly spaced frames, mapped in file time from
    /// <paramref name="mediaOffset"/> at <paramref name="speed"/> (the surface draws as many as fit, nearest frame each). Null until the
    /// strip exists and the frame size is known.
    /// </summary>
    /// <param name="pixelWidth">Source video width; strip frames share the source aspect ratio.</param>
    /// <param name="crop">A Cutout's source region (<see cref="CutoutFilmCrop"/>): each frame is cut to it; null or full keeps whole frames.</param>
    internal static FilmWindow? FilmOf(ImageSource? filmstrip, int pixelWidth, int pixelHeight, double mediaOffset, double mediaDuration, double speed,
        FilmCrop? crop = null)
    {
        if (filmstrip is not BitmapSource strip || strip.PixelHeight <= 0 || strip.PixelWidth <= 0 || pixelWidth <= 0 || pixelHeight <= 0
            || FramesOf(strip, (double)pixelWidth / pixelHeight) is not { } frames)
        {
            return null;
        }

        // The window records the region only when its frames were actually cut to it (TimelineBlock.FilmCrop never disagrees with them).
        FilmCrop? cutTo = null;
        if (crop is { IsFull: false } region && CroppedFramesOf(strip, frames.Length, region) is { } cut)
            (frames, cutTo) = (cut, region);

        // Unknown duration: show the first frame throughout.
        var perFrame = mediaDuration > 0.05 ? mediaDuration / frames.Length : 1e9;
        return new FilmWindow(frames, perFrame, Math.Max(mediaOffset, 0), speed, cutTo);
    }

    // Per-strip Cutout frame cuts by strip-pixel region, most recent first, at most MostCutsPerStrip kept for the strip's lifetime (UI
    // thread): a crop or mask drag makes a new region per move, so older cuts are dropped instead of piling up.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, List<(Int32Rect Area, ImageSource[] Frames)>> CutoutStripFrames = [];
    private const int MostCutsPerStrip = 8;

    /// <summary>
    /// Strip frames cut to a Cutout's region (FilmCrop.ToPixels: at least 16 x 16 strip pixels, aspect kept, so DrawFilm's slots take the
    /// region's shape); null when a frame cell is too small to cut.
    /// </summary>
    private static ImageSource[]? CroppedFramesOf(BitmapSource strip, int count, FilmCrop region)
    {
        var cell = strip.PixelWidth / Math.Max(count, 1);
        if (cell < 2)
            return null;

        var area = region.ToPixels(cell, strip.PixelHeight);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0)
            return null;

        var cuts = CutoutStripFrames.GetOrCreateValue(strip);
        var hit = cuts.FindIndex(c => c.Area == area && c.Frames.Length == count);
        if (hit >= 0)
        {
            var found = cuts[hit];
            cuts.RemoveAt(hit);
            cuts.Insert(0, found);
            return found.Frames;
        }

        var frames = new ImageSource[count];
        for (var i = 0; i < count; i++)
        {
            var frame = new CroppedBitmap(strip, new Int32Rect(i * cell + area.X, area.Y, area.Width, area.Height));
            frame.Freeze();
            frames[i] = frame;
        }

        cuts.Insert(0, (area, frames));
        if (cuts.Count > MostCutsPerStrip)
            cuts.RemoveRange(MostCutsPerStrip, cuts.Count - MostCutsPerStrip);
        return frames;
    }

    /// <summary>
    /// Pure (golden wave-contract-5-6-4a): the source region a Cutout's filmstrip shows, in source-frame fractions: its cropped source rect
    /// (<see cref="Layer.CroppedSource"/>) intersected with its drawn mask's outline bounds (user decision). Frame masks (MaskFrame) are
    /// drawn over the whole frame, others over the clip's source rect; inverted masks show everything outside, so only the crop counts.
    /// </summary>
    internal static FilmCrop CutoutFilmCrop(Layer clip)
    {
        var (x, y, width, height) = clip.CroppedSource;
        var crop = FilmCrop.FromRect(x, y, width, height);
        if (!clip.CustomMask || clip.MaskInvert || !Services.MaskStore.IsDrawn(Services.MaskStore.SourceOf(clip)))
            return crop;

        var (partX, partY, partWidth, partHeight) = clip.MaskFrame ? (0.0, 0.0, 1.0, 1.0) : (clip.SourceX, clip.SourceY, clip.SourceWidth, clip.SourceHeight);
        var outline = FilmCrop.Bounds(Services.MaskStore.ParseShapes(clip.MaskShapes), partX, partY, partWidth, partHeight,
            Layer.MaskOffset(clip.MaskOffsetX) / 100, Layer.MaskOffset(clip.MaskOffsetY) / 100);
        return outline is { } bounds ? crop.Intersect(bounds) : crop;
    }

    /// <summary>
    /// Strip frames split once and cached with the strip; the count derives from the strip's own size and the file's aspect, so a wrong
    /// aspect guess by a caller cannot leave a file frameless for the session.
    /// </summary>
    private static ImageSource[]? FramesOf(BitmapSource strip, double aspect)
    {
        var count = FrameCount(strip.PixelWidth, strip.PixelHeight, aspect);
        if (StripFrames.TryGetValue(strip, out var cut) && cut.Length == count)
            return cut;

        var width = strip.PixelWidth / count;
        if (width < 2)
            return null;

        var frames = new ImageSource[count];
        for (var i = 0; i < count; i++)
        {
            var frame = new CroppedBitmap(strip, new Int32Rect(i * width, 0, width, strip.PixelHeight));
            frame.Freeze();
            frames[i] = frame;
        }

        StripFrames.AddOrUpdate(strip, frames);
        return frames;
    }

    /// <summary>
    /// Frame count of a strip: frames sit side by side at strip height with the file's aspect (even-rounded width), so stripWidth /
    /// frameWidth is approximate; the nearest count that divides stripWidth exactly is the answer.
    /// </summary>
    private static int FrameCount(int stripWidth, int stripHeight, double aspect)
    {
        var guess = Math.Max((int)Math.Round(stripWidth / (stripHeight * aspect)), 1);
        if (stripWidth % guess == 0)
            return guess;
        if (stripWidth % (guess + 1) == 0)
            return guess + 1;
        return guess > 1 && stripWidth % (guess - 1) == 0 ? guess - 1 : guess;
    }

    /// <summary>Moves the surface and ruler playheads; the only per-frame timeline update during playback.</summary>
    private void MoveClipPlayheads()
    {
        var seconds = _viewModel.DurationMs / 1000;
        UnifiedTimeline.Surface.PlayheadSeconds = seconds > 0 ? _viewModel.PositionMs / 1000 : double.NaN;
        if (seconds <= 0)
            return;

        foreach (var ruler in _rulers)
            PlaceRulerPlayhead(ruler);
        FollowPlayhead();
    }

    // All caption blocks are outlined while the caption track is selected.
    private bool IsSelected(object? clip) => clip is null ? _playheadVideoSelected && _selectedClip is null
        : Equals(clip, _selectedClip) || (clip is Layer layer && GroupHolds && _viewModel.IsInClipGroup(layer))
        || clip is SubtitleRef && ReferenceEquals(_selectedClip, _viewModel.CaptionLayer);

    /// <summary>True while the multi-selection is current: the primary item is one of its clips (any other selection ends the group).</summary>
    private bool GroupHolds => _selectedClip is Layer primary && _viewModel.IsInClipGroup(primary);

    /// <summary>The multi-selection while current; else the single selected clip (if groupable), or empty.</summary>
    private IReadOnlyList<Layer> SelectedGroup()
    {
        if (GroupHolds)
            return _viewModel.SelectedClips;
        return _selectedClip is Layer one && _viewModel.CanGroup(one) ? [one] : [];
    }

    /// <summary>
    /// Selects one item (clip, clip audio; null = video at the playhead) and clears the multi-selection unless <paramref name="keepGroup"/>
    /// (the item is a group member being made primary).
    /// </summary>
    private void SelectClip(object? clip, bool keepGroup = false)
    {
        (_selectedClip, _playheadVideoSelected) = (clip, clip is null);

        // Keyframes/Effects target: a video clip on the timeline. Captions (no keyframes, few effects) and orphaned views are not; the
        // panes then keep their previous clip.
        _viewModel.KeyLayer = clip switch
        {
            Layer { IsPicture: true, IsCaptions: false } layer when _viewModel.Layers.Contains(layer) => layer,
            _ => _viewModel.KeyLayer,
        };

        // Blur fill row: no clip, but the Effects pane stays bound while its row or block is selected.
        _viewModel.BackgroundSelected = clip is Layer { IsBackground: true };

        // An audio clip targets only the Effects pane (Speed & Pitch, SOUND cards); the key layer stays the video used by Keyframes, keyframe
        // buttons, text tools and crop. Any other selection clears the audio target.
        EffectsView.SelectSound(clip as Layer);
        if (!keepGroup)
        {
            _rangeAnchor = null;
            _viewModel.ClearClipGroup();
        }

        // The surface draws the outline from the selection; the inspector shows its values and the player boxes outline it (captions
        // included, though they are never the key layer).
        UnifiedTimeline.Surface.Redraw();
        UpdateTimelineInspector();
        if (clip is Layer { IsCaptions: true })
            _playerOutlineHidden = false;
        UpdatePlayerSelection();
    }

    /// <summary>Clears the selection (after split, delete, undo); tools fall back to the video at the playhead.</summary>
    private void ClearClipSelection()
    {
        (_selectedClip, _playheadVideoSelected) = (null, false);
        _rangeAnchor = null;
        _viewModel.BackgroundSelected = false;
        _viewModel.ClearClipGroup();
        UpdateTimelineInspector();
        UpdatePlayerSelection();
    }

    // ----- Multi-selection -----

    /// <summary>Ctrl+click on an unselected clip: adds it (with the previous selection) and makes it primary.</summary>
    private void AddToClipGroup(Layer clip)
    {
        var group = SelectedGroup().ToList();
        group.Add(clip);
        _rangeAnchor = null;
        _viewModel.SetClipSelection(group);
        SelectClip(clip, keepGroup: true);
        CrashReporter.Note("timeline", $"select: {_viewModel.SelectedClips.Count} clips");
    }

    /// <summary>Ctrl+click on a selected clip: removes it; the most recently added remaining clip becomes primary.</summary>
    private void RemoveFromClipGroup(Layer clip)
    {
        var group = SelectedGroup().Where(c => !ReferenceEquals(c, clip)).ToList();
        _viewModel.SetClipSelection(group);
        if (group.Count == 0)
        {
            ClearClipSelection();
            UnifiedTimeline.Surface.Redraw();
            return;
        }

        SelectClip(ReferenceEquals(_selectedClip, clip) ? group[^1] : _selectedClip, keepGroup: true);
    }

    /// <summary>
    /// Shift+click: selects every clip in the time span (earlier start to later end) and track span between the anchor and the clicked
    /// clip, which becomes primary. With no anchor, selects the clicked clip alone.
    /// </summary>
    private void SelectClipRange(Layer clip)
    {
        var anchor = _rangeAnchor is { } held && (GroupHolds ? _viewModel.IsInClipGroup(held) : ReferenceEquals(held, _selectedClip)) ? held
            : _selectedClip as Layer;
        if (anchor is null || !_viewModel.CanGroup(anchor) || ReferenceEquals(anchor, clip))
        {
            SelectClip(clip);
            return;
        }

        var range = UnifiedTimeline.Surface.BlocksInRange(anchor, clip)
            .Select(b => b.Clip).OfType<Layer>().Where(_viewModel.CanGroup).ToList();
        range.Remove(anchor);
        range.Remove(clip);
        _viewModel.SetClipSelection([anchor, .. range, clip]);
        SelectClip(clip, keepGroup: true);
        _rangeAnchor = anchor;
        CrashReporter.Note("timeline", $"select: a range of {_viewModel.SelectedClips.Count} clips");
    }

    // No-move release on a block: applies the selection change deferred by the press.
    private void Timeline_BlockClicked(TimelineRow row, TimelineBlock block)
    {
        if (_clickGesture is not { } pending || !ReferenceEquals(pending.Clip, block.Clip))
            return;

        _clickGesture = null;
        switch (pending.Gesture)
        {
            case ClickGesture.Toggle:
                RemoveFromClipGroup(pending.Clip);
                break;
            case ClickGesture.Range:
                SelectClipRange(pending.Clip);
                break;
            case ClickGesture.Add:
                AddToClipGroup(pending.Clip);
                break;
            default:
                SelectClip(pending.Clip);
                break;
        }
    }

    // View-model multi-selection changed (edit removed clips, undo, new choice): refresh outlines.
    private void ClipGroup_Changed()
    {
        UnifiedTimeline.Surface.Redraw();
        UpdateTimelineInspector();
    }

    /// <summary>
    /// Commits a start trim, roll, slip or ripple released on a block (UnifiedTimelineControl) through the view model, which reports a
    /// refusal; rows rebuild either way. The clip is re-resolved by id and must match <paramref name="built"/>; each edge applies the block's
    /// delta, so an unmoved edge keeps the clip's exact frame.
    /// </summary>
    private void EditClip(Layer clip, (double Start, double End) built, BlockEditDone done, double left, double width, double perPixel)
    {
        if (LiveClip(clip, built) is not { } live)
        {
            ScheduleClipRedraw();
            return;
        }

        var start = Math.Abs(done.Left - left) <= 0.5 ? built.Start : done.Left * perPixel;
        var end = Math.Abs(done.Left + done.Width - (left + width)) <= 0.5 ? built.End : (done.Left + done.Width) * perPixel;
        switch (done.Kind)
        {
            case BlockEdit.TrimStart:
                CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: trim start{(done.Ripple ? ", ripple" : "")} {start - built.Start:+0.000;-0.000} s"));
                _viewModel.TrimClipStart(live, start, done.Ripple);
                break;
            case BlockEdit.TrimEnd when done.Ripple:
                CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: trim end, ripple {end - built.End:+0.000;-0.000} s"));
                _viewModel.RippleClipEnd(live, end - built.Start);
                break;
            case BlockEdit.Roll when done.Neighbor?.Clip is Layer next:
                if (LiveClip(next) is not { } after)
                    break;
                CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: roll {end - built.End:+0.000;-0.000} s"));
                _viewModel.RollCut(live, after, end);
                break;
            case BlockEdit.Slip:
                CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: slip {done.Slip * perPixel:+0.000;-0.000} s"));
                _viewModel.SlipClip(live, done.Slip * perPixel);
                break;
        }

        ScheduleClipRedraw();
    }

    /// <summary>
    /// Commits a horizontal move or right-edge trim: the re-resolved clip (must match the block's build) takes the released edges, each
    /// edge unchanged if it did not move (so a move never trims a clip drawn wider than its duration).
    /// </summary>
    private void CommitClip(Layer clip, (double Start, double End) built, double left, double width, double newLeft, double newWidth, double perPixel)
    {
        if (LiveClip(clip, built) is not { } live)
            return;

        var moved = Math.Abs(newLeft - left) > 0.5;
        var start = moved ? newLeft * perPixel : built.Start;
        var length = Math.Abs(newWidth - width) <= 0.5 ? built.End - built.Start : (newLeft + newWidth) * perPixel - start;
        CrashReporter.Note("timeline", moved
            ? string.Create(CultureInfo.InvariantCulture, $"drag: move {start - built.Start:+0.000;-0.000} s")
            : string.Create(CultureInfo.InvariantCulture, $"drag: trim end {length - (built.End - built.Start):+0.000;-0.000} s"));
        _viewModel.CommitClipMove(live, start, length);
    }

    /// <summary>Start time for a cross-row drop: the released position, or the original start if not moved horizontally.</summary>
    private static double StartOf((double Start, double End) built, double left, double at, double perPixel) =>
        Math.Abs(at - left) <= 0.5 ? built.Start : at * perPixel;

    /// <summary>Drag range of an edge at a folded dead-air seam (out unfolds, in folds more); null when the edge is at no seam.</summary>
    private (double Reveal, double Fold)? SeamRangeOf(Layer clip, bool startEdge) =>
        LiveClip(clip, quiet: true) is { } live && _viewModel.FoldAtEdge(live, startEdge) is not null ? _viewModel.SeamEdgeRange(live, startEdge) : null;

    /// <summary>Commits a dead-air seam edge drag of <paramref name="outward"/> seconds (negative = inward): unfolds or folds more, as one Undo step.</summary>
    private void TrimAtSeam(Layer clip, (double Start, double End) built, bool startEdge, double outward)
    {
        if (LiveClip(clip, built) is not { } live)
            return;

        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: {(startEdge ? "start" : "end")} edge at a seam {outward:+0.000;-0.000} s"));
        if (!_viewModel.TrimIntoSeam(live, startEdge, startEdge ? built.Start - outward : built.End + outward))
            CrashReporter.Note("timeline", "refused: an edge at a seam");
        ScheduleClipRedraw();
    }

    /// <summary>
    /// Cancels an active timeline drag (reverted, nothing committed) before a key, tool or menu edit changes its target; its release
    /// would otherwise edit a stale clip.
    /// </summary>
    private void LetGoOfDrag()
    {
        if (UnifiedTimeline.Surface.IsDragging)
        {
            UnifiedTimeline.Surface.AbortDrag();
            CrashReporter.Note("timeline", "drag let go: another edit came first");
        }
    }

    // ----- The editing tools: each works on whatever is selected -----

    /// <summary>Razor: splits the selected block at the playhead (empty selection = the video there).</summary>
    private void SplitSelectedClip()
    {
        LetGoOfDrag();
        var at = _viewModel.PositionMs / 1000;
        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"split at {at:0.000} s"));

        // Captions selected: split the caption under the playhead at that word instead of refusing.
        if (_selectedClip is Layer { IsCaptions: true })
        {
            SplitCaptionAt(at);
            return;
        }

        if (NotAClip(_selectedClip, "split") is { } refusal)
        {
            _viewModel.StatusText = refusal;
            return;
        }

        switch (_selectedClip)
        {
            case Layer layer when _viewModel.Layers.Contains(layer):
                _viewModel.SplitLayer(layer, at);
                break;
            case LayerSound sound when _viewModel.Layers.Contains(sound.Layer):
                _viewModel.SplitLayer(sound.Layer, at);
                break;
            default:
                // Empty selection: split the video at the playhead.
                _viewModel.SplitLayer(_viewModel.PlayheadVideo, at);
                break;
        }

        ClearClipSelection();
        ScheduleClipRedraw();
    }

    private void TrimSelectedClip(bool start)
    {
        // Clip audio trims with its clip; empty selection targets the video at the playhead.
        LetGoOfDrag();
        if (NotAClip(_selectedClip, "trimmed") is { } refusal)
        {
            _viewModel.StatusText = refusal;
            return;
        }

        var clip = _selectedClip is LayerSound sound ? sound.Layer : _selectedClip ?? _viewModel.PlayheadVideo;
        _viewModel.TrimClip(clip, start, _viewModel.PositionMs / 1000);
        ScheduleClipRedraw();
    }

    /// <summary>
    /// Refusal reason when the selection is not a clip (caption track, blur fill row); the playhead video is never substituted. Null for
    /// a clip or an empty selection.
    /// </summary>
    private static string? NotAClip(object? selected, string what) => selected switch
    {
        Layer { IsCaptions: true } => $"The captions are words, not a clip: they are not {what}. Their words are edited in the Subtitle Studio; select a clip to cut it.",
        Layer { IsBackground: true } => $"The blur fill's settings are not a clip: they are not {what}. Select a clip to cut it.",
        _ => null,
    };

    /// <summary>
    /// Delete: removes the selected clip, or every multi-selected clip, as one Undo step.
    /// </summary>
    private void DeleteSelectedClip()
    {
        // Crossfade selected: Delete removes it, keeping its clips.
        if (UnifiedTimeline.Surface.SelectedCrossfade is Layer faded)
        {
            UnifiedTimeline.Surface.SelectedCrossfade = null;
            if (LiveClip(faded) is { } live)
                _viewModel.SetCrossfade(live, 0);
            return;
        }

        LetGoOfDrag();
        if (GroupHolds)
        {
            if (_viewModel.DeleteClips(_viewModel.SelectedClips, ripple: false))
                ClearClipSelection();
            ScheduleClipRedraw();
            return;
        }

        switch (_selectedClip)
        {
            case LayerSound sound:
                _viewModel.StatusText = $"The sound belongs to {sound.Layer.Name}: delete or hide the clip to take it out.";
                return;
            case Layer { IsCaptions: true }:
                _viewModel.StatusText = "The captions are words, not a clip: right-click their row for Delete Subtitle Track, or turn off Burn Captions into Export.";
                return;
            default:
                if (!_viewModel.DeleteClip(_selectedClip))
                    return;
                break;
        }

        ClearClipSelection();
        ScheduleClipRedraw();
    }

    // ----- Clip clipboard: Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+D, Ctrl+Alt+V -----

    /// <summary>Clipboard targets: the multi-selection, or the single selection (clip audio maps to its clip).</summary>
    private IReadOnlyList<Layer> ClipboardSelection() => _selectedClip is LayerSound sound && !GroupHolds
        ? _viewModel.CanGroup(sound.Layer) ? [sound.Layer] : []
        : SelectedGroup();

    private void CopySelectedClips() => _viewModel.CopyClips(ClipboardSelection());

    /// <summary>Ctrl+X: copies the selected clips, then deletes them as Delete does, in one Undo step.</summary>
    private void CutSelectedClips()
    {
        if (_viewModel.CopyClips(ClipboardSelection()) > 0)
            DeleteSelectedClip();
    }

    private void DuplicateSelectedClips()
    {
        LetGoOfDrag();
        if (_viewModel.DuplicateClips(ClipboardSelection()))
            ScheduleClipRedraw();
    }

    /// <summary>Whether Alt+Up / Alt+Down apply: a selected video clip that can move to its own track.</summary>
    private bool CanMoveSelectedToNewTrack() => _viewModel.CanMoveClipsToNewTrack(ClipboardSelection());

    /// <summary>
    /// Moves the selected clips up or down a track: with <paramref name="ontoNeighbour"/> (keys) onto the adjacent track where free, else a
    /// new track; the clip menu always creates a new track. One Undo step; selection preserved.
    /// </summary>
    private void MoveSelectedToNewTrack(bool above, bool ontoNeighbour = false)
    {
        LetGoOfDrag();
        if (_viewModel.MoveClipsToNewTrack(ClipboardSelection(), above, ontoNeighbour))
            ScheduleClipRedraw();
    }

    /// <summary>Ctrl+Alt+V: applies the attributes chosen in the Paste Attributes dialog from the copied clip to the selected clips.</summary>
    private void PasteAttributesToSelection()
    {
        var targets = ClipboardSelection();
        if (_viewModel.CopiedAttributesName is not { } copied)
        {
            _viewModel.StatusText = "Copy a clip first (Ctrl+C): Paste Attributes gives its look to the selected clips.";
            return;
        }

        if (targets.Count == 0)
        {
            _viewModel.StatusText = "Select the clips to paste attributes onto first.";
            return;
        }

        var dialog = new PasteAttributesDialog(copied, targets.Count) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.PasteAttributes(targets, dialog.Chosen);
        ScheduleClipRedraw();
    }

    // Undo/Redo cancel an active drag first (its release would commit onto the restored timeline).
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        LetGoOfDrag();
        _viewModel.Undo();
        ClearClipSelection();
        ScheduleClipRedraw();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        LetGoOfDrag();
        _viewModel.Redo();
        ClearClipSelection();
        ScheduleClipRedraw();
    }

    // Raised when either chain button toggles (click, key or screen reader); a no-op when one button merely mirrors the other.
    private void LinkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not System.Windows.Controls.Primitives.ToggleButton button || (button.IsChecked == true) == _viewModel.AudioLinked)
            return;

        _viewModel.SetAudioLinked(button.IsChecked == true);

        // Reflect the actual state, not the click: the confirmation may have been declined.
        if ((button.IsChecked == true) != _viewModel.AudioLinked)
            button.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, _viewModel.AudioLinked);
        ScheduleClipRedraw();
    }
}
