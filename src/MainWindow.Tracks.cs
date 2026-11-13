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

namespace HandPegApp;

// ----- Tracks: the timeline's blocks, the editing tools (split, trim, delete, undo) and a track's right-click menus -----
public partial class MainWindow
{
    // ----- Blocks in time -----
    // Every row of the timeline (4.4: one drawn surface, UnifiedTimelineControl; its rows are made in
    // MainWindow.UnifiedTimeline.cs) has what it stands for as blocks: a track's clips, each from when it appears to when
    // it goes (5.2-AuditB: a file's streams are Sub-Lanes of sound clips like any other); a video clip's own sound, while it
    // is unlinked from its picture; the captions. Everything is placed by one sum:
    // seconds times the surface's pixels per second. While a block is being dragged only that block is moved; what it
    // stands for is changed when it is let go, which is also when everything else (the command, the player, Undo) hears of it.
    //
    // The rows are made again after a change, and when the zoom changes the scale; as the video plays only the playhead
    // slides. The pictures in a block (its file's frames, its sound) are cut from pictures already in memory and laid on
    // the file's own time, so they stay where they are in the file as the block is trimmed, and slide as it is slipped.

    private static readonly Brush LayerClipBrush = Frozen(Color.FromArgb(0xD0, 0x8E, 0x5A, 0xB8));
    private static readonly Brush LayerAudioBrush = Frozen(Color.FromArgb(0xD0, 0x3C, 0x9A, 0x5F));
    private static readonly Brush SoundClipBrush = Frozen(Color.FromArgb(0xD0, 0x2B, 0x7A, 0x6B));
    private static readonly Brush OffClipBrush = Frozen(Color.FromArgb(0xC0, 0x55, 0x55, 0x55));
    private static readonly Brush EffectClipBrush = Frozen(Color.FromArgb(0xD0, 0x5E, 0x4B, 0xC4));
    private static readonly Brush SubtitleClipBrush = Frozen(Color.FromArgb(0xD0, 0x2E, 0x7D, 0x6B));
    private static readonly Brush MainUnderBrush = Frozen(Color.FromArgb(0x48, 0x2F, 0x6F, 0xB5));

    /// <summary>Where a clip's keyframes fall along its block, in pixels; nothing while their diamonds are switched off.</summary>
    /// <param name="shift">How far the moment the keyframes are counted from lies from the start of the block, in seconds.</param>
    private IEnumerable<double>? ClipKeys(Layer clip, double shift, double scale) =>
        _viewModel.ShowClipKeyframes && clip.IsAnimated ? clip.KeyTimes.Select(t => (t + shift) * scale) : null;

    /// <summary>A video layer's own sound, as something that can be selected.</summary>
    private sealed record LayerSound(Layer Layer);

    // What is selected: a Layer (one clip, or the blur fill's settings row), a LayerSound.
    // 5.0: clips selected together (Ctrl + click, Shift + click) are the view model's SelectedClips; this is the primary one of
    // them, what the inspector, the Crop boxes and the Keyframes pane show. The group holds only while it does.
    private object? _selectedClip;

    // Nothing picked out, which stands for the video clip at the playhead (SelectClip(null)).
    private bool _playheadVideoSelected;
    private bool _clipRedrawQueued;

    // What a press on a block left to its click (5.0): a Ctrl or Shift click on a clip already selected, or a plain click on
    // one of a group (narrowing it to that clip), decided as it is let go without moving; a drag of the group goes on instead.
    // 5.0-Verify (the user's Q2, A): Ctrl on the edge of a clip not selected is a ripple trim of it alone when dragged, and
    // adds it to the selection only when let go where it was (Add).
    private enum ClickGesture { Toggle, Range, Narrow, Add }

    private (Layer Clip, ClickGesture Gesture)? _clickGesture;

    // Where a Shift + click range starts: the clip selected before the first Shift + click (held while the group is its own).
    private Layer? _rangeAnchor;

    // The rows on the surface are older than the timeline (an edit, an undo, a change of scale since they were made): made
    // again at background priority, or at once by the next press on the timeline or the end of a drag (4.4 BugHunt).
    private bool _rowsStale;

