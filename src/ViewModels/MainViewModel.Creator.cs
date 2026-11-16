using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Creator tools: playback helpers, the frame and its layers, pro filters, target size and dead-air removal.
public partial class MainViewModel
{
    // Kept stretches shorter than this are not worth a cut of their own.
    private const double ShortestKeptSeconds = 0.25;

    // More I-frame lines than this would be closer together than a pixel on any screen.
    private const int MaxIFrameMarks = 1500;

    // ----- Frame size -----
    // The output frame is the Export tab's Resolution (any aspect); the compositor and the Arrange canvas both use it.
    // Blank means the Sequence's natural size, set by the first file placed; clip crops never change it.

    // Fallback Sequence size before one is set.
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
            // A manual toggle becomes the Sequence's orientation; a preset with no shape keeps it.
            _turnBeforePreset = null;
            var (width, height) = (OutputWidth, OutputHeight);

            // Blank boxes mean the Sequence size; fill them in so there is something to transpose.
            if (value && string.IsNullOrWhiteSpace(width) && string.IsNullOrWhiteSpace(height) && GetSequenceSize() is { } natural)
                (width, height) = (Text(Even(natural.Width)), Text(Even(natural.Height)));

            SetOutputSize(height, width);
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
    // Crops are per clip: the look's CropLeft..CropBottom (fractions) reframe its picture, clamped by Layer.HeldCrop, and the
    // clip box takes the cropped aspect. The Crop boxes (percent) and Interactive Crop edit CropTarget; a preset's crop applies
    // to every TemplateTargets clip. Cropping never resizes the Sequence.

    // A crop must leave a non-empty picture.
    private const double LargestCropTotal = Layer.LargestCropTotal;

    /// <summary>A crop as fractions (0 to 1) of the picture, clamped by <see cref="Layer.HeldCrop(double, double, double, double)"/>.</summary>
    private static (double Left, double Top, double Right, double Bottom) HeldCrop(double left, double top, double right, double bottom) => Layer.HeldCrop(left, top, right, bottom);

    /// <summary>A clip's crop, clamped to leave a non-empty picture.</summary>
    private static (double Left, double Top, double Right, double Bottom) CropOf(Layer view) => HeldCrop(view.CropLeft, view.CropTop, view.CropRight, view.CropBottom);

    /// <summary>What the Crop boxes say, as fractions.</summary>
    private (double Left, double Top, double Right, double Bottom) BoxCrop() => HeldCrop(CropLeft / 100, CropTop / 100, CropRight / 100, CropBottom / 100);

    /// <summary>CropTarget's crop as fractions; the boxes' own values when there is no target.</summary>
    private (double Left, double Top, double Right, double Bottom) TargetCrop() => CropTarget is { } target ? CropOf(target) : BoxCrop();

    /// <summary>Source pixel size of CropTarget's file (else the last opened file); (0, 0) while neither is probed.</summary>
    public (int Width, int Height) CropPictureSize => CropTarget is { ImageWidth: > 0, ImageHeight: > 0 } target ? (target.ImageWidth, target.ImageHeight) : (SourceWidth, SourceHeight);

    /// <summary>CropTarget's kept rectangle in source pixels; the rectangle Interactive Crop draws over the player.</summary>
    public (int Left, int Top, int Width, int Height) GetTargetCropRect()
    {
        var crop = TargetCrop();
        var (width, height) = CropPictureSize;
        var (keptWidth, keptHeight) = CroppedSize(crop, width, height);
        return ((int)(width * crop.Left), (int)(height * crop.Top), keptWidth, keptHeight);
    }

    /// <summary>The cropped size in pixels (even sides); assumes 1920 x 1080 for an unknown picture.</summary>
    private static (int Width, int Height) CroppedSize((double Left, double Top, double Right, double Bottom) crop, int sourceWidth, int sourceHeight) =>
        sourceWidth > 0 && sourceHeight > 0
            ? (Math.Max((int)(sourceWidth * (1 - crop.Left - crop.Right) / 2) * 2, 2), Math.Max((int)(sourceHeight * (1 - crop.Top - crop.Bottom) / 2) * 2, 2))
            : (DefaultSourceWidth, DefaultSourceHeight);

    // Re-entrancy guard: set while ShowCropTarget loads the boxes, while the boxes write into clips, and during ApplyPresetCrop.
    private bool _showingTarget, _takingCrop, _cropForAll;

    // Interactive Crop drag state (one undo step per drag) and the last typed change (edits within 1.5 s merge into one step).
    private bool _cropDragging, _cropDragStepTaken;

    /// <summary>Starts an Interactive Crop drag; its first change takes the drag's single undo step.</summary>
    public void BeginCropDrag() => (_cropDragging, _cropDragStepTaken) = (true, false);

    /// <summary>Ends an Interactive Crop drag.</summary>
    public void EndCropDrag() => _cropDragging = false;

    /// <summary>
    /// Writes changed Crop box values into CropTarget (or every preset target), keeping each box centred on its anchor.
    /// One undo step per drag or typing run; ignored while the boxes are only displaying a clip's crop.
    /// </summary>
    private void TakeCropBoxes()
    {
        // Skipped during undo/redo and its binding settle: the restored document already carries every clip's crop.
        if (_showingTarget || _takingCrop || _placingViews || IsRestoringTimeline)
            return;

        var all = _cropForAll;
        var crop = BoxCrop();
        List<Layer> views = all ? [.. TemplateTargets()] : CropTarget is { ClipId: var id } target && id != Guid.Empty ? [target] : [];
        views.RemoveAll(v => !v.IsFile || CropOf(v) == crop);
        if (views.Count == 0)
            return;

        if (!all)
            TakeCropStep();
        _takingCrop = true;
        try
        {
            foreach (var view in views)
                (view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) = crop;
        }
        finally
        {
            _takingCrop = false;
        }
    }

    /// <summary>Takes the undo checkpoint before a crop change: once per drag or typing run.</summary>
    private void TakeCropStep()
    {
        if (_isBackgroundWorker)
            return;

        if (_cropDragging)
        {
            if (!_cropDragStepTaken)
            {
                _cropDragStepTaken = true;
                Checkpoint("crop the video");
            }

            return;
        }

        var now = Environment.TickCount64;
        if (!_gesture.Crop.Continues(this, "crop", now, MergeWindow, newest: null) && now - _checkpointedAt > HandStepGrace)
            Checkpoint("crop the video");
    }

    /// <summary>Sets the crop from edge insets in source pixels of <see cref="CropPictureSize"/>.</summary>
    public void SetCropFromPixels(double left, double top, double right, double bottom)
    {
        var (width, height) = CropPictureSize;
        if (width <= 0 || height <= 0)
            return;

        CropLeft = ToPercent(left, width);
        CropRight = ToPercent(right, width);
        CropTop = ToPercent(top, height);
        CropBottom = ToPercent(bottom, height);
    }

    private static double ToPercent(double pixels, double of) => Math.Round(Math.Clamp(pixels / of, 0, LargestCropTotal) * 100, 2);

