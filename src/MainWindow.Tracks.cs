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

// ----- Tracks: the timeline's blocks, the editing tools (split, trim, delete, undo) and a track's right-click menus -----
public partial class MainWindow
{
    // ----- Blocks in time -----
    // Every row of the timeline (4.4: one drawn surface, UnifiedTimelineControl; its rows are made in
    // MainWindow.UnifiedTimeline.cs) has what it stands for as blocks: a track's clips, each from when it appears to when
    // it goes, the main video's among them; a video clip's own sound, while it is unlinked from its picture; an audio
    // stream of the main file, whole or in the parts it was split into; the captions. Everything is placed by one sum:
    // seconds times the surface's pixels per second. While a block is being dragged only that block is moved; what it
    // stands for is changed when it is let go, which is also when everything else (the command, Live Preview, Undo) hears of it.
    //
    // The rows are made again after a change, and when the zoom changes the scale; as the video plays only the playhead
    // slides. The pictures in a block (its file's frames, its sound) are cut from pictures already in memory and laid on
    // the file's own time, so they stay where they are in the file as the block is trimmed, and slide as it is slipped.

    private static readonly Brush MainClipBrush = Frozen(Color.FromArgb(0xB0, 0x2F, 0x6F, 0xB5));
    private static readonly Brush LayerClipBrush = Frozen(Color.FromArgb(0xD0, 0x8E, 0x5A, 0xB8));
    private static readonly Brush AudioClipBrush = Frozen(Color.FromArgb(0x38, 0x4C, 0xAF, 0x50));
    private static readonly Brush LayerAudioBrush = Frozen(Color.FromArgb(0xD0, 0x3C, 0x9A, 0x5F));
    private static readonly Brush SoundClipBrush = Frozen(Color.FromArgb(0xD0, 0x2B, 0x7A, 0x6B));
    private static readonly Brush OffClipBrush = Frozen(Color.FromArgb(0xC0, 0x55, 0x55, 0x55));
    private static readonly Brush SubtitleClipBrush = Frozen(Color.FromArgb(0xD0, 0x2E, 0x7D, 0x6B));
    private static readonly Brush MainUnderBrush = Frozen(Color.FromArgb(0x48, 0x2F, 0x6F, 0xB5));

    /// <summary>Where a clip's keyframes fall along its block, in pixels; nothing while their diamonds are switched off.</summary>
    /// <param name="shift">How far the moment the keyframes are counted from lies from the start of the block, in seconds.</param>
    private IEnumerable<double>? ClipKeys(Layer clip, double shift, double scale) =>
        _viewModel.ShowClipKeyframes && clip.IsAnimated ? clip.KeyTimes.Select(t => (t + shift) * scale) : null;

    /// <summary>An audio track, or one of the parts it was split into, as something that can be selected.</summary>
    private sealed record AudioClip(AudioTrack Track, AudioPiece? Piece);

    /// <summary>A video layer's own sound, as something that can be selected.</summary>
    private sealed record LayerSound(Layer Layer);

    // What is selected: a Layer (one clip, the main video's clips among them, or the background), an AudioClip, a LayerSound.
    private object? _selectedClip;
    private bool _mainClipSelected;
    private bool _clipRedrawQueued;

    // The rows on the surface are older than the timeline (an edit, an undo, a change of scale since they were made): made
    // again at background priority, or at once by the next press on the timeline or the end of a drag (4.4 BugHunt).
    private bool _rowsStale;

