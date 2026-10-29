using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Creator tools: playback helpers, the Frame & Layer Engine, pro filters, target size and dead-air removal.
public partial class MainViewModel
{
    // Kept stretches shorter than this are not worth a cut of their own.
    private const double ShortestKeptSeconds = 0.25;

    // More I-frame lines than this would be closer together than a pixel on any screen.
    private const int MaxIFrameMarks = 1500;

    // ----- Frame size -----
    // The frame is whatever Resolution & Cropping (Video tab) describes, in any shape: the Frame & Layer Engine composes
    // onto it, and the Arrange canvas draws it.

    // Used for the shape of things until a source says otherwise.
    private const int DefaultSourceWidth = 1920;
    private const int DefaultSourceHeight = 1080;

    // A size still being typed ("1", "10", "108"...) is not a frame yet.
    private const int SmallestFrameSide = 16;

    /// <summary>Turns the shape of the output on its side: a 16:9 source gives a 9:16 frame.</summary>
    [ObservableProperty] private bool _useVerticalResolution;

    partial void OnUseVerticalResolutionChanged(bool value)
    {
        // While a preset or project is being applied the sizes arrive as they were saved, already the right way round.
        if (!_syncingDimensions)
        {
            var (width, height) = (OutputWidth, OutputHeight);

            // Blank boxes mean "the source's size"; spelled out, so that there is something to turn over.
            if (value && string.IsNullOrWhiteSpace(width) && string.IsNullOrWhiteSpace(height) && GetCroppedSourceSize() is { } source)
                (width, height) = (Text(Even(source.Width)), Text(Even(source.Height)));

            SetOutputSize(height, width);

            // One click from wide to tall: the engine is what fits the picture into the new shape instead of stretching it.
            if (value && !FrameEngine)
            {
                FrameEngine = true;
                StatusText = "Vertical Video: the picture sits in the middle of a tall frame, over a blurred copy of itself. Zoom and place it on the Layers tab or on the Canvas.";
            }
        }

        OnPropertyChanged(nameof(FrameWidth));
        OnPropertyChanged(nameof(FrameHeight));

        static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Sets both boxes as given, without one recalculating the other on the way.</summary>
    private void SetOutputSize(string width, string height)
    {
        _syncingDimensions = true;
        try
        {
            (OutputWidth, OutputHeight) = (width, height);
        }
        finally
        {
            _syncingDimensions = false;
        }
    }

    // ----- Crop -----

    // Something of the picture always has to remain.
    private const double LargestCropTotal = 0.95;

    /// <summary>The crop as fractions (0 to 1) of the source's width and height, held to what leaves a picture.</summary>
    private (double Left, double Top, double Right, double Bottom) GetCropFractions()
    {
        var left = Math.Min(Math.Clamp(CropLeft, 0, 100) / 100, LargestCropTotal);
        var top = Math.Min(Math.Clamp(CropTop, 0, 100) / 100, LargestCropTotal);
        var right = Math.Min(Math.Clamp(CropRight, 0, 100) / 100, LargestCropTotal - left);
        var bottom = Math.Min(Math.Clamp(CropBottom, 0, 100) / 100, LargestCropTotal - top);
        return (left, top, right, bottom);
    }

    private bool HasCrop => CropLeft > 0 || CropTop > 0 || CropRight > 0 || CropBottom > 0;

    /// <summary>
    /// What the crop keeps of the loaded source, in its pixels. Rounded the way the crop filter rounds,
    /// so what is drawn over the player is what FFmpeg will cut.
    /// </summary>
    public (int Left, int Top, int Width, int Height) GetCropRect()
    {
        var (left, top, right, bottom) = GetCropFractions();
        return ((int)(SourceWidth * left), (int)(SourceHeight * top),
            Math.Max((int)(SourceWidth * (1 - left - right) / 2) * 2, 2), Math.Max((int)(SourceHeight * (1 - top - bottom) / 2) * 2, 2));
    }

    /// <summary>Sets the crop from distances in source pixels, as drawn on the video.</summary>
    public void SetCropFromPixels(double left, double top, double right, double bottom)
    {
        if (SourceWidth <= 0 || SourceHeight <= 0)
            return;

        CropLeft = ToPercent(left, SourceWidth);
        CropRight = ToPercent(right, SourceWidth);
        CropTop = ToPercent(top, SourceHeight);
        CropBottom = ToPercent(bottom, SourceHeight);
    }

    private static double ToPercent(double pixels, double of) => Math.Round(Math.Clamp(pixels / of, 0, LargestCropTotal) * 100, 2);

    /// <summary>
    /// Takes the crop from a preset. Presets saved before the crop became relative hold pixels; those are
    /// converted using the loaded source's size, or 1920 x 1080 when there is none.
    /// </summary>
    private void ApplyPresetCrop(EncodingPreset preset)
    {
        if (preset.CropPercent is { Length: 4 } percent)
        {
            (CropLeft, CropTop, CropRight, CropBottom) = (percent[0], percent[1], percent[2], percent[3]);
            return;
        }

        double width = SourceWidth > 0 ? SourceWidth : DefaultSourceWidth, height = SourceHeight > 0 ? SourceHeight : DefaultSourceHeight;
        CropLeft = ToPercent(preset.CropLeft, width);
        CropRight = ToPercent(preset.CropRight, width);
        CropTop = ToPercent(preset.CropTop, height);
        CropBottom = ToPercent(preset.CropBottom, height);
    }

    /// <summary>The source frame after cropping, or null while no source is known.</summary>
    private (int Width, int Height)? GetCroppedSourceSize()
    {
        if (SourceWidth <= 0 || SourceHeight <= 0)
            return null;

        var (_, _, width, height) = GetCropRect();
        return (width, height);
    }

    /// <summary>The shape sizes are held to: the cropped source, on its side with Use Vertical Resolution.</summary>
    private (int Width, int Height) GetNaturalSize()
    {
        var (width, height) = GetCroppedSourceSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        return UseVerticalResolution ? (height, width) : (width, height);
    }

    /// <summary>
    /// The size Resolution & Cropping asks for, or null when it leaves the size to the source. One box alone
    /// is completed from the natural shape.
    /// </summary>
    private (int Width, int Height)? GetRequestedSize()
    {
        var hasWidth = int.TryParse(OutputWidth, out var width) && width >= SmallestFrameSide;
        var hasHeight = int.TryParse(OutputHeight, out var height) && height >= SmallestFrameSide;
        var natural = GetNaturalSize();

        if (hasWidth && hasHeight)
            return (Even(width), Even(height));
        if (hasWidth)
            return (Even(width), Even((double)width * natural.Height / natural.Width));
        if (hasHeight)
            return (Even((double)height * natural.Width / natural.Height), Even(height));
        return UseVerticalResolution ? (Even(natural.Width), Even(natural.Height)) : null;
    }

    /// <summary>Width of the output frame the engine composes: as asked for under Resolution & Cropping, otherwise the cropped source's.</summary>
    public int FrameWidth => GetRequestedSize()?.Width ?? Even(GetNaturalSize().Width);

    public int FrameHeight => GetRequestedSize()?.Height ?? Even(GetNaturalSize().Height);

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);

    // ----- Frame & Layer Engine -----