    /// <summary>
    /// A preset's crop as percentages (left, top, right, bottom). Legacy presets store pixels, converted against the last
    /// opened file's size (1920 x 1080 when none).
    /// </summary>
    private (double Left, double Top, double Right, double Bottom) PresetCropPercent(EncodingPreset preset)
    {
        if (preset.CropPercent is { Length: 4 } percent)
            return (percent[0], percent[1], percent[2], percent[3]);

        double width = SourceWidth > 0 ? SourceWidth : DefaultSourceWidth, height = SourceHeight > 0 ? SourceHeight : DefaultSourceHeight;
        return (ToPercent(preset.CropLeft, width), ToPercent(preset.CropTop, height), ToPercent(preset.CropRight, width), ToPercent(preset.CropBottom, height));
    }

    /// <summary>
    /// Applies a preset's crop to each target clip (selected video clips, else every top-level one); with no clips the Crop
    /// boxes hold it for the next file opened.
    /// </summary>
    private void ApplyPresetCrop(EncodingPreset preset)
    {
        _cropForAll = true;
        try
        {
            (CropLeft, CropTop, CropRight, CropBottom) = PresetCropPercent(preset);
        }
        finally
        {
            _cropForAll = false;
        }
    }

    /// <summary>The Sequence's natural size (set by the first file placed), or null while unset.</summary>
    private (int Width, int Height)? GetSequenceSize() => Document.HasNaturalSize ? (Document.NaturalWidth, Document.NaturalHeight) : null;

    /// <summary>The reference aspect for sizes: the natural size (default 1920 x 1080), transposed under Use Vertical Resolution.</summary>
    private (int Width, int Height) GetNaturalSize() => NaturalOf(GetSequenceSize());

    /// <summary>A natural size (1920 x 1080 for none), on its side with Use Vertical Resolution.</summary>
    private (int Width, int Height) NaturalOf((int Width, int Height)? size)
    {
        var (width, height) = size ?? (DefaultSourceWidth, DefaultSourceHeight);
        return UseVerticalResolution ? (height, width) : (width, height);
    }

    /// <summary>
    /// The frame Resolution &amp; Cropping derives from a natural size (null: 1920 x 1080); the space legacy projects laid out
    /// their boxes in during conversion (LegacyMainVideo.Migrate, ConvertLegacy).
    /// </summary>
    private (int Width, int Height) FrameSizeFor((int Width, int Height)? natural)
    {
        var shaped = NaturalOf(natural);
        return RequestedSizeFor(shaped) ?? (Even(shaped.Width), Even(shaped.Height));
    }

    /// <summary>
    /// The size Resolution &amp; Cropping requests, or null to use the Sequence's. A single filled box is completed
    /// from the natural aspect.
    /// </summary>
    private (int Width, int Height)? GetRequestedSize() => RequestedSizeFor(GetNaturalSize());

    private (int Width, int Height)? RequestedSizeFor((int Width, int Height) natural)
    {
        var hasWidth = int.TryParse(OutputWidth, out var width) && width >= SmallestFrameSide;
        var hasHeight = int.TryParse(OutputHeight, out var height) && height >= SmallestFrameSide;

        if (hasWidth && hasHeight)
            return (Even(width), Even(height));
        if (hasWidth)
            return (Even(width), Even((double)width * natural.Height / natural.Width));
        if (hasHeight)
            return (Even((double)height * natural.Width / natural.Height), Even(height));
        return UseVerticalResolution ? (Even(natural.Width), Even(natural.Height)) : null;
    }

    /// <summary>Output frame width the engine composes: the requested Resolution, else the Sequence's.</summary>
    public int FrameWidth => GetRequestedSize()?.Width ?? Even(GetNaturalSize().Width);