    // A block was pressed, with either button: it is what is selected now (a right-click selects it too, so that the menu that opens is about it).
    private void Timeline_BlockPressed(TimelineRow row, TimelineBlock block, MouseButtonEventArgs e)
    {
        // A subtitle on the captions' row is not a clip: it is edited in the Subtitle Editor.
        if (block.Clip is SubtitleRef)
            return;

        SelectClip(block.Clip);

        // On an audio stream a click is also what moves the playhead, as it was before the track had blocks.
        if (block.Clip is AudioClip && e.ChangedButton == MouseButton.Left && _viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(UnifiedTimeline.Surface.TimeAt(e.GetPosition(UnifiedTimeline.Surface).X) * 1000, 0, _viewModel.DurationMs);
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
    /// (its Data): the clips of a track (a video or sound track, the main video's among them), the parts of an audio stream
    /// of the main file where the main video's clips play it, the captions, or the background. Returns the blocks of the
    /// track's clips' sound that is unlinked from their pictures (each dragged on its own), for a lane of their own under
    /// the row; null when there is none.
    /// </summary>
    private List<TimelineBlock>? BuildRowBlocks(TimelineRow row, double scale, double seconds)
    {
        row.Blocks.Clear();
        if (scale <= 0 || seconds <= 0)
            return null;

        var perPixel = 1 / scale;
        switch (row.Data)
        {
            // An audio stream of the main video's file: its parts (in the file's own time) where the main video's clips play
            // them, moved along with the stream when it has been slipped. Each part shows its stretch of the stream's sound.
            case AudioTrack audio:
                var mainClips = _viewModel.GetMainClips();
                var fileLength = _viewModel.MainLayer.MediaDuration > 0 ? _viewModel.MainLayer.MediaDuration : seconds;
                var pieces = audio.Pieces.Count > 0 ? audio.Pieces : [new AudioPiece(0, fileLength, false)];
                var streamSound = _viewModel.ShowWaveforms ? Waveforms.DataOf(audio.TrackWaveform) : null;
                var streamGain = DecibelScale(audio.GainDb);

                // Each part belongs to the main video's clip it plays through (4.4 BugHunt): a drag of that clip carries it,
                // and a trim of the clip's edge takes the part on that edge with it.
                var mainIds = mainClips.Select(shown => MainClipIdAt(shown.Start, shown.End)).ToArray();
                foreach (var piece in pieces)
                {
                    var clip = new AudioClip(audio, audio.Pieces.Count > 0 ? piece : (AudioPiece?)null);
                    for (var m = 0; m < mainClips.Count; m++)
                    {
                        // At a moment of a clip the stream plays its file from where the clip is, less its slip.
                        var shown = mainClips[m];
                        var (windowFrom, windowTo) = (shown.Offset - audio.OffsetSeconds, shown.Offset - audio.OffsetSeconds + (shown.End - shown.Start));
                        var (from, to) = (Math.Max(piece.Start, windowFrom), Math.Min(piece.End, windowTo));
                        if (to - from < 0.001)
                            continue;

                        var pieceLeft = (shown.Start + from + audio.OffsetSeconds - shown.Offset) * scale;
                        var edges = (Math.Abs(from - windowFrom) <= 1e-6 ? BlockEdges.Start : BlockEdges.None)
                            | (Math.Abs(to - windowTo) <= 1e-6 ? BlockEdges.End : BlockEdges.None);
                        row.Blocks.Add(new TimelineBlock
                        {
                            Left = pieceLeft,
                            Width = Math.Max((to - from) * scale, 3),
                            Name = piece.Muted ? "silenced" : "",
                            Fill = piece.Muted ? OffClipBrush : AudioClipBrush,
                            Clip = clip,
                            Family = mainIds[m] is { } mainId ? new BlockFamily((audio.Index, piece.Start, mainId), mainId, true, edges) : null,
                            SoundWindow = streamSound is null ? null : new WaveWindow(streamSound, from, to, 1, streamGain),
                            CanDrag = () => !_viewModel.AudioLinked,
                            MayStartBeforeZero = true,
                            Commit = (left, _) =>
                            {
                                CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: slip a stream {(left - pieceLeft) * perPixel:+0.000;-0.000} s"));
                                _viewModel.SlipAudio(audio, audio.OffsetSeconds + (left - pieceLeft) * perPixel);
                            },
                            CannotDrag = "Audio and video are linked. Unlink them (the chain button) to move this track on its own.",
                        });
                    }
                }

                return null;

            // The captions: each from when it is shown to when it goes. Not dragged here: they follow the sound they were
            // heard in through every cut, and are reworded and retimed in the Subtitle Editor (right-click, Edit Captions...).
            case Layer { IsCaptions: true }:
                foreach (var (from, to, text, _) in _viewModel.GetSubtitleSegments())
                    row.Blocks.Add(new TimelineBlock { Left = from * scale, Width = Math.Max((to - from) * scale, 3), Name = text, Fill = SubtitleClipBrush, Clip = new SubtitleRef(from) });

                return null;

            // The blurred background: under everything, for as long as the sequence. Selected, the inspector shows its blur.
            case Layer { IsBackground: true } background:
                row.Blocks.Add(new TimelineBlock
                {
                    Width = Math.Max(seconds * scale, 3),
                    Name = background.IsHidden ? "hidden: the canvas behind the layers is black" : "",
                    Fill = background.IsHidden ? OffClipBrush : MainUnderBrush,
                    Clip = background,
                    CannotDrag = "The blurred background lasts as long as the sequence: it is not moved.",
                });

                return null;

            // An empty track: nothing on it yet (its row says to drop something there).
            case Layer { IsEmptyTrack: true }:
                return null;

            // A track: its clips, each from when it appears to when it goes. The main video's are clips like any other
            // (4.3C): moved, trimmed from either edge, rolled, slipped, rippled, put on another track, on any track.
            case Layer layer:
                return AddTrackBlocks(row, layer, scale, perPixel, seconds);
        }

        return null;
    }

    /// <summary>The id of the main video's clip that plays a stretch of the sequence (seconds), as the player's list of them has it; null for none.</summary>
    private Guid? MainClipIdAt(double start, double end)
    {
        foreach (var track in _viewModel.Document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.ContentKind == LayerKind.MainVideo && Math.Abs(clip.Start - start) < 1e-4 && Math.Abs(clip.End - end) < 1e-4)
                    return clip.Id;
            }
        }

        return null;
    }

