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
    // stands for is changed when it is let go, which is also when everything else (the command, Live Preview, Undo) hears of it.
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
        // words and times are the Subtitle Editor's: a double-click opens it.
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
            // heard in through every cut, and are reworded and retimed in the Subtitle Editor (right-click, Edit Captions...).
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
                SoundWindow = showsSound && waveforms ? SoundOf(clip.Waveform, clip.MediaOffset, duration, speed, clip.GainDb) : null,
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
                    SoundWindow = waveforms ? SoundOf(clip.Waveform, clip.MediaOffset, duration, speed, clip.GainDb) : null,
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
        Layer { IsCaptions: true } => $"The captions are words, not a clip: they are not {what}. Their words are edited in the Subtitle Editor; select a clip to cut it.",
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

    // ----- Right-click on a track -----
    // One menu for every row of the timeline (4.4): the UnifiedTimelineControl asks for it (MenuRequested) with the row, the
    // block pressed (or a fade handle, or the header), and FillTrackMenu fills it with what can be done as things are now.
    // Names follow one scheme: Title Case; "..." only where a window asks for more; the key bound to an action shown beside
    // it (KeybindManager), never in its name; Delete for content, Remove for an attribute, Clear for a collection, Restore
    // to bring something back.

    /// <summary>
    /// Fills a timeline row's right-click menu: what can be done to the track as a whole (its filters, its place, its mask,
    /// its look, its sound), and to the clip that was clicked (split, trim, fades, speed, delete). A fade handle's menu is that
    /// fade's alone. The items are added after any the menu has already.
    /// </summary>
    /// <param name="rowData">The row's Data: a track's row (a Layer: a video, picture or sound track, the caption row, the blur fill's settings row); null for the space below the rows.</param>
    /// <param name="clicked">The pressed block's Clip (a Layer, a LayerSound, a SubtitleRef), or null.</param>
    /// <param name="fade">The fade handle pressed, when one was: its block's clip, and whether it is the fade in.</param>
    /// <param name="onHeader">The row's header was right-clicked, not its time: the menu is about the track (and the clip selected on it).</param>
    internal void FillTrackMenu(ItemsControl menu, object? rowData, object? clicked, (object Clip, bool In)? fade, bool onHeader)
    {
        // A right-click on a fade handle: that fade's menu, and nothing else.
        if (!onHeader && fade is { } pressed && FadeMenuItems(pressed.Clip, pressed.In) is { Count: > 0 } fadeItems)
        {
            foreach (var item in fadeItems)
                menu.Items.Add(item);
            return;
        }

        var first = menu.Items.Count;
        switch (rowData)
        {
            case Layer layer:
                FillLayerMenu(menu, layer, onHeader ? null : clicked, onHeader);
                break;
            case EffectLane:
                FillEffectMenu(menu, clicked as EffectBlock);
                break;
            default:
                return;
        }

        TidySeparators(menu, first);
    }

    /// <summary>
    /// A track row's menu (a Layer: a video, picture or sound track, the caption row, the blur fill's settings row), in the
    /// order of an edit (5.1-DogfoodPolish, the user's overhaul): Cut &amp; Edit first and flat, then one submenu for each thing a clip has
    /// (its transform, its Game Preset, its effects, its sound, its time), the Smart Tools flat again, and what is the track's last.
    /// </summary>
    /// <param name="onHeader">Its header was right-clicked: what is the track's (its Track Preset, 5.2A) comes first, not in the Track submenu.</param>
    private void FillLayerMenu(ItemsControl menu, Layer layer, object? clicked, bool onHeader = false)
    {
        // The clip the menu is about: the one clicked, else the one selected on this track, else the track's first.
        var clips = _viewModel.GetTrackClips(layer);
        var clip = (clicked ?? _selectedClip) switch
        {
            Layer chosen when clips.Contains(chosen) => chosen,
            LayerSound sound when clips.Contains(sound.Layer) => sound.Layer,
            _ => layer,
        };

        void Add(string header, Action action, bool enabled = true, string? tip = null, string? gesture = null) =>
            AddTrackItem(menu, header, action, enabled, tip, gesture);
        void Separate() => AddSeparator(menu);
        void Keep(MenuItem submenu)
        {
            TidySeparators(submenu, 0);
            if (submenu.Items.Count > 0)
                menu.Items.Add(submenu);
        }

        // One of clips selected together (5.0): what is done to all of them comes first, each one step of Undo.
        if (GroupHolds && _viewModel.IsInClipGroup(clip))
        {
            AddGroupItems(menu);
            Separate();
        }

        var at = _viewModel.PositionMs / 1000;

        // An empty track has nothing to set but its Track Preset (5.2A: set before anything is dropped onto it): it can only leave the
        // mini-stack it is in, or be deleted.
        if (layer.IsEmptyTrack)
        {
            AddTrackPresetItems(menu, layer, flat: false);
            Separate();
            AddUnlinkItem(menu, layer, layer);
            Add("Delete Track", () => _viewModel.RemoveTrack(layer));
            return;
        }

        // The header's menu is the track's: its Track Preset first (5.2A).
        if (onHeader && _viewModel.CanHaveTrackPreset(layer))
        {
            AddTrackPresetItems(menu, layer, flat: false);
            Separate();
        }

        // A sound's name, typed in place at the top of its menu (one step of Undo, taken at the first keystroke). On a stream's
        // Sub-Lane it is the lane's name, and so the title of the output track the lane goes to (5.2-Arm64AndNpu, the user's call).
        if (layer.IsAudio)
        {
            if (_viewModel.StreamLaneName(layer) is { } laneName)
                AddRenameItem(menu, laneName, RenameWithUndo(laneName, name => _viewModel.RenameStreamLane(layer, name)), menu.Items.Count);
            else
                AddRenameItem(menu, clip.Title, RenameWithUndo(clip.Name, name => clip.Title = name), menu.Items.Count);
            Separate();
        }

        // The caption row: the captions' own menu (making, editing, styling, placing, importing and deleting them).
        if (layer.IsCaptions)
        {
            FillCaptionMenu(menu, null);
            Separate();
        }

        var editable = layer.IsRemovable;

        // ----- Cut & Edit -----
        if (layer.HasTiming)
        {
            Add("Split at Playhead", () => _viewModel.SplitLayer(clip, at), gesture: "edit.split");
            Add("Ripple Delete", () => _viewModel.RippleDelete(clip), gesture: "edit.rippleDelete");
        }

        if (editable)
        {
            if (clips.Count > 1)
                Add("Delete", () => DeleteMenuClip(clip), gesture: "edit.delete");
            else
                Add("Delete", () => _viewModel.RemoveTrack(layer), gesture: "edit.delete");
        }

        // The clipboard (5.2A): on this clip, or the clips selected with it.
        if (_viewModel.CanGroup(clip))
        {
            Add("Copy", () => { SelectWithGroup(clip); CopySelectedClips(); }, gesture: "edit.copy");
            Add("Cut", () => { SelectWithGroup(clip); CutSelectedClips(); }, gesture: "edit.cut");
            Add("Duplicate", () => { SelectWithGroup(clip); DuplicateSelectedClips(); }, gesture: "edit.duplicate",
                tip: "A copy of this clip (or the clips selected with it), with its Cutouts, right after it on the same track where there is room.");
            Add("Paste Attributes...", () => { SelectWithGroup(clip); PasteAttributesToSelection(); }, _viewModel.HasCopiedAttributes, gesture: "edit.pasteAttributes",
                tip: "Gives this clip (or the clips selected with it) the parts you choose of the copied clip's look: its transform, crop, opacity, effects, keyframes, mask, fades or sound.");
        }

        if (_viewModel.HasCopiedClips && layer.HasTiming)
            Add("Paste at Playhead", () => _viewModel.PasteClips(at), gesture: "edit.paste");
        if (editable)
            Add("Duplicate Track", () => _viewModel.DuplicateLayer(layer), tip: "A copy of this whole track, every clip on it, on a new track just above, moved a little on the frame.");

        // A sound on a shared lane, or on any Audio track (5.2): onto a lane of its own under its video's track, or a track of its own.
        if (layer.IsAudio && _viewModel.ClipOf(clip) is not null)
        {
            if (_viewModel.ParentRowOf(layer) is { } videoRow && _viewModel.CanMoveSoundUnder(clip, videoRow))
                Add("Move to Its Own Sub-Lane", () => _viewModel.MoveSoundToOwnLane(clip, videoRow),
                    tip: "Takes this sound off the lane it shares onto a lane of its own in the same track's audio stack, at the same time. Dragging it onto a video track's row does the same there.");
            Add("Move to a New Audio Track", () => _viewModel.MoveSoundToOwnLane(clip, null),
                tip: "Takes this sound onto an Audio track of its own, at the same time; it still follows its video clip until it is unlocked from it.");
        }

        // Silences Split & Mark folded out at its cuts (4.3D): each put back where it was, what follows sliding on; and
        // (4.4 BugHunt) each stretch put back over the clip folded out again, what follows sliding back.
        if (layer.HasTiming)
        {
            foreach (var fold in _viewModel.FoldsAt(clip))
            {
                Add(string.Create(CultureInfo.InvariantCulture, $"Restore Folded Silence ({fold.Length:0.##} s) at Its {(fold.AtEnd ? "End" : "Start")}"),
                    () => AnimatedFoldChange(() => _viewModel.UnfoldSegment(fold)));
            }

            foreach (var restored in _viewModel.RestoredFoldsOn(clip))
            {
                Add(string.Create(CultureInfo.InvariantCulture, $"Hide / Fold Dead Air Segment ({restored.Length:0.##} s)"),
                    () => AnimatedFoldChange(() => _viewModel.HideRestoredStretch(restored)),
                    tip: "Folds out again the stretch of dead air that was put back here: what follows closes up, and its seam can put it back once more.");
            }

            if (_viewModel.FoldCount is > 1 and var folds)
                Add(string.Create(CultureInfo.InvariantCulture, $"Restore All {folds} Folded Silences"), () => AnimatedFoldChange(_viewModel.UnfoldAll));
        }

        // Its own switches (5.2), as on its block: On/Off for this clip alone, and a child's Lock to its parent clip.
        if (_viewModel.ClipOf(clip) is not null && !layer.IsCaptions)
        {
            var on = _viewModel.IsClipOn(clip);
            Add(on ? clip.IsAudio ? "Turn Off (Mute This Clip)" : "Turn Off (Hide This Clip)" : "Turn On", () => _viewModel.SetClipOn(clip, !on),
                tip: "This clip alone: its Cutouts and effects keep their own switches. The track header's switch takes the whole track and its stack.");
            if (_viewModel.ClipLock(clip) is { } locked)
            {
                Add(locked ? "Unlock from Parent Clip" : "Lock to Parent Clip", () => _viewModel.SetClipLocked(clip, !locked),
                    tip: locked ? "Frees it: it is dragged, slid and trimmed on its own, and its clip no longer takes it along."
                        : "Locks it to the clip it lies over: it moves, trims and splits with it.");
            }
        }

        Separate();

        // ----- Transform, Sizing & Position -----
        if (clip is { HasPlacement: true, IsCaptions: false } && _viewModel.ClipOf(clip) is not null)
            menu.Items.Add(TransformMenu(clip, clips.Count));

        // ----- Cutout & Mask Studio (5.2: on every video clip, right on its menu and first in the submenu) -----
        var isVideoClip = clip.IsVideoFile;
        const string StudioTip = "The Cutout Studio on this clip: trace its shape or a game's HUD by hand on a still, zoomed in to the very pixels; "
                                 + "cut this clip to it, add the Cutouts to it, or save them as a Game Preset.";
        if (isVideoClip)
            Add("Open Cutout & Mask Studio...", () => OpenCutoutStudio(clip), tip: StudioTip);

        // ----- Game Presets & Cutout Studio -----
        var presets = new MenuItem { Header = "Game Presets & Cutout Studio" };
        if (isVideoClip)
            AddTrackItem(presets, "Open Cutout & Mask Studio...", () => OpenCutoutStudio(clip), tip: StudioTip);
        if (_viewModel.GamePresetTargets([clip]).Count > 0)
            presets.Items.Add(GamePresetMenu(clip));
        if (layer.HasMask)
        {
            AddTrackItem(presets, "Mask from a File or a Ready Shape...", () => OpenMaskTool(clip),
                tip: "The shape this clip alone is cut to: a picture file, one of the ready shapes, one drawn in the Cutout Studio, or none.");
        }

        if (_viewModel.IsCutout(clip))
            AddTrackItem(presets, "Draw Target", () => _viewModel.DrawTargetCommand.Execute(clip));
        if (clip is { CustomMask: true } && _viewModel.IsCutout(clip) && File.Exists(clip.MaskPath.Trim().Trim('"')))
            AddTrackItem(presets, "Add More Cutouts of This Mask...", () => AddLayersFromMask(clip.MaskPath.Trim().Trim('"')));
        if (isVideoClip && _viewModel.ClipOf(clip) is not null)
        {
            var blur = new MenuItem
            {
                Header = "Blur Fill",
                IsCheckable = true,
                IsChecked = _viewModel.IsBlurFilled(clip),
                ToolTip = "Fills the frame around this clip with a blurred copy of its whole picture, under it (the look of the blur fill's settings row). It costs one more decoder while it plays, none while the clip covers the frame.",
            };
            blur.Click += (_, _) =>
            {
                RunGuarded("Blur Fill", () => _viewModel.SetBlurFill(clip, blur.IsChecked));
                ScheduleClipRedraw();
            };
            presets.Items.Add(blur);
        }

        Keep(presets);

        // ----- Effects & Transitions -----
        var effects = new MenuItem { Header = "Effects & Transitions" };
        if ((layer.HasFilters || layer.IsBackground) && AddEffectMenu(layer.IsBackground ? layer : clip) is { } addEffect)
            effects.Items.Add(addEffect);
        if (layer.HasFilters)
            AddTrackItem(effects, "Filters...", () => OpenLayerTool(layer, LayerTool.Filters));
        if (layer.IsBackground)
            AddTrackItem(effects, "Blur Fill Settings...", ShowBlurFillSettings, tip: "Its radius, zoom, passes and dimming, on its row in the Effects pane.");
        else if (layer is { HasStyle: true, IsCaptions: false })
            AddTrackItem(effects, "Show in Effects Pane", () => ShowInEffectsPane(clip), tip: "Opacity, rounded corners, soft edges, a drop shadow, a chroma key: each an effect in the Effects pane.");

        if (layer.HasFilters || layer.IsBackground)
        {
            AddSeparator(effects);
            AddTrackItem(effects, "Copy Effects", () =>
            {
                SelectForMenu(clip);
                EffectsView.CopyEffects();
            });
            AddTrackItem(effects, "Paste Effects", () =>
            {
                SelectForMenu(clip);
                EffectsView.PasteCopiedEffects();
            }, EffectsView.HasCopiedEffects);
        }

        if (layer.HasTiming)
        {
            AddSeparator(effects);

            // Its own fades, over whatever is under it (the handles at its top corners), and a crossfade with a clip on
            // another track that it overlaps.
            AddFadeSubmenus(effects, clip);

            // The cuts it shares with the clips before and after it on its track: a crossfade across either.
            var transition = AppSettings.Current.DefaultTransitionSeconds;
            if (_viewModel.ClipBefore(clip) is { } previous)
            {
                if (clip.TransitionIn > 0)
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Remove Crossfade from {previous.Name} ({clip.TransitionIn:0.##} s)"), () => _viewModel.SetCrossfade(clip, 0));
                else
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Crossfade from {previous.Name} ({transition:0.##} s)"), () => _viewModel.SetCrossfade(clip, transition));
            }

            if (clips.FirstOrDefault(c => ReferenceEquals(_viewModel.ClipBefore(c), clip)) is { } following)
            {
                if (following.TransitionIn > 0)
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Remove Crossfade into {following.Name} ({following.TransitionIn:0.##} s)"), () => _viewModel.SetCrossfade(following, 0));
                else
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Crossfade into {following.Name} ({transition:0.##} s)"), () => _viewModel.SetCrossfade(following, transition));
            }
        }

        if (clip.IsAnimated)
        {
            AddSeparator(effects);
            AddTrackItem(effects, "Clear Keyframes", () => _viewModel.ClearKeys(clip));
        }

        Keep(effects);

        // ----- Audio & Voice -----
        // How loud the clip is, the track's filters, the clip's link to its picture (J and L cuts, slips), and what the track's sound
        // does when someone is speaking. (None of it on a video-only child, 5.0-Release: a blur fill, a HUD Cutout or one cut by a mask.)
        var audio = new MenuItem { Header = "Audio & Voice" };
        var linkable = !clip.IsAudio && _viewModel.ClipOf(clip) is { HasSound: true };
        if (!layer.IsVideoOnlyChild && !clip.IsVideoOnlyChild && (layer.CarriesSound || clip.CarriesSound || linkable))
        {
            if (clip.CarriesSound)
                audio.Items.Add(GainMenu(clip.GainDb, gain => _viewModel.SetClipGain(clip, gain)));
            if (layer.CarriesSound)
                AddTrackItem(audio, "Audio Filters...", () => OpenAudioFilters(layer));
            if (linkable)
                AddLinkItem(audio, clip, _viewModel.ClipOf(clip) is { AudioLinked: true });
            if (layer.CarriesSound)
                AddDuckingItems(audio, layer, layer.AutoDuck, layer.IsVoice);
        }

        // The beats of the clip's sound (5.2B): found in the background, kept with the clip (one step of Undo); dragged clips snap to them and
        // the beat grid draws them on the clip (both on the Snap button's menu).
        if (!layer.IsVideoOnlyChild && !clip.IsVideoOnlyChild && _viewModel.ClipOf(clip) is { HasSound: true })
        {
            AddSeparator(audio);
            AddTrackItem(audio, "Detect & Snap to Audio Beats", () => _ = DetectBeatsAsync(clip), _viewModel.CanDetectBeats(clip),
                "Finds the beats of this clip's sound (in the background) and keeps them with the clip: dragged clips' edges snap to them, and the beat grid shows them as ticks along the clip. One step of Undo.");
            if (_viewModel.HasBeats(clip))
                AddTrackItem(audio, "Clear Audio Beats", () => _viewModel.ClearClipBeats(clip), true, "Takes this clip's beats away. One step of Undo.");
        }

        // A stream's lane: the output track its stream place goes to (5.2-AuditB: what each output audio track is made as, by its
        // place), kept or left out of the export.
        if (layer.IsAudio && _viewModel.OutputTrackOf(layer) is { } output)
        {
            AddSeparator(audio);
            var dropped = new MenuItem
            {
                Header = $"Leave Out of the Export ({output.Title})",
                IsCheckable = true,
                IsChecked = output.IsDropped,
                ToolTip = "The export's audio track for this stream place (every lane of it, on any track) is left out of the file. Unticked, it is encoded again.",
            };
            dropped.Click += (_, _) => RunGuarded("an output track", () => output.IsMuted = dropped.IsChecked);
            audio.Items.Add(dropped);
        }

        // Every video track is equal (the user's directive): all of its sound on lanes of their own.
        if (editable && clip.IsVideoFile && _viewModel.CanUnpackStreams(clip))
        {
            AddTrackItem(audio, "Unpack Audio Streams", () => _ = _viewModel.UnpackAudioStreamsAsync(clip), true,
                "Gives each audio stream of this file (game, desktop, microphone) a lane of its own in the track's audio stack, with its own mute, gain, filters and voice switch. One step of Undo.");
        }

        Keep(audio);

        // ----- Speed & Time -----
        var time = new MenuItem { Header = "Speed & Time" };

        // How fast the clip runs: a few usual speeds, the one it has ticked. Anything between is typed into its Speed box.
        if (clip.HasSpeed)
        {
            var speeds = new MenuItem { Header = string.Create(CultureInfo.InvariantCulture, $"Clip Speed ({clip.Speed:0.##}x)") };
            foreach (var speed in new[] { 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 4, 10 })
            {
                var item = new MenuItem
                {
                    Header = string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x{(speed == 1 ? "  (As Recorded)" : "")}"),
                    IsCheckable = true, IsChecked = Math.Abs(clip.Speed - speed) < 0.0005,
                };
                item.Click += (_, _) =>
                {
                    RunGuarded("a clip's speed", () => _viewModel.SetClipSpeed(clip, speed));
                    ScheduleClipRedraw();
                };
                speeds.Items.Add(item);
            }

            time.Items.Add(speeds);
        }

        if (layer.HasTiming)
        {
            AddTrackItem(time, "Split / Trim by Timecode...", () => SplitTrimByTimecode(clip));
            AddTrackItem(time, "Trim Start to Playhead", () => _viewModel.TrimClip(clip, start: true, at), gesture: "edit.trimStart");
            AddTrackItem(time, "Trim End to Playhead", () => _viewModel.TrimClip(clip, start: false, at), gesture: "edit.trimEnd");
            AddTrackItem(time, "Extend to Whole Sequence", () => _viewModel.ShowThroughout(clip),
                clip.StartTime > 0.001 || clip.StartTime + clip.Duration < _viewModel.SequenceSeconds - 0.001,
                clip.IsAudio ? "Plays the sound from the start of the sequence to its end." : "Shows the clip from the start of the sequence to its end.");
        }

        // A proxy of the clip's file, on demand (5.0D, see ProxyCache), offered only where it helps (5.0-Verify, the user's Q6):
        // a long-GOP video layer the preview cannot decode fast, a clip that has it on, or Shift held as the menu opens.
        if (clip.IsVideoFile && _viewModel.OptimizeIsUseful(clip, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            AddSeparator(time);
            var optimized = clip.ProxyChoice == ProxyChoice.Make;
            var optimize = new MenuItem
            {
                Header = "Optimize Long-GOP Clip for Scrubbing", IsCheckable = true, IsChecked = optimized,
                ToolTip = $"{_viewModel.ProxyStatus(clip)}. Makes a small proxy of this clip's file (its I-frames close together: Settings, Proxies), in the background, which the GPU preview plays in its place, so that seeking and scrubbing a recording whose I-frames are far apart cost little where the graphics card cannot decode it fast. Exports always read the original.",
            };
            optimize.Click += (_, _) => RunGuarded("a clip's proxy", () => _viewModel.SetProxyChoice(clip, optimized ? ProxyChoice.Auto : ProxyChoice.Make));
            time.Items.Add(optimize);
        }

        Keep(time);
        Separate();

        // ----- Smart Tools -----
        // The Personal Clip Library (5.2C): the clip's span kept across projects, a lossless copy.
        if (_viewModel.CanSaveToClipLibrary(clip))
            Add("Save Clip to Personal Clip Library...", () => SaveToClipLibrary(clip), true,
                "Keeps this clip's span (its picture and every sound of its file, a lossless copy: nothing encoded again) in your own library, Documents\\HandPeg\\Clips, with a title, tags and the focal point of a Ping-to-Mark highlight inside it. The Media pane's Clip Library lists it, to search, scrub and drag into any project.");

        // A double-wide recording (5.2C): the gameplay and the facecam as two clips in step, placed for the Sequence's shape.
        if (editable && _viewModel.CanUnpackSideBySide(clip))
            Add("Unpack OBS Side-by-Side Canvas (Gameplay + Facecam)", () => _viewModel.UnpackSideBySide(clip), true,
                "This recording is two pictures side by side on one canvas. The clip keeps the left half (the gameplay), cut to it and placed for the Sequence's shape, and a Cutout of the same file above it shows the right half (the facecam): a corner picture on a wide frame, a band across the top of a tall one. The two stay in step through every split, trim and move. One step of Undo.");

        if (editable && layer is { HasFilters: true, IsAdjustment: false, IsBackground: false })
            Add("3D Fly-In + Shockwave Impact", () => _viewModel.FlyInWithShockwave(clip), _viewModel.HasSource,
                "From the playhead the clip flies out to the middle of the frame, lands with a shockwave and a camera shake, and glides back. It is all keyframes on the clip and an Impact clip: change anything. One step of Undo.");

        if (editable)
        {
            // What its sound is listened to for: the captions (a clip with sound through its own file, a video through its
            // streams on Sub-Lanes) and the silences.
            if (clip.CarriesSound)
            {
                Add("Generate Auto-Captions...", () => GenerateCaptionsFrom(clip), _viewModel.HasSource);
                AddCaptionsSubmenu(menu, clip, () => _viewModel.UseLayerAudioForCaptions(clip));
            }
            else if (clip.IsVideoFile && _viewModel.CaptionStreamsOf(clip) is { Count: > 0 })
            {
                Add("Generate Auto-Captions...", () => GenerateCaptionsFrom(clip), _viewModel.HasSource);
                AddCaptionsSubmenu(menu, clip, null);
            }

            // Remove Dead Air on every track of video or sound (4.3E). A clip with sound of its own is listened to and cut
            // itself; a video whose streams are on Sub-Lanes listens to its first (or the sound marked as the voice) and cuts its
            // file's clips on every unlocked track, their Cutouts among them; a Cutout of a video has no sound
            // of its own to listen to.
            if (clip.CarriesSound)
            {
                Add("Remove Dead Air...", () => RunDeadAir(clip), _viewModel.HasSource,
                    "Finds the silences in this clip's own sound (or the gaps between its captioned words) and cuts them out of it: Split & Mark folds them out of its track (each can be put back from the clip's menu), or they are deleted.");
            }
            else if (clip.IsVideoFile && !_viewModel.IsCutout(clip) && _viewModel.CaptionStreamsOf(clip) is { Count: > 0 })
            {
                Add("Remove Dead Air...", () => RunDeadAir(clip), _viewModel.HasSource,
                    "Finds the silences in this video's sound (the stream marked as the voice, else its first) and cuts them out of every clip of its file on every unlocked track at once, their Cutouts and the other clips with them.");
            }
            else if (clip.IsVideoFile)
            {
                menu.Items.Add(DeadAirWithoutSound(clip));
            }
        }

        Separate();

        // ----- What is the track's -----
        var track = new MenuItem { Header = "Track" };
        if (!onHeader)
        {
            AddTrackPresetItems(track, layer, flat: false);
            AddSeparator(track);
        }

        if (layer.CanReorder && !layer.IsAudio)
        {
            AddTrackItem(track, "Bring Forward", () => _viewModel.MoveLayerUpCommand.Execute(layer), tip: "Moves this track one place towards the front: it is drawn over the track above it now.");
            AddTrackItem(track, "Send Backward", () => _viewModel.MoveLayerDownCommand.Execute(layer), tip: "Moves this track one place towards the back: it is drawn under the track below it now.");
        }

        AddUnlinkItem(track, clip, clips[0]);
        if (editable || layer.IsBackground)
            AddTrackItem(track, layer.IsHidden ? "Show" : layer.IsAudio ? "Mute" : "Hide", () => _viewModel.ToggleHidden(layer));
        if (editable && clips.Count > 1)
        {
            AddSeparator(track);
            AddTrackItem(track, "Delete Track", () => _viewModel.RemoveTrack(layer), tip: $"Deletes the whole track, its {clips.Count} clips with it (Undo brings them back).");
        }

        Keep(track);
    }

    /// <summary>
    /// Transform, Sizing &amp; Position: every number of where a clip sits, typed in place, and the one-click placements. The same values the
    /// Timeline's inspector strip and the Canvas set; each change one step of Undo, this clip's alone.
    /// </summary>
    private MenuItem TransformMenu(Layer clip, int clipsOnTrack)
    {
        var menu = new MenuItem { Header = "Transform, Sizing & Position" };
        void Number(string label, Func<double> read, Action<double> write) =>
            AddNumberRow(menu, label, read, write, $"change {label.ToLowerInvariant()} of {clip.Name}");

        Number("Width %", () => clip.SizePercent, v => clip.SizePercent = (int)Math.Round(v));
        if (clip.IsRemovable)
        {
            Number("Height %", () => clip.SizeHeightPercent, v =>
            {
                clip.LockAspectRatio = false;
                clip.SizeHeightPercent = (int)Math.Round(v);
            });
        }

        Number("Position X %", () => clip.PositionXPercent, v => clip.PositionXPercent = v);
        Number("Position Y %", () => clip.PositionYPercent, v => clip.PositionYPercent = v);
        if (clip.HasFilters)
        {
            // Yaw / Pitch / Roll (5.2A, true 3D): the Roll is the Rotation; the tilts turn the picture out of the frame, seen in perspective.
            Number("Rotation (Roll) \u00B0", () => clip.Rotation, v => clip.Rotation = v);
            Number("Tilt X (Pitch) \u00B0", () => clip.TiltX, v => clip.TiltX = Layer.Tilt(v));
            Number("Tilt Y (Yaw) \u00B0", () => clip.TiltY, v => clip.TiltY = Layer.Tilt(v));
        }

        var crop = new MenuItem { Header = "Crop" };
        void Edge(string label, Func<double> read, Action<double> write) =>
            AddNumberRow(crop, label, read, write, $"crop {clip.Name}");
        Edge("Left %", () => clip.CropLeftPercent, v => clip.CropLeftPercent = v);
        Edge("Right %", () => clip.CropRightPercent, v => clip.CropRightPercent = v);
        Edge("Top %", () => clip.CropTopPercent, v => clip.CropTopPercent = v);
        Edge("Bottom %", () => clip.CropBottomPercent, v => clip.CropBottomPercent = v);
        menu.Items.Add(crop);

        AddSeparator(menu);
        AddTrackItem(menu, "Fit to Frame", () => _viewModel.FitClipToFrame(clip, fill: false),
            tip: "Sizes the clip so that the whole picture shows, touching the frame on one side, and puts its middle on the frame's.");
        AddTrackItem(menu, "Fill Frame", () => _viewModel.FitClipToFrame(clip, fill: true),
            tip: "Sizes the clip to cover the whole frame, what reaches past it off its edges, and puts its middle on the frame's.");
        AddTrackItem(menu, "Center in Frame", () => _viewModel.CenterClipOnFrame(clip), tip: "Puts the clip's middle on the frame's; its size stays.");
        AddTrackItem(menu, "Reset Transform", () => _viewModel.ResetClipTransform(clip), tip: "This clip back as a new clip starts: sized to the frame, in its middle, not turned, nothing cropped.");

        if (clipsOnTrack > 1)
        {
            AddTrackItem(menu, "Apply Transform to Track", () => _viewModel.ApplyPlacementToTrack(clip),
                tip: $"Gives every other clip on this track the place of {clip.Name}: its corner, size and shape, its turn and anchor, and its crop. Their looks, timing and keyframes stay theirs.");
        }

        return menu;
    }

    /// <summary>A number typed in a menu: taken on Enter or when the box is left, as one step of Undo; the menu stays open for the next.</summary>
    private void AddNumberRow(ItemsControl menu, string label, Func<double> read, Action<double> write, string step)
    {
        static string Shown(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var box = new TextBox { Text = Shown(read()), Width = 64, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        void Take()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && Math.Abs(value - read()) > 1e-6)
            {
                RunGuarded(step, () =>
                {
                    _viewModel.Checkpoint(step);
                    write(value);
                });
                ScheduleClipRedraw();
            }

            box.Text = Shown(read());
        }

        box.LostKeyboardFocus += (_, _) => Take();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Take();
                box.SelectAll();
                e.Handled = true;
            }
        };

        var line = new DockPanel { LastChildFill = false };
        line.Children.Add(new TextBlock { Text = label, Width = 96, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(box);
        menu.Items.Add(new MenuItem { Header = line, StaysOpenOnClick = true });
    }

    /// <summary>Apply Game Preset: every saved preset, put on this clip alone (or the clips selected with it), each with Cutouts of its own over its own span.</summary>
    private MenuItem GamePresetMenu(Layer clip)
    {
        _viewModel.RefreshStylePresets();
        var menu = new MenuItem { Header = "Apply Game Preset" };
        foreach (var name in _viewModel.StylePresets)
        {
            AddTrackItem(menu, name, () =>
            {
                _viewModel.ApplyGamePreset(GroupHolds && _viewModel.IsInClipGroup(clip) ? _viewModel.SelectedClips.ToList() : [clip], name);
            });
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "None Saved Yet: Make One in the Cutout Studio", IsEnabled = false });
        return menu;
    }

    /// <summary>
    /// A track's Game Preset (5.2A, a Track Preset Node): every saved preset, the track's own ticked, then Clear Track Preset. Flat (the
    /// header badge's list), or as a Set Track Preset submenu and the clear beside it (the track's right-click menu). Each one step of Undo.
    /// </summary>
    private void AddTrackPresetItems(ItemsControl menu, Layer layer, bool flat)
    {
        if (!_viewModel.CanHaveTrackPreset(layer))
            return;

        _viewModel.RefreshStylePresets();
        var current = _viewModel.TrackPresetOf(layer);
        var list = flat ? menu : new MenuItem { Header = "Set Track Preset" };
        foreach (var name in _viewModel.StylePresets)
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(name, current, StringComparison.Ordinal),
                ToolTip = $"Every video on this track takes {name} now, and every video dropped or moved onto it later, each with Cutouts of its own.",
            };
            item.Click += (_, _) =>
            {
                RunGuarded("Set Track Preset", () => _viewModel.SetTrackPreset(layer, name));
                ScheduleClipRedraw();
            };
            list.Items.Add(item);
        }

        if (list.Items.Count == 0)
            list.Items.Add(new MenuItem { Header = "None Saved Yet: Make One in the Cutout Studio", IsEnabled = false });
        if (!flat)
            menu.Items.Add(list);
        else
            AddSeparator(menu);

        AddTrackItem(menu, "Clear Track Preset", () => _viewModel.SetTrackPreset(layer, null), current is not null,
            tip: "This track gives no Game Preset to what comes onto it; its clips keep the presets they have.");
    }

    /// <summary>A clip's effects in the Effects pane: it is selected, and the pane shown.</summary>
    private void ShowInEffectsPane(Layer clip)
    {
        SelectForMenu(clip);
        _viewModel.ShowEffectsPane = true;
    }

    /// <summary>The blur fill's settings row, pinned in the Effects pane, is where it is set (5.1-DogfoodPolish: no dialog of its own).</summary>
    private void ShowBlurFillSettings()
    {
        SelectClip(_viewModel.BackgroundLayer);
        _viewModel.ShowEffectsPane = true;
    }

    /// <summary>
    /// What a menu does to clips selected together (5.0): how many there are, Delete and Ripple Delete, Mute (or Unmute) of
    /// their sound, each one step of Undo. Paste Effects onto all of them is the Effects pane's (MainViewModel.PasteEffectsToClips).
    /// </summary>
    private void AddGroupItems(ItemsControl menu)
    {
        var group = _viewModel.SelectedClips.ToList();
        var count = group.Count.ToString(CultureInfo.InvariantCulture);
        menu.Items.Add(new MenuItem { Header = $"{count} Clips Selected", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        AddTrackItem(menu, $"Delete {count} Clips", DeleteSelectedClip, gesture: "edit.delete",
            tip: "Every selected clip goes into the recycle bin, their time left empty. One step of Undo.");
        AddTrackItem(menu, $"Ripple Delete {count} Clips", () =>
        {
            if (_viewModel.DeleteClips(group, ripple: true))
                ClearClipSelection();
        }, gesture: "edit.rippleDelete", tip: "Every selected clip goes into the recycle bin, and what follows each closes up. One step of Undo.");

        if (group.Exists(c => _viewModel.ClipOf(c) is { HasSound: true }))
        {
            var muted = _viewModel.AreClipsMuted(group);
            AddTrackItem(menu, muted ? $"Unmute {count} Clips" : $"Mute {count} Clips", () => _viewModel.SetClipsMuted(group, !muted),
                tip: muted ? "The sound of every selected clip is heard again." : "The sound of every selected clip is left out of the mix; their pictures stay.");
        }

        AddTrackItem(menu, "Select Only This Clip", () => SelectClip(_selectedClip),
            tip: "Lets go of the others: only the clip right-clicked stays selected.");
    }

    /// <summary>Delete Clip on a clip's menu: the clip, or (one of clips selected together, 5.0) all of them.</summary>
    private void DeleteMenuClip(Layer clip)
    {
        if (GroupHolds && _viewModel.IsInClipGroup(clip))
        {
            DeleteSelectedClip();
            return;
        }

        _viewModel.DeleteClip(clip);
    }


    /// <summary>
    /// Adds an item to a track's menu: what it does (a drag still held let go first; a fault in it a status line, never an
    /// exception that ends the app: 4.4 BugHunt), then the timeline drawn again. A shortcut shows the key bound to its action.
    /// </summary>
    private MenuItem AddTrackItem(ItemsControl menu, string header, Action action, bool enabled = true, string? tip = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
        if (gesture is not null)
            item.InputGestureText = KeybindManager.GestureText(gesture);
        if (tip is not null)
            ToolTipService.SetShowOnDisabled(item, true);
        item.Click += (_, _) =>
        {
            LetGoOfDrag();
            RunGuarded(header, action);
            ScheduleClipRedraw();
        };
        menu.Items.Add(item);
        return item;
    }

    /// <summary>A line between groups of a menu's items: never first, never two together.</summary>
    private static void AddSeparator(ItemsControl menu)
    {
        if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
            menu.Items.Add(new Separator());
    }

    /// <summary>Takes out the lines a menu's groups left at its ends or two together, from the first item that was added.</summary>
    private static void TidySeparators(ItemsControl menu, int first)
    {
        var items = menu.Items;
        for (var i = items.Count - 1; i >= Math.Max(first, 0); i--)
        {
            if (items[i] is Separator && (i == 0 || i == items.Count - 1 || items[i - 1] is Separator))
                items.RemoveAt(i);
        }
    }

    /// <summary>
    /// Makes the block a menu acts on the selection, for the tools that work on what is selected (a right-click selects its
    /// block already: this keeps the menu right when it was opened another way).
    /// </summary>
    private void SelectForMenu(object clip)
    {
        if (!Equals(_selectedClip, clip))
            SelectClip(clip);
    }

    // The same, but a clip of the clips selected together keeps them selected (the clipboard's items work on all of them, 5.2A).
    private void SelectWithGroup(Layer clip)
    {
        if (!(GroupHolds && _viewModel.IsInClipGroup(clip)))
            SelectForMenu(clip);
    }

    /// <summary>
    /// Add Effect on a track's menu (5.0-Release, the user's Q1): every effect the clip can take (Layer.CanTake), by category,
    /// each put on it with its visible starting values (MainViewModel.AddEffect, as the Effects pane's Add Effect does), the
    /// clip then selected so that the Effects pane shows its new effect; the preview follows the edit as any other. Null when the
    /// clip takes none.
    /// </summary>
    private MenuItem? AddEffectMenu(Layer target)
    {
        var menu = new MenuItem { Header = "Add Effect", ToolTip = "Puts an effect on this clip, already set to show (the Effects pane then shows it, to be tuned)." };
        string? category = null;
        foreach (var definition in Models.Effects.EffectRegistry.All.Where(d => target.CanTake(d)).OrderBy(d => Models.Effects.EffectRegistry.CategoryRank(d.Category)))
        {
            if (category is not null && definition.Category != category)
                menu.Items.Add(new Separator());
            category = definition.Category;
            var item = new MenuItem { Header = definition.Name, ToolTip = definition.Description };
            item.Click += (_, _) => RunGuarded($"add {definition.Name}", () =>
            {
                if (_viewModel.AddEffect(target, definition) is null)
                    return;
                SelectClip(target);
                ScheduleClipRedraw();

                // A mask card needs its mask: the editor opens, as Add Effect in the Effects pane opens it.
                if (definition.Type == Models.Effects.EffectRegistry.Mask && !target.CustomMask)
                    OpenLayerTool(target, LayerTool.Mask);
            });
            menu.Items.Add(item);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    /// <summary>
    /// Toggle A/V Link for a clip with a picture and a sound (a video file's): linked, its sound moves and
    /// trims with its picture; unlinked, on its own (a slip, J and L cuts). As the U key does, on the clip the menu is about.
    /// </summary>
    private void AddLinkItem(ItemsControl menu, Layer clip, bool linked) =>
        AddTrackItem(menu, "Toggle A/V Link", () =>
        {
            SelectForMenu(clip);
            ToggleSelectedClipLink();
        }, gesture: "clip.toggleLink", tip: linked
            ? "Its sound moves and trims with its picture now. Unlinked, the sound can be moved or trimmed on its own."
            : "Its sound moves on its own now. Linked again, it moves and trims with its picture.");

    /// <summary>
    /// Unlink from Parent, when the clip's track is in another track's mini-stack (a Game Preset's HUD Cutout under its video's)
    /// or the clip lies over a parent clip: edits of the parent stop carrying it along. A track leaves with its clips.
    /// </summary>
    /// <param name="clip">The clip the menu is about (an empty track's stand-in for an empty track).</param>
    /// <param name="row">The view standing for the clip's track: its first clip's, or the empty track's stand-in.</param>
    private void AddUnlinkItem(ItemsControl menu, Layer clip, Layer row)
    {
        var parentRow = _viewModel.ParentRowOf(clip);
        if (parentRow is null && !_viewModel.HasParentClip(clip))
            return;

        AddTrackItem(menu, "Unlink from Parent", () => _viewModel.UnlinkFromParent(parentRow is null ? clip : row), tip: parentRow is null
            ? "Takes this clip out of the keeping of the clip it lies over: splitting, trimming, moving or deleting that clip no longer carries it along."
            : $"Takes this track out of the mini-stack of {parentRow.Name}, its clips with it: edits there no longer carry them along.");
    }

    /// <summary>
    /// The Captions submenu of a sound's menu, filled as the caption row's is (FillCaptionMenu), with Use for Auto-Captions
    /// after Generate Auto-Captions... when the sound can be chosen as what the captions listen to.
    /// </summary>
    // <param name="target">The sound (a clip with sound), or null for what is set (the sound listened to: ListenedSound).</param>
    private void AddCaptionsSubmenu(ItemsControl menu, object? target, Action? useFor)
    {
        var captions = new MenuItem { Header = "Captions" };
        FillCaptionMenu(captions, target);
        if (useFor is not null)
        {
            var use = new MenuItem
            {
                Header = "Use for Auto-Captions",
                ToolTip = "Auto-Captions listen to this sound from now on: when the video is exported, or when Generate Auto-Captions... is chosen.",
            };
            use.Click += (_, _) => RunGuarded("Use for Auto-Captions", useFor);
            captions.Items.Insert(Math.Min(1, captions.Items.Count), use);
        }

        menu.Items.Add(captions);
    }

    private static readonly double[] GainSteps = [12, 6, 3, 0, -3, -6, -12];

    /// <summary>A sound's Gain submenu: a few usual amounts, the one it has ticked. Anything between is typed into its Gain box.</summary>
    private MenuItem GainMenu(double gainDb, Action<double> setGain)
    {
        var gain = new MenuItem { Header = string.Create(CultureInfo.InvariantCulture, $"Gain ({gainDb:+0.0;-0.0;0} dB)") };
        foreach (var step in GainSteps)
        {
            var db = step;
            var item = new MenuItem
            {
                Header = string.Create(CultureInfo.InvariantCulture, $"{db:+0;-0;0} dB{(db == 0 ? "  (As Recorded)" : "")}"),
                IsCheckable = true,
                IsChecked = Math.Abs(gainDb - db) < 0.05,
            };
            item.Click += (_, _) =>
            {
                RunGuarded("a gain", () => setGain(db));
                ScheduleClipRedraw();
            };
            gain.Items.Add(item);
        }

        return gain;
    }

    /// <summary>
    /// The audio filters dialog of a sound (a track of sound clips or of a video with sound),
    /// as its Filters button opens it: TrackFilters_Click reads what it is for from its sender's DataContext.
    /// </summary>
    private void OpenAudioFilters(object sound) => TrackFilters_Click(new FrameworkElement { DataContext = sound }, new RoutedEventArgs());

    /// <summary>A rename that takes its one step of Undo at its first keystroke, as a marker's does.</summary>
    private Action<string> RenameWithUndo(string name, Action<string> rename)
    {
        var started = false;
        return typed =>
        {
            if (!started)
            {
                _viewModel.Checkpoint($"rename {name}");
                started = true;
            }

            rename(typed);
        };
    }

    /// <summary>The two ducking boxes of a sound's right-click menu: turned down under a voice, or the voice itself.</summary>
    private void AddDuckingItems(ItemsControl menu, object sound, bool ducks, bool isVoice)
    {
        foreach (var (header, isOn, voice, tip) in new[]
                 {
                     ("Auto-Duck against Voice", ducks, false, "Turns this sound down, by the Duck By (dB) amount in the Audio settings, whenever a sound marked as a voice is speaking."),
                     ("Voiceover / Dialogue", isVoice, true, "This sound is a voice: the sounds set to Auto-Duck make room for it."),
                 })
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isOn, ToolTip = tip };
            item.Click += (_, _) => RunGuarded(header, () => _viewModel.ToggleDucking(sound, voice));
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Remove Dead Air on a video with no sound to listen to (4.3E): shown on its menu like every video track's, greyed out, with
    /// where to run it instead (the video it was cut from, whose cuts reach every unlocked track, its Cutouts among them).
    /// </summary>
    private static MenuItem DeadAirWithoutSound(Layer layer)
    {
        var item = new MenuItem
        {
            Header = "Remove Dead Air...",
            IsEnabled = false,
            ToolTip = $"{layer.Name} has no sound of its own to listen to. Run Remove Dead Air on the video it is cut from (or one with sound): its silences come out of every unlocked track at once, this one with it.",
        };
        ToolTipService.SetShowOnDisabled(item, true);
        return item;
    }

    /// <summary>A clip's Fade In and Fade Out, each a submenu of what its fade's handle menu offers (see FadeMenuItems).</summary>
    private void AddFadeSubmenus(ItemsControl menu, object clip)
    {
        var (fadeIn, fadeOut) = (FadeMenuItems(clip, fadeIn: true), FadeMenuItems(clip, fadeIn: false));
        if (fadeIn.Count == 0 && fadeOut.Count == 0)
            return;

        if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
            menu.Items.Add(new Separator());
        foreach (var (header, items) in new[] { ("Fade In", fadeIn), ("Fade Out", fadeOut) })
        {
            var submenu = new MenuItem { Header = header };
            foreach (var item in items)
                submenu.Items.Add(item);
            menu.Items.Add(submenu);
        }
    }

    /// <summary>
    /// What a fade's menu offers, for a clip: its transition styles (the one it has ticked;
    /// a sound has none), a fade of the default length (Settings) or none, and, where a clip on another track is on
    /// across that end of the clip, a crossfade with it over their overlap.
    /// </summary>
    private List<object> FadeMenuItems(object? clip, bool fadeIn)
    {
        var items = new List<object>();
        double seconds;
        TransitionStyle style;
        Action<TransitionStyle> setStyle;
        Action<double> setLength;
        Layer? layer = null;
        switch (clip)
        {
            case Layer { IsLoading: false } own when _viewModel.Layers.Contains(own):
                layer = own;
                (seconds, style) = fadeIn ? (own.FadeInSeconds, own.FadeInStyle) : (own.FadeOutSeconds, own.FadeOutStyle);
                setStyle = chosen => _viewModel.SetClipFadeStyle(own, fadeIn, chosen);
                setLength = length => _viewModel.SetClipFade(own, fadeIn, length);
                break;

            default:
                return items;
        }

        MenuItem Item(string header, Action action, bool ticked = false, string? tip = null)
        {
            var item = new MenuItem { Header = header, IsCheckable = ticked, IsChecked = ticked, ToolTip = tip };
            item.Click += (_, _) =>
            {
                RunGuarded(header, action);
                ScheduleClipRedraw();
            };
            return item;
        }

        var name = fadeIn ? "In" : "Out";
        items.Add(new MenuItem
        {
            Header = seconds > 0.005 ? string.Create(CultureInfo.InvariantCulture, $"Fade {name}: {seconds:0.##} s") : $"No Fade {name}",
            IsEnabled = false,
        });

        // A sound fades in and out; only a picture has a style to come on or go in.
        if (layer is not { IsAudio: true })
        {
            foreach (var each in TransitionStyles.All)
            {
                var chosen = each;
                var styleItem = Item(TransitionStyles.Label(chosen), () => setStyle(chosen), ticked: seconds > 0.005 && chosen == style,
                    tip: seconds > 0.005 ? null : $"Adds a fade {name.ToLowerInvariant()} of the default transition length (Settings > Timeline & Player), in this style.");
                styleItem.Icon = TransitionIcon(chosen, styleItem);
                items.Add(styleItem);
            }

            items.Add(new Separator());
        }

        var standard = AppSettings.Current.DefaultTransitionSeconds;
        if (seconds > 0.005)
            items.Add(Item($"Remove Fade {name}", () => setLength(0)));
        else
            items.Add(Item(string.Create(CultureInfo.InvariantCulture, $"Add Fade {name} ({standard:0.##} s)"), () => setLength(standard)));

        if (layer is not null && _viewModel.OverlapAt(layer, fadeIn) is var (other, overlap))
        {
            items.Add(Item(string.Create(CultureInfo.InvariantCulture, $"Crossfade with Overlapping Clip: {other.Name} ({overlap:0.##} s)"),
                () => _viewModel.CrossfadeWithOverlap(layer, fadeIn),
                tip: $"Fades this clip {name.ToLowerInvariant()} and {other.Name} {(fadeIn ? "out" : "in")} over the stretch where both are on, on their separate tracks: the one above dissolves (or wipes, slides...) over the one below, and their sounds cross."));
        }

        return items;
    }

    /// <summary>
    /// A transition style's drawn picture for its menu item (4.2B-3, <see cref="TransitionStyles.IconGeometry"/>), stroked
    /// and filled in the item's own text colour, so it turns with the words when the item is highlighted. The colour is bound to
    /// the item itself: a FindAncestor binding is tried while the icon is not yet in the item's tree, fails, and is never tried again.
    /// </summary>
    private static System.Windows.Shapes.Path TransitionIcon(TransitionStyle style, MenuItem item)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = TransitionStyles.IconGeometry(style),
            Width = EasingCurves.IconWidth,
            Height = EasingCurves.IconHeight,
            StrokeThickness = 1.1,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true,
        };
        var colour = new System.Windows.Data.Binding(nameof(Foreground)) { Source = item };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, colour);
        icon.SetBinding(System.Windows.Shapes.Shape.FillProperty, colour);
        return icon;
    }

    /// <summary>
    /// Puts a box for a name in a menu (at the top, or at <paramref name="at"/>): a sound's (the title a track of the video
    /// is saved under, or what a clip is called) or a marker's. Each keystroke renames; Enter or Escape closes the menu.
    /// </summary>
    private static void AddRenameItem(ItemsControl menu, string name, Action<string> rename, int at = 0)
    {
        var box = new TextBox { Text = name, MinWidth = 190, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text))
                rename(box.Text.Trim());
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape && menu is ContextMenu open)
                open.IsOpen = false;
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(box);
        at = Math.Clamp(at, 0, menu.Items.Count);
        menu.Items.Insert(at, new MenuItem { Header = line, StaysOpenOnClick = true, ToolTip = "The title a track of the video is saved under in the output file, or what a clip is called." });
        if (menu.Items.Count > at + 1 && menu.Items[at + 1] is not Separator)
            menu.Items.Insert(at + 1, new Separator());
    }

    /// <summary>The shape a clip alone is cut to: its mask dialog, with the Cutout Studio's pen behind Draw Custom Shape.</summary>
    private void OpenMaskTool(Layer clip) => OpenLayerTool(clip, LayerTool.Mask);

    /// <summary>Opens one of a track's modifier dialogs. What it changes can be undone as one step.</summary>
    private void OpenLayerTool(Layer layer, LayerTool tool)
    {
        _viewModel.Checkpoint(tool == LayerTool.Filters ? $"change the filters of {layer.Name}" : $"change the mask of {layer.Name}");

        var visit = _viewModel.UndoMark;
        var dialog = new LayerToolDialog(layer, _viewModel, tool) { Owner = this };
        dialog.ShowDialog();
        _viewModel.DropStepIfUnchanged(visit);
        if (dialog.RedrawRequested)
            _viewModel.DrawTargetCommand.Execute(layer);
    }
}