    public int FrameHeight => GetRequestedSize()?.Height ?? Even(GetNaturalSize().Height);

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);

    // ----- The layers on the frame -----

    // Blur fill (per video clip via Layer.BlurFill; these values are the Sequence-level BackgroundLayer look): radius in frame
    // pixels (0..64, continuous), passes (1..4), dim, and zoom about the centre (1 = just covers). Effects: BackgroundLayer.Effects.
    public const double DefaultBlurRadius = 20, DefaultBackgroundDim = -0.15, MostBackgroundZoom = 4;
    public const int DefaultBlurPasses = 2, MostBlurPasses = 4;

    [ObservableProperty] private double _blurRadius = DefaultBlurRadius;
    [ObservableProperty] private int _blurPasses = DefaultBlurPasses;
    [ObservableProperty] private double _backgroundDim = DefaultBackgroundDim;
    [ObservableProperty] private double _backgroundZoom = 1;

    partial void OnBlurRadiusChanged(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > EffectRegistry.BackgroundMostRadius)
            BlurRadius = double.IsFinite(value) ? Math.Clamp(value, 0, EffectRegistry.BackgroundMostRadius) : DefaultBlurRadius;
    }

    partial void OnBlurPassesChanged(int value)
    {
        if (value is < 1 or > MostBlurPasses)
            BlurPasses = Math.Clamp(value, 1, MostBlurPasses);
    }

    partial void OnBackgroundZoomChanged(double value)
    {
        if (!double.IsFinite(value) || value < 1 || value > MostBackgroundZoom)
            BackgroundZoom = double.IsFinite(value) ? Math.Clamp(value, 1, MostBackgroundZoom) : 1;
    }

    [RelayCommand]
    private void ResetBlurSettings() => (BlurRadius, BlurPasses, BackgroundDim, BackgroundZoom) = (DefaultBlurRadius, DefaultBlurPasses, DefaultBackgroundDim, 1);

    /// <summary>All clip views: Cutouts (minimap, ammo counter), videos, images (logo) and text.</summary>
    public ViewList Layers { get; } = [];

    /// <summary>
    /// Adds a region Cutout of CropTarget (child on its clock, full span), initially a centred quarter of the picture; the
    /// source rectangle is set later with Draw Target. One undo step.
    /// </summary>
    [RelayCommand]
    private void AddLayer()
    {
        if (CropTarget is not { IsVideoFile: true } target || ClipOf(target) is not { } clip)
        {
            StatusText = "A region is cut from a video clip: open or drop a video first, and select it.";
            return;
        }

        var count = Layers.Count(IsCutout) + 1;
        var state = new LayerState { Kind = LayerKind.Video, Name = $"Region {count}", SourceX = 0.375, SourceY = 0.375, SourceWidth = 0.25, SourceHeight = 0.25 };
        var view = Layer.FromState(state, target.ImageWidth, target.ImageHeight, FrameWidth, FrameHeight);
        FitToFrame(view, fill: false);
        state = view.ToState();
        var template = new ClipTemplate(null, 0, 0, 0, false, [new TemplatePiece(state, Under: false, Offset: 0, Length: 0, FollowsBase: false)]);
        if (!Edit($"add a region of {target.Name}", edit => SpawnCutouts(edit, clip, template, replace: false)))
            return;

        // The first Cutout binds the layout to this video's aspect.
        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        Log($"Added {state.Name}, a region of {target.Name}: use Draw Target to mark it on the video");
    }

    /// <summary>
    /// Whether a clip is a Cutout: a video child of a clip of the same file, on its clock (Game Preset HUD, region, mask part).
    /// </summary>
    public bool IsCutout(Layer view) => view is { IsVideoFile: true, IsDeleted: false } && ClipOf(view) is { } clip && IsCutout(clip);

    /// <summary>Whether a clip is a Cutout: a video clip of the same file as the video clip it is a child of.</summary>
    private bool IsCutout(TimelineClip clip) =>
        clip is { ContentKind: LayerKind.VideoFile, ParentClipId: { } parentId }
        && Document.FindClip(parentId) is { ContentKind: LayerKind.VideoFile } parent && SameFile(clip.SourcePath, parent.SourcePath ?? "");

    /// <summary>
    /// Whether a clip is a linked or freed Cutout (Timeline effect lanes): <see cref="IsCutout(Layer)"/>, or a parentless video clip
    /// (unlocked via SetClipLocked, or trimmed outside its clip) still on a Sub-Lane of a track playing the same file.
    /// </summary>
    public bool IsCutoutOrFreed(Layer view) => view is { IsVideoFile: true, IsDeleted: false } && ClipOf(view) is { } clip && IsCutoutOrFreed(clip);

    /// <summary>Whether a clip is a Cutout, linked to its clip or freed from it on a Sub-Lane of a clip of the same file.</summary>
    private bool IsCutoutOrFreed(TimelineClip clip) =>
        IsCutout(clip)
        || (clip is { ContentKind: LayerKind.VideoFile, ParentClipId: null }
            && Document.TrackOf(clip) is { ParentTrackId: { } parentTrackId }
            && Document.Tracks.Find(t => t.Id == parentTrackId) is { } parentTrack
            && parentTrack.Clips.Any(c => c.ContentKind == LayerKind.VideoFile && SameFile(clip.SourcePath, c.SourcePath ?? "")));

    /// <summary>
    /// Whether a Cutout is a piece (<see cref="IsPiece"/>) of one of the given clips, wherever it lies; tethered clips never count.
    /// Used to clear existing Cutouts before new ones are applied.
    /// </summary>
    private bool IsCutoutOf(Layer view, IReadOnlyCollection<Guid> clips) =>
        ClipOf(view) is { ParentClipId: { } parent } clip && clips.Contains(parent) && IsPiece(clip);

    /// <summary>
    /// End time for a clip placed "to the end": the current timeline end, extended a few seconds when the clip starts there
    /// or the timeline is empty.
    /// </summary>
    private double ToTheEnd(double start) => Math.Max(SequenceSeconds, start + PictureSeconds);

    /// <summary>Adds a new layer as one undo step: on a new top picture track, or on the given row's track.</summary>
    private bool PlaceLayer(Layer element, double start, double end, string label, Layer? onRow = null, bool joins = false) =>
        Edit(label, edit => PlaceNew(edit, element, start, end, onRow, joins));

    /// <summary>Adds an image file as a layer. Returns false when the file is not a readable image.</summary>
    /// <param name="startSeconds">
    /// Start time: runs to the timeline end, or (placed at the end) for a short default duration the timeline grows to hold.
    /// </param>
    /// <param name="onTrack">
    /// The drop row, which selects the track (<see cref="PlaceNew"/>). Sized and centred per <see cref="AppSettings.NewClipSizing"/>.
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
        };

        // Fit or fill the frame per Settings, centred.
        FitToFrame(element, AppSettings.Current.NewClipsFill);

        var start = Math.Max(startSeconds, 0);
        if (!PlaceLayer(element, start, ToTheEnd(start), "add an image", onTrack,
                joins: onTrack is { IsImage: true } && AppSettings.Current.DroppedClipsJoinTrack))
            return false;

        Log($"Added image clip {element.Name}");
        return true;
    }

    /// <summary>
    /// Adds an empty track (+ Add Track): renders nothing, persists in the project and undo, and is replaced by the first
    /// clip dropped on it.
    /// </summary>
    [RelayCommand]
    private void AddEmptyTrack()
    {
        if (Edit("add a track", edit => edit.InsertTrack(TopPictureIndex(), TimelineTrackKind.Video)))
            Log("Added an empty track: drop a video or a picture on it");
    }

    /// <summary>
    /// Whether a clip can move to another track at a start time: the track must accept its kind (empty tracks do) and be free
    /// for the clip's whole length. Never onto the caption box or the blur fill row.
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
    /// Moves a clip to another track starting at <paramref name="start"/>, keeping its look and timing unless the target holds a
    /// template (<see cref="InheritTemplate"/>) or a Track Preset Node (its Cutouts are swapped for the preset's). An emptied
    /// source track is removed. One undo step.
    /// </summary>
    public bool MoveClipToTrack(Layer clip, Layer track, double start)
    {
        start = Math.Max(start, 0);
        if (!CanMoveClipToTrack(clip, track, start) || ClipOf(clip) is not { } model || TrackOfRow(track) is not { } target)
        {
            StatusText = TrackOfRow(track) is { } other && ClipOf(clip) is { } moving && other.Accepts(moving)
                ? $"{clip.Name} would run over a clip on that track: drop it where the track is free, or onto an empty track (+ Add Track)."
                : $"{clip.Name} can only go onto a track that takes its kind of clip, or onto an empty track (+ Add Track).";
            return false;
        }

        var name = target.Clips.Count == 0 ? "the empty track" : track.Name;
        var hadPreset = NodeOf(Document.TrackOf(model));
        // Move the clip's stream-audio and Cutout lanes under the destination track so they leave the old track's stack.
        var source = Document.TrackOf(model);
        if (!Edit($"move {clip.Name} to another track", edit =>
        {
            var followed = source is null ? new List<TimelineTrackModel>() : PictureLanesOf(source);
            edit.MoveClip(model, target, start, allowCrossfadeOverlap: false);
            if (source is not null && Document.TrackOf(model) is { } landed && IsTopPicture(landed))
                Relane(edit, model, source, landed, followed, new Dictionary<Guid, TimelineTrackModel>());
        }))
        {
            return false;
        }

        StatusText = $"{clip.Name} moved to {name}, at {TimeSpan.FromSeconds(model.Start):h\\:mm\\:ss\\.ff}. Ctrl+Z puts it back.";
        InheritTemplate(clip, track, start, hadPreset);
        return true;
    }

    /// <summary>Default duration in seconds of a still placed at the timeline end or on an empty timeline.</summary>
    private const double PictureSeconds = 5;

    /// <summary>Adds a centred text layer and selects it so Text Settings show.</summary>
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

        // Box = measured text at font size, centred on the frame.
        LayoutText(element, keepScale: false);
        var (_, _, width, height) = element.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        if (FrameWidth > 0 && FrameHeight > 0)
            (element.PositionX, element.PositionY) = ((FrameWidth - width) / 2.0 / FrameWidth, (FrameHeight - height) / 2.0 / FrameHeight);

        if (!PlaceLayer(element, 0, ToTheEnd(0), "add a text clip"))
            return;

        KeyLayer = element;
        Log($"Added text clip {element.Name}: type its words under Text Settings");
    }

    /// <summary>
    /// Re-measures a text layer and resizes its box about the centre, preserving the scale relative to font size.
    /// Scale keyframes store widths, so they are rescaled by the same factor as the box.
    /// </summary>
    /// <param name="keepScale">False for a new layer: the box is exactly the text at font size.</param>
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

        var quietly = _settingQuietly;
        _settingQuietly = true;
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
            _settingQuietly = quietly;
        }
    }

    /// <summary>The fonts installed on this computer, for a text layer's font list.</summary>
    public IReadOnlyList<string> SystemFonts => TextLayout.SystemFonts;

    /// <summary>Adds a video file as a clip composited over the clips beneath it.</summary>
    /// <param name="startSeconds">
    /// Start time. Placed at a time it plays once for the file's length (the timeline grows); at 0 it loops to the timeline end.
    /// </param>
    /// <param name="onTrack">
    /// The drop row (<see cref="PlaceNew"/>): fills an empty track, joins a video track taking its look (never its mask), inherits
    /// a track template (<see cref="InheritTemplate"/>), or otherwise gets a new track.
    /// </param>
    public async Task<bool> AddVideoLayerAsync(string path, double startSeconds = 0, Layer? onTrack = null) =>
        await AddVideoClipAsync(path, startSeconds, onTrack) is not null;

    /// <summary>As <see cref="AddVideoLayerAsync"/>, returning the clip once probed; null when it could not be added.</summary>
    /// <param name="playOnce">Play once even at time 0 (multi-file drops placed end to end).</param>
    /// <param name="follows">Force <paramref name="onTrack"/>'s track regardless of Settings: follows the previous clip of one drop.</param>
    /// <param name="sourceRange">Sub-clip range from the Media Browser cutter, in seconds of the MediaOffset clock.</param>
    public async Task<Layer?> AddVideoClipAsync(string path, double startSeconds, Layer? onTrack, bool playOnce = false, bool follows = false, (double In, double Out)? sourceRange = null)
    {
        // Insert a loading placeholder immediately (no wait on the drop); the probe fills size, length and audio later.
        // Until then it has a short stand-in length (trimmed to fit before the next clip) and is not rendered.
        var start = Math.Max(startSeconds, 0);
        DevFlightRecorder.Record("media", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"clip added at {start:0.000}s: {path}"));
        Layer element;
        var row = TrackOfRow(onTrack);
        var joins = onTrack is { IsEmptyTrack: false, IsVideoFile: true } && row is not null
                    && (follows || AppSettings.Current.DroppedClipsJoinTrack || TemplateClipOn(row, start) is not null);
        if (joins && onTrack!.IsVideoFile)
        {
            // Inherit the track's look (placement, size, filters) without any of the source clip's data, mask included.
            var state = onTrack.ToState();
            (state.Name, state.ImagePath, state.ImageWidth, state.ImageHeight, state.MediaDuration) = (Path.GetFileName(path), path, 0, 0, 0);
            (state.HasAudio, state.AudioCodec, state.AudioChannels) = (false, "", "");
            (state.StartTime, state.Duration, state.MediaOffset, state.Speed, state.AudioOffset) = (start, 0, 0, 1, 0);
            (state.IsHidden, state.IsDeleted, state.FadeInSeconds, state.FadeOutSeconds, state.TransitionIn) = (false, false, 0, 0, 0);
            (state.KeysX, state.KeysY, state.KeysScale, state.KeysRotation, state.KeysOpacity) = (null, null, null, null, null);
            (state.CustomMask, state.MaskPath, state.MaskShapes, state.MaskFrame, state.MaskInvert, state.MaskGrow) = (false, "", "", false, false, 0);
            (state.MaskOffsetX, state.MaskOffsetY) = (0, 0);
            element = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
        }
        else
        {
            // Placeholder: frame-wide, centred, 16:9 until the aspect is known (fitted below).
            element = new Layer
            {
                Kind = LayerKind.VideoFile,
                Name = Path.GetFileName(path),
                ImagePath = path,
                SizeWidth = 1,
                PositionX = 0,
                PositionY = FrameWidth > 0 && FrameHeight > 0 ? (1 - FrameWidth * 9.0 / 16 / FrameHeight) / 2 : 0,
            };
        }

        element.IsLoading = true;
        var room = row?.Clips.FirstOrDefault(c => c.Start > start + 0.001)?.Start ?? double.PositiveInfinity;
        var end = joins && room - start >= TimelineDocument.MinimumClipSeconds * 2 ? Math.Min(start + PictureSeconds, room) : start + PictureSeconds;
        if (!PlaceLayer(element, start, end, "add a video clip", onTrack, joins))
            return null;
        StatusText = $"Loading {element.Name}...";

        // Fit or fill the frame per Settings, centred, once the aspect is known.
        (int X, int Y, int Width, int Height)? box = FrameWidth > 0 && FrameHeight > 0 ? (0, 0, FrameWidth, FrameHeight) : null;
        // Sub-clips always play once: at 0 s they would otherwise loop the whole file with only the first round cut.
        var add = new LoadingAdd(playOnce || start > 0 || onTrack is not null || sourceRange is not null, box, AppSettings.Current.NewClipsFill, sourceRange);
        return await FinishVideoAddAsync(element, add) ? element : null;
    }

    /// <summary>
    /// Completes a loading video clip after its probe (<see cref="CompleteVideoLayerAsync"/>) and finishes it per <paramref name="add"/>.
    /// Counted in <see cref="HasPendingAdds"/>; if its tab is backgrounded first, the tab completes it on activation (ViewFor).
    /// </summary>
    private async Task<bool> FinishVideoAddAsync(Layer element, LoadingAdd add)
    {
        var (clipId, generation) = (element.ClipId, _projectGeneration);
        _loadingAdds[clipId] = add;
        bool filled;
        using (BeginAdd())
        {
            try
            {
                filled = await CompleteVideoLayerAsync(element, add.PlayOnce, add.FitInto, fill: add.Fill);
            }
            finally
            {
                // Tab backgrounded meanwhile (DetachForTab): the map now belongs to another project.
                if (generation == _projectGeneration)
                    _loadingAdds.Remove(clipId);
            }
        }

        if (!filled)
            return false;

        // Sub-clips play only their range; Clip Library clips keep their saved in/out. Side-by-side layout is offered, never automatic.
        if (add.SourceRange is { } range)
            FitToSourceRange(element, range.In, range.Out);
        else
            FitToLibraryEntry(element);
        OfferSideBySide(element);
        return true;
    }

    /// <summary>
    /// Fits a layer inside a frame-pixel box, centred at its own aspect, or covers it when <paramref name="fill"/>.
    /// No-op while the layer's aspect is unknown.
    /// </summary>
    private void FitInto(Layer element, (int X, int Y, int Width, int Height) box, bool fill = false)
    {
        var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
        var tall = ShapeOf(element);
        if (tall <= 0 || box.Width <= 0 || box.Height <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return;

        // Fit the cropped aspect.
        if (element.IsFile)
        {
            var (left, top, right, bottom) = element.CropFractions;
            tall *= (1 - top - bottom) / (1 - left - right);
        }

        var width = (double)box.Width;
        var height = width * tall;
        if (fill ? height < box.Height : height > box.Height)
            (width, height) = (box.Height / tall, box.Height);

        element.FromCorner(() =>
        {
            element.SizeWidth = width / frameWidth;
            element.PositionX = (box.X + (box.Width - width) / 2) / frameWidth;
            element.PositionY = (box.Y + (box.Height - height) / 2) / frameHeight;
        });
    }

    /// <summary>A clip's height/width ratio in pixels (file picture, text box, or Cutout source rect); 0 while unknown.</summary>
    private double ShapeOf(Layer element)
    {
        if (element.IsVideo)
        {
            return SourceWidth > 0 && SourceHeight > 0 && element.SourceWidth > 0 && element.SourceHeight > 0
                ? element.SourceHeight * SourceHeight / (element.SourceWidth * SourceWidth)
                : 0;
        }

        return element.ImageWidth > 0 && element.ImageHeight > 0 ? (double)element.ImageHeight / element.ImageWidth : 0;
    }

    /// <summary>
    /// Centres a layer and fits (letterboxed) or fills (covers) the frame at its locked, cropped aspect.
    /// No-op while the layer's aspect is unknown.
    /// </summary>
    private void FitToFrame(Layer element, bool fill)
    {
        var (frameWidth, frameHeight) = (FrameWidth, FrameHeight);
        if (frameWidth <= 0 || frameHeight <= 0)
            return;

        if (!element.LockAspectRatio && ShapeOf(element) > 0)
            element.LockAspectRatio = true;
        FitInto(element, (0, 0, frameWidth, frameHeight), fill);
    }

    /// <summary>Records the first audio stream's codec and channels for the clip's row header.</summary>
    private static void DescribeSound(Layer element, MediaInfo info)
    {
        if (info.Audio.FirstOrDefault() is not { } stream)
            return;

        (element.AudioCodec, element.AudioChannels) = (stream.Codec, stream.ChannelLayout.Length > 0 ? stream.ChannelLayout : $"{stream.Channels} ch");
    }

    /// <summary>Raised when a loading layer is completed and its layout box has its real aspect.</summary>
    public event Action? LayerLoaded;

    /// <summary>
    /// Probes a video layer's file off the UI thread and fills in length and frames. A non-video file is removed again (an
    /// empty track it filled stays). Not an undo step: the drop already was.
    /// </summary>
    /// <param name="playOnce">Placed at a time: play once; otherwise loop to the timeline end.</param>
    /// <param name="fitInto">Frame-pixel box to fit into once the aspect is known; null leaves placement unchanged.</param>
    /// <param name="keepLength">A restored unfinished clip (undo, project load): keep its length, clamped to the file.</param>
    /// <param name="fill">Cover <paramref name="fitInto"/> instead of fitting inside it.</param>
    private async Task<bool> CompleteVideoLayerAsync(Layer element, bool playOnce, (int X, int Y, int Width, int Height)? fitInto = null, bool keepLength = false, bool fill = false)
    {
        var path = element.ImagePath;
        var wanted = ToTheEnd(element.StartTime);
        var (clipId, generation) = (element.ClipId, _projectGeneration);
        MediaInfo? info = null;
        try
        {
            // A fresh drop probes immediately, ahead of Media Browser posters/strips in the Interactive lane, and reuses the
            // browser's cached probe when present. A restored unfinished clip queues normally.
            var token = _shutdown.Token;
            info = await Task.Run(() => MediaProbe.ProbeAsync(path, token, urgent: !keepLength), token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Unreadable: handled below like a non-video file.
        }

        // Tab backgrounded during the probe: edit nothing here (a duplicate tab shares clip ids); ViewFor finishes it on activation.
        if (generation != _projectGeneration)
            return false;

        // Undone or removed during the probe: nothing to fill. After undo+redo the clip has a new view, which is the one filled.
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
        NoteHevcDecoder(video);
        using (DeferCommand())
        {
            (element.ImageWidth, element.ImageHeight) = (video.Width, video.Height);
            (element.HasAudio, element.MediaDuration) = (info.Audio.Count > 0, Math.Max(info.DurationSeconds, 0));
            DescribeSound(element, info);
            if (fitInto is { } box)
                FitInto(element, box, fill);

            // Probe results (length, audio) are not an edit.
            (clip.SourceDuration, clip.AudioStreamIndex) = (Math.Max(info.DurationSeconds, 0), info.Audio.Count > 0 ? 0 : -1);
            // Placed at a time: play once for the file's length; at 0: loop to the end. Length is the video stream's, so audio
            // padded past the last frame adds no frozen or black tail.
            var file = VideoSeconds(info, Math.Max(info.DurationSeconds, 0));
            // Whole-file checks use the video length; a restored clip keeps its saved length (legacy projects ended at the
            // container end and still count as whole-file).
            if (!keepLength)
                clip.PictureDuration = PictureDurationOf(file, clip.SourceDuration);
            var end = keepLength ? clip.End : playOnce || file <= 0.05 ? clip.Start + (file > 0.05 ? file : PictureSeconds) : Math.Max(wanted, clip.Start + file);
            Edit("fill in a video clip", edit => LayOut(edit, clip, end), undoable: false);
            UnpackStreams(element, info, undoable: false);
            element.IsLoading = false;
            _previewState = null;
            GenerateCommand();
        }

        // Audio, if any, becomes audible now at the clip's position and length.
        RefreshLayerRows();
        LayerLoaded?.Invoke();
        _ = LoadLayerPicturesAsync(element);
        Log(element.HasAudio
            ? $"Added video clip {element.Name}, and its sound with it"
            : $"Added video clip {element.Name}");
        return true;
    }

    // Empty tracks filled by loading clips; restored empty if the file is not a video.
    private readonly HashSet<Guid> _filledEmptyTracks = [];

    /// <summary>
    /// Replaces a dropped clip's stand-in length: up to <paramref name="end"/> or the file end, then (running to the timeline end)
    /// as silent loop rounds from frame 0. A clip that would overlap its track moves to a new track just above first.
    /// Each round lasts <see cref="TimelineClip.WholeDuration"/> (video stream length), so seams show no frozen or black tail.
    /// </summary>
    private void LayOut(ITimelineTransaction edit, TimelineClip clip, double end)
    {
        var track = Document.TrackOf(clip)!;
        var whole = clip.WholeDuration;
        var first = whole > 0.05 ? Math.Min(end, clip.Start + (whole - clip.MediaOffset) / clip.Speed) : end;
        if (!track.ClipsIntersecting(clip.Start + 1e-6, end - 1e-6).ToArray().All(c => ReferenceEquals(c, clip)))
        {
            edit.MoveClip(clip, track, clip.Start, insertTrackAtIndex: Document.Tracks.IndexOf(track) + 1, allowCrossfadeOverlap: false);
            track = Document.TrackOf(clip)!;
        }

        edit.TrimClipEnd(clip, first);
        for (var at = clip.End; whole > 0.05 && end - at >= TimelineDocument.MinimumClipSeconds;)
        {
            var round = CopyOfClip(clip);
            (round.Id, round.MediaOffset, round.IsMuted, round.FadeInSeconds, round.TransitionIn) = (Guid.NewGuid(), 0, true, 0, 0);
            (round.Start, round.End) = (at, Math.Min(end, at + whole / clip.Speed));
            edit.PlaceClip(track, round, allowCrossfadeOverlap: false);
            at = round.End;
        }
    }

    /// <summary>
    /// Adds a file's audio (music, recording, or a video's soundtrack) as an audio-only clip mixed into output audio track 1.
    /// </summary>
    /// <param name="isVoice">Marks a voiceover: Auto-Duck clips duck under it.</param>
    /// <param name="name">Clip name override; defaults to the file name.</param>
    /// <param name="sourceRange">Sub-clip range from the Media Browser cutter, in file seconds.</param>
    public async Task<bool> AddAudioLayerAsync(string path, double startSeconds = 0, bool isVoice = false, string? name = null, (double In, double Out)? sourceRange = null)
    {
        // Urgent probe: never queued behind Media Browser work. Counted as a pending add; if its tab is backgrounded anyway,
        // the clip is not placed.
        var generation = _projectGeneration;
        MediaInfo? info;
        using (BeginAdd())
            info = await MediaProbe.ProbeAsync(path, _shutdown.Token, urgent: true);
        if (generation != _projectGeneration)
        {
            StatusText = $"{Path.GetFileName(path)} was not added: its project tab went behind first.";
            return false;
        }

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

        // Length = audio length, clipped to the timeline end; dropped past the end it plays in full and the timeline grows.
        var start = Math.Max(startSeconds, 0);
        var sequence = SequenceSeconds;
        var afterEnd = start > 0.01 && start >= sequence - 0.05;
        var end = element.MediaDuration > 0.05 && (afterEnd || start + element.MediaDuration < sequence || sequence <= 0.05)
            ? start + element.MediaDuration
            : Math.Max(sequence, start + TimelineDocument.MinimumClipSeconds);
        (element.IsVoice, element.Name) = (isVoice, name ?? element.Name);
        if (!PlaceLayer(element, start, end, isVoice ? "add a voiceover" : "add an audio clip"))
            return false;
        if (sourceRange is { } range)
            FitToSourceRange(element, range.In, range.Out);

        _ = LoadLayerPicturesAsync(element);
        if (isVoice)
            return true;

        Log($"Added audio clip {element.Name} at {TimeDisplay.Format(element.StartTime)}");
        return true;
    }

    // ----- Stacking order -----

    /// <summary>Whether the snapping/I-frame toggles and cut buttons sit under the timeline (false while a transport row hosts them).</summary>
    public bool ShowTimelineOptionsBelow => !ShowAdvancedPlayback;

    /// <summary>Raised when a layer change may require the time bars to redraw.</summary>
    public event Action? TimelineChanged;

    // Blur fill settings row: a Sequence-level pseudo-layer (not a document clip) with its own Look-only effect stack;
    // its blur and zoom live on the VM (BlurRadius, BlurPasses, BackgroundDim, BackgroundZoom).
    private readonly Layer _backgroundRow = new() { Kind = LayerKind.Background, Name = "Background Blur" };

    /// <summary>Picture-track clip views, bottom first; audio excluded.</summary>
    private List<Layer> GetStack() => Layers.Where(l => !l.IsAudio).ToList();

    /// <summary>Picture views grouped by track, bottom first.</summary>
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

    /// <summary>Moves a layer's track up (+1) or down (-1) past the adjacent picture track.</summary>
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

        StatusText = $"{layer.Name} moved {(by > 0 ? "up" : "down")}: it is now {(to == pictures.Count - 1 ? "the front track" : to == 0 ? "the back track" : $"track {to + 1} of {pictures.Count} from the back")}.";
    }

    // ----- Splitting: the razor -----

    /// <summary>Splits any clip at a time into two otherwise identical clips on the same track.</summary>
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

    // ----- HUD layers -----

    /// <summary>
    /// The source file and file-clock time shown at a timeline time (the still for mask tools and frame preview): the selected
    /// video if active, else the lowest visible one, else <see cref="CropTarget"/>; null when none.
    /// </summary>
    public (string Path, double Seconds)? FileMomentAt(double sequenceSeconds)
    {
        var target = KeyLayer is { IsVideoFile: true } selected && ClipOf(selected) is { } on && on.Start <= sequenceSeconds + 0.0005 && on.End > sequenceSeconds
            ? selected
            : VideoViewAt(sequenceSeconds) ?? CropTarget;
        if (target is not { IsVideoFile: true } || ClipOf(target) is not { SourcePath: { Length: > 0 } source } clip)
            return null;

        var path = source.Trim().Trim('"');
        var at = Math.Clamp(sequenceSeconds, clip.Start, Math.Max(clip.End - 0.001, clip.Start));
        var shift = MediaProbe.KnownVideoStart(path) is { } video && MediaProbe.KnownContainerStart(path) is { } container ? Math.Max(video - container, 0) : 0;
        return (path, Math.Max(clip.MediaOffset + (at - clip.Start) * clip.Speed + shift, 0));
    }

    /// <summary>Whether a timeline drag is in progress; background results defer until it ends, like other project changes.</summary>
    public Func<bool>? IsTimelineDragHeld { get; set; }

    /// <summary>
    /// Target clips for a Cutout set (mask, Cutout Studio) via <see cref="TemplateTargets"/>; posts a status line when none.
    /// </summary>
    private List<Guid> CutoutTargets(string what)
    {
        var targets = TemplateTargets().Select(v => v.ClipId).Where(id => id != Guid.Empty).ToList();
        if (targets.Count == 0)
            StatusText = $"{what} are cut from a video clip: open or drop a video first.";
        return targets;
    }

    /// <summary>
    /// Creates one Cutout per mask region (sharing the mask), placed in situ or re-laid for a vertical frame, as children above
    /// each target clip (selected, else every top-level video). One undo step.
    /// </summary>
    /// <param name="replace">Moves the targets' existing Cutouts to the recycle bin first.</param>
    public void AddMaskLayers(string maskPath, IReadOnlyList<System.Windows.Rect> pieces, bool vertical, bool replace)
    {
        if (pieces.Count == 0 || CutoutTargets("HUD Cutouts") is not { Count: > 0 } targets)
            return;

        FlushLooks();
        var before = CaptureSnapshot();
        using var held = HoldLookSteps();
        using var whole = DeferCommand();
        if (replace)
        {
            var old = Layers.Where(l => IsCutout(l) && IsCutoutOf(l, targets)).ToList();
            var binned = old.Select(v => (View: v, Deleted: Binned(ClipOf(v)!, Document.TrackOf(ClipOf(v)!)!, v))).ToList();
            if (Edit("take away the Cutouts of the video", edit =>
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

        // Named by region, numbered on name collisions.
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

        var template = new ClipTemplate(null, 0, 0, 0, false, states.ConvertAll(s => new TemplatePiece(s, Under: false, Offset: 0, Length: 0, FollowsBase: false)));
        Edit("add Cutouts from a mask", edit =>
        {
            foreach (var id in targets)
            {
                if (Document.FindClip(id) is { } clip)
                    SpawnCutouts(edit, clip, template, replace: false);
            }
        }, undoable: false);
        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        Checkpoint("add Cutouts from a mask", before);
        StatusText = $"Added {states.Count} Cutout{(states.Count == 1 ? "" : "s")} from the mask, one for each HUD element it shows. Ctrl+Z takes them away again.";
    }

    // ----- Stream copy -----

    /// <summary>
    /// Warning for Copy exports (SequenceSpans.CopyCutOf passes the dominant file's clips through unencoded): lists dropped
    /// filters/looks and uncopied clips; empty when nothing is lost.
    /// </summary>
    public string CopyBypassWarning
    {
        get
        {
            // Audio Only exports copy no picture.
            if (VideoEncoder.Family != EncoderFamily.Copy || IsAnimatedOutput || IsAudioOnlyExport)
                return "";

            var copy = SequenceSpans.CopyCutOf(Document);
            var (frameWidth, frameHeight) = (FrameWidth, FrameHeight);
            var skipped = AutoCaptions || Deinterlace || Denoise || FadeIn || FadeOut
                          || !string.IsNullOrWhiteSpace(LutPath) || !GetSequenceGrade().IsNeutral
                          || !string.IsNullOrWhiteSpace(OutputWidth) || !string.IsNullOrWhiteSpace(OutputHeight)
                          || (copy?.Pieces ?? []).Select(p => _views.GetValueOrDefault(p.ClipId)).OfType<Layer>()
                              .Any(v => v.IsCropped || v.GetOutputRect(frameWidth, frameHeight, SourceWidth, SourceHeight) != (0, 0, frameWidth, frameHeight));

            // Copy cuts without encoding: the file's clips are joined end to end from the I-frames at or before their starts.
            var file = copy is null ? "" : System.IO.Path.GetFileName(copy.Path);
            // Impossible copy cuts are refused at export start (EncoderCommand.WhyCopyCannotCut).
            var cut = copy is null ? " No clip on the timeline shows a file's picture: Copy has nothing to write, so its export is refused."
                : copy.Pieces.Count == 0 ? $" The clips of {file} play at another speed: Copy cannot cut them without encoding, so its export is refused."
                : copy.IsCut ? $" The clips of {file} are joined end to end, each from the I-frame at or before its start (gaps and moves are closed up)."
                : "";
            if (copy is { LeftOut.Count: > 0 } left)
                cut += $" Copy writes one file's picture: {string.Join(", ", left.LeftOut)} {(left.LeftOut.Count == 1 ? "is" : "are")} left out.";
            return skipped ? "Stream Copy selected: effects and filters will be bypassed." + cut : cut.TrimStart();
        }
    }

    /// <summary>Removes a clip (layer tools' Remove, no recycle bin), leaving a gap. One undo step.</summary>
    [RelayCommand]
    private void RemoveLayer(Layer? element)
    {
        if (ClipOf(element) is { } clip)
            Edit($"remove {element!.Name}", edit => edit.DeleteClip(clip));
    }

    /// <summary>
    /// Copies this Cutout's mask (image, outlines, invert, grow) to every other Cutout of the same file; their crops and
    /// own outlines are left unchanged.
    /// </summary>
    /// <returns>The number of Cutouts updated.</returns>
    public int ShareFrameMask(Layer from)
    {
        var count = 0;
        foreach (var layer in Layers.Where(l => IsCutout(l) && !ReferenceEquals(l, from) && string.Equals(l.ImagePath, from.ImagePath, StringComparison.OrdinalIgnoreCase)))
        {
            (layer.CustomMask, layer.MaskPath, layer.MaskFrame, layer.MaskShapes) = (true, from.MaskPath, from.MaskFrame, from.MaskShapes);
            (layer.MaskInvert, layer.MaskGrow) = (from.MaskInvert, from.MaskGrow);
            count++;
        }

        return count;
    }

    /// <summary>The row clip's track-mates in time order; just the row for a stand-in or empty track.</summary>
    public List<Layer> GetTrackClips(Layer layer) =>
        TrackOfRow(layer) is { Clips.Count: > 0 } track ? track.Clips.Select(ViewOf).OfType<Layer>().ToList() : [layer];

    public Layer BackgroundLayer => _backgroundRow;

    private void OnLayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Layer source)
            return;

        // Timing and switches belong to the document and become a clip edit; any other property is a look change
        // (MainViewModel.Document.cs).
        if (NoteViewChange(source, e.PropertyName) || _syncingViews)
            return;

        // Crop changed elsewhere (layer tools): refresh the Crop boxes if this clip is CropTarget.
        if (!_takingCrop && !_placingViews && e.PropertyName is nameof(Layer.CropLeft) or nameof(Layer.CropTop) or nameof(Layer.CropRight) or nameof(Layer.CropBottom)
            && ReferenceEquals(source, CropTarget))
        {
            _cropShown = null;
            ShowCropTarget();
        }

        // Releasing the aspect lock: the layer keeps the shape it has until it is stretched.
        if (e.PropertyName == nameof(Layer.LockAspectRatio) && source is { LockAspectRatio: false })
            source.StartFreeHeight(FrameWidth, FrameHeight, SourceWidth, SourceHeight);

        // Re-marked on the loaded video: the layout now uses this aspect.
        if (e.PropertyName is nameof(Layer.SourceWidth) or nameof(Layer.SourceHeight) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;

        // Derived change, not user input. Looks are per clip: nothing propagates along the track.
        if (_settingQuietly || _applyingKeys)
            return;

        if (e.PropertyName == Layer.KeysChanged)
            RefreshAnyKeys();

        // Manual move/resize/rotate updates keyframes; placement rewritten by a frame-shape change does not.
        if (!_placingViews)
            RecordKey(source, e.PropertyName);

        // Effect parameter values (text, keyframes) affect only pixels/sound (Layer.RaisingEffectValue): look-only.
        // Clip pictures live in ClipPictures, not on the layer, so every change here reaches the command.
        var change = source.RaisingEffectValue ? PreviewChange.Look : PreviewChange.Rows;
        var batched = DeferToLookBatch(change, command: true);
        if (!batched)
            RaiseTimelineChanged(change);

        // Text or font changed: re-measure the box.
        if (source.IsText && Layer.IsTextShapeProperty(e.PropertyName))
            LayoutText(source);

        if (!batched)
            GenerateCommand(change);
    }

    // Set while a layer is written by derived updates (text re-measure, automatic placement), not by user input.
    private bool _settingQuietly;

    // ----- Timeline I-frame lines -----

    /// <summary>Whether the I-frame lines are drawn on the timeline. Remembered between sessions.</summary>
    [ObservableProperty] private bool _showIFrames = AppSettings.Current.ShowIFrames;

    // Set while ShowIFrames syncs from a Settings save (OnSettingsSaved): the saved settings already hold both the lines and
    // the thumb snap, so the toggle writes nothing and leaves the snap unchanged.
    private bool _resyncingShowIFrames;

    partial void OnShowIFramesChanged(bool value)
    {
        // Still raise the change (Scrubber and Timeline Settings menu bind to it); only the manual-toggle side effects are skipped.
        if (_resyncingShowIFrames)
            return;

        // Optionally toggle thumb snapping to I-frames with the lines; cut-point snapping is independent.
        AppSettings.Update(current =>
        {
            current.ShowIFrames = value;
            if (current.AutoToggleTimelineSnap)
                current.SnapTimelineToIFrames = value;
        });
        var settings = AppSettings.Current;

        if (settings.AutoToggleTimelineSnap)
            StatusText = value ? "I-frames shown; the timeline snaps to them." : "I-frames hidden; timeline snapping is off.";
    }

    // ----- Taskbar progress -----

    /// <summary>
    /// Taskbar progress sink set by the owning window (null without one). State is set explicitly since None draws nothing
    /// regardless of value; fraction 0..1.
    /// </summary>
    public ITaskbarProgress? TaskbarProgress { get; set; }

    private void SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState state, double fraction = 0)
    {
        if (!_isBackgroundWorker)
            TaskbarProgress?.Show(state, Math.Clamp(fraction, 0, 1));
    }
    // ----- Encoder presets -----
    // Each encoder family has its own speed/quality steps and FFmpeg option.

    private static readonly string[] SoftwarePresets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private static readonly string[] QsvPresets = ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private static readonly string[] AmfPresets = ["speed", "balanced", "quality", "high_quality"];
    // NVENC's current names: p1 the fastest, p7 the best, p4 the middle.
    private static readonly string[] NvencPresets = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];

    /// <summary>
    /// Default (slowest, highest-quality) step of a hardware family: NVENC p7, AMF high_quality (slower than quality in the
    /// bundled FFmpeg), QSV veryslow (target usage 1); null for families without one.
    /// </summary>
    public static string? BestStepOf(EncoderFamily family) => family switch
    {
        EncoderFamily.Qsv => "veryslow",
        EncoderFamily.Amf => "high_quality",
        EncoderFamily.Nvenc => "p7",
        _ => null,
    };

    /// <summary>
    /// Default (highest-quality) step of an encoder: <see cref="BestStepOf(EncoderFamily)"/> for hardware, veryslow for x264/x265;
    /// null for encoders without steps (MF, SVT-AV1, VP9, editing codecs, Copy). Software keeps a manual choice (UpdateEncoderPresets).
    /// </summary>
    public static string? BestStepOf(EncoderOption encoder) =>
        encoder.Name is "libx264" or "libx265" ? "veryslow" : BestStepOf(encoder.Family);

    /// <summary>
    /// Fastest step of a hardware family, used only by the speed-first Fast Proxy preset: NVENC p1, QSV veryfast, AMF speed;
    /// null otherwise (MF uses -compression_level 0 via MediaFoundationEncoder.FastestStepArgument).
    /// </summary>
    public static string? FastestStepOf(EncoderFamily family) => family switch
    {
        EncoderFamily.Qsv => "veryfast",
        EncoderFamily.Amf => "speed",
        EncoderFamily.Nvenc => "p1",
        _ => null,
    };

    /// <summary>Fastest step of an encoder: <see cref="FastestStepOf(EncoderFamily)"/> for hardware, ultrafast for x264/x265.</summary>
    public static string? FastestStepOf(EncoderOption encoder) =>
        encoder.Name is "libx264" or "libx265" ? "ultrafast" : FastestStepOf(encoder.Family);

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

            // Family changed: hardware resets to its best step even if the old name exists (QSV also has "medium");
            // software keeps its choice when available, else veryslow.
            EncoderPreset = BestStepOf(VideoEncoder.Family) is { } best ? best
                : presets.Contains(previous) ? previous
                : BestStepOf(VideoEncoder) is { } own && presets.Contains(own) ? own
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
    // One player overlay, one mode at a time: crop, mark a UI layer's source rect, or arrange layers on the output frame.

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

    // ----- Interface settings -----

    public bool ShowCommandPreviewTab => AppSettings.Current.ShowCommandPreviewTab;

    // ----- Hover previews -----

    /// <summary>The sheet of timeline thumbnails for the loaded video, or null when there is none (yet).</summary>
    [ObservableProperty] private string? _spriteSheetPath;

    /// <summary>Seconds between two thumbnails on the sheet.</summary>
    public double SpriteIntervalSeconds { get; private set; }

    private static string SpriteFolder => SessionPaths.Sprites;

    private CancellationTokenSource? _spriteCancellation;

    // Source file of the current sheet.
    private string? _spriteSource;

    // Set when GpuProgramStrip failed this session: the Overview Scrubber falls back to the last file's key frames.
    private bool _sourceThumbnails;

    /// <summary>
    /// Switches the Overview Scrubber to key-frame sheets of the last opened file (now and for later files) after GpuProgramStrip fails.
    /// </summary>
    public void UseSourceThumbnails()
    {
        _sourceThumbnails = true;
        if (OpenedFile is { Length: > 0 } path)
            _ = GenerateSpriteSheetAsync(path);
    }

    /// <summary>
    /// Builds the last opened file's key-frame hover sheet in the background, only when <see cref="GpuProgramStrip"/> is unavailable
    /// and the setting is on.
    /// </summary>
    private async Task GenerateSpriteSheetAsync(string videoPath)
    {
        _spriteCancellation?.Cancel();
        if (GpuProgramStrip.IsWanted && !_sourceThumbnails)
        {
            (SpriteSheetPath, _spriteSource) = (null, null);
            return;
        }

        // Same file: keep the current sheet until the new one is ready so the strip never blanks; another file: drop it now.
        if (!string.Equals(_spriteSource, videoPath, StringComparison.OrdinalIgnoreCase))
            (SpriteSheetPath, _spriteSource) = (null, null);

        var settings = AppSettings.Current;
        var duration = _mediaInfo?.DurationSeconds ?? 0;
        if (!(settings.GenerateHoverPreviews || ShowTimelineThumbnails) || duration <= 0 || _mediaInfo?.Video is null)
        {
            (SpriteSheetPath, _spriteSource) = (null, null);
            return;
        }

        var cancellation = _spriteCancellation = new CancellationTokenSource();

        // Fixed thumbnail count per sheet: spacing grows with duration to cover the whole file, while short files still
        // get enough thumbnails to fill the timeline strip cut from the same sheet.
        var capacity = FfmpegRunner.SpriteGridSize * FfmpegRunner.SpriteGridSize;
        var wanted = Math.Max(settings.ThumbnailIntervalSeconds, 1.0);
        if (ShowTimelineThumbnails)
            wanted = Math.Min(wanted, Math.Max(duration / FilmstripPictures, 0.2));
        var interval = Math.Max(wanted, duration / capacity);

        // Cache hit for this file version and spacing.
        var kind = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sprite{interval:0.###}");
        if (PreviewCache.Timeline.Find(videoPath, kind, ".jpg") is { } kept)
        {
            SpriteIntervalSeconds = interval;
            (SpriteSheetPath, _spriteSource) = (kept, videoPath);
            return;
        }

        var imagePath = Path.Combine(SpriteFolder, $"{Guid.NewGuid():N}.jpg");
        try
        {
            if (await FfmpegRunner.GenerateSpriteSheetAsync(videoPath, imagePath, interval, cancellation.Token)
                && !cancellation.IsCancellationRequested)
            {
                SpriteIntervalSeconds = interval;
                (SpriteSheetPath, _spriteSource) = (PreviewCache.Timeline.Keep(videoPath, kind, ".jpg", imagePath), videoPath);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Previews are optional; failures are ignored.
        }
        finally
        {
            if (ReferenceEquals(_spriteCancellation, cancellation))
                _spriteCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>Minimum thumbnail count for the timeline strip.</summary>
    private const int FilmstripPictures = 40;

    /// <summary>Whether a strip of pictures from the video is drawn along the timeline, behind the waveform.</summary>
    public bool ShowTimelineThumbnails => AppSettings.Current.ShowTimelineThumbnails == true;

    /// <summary>Whether a picture of the video follows the pointer along the timeline.</summary>
    public bool ShowHoverPreviews => AppSettings.Current.GenerateHoverPreviews;

    // ----- Filters -----

    [ObservableProperty] private string _lutPath = "";

    [ObservableProperty] private bool _fadeIn;
    [ObservableProperty] private bool _fadeOut;

    // ----- Audio -----

    /// <summary>Lowers the first track (game) while the second (microphone) is loud.</summary>
    [ObservableProperty] private bool _duckAudio;
}
