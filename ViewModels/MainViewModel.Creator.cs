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

    // More keyframe lines than this would be closer together than a pixel on any screen.
    private const int MaxKeyframeMarks = 1500;

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

            if (value && !FrameEngine)
                StatusText = "Vertical resolution set. Without the Frame & Layer Engine (Filters tab) the picture is stretched to fit it.";
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
    public ObservableCollection<OverlayRegion> UiElements { get; } = [];

    /// <summary>False with the engine: its layers are composed with square pixels.</summary>
    public bool CanSetPixelAspect => !FrameEngine;

    partial void OnFrameEngineChanged(bool value)
    {
        // Nothing to mark without the frame; the layout pane stays only while it has captions to show.
        OnPropertyChanged(nameof(CanEditLayout));
        if (!value)
            DrawTargetElement = null;
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
    /// corner stays where it is, as when any other element is resized by its corner.
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
    private void AddElement()
    {
        var element = new OverlayRegion { Name = $"Element {UiElements.Count + 1}", PositionY = NextElementY() };

        // The first piece of video in a layout ties the layout to the shape of this video.
        if (!UiElements.Any(e => e.IsVideo) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        AttachUiElement(element);
        StatusText = $"Added {element.Name}. Use Draw Target to mark it on the video.";
    }

    /// <summary>Adds a picture from a file as an element. Returns false when the file is not a readable image.</summary>
    public bool AddImageElement(string path)
    {
        if (ImageInfo.GetSize(path) is not { } size)
        {
            StatusText = $"Not an image that can be read: {path}";
            return false;
        }

        var element = new OverlayRegion
        {
            Kind = ElementKind.Image,
            Name = Path.GetFileName(path),
            ImagePath = path,
            ImageWidth = size.Width,
            ImageHeight = size.Height,
            SizeWidth = 0.25,
            PositionX = 0.05,
            PositionY = NextElementY(),
        };
        AttachUiElement(element);
        StatusText = $"Added {element.Name}. Use Arrange to place it on the frame.";
        return true;
    }

    /// <summary>Adds a video from a file as a layer: it plays alongside the main video, and starts again when it runs out.</summary>
    public async Task<bool> AddVideoElementAsync(string path)
    {
        var info = await MediaProbe.ProbeAsync(path, _shutdown.Token);
        if (info?.Video is not { Width: > 0, Height: > 0 } video)
        {
            StatusText = $"Not a video that can be read: {path}";
            return false;
        }

        var element = new OverlayRegion
        {
            Kind = ElementKind.VideoFile,
            Name = Path.GetFileName(path),
            ImagePath = path,
            ImageWidth = video.Width,
            ImageHeight = video.Height,
            SizeWidth = 0.4,
            PositionX = 0.05,
            PositionY = NextElementY(),
        };
        AttachUiElement(element);
        StatusText = $"Added {element.Name} as a video layer. Its sound is not used.";
        return true;
    }

    // ----- Stacking order -----

    /// <summary>Whether the tools of Editor Mode are in use: the Layers tab, and the Layer Engine composing every picture.</summary>
    public bool IsEditorMode => AppSettings.Current.UiMode == AppSettings.EditorMode;

    /// <summary>The main video's place in the stack: how many of the layers lie under it. 0 is beneath them all.</summary>
    [ObservableProperty] private int _mainVideoIndex;

    partial void OnMainVideoIndexChanged(int value) => RefreshElementRows();

    // The two layers that are always there, as rows of the list.
    private readonly OverlayRegion _mainVideoRow = new() { Kind = ElementKind.MainVideo, Name = "Main Video" };
    private readonly OverlayRegion _backgroundRow = new() { Kind = ElementKind.Background, Name = "Background Blur" };

    /// <summary>The layers that can be reordered, bottom first: the ones added by hand, with the main video among them.</summary>
    private List<OverlayRegion> GetStack()
    {
        var stack = UiElements.ToList();
        stack.Insert(Math.Clamp(MainVideoIndex, 0, stack.Count), _mainVideoRow);
        return stack;
    }

    [RelayCommand]
    private void MoveLayerUp(OverlayRegion? layer) => MoveLayer(layer, 1);

    [RelayCommand]
    private void MoveLayerDown(OverlayRegion? layer) => MoveLayer(layer, -1);

    /// <summary>Moves a layer one place towards the front (+1) or the back (-1).</summary>
    private void MoveLayer(OverlayRegion? layer, int by)
    {
        var stack = GetStack();
        var from = layer is null ? -1 : stack.IndexOf(layer);
        var to = from + by;
        if (from < 0 || to < 0 || to >= stack.Count)
            return;

        (stack[from], stack[to]) = (stack[to], stack[from]);

        // Written back as the two things it is kept as: the order of the layers, and where the main video is among them.
        var main = stack.IndexOf(_mainVideoRow);
        var elements = stack.Where(l => !ReferenceEquals(l, _mainVideoRow)).ToList();
        for (var i = 0; i < elements.Count; i++)
        {
            var current = UiElements.IndexOf(elements[i]);
            if (current != i)
                UiElements.Move(current, i);
        }

        MainVideoIndex = main;
        RefreshElementRows();
        StatusText = $"{layer!.Name} moved {(by > 0 ? "up" : "down")}: it is now {(stack.IndexOf(layer) == stack.Count - 1 ? "the front layer" : stack.IndexOf(layer) == 0 ? "the back layer, just above the background" : $"layer {stack.IndexOf(layer) + 1} of {stack.Count} from the back")}.";
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

            var skipped = UiElements.Any(e => e.IsUsable) || AutoCaptions || HasCrop || Deinterlace || Denoise || FadeIn || FadeOut
                          || !string.IsNullOrWhiteSpace(LutPath) || BuildColorFilters().Count > 0
                          || !string.IsNullOrWhiteSpace(OutputWidth) || !string.IsNullOrWhiteSpace(OutputHeight)
                          || Math.Abs(CenterZoom - 1) > 0.001 || CenterOffsetX != 0 || CenterOffsetY != 0;
            return skipped ? "Stream Copy selected: Layers and Filters will be bypassed." : "";
        }
    }

    // Stacked down the frame, so a new element does not land exactly on top of the last.
    private double NextElementY() => Math.Min(0.03 + UiElements.Count * 0.2, 0.8);

    [RelayCommand]
    private void RemoveElement(OverlayRegion? element)
    {
        if (element is null)
            return;

        if (ReferenceEquals(DrawTargetElement, element))
            DrawTargetElement = null;
        element.PropertyChanged -= OnUiElementChanged;
        UiElements.Remove(element);
    }

    private void AttachUiElement(OverlayRegion element)
    {
        element.PropertyChanged += OnUiElementChanged;
        UiElements.Add(element);
    }

    private void SetUiElements(IEnumerable<OverlayRegionState> states)
    {
        DrawTargetElement = null;
        foreach (var element in UiElements)
            element.PropertyChanged -= OnUiElementChanged;
        UiElements.Clear();
        foreach (var state in states)
            AttachUiElement(OverlayRegion.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight));
    }

    /// <summary>
    /// Presets saved before images became elements may carry a watermark. It becomes an image element in
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

        AttachUiElement(new OverlayRegion
        {
            Kind = ElementKind.Image,
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

    private void OnUiElementChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Releasing the aspect lock: the element keeps the shape it has until it is stretched.
        if (e.PropertyName == nameof(OverlayRegion.LockAspectRatio) && sender is OverlayRegion { LockAspectRatio: false } element)
            element.StartFreeHeight(FrameWidth, FrameHeight, SourceWidth, SourceHeight);

        // Marked again on the video that is loaded: the layout now fits this shape.
        if (e.PropertyName is nameof(OverlayRegion.SourceWidth) or nameof(OverlayRegion.SourceHeight) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;

        GenerateCommand();
    }

    // ----- Timeline keyframe lines -----

    /// <summary>Whether the keyframe lines are drawn on the timeline. Remembered between sessions.</summary>
    [ObservableProperty] private bool _showKeyframes = AppSettings.Current.ShowKeyframes;

    partial void OnShowKeyframesChanged(bool value)
    {
        var settings = AppSettings.Current;
        settings.ShowKeyframes = value;

        // Optionally the pull of the timeline thumb towards keyframes goes on and off with the lines.
        // Snapping of the cut points is a separate matter and is not touched.
        if (settings.AutoToggleTimelineSnap)
            settings.SnapTimelineToKeyframes = value;

        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still holds for this session.
        }

        if (settings.AutoToggleTimelineSnap)
            StatusText = value ? "Keyframes shown; the timeline snaps to them." : "Keyframes hidden; timeline snapping is off.";
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
    private static readonly string[] NvencPresets = ["fast", "medium", "slow"];

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
    // marking where a UI element is in the source, and arranging the elements on the output frame.

    /// <summary>While on, a rectangle can be dragged over the video to set the crop.</summary>
    [ObservableProperty] private bool _isInteractiveCropActive;

    /// <summary>The UI element whose source rectangle is being drawn on the video, if any.</summary>
    [ObservableProperty] private OverlayRegion? _drawTargetElement;

    /// <summary>While on, the output frame is drawn over the player and its layers can be dragged into place.</summary>
    [ObservableProperty] private bool _isArrangeActive;

    // The layout has a pane of its own, so it can stay open while a crop or a target is drawn on the video.
    partial void OnIsInteractiveCropActiveChanged(bool value)
    {
        if (value)
            DrawTargetElement = null;
    }

    partial void OnDrawTargetElementChanged(OverlayRegion? value)
    {
        if (value is not null)
            IsInteractiveCropActive = false;
    }

    /// <summary>Starts (or, pressed again, stops) drawing an element's source rectangle on the video.</summary>
    [RelayCommand]
    private void DrawTarget(OverlayRegion? element) =>
        DrawTargetElement = ReferenceEquals(DrawTargetElement, element) ? null : element;

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

    /// <summary>The full transport row under the player: keyframe jumps, frame steps, play/pause, speed.</summary>
    public bool ShowAdvancedPlayback => AppSettings.Current.ShowAdvancedPlayback;

    /// <summary>Just the play button beside the timeline.</summary>
    public bool ShowSimplePlayback => !ShowAdvancedPlayback;

    /// <summary>Whether the player is running, for the play/pause button to show the right symbol. Set by the window.</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>
    /// Moves the playhead onto the keyframe before (-1) or after (+1) where it is, using the same keyframe
    /// index that cut points snap to. Returns false when there is no such keyframe.
    /// </summary>
    public bool SeekToKeyframe(int direction)
    {
        if (Keyframes.Count == 0)
        {
            StatusText = "No keyframe index for this video (yet).";
            return false;
        }

        // A millisecond of slack either way: the playhead sitting on a keyframe counts as being on it,
        // so the jump goes to the one beyond.
        var seconds = PositionMs / 1000;
        var index = direction < 0
            ? Keyframes.FindLastIndex(k => k < seconds - 0.001)
            : Keyframes.FindIndex(k => k > seconds + 0.001);
        if (index < 0)
        {
            StatusText = direction < 0 ? "Already at the first keyframe." : "Already past the last keyframe.";
            return false;
        }

        PositionMs = Keyframes[index] * 1000;
        StatusText = $"Keyframe {index + 1} of {Keyframes.Count} at {TimeDisplay.Format(Keyframes[index])}";
        return true;
    }

    /// <summary>Moves the playhead by whole frames: one frame is 1000 / frame rate milliseconds.</summary>
    public void StepFrames(int frames)
    {
        if (DurationMs > 0)
            PositionMs = Math.Clamp(PositionMs + frames * 1000 / SourceFrameRate, 0, DurationMs);
    }

    /// <summary>Where the keyframes fall along the timeline, each as a fraction (0 to 1) of its length.</summary>
    public ObservableCollection<double> KeyframeMarks { get; } = [];

    private void UpdateKeyframeMarks()
    {
        KeyframeMarks.Clear();

        var duration = _mediaInfo?.DurationSeconds > 0 ? _mediaInfo.DurationSeconds : DurationMs / 1000;
        if (duration <= 0 || Keyframes.Count == 0)
            return;

        var step = Math.Max(1, (int)Math.Ceiling(Keyframes.Count / (double)MaxKeyframeMarks));
        for (var i = 0; i < Keyframes.Count; i += step)
            KeyframeMarks.Add(Math.Clamp(Keyframes[i] / duration, 0, 1));
    }

    /// <summary>The keyframe nearest a time, in milliseconds, or null without a keyframe index.</summary>
    public double? GetNearestKeyframeMs(double positionMs)
    {
        var seconds = positionMs / 1000;
        var floor = GetKeyframeFloor(seconds, 0);
        var ceiling = GetKeyframeCeiling(seconds, 0);
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
        if (!HasSource)
            return "Load a video first.";
        if (_mediaInfo is { Audio.Count: 0 })
            return "This video has no audio to analyse.";

        IsProgressIndeterminate = true;
        StatusText = "Listening for silence...";

        var duration = _mediaInfo?.DurationSeconds > 0 ? _mediaInfo.DurationSeconds : DurationMs / 1000;
        var noiseDb = Math.Clamp(AppSettings.Current.DeadAirThresholdDb, -80, -10);
        var minimumSeconds = Math.Clamp(AppSettings.Current.DeadAirMinSeconds, 0.2, 30);
        var silences = await FfmpegRunner.DetectSilenceAsync(LocalMediaPath, noiseDb, minimumSeconds, duration, cancellationToken);
        if (silences.Count == 0)
            return $"Nothing quieter than {noiseDb:0} dB for {minimumSeconds:0.#} s was found: the cut list was left as it is.";

        // Keep what lies between the silences.
        var kept = new List<CutSegment>();
        var cursor = 0.0;
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

        Segments.Clear();
        PendingStartMs = null;
        foreach (var segment in kept)
            Segments.Add(segment);

        if (SnapToKeyframes)
            SnapSegmentsToKeyframes();

        var removed = silences.Sum(s => s.End - s.Start);
        return $"Removed {silences.Count} silent stretch{(silences.Count == 1 ? "" : "es")} below {noiseDb:0} dB ({removed:0.#} s): {Segments.Count} segment{(Segments.Count == 1 ? "" : "s")} kept.";
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