    /// <summary>The blocks of a track's clips on its row; returns the blocks of their unlinked sound (see BuildRowBlocks).</summary>
    private List<TimelineBlock>? AddTrackBlocks(TimelineRow row, Layer layer, double scale, double perPixel, double seconds)
    {
        List<TimelineBlock>? sounds = null;
        var clipsOnTrack = _viewModel.GetTrackClips(layer);
        var gainLines = AppSettings.Current.ShowClipGainLines;
        foreach (var clip in clipsOnTrack)
        {
            var (from, to) = clip.GetSpan(seconds);
            var duration = to - from;
            var main = clip.IsMainVideo;
            var speed = ClipSpeed(clip);
            var linked = _viewModel.IsClipAudioLinked(clip);
            var slip = linked ? 0 : _viewModel.ClipAudioSlip(clip);
            var split = _viewModel.ClipAudioSplit(clip);

            // Its sound is drawn in its block while it is linked to its picture (a sound clip's always): the main video's is
            // its file's (the timeline's waveform), a video clip's its own. Unlinked, it has a block of its own (below).
            var showsSound = !clip.IsHidden && (clip.IsAudio || (linked && _viewModel.ShowLinkedAudio && (main || clip.CarriesSound)));
            var gainLine = showsSound && !main && !clip.IsLoading && gainLines;
            var name = clip.IsLoading ? "Loading..." : main ? (clip.IsHidden ? $"{clip.Name} (picture hidden)" : clipsOnTrack.Count > 1 ? "" : clip.Name)
                : clip.IsHidden ? "hidden" : clip.IsAudio ? clip.Name : "";

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
                Fill = clip.IsLoading || clip.IsHidden ? OffClipBrush : main ? MainClipBrush : clip.IsAudio ? SoundClipBrush : LayerClipBrush,
                Clip = clip,
                Family = FamilyOf(model),

                // Its frames, from its file's strip, and its sound: both laid on the file's own time.
                Film = clip.IsHidden ? null
                    : main ? FilmOf(clip.Filmstrip, _viewModel.SourceWidth, _viewModel.SourceHeight, clip.MediaOffset, clip.MediaDuration, speed)
                    : clip.IsVideoFile && !clip.IsLoading ? FilmOf(clip.Filmstrip, clip.ImageWidth, clip.ImageHeight, clip.MediaOffset, clip.MediaDuration, speed)
                    : null,
                SoundWindow = showsSound ? SoundOf(main ? _viewModel.TimelineWaveform : clip.Waveform, clip.MediaOffset, duration, speed, main ? 0 : clip.GainDb) : null,
                Keys = ClipKeys(clip, 0, scale)?.ToList(),
                Badge = BadgeOf(speed, slip),
                CanDrag = AlwaysDraggable,
                Resizable = true,

                // Let go a little over the clip it meets: a crossfade instead (see CommitClipMove).
                Commit = (newLeft, newWidth) => CommitClip(clip, built, left, width, newLeft, newWidth, perPixel),

                // Its left edge, the cut it shares with the next clip (a roll), Alt inside it (a slip), Ctrl on an edge (a ripple).
                TrimmableStart = clip.Duration > 0.001 && !clip.IsLoading,
                Slippable = clip.Duration > 0.001 && (clip.IsVideoFile || clip.IsAudio || main),
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

                // Dragged up or down onto another row: onto that track (an empty one, or one of its kind with room).
                CanMoveTo = clip.IsAudio ? null : (target, at) =>
                    target is Layer track && LiveClip(clip, built, quiet: true) is { } live && _viewModel.CanMoveClipToTrack(live, track, StartOf(built, left, at, perPixel)),
                MoveTo = clip.IsAudio ? null : (target, at) =>
                {
                    if (target is not Layer track || LiveClip(clip, built) is not { } live)
                        return;

                    CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"drag: onto another track, {(at - left) * perPixel:+0.000;-0.000} s"));
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
                    SoundWindow = SoundOf(clip.Waveform, clip.MediaOffset, duration, speed, clip.GainDb),
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

    /// <summary>A block's tag: its speed when that is not 1 ("2x"), and how far its unlinked sound was slipped ("+120 ms"); null for neither.</summary>
    private static string? BadgeOf(double speed, double slip)
    {
        var speedText = Math.Abs(speed - 1) > 0.001 ? string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x") : null;
        var slipText = Math.Abs(slip) >= 0.0005 ? string.Create(CultureInfo.InvariantCulture, $"{(slip > 0 ? "+" : "-")}{Math.Abs(slip) * 1000:0} ms") : null;
        return speedText is null ? slipText : slipText is null ? speedText : $"{speedText}  {slipText}";
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

    private bool IsSelected(object? clip) => clip is null ? _mainClipSelected && _selectedClip is null : Equals(clip, _selectedClip);

    private void SelectClip(object? clip)
    {
        (_selectedClip, _mainClipSelected) = (clip, clip is null);
        _viewModel.KeyLayer = clip switch { Layer { IsPicture: true } layer => layer, _ => _viewModel.KeyLayer };

        // The outline is drawn by the surface from what is selected; the inspector shows the selection's values.
        UnifiedTimeline.Surface.Redraw();
        UpdateTimelineInspector();
    }

    /// <summary>Nothing is selected any more (after a split, a delete, an undo): the main video's clip at the playhead is what the tools work on.</summary>
    private void ClearClipSelection()
    {
        (_selectedClip, _mainClipSelected) = (null, false);
        UpdateTimelineInspector();
    }

    /// <summary>
    /// A clip's block let go after a trim of its start, a roll, a slip or a ripple (see TimelineTrack): the view model
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

    /// <summary>The razor: cuts the selected block in two at the playhead. Nothing selected means the main video's clip there.</summary>
    private void SplitSelectedClip()
    {
        LetGoOfDrag();
        var at = _viewModel.PositionMs / 1000;
        CrashReporter.Note("timeline", string.Create(CultureInfo.InvariantCulture, $"split at {at:0.000} s"));
        switch (_selectedClip)
        {
            case Layer layer when _viewModel.Layers.Contains(layer):
                _viewModel.SplitLayer(layer, at);
                break;
            case AudioClip audio:
                _viewModel.SplitAudio(audio.Track, at);
                break;
            case LayerSound sound when _viewModel.Layers.Contains(sound.Layer):
                _viewModel.SplitLayer(sound.Layer, at);
                break;
            default:
                // Nothing selected: the main video's clip at the playhead is what is split.
                _viewModel.SplitLayer(_viewModel.MainLayer, at);
                break;
        }

        ClearClipSelection();
        ScheduleClipRedraw();
    }

    private void TrimStart_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: true);

    private void TrimEnd_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: false);