    // A block was pressed, with either button: it is what is selected now (a right-click selects it too, so that the menu that opens is about it).
    // 5.0: Ctrl + click adds a clip to the selection (or, clicked again, takes it out), Shift + click selects every clip from
    // the one selected to it, across time and tracks; a press on one of a group keeps the group (dragged, it all goes).
    private void Timeline_BlockPressed(TimelineRow row, TimelineBlock block, MouseButtonEventArgs e)
    {
        _clickGesture = null;

        // An effect's block (5.2): its clip, selected, with its effects in the Effects pane; an unlocked one is dragged as it is pressed.
        if (block.Clip is EffectBlock effectBlock)
        {
            if (LiveClip(effectBlock.Clip) is { } owner && !IsSelected(owner))
                SelectClip(owner);
            return;
        }

        // A subtitle on the captions' row is not a clip of its own: a press on any of them selects the captions (their place,
        // size and style in the inspector, their box over the player and on the Canvas; 5.0-Verify, the user's bug 3). Their
        // words and times are the Subtitle Studio's: a double-click opens it.
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
                // Not selected yet: in at once, so that a drag that follows takes it along. Selected: decided as it is let go.
                // Ctrl on an edge is a ripple trim of the clip alone: it joins the selection only if let go without a drag.
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
                // One of the group: the group stays (a drag takes it all, a right-click's menu is about it all); it is the
                // one the panes show. Clicked and let go without a drag, it alone is selected.
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

    /// <summary>A subtitle drawn on the captions' row.</summary>
    private sealed record SubtitleRef(double Start);

    // A click on a row beside its blocks, or below every row, moves the playhead to that moment.
    private void Timeline_EmptyPressed(TimelineRow? row, double seconds, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(seconds * 1000, 0, _viewModel.DurationMs);
    }

    /// <summary>
    /// Lays the timeline's rows out again once, however many things have just changed (never under a drag: its end lays
    /// them out). Until then they are owed: the next press on the timeline lays them out first (<see cref="FlushClipRedraw"/>).
    /// </summary>
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
    /// Lays the timeline's rows out now when they are owed (4.4 BugHunt): before a press on the timeline is looked at (the
    /// queued layout waits behind input, so a clip split a moment ago would still be one block under the pointer, and its
    /// split-off part dragged as the whole), and as a drag ends. Never under a drag.
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
            // Said and logged once, and tried again at the next change: never a loop of failing layouts.
            _rowsStale = false;
            AppLog.Write($"Timeline: laying out the rows failed: {ex}");
            CrashReporter.Note("timeline", $"rows failed: {ex.GetType().Name}");
            _viewModel.StatusText = "The timeline could not be drawn as it is now (the details are in the log). The next change draws it again.";
        }
    }

    /// <summary>
    /// Runs what a timeline menu item or tool does (4.4 BugHunt): a fault in it becomes a status line and a note in the
    /// log, never an exception that ends the app; the rows are laid out again from the timeline either way.
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

    /// <summary>A fault in an edit made on the timeline: logged (with where), noted for a crash report (its kind only), said in the status bar.</summary>
    private void TimelineFault(string what, Exception ex)
    {
        AppLog.Write($"Timeline: {what} failed: {ex}");
        CrashReporter.Note("timeline", $"failed: {ex.GetType().Name}");
        _viewModel.StatusText = "Something went wrong with that edit, so it was stopped (the details are in the log). If anything changed, Undo puts it back.";
        ScheduleClipRedraw();
    }

    /// <summary>
    /// The view of the clip a block was made for, as the timeline has it now (4.4 BugHunt): found again by its id (an undo
    /// makes every view afresh), and, given where the block showed it (<paramref name="built"/>, seconds), still there. Null
    /// when it is gone or was changed meanwhile (an undo, another edit), or while a step is being put back: the status says
    /// why (unless <paramref name="quiet"/>) and nothing is edited.
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

    // Within this of the parent's edge (seconds), a child shares it: the model's own reach.
    private const double FamilySlack = 1e-6;

    /// <summary>
    /// Where a clip's block stands in its family (4.4 BugHunt), in seconds from the model: known by the clip's id; its
    /// parent's id while that parent is on the timeline; whether a move of the parent carries it (it overlaps the parent, on
    /// a track that is not locked: the model's own rule); which of the parent's edges it shares.
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

    // Every block of a clip can be dragged: this says so, once for all of them.
    private static readonly Func<bool> AlwaysDraggable = static () => true;

    /// <summary>
    /// Fills a row of the timeline with its blocks, at the surface's scale (pixels per second), by what the row stands for
    /// (its Data): the clips of a track (a video or sound track), the captions, or the blur fill's settings row. Returns the
    /// blocks of the track's clips' sound that is unlinked from their pictures (each dragged on its own), for a lane of their own
    /// under the row; null when there is none.
    /// </summary>
    private List<TimelineBlock>? BuildRowBlocks(TimelineRow row, double scale, double seconds)
    {
        row.Blocks.Clear();
        if (scale <= 0 || seconds <= 0)
            return null;

        var perPixel = 1 / scale;
        switch (row.Data)
        {
            // The captions: each from when it is shown to when it goes. Not dragged here: they follow the sound they were
            // heard in through every cut, and are reworded and retimed in the Subtitle Studio (right-click, Edit Captions...).
            case Layer { IsCaptions: true }:
                foreach (var (from, to, text, _) in _viewModel.GetSubtitleSegments())
                    row.Blocks.Add(new TimelineBlock { Left = from * scale, Width = Math.Max((to - from) * scale, 3), Name = text, Fill = SubtitleClipBrush, Clip = new SubtitleRef(from) });

                return null;

            // The blur fill's settings (5.2-AuditB: each video's own, this row their look): for as long as the sequence. Selected,
            // the inspector shows its blur.
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

            // An empty track: nothing on it yet (its row says to drop something there).
            case Layer { IsEmptyTrack: true }:
                return null;

            // A track: its clips, each from when it appears to when it goes (4.3C): moved, trimmed from either edge, rolled,
            // slipped, rippled, put on another track, on any track.
            case Layer layer:
                return AddTrackBlocks(row, layer, scale, perPixel, seconds);
        }

        return null;
    }

    /// <summary>The blocks of a track's clips on its row; returns the blocks of their unlinked sound (see BuildRowBlocks).</summary>
    private List<TimelineBlock>? AddTrackBlocks(TimelineRow row, Layer layer, double scale, double perPixel, double seconds)
    {
        List<TimelineBlock>? sounds = null;
        var clipsOnTrack = _viewModel.GetTrackClips(layer);
        var settings = AppSettings.Current;
        var gainLines = settings.ShowClipGainLines;

        // The Timeline Settings switches (5.3): the blocks' frames and their waveforms (a sound clip's, a video clip's own, an unlinked
        // sound's, and so the closed sound stack's one bar, which draws its lanes' sound) are left off the blocks while they are off.
        var (filmstrips, waveforms) = (settings.ShowClipFilmstrips, settings.ShowClipWaveforms);

        // A waveform is as tall as it is heard (5.3-Stage2): its clip's gain and its track's together, as the sound engine mixes it, so a
        // lane's own waveform and its share of its video's (StackSound) agree.
        var trackGainDb = _viewModel.TrackOfRow(layer)?.GainDb ?? 0;
        foreach (var clip in clipsOnTrack)
        {
            var (from, to) = clip.GetSpan(seconds);
            var duration = to - from;
            var speed = ClipSpeed(clip);
            var linked = _viewModel.IsClipAudioLinked(clip);
            var slip = linked ? 0 : _viewModel.ClipAudioSlip(clip);
            var split = _viewModel.ClipAudioSplit(clip);

            // Its sound is drawn in its block while it is linked to its picture (a sound clip's always): a video clip's own.
            // Unlinked, it has a block of its own (below).
            var showsSound = !clip.IsHidden && (clip.IsAudio || (linked && _viewModel.ShowLinkedAudio && clip.CarriesSound));
            var gainLine = showsSound && !clip.IsLoading && gainLines;
            var name = clip.IsLoading ? "Loading..." : clip.IsHidden ? "hidden" : clip.IsAudio ? clip.Name : "";

            // The clip as the timeline has it (4.4 BugHunt): what its block is known by and carries along in a drag, and
            // where it was when the block was made. A let-go edits the clip as it is then (found by its id), only while it is
            // still where the block showed it, and measures from the block's change: a block drawn wider than its clip (3 px
            // at least) never stretches it.
            var model = _viewModel.ClipOf(clip);
            (double Start, double End) built = model is null ? (from, to) : (model.Start, model.End);
            var (left, width) = (from * scale, Math.Max(duration * scale, 3));
            row.Blocks.Add(new TimelineBlock
            {
                Left = left,
                Width = width,
                Name = name,
                Fill = clip.IsLoading || clip.IsHidden ? OffClipBrush : clip.IsAudio ? SoundClipBrush : LayerClipBrush,
                Clip = clip,
                Family = FamilyOf(model),

                // Its frames, from its file's strip, and its sound: both laid on the file's own time.
                Film = clip.IsHidden || !filmstrips ? null
                    : clip.IsVideoFile && !clip.IsLoading ? FilmOf(clip.Filmstrip, clip.ImageWidth, clip.ImageHeight, clip.MediaOffset, clip.MediaDuration, speed)
                    : null,
                SoundWindow = showsSound && waveforms ? SoundOf(clip.Waveform, clip.MediaOffset, duration, speed, clip.GainDb + trackGainDb) : null,
                Keys = ClipKeys(clip, 0, scale)?.ToList(),
                Beats = BeatTicks(model, from, scale),
                Badge = BadgeOf(speed, slip, BadgeEffects(clip, CountsAsCutout(_viewModel, clip), settings)),
                CanDrag = AlwaysDraggable,
                Resizable = true,

                // Its own switches (5.2): On/Off for this clip alone (its children keep theirs), and a child's Lock to its parent clip.
                IsOn = model is null || clip.IsLoading ? null : _viewModel.IsClipOn(clip),
                SwitchIsSound = clip.IsAudio,
                IsLocked = model is null || clip.IsLoading ? null : _viewModel.ClipLock(clip),
                Switched = which => BlockSwitched(clip, which),

                // Let go a little over the clip it meets: a crossfade instead (see CommitClipMove).
                Commit = (newLeft, newWidth) => CommitClip(clip, built, left, width, newLeft, newWidth, perPixel),

                // Its left edge, the cut it shares with the next clip (a roll), Alt inside it (a slip), Ctrl on an edge (a ripple).
                TrimmableStart = clip.Duration > 0.001 && !clip.IsLoading,
                Slippable = clip.Duration > 0.001 && (clip.IsVideoFile || clip.IsAudio),
                Edit = done => EditClip(clip, built, done, left, width, perPixel),
                Gain = gainLine ? (clip.GainDb, clip.FadeInSeconds, clip.FadeOutSeconds) : null,
                GainEdit = done =>
                {
                    if (LiveClip(clip) is { } live)
                        _viewModel.SetClipGain(live, done.GainDb);
                },

                // Its fades, at its top corners: picture and sound, over whatever is under it. And a crossfade from
                // the clip before it on its track, as a pill across their cut.
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

                // Dragged up or down onto another row: onto that track (an empty one, or one of its kind with room). A sound (5.2: an
                // unpacked stream too) also onto a video row: a lane of its own in that track's audio stack, at its own time.
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

                // Dragged to a seam above, below or between the picture tracks (5.4A): onto a new track there, its family with it.
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

                // Let go on the bottom edge of a clip it overlaps on another lane (5.5A, the tether; with Alt pressed during the drag, anywhere
                // on it): that clip's child, at the time it was let go. The pill names the parent (a picture block's own Name is empty).
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

                // A J/L cut: its sound reaching out past its picture's edges; Alt on the lower half of an edge makes or changes one.
                AudioSplit = split is { } cut && (Math.Abs(cut.LeadIn) > 1e-6 || Math.Abs(cut.TailOut) > 1e-6) ? cut : null,
                AudioSplitEdit = split is null || clip.IsLoading ? null : (lead, tail) =>
                {
                    if (LiveClip(clip) is { } live)
                        _viewModel.SetClipAudioSplit(live, lead, tail);
                },

                // An edge at a seam of folded dead air (Q11): dragged outward it shows the fold again, inward it folds more.
                SeamRange = model is null || clip.IsLoading ? null : startEdge => SeamRangeOf(clip, startEdge),
                SeamTrim = model is null || clip.IsLoading ? null : (startEdge, outward) => TrimAtSeam(clip, built, startEdge, outward),
            });

            // A video clip's own sound, unlinked from its picture: starting where its picture does, or wherever it was slipped
            // to. It goes with its picture in a drag of it (moved, or trimmed with either edge).
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
                    SoundWindow = waveforms ? SoundOf(clip.Waveform, clip.MediaOffset, duration, speed, clip.GainDb + trackGainDb) : null,
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
    /// The stretch of a sound a block shows, by time: its file's sound from <paramref name="mediaFrom"/> on, for
    /// <paramref name="seconds"/> of the timeline at <paramref name="speed"/>, drawn as tall as its gain makes it. Null while
    /// the file's sound is not known yet (it is drawn when it is: the rows are made again then).
    /// </summary>
    private static WaveWindow? SoundOf(ImageSource? picture, double mediaFrom, double seconds, double speed, double gainDb) =>
        Waveforms.DataOf(picture) is { } data ? new WaveWindow(data, mediaFrom, mediaFrom + Math.Max(seconds, 0) * speed, speed, DecibelScale(gainDb)) : null;

    /// <summary>A gain in decibels as how many times as tall its sound is drawn (1 for 0 dB), within reason.</summary>
    private static double DecibelScale(double gainDb) => Math.Clamp(Math.Pow(10, gainDb / 20), 0, 8);

    // ----- A video's sound stack as one waveform (5.3-Stage2) -----
    // A video clip with a sound stack (its file's streams on Sub-Lanes, its unlinked sound) shows what that stack plays as one
    // waveform across its block (the user's call, where 5.1 showed its pictures alone): its audible lanes' sound combined, the most
    // any of them reaches (WaveformData.Combine), each at its clip's and its track's gain. Worked out on the rows once they are in
    // their stacks, by the rules the closed stack's one bar draws by: a lane not muted (its own switch, or its video track's master
    // mute), a block with a sound. A parent hidden or switched off still shows it: its lanes keep playing (its switch and its track's
    // eye leave its Sub-Lanes' sound as it is). Combined on the parent's own file time: with every lane under the whole of its clip,
    // every clip of one file shares one combined sound across splits and zooms while the gains and shifts stay the same, and its
    // waveform tiles are drawn again after an edit, never made again; with a lane under part of its clip, the combined sound is that
    // clip's stretch of the file alone (Law 2: no clip holds a whole file's sound of its own).

    // The combined sounds by what they were made of: those the last layout of the rows drew (looked in first, so a timeline of more
    // files than the few kept never makes them all again at every edit) and those this one draws, and a few kept besides (an hour of
    // one is about 2.6 MB, Law 2).
    private static Dictionary<StackSoundKey, WaveformData> _stackSoundsDrawn = [], _stackSoundsDrawing = [];
    private static readonly LruCache<StackSoundKey, WaveformData> StackSounds = new(12);

    /// <summary>
    /// One part of a combined stack sound as it is kept: its peaks (by identity), its gain in thousandths, its shift in pairs, and the
    /// stretch of its own pairs it plays (from First up to End; int.MinValue and int.MaxValue for all of it, a lane under the whole block).
    /// </summary>
    private readonly record struct StackSoundPart(WaveformData Data, int GainMilli, int ShiftPairs, int First, int End);

    /// <summary>
    /// What a combined stack sound is made of, part by part in order, and the stretch of the parent's file it covers in its pairs (From up
    /// to To; null for all of it, while every part is a lane under the whole block): two are the same when every part and the stretch are.
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
    /// Gives each video block with a sound stack what that stack plays, as one sound (<see cref="TimelineBlock.StackSound"/>), once the
    /// rows are in their stacks (BuildTimelineRows, while the waveforms and Show Linked Audio are on): from the blocks of its audible
    /// lanes, each found by its Family's parent (its clip's parent clip), at <paramref name="scale"/> pixels a second on a sequence of
    /// <paramref name="seconds"/>. Every mute, gain or waveform that changes lays the rows out again (ScheduleClipRedraw), so this
    /// follows them with nothing of its own to watch. A parent hidden or switched off keeps its stack sound (its lanes still play). What
    /// the last pass drew and this one does not is let go (no rows: all of it, an empty timeline's too).
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
            // The audible lanes' blocks, by the clip they belong to: a lane not muted (a muted video track mutes them all), each block
            // with a sound and not muted by its own switch (an unlinked sound's).
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

            // Each video block whose clip they belong to, hidden or switched off as well (its lanes are heard all the same).
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
    /// A video block's stack sound (<see cref="TimelineBlock.StackSound"/>): its lanes' sounds combined on its own file time, its file
    /// from <paramref name="mediaFrom"/> on for <paramref name="duration"/> seconds of the timeline at <paramref name="speed"/>, the block
    /// starting at <paramref name="start"/> (seconds). Each child is where its block starts (seconds) and the sound it shows. One at
    /// another speed is left out (never resampled: a file's streams take their video's speed), and so is one playing nowhere under the
    /// block. While every lane is under the whole block, the combined sound is of their whole files, so every clip of a file shares one;
    /// with one under part of it, the combined sound covers only the block's own stretch of its file (laid from there), each lane cut to
    /// what it plays in it, so no clip holds a whole file's for itself (Law 2). The same parts give the same combined sound, kept
    /// (StackSounds). Null for nothing audible.
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

            // The parent's file second s is the lane's own s + shift; where the lane plays, on the parent's file time.
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

        // With a lane under part of the block, the block's own stretch of its file alone: from its pair at mediaFrom up to the one at
        // mediaTo (counted at the first part's rate), the combined pair 0 being the first of them.
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
    /// A stack sound made: each part at its gain and shift; with a stretch of the parent's file (the key's Window: a lane under part of
    /// its block), each part cut first to the pairs of its own stretch that play in it.
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

            // Its pairs from first up to end (all of them for a lane under the whole block) that play in the stretch: the combined
            // pair i is the parent's pair window.From + i, its own pair window.From + shift + i. One at another rate than the stretch
            // was counted at is left out, as Combine leaves it out.
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

    /// <summary>How many times as fast as recorded a clip runs; 1 for a clip that has no speed of its own.</summary>
    private static double ClipSpeed(Layer clip) => clip.HasSpeed ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    /// <summary>
    /// A block's tag: its speed when that is not 1 ("2x"), how far its unlinked sound was slipped ("+120 ms"), and how many effects it
    /// has (5.2: "fx 2", its effect lanes shown or not; 5.3: the cards its lanes would show, <see cref="BadgeEffects"/>); null for none.
    /// </summary>
    internal static string? BadgeOf(double speed, double slip, int effects = 0)
    {
        var speedText = Math.Abs(speed - 1) > 0.001 ? string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x") : null;
        var slipText = Math.Abs(slip) >= 0.0005 ? string.Create(CultureInfo.InvariantCulture, $"{(slip > 0 ? "+" : "-")}{Math.Abs(slip) * 1000:0} ms") : null;
        var fxText = effects > 0 ? string.Create(CultureInfo.InvariantCulture, $"fx {effects}") : null;
        var parts = new[] { speedText, slipText, fxText }.Where(p => p is not null);
        return string.Join("  ", parts) is { Length: > 0 } text ? text : null;
    }

    // ----- Which cards a clip's effect lanes and its badge show (5.3) -----
    // Every Cutout is made with two cards of its own, its Mask and its Rounded & Feathered Edges (Layer.EnsureEffect, as its mask and its
    // feather go on): on the lanes they were two thin purple bars under every Cutout of a Game Preset, saying nothing (the user's report).
    // So a Cutout's own two are left off its lanes and its "fx N" badge unless Show Built-In Mask & Edge Lanes on Cutouts is on; they stay
    // in the Effects pane, as editable as ever. The same cards on any other clip (a Mask a user put on a video clip) are its effects.

    /// <summary>A Cutout's own structural cards (5.3): its mask and its rounded / feathered edges, which every Cutout is made with.</summary>
    internal static bool IsStructuralCard(EffectInstance effect) => effect.Type is EffectRegistry.Mask or EffectRegistry.Edges;

    /// <summary>
    /// Whether a clip is a Cutout for its effect lanes (5.3): a video clip of the same file as its parent clip, or one unlocked from it and
    /// still in its stack (MainViewModel.IsCutoutOrFreed: its Lock switch, or a trim that left it outside its clip, unlinks it, and its own
    /// Mask and Edges must not come back as lanes and "fx 2" for that), or a piece cut from the video's frame with a mask of the whole frame
    /// (Layer.UsesFrameMask).
    /// </summary>
    internal static bool CountsAsCutout(MainViewModel viewModel, Layer clip) => viewModel.IsCutoutOrFreed(clip) || clip.UsesFrameMask;

    /// <summary>
    /// The cards a clip's effect lanes show (5.3), in its stack's order: none while the Timeline's effect lanes are off, none for a sound
    /// or the captions; a Cutout's own Mask and Edges only with <see cref="AppSettings.ShowBuiltInMaskEffectLanes"/>. The k-th lane of a
    /// track shows each clip's k-th card of this list.
    /// </summary>
    internal static List<EffectInstance> LaneCards(Layer clip, bool isCutout, AppSettings settings) =>
        !settings.ShowTimelineEffectLanes || clip.IsAudio || clip.IsCaptions ? []
        : clip.Effects.Where(effect => ShowsCard(effect, isCutout, settings.ShowBuiltInMaskEffectLanes)).ToList();

    /// <summary>
    /// How many effects a clip's "fx N" badge counts (5.3): the cards its lanes would show were they on, so a plain Cutout shows no
    /// badge unless its own Mask and Edges have lanes; the badge stays while the lanes are off.
    /// </summary>
    internal static int BadgeEffects(Layer clip, bool isCutout, AppSettings settings) =>
        clip.IsAudio || clip.IsCaptions ? 0 : clip.Effects.Count(effect => ShowsCard(effect, isCutout, settings.ShowBuiltInMaskEffectLanes));

    // A card a clip's lanes show: one HandPeg knows, and not a Cutout's own Mask or Edges unless those are asked for.
    private static bool ShowsCard(EffectInstance effect, bool isCutout, bool builtIns) => !effect.IsMissing && (builtIns || !isCutout || !IsStructuralCard(effect));

    // A block's own switch pressed (5.2): its On/Off, this clip's alone; a child's Lock to its parent clip.
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

    // ----- The effects' lanes (5.2) -----

    /// <summary>A track's effect lane: what its row stands for (the track's row, and which of each clip's cards it shows).</summary>
    internal sealed record EffectLane(Layer Track, int Index);

    /// <summary>One effect card of a clip, as its block on an effect lane: the same object for as long as the card is, so a block made again is found again.</summary>
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
    /// A picture track's effects as lanes in its stack (5.2, the user's call: an effect added to a clip shows on the Timeline above it, like
    /// its Cutouts): each clip's first card on the first lane, its second on the second, each over the stretch it works in (its clip's
    /// span; once unlocked a stretch of its own, dragged and trimmed along the clip). Its On/Off is the card's, its Lock frees it to that
    /// stretch. A crossfade on the clips' row is not on these lanes: it never stands between a click and an effect.
    /// 5.3 (the user's call): the lanes take no room until the track's own stack is opened (HidesWhenClosed), there are none while the
    /// Timeline's effect lanes are switched off, and a Cutout's own Mask and Edges cards are not on them unless asked for (LaneCards).
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

                    // Locked it is its clip's span, moved and trimmed with it; unlocked its block is dragged and trimmed along the clip.
                    CanDrag = locked ? null : AlwaysDraggable,
                    Resizable = !locked,
                    TrimmableStart = !locked,
                    // Dragged past its clip (more than a few pixels), it leaves it: an Effect Clip of its own (5.2-Audit, the user's Q2).
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

                    // Or let go on a picture track's row: an Effect Clip on that track.
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
    /// A track's k-th effect lane (from 0) as its row, before EffectRows puts the clips' cards on it as blocks: what it stands for, its name
    /// and height, and that it takes no room until the track's own stack is opened (5.3, the user's call: <see cref="TimelineRow.HidesWhenClosed"/>).
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
    /// Whether the Timeline's picture tracks take their effect lanes into their stacks (5.3, BuildTimelineRows): not while the effect lanes are
    /// switched off (<see cref="AppSettings.ShowTimelineEffectLanes"/>), when a clip's effects show only as the "fx N" badge on its block.
    /// </summary>
    internal static bool AdoptsEffectLanes(AppSettings settings) => settings.ShowTimelineEffectLanes;

    /// <summary>A track's k-th effect lane's name (from 0): "Effects", then "Effects 2", "Effects 3"...</summary>
    internal static string EffectLaneName(int k) => k == 0 ? "Effects" : string.Create(CultureInfo.InvariantCulture, $"Effects {k + 1}");

    // An effect block's switch pressed: the card's On/Off, or its Lock to its clip's span.
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

    // An effect block's menu (5.2): its switches, the Effects pane, and taking it away.
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

    // The single frames of a filmstrip, cut from it once and kept for as long as the strip is.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, ImageSource[]> StripFrames = [];

    /// <summary>
    /// A video block's pictures: its file's strip cut into its frames, which lie evenly along the whole file, laid on the
    /// file's own time from <paramref name="mediaOffset"/> on at <paramref name="speed"/> (the surface draws as many as fit,
    /// each the frame nearest that moment). Null while there is no strip yet, or while the file's shape is not known.
    /// </summary>
    /// <param name="pixelWidth">The video's own size: a frame of the strip has its shape.</param>
    private static FilmWindow? FilmOf(ImageSource? filmstrip, int pixelWidth, int pixelHeight, double mediaOffset, double mediaDuration, double speed)
    {
        if (filmstrip is not BitmapSource strip || strip.PixelHeight <= 0 || strip.PixelWidth <= 0 || pixelWidth <= 0 || pixelHeight <= 0
            || FramesOf(strip, (double)pixelWidth / pixelHeight) is not { } frames)
        {
            return null;
        }

        // A file whose length is not known shows its first frame throughout.
        var perFrame = mediaDuration > 0.05 ? mediaDuration / frames.Length : 1e9;
        return new FilmWindow(frames, perFrame, Math.Max(mediaOffset, 0), speed);
    }

    /// <summary>
    /// The frames of a strip, cut once by their count and kept with it (4.4: cut by the strip's own size and the file's shape,
    /// so a caller that once guessed the shape wrong can no longer leave a file with no frames for the session).
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
    /// How many frames a strip holds: they lie side by side, each of the file's shape at the strip's height (rounded to an
    /// even width), so the strip's width over a frame's gives the count to within a fraction, and the strip's width is a
    /// whole number of frames: the nearest count that divides it is the one.
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

    /// <summary>Slides the playheads (the surface's, the ruler's): nothing else changes as the video plays, and nothing else is drawn again.</summary>
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

    // Every caption block is outlined while the captions are what is selected (5.0-Verify).
    private bool IsSelected(object? clip) => clip is null ? _playheadVideoSelected && _selectedClip is null
        : Equals(clip, _selectedClip) || (clip is Layer layer && GroupHolds && _viewModel.IsInClipGroup(layer))
        || clip is SubtitleRef && ReferenceEquals(_selectedClip, _viewModel.CaptionLayer);

    /// <summary>Whether the clips selected together are the selection (5.0): its primary clip is one of them (any other selection made since ends the group).</summary>
    private bool GroupHolds => _selectedClip is Layer primary && _viewModel.IsInClipGroup(primary);

    /// <summary>The clips selected together, while they are the selection; else the one clip selected (when it could be one of a group), or none.</summary>
    private IReadOnlyList<Layer> SelectedGroup()
    {
        if (GroupHolds)
            return _viewModel.SelectedClips;
        return _selectedClip is Layer one && _viewModel.CanGroup(one) ? [one] : [];
    }

    /// <summary>
    /// Selects one thing (a clip, a clip's sound; null: the video at the playhead). The
    /// clips selected together are let go, unless <paramref name="keepGroup"/> (5.0: the clip is one of them, made the one
    /// the panes show).
    /// </summary>
    private void SelectClip(object? clip, bool keepGroup = false)
    {
        (_selectedClip, _playheadVideoSelected) = (clip, clip is null);

        // The clip the Keyframes and Effects panes work on: a picture clip on the timeline. The captions are not one (they take
        // no keyframes and few effects), nor is a view no clip has any more: the panes keep the clip they had (5.0-Verify).
        _viewModel.KeyLayer = clip switch
        {
            Layer { IsPicture: true, IsCaptions: false } layer when _viewModel.Layers.Contains(layer) => layer,
            _ => _viewModel.KeyLayer,
        };

        // The blur fill's settings row (5.0-Release): no clip, and the Effects pane's all the same while its row or block is selected.
        _viewModel.BackgroundSelected = clip is Layer { IsBackground: true };
        if (!keepGroup)
        {
            _rangeAnchor = null;
            _viewModel.ClearClipGroup();
        }

        // The outline is drawn by the surface from what is selected; the inspector shows the selection's values, and the
        // player's boxes outline it (the captions' too, which the key layer never is).
        UnifiedTimeline.Surface.Redraw();
        UpdateTimelineInspector();
        if (clip is Layer { IsCaptions: true })
            _playerOutlineHidden = false;
        UpdatePlayerSelection();
    }

    /// <summary>Nothing is selected any more (after a split, a delete, an undo): the video at the playhead is what the tools work on.</summary>
    private void ClearClipSelection()
    {
        (_selectedClip, _playheadVideoSelected) = (null, false);
        _rangeAnchor = null;
        _viewModel.BackgroundSelected = false;
        _viewModel.ClearClipGroup();
        UpdateTimelineInspector();
        UpdatePlayerSelection();
    }

    // ----- Clips selected together (5.0) -----

    /// <summary>Ctrl + click on a clip not selected: it joins the selection (the one selected before with it), and is the one the panes show.</summary>
    private void AddToClipGroup(Layer clip)
    {
        var group = SelectedGroup().ToList();
        group.Add(clip);
        _rangeAnchor = null;
        _viewModel.SetClipSelection(group);
        SelectClip(clip, keepGroup: true);
        CrashReporter.Note("timeline", $"select: {_viewModel.SelectedClips.Count} clips");
    }

    /// <summary>Ctrl + click on a clip selected: it leaves the selection; the last one chosen of those left is the one the panes show.</summary>
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
    /// Shift + click: every clip from the one selected (where the range began, on a run of Shift + clicks) to the one clicked,
    /// across the time from the earlier's start to the later's end and the tracks between them, is selected; the clip clicked
    /// is the one the panes show. With nothing to begin from, the clip alone.
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

    // A block clicked and let go where it was (5.0): what its press left to the click.
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

    // The group changed in the view model (an edit took its clips away, an undo, a choice made): the outlines follow it.
    private void ClipGroup_Changed()
    {
        UnifiedTimeline.Surface.Redraw();
        UpdateTimelineInspector();
    }

    /// <summary>
    /// A clip's block let go after a trim of its start, a roll, a slip or a ripple (see UnifiedTimelineControl): the view model
    /// makes the edit, or says why it cannot, and the bars are drawn again either way. The clip is the one the timeline has
    /// now (by its id), only while it is still where its block was made from (<paramref name="built"/>); each edge is
    /// measured from the block's change, so an edge let go where it was is the clip's own, to the frame.
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
    /// A clip's block let go after a move along its row, or a drag of its right edge: the clip (as the timeline has it now,
    /// while it is still where the block showed it) starts where the block's left edge was let go and ends at its right edge,
    /// each as it was when that edge did not move (so a move never trims a clip drawn wider than it is).
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

    /// <summary>Where a clip dragged onto another row starts: where it was let go, or (its block not moved along) where it was.</summary>
    private static double StartOf((double Start, double End) built, double left, double at, double perPixel) =>
        Math.Abs(at - left) <= 0.5 ? built.Start : at * perPixel;

    /// <summary>How far an edge of a clip at a seam of folded dead air can be dragged (Q11): out, showing the fold again; in, folding more. Null when it is at none.</summary>
    private (double Reveal, double Fold)? SeamRangeOf(Layer clip, bool startEdge) =>
        LiveClip(clip, quiet: true) is { } live && _viewModel.FoldAtEdge(live, startEdge) is not null ? _viewModel.SeamEdgeRange(live, startEdge) : null;

    /// <summary>An edge of a clip let go at a seam of folded dead air, so many seconds outward (inward, negative): the fold shown again, or more folded into it (one step of Undo).</summary>
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
    /// A drag on the timeline still held is let go, put back and committed to nothing (4.4 BugHunt), before an edit made
    /// another way (a key, a tool, a menu) changes what it was dragging: its let-go would edit a clip that is no longer so.
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

    private void SplitClip_Click(object sender, RoutedEventArgs e) => SplitSelectedClip();

    /// <summary>The razor: cuts the selected block in two at the playhead. Nothing selected means the video there.</summary>
    private void SplitSelectedClip()
    {
        LetGoOfDrag();
        var at = _viewModel.PositionMs / 1000;
        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"split at {at:0.000} s"));

        // The captions selected: the caption under the playhead is split at its word there (5.1-Polish), not refused.
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
                // Nothing selected: the video at the playhead is what is split.
                _viewModel.SplitLayer(_viewModel.PlayheadVideo, at);
                break;
        }

        ClearClipSelection();
        ScheduleClipRedraw();
    }

    private void TrimStart_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: true);

    private void TrimEnd_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: false);

    private void TrimSelectedClip(bool start)
    {
        // A clip's sound is trimmed as its clip is; nothing selected, the video at the playhead.
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
    /// Why the selection is not something to cut (the captions, selected from their row since 5.0-Verify, and the blur fill's
    /// settings row are no clips): never the video at the playhead in their place. Null for a clip, or nothing selected.
    /// </summary>
    private static string? NotAClip(object? selected, string what) => selected switch
    {
        Layer { IsCaptions: true } => $"The captions are words, not a clip: they are not {what}. Their words are edited in the Subtitle Studio; select a clip to cut it.",
        Layer { IsBackground: true } => $"The blur fill's settings are not a clip: they are not {what}. Select a clip to cut it.",
        _ => null,
    };

    private void DeleteClip_Click(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    /// <summary>
    /// Delete: a clip goes. Clips selected together (5.0) all go, one step of Undo.
    /// </summary>
    private void DeleteSelectedClip()
    {
        // A crossfade's box selected: Delete takes it away, and leaves its clips.
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

    // ----- The clip clipboard (5.2A): Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+D, Ctrl+Alt+V on what is selected -----

    /// <summary>The clips the clipboard's keys work on: the clips selected together, or the one selected (a clip's sound standing for its clip).</summary>
    private IReadOnlyList<Layer> ClipboardSelection() => _selectedClip is LayerSound sound && !GroupHolds
        ? _viewModel.CanGroup(sound.Layer) ? [sound.Layer] : []
        : SelectedGroup();

    private void CopySelectedClips() => _viewModel.CopyClips(ClipboardSelection());

    /// <summary>Ctrl+X: the selected clips copied, then deleted (to the recycle bin) as Delete does: one step of Undo.</summary>
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

    /// <summary>Whether Alt+Up / Alt+Down have a clip to move (5.4A): a selected picture clip that can go onto a track of its own.</summary>
    private bool CanMoveSelectedToNewTrack() => _viewModel.CanMoveClipsToNewTrack(ClipboardSelection());

    /// <summary>
    /// The selected clips onto a track above or below (5.4A): with the keys (<paramref name="ontoNeighbour"/>) onto the next track that
    /// way where it is free, otherwise a new track; the clip menu's items always a new track. One step of Undo, the selection kept.
    /// </summary>
    private void MoveSelectedToNewTrack(bool above, bool ontoNeighbour = false)
    {
        LetGoOfDrag();
        if (_viewModel.MoveClipsToNewTrack(ClipboardSelection(), above, ontoNeighbour))
            ScheduleClipRedraw();
    }

    /// <summary>Ctrl+Alt+V: what of the copied clip goes onto the selected clips, chosen in the Paste Attributes dialog.</summary>
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

    // Undo and Redo let go of a drag first (what it drew would be committed onto the timeline put back under it).
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

    // Raised when either chain button is switched, by a click, a key or a screen reader; and, to no effect,
    // when a button merely follows the other one.
    private void LinkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not System.Windows.Controls.Primitives.ToggleButton button || (button.IsChecked == true) == _viewModel.AudioLinked)
            return;

        _viewModel.SetAudioLinked(button.IsChecked == true);

        // The button shows what is so, not what was clicked: the question may have been answered with no.
        if ((button.IsChecked == true) != _viewModel.AudioLinked)
            button.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, _viewModel.AudioLinked);
        ScheduleClipRedraw();
    }
}
