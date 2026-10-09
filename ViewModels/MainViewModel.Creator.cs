using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
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
                StatusText = "Vertical Video: the picture sits in the middle of a tall frame, over a blurred copy of itself. Zoom and place it on the Layers tab or with Edit Layout.";
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
        Checkpoint("add a layer");
        var element = new Layer { Name = $"Layer {Layers.Count + 1}", PositionY = NextLayerY() };

        // The first piece of video in a layout ties the layout to the shape of this video.
        if (!Layers.Any(e => e.IsVideo) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        AttachLayer(element);
        Log($"Added layer {element.Name}: use Draw Target to mark it on the video");
    }

    /// <summary>Adds a picture from a file as a layer. Returns false when the file is not a readable image.</summary>
    /// <param name="startSeconds">When on the timeline the layer appears; 0 with no length is the whole video.</param>
    public bool AddImageLayer(string path, double startSeconds = 0)
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
            StartTime = Math.Round(Math.Max(startSeconds, 0), 2),
        };
        Checkpoint("add an image");
        AttachLayer(element);
        Log($"Added image layer {element.Name}");
        return true;
    }

    /// <summary>Adds a video from a file as a layer: it plays alongside the main video, and starts again when it runs out.</summary>
    /// <param name="startSeconds">
    /// When on the timeline the layer appears. Placed at a moment, it plays once through from there; left at
    /// the start, it runs for the whole video.
    /// </param>
    public async Task<bool> AddVideoLayerAsync(string path, double startSeconds = 0)
    {
        // The block goes onto the timeline now, marked as loading: nothing is waited for before the drop is
        // answered. What the file is (its size, its length, whether it has sound) is found out afterwards.
        var element = new Layer
        {
            Kind = LayerKind.VideoFile,
            Name = Path.GetFileName(path),
            ImagePath = path,
            SizeWidth = 0.4,
            PositionX = 0.05,
            PositionY = NextLayerY(),
            StartTime = startSeconds > 0.01 ? Math.Round(startSeconds, 2) : 0,
            IsLoading = true,
        };

        Checkpoint("add a video layer");
        AttachLayer(element);
        StatusText = $"Loading {element.Name}...";
        return await CompleteVideoLayerAsync(element, startSeconds > 0.01);
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
    /// Looks at a video layer's file, away from the interface, and fills the layer in with what it finds: then
    /// its block has its real length and its frames are fetched. A file that turns out not to be a video is
    /// taken off the timeline again.
    /// </summary>
    /// <param name="playOnce">Whether the layer was placed at a moment: it then plays once through from there, when that fits.</param>
    private async Task<bool> CompleteVideoLayerAsync(Layer element, bool playOnce)
    {
        var path = element.ImagePath;
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

        // Undone, or removed, while it was being looked at: there is nothing left to fill in.
        if (!Layers.Contains(element))
            return false;

        if (info?.Video is not { Width: > 0, Height: > 0 } video)
        {
            Detach(element);
            StatusText = $"Not a video that can be read: {path}";
            return false;
        }

        using (DeferCommand())
        {
            (element.ImageWidth, element.ImageHeight) = (video.Width, video.Height);
            (element.HasAudio, element.MediaDuration) = (info.Audio.Count > 0, Math.Max(info.DurationSeconds, 0));
            DescribeSound(element, info);
            if (playOnce && element.Duration <= 0.001)
            {
                var left = SequenceSeconds - element.StartTime;
                element.Duration = info.DurationSeconds > 0.05 && info.DurationSeconds < left ? Math.Round(info.DurationSeconds, 2) : 0;
            }

            element.IsLoading = false;
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
            StartTime = Math.Round(Math.Max(startSeconds, 0), 2),
        };
        DescribeSound(element, info);

        // Its block is as long as the sound is, unless that runs past the end of the video.
        if (element.MediaDuration > 0.05 && element.StartTime + element.MediaDuration < SequenceSeconds)
            element.Duration = Math.Round(element.MediaDuration, 2);
        (element.IsVoice, element.Name) = (isVoice, name ?? element.Name);
        Checkpoint(isVoice ? "add a voiceover" : "add an audio clip");
        AttachLayer(element);
        _ = LoadLayerPicturesAsync(element);
        if (isVoice)
            return true;

        Log($"Added audio clip {element.Name} at {TimeDisplay.Format(element.StartTime)}");
        return true;
    }

    // ----- Stacking order -----

    /// <summary>Whether the tools of Editor Mode are in use: the Layers tab, and the Layer Engine composing every picture.</summary>
    public bool IsEditorMode => AppSettings.Current.UiMode == AppSettings.EditorMode;

    public bool IsEncoderMode => !IsEditorMode;

    /// <summary>
    /// Whether the timeline's checkboxes (snapping, play only cut segments, I-frames) and the cut buttons sit
    /// under the timeline. In Editor Mode they are up in the transport row instead, while there is one.
    /// </summary>
    public bool ShowTimelineOptionsBelow => IsEncoderMode || !ShowAdvancedPlayback;

    /// <summary>Raised when something about the layers has changed that their time bars may have to be drawn again for.</summary>
    public event Action? TimelineChanged;

    /// <summary>The main video's place in the stack: how many of the layers lie under it. 0 is beneath them all.</summary>
    [ObservableProperty] private int _mainVideoIndex;

    partial void OnMainVideoIndexChanged(int value) => RefreshLayerRows();

    // The two layers that are always there, as rows of the list.
    private readonly Layer _mainVideoRow = new() { Kind = LayerKind.MainVideo, Name = "Main Video" };
    private readonly Layer _backgroundRow = new() { Kind = LayerKind.Background, Name = "Background Blur" };

    /// <summary>The pictures that can be reordered, bottom first: the ones added by hand, with the main video among them. Sounds have no place in it.</summary>
    private List<Layer> GetStack()
    {
        var stack = Layers.Where(l => !l.IsAudio).ToList();
        stack.Insert(Math.Clamp(MainVideoIndex, 0, stack.Count), _mainVideoRow);
        return stack;
    }

    /// <summary>The stack as tracks, bottom first: the clips of one track lie together, and move together.</summary>
    private List<List<Layer>> GetStackTracks()
    {
        var tracks = new List<List<Layer>>();
        foreach (var layer in GetStack())
        {
            if (tracks.Count > 0 && !layer.IsMainVideo && tracks[^1][0] is { IsMainVideo: false } last && last.TrackId == layer.TrackId)
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

    /// <summary>Moves a layer's track one place towards the front (+1) or the back (-1).</summary>
    private void MoveLayer(Layer? layer, int by)
    {
        var tracks = GetStackTracks();
        var from = layer is null ? -1 : tracks.FindIndex(t => t.Contains(layer));
        var to = from + by;
        if (from < 0 || to < 0 || to >= tracks.Count)
            return;

        Checkpoint($"move {layer!.Name} {(by > 0 ? "up" : "down")}");
        (tracks[from], tracks[to]) = (tracks[to], tracks[from]);
        var stack = tracks.SelectMany(t => t).ToList();

        // Written back as the two things it is kept as: the order of the layers, and where the main video is among them.
        var main = stack.IndexOf(_mainVideoRow);
        var elements = stack.Where(l => !ReferenceEquals(l, _mainVideoRow)).ToList();
        for (var i = 0; i < elements.Count; i++)
        {
            var current = Layers.IndexOf(elements[i]);
            if (current != i)
                Layers.Move(current, i);
        }

        MainVideoIndex = main;
        RefreshLayerRows();
        StatusText = $"{layer!.Name} moved {(by > 0 ? "up" : "down")}: it is now {(to == tracks.Count - 1 ? "the front layer" : to == 0 ? "the back layer, just above the background" : $"layer {to + 1} of {tracks.Count} from the back")}.";
    }

    // ----- Splitting: the razor -----

    /// <summary>
    /// Cuts a clip in two at a time: the first part ends there and a second clip, the same in everything
    /// else, begins there, on the same track. Each can then be moved in time, trimmed or deleted on its own.
    /// </summary>
    public bool SplitLayer(Layer layer, double seconds)
    {
        var total = SequenceSeconds;
        var end = layer.Duration > 0.001 ? layer.StartTime + layer.Duration : total;
        if (!layer.HasTiming || layer.IsMainVideo || seconds <= layer.StartTime + 0.05 || seconds >= end - 0.05)
        {
            StatusText = $"The playhead is not inside {layer.Name}: move it to where the layer should be split.";
            return false;
        }

        Checkpoint($"split {layer.Name}");
        var second = Layer.FromState(layer.ToState(), SourceWidth, SourceHeight, FrameWidth, FrameHeight);
        (second.Waveform, second.Filmstrip) = (layer.Waveform, layer.Filmstrip);
        (second.StartTime, second.Duration) = (seconds, end - seconds);
        second.MediaOffset = layer.MediaOffset + (seconds - layer.StartTime) * layer.Speed;

        // The motion carries on across the cut: the second part's keyframes are counted from its own beginning.
        second.ShiftKeys(layer.StartTime - seconds);
        layer.Duration = seconds - layer.StartTime;

        Hook(second);
        InsertLayer(Layers.IndexOf(layer) + 1, second);
        Log($"Split clip: {layer.Name} at {TimeDisplay.Format(seconds)}");
        return true;
    }

    /// <summary>
    /// Cuts the main video at a time: the cut segment the time lies in becomes two that meet there, each of
    /// which can then be removed on its own. With no cuts yet, the whole video becomes two segments.
    /// </summary>
    public bool SplitSegment(CutSegment? segment, double seconds)
    {
        var total = SequenceSeconds;
        var at = TimeSpan.FromSeconds(seconds);
        segment ??= Segments.FirstOrDefault(s => at > s.Start && at < s.End);

        if (segment is null && Segments.Count == 0 && seconds > 0.05 && seconds < total - 0.05)
        {
            Checkpoint("split the video");
            Segments.Add(new CutSegment(TimeSpan.Zero, at));
            Segments.Add(new CutSegment(at, TimeSpan.FromSeconds(total)));
            Log($"Split the video at {TimeDisplay.Format(seconds)} into two segments");
            return true;
        }

        if (segment is null || at <= segment.Start + TimeSpan.FromMilliseconds(50) || at >= segment.End - TimeSpan.FromMilliseconds(50))
        {
            StatusText = "The playhead is not inside a cut segment: move it to where the video should be split.";
            return false;
        }

        Checkpoint("split a segment");
        var index = Segments.IndexOf(segment);
        Segments[index] = new CutSegment(segment.Start, at) { IsSkipped = segment.IsSkipped };
        Segments.Insert(index + 1, new CutSegment(at, segment.End) { IsSkipped = segment.IsSkipped });
        Log($"Split segment at {TimeDisplay.Format(seconds)}");
        return true;
    }

    // ----- HUD layers -----

    /// <summary>
    /// Takes a game's HUD apart: one layer per piece, each cut out of the video where that piece sits and laid
    /// back in the same place, with a mask for the pieces that are not rectangles. From there each can be moved.
    /// </summary>
    /// <param name="vertical">Lays the pieces out for a tall frame, above and below the video, and makes the frame tall; otherwise each piece is laid back where it was cut from.</param>
    public void AddHudLayers(HudGame game, bool vertical = false)
    {
        Checkpoint($"add the {game.Name} HUD layers");
        if (vertical && !UseVerticalResolution)
            UseVerticalResolution = true;

        foreach (var state in HudLibrary.BuildLayers(game, vertical))
        {
            state.TrackId = 0;
            AttachLayer(Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight));
        }

        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        FrameEngine = true;

        // The main video in front of the pieces cut from it: it is what is being watched.
        MainVideoIndex = Layers.Count(l => !l.IsAudio);
        StatusText = vertical
            ? $"Added {game.Pieces.Count} layers for {game.Name}, laid out above and below the video. Check each against your video: right-click a layer and choose Redraw / Modify Mask to correct it."
            : $"Added {game.Pieces.Count} layers for {game.Name}. They sit where the HUD is by default: check each against your video and correct it with Draw Target.";
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

            // Copy takes the file as it is: a main video that was moved, trimmed or split is none of those in the output.
            if (!IsMainWholeSequence)
                return "Stream Copy selected: the main video has been moved, trimmed or split on the timeline, and -c copy ignores all of that. Choose an encoder on the Video tab.";

            var skipped = Layers.Any(e => e.IsUsable) || AutoCaptions || HasCrop || Deinterlace || Denoise || FadeIn || FadeOut
                          || !string.IsNullOrWhiteSpace(LutPath) || BuildColorFilters().Count > 0
                          || !string.IsNullOrWhiteSpace(OutputWidth) || !string.IsNullOrWhiteSpace(OutputHeight)
                          || Math.Abs(CenterZoom - 1) > 0.001 || CenterOffsetX != 0 || CenterOffsetY != 0;
            return skipped ? "Stream Copy selected: Layers and Filters will be bypassed." : "";
        }
    }

    // Stacked down the frame, so a new layer does not land exactly on top of the last.
    private double NextLayerY() => Math.Min(0.03 + Layers.Count * 0.2, 0.8);

    [RelayCommand]
    private void RemoveLayer(Layer? element)
    {
        if (element is null)
            return;

        Checkpoint($"remove {element.Name}");
        Detach(element);
    }

    /// <summary>
    /// Takes a layer out of the list. The main video's place in the stack is a count of the pictures under it,
    /// so a picture that goes from under it takes one off that count: otherwise the main video would climb
    /// over the next layer up.
    /// </summary>
    private void Detach(Layer element)
    {
        var index = Layers.IndexOf(element);
        if (index < 0)
            return;

        if (ReferenceEquals(DrawTargetLayer, element))
            DrawTargetLayer = null;
        element.PropertyChanged -= OnLayerChanged;
        var under = !element.IsAudio && Layers.Take(index).Count(l => !l.IsAudio) < MainVideoIndex;
        Layers.RemoveAt(index);
        if (under)
            MainVideoIndex--;
    }

    /// <summary>Puts a layer into the list at a place of its own; one that goes in under the main video adds one to that count.</summary>
    private void InsertLayer(int index, Layer element)
    {
        index = Math.Clamp(index, 0, Layers.Count);
        var under = !element.IsAudio && Layers.Take(index).Count(l => !l.IsAudio) < MainVideoIndex;
        Layers.Insert(index, element);
        if (under)
            MainVideoIndex++;
    }

    /// <summary>Adds a layer to the list: a picture above the other pictures, a sound after everything.</summary>
    private void AttachLayer(Layer element)
    {
        Hook(element);
        var firstSound = element.IsAudio ? -1 : Layers.ToList().FindIndex(l => l.IsAudio);
        if (firstSound >= 0)
            Layers.Insert(firstSound, element);
        else
            Layers.Add(element);
    }

    /// <summary>Takes a layer into use: its changes are listened to, it is given a track when it has none, and from now on it grows about its middle.</summary>
    private void Hook(Layer element)
    {
        element.PropertyChanged += OnLayerChanged;
        if (element.TrackId <= 0)
            element.TrackId = Layers.Select(l => l.TrackId).DefaultIfEmpty().Max() + 1;
        element.AnchorCenter = true;
    }

    /// <summary>The clips on the same track as a layer, in the order they are kept; the layer alone when it is not one of the list.</summary>
    public List<Layer> GetTrackClips(Layer layer) =>
        Layers.Contains(layer) ? Layers.Where(l => l.TrackId == layer.TrackId && l.IsAudio == layer.IsAudio).ToList() : [layer];

    /// <summary>The main video as a layer: its look, its filters and how it is turned. Where it sits is the Center controls.</summary>
    public Layer MainLayer => _mainVideoRow;

    public Layer BackgroundLayer => _backgroundRow;

    private void SetLayers(IEnumerable<LayerState> states)
    {
        DrawTargetLayer = null;
        foreach (var element in Layers)
            element.PropertyChanged -= OnLayerChanged;
        Layers.Clear();
        foreach (var state in states)
        {
            var layer = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);

            // Two layers that came with the same track number but are different things are not one track.
            if (Layers.FirstOrDefault(l => l.TrackId == layer.TrackId) is { } other && (other.Kind != layer.Kind || other.ImagePath != layer.ImagePath))
                layer.TrackId = 0;
            AttachLayer(layer);

            // Saved while it was still being looked at (an undo step taken a moment after a drop): looked at again.
            if (layer is { IsVideoFile: true, ImageWidth: <= 0 })
            {
                layer.IsLoading = true;
                _ = CompleteVideoLayerAsync(layer, playOnce: false);
            }
            else
            {
                _ = LoadLayerPicturesAsync(layer);
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

        AttachLayer(new Layer
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
        });
        FrameEngine = true;
    }

    private void OnLayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Releasing the aspect lock: the layer keeps the shape it has until it is stretched.
        if (e.PropertyName == nameof(Layer.LockAspectRatio) && sender is Layer { LockAspectRatio: false } element)
            element.StartFreeHeight(FrameWidth, FrameHeight, SourceWidth, SourceHeight);

        // Marked again on the video that is loaded: the layout now fits this shape.
        if (e.PropertyName is nameof(Layer.SourceWidth) or nameof(Layer.SourceHeight) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;

        // A track is one thing with several clips on it: where it sits, how it looks and what filters it has
        // are the track's, so a change to one clip is made to the others on its track as well.
        if (_copyingToTrack || _applyingKeys || sender is not Layer source)
            return;

        if (e.PropertyName == Layer.KeysChanged)
            RefreshAnyKeys();

        // Muted, or heard again, while dropped tracks are hidden: its row on the Audio tab goes, or comes back.
        if (e.PropertyName is nameof(Layer.IsHidden) or nameof(Layer.IsSoundMuted) && HideDroppedTracks)
            RefreshAudioRows();

        // Moved, sized or turned by hand: what that does to its keyframes.
        RecordKey(source, e.PropertyName);
        TimelineChanged?.Invoke();

        // A picture of the clip arriving is something to draw, and nothing to do with the command.
        if (e.PropertyName is nameof(Layer.Waveform) or nameof(Layer.Filmstrip))
            return;

        if (sender is Layer changed && e.PropertyName is { } name && !Layer.IsClipProperty(name) && GetTrackProperty(name) is { } property)
        {
            var others = Layers.Where(l => !ReferenceEquals(l, changed) && l.TrackId == changed.TrackId && l.IsAudio == changed.IsAudio).ToList();
            if (others.Count > 0)
            {
                _copyingToTrack = true;
                try
                {
                    var value = property.GetValue(changed);
                    foreach (var other in others)
                        other.FromCorner(() => property.SetValue(other, value));
                }
                finally
                {
                    _copyingToTrack = false;
                }
            }
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

    partial void OnVideoEncoderChanged(EncoderOption value) => UpdateEncoderPresets();

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
    /// Renders a few seconds with the current filters, at reduced size, and hands the clip to the window.
    /// It starts at the selected cut segment, or the first one; without cuts, at the beginning.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task RenderPreviewAsync() => RunOperationAsync(async cancellationToken =>
    {
        if (!HasSource)
            return "Load a video first.";

        var settings = AppSettings.Current;
        var seconds = Math.Clamp(settings.PreviewDurationSeconds, 1, 60);
        var start = (SelectedSegment ?? Segments.FirstOrDefault())?.Start.TotalSeconds ?? 0;

        // With Play only cut segments on, the preview is of the trimmed timeline: the kept segments end to
        // end, from the selected one on, for as long as a preview lasts. Otherwise it is one stretch of the source.
        var ranges = new List<(double Start, double End)>();
        if (PlayOnlySegments && Segments.Count > 0)
        {
            var left = (double)seconds;
            foreach (var segment in GetMergedSegments().Where(s => s.End.TotalSeconds > start + 0.001))
            {
                var from = Math.Max(segment.Start.TotalSeconds, start);
                var length = Math.Min(segment.End.TotalSeconds - from, left);
                if (length < 0.05)
                    continue;

                ranges.Add((from, from + length));
                left -= length;
                if (left < 0.05)
                    break;
            }
        }

        // Burned-in subtitles are placed by the source's clock, which joined-up stretches do not keep.
        if (ranges.Count < 2 || SubtitleTracks.Any(t => t.Action == SubtitleTrack.HardSub))
            ranges = [ranges.Count > 0 ? (ranges[0].Start, ranges[0].Start + seconds) : (start, start + seconds)];
        start = ranges[0].Start;

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

        await FfmpegRunner.RunAsync(command, progress, cancellationToken);
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

        var imagePath = Path.Combine(SpriteFolder, $"{Guid.NewGuid():N}.jpg");
        try
        {
            if (await FfmpegRunner.GenerateSpriteSheetAsync(videoPath, imagePath, interval, cancellation.Token)
                && !cancellation.IsCancellationRequested)
            {
                SpriteIntervalSeconds = interval;
                SpriteSheetPath = imagePath;
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
        OnPropertyChanged(nameof(IsEditorMode));
        OnPropertyChanged(nameof(IsEncoderMode));
        OnPropertyChanged(nameof(ShowTimelineOptionsBelow));
        OnPropertyChanged(nameof(ShowPresetBarAtTop));
        OnPropertyChanged(nameof(ShowPresetBarInSummary));
        OnPropertyChanged(nameof(TimelineAreaHeight));
        OnPropertyChanged(nameof(AudioTrackRowHeight));
        OnPropertyChanged(nameof(ShowWaveforms));
        OnPropertyChanged(nameof(ShowTimelineThumbnails));
        OnPropertyChanged(nameof(CaptionHint));
        foreach (var segment in Segments)
            segment.RefreshDisplay();
    }

    // ----- Dead air -----

    /// <summary>
    /// Finds the silent stretches of the first audio track and replaces the cut list with everything in between.
    /// What counts as silence (how quiet, for how long) is in the settings, set from the Remove Dead Air dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task RemoveDeadAirAsync() => RunOperationAsync(async cancellationToken =>
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

        // A video layer: its own sound is listened to, and the layer is what gets cut up.
        if (target is Layer { CarriesSound: true } layer && Layers.Contains(layer))
        {
            var info = await MediaProbe.ProbeAsync(layer.ImagePath, cancellationToken);
            if (info is not { DurationSeconds: > 0, Audio.Count: > 0 })
                return $"{layer.Name} has no sound to listen to.";

            var found = await FfmpegRunner.DetectSilenceAsync(layer.ImagePath, 0, noiseDb, minimumSeconds, info.DurationSeconds, cancellationToken);
            return ApplyDeadAirToLayer(layer, found, mode == DeadAirMode.SplitAndMark, info.DurationSeconds);
        }

        if (_mediaInfo is { Audio.Count: 0 })
            return "This video has no audio to analyse.";

        // The main video, listening to the selected audio track, or the first one.
        var listenTo = target is AudioTrack chosen && AudioTracks.Contains(chosen) ? chosen.Index : 0;
        if (IsMainSpliced)
            return "The main video has been split into clips: Remove Dead Air works on it while it is one clip (right-click its row: Back to the Start of the Timeline, Whole).";

        // Heard in the file's own time, and put on the timeline where the main video is.
        var (mainFrom, duration) = GetMainSpan();
        var shift = MainShift;
        var silences = (await FfmpegRunner.DetectSilenceAsync(LocalMediaPath, listenTo, noiseDb, minimumSeconds, MainMediaSeconds, cancellationToken))
            .Select(s => (Start: Math.Max(s.Start + shift, mainFrom), End: Math.Min(s.End + shift, duration))).Where(s => s.End - s.Start > 0.05).ToList();
        if (silences.Count == 0)
            return $"Nothing quieter than {noiseDb:0} dB for {minimumSeconds:0.#} s was found: the cut list was left as it is.";

        // Keep what lies between the silences.
        var kept = new List<CutSegment>();
        var cursor = mainFrom;
        foreach (var (start, end) in silences.OrderBy(s => s.Start))
        {
            if (start - cursor >= ShortestKeptSeconds)
                kept.Add(new CutSegment(TimeSpan.FromSeconds(cursor), TimeSpan.FromSeconds(start)));
            cursor = Math.Max(cursor, end);
        }

        if (duration - cursor >= ShortestKeptSeconds)
            kept.Add(new CutSegment(TimeSpan.FromSeconds(cursor), TimeSpan.FromSeconds(duration)));

        if (kept.Count == 0)
            return "The whole video counts as silence: the cut list was left as it is.";

        // Split & Mark: the silences stay on the timeline as segments of their own, marked as skipped.
        if (mode == DeadAirMode.SplitAndMark)
            kept.AddRange(silences.Select(s => new CutSegment(TimeSpan.FromSeconds(s.Start), TimeSpan.FromSeconds(Math.Min(s.End, duration))) { IsSkipped = true }));

        Checkpoint("remove dead air");
        Segments.Clear();
        PendingStartMs = null;
        foreach (var segment in kept.OrderBy(s => s.Start))
            Segments.Add(segment);

        // Snapping grows segments onto I-frames; marked silences are left exactly where they were heard.
        if (SnapToIFrames && mode != DeadAirMode.SplitAndMark)
            SnapSegmentsToIFrames();

        var removed = silences.Sum(s => s.End - s.Start);
        var count = $"{silences.Count} silent stretch{(silences.Count == 1 ? "" : "es")} below {noiseDb:0} dB ({removed:0.#} s)";
        return mode == DeadAirMode.SplitAndMark
            ? $"Marked {count} as skipped: they stay on the timeline, greyed out, and are left out of the output. Right-click one to bring it back."
            : $"Removed {count}: {Segments.Count} segment{(Segments.Count == 1 ? "" : "s")} kept.";
    });

    // ----- Chapters at cut points -----

    /// <summary>FFmpeg metadata file naming one chapter per kept segment.</summary>
    public string ChaptersFilePath => Path.Combine(_workFolder, "chapters.txt");

    private void WriteChaptersFile(List<CutSegment> segments)
    {
        var lines = new List<string> { ";FFMETADATA1" };
        var start = 0.0;
        for (var i = 0; i < segments.Count; i++)
        {
            // In the output the segments follow one another, so each chapter starts where the previous one ended.
            var end = start + segments[i].Duration.TotalMilliseconds;
            lines.Add("[CHAPTER]");
            lines.Add("TIMEBASE=1/1000");
            lines.Add($"START={(long)start}");
            lines.Add($"END={(long)end}");
            lines.Add($"title=Segment {i + 1}");
            start = end;
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