    private void TrimSelectedClip(bool start)
    {
        // A clip's sound is trimmed as its clip is; nothing selected, the main video's clip at the playhead.
        LetGoOfDrag();
        var clip = _selectedClip is LayerSound sound ? sound.Layer : _selectedClip ?? _viewModel.MainLayer;
        _viewModel.TrimClip(clip, start, _viewModel.PositionMs / 1000);
        ScheduleClipRedraw();
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    /// <summary>Delete: a clip goes (the main video's too); a part of an audio track is silenced (and, deleted again, plays again).</summary>
    private void DeleteSelectedClip()
    {
        LetGoOfDrag();
        switch (_selectedClip)
        {
            case AudioClip { Piece: { } piece } audio:
                _viewModel.ToggleAudioPiece(audio.Track, piece);
                break;
            case AudioClip audio:
                var split = KeybindManager.GestureText("edit.split") is { Length: > 0 } keys ? $"Split it at the playhead ({keys})" : "Split it at the playhead";
                _viewModel.StatusText = $"{audio.Track.Title} is still whole. {split} first, then delete the part to silence; or set the track to Ignore / Drop to leave it out altogether.";
                return;
            case LayerSound sound:
                _viewModel.StatusText = $"The sound belongs to {sound.Layer.Name}: delete or hide the layer to take it out.";
                return;
            default:
                if (!_viewModel.DeleteClip(_selectedClip))
                    return;
                break;
        }

        ClearClipSelection();
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
    /// <param name="rowData">The row's Data: a track's row (a Layer: a video, picture or sound track, the main video's, the caption row, the background) or one of the main file's audio tracks (an AudioTrack); null for the space below the rows.</param>
    /// <param name="clicked">The pressed block's Clip (a Layer, an AudioClip, a LayerSound, a SubtitleRef), or null.</param>
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
            case AudioTrack track:
                FillAudioTrackMenu(menu, track, onHeader ? null : clicked as AudioClip);
                break;
            case Layer layer:
                FillLayerMenu(menu, layer, onHeader ? null : clicked);
                break;
            default:
                return;
        }

        TidySeparators(menu, first);
    }