    private const double SmallestCenterZoom = 0.1;
    private const double LargestCenterZoom = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetPixelAspect))]
    private bool _frameEngine;

    /// <summary>Scale of the sharp video: 1 fills the width of the frame.</summary>
    [ObservableProperty] private double _centerZoom = 1;

    /// <summary>Moves the sharp video left (negative) or right, as a percentage of the frame width.</summary>
    [ObservableProperty] private double _centerOffsetX;

    /// <summary>Moves the sharp video up (negative) or down, as a percentage of the frame height.</summary>
    [ObservableProperty] private double _centerOffsetY;

    // The blurred background: how far the blur reaches, how often it is applied, and how much it is darkened.
    [ObservableProperty] private int _blurRadius = 20;
    [ObservableProperty] private int _blurPasses = 2;
    [ObservableProperty] private double _backgroundDim = -0.15;

    [RelayCommand]
    private void ResetBlurSettings() => (BlurRadius, BlurPasses, BackgroundDim) = (20, 2, -0.15);

    [RelayCommand]
    private void ResetCenterVideo() => (CenterZoom, CenterOffsetX, CenterOffsetY) = (1, 0, 0);

    /// <summary>Pieces of the source (a minimap, an ammo counter) and images (a logo) placed on the frame.</summary>
    public ObservableCollection<Layer> Layers { get; } = [];

    /// <summary>False with the engine: its layers are composed with square pixels.</summary>
    public bool CanSetPixelAspect => !FrameEngine;

    partial void OnFrameEngineChanged(bool value)
    {
        // Nothing to mark without the frame; the layout pane stays only while it has captions to show.
        OnPropertyChanged(nameof(CanEditLayout));
        if (!value)
            DrawTargetLayer = null;
        if (!CanEditLayout)
            IsArrangeActive = false;
        else if (value)
            OpenLayoutPaneIfWanted();
    }

    /// <summary>
    /// Where the sharp video sits on the output frame, in output pixels. The filter graph and the Arrange
    /// canvas both work from this, so what is dragged is what is encoded.
    /// </summary>
    public (int X, int Y, int Width, int Height) GetCenterRect()
    {
        var (frameWidth, frameHeight) = (FrameWidth, FrameHeight);
        var (sourceWidth, sourceHeight) = GetCroppedSourceSize() ?? (DefaultSourceWidth, DefaultSourceHeight);

        var width = Even(frameWidth * Math.Clamp(CenterZoom, SmallestCenterZoom, LargestCenterZoom));
        var height = Even((double)width * sourceHeight / sourceWidth);
        var x = (frameWidth - width) / 2 + (int)Math.Round(Math.Clamp(CenterOffsetX, -100, 100) / 100 * frameWidth);
        var y = (frameHeight - height) / 2 + (int)Math.Round(Math.Clamp(CenterOffsetY, -100, 100) / 100 * frameHeight);
        return (x, y, width, height);
    }

    /// <summary>Moves the sharp video by a distance given in output pixels.</summary>
    public void MoveCenter(double deltaX, double deltaY)
    {
        CenterOffsetX = Math.Clamp(CenterOffsetX + deltaX / FrameWidth * 100, -100, 100);
        CenterOffsetY = Math.Clamp(CenterOffsetY + deltaY / FrameHeight * 100, -100, 100);
    }

    /// <summary>
    /// Makes the sharp video wider or narrower by a number of output pixels, its shape kept. Its top-left
    /// corner stays where it is, as when any other layer is resized by its corner.
    /// </summary>
    public void ResizeCenter(double deltaWidth)
    {
        var (_, _, oldWidth, oldHeight) = GetCenterRect();
        CenterZoom = Math.Clamp(CenterZoom + deltaWidth / FrameWidth, SmallestCenterZoom, LargestCenterZoom);
        var (_, _, width, height) = GetCenterRect();

        // The video is placed by its middle, so growing it would push the corner out by half the growth.
        MoveCenter((width - oldWidth) / 2.0, (height - oldHeight) / 2.0);
    }

    /// <summary>Adds a piece of the source video. Where it is cut from is marked afterwards with Draw Target.</summary>
    [RelayCommand]
    private void AddLayer()
    {
        var element = new Layer { Kind = LayerKind.Video, Name = $"Layer {Layers.Count(l => l.IsVideo) + 1}", PositionY = NextLayerY() };

        // The first piece of video in a layout ties the layout to the shape of this video.
        if (!Layers.Any(e => e.IsVideo) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        if (PlaceLayer(element, 0, ToTheEnd(0), "add a layer"))
            Log($"Added layer {element.Name}: use Draw Target to mark it on the video");
    }

    /// <summary>
    /// Where a clip that 4.2.0 would have left "to the end" ends now that every clip has an end of its own: the end of the
    /// timeline as it is, and a few seconds on when it is placed there or the timeline is empty.
    /// </summary>
    private double ToTheEnd(double start) => Math.Max(SequenceSeconds, start + PictureSeconds);

    /// <summary>Puts a new layer onto the timeline as one step of Undo (a track of its own on top of the pictures, or by the row given).</summary>
    private bool PlaceLayer(Layer element, double start, double end, string label, Layer? onRow = null, bool joins = false) =>
        Edit(label, edit => PlaceNew(edit, element, start, end, onRow, joins));

    /// <summary>Adds a picture from a file as a layer. Returns false when the file is not a readable image.</summary>
    /// <param name="startSeconds">
    /// When on the timeline the layer appears: from there to the end of the timeline, or (placed at its end) for a few
    /// seconds of its own, which the timeline grows to hold.
    /// </param>
    /// <param name="onTrack">
    /// The row it was dropped on, which decides its track (<see cref="PlaceNew"/>). The main video's also puts it in
    /// the main video's place.
    /// </param>
    public bool AddImageLayer(string path, double startSeconds = 0, Layer? onTrack = null)
    {
        if (ImageInfo.GetSize(path) is not { } size)
        {
            StatusText = $"Not an image that can be read: {path}";
            return false;
        }

        var element = new Layer
        {
            Kind = LayerKind.Image,
            Name = Path.GetFileName(path),
            ImagePath = path,
            ImageWidth = size.Width,
            ImageHeight = size.Height,
            SizeWidth = 0.25,
            PositionX = 0.05,
            PositionY = NextLayerY(),
        };
        if (onTrack is { IsMainVideo: true } && FrameWidth > 0 && FrameHeight > 0)
            FitInto(element, GetCenterRect());

        var start = Math.Max(startSeconds, 0);
        if (!PlaceLayer(element, start, ToTheEnd(start), "add an image", onTrack,
                joins: onTrack is { IsImage: true } && AppSettings.Current.DroppedClipsJoinTrack))
            return false;

        Log($"Added image layer {element.Name}");
        return true;
    }

    /// <summary>
    /// Adds a track with nothing on it (the + Add Layer button): a row of the Layers tab to drop clips onto. It draws
    /// nothing and is kept in the project and in undo; the first clip dropped on it takes its place.
    /// </summary>
    [RelayCommand]
    private void AddEmptyTrack()
    {
        if (Edit("add a track", edit => edit.InsertTrack(TopPictureIndex(), TimelineTrackKind.Video)))
            Log("Added an empty track: drop a video or a picture on it");
    }

    /// <summary>
    /// Whether a clip on the Layers tab can be dragged onto another track's row, to begin at a moment there: onto a track
    /// that takes its kind of clip (an empty one too), where that track has nothing for the clip's whole length. Any clip,
    /// the main video's included; never onto the caption box or the background.
    /// </summary>
    public bool CanMoveClipToTrack(Layer clip, Layer track, double start)
    {
        if (ClipOf(clip) is not { } model || TrackOfRow(track) is not { IsLocked: false } target || !target.Accepts(model) || ReferenceEquals(target, Document.TrackOf(model)))
            return false;

        start = Math.Max(start, 0);
        var end = start + model.Duration;
        return target.ClipsIntersecting(start + 1e-6, end - 1e-6).IsEmpty;
    }

    /// <summary>
    /// Moves a clip to another track (a drag from its row onto another), beginning at <paramref name="start"/>. Onto an
    /// empty track it keeps its own look; onto a track of clips it takes that track's place, size and look (a track's
    /// clips share them) and keeps its own timing and keyframes. Its old track goes when it was the only clip on it.
    /// One step of Undo.
    /// </summary>
    public bool MoveClipToTrack(Layer clip, Layer track, double start)
    {
        start = Math.Max(start, 0);
        if (!CanMoveClipToTrack(clip, track, start) || ClipOf(clip) is not { } model || TrackOfRow(track) is not { } target)
        {
            StatusText = TrackOfRow(track) is { } other && ClipOf(clip) is { } moving && other.Accepts(moving)
                ? $"{clip.Name} would run over a clip on that track: drop it where the track is free, or onto an empty track (+ Add Layer)."
                : $"{clip.Name} can only go onto a track that takes its kind of clip, or onto an empty track (+ Add Layer).";
            return false;
        }

        var name = target.Clips.Count == 0 ? "the empty track" : track.Name;
        var lead = target.Clips.Count > 0 ? ViewOf(target.Clips[0]) : null;
        if (!Edit($"move {clip.Name} to another track", edit => edit.MoveClip(model, target, start, allowCrossfadeOverlap: false)))
            return false;

        // Its look is the track's now: the size first (a layer that grows about its middle moves as it is sized), then the
        // place. Its own switches and timing stay the clip's (a look also holds them, and the track's lead's are not its).
        if (lead is not null && Document.TrackOf(model) is { } now)
        {
            _syncingViews = true;
            try
            {
                clip.ApplyLook(lead.ToState());
                clip.LockAspectRatio = lead.LockAspectRatio;
                (clip.SizeWidth, clip.SizeHeight) = (lead.SizeWidth, lead.SizeHeight);
                (clip.PositionX, clip.PositionY) = (lead.PositionX, lead.PositionY);
                TakeClip(clip, model, now);
            }
            finally
            {
                _syncingViews = false;
            }

            _staleLooks.Add(clip);
            GenerateCommand();
        }

        StatusText = $"{clip.Name} moved to {name}, at {TimeSpan.FromSeconds(model.Start):h\\:mm\\:ss\\.ff}. Ctrl+Z puts it back.";
        return true;
    }

    /// <summary>How long a picture placed at the end of the timeline (or on an empty one) is shown for, in seconds.</summary>
    private const double PictureSeconds = 5;

    /// <summary>Adds words as a layer, in the middle of the frame, and selects it so that its Text Settings are shown.</summary>
    [RelayCommand]
    private void AddTextLayer()
    {
        var element = new Layer
        {
            Kind = LayerKind.Text,
            Name = $"Text {Layers.Count(l => l.IsText) + 1}",
            TextContent = "Your Text",
            SizeWidth = 0.25,
        };

        // Its box is its words': as wide on the frame as they are at the font's size, and in the middle of it.
        LayoutText(element, keepScale: false);
        var (_, _, width, height) = element.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        if (FrameWidth > 0 && FrameHeight > 0)
            (element.PositionX, element.PositionY) = ((FrameWidth - width) / 2.0 / FrameWidth, (FrameHeight - height) / 2.0 / FrameHeight);

        if (!PlaceLayer(element, 0, ToTheEnd(0), "add a text layer"))
            return;

        KeyLayer = element;
        Log($"Added text layer {element.Name}: type its words under Text Settings");
    }

    /// <summary>
    /// Measures a text layer's words again and makes its box theirs. The box keeps its middle where it was, and
    /// the layer keeps how much it had been scaled: words set twice as large as their font's size stay twice as
    /// large, whatever they now say. Its Scale keyframes are widths, so they are scaled by as much as the box.
    /// </summary>
    /// <param name="keepScale">False for a layer that is new: its box is exactly its words at the font's size.</param>
    private void LayoutText(Layer layer, bool keepScale = true)
    {
        if (!layer.IsText)
            return;

        var (frameWidth, frameHeight) = (Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2));
        var layout = TextLayout.Measure(layer);
        var before = layer.GetOutputRect(frameWidth, frameHeight, SourceWidth, SourceHeight);
        var (oldWidth, oldHeight) = (layer.ImageWidth, layer.ImageHeight);
        var scale = keepScale && oldWidth > 0 ? layer.SizeWidth * frameWidth / oldWidth : 1;
        if ((oldWidth, oldHeight) == (layout.Width, layout.Height) && keepScale)
            return;

        var copying = _copyingToTrack;
        _copyingToTrack = true;
        try
        {
            (layer.ImageWidth, layer.ImageHeight) = (layout.Width, layout.Height);
            layer.FromCorner(() => layer.SizeWidth = Math.Clamp(layout.Width * scale / frameWidth, 0.01, 4));
            if (keepScale)
            {
                var after = layer.GetOutputRect(frameWidth, frameHeight, SourceWidth, SourceHeight);
                layer.PositionX += (before.Width - after.Width) / 2.0 / frameWidth;
                layer.PositionY += (before.Height - after.Height) / 2.0 / frameHeight;

                if (oldWidth > 0 && layer.HasKeys(KeyProperty.Scale))
                {
                    var grown = (double)layout.Width / oldWidth;
                    layer.SetKeys(KeyProperty.Scale, layer.GetKeys(KeyProperty.Scale).Select(k => k with { Value = k.Value * grown }).ToList());
                }
            }
        }
        finally
        {
            _copyingToTrack = copying;
        }
    }

    /// <summary>The fonts installed on this computer, for a text layer's font list.</summary>
    public IReadOnlyList<string> SystemFonts => TextLayout.SystemFonts;

    /// <summary>Adds a video from a file as a layer: it plays alongside the main video.</summary>
    /// <param name="startSeconds">
    /// When on the timeline the layer appears. Placed at a moment, it plays once through from there, for as long
    /// as the file lasts (the timeline grows to hold it); left at the start, it runs to the end of the timeline,
    /// starting again from its first frame each time it runs out.
    /// </param>
    /// <param name="onTrack">
    /// The row of the Layers tab the file was dropped on, which decides its track (<see cref="PlaceNew"/>): an empty
    /// track is filled; a video track it joins takes it in its place and with its look; otherwise it gets a track of
    /// its own. The main video's row also sizes and places it as the main video is.
    /// </param>
    public async Task<bool> AddVideoLayerAsync(string path, double startSeconds = 0, Layer? onTrack = null) =>
        await AddVideoClipAsync(path, startSeconds, onTrack) is not null;

    /// <summary>As <see cref="AddVideoLayerAsync"/>, and returns the clip once its file has been looked at; null when it could not be added.</summary>
    /// <param name="playOnce">Plays once through even when placed at the very start (videos dropped together, one after another).</param>
    /// <param name="follows">Goes on <paramref name="onTrack"/>'s track whatever Settings say: it follows the clip before it in one drop.</param>
    public async Task<Layer?> AddVideoClipAsync(string path, double startSeconds, Layer? onTrack, bool playOnce = false, bool follows = false)
    {
        // The block goes onto the timeline now, marked as loading: nothing is waited for before the drop is answered. What
        // the file is (its size, its length, whether it has sound) is found out afterwards; until then it is a few seconds
        // long (less, to fit before the next clip of a track it joins) and is not rendered.
        var start = Math.Max(startSeconds, 0);
        Layer element;
        var row = TrackOfRow(onTrack);
        var joins = onTrack is { IsVideoFile: true, IsEmptyTrack: false } && row is not null && (follows || AppSettings.Current.DroppedClipsJoinTrack);
        if (joins)
        {
            // The track's look (place, size, filters, mask) with nothing of the clip it was copied from.
            var state = onTrack!.ToState();
            (state.Name, state.ImagePath, state.ImageWidth, state.ImageHeight, state.MediaDuration) = (Path.GetFileName(path), path, 0, 0, 0);
            (state.HasAudio, state.AudioCodec, state.AudioChannels) = (false, "", "");
            (state.StartTime, state.Duration, state.MediaOffset, state.Speed, state.AudioOffset) = (start, 0, 0, 1, 0);
            (state.IsHidden, state.IsDeleted, state.FadeInSeconds, state.FadeOutSeconds, state.TransitionIn) = (false, false, 0, 0, 0);
            (state.KeysX, state.KeysY, state.KeysScale, state.KeysRotation, state.KeysOpacity) = (null, null, null, null, null);
            element = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
        }
        else
        {
            element = new Layer
            {
                Kind = LayerKind.VideoFile,
                Name = Path.GetFileName(path),
                ImagePath = path,
                SizeWidth = 0.4,
                PositionX = 0.05,
                PositionY = NextLayerY(),
            };
        }

        element.IsLoading = true;
        var room = row?.Clips.FirstOrDefault(c => c.Start > start + 0.001)?.Start ?? double.PositiveInfinity;
        var end = joins && room - start >= TimelineDocument.MinimumClipSeconds * 2 ? Math.Min(start + PictureSeconds, room) : start + PictureSeconds;
        if (!PlaceLayer(element, start, end, "add a video layer", onTrack, joins))
            return null;
        StatusText = $"Loading {element.Name}...";

        // Dropped on the main video's row: it takes the main video's place, fitted inside it once its shape is known.
        (int X, int Y, int Width, int Height)? box = onTrack is { IsMainVideo: true } && FrameWidth > 0 && FrameHeight > 0 ? GetCenterRect() : null;
        return await CompleteVideoLayerAsync(element, playOnce || start > 0 || onTrack is not null, box) ? element : null;
    }

    /// <summary>Sizes and places a layer to fit inside a box of the frame (in the frame's pixels), in its middle, at its own shape.</summary>
    private void FitInto(Layer element, (int X, int Y, int Width, int Height) box)
    {
        var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
        if (element.ImageWidth <= 0 || element.ImageHeight <= 0 || box.Width <= 0 || box.Height <= 0)
            return;

        var width = (double)box.Width;
        var height = width * element.ImageHeight / element.ImageWidth;
        if (height > box.Height)
            (width, height) = (box.Height * (double)element.ImageWidth / element.ImageHeight, box.Height);

        element.FromCorner(() =>
        {
            element.SizeWidth = width / frameWidth;
            element.PositionX = (box.X + (box.Width - width) / 2) / frameWidth;
            element.PositionY = (box.Y + (box.Height - height) / 2) / frameHeight;
        });
    }

    /// <summary>Notes what a clip's sound is in its file (its first audio stream's codec and channels), for its row's heading.</summary>
    private static void DescribeSound(Layer element, MediaInfo info)
    {
        if (info.Audio.FirstOrDefault() is not { } stream)
            return;

        (element.AudioCodec, element.AudioChannels) = (stream.Codec, stream.ChannelLayout.Length > 0 ? stream.ChannelLayout : $"{stream.Channels} ch");
    }

    /// <summary>Raised when a layer that was loading has been filled in: its box in the layout pane has its real shape now.</summary>
    public event Action? LayerLoaded;

    /// <summary>
    /// Looks at a video layer's file, away from the interface, and fills the layer in with what it finds: then its block
    /// has its real length and its frames are fetched. A file that turns out not to be a video is taken off the timeline
    /// again (an empty track it filled stays). None of this is an undo step of its own: the drop was.
    /// </summary>
    /// <param name="playOnce">Whether the layer was placed at a moment: it then plays once through from there; otherwise to the end of the timeline, round and round.</param>
    /// <param name="fitInto">A box of the frame, in its pixels, that the layer is fitted inside once its shape is known; null to leave it where it is.</param>
    /// <param name="keepLength">A clip brought back unfinished (an undo step, a project): its length stays, held to its file.</param>
    private async Task<bool> CompleteVideoLayerAsync(Layer element, bool playOnce, (int X, int Y, int Width, int Height)? fitInto = null, bool keepLength = false)
    {
        var path = element.ImagePath;
        var wanted = ToTheEnd(element.StartTime);
        var clipId = element.ClipId;
        MediaInfo? info = null;
        try
        {
            var token = _shutdown.Token;
            info = await Task.Run(() => MediaProbe.ProbeAsync(path, token), token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not readable: dealt with below, as a file that is not a video.
        }

        // Undone, or removed, while it was being looked at: there is nothing left to fill in. Undone and done again, the clip
        // is back with a view of its own, which is the one filled in.
        if (Document.FindClip(clipId) is not { } clip || Document.TrackOf(clip) is not { } track)
            return false;
        element = ViewOf(clip) ?? element;

        if (info?.Video is not { Width: > 0, Height: > 0 } video)
        {
            var alone = track.Clips.Count == 1;
            Edit("take an unreadable file off the timeline", edit => edit.DeleteClip(clip), undoable: false, removeEmptied: !alone || !_filledEmptyTracks.Remove(track.Id));
            StatusText = $"Not a video that can be read: {path}";
            return false;
        }

        _filledEmptyTracks.Remove(track.Id);
        using (DeferCommand())
        {
            (element.ImageWidth, element.ImageHeight) = (video.Width, video.Height);
            (element.HasAudio, element.MediaDuration) = (info.Audio.Count > 0, Math.Max(info.DurationSeconds, 0));
            DescribeSound(element, info);
            if (fitInto is { } box)
                FitInto(element, box);

            // What the file is, which is not an edit: its length and its sound.
            (clip.SourceDuration, clip.AudioStreamIndex) = (Math.Max(info.DurationSeconds, 0), info.Audio.Count > 0 ? 0 : -1);
            // Placed at a moment, it plays once through (a file's length, however short); at the start, to the end, round and round.
            var file = Math.Max(info.DurationSeconds, 0);
            var end = keepLength ? clip.End : playOnce || file <= 0.05 ? clip.Start + (file > 0.05 ? file : PictureSeconds) : Math.Max(wanted, clip.Start + file);
            Edit("fill in a video layer", edit => LayOut(edit, clip, end), undoable: false);
            element.IsLoading = false;
            _liveState = null;
            GenerateCommand();
        }

        // Its sound, when it has any, is a row of the Audio tab from now on: at the layer's place, for its length.
        RefreshLayerRows();
        LayerLoaded?.Invoke();
        _ = LoadLayerPicturesAsync(element);
        Log(element.HasAudio
            ? $"Added video layer {element.Name}, and its sound on the Audio tab at the same place"
            : $"Added video layer {element.Name}");
        return true;
    }

    // Empty tracks a loading clip was dropped onto: a file that turns out not to be a video gives the track back, empty.
    private readonly HashSet<Guid> _filledEmptyTracks = [];

    /// <summary>
    /// A video clip dropped with a stand-in length, given its own: to <paramref name="end"/>, or to its file's end, and
    /// past that (a clip running to the end of the timeline) as rounds of its file from its first frame, silent after the
    /// first (4.2.0's loop). One that would run over a clip of its track goes onto a track of its own just above, first:
    /// nothing is laid over a clip already on the timeline.
    /// </summary>
    private void LayOut(ITimelineTransaction edit, TimelineClip clip, double end)
    {
        var track = Document.TrackOf(clip)!;
        var first = clip.SourceDuration > 0.05 ? Math.Min(end, clip.Start + (clip.SourceDuration - clip.MediaOffset) / clip.Speed) : end;
        if (!track.ClipsIntersecting(clip.Start + 1e-6, end - 1e-6).ToArray().All(c => ReferenceEquals(c, clip)))
        {
            edit.MoveClip(clip, track, clip.Start, insertTrackAtIndex: Document.Tracks.IndexOf(track) + 1, allowCrossfadeOverlap: false);
            track = Document.TrackOf(clip)!;
        }

        edit.TrimClipEnd(clip, first);
        for (var at = clip.End; clip.SourceDuration > 0.05 && end - at >= TimelineDocument.MinimumClipSeconds;)
        {
            var round = CopyOfClip(clip);
            (round.Id, round.MediaOffset, round.IsMuted, round.FadeInSeconds, round.TransitionIn) = (Guid.NewGuid(), 0, true, 0, 0);
            (round.Start, round.End) = (at, Math.Min(end, at + clip.SourceDuration / clip.Speed));
            edit.PlaceClip(track, round, allowCrossfadeOverlap: false);
            at = round.End;
        }
    }

    /// <summary>
    /// Adds the sound of a file (a song, a recording, or the sound of a video) to the timeline as a clip of its
    /// own: it has no picture, starts where it is put, and is mixed into the first audio track of the output.
    /// </summary>
    /// <param name="isVoice">Whether the sound is a voice (a voiceover): the sounds set to Auto-Duck make room for it.</param>
    /// <param name="name">What the clip is called, when that is not its file's name.</param>
    public async Task<bool> AddAudioLayerAsync(string path, double startSeconds = 0, bool isVoice = false, string? name = null)
    {
        var info = await MediaProbe.ProbeAsync(path, _shutdown.Token);
        if (info is not { Audio.Count: > 0 })
        {
            StatusText = $"No sound that can be read in: {path}";
            return false;
        }

        var element = new Layer
        {
            Kind = LayerKind.Audio,
            Name = Path.GetFileName(path),
            ImagePath = path,
            HasAudio = true,
            MediaDuration = Math.Max(info.DurationSeconds, 0),
        };
        DescribeSound(element, info);

        // Its block is as long as the sound is, unless that runs past the end of the timeline. Dropped after the end,
        // in the room there, it plays all of itself, and the timeline grows to hold it.
        var start = Math.Max(startSeconds, 0);
        var sequence = SequenceSeconds;
        var afterEnd = start > 0.01 && start >= sequence - 0.05;
        var end = element.MediaDuration > 0.05 && (afterEnd || start + element.MediaDuration < sequence || sequence <= 0.05)
            ? start + element.MediaDuration
            : Math.Max(sequence, start + TimelineDocument.MinimumClipSeconds);
        (element.IsVoice, element.Name) = (isVoice, name ?? element.Name);
        if (!PlaceLayer(element, start, end, isVoice ? "add a voiceover" : "add an audio clip"))
            return false;

        _ = LoadLayerPicturesAsync(element);
        if (isVoice)
            return true;

        Log($"Added audio clip {element.Name} at {TimeDisplay.Format(element.StartTime)}");
        return true;
    }

    // ----- Stacking order -----

    /// <summary>
    /// Whether the timeline's checkboxes (snapping, I-frames) and the cut buttons sit under the timeline. They are up in
    /// the transport row instead, while there is one.
    /// </summary>
    public bool ShowTimelineOptionsBelow => !ShowAdvancedPlayback;

    /// <summary>Raised when something about the layers has changed that their time bars may have to be drawn again for.</summary>
    public event Action? TimelineChanged;

    // The blurred background is the sequence's, not a clip's: its row stands for it.
    private readonly Layer _backgroundRow = new() { Kind = LayerKind.Background, Name = "Background Blur" };

    /// <summary>The pictures, bottom first: every clip's view on the picture tracks, the main video's among them. Sounds have no place in it.</summary>
    private List<Layer> GetStack() => Layers.Where(l => !l.IsAudio).ToList();

    /// <summary>The stack as tracks, bottom first: the clips of one track lie together, and move together.</summary>
    private List<List<Layer>> GetStackTracks()
    {
        var tracks = new List<List<Layer>>();
        foreach (var layer in GetStack())
        {
            if (tracks.Count > 0 && tracks[^1][0].TrackId == layer.TrackId)
                tracks[^1].Add(layer);
            else
                tracks.Add([layer]);
        }

        return tracks;
    }

    [RelayCommand]
    private void MoveLayerUp(Layer? layer) => MoveLayer(layer, 1);

    [RelayCommand]
    private void MoveLayerDown(Layer? layer) => MoveLayer(layer, -1);

    /// <summary>Moves a layer's track one place towards the front (+1) or the back (-1), past the next picture track that way.</summary>
    private void MoveLayer(Layer? layer, int by)
    {
        if (TrackOfRow(layer) is not { } track || track.Kind is TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)
            return;

        var pictures = Document.Tracks.Where(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).ToList();
        var from = pictures.IndexOf(track);
        var to = from + by;
        if (to < 0 || to >= pictures.Count)
            return;

        if (!Edit($"move {layer!.Name} {(by > 0 ? "up" : "down")}", edit => edit.MoveTrack(track, Document.Tracks.IndexOf(pictures[to]))))
            return;

        StatusText = $"{layer.Name} moved {(by > 0 ? "up" : "down")}: it is now {(to == pictures.Count - 1 ? "the front layer" : to == 0 ? "the back layer, just above the background" : $"layer {to + 1} of {pictures.Count} from the back")}.";
    }

    // ----- Splitting: the razor -----

    /// <summary>
    /// Cuts a clip in two at a time: the first part ends there and a second clip, the same in everything
    /// else, begins there, on the same track. Each can then be moved in time, trimmed or deleted on its own.
    /// Any clip: the main video's too.
    /// </summary>
    public bool SplitLayer(Layer layer, double seconds)
    {
        if (ClipOf(layer) is not { } clip || seconds <= clip.Start + TimelineDocument.MinimumClipSeconds || seconds >= clip.End - TimelineDocument.MinimumClipSeconds)
        {
            StatusText = $"The playhead is not inside {layer.Name}: move it to where the clip should be split.";
            return false;
        }

        if (!Edit($"split {layer.Name}", edit => edit.SplitClipAt(clip, seconds)))
            return false;

        Log($"Split clip: {layer.Name} at {TimeDisplay.Format(seconds)}");
        return true;
    }

    /// <summary>
    /// Splits every clip under a moment on every unlocked track (the razor across the whole timeline: what the in and
    /// out points of 4.2.0's cut list are now, split points). Returns how many clips were split.
    /// </summary>
    public int SplitAllAt(double seconds, string label)
    {
        var split = 0;
        Edit(label, edit =>
        {
            foreach (var track in Document.Tracks.Where(t => !t.IsLocked && t.Kind != TimelineTrackKind.Subtitle).ToList())
            {
                foreach (var clip in track.ClipsIntersecting(seconds, seconds + 1e-6).ToArray())
                {
                    if (seconds > clip.Start + TimelineDocument.MinimumClipSeconds && seconds < clip.End - TimelineDocument.MinimumClipSeconds)
                    {
                        try
                        {
                            edit.SplitClipAt(clip, seconds);
                            split++;
                        }
                        catch (TimelineEditException)
                        {
                            // Its sound too short on one side (a J or L cut): left whole.
                        }
                    }
                }
            }
        });
        return split;
    }

    // ----- HUD layers -----

    /// <summary>
    /// Takes a game's HUD apart: one layer per piece, each cut out of the video where that piece sits and laid
    /// back in the same place, all sharing the game's mask of the whole frame. From there each can be moved.
    /// </summary>
    /// <param name="vertical">Lays the pieces out for a tall frame, above and below the video, and makes the frame tall; otherwise each piece is laid back where it was cut from.</param>
    public void AddHudLayers(HudGame game, bool vertical = false)
    {
        FlushLooks();
        var before = CaptureSnapshot();
        if (vertical && !UseVerticalResolution)
            UseVerticalResolution = true;

        var states = HudLibrary.BuildLayers(game, vertical).ToList();
        PlacePieces(states, $"add the {game.Name} HUD layers");

        // A vertical style made by hand places its pieces round the video at its own zoom.
        if (vertical && HudLibrary.LoadVerticalStyle(game)?.Layout is { CenterZoom: var zoom } && Math.Abs(zoom - 1) > 0.001)
            CenterZoom = zoom;

        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        FrameEngine = true;
        Checkpoint($"add the {game.Name} HUD layers", before);
        StatusText = vertical
            ? $"Added {game.MainPieces.Count()} layers for {game.Name}, laid out above and below the video. Check each against your video: right-click a layer and choose Redraw / Modify Mask to correct it."
            : $"Added {game.MainPieces.Count()} layers for {game.Name}. They sit where the HUD is by default: check each against your video, and correct its area with Draw Target or its outline with Draw Custom Shape (Mask).";
    }

    /// <summary>
    /// Pieces cut from the video (a HUD's, a mask's), each a clip on a track of its own on top of the pictures for the
    /// length of the timeline, with the main video then put in front of them all: it is what is being watched. No undo
    /// step of its own: the caller's.
    /// </summary>
    private void PlacePieces(IReadOnlyList<LayerState> states, string label)
    {
        var end = ToTheEnd(0);
        Edit(label, edit =>
        {
            foreach (var state in states)
            {
                state.TrackId = 0;
                PlaceNew(edit, Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight), 0, end, null, joins: false);
            }

            if (MainVideoClips().Select(m => m.Track).FirstOrDefault() is { } main)
                edit.MoveTrack(main, TopPictureIndex() - 1);
        }, undoable: false);
    }

    /// <summary>
    /// Layers from a Mask: one layer per piece of a mask of the whole frame, each cut out of the video where
    /// the piece is and sharing the mask, laid back where it was or out for a tall frame. One step of Undo.
    /// </summary>
    /// <param name="replace">Takes the layers already cut from the video away first (into the recycle bin).</param>
    public void AddMaskLayers(string maskPath, IReadOnlyList<System.Windows.Rect> pieces, bool vertical, bool replace)
    {
        if (pieces.Count == 0)
            return;

        FlushLooks();
        var before = CaptureSnapshot();
        using var whole = DeferCommand();
        if (replace)
        {
            var old = Layers.Where(l => l.IsVideo && ClipOf(l) is not null).ToList();
            var binned = old.Select(v => (View: v, Deleted: Binned(ClipOf(v)!, Document.TrackOf(ClipOf(v)!)!, v))).ToList();
            if (Edit("take away the layers cut from the video", edit =>
                {
                    foreach (var (view, _) in binned)
                        edit.DeleteClip(ClipOf(view)!);
                }, undoable: false))
            {
                foreach (var (view, deleted) in binned)
                    AddToBin(deleted.Clip, view.Name, deleted.Preview);
            }
        }

        if (vertical && !UseVerticalResolution)
            UseVerticalResolution = true;

        // Named after where they are, and numbered where two would have the same name.
        var names = pieces.Select(MaskStore.PlaceName).ToList();
        var states = pieces.Select((piece, i) => new LayerState
        {
            Name = names.Count(n => n == names[i]) > 1 ? $"{names[i]} {names.Take(i + 1).Count(n => n == names[i])}" : names[i],
            Kind = LayerKind.Video,
            SourceX = piece.X, SourceY = piece.Y, SourceWidth = piece.Width, SourceHeight = piece.Height,
            PositionX = piece.X, PositionY = piece.Y, SizeWidth = piece.Width,
            CustomMask = true, MaskPath = maskPath, MaskFrame = true, Feather = true, FeatherRadius = 4,
        }).ToList();
        if (vertical)
            HudLibrary.LayOutVertically(states);

        PlacePieces(states, "add layers from a mask");
        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        FrameEngine = true;
        Checkpoint("add layers from a mask", before);
        StatusText = $"Added {states.Count} layer{(states.Count == 1 ? "" : "s")} from the mask, one for each HUD element it shows. Ctrl+Z takes them away again.";
    }

    // ----- Stream copy -----

    /// <summary>
    /// Copy passes the picture through untouched. When there are layers or filters set that it will therefore
    /// leave out, this says so; otherwise it is empty.
    /// </summary>
    public string CopyBypassWarning
    {
        get
        {
            if (VideoEncoder.Family != EncoderFamily.Copy || IsAnimatedOutput)
                return "";

            var skipped = Layers.Any(e => e.IsUsable && !e.IsMainVideo) || AutoCaptions || HasCrop || Deinterlace || Denoise || FadeIn || FadeOut
                          || !string.IsNullOrWhiteSpace(LutPath) || BuildColorFilters().Count > 0
                          || !string.IsNullOrWhiteSpace(OutputWidth) || !string.IsNullOrWhiteSpace(OutputHeight)
                          || Math.Abs(CenterZoom - 1) > 0.001 || CenterOffsetX != 0 || CenterOffsetY != 0;

            // Copy cuts without encoding: the main video's clips are joined end to end from the I-frames at or before their starts;
            // a clip of it that the player does not play from its file as it is (slowed, another look, on a higher track) is not among them.
            var chain = GetMainClips();
            var cut = chain.Count == 0 ? " The main video is not on the timeline (or is hidden): Copy writes its whole file."
                : IsWholePicture(chain) ? ""
                : " The main video's clips are joined end to end, each from the I-frame at or before its start (gaps and moves are closed up).";
            if (chain.Count > 0 && MainVideoClips().Count() > chain.Count)
                cut += " Some clips of the main video (slowed, with a look of their own, or on another track) are left out.";
            return skipped ? "Stream Copy selected: Layers and Filters will be bypassed." + cut : cut.TrimStart();
        }
    }

    // Stacked down the frame, so a new layer does not land exactly on top of the last.
    private double NextLayerY() => Math.Min(0.03 + Layers.Count * 0.2, 0.8);

    /// <summary>Takes a clip off the timeline (no recycle bin: the layer tools' Remove), its time left empty. One step of Undo.</summary>
    [RelayCommand]
    private void RemoveLayer(Layer? element)
    {
        if (ClipOf(element) is { } clip)
            Edit($"remove {element!.Name}", edit => edit.DeleteClip(clip));
    }

    /// <summary>
    /// Gives every other layer cut from the video the mask of the whole frame that this one has (its picture,
    /// its drawn outlines, whether it is turned inside out, how far it is grown). Each then takes its own part of
    /// it and stays a layer of its own; their crops and outlines are their own and are left as they are.
    /// </summary>
    /// <returns>How many layers were given it.</returns>
    public int ShareFrameMask(Layer from)
    {
        var count = 0;
        foreach (var layer in Layers.Where(l => l.IsVideo && !l.IsDeleted && !ReferenceEquals(l, from)))
        {
            (layer.CustomMask, layer.MaskPath, layer.MaskFrame, layer.MaskShapes) = (true, from.MaskPath, true, from.MaskShapes);
            (layer.MaskInvert, layer.MaskGrow) = (from.MaskInvert, from.MaskGrow);
            count++;
        }

        return count;
    }

    /// <summary>The clips on the same track as a row's clip, in time; the row alone when it stands for no track's clips (a stand-in, an empty track).</summary>
    public List<Layer> GetTrackClips(Layer layer) =>
        TrackOfRow(layer) is { Clips.Count: > 0 } track ? track.Clips.Select(ViewOf).OfType<Layer>().ToList() : [layer];

    public Layer BackgroundLayer => _backgroundRow;

    /// <summary>
    /// A preset's or a style's layers put on the timeline as its template (app data, not a project): every track but the
    /// main video's goes, and the template's layers come in their place, a track for each run of one 4.2.0 track number
    /// in its list, under or over the main video as <paramref name="mainVideoIndex"/> (how many of its pictures lie under
    /// the main video) says; a layer with no length runs to the end of the timeline. No undo step of its own.
    /// </summary>
    private void PlaceTemplate(IReadOnlyList<LayerState> states, int mainVideoIndex)
    {
        Edit("put a layout on the timeline", edit =>
        {
            foreach (var track in Document.Tracks.Where(t => t.Kind != TimelineTrackKind.Subtitle && !t.IsLocked && !t.Clips.Any(c => c.ContentKind == LayerKind.MainVideo)).ToList())
                edit.RemoveTrack(track);

            // The template's tracks: runs of one number among the pictures, then the sounds by number.
            var pictures = new List<List<LayerState>>();
            foreach (var state in states.Where(s => s.Kind != LayerKind.Audio && !s.IsEmptyTrack))
            {
                if (pictures.Count > 0 && pictures[^1][0].TrackId == state.TrackId && state.TrackId > 0)
                    pictures[^1].Add(state);
                else
                    pictures.Add([state]);
            }

            var sounds = states.Where(s => s.Kind == LayerKind.Audio).GroupBy(s => s.TrackId).Select(g => g.ToList()).ToList();
            var main = MainVideoClips().Select(m => m.Track).FirstOrDefault();
            var under = 0;
            var counted = 0;
            foreach (var run in pictures)
            {
                if (counted + run.Count <= mainVideoIndex)
                    under++;
                counted += run.Count;
            }

            // With no main video yet (a style chosen before a video is opened), an empty track keeps its place among the
            // template's (4.3D): the next video opened goes onto it (PlaceMainVideo), so what the style lays under it stays
            // under it, the cut-outs and the blurred background made from it.
            var end = ToTheEnd(0);
            for (var i = 0; i < pictures.Count; i++)
            {
                if (main is null && i == under)
                    edit.InsertTrack(TopPictureIndex(), TimelineTrackKind.Video);
                var index = main is null ? TopPictureIndex() : i < under ? Document.Tracks.IndexOf(main) : TopPictureIndex();
                PlaceRun(edit, edit.InsertTrack(index, pictures[i].All(s => s.Kind == LayerKind.Adjustment) ? TimelineTrackKind.Adjustment : TimelineTrackKind.Video), pictures[i], end);
            }

            if (main is null && under >= pictures.Count && pictures.Count > 0)
                edit.InsertTrack(TopPictureIndex(), TimelineTrackKind.Video);

            foreach (var run in sounds)
                PlaceRun(edit, edit.InsertTrack(Document.Tracks.Count, TimelineTrackKind.Audio), run, end);
        }, undoable: false);
    }

    /// <summary>The clips of one template track onto a new track, each where it says (with no length: to <paramref name="end"/>), those that do not fit left out.</summary>
    private void PlaceRun(ITimelineTransaction edit, TimelineTrackModel track, List<LayerState> run, double end)
    {
        foreach (var state in run.OrderBy(s => s.StartTime))
        {
            var view = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            var start = Math.Max(state.StartTime, 0);
            var clipEnd = state.Duration > 0.001 ? start + state.Duration : Math.Max(end, start + PictureSeconds);
            var clip = NewClipFor(view, start, clipEnd);
            Adopt(view, clip);
            try
            {
                edit.PlaceClip(track, clip, allowCrossfadeOverlap: false);
            }
            catch (TimelineEditException)
            {
                Unadopt(view);
            }
        }
    }

    /// <summary>
    /// Presets saved before images became layers may carry a watermark. It becomes an image layer in
    /// the same place and at the same size, and the engine is switched on to draw it.
    /// </summary>
    private void AddLegacyWatermark(EncodingPreset preset)
    {
        var path = preset.WatermarkPath?.Trim().Trim('"') ?? "";
        if (path.Length == 0)
            return;

        var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
        var size = ImageInfo.GetSize(path);

        // The watermark was drawn at its own pixel size, 24 pixels in from the edges.
        var width = size is { } known ? Math.Min(known.Width / frameWidth, 1) : 0.2;
        var height = size is { } shape ? width * frameWidth * shape.Height / shape.Width / frameHeight : width;
        var (marginX, marginY) = (24 / frameWidth, 24 / frameHeight);
        var (x, y) = preset.WatermarkPosition switch
        {
            "Top-Left" => (marginX, marginY),
            "Top-Right" => (1 - width - marginX, marginY),
            "Bottom-Left" => (marginX, 1 - height - marginY),
            "Center" => ((1 - width) / 2, (1 - height) / 2),
            _ => (1 - width - marginX, 1 - height - marginY),
        };

        var element = new Layer
        {
            Kind = LayerKind.Image,
            Name = Path.GetFileName(path),
            ImagePath = path,
            ImageWidth = size?.Width ?? 0,
            ImageHeight = size?.Height ?? 0,
            SizeWidth = width,
            PositionX = Math.Clamp(x, 0, 1),
            PositionY = Math.Clamp(y, 0, 1),
            Opacity = Math.Clamp(preset.WatermarkOpacity ?? 70, 0, 100),
        };
        Edit("add the preset's watermark", edit => PlaceNew(edit, element, 0, ToTheEnd(0), null, joins: false), undoable: false);
        FrameEngine = true;
    }

    private void OnLayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Layer source)
            return;

        // A clip's timing and switches are the document's: one changed here (a box bound to it) is made an edit of the
        // clip, and anything else is its look, newer now than the clip's (MainViewModel.Document.cs).
        if (NoteViewChange(source, e.PropertyName) || _syncingViews)
            return;

        // The main video's place is the Center controls': a view of one of its clips moved or sized (a keyframe applied,
        // a box typed into) moves it.
        if (source.IsMainVideo && !_placingMain && Layer.GetKeyProperty(e.PropertyName) is { } key and not (KeyProperty.Rotation or KeyProperty.Opacity))
            TakeMainPlace(source, key);

        // Releasing the aspect lock: the layer keeps the shape it has until it is stretched.
        if (e.PropertyName == nameof(Layer.LockAspectRatio) && source is { LockAspectRatio: false })
            source.StartFreeHeight(FrameWidth, FrameHeight, SourceWidth, SourceHeight);

        // Marked again on the video that is loaded: the layout now fits this shape.
        if (e.PropertyName is nameof(Layer.SourceWidth) or nameof(Layer.SourceHeight) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;

        // A track is one thing with several clips on it: where it sits, how it looks and what filters it has
        // are the track's, so a change to one clip is made to the others on its track as well.
        if (_copyingToTrack || _applyingKeys)
            return;

        if (e.PropertyName == Layer.KeysChanged)
            RefreshAnyKeys();

        // Moved, sized or turned by hand: what that does to its keyframes. (Not the main video's place being written into its views.)
        if (!_placingMain)
            RecordKey(source, e.PropertyName);
        TimelineChanged?.Invoke();

        // A picture of the clip arriving is something to draw, and nothing to do with the command.
        if (e.PropertyName is nameof(Layer.Waveform) or nameof(Layer.Filmstrip))
            return;

        // A change made in the Effects pane is the clip's own (Layer.IsEchoing); the layer tools' are the track's.
        if (e.PropertyName is { } name && !Layer.IsEchoing && !Layer.IsClipProperty(name) && GetTrackProperty(name) is { } property)
        {
            var others = GetTrackClips(source).Where(l => !ReferenceEquals(l, source)).ToList();
            if (others.Count > 0)
            {
                _copyingToTrack = true;
                try
                {
                    var value = property.GetValue(source);
                    foreach (var other in others)
                        other.FromCorner(() => property.SetValue(other, value));
                }
                finally
                {
                    _copyingToTrack = false;
                }
            }
        }

        // Different words, or a different font: the box of every clip on the track is measured again.
        if (source.IsText && Layer.IsTextShapeProperty(e.PropertyName))
        {
            foreach (var clip in GetTrackClips(source))
                LayoutText(clip);
        }

        GenerateCommand();
    }

    private bool _copyingToTrack;
    private static readonly Dictionary<string, System.Reflection.PropertyInfo?> TrackProperties = [];

    /// <summary>A property of a layer that can be set from outside, by name; null for one that cannot.</summary>
    private static System.Reflection.PropertyInfo? GetTrackProperty(string name)
    {
        if (!TrackProperties.TryGetValue(name, out var property))
        {
            property = typeof(Layer).GetProperty(name);
            if (property is not { CanRead: true, SetMethod.IsPublic: true }
                || property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
            {
                property = null;
            }

            TrackProperties[name] = property;
        }

        return property;
    }

    // ----- Timeline I-frame lines -----

    /// <summary>Whether the I-frame lines are drawn on the timeline. Remembered between sessions.</summary>
    [ObservableProperty] private bool _showIFrames = AppSettings.Current.ShowIFrames;

    partial void OnShowIFramesChanged(bool value)
    {
        var settings = AppSettings.Current;
        settings.ShowIFrames = value;

        // Optionally the pull of the timeline thumb towards I-frames goes on and off with the lines.
        // Snapping of the cut points is a separate matter and is not touched.
        if (settings.AutoToggleTimelineSnap)
            settings.SnapTimelineToIFrames = value;

        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still holds for this session.
        }

        if (settings.AutoToggleTimelineSnap)
            StatusText = value ? "I-frames shown; the timeline snaps to them." : "I-frames hidden; timeline snapping is off.";
    }

    // ----- Taskbar progress -----

    /// <summary>
    /// Shows an encode's progress on the application's taskbar button. The state has to be set explicitly:
    /// it starts as None, in which the taskbar draws nothing whatever the value is.
    /// </summary>
    /// <param name="fraction">0 to 1.</param>
    private void SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState state, double fraction = 0)
    {
        if (_isBackgroundWorker || System.Windows.Application.Current?.MainWindow?.TaskbarItemInfo is not { } taskbar)
            return;

        taskbar.ProgressState = state;
        taskbar.ProgressValue = Math.Clamp(fraction, 0, 1);
    }
    // ----- Encoder presets -----
    // Each kind of encoder has its own speed/quality steps, and its own FFmpeg option for them.

    private static readonly string[] SoftwarePresets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private static readonly string[] QsvPresets = ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private static readonly string[] AmfPresets = ["speed", "balanced", "quality"];
    // NVENC's current names: p1 the fastest, p7 the best, p4 the middle.
    private static readonly string[] NvencPresets = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];

    /// <summary>The preset steps of the selected encoder; empty for encoders that have none here.</summary>
    public ObservableCollection<string> EncoderPresets { get; } = [.. SoftwarePresets];

    public bool HasEncoderPresets => EncoderPresets.Count > 0;

    /// <summary>Refills the preset list for the selected encoder, keeping the choice when the new list has it.</summary>
    /// <summary>The preset steps an encoder has, by its FFmpeg name; none for one that has none, or is not known here.</summary>
    public IReadOnlyList<string> EncoderPresetsFor(string encoderName)
    {
        if (VideoEncoders.FirstOrDefault(e => e.Name == encoderName) is not { } encoder)
            return [];

        return encoder.Family switch
        {
            EncoderFamily.Qsv => QsvPresets,
            EncoderFamily.Amf => AmfPresets,
            EncoderFamily.Nvenc => NvencPresets,
            EncoderFamily.Software when encoder.Name is "libx264" or "libx265" => SoftwarePresets,
            _ => [],
        };
    }

    private void UpdateEncoderPresets()
    {
        string[] presets = VideoEncoder.Family switch
        {
            EncoderFamily.Qsv => QsvPresets,
            EncoderFamily.Amf => AmfPresets,
            EncoderFamily.Nvenc => NvencPresets,
            EncoderFamily.Software when AreSoftwareEncoderOptionsEnabled => SoftwarePresets,
            _ => [],
        };

        if (!presets.SequenceEqual(EncoderPresets))
        {
            var previous = EncoderPreset;
            EncoderPresets.Clear();
            foreach (var preset in presets)
                EncoderPresets.Add(preset);

            // The middle step when the old choice does not exist for this encoder: "medium", or AMF's "balanced".
            EncoderPreset = presets.Contains(previous) ? previous
                : presets.Contains("medium") ? "medium"
                : presets.Length > 0 ? presets[presets.Length / 2]
                : previous;
        }

        OnPropertyChanged(nameof(HasEncoderPresets));
    }

    partial void OnVideoEncoderChanged(EncoderOption value)
    {
        UpdateEncoderPresets();
        UpdateRateControlChoices();
    }

    // ----- Drawing on the video -----
    // One overlay over the player serves three jobs, one at a time: setting the crop,
    // marking where a UI layer is in the source, and arranging the layers on the output frame.

    /// <summary>While on, a rectangle can be dragged over the video to set the crop.</summary>
    [ObservableProperty] private bool _isInteractiveCropActive;

    /// <summary>The UI layer whose source rectangle is being drawn on the video, if any.</summary>
    [ObservableProperty] private Layer? _drawTargetLayer;

    /// <summary>While on, the output frame is drawn over the player and its layers can be dragged into place.</summary>
    [ObservableProperty] private bool _isArrangeActive;

    // The layout has a pane of its own, so it can stay open while a crop or a target is drawn on the video.
    partial void OnIsInteractiveCropActiveChanged(bool value)
    {
        if (value)
            DrawTargetLayer = null;
    }

    partial void OnDrawTargetLayerChanged(Layer? value)
    {
        if (value is not null)
            IsInteractiveCropActive = false;
    }

    /// <summary>Starts (or, pressed again, stops) drawing a layer's source rectangle on the video.</summary>
    [RelayCommand]
    private void DrawTarget(Layer? element) =>
        DrawTargetLayer = ReferenceEquals(DrawTargetLayer, element) ? null : element;

    [RelayCommand]
    private void ResetCrop() => (CropTop, CropBottom, CropLeft, CropRight) = (0, 0, 0, 0);

    // ----- Render preview -----

    /// <summary>Raised with the path of a freshly rendered preview clip, for the window to play.</summary>
    public event Action<string>? PreviewRendered;

    private static string PreviewFolder => SessionPaths.Previews;

    /// <summary>
    /// Renders a few seconds with the current filters, at reduced size, and hands the clip to the window. It starts at
    /// the in point while one is set ([), otherwise at the playhead (from the start when the playhead is at the end).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task RenderPreviewAsync() => RunOperationAsync("Render Preview", WorkLane.Foreground, async cancellationToken =>
    {
        if (!HasSource)
            return "Load a video first.";

        var settings = AppSettings.Current;
        var seconds = Math.Clamp(settings.PreviewDurationSeconds, 1, 60);

        // One stretch of the timeline: from the in point while one is set (4.3D), otherwise from the playhead (from
        // earlier, when the timeline ends before a whole preview; from the start, when the playhead is at the end).
        var length = Math.Max(SequenceSeconds, 0);
        var start = PendingStartMs is { } inPoint
            ? Math.Clamp(inPoint / 1000, 0, length)
            : PositionMs / 1000 >= length - 0.05 ? 0 : Math.Clamp(Math.Min(PositionMs / 1000, length - seconds), 0, length);
        List<(double Start, double End)> ranges = [(start, start + seconds)];

        Directory.CreateDirectory(PreviewFolder);
        var path = Path.Combine(PreviewFolder, $"preview_{Guid.NewGuid():N}.mp4");

        // Captions for just the stretch being previewed. With burned-in subtitles the preview keeps the
        // source's clock (see BuildPreviewCommand), so the captions are moved to where that stretch is on it.
        string? captionsPath = null;
        if (AutoCaptions)
        {
            captionsPath = Path.Combine(PreviewFolder, "captions_preview.ass");
            var keepsSourceClock = SubtitleTracks.Any(t => t.Action == SubtitleTrack.HardSub);
            if (await PrepareCaptionsAsync(ranges, captionsPath, keepsSourceClock ? start : 0, cancellationToken) is { } problem)
                return problem;
        }

        var command = BuildPreviewCommand(path, ranges, settings.PreviewResolutionPercent, captionsPath);

        IsProgressIndeterminate = true;
        StatusText = "Rendering preview...";
        var expected = TimeSpan.FromSeconds(Math.Max(ranges.Sum(r => r.End - r.Start), 0.1));
        var progress = new Progress<FfmpegProgress>(report =>
        {
            if (_acceptProgressReports && report.Position is { } position)
            {
                IsProgressIndeterminate = false;
                ProgressValue = Math.Clamp(position / expected * 100, 0, 100);
            }
        });

        await FfmpegRunner.RunAsync(command, progress, cancellationToken, graphInFile: true);
        PreviewRendered?.Invoke(path);
        return ranges.Count > 1
            ? $"Preview rendered: {ranges.Sum(r => r.End - r.Start):0.#} s across {ranges.Count} cut segments, from {TimeDisplay.Format(start)}, at {settings.PreviewResolutionPercent}% size."
            : $"Preview rendered: {seconds} s from {TimeDisplay.Format(start)} at {settings.PreviewResolutionPercent}% size.";
    });

    // ----- Interface settings -----

    public bool ShowCommandPreviewTab => AppSettings.Current.ShowCommandPreviewTab;

    // ----- Hover previews -----

    /// <summary>The sheet of timeline thumbnails for the loaded video, or null when there is none (yet).</summary>
    [ObservableProperty] private string? _spriteSheetPath;

    /// <summary>Seconds between two thumbnails on the sheet.</summary>
    public double SpriteIntervalSeconds { get; private set; }

    private static string SpriteFolder => SessionPaths.Sprites;

    private CancellationTokenSource? _spriteCancellation;

    /// <summary>Builds the hover-preview sheet in the background. Does nothing when the setting is off.</summary>
    private async Task GenerateSpriteSheetAsync(string videoPath)
    {
        _spriteCancellation?.Cancel();
        SpriteSheetPath = null;

        var settings = AppSettings.Current;
        var duration = _mediaInfo?.DurationSeconds ?? 0;
        if (!(settings.GenerateHoverPreviews || ShowTimelineThumbnails) || duration <= 0 || _mediaInfo?.Video is null)
            return;

        var cancellation = _spriteCancellation = new CancellationTokenSource();

        // One sheet holds a fixed number of thumbnails; for a long video they are spread further apart
        // so that the sheet still covers all of it. The strip behind the timeline is cut from the same
        // sheet, and wants enough pictures to fill its length even for a short video.
        var capacity = FfmpegRunner.SpriteGridSize * FfmpegRunner.SpriteGridSize;
        var wanted = Math.Max(settings.ThumbnailIntervalSeconds, 1.0);
        if (ShowTimelineThumbnails)
            wanted = Math.Min(wanted, Math.Max(duration / FilmstripPictures, 0.2));
        var interval = Math.Max(wanted, duration / capacity);

        // Made before, for this file as it is and at this spacing: taken from the cache.
        var kind = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sprite{interval:0.###}");
        if (PreviewCache.Timeline.Find(videoPath, kind, ".jpg") is { } kept)
        {
            SpriteIntervalSeconds = interval;
            SpriteSheetPath = kept;
            return;
        }

        var imagePath = Path.Combine(SpriteFolder, $"{Guid.NewGuid():N}.jpg");
        try
        {
            if (await FfmpegRunner.GenerateSpriteSheetAsync(videoPath, imagePath, interval, cancellation.Token)
                && !cancellation.IsCancellationRequested)
            {
                SpriteIntervalSeconds = interval;
                SpriteSheetPath = PreviewCache.Timeline.Keep(videoPath, kind, ".jpg", imagePath);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Previews are a nicety; the app works without them.
        }
        finally
        {
            if (ReferenceEquals(_spriteCancellation, cancellation))
                _spriteCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>How many pictures the strip along the timeline is given to choose from, at least.</summary>
    private const int FilmstripPictures = 40;

    /// <summary>Whether a strip of pictures from the video is drawn along the timeline, behind the waveform.</summary>
    public bool ShowTimelineThumbnails => AppSettings.Current.ShowTimelineThumbnails == true;

    /// <summary>Whether a picture of the video follows the pointer along the timeline.</summary>
    public bool ShowHoverPreviews => AppSettings.Current.GenerateHoverPreviews;

    private static void DeleteSpriteFiles()
    {
        try
        {
            if (Directory.Exists(SpriteFolder))
                Directory.Delete(SpriteFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ----- Filters -----

    [ObservableProperty] private string _lutPath = "";

    [ObservableProperty] private bool _fadeIn;
    [ObservableProperty] private bool _fadeOut;

    // ----- Audio -----

    /// <summary>Lowers the first track (game) while the second (microphone) is loud.</summary>
    [ObservableProperty] private bool _duckAudio;

    // ----- Output -----

    /// <summary>
    /// Target size in MB, tied to the Target Bitrate box: either one can be typed and the other follows.
    /// </summary>
    [ObservableProperty] private string _targetFileSize = "";

    [ObservableProperty] private string _targetSizeHint = "";

    [ObservableProperty] private bool _chaptersAtCuts;

    // Which of the two linked boxes the user typed in last. That one stays put when the length of the
    // output changes; the other is recalculated.
    private bool _sizeDrivesBitrate;
    private bool _syncingSizeAndBitrate;

    // Below this the picture falls apart, so a target size never asks for less.
    private const int LowestVideoKbps = 50;

    partial void OnTargetFileSizeChanged(string value)
    {
        if (_syncingSizeAndBitrate)
            return;

        _sizeDrivesBitrate = !string.IsNullOrWhiteSpace(value);
        if (!_sizeDrivesBitrate)
            return;

        // A size can only be aimed at with a bitrate, so constant quality gives way.
        if (RateControl == ConstantQuality)
            RateControl = ConstantBitrate;
        SyncSizeAndBitrate();
    }

    partial void OnTargetBitrateChanged(string value)
    {
        if (_syncingSizeAndBitrate)
            return;

        _sizeDrivesBitrate = false;
        SyncSizeAndBitrate();
    }

    /// <summary>
    /// Keeps Target File Size and Target Bitrate in step:
    /// video kbps = (TargetMB x 8192) / output seconds - audio kbps, and the same sum the other way round.
    /// </summary>
    private void SyncSizeAndBitrate()
    {
        if (_syncingSizeAndBitrate)
            return;

        var duration = GetOutputDuration();
        var hint = "";
        _syncingSizeAndBitrate = true;
        try
        {
            // Constant quality has no bitrate to tie a size to.
            if (!IsBitrateMode)
            {
                _sizeDrivesBitrate = false;
                TargetFileSize = "";
                return;
            }

            if (duration <= 0 || !HasRateControl || IsAnimatedOutput)
            {
                hint = string.IsNullOrWhiteSpace(TargetFileSize) ? ""
                    : duration <= 0 ? "Load a video to work out the bitrate."
                    : "Not used with this encoder or format.";
                return;
            }

            var tracks = GetOutputAudioTracks();
            var audioKbps = GetOutputAudioKbps(tracks, MergeAudioTracks && tracks.Count > 1);

            if (_sizeDrivesBitrate)
            {
                if (!double.TryParse(TargetFileSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var megabytes) || megabytes <= 0)
                    return;

                var kbps = megabytes * 8192 / duration - audioKbps;
                if (kbps < LowestVideoKbps)
                    hint = $"Too small for this length: the video is held at {LowestVideoKbps} kbps, so the file will be larger.";
                TargetBitrate = ((int)Math.Max(kbps, LowestVideoKbps)).ToString(CultureInfo.InvariantCulture);
            }
            else if (int.TryParse(TargetBitrate, out var bitrate) && bitrate > 0)
            {
                TargetFileSize = ((bitrate + audioKbps) * duration / 8192).ToString("0.#", CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _syncingSizeAndBitrate = false;
            TargetSizeHint = hint;
        }
    }

    // ----- Playback helpers -----

    public IReadOnlyList<string> PlaybackSpeeds { get; } = ["0.25x", "0.5x", "0.75x", "1x", "1.25x", "1.5x", "2x"];

    [ObservableProperty] private string _playbackSpeed = "1x";

    public float PlaybackRate =>
        float.TryParse(PlaybackSpeed.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 1;

    /// <summary>The full transport row under the player: I-frame jumps, frame steps, play/pause, speed.</summary>
    public bool ShowAdvancedPlayback => AppSettings.Current.ShowAdvancedPlayback;

    /// <summary>Just the play button beside the timeline.</summary>
    public bool ShowSimplePlayback => !ShowAdvancedPlayback;

    /// <summary>Whether the player is running, for the play/pause button to show the right symbol. Set by the window.</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>
    /// Moves the playhead onto the I-frame before (-1) or after (+1) where it is, using the same I-frame
    /// index that cut points snap to. Returns false when there is no such I-frame.
    /// </summary>
    public bool SeekToIFrame(int direction)
    {
        if (IFrames.Count == 0)
        {
            StatusText = "No I-frame index for this video (yet).";
            return false;
        }

        // A millisecond of slack either way: the playhead sitting on an I-frame counts as being on it,
        // so the jump goes to the one beyond.
        var seconds = PositionMs / 1000;
        var index = direction < 0
            ? IFrames.FindLastIndex(k => k < seconds - 0.001)
            : IFrames.FindIndex(k => k > seconds + 0.001);
        if (index < 0)
        {
            StatusText = direction < 0 ? "Already at the first I-frame." : "Already past the last I-frame.";
            return false;
        }

        PositionMs = IFrames[index] * 1000;
        StatusText = $"I-frame {index + 1} of {IFrames.Count} at {TimeDisplay.Format(IFrames[index])}";
        return true;
    }

    /// <summary>Moves the playhead by whole frames: one frame is 1000 / frame rate milliseconds.</summary>
    public void StepFrames(int frames)
    {
        if (DurationMs > 0)
            PositionMs = Math.Clamp(PositionMs + frames * 1000 / SourceFrameRate, 0, DurationMs);
    }

    /// <summary>Where the I-frames fall along the timeline, each as a fraction (0 to 1) of its length.</summary>
    public ObservableCollection<double> IFrameMarks { get; } = [];

    private void UpdateIFrameMarks()
    {
        IFrameMarks.Clear();

        var duration = SequenceSeconds;
        if (duration <= 0 || IFrames.Count == 0)
            return;

        var step = Math.Max(1, (int)Math.Ceiling(IFrames.Count / (double)MaxIFrameMarks));
        for (var i = 0; i < IFrames.Count; i += step)
            IFrameMarks.Add(Math.Clamp(IFrames[i] / duration, 0, 1));
    }

    /// <summary>The I-frame nearest a time, in milliseconds, or null without an I-frame index.</summary>
    public double? GetNearestIFrameMs(double positionMs)
    {
        var seconds = positionMs / 1000;
        var floor = GetIFrameFloor(seconds, 0);
        var ceiling = GetIFrameCeiling(seconds, 0);
        var nearest = floor is null ? ceiling
            : ceiling is null ? floor
            : seconds - floor.Value <= ceiling.Value - seconds ? floor : ceiling;
        return nearest * 1000;
    }

    /// <summary>Applies the display settings: time format, and which playback controls are shown.</summary>
    private void ApplyDisplaySettings()
    {
        TimeDisplay.UseFrames = AppSettings.Current.ShowTimesAsFrames;
        TimeDisplay.FrameRate = SourceFrameRate;

        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PendingStartText));
        OnPropertyChanged(nameof(ShowAdvancedPlayback));
        OnPropertyChanged(nameof(ShowSimplePlayback));
        OnPropertyChanged(nameof(ShowCommandPreviewTab));
        OnPropertyChanged(nameof(ShowAdvancedFiltersTab));
        OnPropertyChanged(nameof(IsMediaBrowserVisible));
        OnPropertyChanged(nameof(ShowSubtitleImportRow));
        OnPropertyChanged(nameof(ShowTimelineOptionsBelow));
        OnPropertyChanged(nameof(ShowPresetBarAtTop));
        OnPropertyChanged(nameof(ShowPresetBarInSummary));
        OnPropertyChanged(nameof(TimelineAreaHeight));
        OnPropertyChanged(nameof(AudioTrackRowHeight));
        OnPropertyChanged(nameof(ShowWaveforms));
        OnPropertyChanged(nameof(ShowTimelineThumbnails));
        OnPropertyChanged(nameof(CaptionHint));
    }

    // ----- Dead air -----

    /// <summary>
    /// Finds the silent stretches of a sound and cuts them out of what plays it: the selected video layer (its own sound),
    /// or the main video (the selected audio track of its file, or the first). Remove (and the classic way) takes them out
    /// and closes up what follows on every unlocked track, as a cut list's left-out stretches did; Split &amp; Mark folds them
    /// out the same way and keeps each at its cut, to be put back (4.3D). What counts as silence (how quiet, for how long)
    /// is in the settings, set from the Remove Dead Air dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task RemoveDeadAirAsync() => RunOperationAsync("Removing dead air", WorkLane.Interactive, async cancellationToken =>
    {
        // How, and on what: set by RunDeadAir; left alone, it is the classic way on the main video.
        var (mode, target) = (_deadAirMode, _deadAirTarget);
        (_deadAirMode, _deadAirTarget) = (DeadAirMode.Classic, null);

        if (!HasSource)
            return "Load a video first.";

        IsProgressIndeterminate = true;
        StatusText = "Listening for silence...";
        var noiseDb = Math.Clamp(AppSettings.Current.DeadAirThresholdDb, -80, -10);
        var minimumSeconds = Math.Clamp(AppSettings.Current.DeadAirMinSeconds, 0.2, 30);
        var mark = mode == DeadAirMode.SplitAndMark;

        // A video layer: its own sound is listened to, and the layer is what gets cut up (its time left empty, as in 4.2.0).
        if (target is Layer { CarriesSound: true, IsMainVideo: false } layer && ClipOf(layer) is not null)
        {
            var info = await MediaProbe.ProbeAsync(layer.ImagePath, cancellationToken);
            if (info is not { DurationSeconds: > 0, Audio.Count: > 0 })
                return $"{layer.Name} has no sound to listen to.";

            var found = await FfmpegRunner.DetectSilenceAsync(layer.ImagePath, 0, noiseDb, minimumSeconds, info.DurationSeconds, cancellationToken);
            return ApplyDeadAir([layer], found, mark, closeUp: false);
        }

        if (_mediaInfo is { Audio.Count: 0 })
            return "This video has no audio to analyse.";

        // The main video, listening to the selected audio track, or the first one: every clip of it is cut where the
        // silences it plays are (heard in the file's own time, each clip placing them where it shows that time).
        var listenTo = target is AudioTrack chosen && AudioTracks.Contains(chosen) ? chosen.Index : 0;
        var silences = (await FfmpegRunner.DetectSilenceAsync(LocalMediaPath, listenTo, noiseDb, minimumSeconds, MainMediaSeconds, cancellationToken))
            .Where(s => s.End - s.Start > 0.05).ToList();
        if (silences.Count == 0)
            return string.Create(CultureInfo.InvariantCulture, $"Nothing quieter than {noiseDb:0} dB for {minimumSeconds:0.#} s was found: the main video was left as it is.");

        var views = MainVideoClips().Select(m => ViewOf(m.Clip)).OfType<Layer>().ToList();
        return views.Count == 0 ? "The main video is not on the timeline." : ApplyDeadAir(views, silences, mark, closeUp: true);
    });

    // ----- Chapters at cut points -----

    /// <summary>FFmpeg metadata file naming the chapters: one at each clip of the main video (Chapters at Cut Points), and the markers.</summary>
    public string ChaptersFilePath => Path.Combine(_workFolder, "chapters.txt");

    /// <param name="throughCuts">The export reads the cuts list (a Copy export of the main video's clips end to end): its clock is the list's.</param>
    private void WriteChaptersFile(bool throughCuts = false)
    {
        // Each chapter runs to the start of the next, the last to the end of the output.
        var lines = new List<string> { ";FFMETADATA1" };
        var chapters = BuildChapters(throughCuts);
        var total = throughCuts ? GetOutputDuration() : SequenceSeconds;
        for (var i = 0; i < chapters.Count; i++)
        {
            var end = i + 1 < chapters.Count ? chapters[i + 1].Start : Math.Max(total, chapters[i].Start + 0.001);
            lines.Add("[CHAPTER]");
            lines.Add("TIMEBASE=1/1000");
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"START={(long)Math.Round(chapters[i].Start * 1000)}"));
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"END={(long)Math.Round(end * 1000)}"));
            lines.Add($"title={EscapeMetadata(chapters[i].Title)}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChaptersFilePath)!);
            File.WriteAllLines(ChaptersFilePath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not write {ChaptersFilePath}: {ex.Message}";
        }
    }
}
