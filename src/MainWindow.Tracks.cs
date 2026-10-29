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

// ----- Tracks: the time bars' blocks, the editing tools (split, trim, delete, undo) and a track's right-click menus -----
public partial class MainWindow
{
    // ----- Blocks in time -----
    // Every row of the Layers tab, and every track of the Audio tab, has a time bar as long as the video, with
    // what it stands for as blocks: a track's clips, each from when it appears to when it goes; the main
    // video's cut segments; a video layer's own sound; an audio track, whole or in the parts it was split into;
    // a sound added from a file. Everything is placed by one sum: seconds / length * width. While a block is
    // being dragged only that block is moved; what it stands for is changed when it is let go, which is also
    // when everything else (the command, Live Preview, Undo) hears of it.
    //
    // A bar is drawn once and left alone: as the video plays only its playhead line moves. The pictures in a
    // block (frames of a video layer, a waveform) are brushes and crops of pictures already in memory.

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

    // The bars that are on screen now (the lists make and drop rows as they scroll).
    private readonly List<TimelineTrack> _clipTracks = [];

    // What is selected: a Layer (one clip, the main video's clips among them), an AudioClip, a LayerSound.
    private object? _selectedClip;
    private bool _mainClipSelected;
    private bool _clipRedrawQueued;

    private void ClipTrack_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TimelineTrack track && !_clipTracks.Contains(track))
        {
            _clipTracks.Add(track);
            track.IsSelected = IsSelected;
            track.BlockPressed += ClipTrack_BlockPressed;
            track.DragRefused += ClipTrack_DragRefused;
            track.DragEnded += ScheduleClipRedraw;
            track.ZoomRequested += ClipTrack_ZoomRequested;
            track.PanRequested += ClipTrack_PanRequested;
            track.SnapTimes = SnapTimes;
            track.FrameRate = () => _viewModel.SourceFrameRate;
        }

        DrawTrack(sender as TimelineTrack);
        ScheduleChromeAlign();
    }

    private void ClipTrack_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TimelineTrack track && _clipTracks.Remove(track))
        {
            track.BlockPressed -= ClipTrack_BlockPressed;
            track.DragRefused -= ClipTrack_DragRefused;
            track.DragEnded -= ScheduleClipRedraw;
            track.ZoomRequested -= ClipTrack_ZoomRequested;
            track.PanRequested -= ClipTrack_PanRequested;
        }
    }

    private void ClipTrack_Changed(object sender, SizeChangedEventArgs e)
    {
        DrawTrack(sender as TimelineTrack);
        ScheduleChromeAlign();
    }

    private void ClipTrack_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => DrawTrack(sender as TimelineTrack);

    // A block was pressed, with either button: it is what is selected now (a right-click selects it too, so that the menu that opens is about it).
    private void ClipTrack_BlockPressed(TimelineBlock block, MouseButtonEventArgs e)
    {
        // A subtitle on the caption layer's bar is not a clip: it is edited in the Subtitle Editor.
        if (block.Clip is SubtitleRef)
            return;

        SelectClip(block.Clip);

        // On an audio track a click is also what moves the playhead, as it was before the track had blocks.
        if (block.Clip is AudioClip && e.ChangedButton == MouseButton.Left && e.Source is TimelineTrack { ActualWidth: > 0 } track)
            _viewModel.PositionMs = Math.Clamp(TimeAt(track, e.GetPosition(track).X) * 1000, 0, _viewModel.DurationMs);
    }

    private void ClipTrack_DragRefused(string why) => _viewModel.StatusText = why;

    /// <summary>A subtitle drawn on the caption layer's bar.</summary>
    private sealed record SubtitleRef(double Start);

    // A click on a bar, beside its blocks, moves the playhead to that moment.
    private void ClipTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TimelineTrack { ActualWidth: > 0 } track && _viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(TimeAt(track, e.GetPosition(track).X) * 1000, 0, _viewModel.DurationMs);
    }

    /// <summary>Redraws the bars once, however many things have just changed.</summary>
    private void ScheduleClipRedraw()
    {
        if (_clipRedrawQueued || _clipTracks.Exists(t => t.IsDragging))
            return;

        _clipRedrawQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _clipRedrawQueued = false;
            foreach (var track in _clipTracks.ToList())
                DrawTrack(track);
            UpdateTimelineChrome();
            UpdateMasterStretch();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Draws one bar: its block or blocks, and the playhead.</summary>
    private void DrawTrack(TimelineTrack? track)
    {
        if (track is null)
            return;

        // A bar whose block is being dragged is never laid out again under the pointer: it only moves along with the
        // view (the drag scrolling the time bars at an edge).
        if (track.IsDragging)
        {
            if (track.ActualWidth > 0)
            {
                track.ViewOffsetX = _viewStart * track.ActualWidth / Math.Max(ViewSpan, 1e-6);
                track.InvalidateVisual();
            }

            return;
        }

        track.Blocks.Clear();
        track.PlayheadX = double.NaN;
        track.InvalidateVisual();
        var seconds = _viewModel.DurationMs / 1000;
        // A video layer's sound is drawn on the bar under its picture on the Layers tab ("audio"), and on
        // the layer's row of the Audio tab ("row"), where it is listed with the other sounds.
        var onAudioTab = track.Tag as string == "row";
        var isSound = track.Tag as string == "audio" || (onAudioTab && track.DataContext is Layer { IsVideoFile: true });
        var show = seconds > 0 && track.DataContext switch
        {
            AudioTrack => true,

            // A video layer's sound has a bar of its own only while it is unlinked from its picture; linked, it is drawn on the picture's block.
            // On the Audio tab its row always shows it.
            Layer layerOf when isSound => (onAudioTab || !_viewModel.AudioLinked) && layerOf is { IsVideoFile: true, HasAudio: true },
            Layer { IsCaptions: true } => _viewModel.GetSubtitleSegments().Count > 0,
            Layer { IsMainVideo: true } or Layer { HasTiming: true } => true,
            _ => false,
        };
        track.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || track.ActualWidth <= 0)
            return;

        // The bar is laid out at the view's scale (a second is so many pixels), and the part of it that is seen is
        // slid into view. Past the end of the sequence it is shaded: room to drop or drag a clip into.
        ClampView();
        var scale = track.ActualWidth / Math.Max(ViewSpan, 1e-6);
        var width = seconds * scale;
        track.ViewOffsetX = _viewStart * scale;
        (track.EndX, track.PixelsPerSecond) = (width, scale);
        var perPixel = 1 / scale;
        var height = Math.Max(track.ActualHeight - 2, 4);
        switch (track.DataContext)
        {
            // An audio track of the main video's file: its parts (in the file's own time) where the main video's clips play
            // them, moved along with the track when it has been slipped.
            case AudioTrack audio:
                var mainClips = _viewModel.GetMainClips();
                var fileLength = _viewModel.MainLayer.MediaDuration > 0 ? _viewModel.MainLayer.MediaDuration : seconds;
                var pieces = audio.Pieces.Count > 0 ? audio.Pieces : [new AudioPiece(0, fileLength, false)];
                foreach (var piece in pieces)
                {
                    var clip = new AudioClip(audio, audio.Pieces.Count > 0 ? piece : (AudioPiece?)null);
                    foreach (var shown in mainClips)
                    {
                        // At a moment of a clip the track plays its file from where the clip is, less its slip.
                        var (from, to) = (Math.Max(piece.Start, shown.Offset - audio.OffsetSeconds), Math.Min(piece.End, shown.Offset - audio.OffsetSeconds + (shown.End - shown.Start)));
                        if (to - from < 0.001)
                            continue;

                        var pieceLeft = (shown.Start + from + audio.OffsetSeconds - shown.Offset) * scale;
                        AddClip(track, pieceLeft, (to - from) * scale, piece.Muted ? "silenced" : "",
                            piece.Muted ? OffClipBrush : AudioClipBrush, clip, canDrag: () => !_viewModel.AudioLinked, resizable: false,
                            commit: (left, _) => _viewModel.SlipAudio(audio, audio.OffsetSeconds + (left - pieceLeft) * perPixel),
                            cannotDrag: "Audio and video are linked. Unlink them (the chain button) to move this track on its own.");
                    }
                }

                // The picture of the sound is of the whole file, and lies where the file does for the main video's first clip.
                if (track.Parent is Grid { Children: [Image waveform, ..] })
                {
                    var fileAt = mainClips.Count > 0 ? mainClips[0].Start - mainClips[0].Offset : 0;
                    var waveLeft = (audio.OffsetSeconds + fileAt) * scale;
                    waveform.Margin = new Thickness(waveLeft - track.ViewOffsetX, 0, track.ActualWidth - (waveLeft - track.ViewOffsetX) - fileLength * scale, 0);
                }

                break;

            // A video layer's own sound, unlinked: each clip's starts with the clip, or wherever it was slipped to.
            case Layer sound when isSound:
                foreach (var clip in _viewModel.GetTrackClips(sound))
                {
                    var (from, to) = clip.GetSpan(seconds);
                    AddClip(track, (from + clip.AudioOffset) * scale, (to - from) * scale, "sound", clip.IsHidden ? OffClipBrush : LayerAudioBrush, new LayerSound(clip),
                        canDrag: () => !_viewModel.AudioLinked, resizable: false, peaks: MediaPeaks(clip.Waveform, clip, scale),
                        commit: (left, _) =>
                        {
                            _viewModel.Checkpoint($"move the sound of {clip.Name}");
                            clip.AudioOffset = Math.Round(left * perPixel - clip.StartTime, 2);
                        },
                        cannotDrag: "Audio and video are linked: the sound follows its layer. Unlink them (the chain button) to move it on its own.");
                }

                break;

            // The caption layer: the subtitles, each from when it is shown to when it goes. Not dragged here:
            // they are retimed and reworded in the Subtitle Editor (right-click, Manual Edit).
            case Layer { IsCaptions: true }:
                foreach (var (from, to, text, _) in _viewModel.GetSubtitleSegments())
                {
                    // Off the bar altogether: not worth a block.
                    if (to * scale < 0 || from * scale > width)
                        continue;
                    AddClip(track, from * scale, (to - from) * scale, text, SubtitleClipBrush, new SubtitleRef(from));
                }

                break;

            // An empty track: nothing on it yet (its row says to drop something there).
            case Layer { IsEmptyTrack: true }:
                break;

            // A track: its clips, each from when it appears to when it goes. The main video's are clips like any other
            // (4.3C): moved, trimmed from either edge, rolled, slipped, rippled, put on another track, on any track.
            case Layer layer:
                var clipsOnTrack = _viewModel.GetTrackClips(layer);
                foreach (var clip in clipsOnTrack)
                {
                    var (from, to) = clip.GetSpan(seconds);
                    var blockWidth = (to - from) * scale;
                    var main = clip.IsMainVideo;
                    var showsSound = clip.IsAudio || (clip is { IsVideoFile: true, HasAudio: true } && _viewModel.ShowLinkedAudio && _viewModel.AudioLinked);
                    var gainLine = showsSound && !clip.IsHidden && !clip.IsLoading && AppSettings.Current.ShowClipGainLines;

                    // The main video's frames and sound are its file's (its strip, the timeline's waveform): a block shows the part its clip plays.
                    var filePixels = clip.MediaDuration > 0 ? clip.MediaDuration * scale : width;
                    var name = clip.IsLoading ? "Loading..." : main ? (clip.IsHidden ? $"{clip.Name} (picture hidden)" : clipsOnTrack.Count > 1 ? "" : clip.Name)
                        : clip.IsHidden ? "hidden" : clip.IsAudio ? clip.Name : "";
                    AddClip(track, from * scale, blockWidth, name,
                        clip.IsLoading || clip.IsHidden ? OffClipBrush : main ? MainClipBrush : clip.IsAudio ? SoundClipBrush : LayerClipBrush, clip, canDrag: () => true, resizable: true,
                        frames: clip.IsHidden ? null
                            : main ? BuildFrames(clip.Filmstrip, _viewModel.SourceWidth, _viewModel.SourceHeight, clip.MediaOffset, clip.MediaDuration, 1, blockWidth, height, scale)
                            : !clip.IsVideoFile ? null
                            : BuildFrames(clip.Filmstrip, clip.ImageWidth, clip.ImageHeight, clip.MediaOffset, clip.MediaDuration, ClipSpeed(clip), blockWidth, height, scale),
                        peaks: clip.IsHidden ? null
                            : main ? _viewModel.ShowLinkedAudio ? WaveOf(_viewModel.TimelineWaveform, clip.MediaOffset * scale, filePixels) : null
                            : !showsSound ? null : MediaPeaks(clip.Waveform, clip, scale),
                        keys: ClipKeys(clip, 0, scale),

                        // Let go a little over the clip it meets: a crossfade instead (see CommitClipMove).
                        commit: (left, nowWidth) => _viewModel.CommitClipMove(clip, left * perPixel, nowWidth * perPixel),

                        // Its left edge, the cut it shares with the next clip (a roll), Alt inside it (a slip), Ctrl on an edge (a ripple).
                        trimmableStart: clip.Duration > 0.001 && !clip.IsLoading, slippable: clip.Duration > 0.001 && (clip.IsVideoFile || clip.IsAudio || main),
                        edit: done => EditClip(clip, done, perPixel),
                        gain: gainLine ? (clip.GainDb, clip.FadeInSeconds, clip.FadeOutSeconds) : null,
                        gainEdit: done => _viewModel.SetClipGain(clip, done.GainDb),

                        // Its fades, at its top corners: picture and sound, over whatever is under it. And a crossfade from
                        // the clip before it on its track, as a pill across their cut.
                        fades: clip.IsLoading || clip.IsHidden ? null : (clip.FadeInSeconds, clip.FadeOutSeconds, clip.FadeInStyle, clip.FadeOutStyle),
                        fadeEdit: (fadeIn, fadeSeconds) => _viewModel.SetClipFade(clip, fadeIn, fadeSeconds),
                        crossfadeIn: clip.TransitionIn > 0.01 && _viewModel.ClipBefore(clip) is not null ? clip.TransitionIn : 0,

                        // Dragged up or down onto another row: onto that track (an empty one, or one of its kind with room).
                        canMoveTo: clip.IsAudio ? null : (row, left) => row is Layer target && _viewModel.CanMoveClipToTrack(clip, target, left * perPixel),
                        moveTo: clip.IsAudio ? null : (row, left) =>
                        {
                            if (row is Layer target)
                                _viewModel.MoveClipToTrack(clip, target, left * perPixel);
                        });
                }

                break;
        }

        // The playhead, so it can be seen where a split would fall.
        track.PlayheadX = _viewModel.PositionMs / 1000 * scale - track.ViewOffsetX;
    }

    /// <summary>
    /// The part of a clip's sound that its block shows: its file's waveform, laid out at the scale of the
    /// timeline and shifted to where the clip begins in it. The bar draws it from the peaks, at the block's own size.
    /// </summary>
    private static WaveSpan? MediaPeaks(ImageSource? picture, Layer clip, double scale)
    {
        // A clip that runs faster covers its file in less of the timeline, and its sound is that much narrower.
        var speed = ClipSpeed(clip);
        return clip.MediaDuration > 0.05
            ? WaveOf(picture, clip.MediaOffset * scale / speed, clip.MediaDuration * scale / speed)
            : WaveOf(picture, 0, 0);
    }

    /// <summary>One stretch of a sound, for a block: the sound is <paramref name="span"/> pixels wide in all, and the block begins <paramref name="offset"/> pixels into it.</summary>
    private static WaveSpan? WaveOf(ImageSource? picture, double offset, double span) =>
        Waveforms.DataOf(picture) is { } data ? new WaveSpan(data, offset, span) : null;

    /// <summary>How many times as fast as recorded a clip runs; 1 for a clip that has no speed of its own.</summary>
    private static double ClipSpeed(Layer clip) => clip.HasSpeed ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    /// <summary>A brush that will not change again, frozen: WPF then neither watches it nor ties it to this thread.</summary>
    private static ImageBrush Frozen(ImageBrush brush)
    {
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }

    // The single frames of a filmstrip, cut from it once and kept for as long as the strip is.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, CroppedBitmap?[]> StripFrames = [];

    /// <summary>
    /// The frames a video's block is drawn with: as many as fit side by side at their own shape, each the
    /// frame of the file that is on screen at that point of the block. The file's strip holds a fixed
    /// number of frames, spread over its length; the nearest one is used, and a video that repeats starts again.
    /// </summary>
    /// <param name="filmstrip">The strip of frames made of the file, or null while there is none (yet).</param>
    /// <param name="pixelWidth">The video's own size, which gives a frame of the strip its shape.</param>
    /// <param name="mediaOffset">How far into the file the block begins, in seconds.</param>
    /// <param name="speed">How many times as fast as recorded the block runs through the file.</param>
    private static List<(ImageSource Picture, double Left, double Width)>? BuildFrames(ImageSource? filmstrip, int pixelWidth, int pixelHeight, double mediaOffset, double mediaDuration, double speed, double blockWidth, double height, double scale)
    {
        if (filmstrip is not BitmapSource strip || strip.PixelHeight <= 0 || blockWidth < 6)
            return null;

        var shape = pixelWidth > 0 && pixelHeight > 0 ? (double)pixelWidth / pixelHeight : 16.0 / 9;
        var count = Math.Max((int)Math.Round(strip.PixelWidth / (strip.PixelHeight * shape)), 1);
        var framePixels = strip.PixelWidth / count;
        if (framePixels < 2)
            return null;

        var frames = StripFrames.GetValue(strip, _ => new CroppedBitmap?[count]);
        if (frames.Length != count)
            return null;

        var slot = height * framePixels / strip.PixelHeight;
        var pictures = new List<(ImageSource Picture, double Left, double Width)>();
        for (var k = 0; k * slot < blockWidth && k < 80; k++)
        {
            // What the layer shows in the middle of this slot.
            var into = mediaOffset + (k + 0.5) * slot / scale * speed;
            var index = mediaDuration > 0.05 ? Math.Clamp((int)(into % mediaDuration / mediaDuration * count), 0, count - 1) : 0;
            var frame = frames[index] ??= Cut(strip, index * framePixels, framePixels);
            pictures.Add((frame, k * slot, slot));
        }

        return pictures;

        static CroppedBitmap Cut(BitmapSource strip, int x, int width)
        {
            var frame = new CroppedBitmap(strip, new Int32Rect(x, 0, width, strip.PixelHeight));
            frame.Freeze();
            return frame;
        }
    }

    /// <summary>Moves the playhead line of every bar; nothing else about them changes as the video plays, and nothing else is drawn again.</summary>
    private void MoveClipPlayheads()
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (seconds <= 0)
            return;

        var span = ViewSpan;
        foreach (var track in _clipTracks)
        {
            if (!double.IsNaN(track.PlayheadX))
                track.PlayheadX = _viewModel.PositionMs / 1000 * track.ActualWidth / span - track.ViewOffsetX;
        }

        foreach (var ruler in _rulers)
            PlaceRulerPlayhead(ruler);
        FollowPlayhead();
    }

    private bool IsSelected(object? clip) => clip is null ? _mainClipSelected && _selectedClip is null : Equals(clip, _selectedClip);

    private void SelectClip(object? clip)
    {
        (_selectedClip, _mainClipSelected) = (clip, clip is null);
        _viewModel.KeyLayer = clip switch { Layer { IsPicture: true } layer => layer, _ => _viewModel.KeyLayer };

        // The outline is drawn by each bar from what is selected: they are asked to draw themselves again.
        foreach (var track in _clipTracks)
            track.InvalidateVisual();
    }

    /// <summary>Puts a block on a bar. The bar draws it, and moves or stretches it when it is dragged.</summary>
    /// <param name="canDrag">Whether the block can be moved right now; null for one that never can (a cut segment).</param>
    /// <param name="commit">What letting go of a moved block does, given its new left edge and width in pixels.</param>
    /// <param name="frames">Frames of the block's video, drawn inside it.</param>
    /// <param name="peaks">The block's sound, drawn inside it over the frames.</param>
    /// <param name="edit">What a trim of its start, a roll, a slip or a ripple does (see TimelineBlock.Edit).</param>
    /// <param name="gain">Its gain line and fades, for a clip whose sound is drawn on it.</param>
    /// <param name="fades">Its fades and their styles, for the handles at its top corners (see TimelineBlock.Fades).</param>
    /// <param name="crossfadeIn">A crossfade from the clip before it on its track, in seconds, drawn across their cut.</param>
    private static void AddClip(
        TimelineTrack track, double left, double width, string name, Brush fill, object? clip,
        Func<bool>? canDrag = null, bool resizable = false, Action<double, double>? commit = null,
        IReadOnlyList<(ImageSource Picture, double Left, double Width)>? frames = null, WaveSpan? peaks = null, string cannotDrag = "",
        IEnumerable<double>? keys = null, double top = 0, Func<object, double, bool>? canMoveTo = null, Action<object, double>? moveTo = null,
        bool trimmableStart = false, bool slippable = false, Action<BlockEditDone>? edit = null,
        (double GainDb, double FadeIn, double FadeOut)? gain = null, Action<GainEditDone>? gainEdit = null,
        (double In, double Out, TransitionStyle InStyle, TransitionStyle OutStyle)? fades = null, Action<bool, double>? fadeEdit = null, double crossfadeIn = 0) =>
        track.Blocks.Add(new TimelineBlock
        {
            TrimmableStart = trimmableStart,
            Slippable = slippable,
            Edit = edit,
            Gain = gain,
            GainEdit = gainEdit,
            Fades = fades,
            FadeEdit = fadeEdit,
            CrossfadeIn = crossfadeIn,
            Left = left,
            Top = 1 + top,
            Width = Math.Max(width, 3),
            Height = Math.Max(track.ActualHeight - 2 - top, 4),
            Fill = fill,
            Name = name,
            Clip = clip,
            Frames = frames,
            Peaks = peaks,
            Keys = keys?.ToList(),
            CanDrag = canDrag,
            Resizable = resizable,
            Commit = commit,
            CannotDrag = cannotDrag,
            MayStartBeforeZero = clip is AudioClip or LayerSound,
            CanMoveTo = canMoveTo,
            MoveTo = moveTo,
        });

    /// <summary>
    /// A clip's block let go after a trim of its start, a roll, a slip or a ripple (see TimelineTrack): the view model
    /// makes the edit, or says why it cannot, and the bars are drawn again either way.
    /// </summary>
    private void EditClip(Layer clip, BlockEditDone done, double perPixel)
    {
        switch (done.Kind)
        {
            case BlockEdit.TrimStart:
                _viewModel.TrimClipStart(clip, done.Left * perPixel, done.Ripple);
                break;
            case BlockEdit.TrimEnd when done.Ripple:
                _viewModel.RippleClipEnd(clip, done.Width * perPixel);
                break;
            case BlockEdit.Roll when done.Neighbor?.Clip is Layer next:
                _viewModel.RollCut(clip, next, (done.Left + done.Width) * perPixel);
                break;
            case BlockEdit.Slip:
                _viewModel.SlipClip(clip, done.Slip * perPixel);
                break;
        }

        ScheduleClipRedraw();
    }

    // ----- The editing tools: each works on whatever is selected -----

    private void SplitClip_Click(object sender, RoutedEventArgs e) => SplitSelectedClip();

    /// <summary>The razor: cuts the selected block in two at the playhead. Nothing selected means the main video's clip there.</summary>
    private void SplitSelectedClip()
    {
        var at = _viewModel.PositionMs / 1000;
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

        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    private void TrimStart_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: true);

    private void TrimEnd_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: false);

    private void TrimSelectedClip(bool start)
    {
        // A clip's sound is trimmed as its clip is; nothing selected, the main video's clip at the playhead.
        var clip = _selectedClip is LayerSound sound ? sound.Layer : _selectedClip ?? _viewModel.MainLayer;
        _viewModel.TrimClip(clip, start, _viewModel.PositionMs / 1000);
        ScheduleClipRedraw();
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    /// <summary>Delete: a clip goes (the main video's too); a part of an audio track is silenced (and, deleted again, plays again).</summary>
    private void DeleteSelectedClip()
    {
        switch (_selectedClip)
        {
            case AudioClip { Piece: { } piece } audio:
                _viewModel.ToggleAudioPiece(audio.Track, piece);
                break;
            case AudioClip audio:
                _viewModel.StatusText = $"{audio.Track.Title} is still whole. Split it at the playhead (S) first, then delete the part to silence; or set the track to Ignore / Drop to leave it out altogether.";
                return;
            case LayerSound sound:
                _viewModel.StatusText = $"The sound belongs to {sound.Layer.Name}: delete or hide the layer to take it out.";
                return;
            default:
                if (!_viewModel.DeleteClip(_selectedClip))
                    return;
                break;
        }

        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Undo();
        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Redo();
        (_selectedClip, _mainClipSelected) = (null, false);
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

    /// <summary>
    /// Fills a track's menu as it opens, with what can be done to it as it is now: to the track as a whole
    /// (its filters, its place, its mask, its look), and to the clip that was clicked (split, trim, delete).
    /// </summary>
    private void LayerRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Layer layer, ContextMenu: { } menu } row)
            return;

        // A right-click on a fade handle: that fade's menu, and nothing else.
        if (e.OriginalSource is TimelineTrack { MenuFade: { } pressedFade } && FadeMenuItems(pressedFade.Block.Clip, pressedFade.In) is { Count: > 0 } fadeItems)
        {
            menu.Items.Clear();
            foreach (var item in fadeItems)
                menu.Items.Add(item);
            return;
        }

        // The clip the menu is about: the one selected on this track's bar, or the track's first.
        var clips = _viewModel.GetTrackClips(layer);
        var clip = _selectedClip switch
        {
            Layer selected when clips.Contains(selected) => selected,
            LayerSound sound when clips.Contains(sound.Layer) => sound.Layer,
            _ => layer,
        };

        menu.Items.Clear();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                action();
                ScheduleClipRedraw();
            };
            menu.Items.Add(item);
        }

        void Separate()
        {
            if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
                menu.Items.Add(new Separator());
        }

        var at = _viewModel.PositionMs / 1000;

        // An empty track has nothing to set: it can only be taken away.
        if (layer.IsEmptyTrack)
        {
            Add("Remove Empty Track", () => _viewModel.RemoveTrack(layer));
            return;
        }

        // The caption layer: how the subtitles look, and the editor for what they say and when.
        if (layer.IsCaptions)
        {
            Add("Caption Style Settings", () => CaptionStyle_Click(row, new RoutedEventArgs()));
            Add("Manual Edit", () => _ = OpenSubtitleEditorAsync());
            Separate();
        }

        // The track's own modifiers.
        if (layer.HasFilters)
            Add("Filters...", () => OpenLayerTool(layer, LayerTool.Filters));
        if (layer.HasFilters)
            Add("Position & Transform...", () => OpenLayerTool(layer, LayerTool.Transform));
        if (layer.HasMask)
            Add(layer is { IsVideo: true, CustomMask: true } ? "Redraw / Modify Mask..." : "Custom Mask...", () => OpenLayerTool(layer, LayerTool.Mask));
        if (layer.HasStyle)
            Add("Properties...", () => ShowLayerProperties(layer));
        if (layer.IsBackground)
            Add("Blur Settings...", () => BlurSettings_Click(row, new RoutedEventArgs()));
        if (layer.IsVideo)
            Add("Draw Target", () => _viewModel.DrawTargetCommand.Execute(layer));
        if (layer is { UsesFrameMask: true, CustomMask: true } && File.Exists(layer.MaskPath.Trim().Trim('"')))
            Add("Add More Pieces of This Mask...", () => AddLayersFromMask(layer.MaskPath.Trim().Trim('"')));

        if (layer.HasTiming)
        {
            Separate();
            Add("Split at Playhead", () => _viewModel.SplitLayer(clip, at));
            Add("Trim Start to Playhead", () => _viewModel.TrimClip(clip, start: true, at));
            Add("Trim End to Playhead", () => _viewModel.TrimClip(clip, start: false, at));
            Add("Split / Trim by Exact Timecode...", () => SplitTrimByTimecode(clip));
            Add("Ripple Delete (Shift+Delete)", () => _viewModel.RippleDelete(clip));

            // Silences Split & Mark folded out at its cuts (4.3D): each put back where it was, what follows moving on.
            foreach (var fold in _viewModel.FoldsAt(clip))
                Add(string.Create(CultureInfo.InvariantCulture, $"Restore Folded Silence ({fold.Length:0.##} s) at Its {(fold.AtEnd ? "End" : "Start")}"), () => _viewModel.UnfoldSegment(fold));
            if (_viewModel.FoldCount is > 1 and var folds)
                Add(string.Create(CultureInfo.InvariantCulture, $"Restore All {folds} Folded Silences"), _viewModel.UnfoldAll);

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
                Add("Back to the Start of the Timeline, Whole", () => _viewModel.ResetMainTiming(),
                    clips.Count > 1 || clip.StartTime > 0.001 || clip.MediaOffset > 0.001);
                Add("Reset Position and Zoom", () => _viewModel.ResetCenterVideoCommand.Execute(null));
            }
            else
            {
                Add(clip.IsAudio ? "Play from Its Start to Its End" : "Show for the Whole Video", () => _viewModel.ShowThroughout(clip),
                    clip.StartTime > 0.001 || clip.StartTime + clip.Duration < _viewModel.SequenceSeconds - 0.001);
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
                    Header = string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x{(speed == 1 ? "  (as recorded)" : "")}"),
                    IsCheckable = true, IsChecked = Math.Abs(clip.Speed - speed) < 0.0005,
                };
                item.Click += (_, _) =>
                {
                    _viewModel.SetClipSpeed(clip, speed);
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
                item.Click += (_, _) => _viewModel.SetProxyChoice(clip, choice);
                proxy.Items.Add(item);
            }

            menu.Items.Add(proxy);
        }

        // What its sound does when someone is speaking.
        if (layer.CarriesSound)
        {
            Separate();
            AddDuckingItems(menu, layer, layer.AutoDuck, layer.IsVoice);
        }

        if (layer.CanReorder && !layer.IsAudio)
        {
            Separate();
            Add("Move Up (towards the front)", () => _viewModel.MoveLayerUpCommand.Execute(layer));
            Add("Move Down (towards the back)", () => _viewModel.MoveLayerDownCommand.Execute(layer));
        }

        Separate();
        if (layer.IsRemovable || layer.IsMainVideo || layer.IsBackground)
            Add(layer.IsHidden ? "Show" : layer.IsAudio ? "Mute" : "Hide", () => _viewModel.ToggleHidden(layer));
        if (layer.IsRemovable || layer.IsMainVideo)
        {
            Add("Duplicate", () => _viewModel.DuplicateLayer(layer));
            if (layer.CarriesSound)
            {
                Add("Use Its Sound for Auto-Captions", () => _viewModel.UseLayerAudioForCaptions(layer));
                Add("Remove Dead Air...", () => RunDeadAir(clip));
            }

            Separate();
            if (clips.Count > 1)
                Add("Delete This Clip", () => _viewModel.DeleteClip(clip));
            Add(clips.Count > 1 ? "Delete the Whole Track" : "Delete", () => _viewModel.RemoveTrack(layer));
        }

        if (menu.Items.Count > 0 && menu.Items[^1] is Separator)
            menu.Items.RemoveAt(menu.Items.Count - 1);
        e.Handled = menu.Items.Count == 0;
    }

    /// <summary>The two ducking boxes of a sound's right-click menu: turned down under a voice, or the voice itself.</summary>
    private void AddDuckingItems(ContextMenu menu, object sound, bool ducks, bool isVoice)
    {
        foreach (var (header, isOn, voice, tip) in new[]
                 {
                     ("Auto-Duck against Voice", ducks, false, "Turns this sound down, by the Duck by (dB) amount on the Audio tab, whenever a sound marked as a voice is speaking."),
                     ("Voiceover / Dialogue", isVoice, true, "This sound is a voice: the sounds set to Auto-Duck make room for it."),
                 })
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isOn, ToolTip = tip };
            item.Click += (_, _) => _viewModel.ToggleDucking(sound, voice);
            menu.Items.Add(item);
        }
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
                action();
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
    /// The right-click menu of a row of the Audio tab. The rows are one template; what the menu offers is
    /// what the sound is: a clip from a file has a clip's menu (split, trim, speed, delete), and one of the
    /// video's own tracks has its ducking.
    /// </summary>
    private void AudioRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer layer, ContextMenu: { } clipMenu })
        {
            LayerRow_ContextMenuOpening(sender, e);
            if (layer.IsAudio)
                AddRenameItem(clipMenu, layer.Title, name => layer.Title = name);
            e.Handled = clipMenu.Items.Count == 0;
            return;
        }

        if (sender is not FrameworkElement { DataContext: AudioTrack track, ContextMenu: { } menu })
            return;

        menu.Items.Clear();
        AddDuckingItems(menu, track, track.AutoDuck, track.IsVoice);
        AddRenameItem(menu, track.Title, name => track.Title = name);
    }

    /// <summary>
    /// Puts a box for the sound's name at the top of its menu. The heading of a row is the file the sound comes
    /// from; the name is the title a track of the video is saved under, or what a clip is called.
    /// </summary>
    private static void AddRenameItem(ContextMenu menu, string name, Action<string> rename)
    {
        var box = new TextBox { Text = name, MinWidth = 190, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text))
                rename(box.Text.Trim());
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
                menu.IsOpen = false;
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(box);
        menu.Items.Insert(0, new MenuItem { Header = line, StaysOpenOnClick = true, ToolTip = "The title a track of the video is saved under in the output file, or what a clip is called." });
        if (menu.Items.Count > 1)
            menu.Items.Insert(1, new Separator());
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

        var dialog = new LayerToolDialog(layer, _viewModel, tool) { Owner = this };
        dialog.ShowDialog();
        if (dialog.RedrawRequested)
            _viewModel.DrawTargetCommand.Execute(layer);
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer layer })
            _viewModel.RemoveTrack(layer);
    }
}