    /// <summary>
    /// A track row's menu (a Layer: a video, picture or sound track, the main video's, the caption row, the background): the
    /// track's own modifiers, where its clip sits on the frame, the clip the menu is about (split, trim, fades, speed, its
    /// sound), the track's order and link, its visibility, what its sound is listened to for, and deleting.
    /// </summary>
    private void FillLayerMenu(ItemsControl menu, Layer layer, object? clicked)
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

        var at = _viewModel.PositionMs / 1000;

        // An empty track has nothing to set: it can only leave the mini-stack it is in, or be deleted.
        if (layer.IsEmptyTrack)
        {
            AddUnlinkItem(menu, layer, layer);
            Add("Delete Track", () => _viewModel.RemoveTrack(layer));
            return;
        }

        // A sound's name, typed in place at the top of its menu (one step of Undo, taken at the first keystroke).
        if (layer.IsAudio)
        {
            AddRenameItem(menu, clip.Title, RenameWithUndo(clip.Name, name => clip.Title = name), menu.Items.Count);
            Separate();
        }

        // The caption row: the captions' own menu (making, editing, styling, placing, importing and deleting them).
        if (layer.IsCaptions)
        {
            FillCaptionMenu(menu, null);
            Separate();
        }

        // The track's own modifiers.
        if (layer.HasFilters)
        {
            Add("Filters...", () => OpenLayerTool(layer, LayerTool.Filters));
            Add("Position & Transform...", () => OpenLayerTool(layer, LayerTool.Transform));
        }
        if (layer.HasMask)
            Add(layer is { IsVideo: true, CustomMask: true } ? "Edit Mask..." : "Add Mask...", () => OpenLayerTool(layer, LayerTool.Mask));
        // The caption row's are its Caption Position & Look..., above.
        if (layer is { HasStyle: true, IsCaptions: false })
            Add("Properties...", () => ShowLayerProperties(layer));
        if (layer.IsBackground)
            Add("Blur Settings...", () => BlurSettings_Click(this, new RoutedEventArgs()));
        if (layer.IsVideo)
            Add("Draw Target", () => _viewModel.DrawTargetCommand.Execute(layer));
        if (layer is { UsesFrameMask: true, CustomMask: true } && File.Exists(layer.MaskPath.Trim().Trim('"')))
            Add("Add More Pieces of This Mask...", () => AddLayersFromMask(layer.MaskPath.Trim().Trim('"')));

        // Where the clip sits on the frame (4.4: each clip's own): fitted or filling, given to the rest of its track, and the
        // blurred copy of itself that can fill the frame around it.
        if (clip is { HasPlacement: true, IsCaptions: false } && _viewModel.ClipOf(clip) is not null)
        {
            Separate();
            Add("Fit to Frame", () => _viewModel.FitClipToFrame(clip, fill: false),
                tip: "Sizes the clip so that the whole picture shows, touching the frame on one side, and puts its middle on the frame's.");
            Add("Fill Frame", () => _viewModel.FitClipToFrame(clip, fill: true),
                tip: "Sizes the clip to cover the whole frame, what reaches past it off its edges, and puts its middle on the frame's.");
            if (clips.Count > 1)
            {
                Add("Apply Placement to All Clips on Track", () => _viewModel.ApplyPlacementToTrack(clip),
                    tip: $"Gives every other clip on this track the place of {clip.Name}: its corner, size and shape, its turn and anchor, and its crop. Their looks, timing and keyframes stay theirs.");
            }

            if (clip.IsMainVideo || clip.IsVideoFile)
            {
                var blur = new MenuItem
                {
                    Header = "Blur Fill Background",
                    IsCheckable = true,
                    IsChecked = _viewModel.IsBlurFilled(clip),
                    ToolTip = clip.IsMainVideo
                        ? "Fills the frame around this clip with the blurred background (Blur Settings on the background's row). Unticked, what is under it shows."
                        : "Fills the frame around this clip with a blurred copy of itself, under it. It costs one more decoder while it plays.",
                };
                blur.Click += (_, _) =>
                {
                    RunGuarded("Blur Fill Background", () => _viewModel.SetBlurFill(clip, blur.IsChecked));
                    ScheduleClipRedraw();
                };
                menu.Items.Add(blur);
            }
        }

        if (layer.HasTiming)
        {
            Separate();
            Add("Split at Playhead", () => _viewModel.SplitLayer(clip, at), gesture: "edit.split");
            Add("Trim Start to Playhead", () => _viewModel.TrimClip(clip, start: true, at), gesture: "edit.trimStart");
            Add("Trim End to Playhead", () => _viewModel.TrimClip(clip, start: false, at), gesture: "edit.trimEnd");
            Add("Split or Trim at Timecode...", () => SplitTrimByTimecode(clip));
            Add("Ripple Delete", () => _viewModel.RippleDelete(clip), gesture: "edit.rippleDelete");

            // Silences Split & Mark folded out at its cuts (4.3D): each put back where it was, what follows sliding on; and
            // (4.4 BugHunt) each stretch put back over the clip folded out again, what follows sliding back.
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

            // The cuts it shares with the clips before and after it on its track: a crossfade across either.
            var transition = AppSettings.Current.DefaultTransitionSeconds;
            if (_viewModel.ClipBefore(clip) is { } previous)
            {
                if (clip.TransitionIn > 0)
                    Add(string.Create(CultureInfo.InvariantCulture, $"Remove Crossfade from {previous.Name} ({clip.TransitionIn:0.##} s)"), () => _viewModel.SetCrossfade(clip, 0));
                else
                    Add(string.Create(CultureInfo.InvariantCulture, $"Crossfade from {previous.Name} ({transition:0.##} s)"), () => _viewModel.SetCrossfade(clip, transition));
            }

            if (clips.FirstOrDefault(c => ReferenceEquals(_viewModel.ClipBefore(c), clip)) is { } following)
            {
                if (following.TransitionIn > 0)
                    Add(string.Create(CultureInfo.InvariantCulture, $"Remove Crossfade into {following.Name} ({following.TransitionIn:0.##} s)"), () => _viewModel.SetCrossfade(following, 0));
                else
                    Add(string.Create(CultureInfo.InvariantCulture, $"Crossfade into {following.Name} ({transition:0.##} s)"), () => _viewModel.SetCrossfade(following, transition));
            }

            // Its own fades, over whatever is under it (the handles at its top corners), and a crossfade with a clip on
            // another track that it overlaps.
            AddFadeSubmenus(menu, clip);
            if (layer.IsMainVideo)
            {
                // The main video's own: back to one clip of the whole file at the start, and its place on the frame.
                Add("Restore Whole Main Video", () => _viewModel.ResetMainTiming(),
                    clips.Count > 1 || clip.StartTime > 0.001 || clip.MediaOffset > 0.001,
                    "Puts the main video back as one clip of its whole file, at the start of the timeline.");
                Add("Reset Position and Zoom", () => _viewModel.ResetCenterVideoCommand.Execute(null),
                    tip: "Puts every clip of the main video back in the middle of the frame, unzoomed.");
            }
            else
            {
                Add("Extend to Whole Sequence", () => _viewModel.ShowThroughout(clip),
                    clip.StartTime > 0.001 || clip.StartTime + clip.Duration < _viewModel.SequenceSeconds - 0.001,
                    clip.IsAudio ? "Plays the sound from the start of the sequence to its end." : "Shows the clip from the start of the sequence to its end.");
            }
        }

        if (clip.IsAnimated)
        {
            Separate();
            Add("Clear Keyframes", () => _viewModel.ClearKeys(clip));
        }

        // How fast the clip runs: a few usual speeds, the one it has ticked. Anything between is typed into its Speed box.
        if (clip.HasSpeed)
        {
            Separate();
            var speeds = new MenuItem { Header = string.Create(CultureInfo.InvariantCulture, $"Speed ({clip.Speed:0.##}x)") };
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

            menu.Items.Add(speeds);
        }

        // Live Preview's proxy for the clip's file (see ProxyCache): when it is heavy, always, or never.
        if (clip.IsVideoFile || (clip.IsMainVideo && AppSettings.Current.ProxyScope == AppSettings.ProxyAllVideos))
        {
            Separate();
            var proxy = new MenuItem { Header = "Proxy" };
            proxy.Items.Add(new MenuItem { Header = _viewModel.ProxyStatus(clip), IsEnabled = false });
            proxy.Items.Add(new Separator());
            foreach (var (header, choice, tip) in new[]
                     {
                         ("Automatic (When the File Is Heavy)", ProxyChoice.Auto, "A proxy is made when the file is 4K, AV1, VP9, ProRes, 10-bit HEVC or far between I-frames (Settings, Proxies)."),
                         ("Make Proxy", ProxyChoice.Make, "A proxy is made for this file whatever it is, and Live Preview plays it in its place. Exports always read the original."),
                         ("Use Original", ProxyChoice.Original, "Live Preview plays the original file, even when it has a proxy."),
                     })
            {
                var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = clip.ProxyChoice == choice, ToolTip = tip };
                item.Click += (_, _) => RunGuarded("a clip's proxy", () => _viewModel.SetProxyChoice(clip, choice));
                proxy.Items.Add(item);
            }

            menu.Items.Add(proxy);
        }

        // Its sound: how loud the clip is, the track's filters, the clip's link to its picture (J and L cuts, slips), and what
        // the track's sound does when someone is speaking.
        var linkable = !clip.IsAudio && _viewModel.ClipOf(clip) is { HasSound: true };
        if (layer.CarriesSound || clip.CarriesSound || linkable)
        {
            Separate();
            if (clip.CarriesSound)
                menu.Items.Add(GainMenu(clip.GainDb, gain => _viewModel.SetClipGain(clip, gain)));
            if (layer.CarriesSound)
                Add("Audio Filters...", () => OpenAudioFilters(layer));
            if (linkable)
                AddLinkItem(menu, clip, _viewModel.ClipOf(clip) is { AudioLinked: true });
            if (layer.CarriesSound)
                AddDuckingItems(menu, layer, layer.AutoDuck, layer.IsVoice);
        }

        Separate();
        if (layer.CanReorder && !layer.IsAudio)
        {
            Add("Bring Forward", () => _viewModel.MoveLayerUpCommand.Execute(layer), tip: "Moves this track one place towards the front: it is drawn over the track above it now.");
            Add("Send Backward", () => _viewModel.MoveLayerDownCommand.Execute(layer), tip: "Moves this track one place towards the back: it is drawn under the track below it now.");
        }

        AddUnlinkItem(menu, clip, clips[0]);

        Separate();
        if (layer.IsRemovable || layer.IsMainVideo || layer.IsBackground)
            Add(layer.IsHidden ? "Show" : layer.IsAudio ? "Mute" : "Hide", () => _viewModel.ToggleHidden(layer));
        if (!layer.IsRemovable && !layer.IsMainVideo)
            return;

        Add("Duplicate", () => _viewModel.DuplicateLayer(layer));

        // What its sound is listened to for: the captions (the main video's through its file's tracks, as they are set; a clip
        // with sound through its own file) and the silences.
        if (layer.IsMainVideo)
            AddCaptionsSubmenu(menu, null, null);
        else if (clip.CarriesSound)
            AddCaptionsSubmenu(menu, clip, () => _viewModel.UseLayerAudioForCaptions(clip));

        // Remove Dead Air on every track of video or sound (4.3E). A clip with sound of its own is listened to and cut
        // itself; the main video listens to its file's sound and cuts every unlocked track, the pieces cut from it among
        // them; a picture cut from the main video, or a video with no sound, has none of its own to listen to.
        if (clip.CarriesSound && !layer.IsMainVideo)
        {
            Add("Remove Dead Air...", () => RunDeadAir(clip), _viewModel.HasSource,
                "Finds the silences in this clip's own sound (or the gaps between its captioned words) and cuts them out of it: Split & Mark folds them out of its track (each can be put back from the clip's menu), or they are deleted.");
        }
        else if (layer.IsMainVideo)
        {
            Add("Remove Dead Air...", () => RunDeadAir(null), _viewModel.HasSource,
                "Finds the silences in the main video's sound (its Voice track when one is marked, else its first: right-click another of its audio tracks to listen to that one) and cuts them out of every unlocked track at once, the HUD pieces and other layers with it.");
        }
        else if (layer.IsVideo || layer.IsVideoFile)
        {
            menu.Items.Add(DeadAirWithoutSound(layer));
        }

        Separate();
        if (clips.Count > 1)
            Add("Delete Clip", () => _viewModel.DeleteClip(clip), gesture: "edit.delete");
        Add("Delete Track", () => _viewModel.RemoveTrack(layer),
            tip: clips.Count > 1 ? $"Deletes the whole track, its {clips.Count} clips with it (Undo brings them back)." : null);
    }

    /// <summary>
    /// The menu of one of the main file's audio tracks (a child row of the main video's, 4.4): its name, how loud it is,
    /// whether it is heard, its filters and ducking; the part of it that was clicked (split, silenced); and what it is
    /// listened to for (the captions, the silences cut out of the main video).
    /// </summary>
    private void FillAudioTrackMenu(ItemsControl menu, AudioTrack track, AudioClip? clicked)
    {
        AddRenameItem(menu, track.Title, RenameWithUndo(track.Title, name => track.Title = name), menu.Items.Count);
        AddSeparator(menu);

        menu.Items.Add(GainMenu(track.GainDb, gain => SetTrackGain(track, gain)));
        var mute = new MenuItem
        {
            Header = "Mute",
            IsCheckable = true,
            IsChecked = track.IsMuted,
            ToolTip = "Leaves this track out of the mix and of the output (the same as Ignore / Drop).",
        };
        mute.Click += (_, _) =>
        {
            RunGuarded("a track's mute", () =>
            {
                _viewModel.Checkpoint(mute.IsChecked ? $"mute {track.Title}" : $"unmute {track.Title}");
                track.IsMuted = mute.IsChecked;
            });
            ScheduleClipRedraw();
        };
        menu.Items.Add(mute);
        AddTrackItem(menu, "Audio Filters...", () => OpenAudioFilters(track));
        AddSeparator(menu);
        AddDuckingItems(menu, track, track.AutoDuck, track.IsVoice);

        // The part of it that was clicked: the razor at the playhead, and that part silenced or played again, as Split and
        // Delete do.
        if (clicked is not null)
        {
            AddSeparator(menu);
            AddTrackItem(menu, "Split at Playhead", () =>
            {
                SelectForMenu(clicked);
                SplitSelectedClip();
            }, gesture: "edit.split");
            if (clicked.Piece is { } piece)
            {
                AddTrackItem(menu, piece.Muted ? "Restore Part" : "Silence Part", () =>
                {
                    SelectForMenu(clicked);
                    DeleteSelectedClip();
                }, gesture: "edit.delete", tip: piece.Muted ? "This part of the track plays again." : "This part of the track is silenced; the rest plays on.");
            }
        }

        // What this track's sound is listened to for (4.3E): the captions, and the silences cut out of the main video.
        AddSeparator(menu);
        AddCaptionsSubmenu(menu, track, () => _viewModel.UseTrackForCaptions(track));
        AddTrackItem(menu, "Remove Dead Air...", () => RunDeadAir(track), _viewModel.HasSource,
            "Finds the silences in this track and cuts them out of the main video, from every unlocked track at once: listening to the microphone's track finds the pauses in the speech.");
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

    /// <summary>
    /// Toggle A/V Link for a clip with a picture and a sound (the main video's, a video file's): linked, its sound moves and
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
    /// Unlink from Parent, when the clip's track is in another track's mini-stack (a style's HUD cut-out under the main video's)
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
    /// <param name="target">The sound (an audio track, a clip with sound), or null for what is set (the main video's).</param>
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
    /// One of the main file's audio tracks made louder or quieter, as one step of Undo. Gain is heard in a track that is
    /// encoded again: one copied as it is cannot be changed, so it is encoded again from now on.
    /// </summary>
    private void SetTrackGain(AudioTrack track, double gainDb)
    {
        if (Math.Abs(track.GainDb - gainDb) < 0.05)
            return;

        _viewModel.Checkpoint($"change the gain of {track.Title}");
        track.GainDb = gainDb;
        if (Math.Abs(gainDb) >= 0.05 && track.IsPassthrough)
            track.Action = AudioTrack.Reencode;
        _viewModel.StatusText = string.Create(CultureInfo.InvariantCulture, $"{track.Title}: {gainDb:+0.0;-0.0;0} dB");
    }

    /// <summary>
    /// The audio filters dialog of a sound (one of the main file's tracks, a track of sound clips or of a video with sound),
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
    /// Remove Dead Air on a video track with no sound of its own (4.3E): shown on its menu like every video track's, greyed
    /// out, with where to run it instead (the main video's track, whose cuts reach every unlocked track, its children among them).
    /// </summary>
    private static MenuItem DeadAirWithoutSound(Layer layer)
    {
        var item = new MenuItem
        {
            Header = "Remove Dead Air...",
            IsEnabled = false,
            ToolTip = $"{(layer.IsVideo ? "This piece of the main video" : "This video")} has no sound of its own to listen to. Run Remove Dead Air on the main video's track: its silences come out of every unlocked track at once, this one with it.",
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
    /// What a fade's menu offers, for a clip or a clip of the main video: its transition styles (the one it has ticked;
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

    /// <summary>Opens one of a track's modifier dialogs. What it changes can be undone as one step.</summary>
    private void OpenLayerTool(Layer layer, LayerTool tool)
    {
        _viewModel.Checkpoint(tool switch
        {
            LayerTool.Filters => $"change the filters of {layer.Name}",
            LayerTool.Transform => $"move or turn {layer.Name}",
            _ => $"change the mask of {layer.Name}",
        });

        var visit = _viewModel.UndoMark;
        var dialog = new LayerToolDialog(layer, _viewModel, tool) { Owner = this };
        dialog.ShowDialog();
        _viewModel.DropStepIfUnchanged(visit);
        if (dialog.RedrawRequested)
            _viewModel.DrawTargetCommand.Execute(layer);
    }
}
