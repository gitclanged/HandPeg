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
    // The frame is whatever Resolution & Cropping (Video tab) describes, in any shape: the GPU compositor composes every
    // clip onto it, and the Arrange canvas draws it. Left blank, it is the Sequence's own size (5.2-AuditB: its natural size,
    // given as the first file's picture landed on it; no clip's crop changes it afterwards).

    // The Sequence's size while it has none of its own.
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

            // Blank boxes mean "the Sequence's size"; spelled out, so that there is something to turn over.
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
    // A crop is each clip's own (4.4): its look's CropLeft..CropBottom (fractions of its picture) reframe the picture it plays, on
    // every track (4.4-BugHunt, the user's Q2: one rule, Layer.HeldCrop; its box takes the shape the crop leaves). The Crop boxes
    // (CropLeft..CropBottom here, percentages) and Interactive Crop show and set the clip they stand for (CropTarget: the selected
    // picture, else the video at the playhead), a preset's crop every clip it goes on (TemplateTargets). Cropping a clip never
    // resizes the Sequence (5.2-AuditB): its natural size is its own.

    // Something of the picture always has to remain.
    private const double LargestCropTotal = Layer.LargestCropTotal;

    /// <summary>A crop as fractions (0 to 1) of a picture's width and height, held to what leaves a picture (the one rule: <see cref="Layer.HeldCrop(double, double, double, double)"/>).</summary>
    private static (double Left, double Top, double Right, double Bottom) HeldCrop(double left, double top, double right, double bottom) => Layer.HeldCrop(left, top, right, bottom);

    /// <summary>A clip's crop, held to what leaves a picture.</summary>
    private static (double Left, double Top, double Right, double Bottom) CropOf(Layer view) => HeldCrop(view.CropLeft, view.CropTop, view.CropRight, view.CropBottom);

    /// <summary>What the Crop boxes say, as fractions.</summary>
    private (double Left, double Top, double Right, double Bottom) BoxCrop() => HeldCrop(CropLeft / 100, CropTop / 100, CropRight / 100, CropBottom / 100);

    /// <summary>The crop of the clip the Crop boxes stand for (CropTarget), as fractions; the boxes' own while there is none.</summary>
    private (double Left, double Top, double Right, double Bottom) TargetCrop() => CropTarget is { } target ? CropOf(target) : BoxCrop();

    /// <summary>
    /// The size of the picture the Crop boxes and Interactive Crop cut, in its pixels: the file of the clip they stand for (the file
    /// last opened's while there is none); (0, 0) while neither is known.
    /// </summary>
    public (int Width, int Height) CropPictureSize => CropTarget is { ImageWidth: > 0, ImageHeight: > 0 } target ? (target.ImageWidth, target.ImageHeight) : (SourceWidth, SourceHeight);

    /// <summary>What the crop of the clip the Crop boxes stand for keeps of its picture, in its pixels: what Interactive Crop draws over the player.</summary>
    public (int Left, int Top, int Width, int Height) GetTargetCropRect()
    {
        var crop = TargetCrop();
        var (width, height) = CropPictureSize;
        var (keptWidth, keptHeight) = CroppedSize(crop, width, height);
        return ((int)(width * crop.Left), (int)(height * crop.Top), keptWidth, keptHeight);
    }

    /// <summary>What a crop leaves of a picture of a size, in its pixels (even sides); 1920 x 1080 for a picture not known yet.</summary>
    private static (int Width, int Height) CroppedSize((double Left, double Top, double Right, double Bottom) crop, int sourceWidth, int sourceHeight) =>
        sourceWidth > 0 && sourceHeight > 0
            ? (Math.Max((int)(sourceWidth * (1 - crop.Left - crop.Right) / 2) * 2, 2), Math.Max((int)(sourceHeight * (1 - crop.Top - crop.Bottom) / 2) * 2, 2))
            : (DefaultSourceWidth, DefaultSourceHeight);

    // Set while the Crop boxes show a clip's crop (ShowCropTarget), and while what they say is written into clips; and while a
    // preset's crop goes on every clip it goes on (ApplyPresetCrop).
    private bool _showingTarget, _takingCrop, _cropForAll;

    // Interactive Crop being dragged, and whether its drag has taken its one undo step; when a crop was last typed (a box
    // dragged or typed into sets it on every change: one undo step for changes within a second and a half).
    private bool _cropDragging, _cropDragStepTaken;
    private long _cropTypedAt;

    /// <summary>Interactive Crop's drag begins: its first change of the crop takes one undo step for the whole drag.</summary>
    public void BeginCropDrag() => (_cropDragging, _cropDragStepTaken) = (true, false);

    /// <summary>Interactive Crop's drag is over.</summary>
    public void EndCropDrag() => _cropDragging = false;

    /// <summary>
    /// The Crop boxes changed (typed, dragged on the video, a preset's): unless they are only showing a clip's crop, what they
    /// say is written into the clip they stand for (a preset's into every clip it goes on), whose box keeps its middle where it
    /// was (Layer: a locked box grows about its anchor); one undo step for a drag or a run of typing. The Sequence keeps its size.
    /// </summary>
    private void TakeCropBoxes()
    {
        // Not while a step is being put back, or its bindings settle (4.4-BugHunt): it brings every clip's crop with it, and
        // what the boxes are told then is no one's request.
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

    /// <summary>The undo step a change of the crop takes before it is made: the first of a drag, or of a run of typing.</summary>
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
        var again = now - _cropTypedAt < 1500;
        _cropTypedAt = now;
        if (!again && now - _checkpointedAt > 500)
            Checkpoint("crop the video");
    }

    /// <summary>Sets the crop from distances in pixels of the picture the Crop boxes cut (<see cref="CropPictureSize"/>), as drawn on it.</summary>
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
    /// A preset's crop, as percentages (left, top, right, bottom). Presets saved before the crop became relative hold
    /// pixels; those are converted using the size of the file last opened, or 1920 x 1080 when there is none.
    /// </summary>
    private (double Left, double Top, double Right, double Bottom) PresetCropPercent(EncodingPreset preset)
    {
        if (preset.CropPercent is { Length: 4 } percent)
            return (percent[0], percent[1], percent[2], percent[3]);

        double width = SourceWidth > 0 ? SourceWidth : DefaultSourceWidth, height = SourceHeight > 0 ? SourceHeight : DefaultSourceHeight;
        return (ToPercent(preset.CropLeft, width), ToPercent(preset.CropTop, height), ToPercent(preset.CropRight, width), ToPercent(preset.CropBottom, height));
    }

    /// <summary>
    /// Takes the crop from a preset: every clip it goes on is cropped so (4.4: a crop is each clip's own; 5.2-AuditB: the selected
    /// video clips, or every top-level one); with no clip yet, the Crop boxes keep it for the next file opened.
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

    /// <summary>The Sequence's natural size (its own: given as the first file's picture landed on it), or null while it has none.</summary>
    private (int Width, int Height)? GetSequenceSize() => Document.HasNaturalSize ? (Document.NaturalWidth, Document.NaturalHeight) : null;

    /// <summary>The shape sizes are held to: the Sequence's natural size (1920 x 1080 while it has none), on its side with Use Vertical Resolution.</summary>
    private (int Width, int Height) GetNaturalSize() => NaturalOf(GetSequenceSize());

    /// <summary>A natural size (1920 x 1080 for none), on its side with Use Vertical Resolution.</summary>
    private (int Width, int Height) NaturalOf((int Width, int Height)? size)
    {
        var (width, height) = size ?? (DefaultSourceWidth, DefaultSourceHeight);
        return UseVerticalResolution ? (height, width) : (width, height);
    }

    /// <summary>
    /// The frame Resolution &amp; Cropping makes of a Sequence of a natural size (null: 1920 x 1080): what a project of 5.1 laid its old
    /// boxes in, as it is converted (LegacyMainVideo.Migrate, ConvertLegacy).
    /// </summary>
    private (int Width, int Height) FrameSizeFor((int Width, int Height)? natural)
    {
        var shaped = NaturalOf(natural);
        return RequestedSizeFor(shaped) ?? (Even(shaped.Width), Even(shaped.Height));
    }

    /// <summary>
    /// The size Resolution & Cropping asks for, or null when it leaves the size to the Sequence. One box alone
    /// is completed from the natural shape.
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

    /// <summary>Width of the output frame the engine composes: as asked for under Resolution & Cropping, otherwise the Sequence's own.</summary>
    public int FrameWidth => GetRequestedSize()?.Width ?? Even(GetNaturalSize().Width);

    public int FrameHeight => GetRequestedSize()?.Height ?? Even(GetNaturalSize().Height);

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);

    // ----- The layers on the frame -----

    // The blur fill (5.2-AuditB: each video clip's own, Layer.BlurFill; these are its look, the sequence's one, BackgroundLayer):
    // how far the blur reaches in pixels of the frame (5.0-Release: 0, sharp, to 64, continuously), how often it is applied (1 to
    // 4), how much it is darkened, and how much it is enlarged about its middle (1 just covers the frame). Its own effects are
    // BackgroundLayer.Effects.
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

    /// <summary>Cutouts of videos (a minimap, an ammo counter), videos, images (a logo), words: every clip's view.</summary>
    public ObservableCollection<Layer> Layers { get; } = [];

    /// <summary>
    /// Adds a region of a video (5.2-AuditB: a Cutout of the video clip the Crop boxes stand for, a child of it on its clock,
    /// over its whole span): a quarter of its picture to begin with, in the middle of the frame. Where it is cut from is marked
    /// afterwards with Draw Target. One step of Undo.
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

        // The first Cutout in a layout ties the layout to the shape of this video.
        if (CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;
        Log($"Added {state.Name}, a region of {target.Name}: use Draw Target to mark it on the video");
    }

    /// <summary>
    /// Whether a clip is a Cutout (5.2-AuditB): a video clip of the same file as the clip it is a child of, on its clock (a Game
    /// Preset's HUD, a region, one HUD element of a mask).
    /// </summary>
    public bool IsCutout(Layer view) => view is { IsVideoFile: true, IsDeleted: false } && ClipOf(view) is { } clip && IsCutout(clip);

    /// <summary>Whether a clip is a Cutout: a video clip of the same file as the video clip it is a child of.</summary>
    private bool IsCutout(TimelineClip clip) =>
        clip is { ContentKind: LayerKind.VideoFile, ParentClipId: { } parentId }
        && Document.FindClip(parentId) is { ContentKind: LayerKind.VideoFile } parent && SameFile(clip.SourcePath, parent.SourcePath ?? "");

    /// <summary>
    /// Whether a clip is a Cutout, linked to its clip or freed from it (5.3, for the Timeline's effect lanes): a Cutout (<see cref="IsCutout(Layer)"/>),
    /// or one its block's Lock switch unlocked (SetClipLocked) or a trim left wholly outside its clip: a video clip with no parent clip,
    /// still on a Sub-Lane in another track's stack, a clip of which plays the same file.
    /// </summary>
    public bool IsCutoutOrFreed(Layer view) => view is { IsVideoFile: true, IsDeleted: false } && ClipOf(view) is { } clip && IsCutoutOrFreed(clip);

    /// <summary>Whether a clip is a Cutout, linked to its clip or freed from it on a Sub-Lane of a clip of the same file.</summary>
    private bool IsCutoutOrFreed(TimelineClip clip) =>
        IsCutout(clip)
        || (clip is { ContentKind: LayerKind.VideoFile, ParentClipId: null }
            && Document.TrackOf(clip) is { ParentTrackId: { } parentTrackId }
            && Document.Tracks.Find(t => t.Id == parentTrackId) is { } parentTrack
            && parentTrack.Clips.Any(c => c.ContentKind == LayerKind.VideoFile && SameFile(clip.SourcePath, c.SourcePath ?? "")));

    /// <summary>Whether a Cutout hangs from one of the clips named (the clips Cutouts are being put on, which lose their own first).</summary>
    private bool IsCutoutOf(Layer view, IReadOnlyCollection<Guid> clips) => ClipOf(view) is { ParentClipId: { } parent } && clips.Contains(parent);

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
    /// The row it was dropped on, which decides its track (<see cref="PlaceNew"/>). Wherever it goes it is sized to the frame,
    /// in its middle (4.4, <see cref="AppSettings.NewClipSizing"/>).
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

        // Sized to the frame (fitted, or filling it as Settings say), in its middle (4.4).
        FitToFrame(element, AppSettings.Current.NewClipsFill);

        var start = Math.Max(startSeconds, 0);
        if (!PlaceLayer(element, start, ToTheEnd(start), "add an image", onTrack,
                joins: onTrack is { IsImage: true } && AppSettings.Current.DroppedClipsJoinTrack))
            return false;

        Log($"Added image clip {element.Name}");
        return true;
    }

    /// <summary>
    /// Adds a track with nothing on it (the + Add Track button): a row of the timeline to drop clips onto. It draws
    /// nothing and is kept in the project and in undo; the first clip dropped on it takes its place.
    /// </summary>
    [RelayCommand]
    private void AddEmptyTrack()
    {
        if (Edit("add a track", edit => edit.InsertTrack(TopPictureIndex(), TimelineTrackKind.Video)))
            Log("Added an empty track: drop a video or a picture on it");
    }

    /// <summary>
    /// Whether a clip on the timeline can be dragged onto another track's row, to begin at a moment there: onto a track
    /// that takes its kind of clip (an empty one too), where that track has nothing for the clip's whole length. Any clip,
    /// a video of the file opened included; never onto the caption box or the blur fill's settings row.
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
    /// Moves a clip to another track (a drag from its row onto another), beginning at <paramref name="start"/>. It keeps its own
    /// look, place and timing (5.1-DogfoodPolish: a clip's look is its own), unless the track holds a template: it then takes
    /// that clip's, with Cutouts of its own (<see cref="InheritTemplate"/>); onto a track with a Game Preset of its own (5.2A, a
    /// Track Preset Node) it takes that preset, its Cutouts swapped for the preset's. Its old track goes when it was the only clip
    /// on it. One step of Undo.
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
        if (!Edit($"move {clip.Name} to another track", edit => edit.MoveClip(model, target, start, allowCrossfadeOverlap: false)))
            return false;

        StatusText = $"{clip.Name} moved to {name}, at {TimeSpan.FromSeconds(model.Start):h\\:mm\\:ss\\.ff}. Ctrl+Z puts it back.";
        InheritTemplate(clip, track, start, hadPreset);
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

        if (!PlaceLayer(element, 0, ToTheEnd(0), "add a text clip"))
            return;

        KeyLayer = element;
        Log($"Added text clip {element.Name}: type its words under Text Settings");
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

    /// <summary>Adds a video from a file as a clip: it plays over the clips under it.</summary>
    /// <param name="startSeconds">
    /// When on the timeline the layer appears. Placed at a moment, it plays once through from there, for as long
    /// as the file lasts (the timeline grows to hold it); left at the start, it runs to the end of the timeline,
    /// starting again from its first frame each time it runs out.
    /// </param>
    /// <param name="onTrack">
    /// The row of the timeline the file was dropped on, which decides its track (<see cref="PlaceNew"/>): an empty
    /// track is filled; a video track it joins takes it in its place and with its look, never its mask (a mask is one clip's
    /// own); a track with a template takes it too (the drop then gives it that template, <see cref="InheritTemplate"/>);
    /// otherwise it gets a track of its own.
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
        DevFlightRecorder.Record("media", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"clip added at {start:0.000}s: {path}"));
        Layer element;
        var row = TrackOfRow(onTrack);
        var joins = onTrack is { IsEmptyTrack: false, IsVideoFile: true } && row is not null
                    && (follows || AppSettings.Current.DroppedClipsJoinTrack || TemplateClipOn(row, start) is not null);
        if (joins && onTrack!.IsVideoFile)
        {
            // The track's look (place, size, filters) with nothing of the clip it was copied from, its mask included.
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
            // Until its shape is known, as wide as the frame and in its middle at 16:9 (fitted once it is, below).
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

        // Wherever it goes, sized to the frame (fitted, or filling it as Settings say) in its middle once its shape is known (4.4).
        (int X, int Y, int Width, int Height)? box = FrameWidth > 0 && FrameHeight > 0 ? (0, 0, FrameWidth, FrameHeight) : null;
        if (!await CompleteVideoLayerAsync(element, playOnce || start > 0 || onTrack is not null, box, fill: AppSettings.Current.NewClipsFill))
            return null;

        // A clip of the Clip Library starts and ends where it was saved (5.2C); two pictures side by side on one canvas are offered, never
        // done unasked.
        FitToLibraryEntry(element);
        OfferSideBySide(element);
        return element;
    }

    /// <summary>
    /// Sizes and places a layer to fit inside a box of the frame (in the frame's pixels), in its middle, at its own shape;
    /// or, <paramref name="fill"/>ing it, to cover the box (what reaches past it is off its edges). Nothing for a layer
    /// whose shape is not known yet.
    /// </summary>
    private void FitInto(Layer element, (int X, int Y, int Width, int Height) box, bool fill = false)
    {
        var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
        var tall = ShapeOf(element);
        if (tall <= 0 || box.Width <= 0 || box.Height <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return;

        // A picture's box takes the shape its crop leaves (4.4-BugHunt): that is what is fitted.
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

    /// <summary>
    /// A clip's own shape, its height for each unit of its width in pixels: its picture's (a file's, its words' box), or a
    /// Cutout's, its cut part's; 0 while it is not known.
    /// </summary>
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
    /// Sizes a layer to the frame and puts its middle on the frame's (4.4): fitted (the whole picture shows, touching the
    /// frame on one side) or filling it (the frame covered). Its aspect is locked: its own shape (as its crop leaves it) is
    /// what is fitted. Nothing for a layer whose shape is not known yet.
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
    /// <param name="fill">The layer covers <paramref name="fitInto"/> instead of fitting inside it.</param>
    private async Task<bool> CompleteVideoLayerAsync(Layer element, bool playOnce, (int X, int Y, int Width, int Height)? fitInto = null, bool keepLength = false, bool fill = false)
    {
        var path = element.ImagePath;
        var wanted = ToTheEnd(element.StartTime);
        var clipId = element.ClipId;
        MediaInfo? info = null;
        try
        {
            // A clip just dropped is what the user waits on (4.3E): its probe starts at once, never behind the media
            // browser's posters and strips in the Interactive lane, and is answered from memory when the browser has
            // already inspected the file. A clip brought back unfinished waits its turn.
            var token = _shutdown.Token;
            info = await Task.Run(() => MediaProbe.ProbeAsync(path, token, urgent: !keepLength), token);
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
        NoteHevcDecoder(video);
        using (DeferCommand())
        {
            (element.ImageWidth, element.ImageHeight) = (video.Width, video.Height);
            (element.HasAudio, element.MediaDuration) = (info.Audio.Count > 0, Math.Max(info.DurationSeconds, 0));
            DescribeSound(element, info);
            if (fitInto is { } box)
                FitInto(element, box, fill);

            // What the file is, which is not an edit: its length and its sound.
            (clip.SourceDuration, clip.AudioStreamIndex) = (Math.Max(info.DurationSeconds, 0), info.Audio.Count > 0 ? 0 : -1);
            // Placed at a moment, it plays once through (a file's length, however short); at the start, to the end, round and round.
            var file = Math.Max(info.DurationSeconds, 0);
            var end = keepLength ? clip.End : playOnce || file <= 0.05 ? clip.Start + (file > 0.05 ? file : PictureSeconds) : Math.Max(wanted, clip.Start + file);
            Edit("fill in a video clip", edit => LayOut(edit, clip, end), undoable: false);
            UnpackStreams(element, info, undoable: false);
            element.IsLoading = false;
            _previewState = null;
            GenerateCommand();
        }

        // Its sound, when it has any, is heard from now on: at the clip's place, for its length.
        RefreshLayerRows();
        LayerLoaded?.Invoke();
        _ = LoadLayerPicturesAsync(element);
        Log(element.HasAudio
            ? $"Added video clip {element.Name}, and its sound with it"
            : $"Added video clip {element.Name}");
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
        // Its block waits on this probe, so it is urgent (4.3E): it never queues behind the media browser's work.
        var info = await MediaProbe.ProbeAsync(path, _shutdown.Token, urgent: true);
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

    // The blur fill's settings row (5.0-Release; 5.2-AuditB: every video clip's own blur fill is drawn with its look). The
    // sequence's, not a clip of the document: its row stands for it, and it carries its own effect stack (Look effects only);
    // its blur and zoom are the view model's (BlurRadius, BlurPasses, BackgroundDim, BackgroundZoom).
    private readonly Layer _backgroundRow = new() { Kind = LayerKind.Background, Name = "Background Blur" };

    /// <summary>The pictures, bottom first: every clip's view on the picture tracks. Sounds have no place in it.</summary>
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

        StatusText = $"{layer.Name} moved {(by > 0 ? "up" : "down")}: it is now {(to == pictures.Count - 1 ? "the front track" : to == 0 ? "the back track" : $"track {to + 1} of {pictures.Count} from the back")}.";
    }

    // ----- Splitting: the razor -----

    /// <summary>
    /// Cuts a clip in two at a time: the first part ends there and a second clip, the same in everything
    /// else, begins there, on the same track. Each can then be moved in time, trimmed or deleted on its own.
    /// Any clip.
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

    // ----- HUD layers -----

    /// <summary>
    /// The file of a video clip at a moment of the timeline and the moment of it shown there (5.2-AuditB: the still the mask tools
    /// and the frame preview draw on), on the file's own clock as FFmpeg seeks it: the selected video when it is on then, else the
    /// lowest shown one on then, else the one the Crop boxes stand for (<see cref="CropTarget"/>); null while there is none to read.
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

    /// <summary>Whether a timeline drag is held (the window's): background work's result waits for it, as every other change to the project does.</summary>
    public Func<bool>? IsTimelineDragHeld { get; set; }

    /// <summary>
    /// The clips a set of Cutouts goes on (a mask's, the Cutout Studio's), with a status line said when there is none (5.2-AuditB:
    /// a Cutout is a clip's child, on its clock: <see cref="TemplateTargets"/>).
    /// </summary>
    private List<Guid> CutoutTargets(string what)
    {
        var targets = TemplateTargets().Select(v => v.ClipId).Where(id => id != Guid.Empty).ToList();
        if (targets.Count == 0)
            StatusText = $"{what} are cut from a video clip: open or drop a video first.";
        return targets;
    }

    /// <summary>
    /// Cutouts from a Mask: one Cutout per part of a mask of the whole frame, each cut out of the video where the part is and
    /// sharing the mask, laid back where it was or out for a tall frame; a set of them over each video clip they go on (the
    /// selected ones, or every top-level one), each a child of its clip on its clock, in front of it. One step of Undo.
    /// </summary>
    /// <param name="replace">Takes the Cutouts already cut from those clips away first (into the recycle bin).</param>
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
    /// Copy passes one file's picture through untouched (SequenceSpans.CopyCutOf: the file the Sequence shows most of, its clips
    /// joined end to end). When there are filters or a look set that it will therefore leave out, or clips it does not copy, this
    /// says so; otherwise it is empty.
    /// </summary>
    public string CopyBypassWarning
    {
        get
        {
            if (VideoEncoder.Family != EncoderFamily.Copy || IsAnimatedOutput)
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
            // (The two that cannot be made are refused as an export starts, 5.2-Arm64AndNpu: EncoderCommand.WhyCopyCannotCut.)
            var cut = copy is null ? " No clip on the timeline shows a file's picture: Copy has nothing to write, so its export is refused."
                : copy.Pieces.Count == 0 ? $" The clips of {file} play at another speed: Copy cannot cut them without encoding, so its export is refused."
                : copy.IsCut ? $" The clips of {file} are joined end to end, each from the I-frame at or before its start (gaps and moves are closed up)."
                : "";
            if (copy is { LeftOut.Count: > 0 } left)
                cut += $" Copy writes one file's picture: {string.Join(", ", left.LeftOut)} {(left.LeftOut.Count == 1 ? "is" : "are")} left out.";
            return skipped ? "Stream Copy selected: effects and filters will be bypassed." + cut : cut.TrimStart();
        }
    }

    /// <summary>Takes a clip off the timeline (no recycle bin: the layer tools' Remove), its time left empty. One step of Undo.</summary>
    [RelayCommand]
    private void RemoveLayer(Layer? element)
    {
        if (ClipOf(element) is { } clip)
            Edit($"remove {element!.Name}", edit => edit.DeleteClip(clip));
    }

    /// <summary>
    /// Gives every other Cutout of the same file's picture the mask this one has (its picture, its drawn outlines, whether it is
    /// turned inside out, how far it is grown). Each then takes its own part of it and stays a clip of its own; their crops and
    /// outlines are their own and are left as they are.
    /// </summary>
    /// <returns>How many Cutouts were given it.</returns>
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

    /// <summary>The clips on the same track as a row's clip, in time; the row alone when it stands for no track's clips (a stand-in, an empty track).</summary>
    public List<Layer> GetTrackClips(Layer layer) =>
        TrackOfRow(layer) is { Clips.Count: > 0 } track ? track.Clips.Select(ViewOf).OfType<Layer>().ToList() : [layer];

    public Layer BackgroundLayer => _backgroundRow;

    private void OnLayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Layer source)
            return;

        // A clip's timing and switches are the document's: one changed here (a box bound to it) is made an edit of the
        // clip, and anything else is its look, newer now than the clip's (MainViewModel.Document.cs).
        if (NoteViewChange(source, e.PropertyName) || _syncingViews)
            return;

        // Its crop set elsewhere (the layer tools): the Crop boxes show it when they stand for this clip (4.4).
        if (!_takingCrop && !_placingViews && e.PropertyName is nameof(Layer.CropLeft) or nameof(Layer.CropTop) or nameof(Layer.CropRight) or nameof(Layer.CropBottom)
            && ReferenceEquals(source, CropTarget))
        {
            _cropShown = null;
            ShowCropTarget();
        }

        // Releasing the aspect lock: the layer keeps the shape it has until it is stretched.
        if (e.PropertyName == nameof(Layer.LockAspectRatio) && source is { LockAspectRatio: false })
            source.StartFreeHeight(FrameWidth, FrameHeight, SourceWidth, SourceHeight);

        // Marked again on the video that is loaded: the layout now fits this shape.
        if (e.PropertyName is nameof(Layer.SourceWidth) or nameof(Layer.SourceHeight) && CurrentSourceAspect > 0)
            _layoutSourceAspect = CurrentSourceAspect;

        // Not a change of the user's. (A clip's look is its own, 5.1-DogfoodPolish: nothing is copied along its track.)
        if (_settingQuietly || _applyingKeys)
            return;

        if (e.PropertyName == Layer.KeysChanged)
            RefreshAnyKeys();

        // Moved, sized or turned by hand: what that does to its keyframes. (Not a place written into it as the frame changed shape.)
        if (!_placingViews)
            RecordKey(source, e.PropertyName);
        TimelineChanged?.Invoke();

        // A picture of the clip arriving is something to draw, and nothing to do with the command.
        if (e.PropertyName is nameof(Layer.Waveform) or nameof(Layer.Filmstrip))
            return;

        // Different words, or a different font: its box is measured again.
        if (source.IsText && Layer.IsTextShapeProperty(e.PropertyName))
            LayoutText(source);

        GenerateCommand();
    }

    // Set while a layer is written to by what follows from a change (a text box measured, a placement given), not by the user.
    private bool _settingQuietly;

    // ----- Timeline I-frame lines -----

    /// <summary>Whether the I-frame lines are drawn on the timeline. Remembered between sessions.</summary>
    [ObservableProperty] private bool _showIFrames = AppSettings.Current.ShowIFrames;

    // Set while ShowIFrames follows a save of the Settings window (OnSettingsSaved, 5.3): the saved settings already hold the lines and
    // the timeline thumb's snap the user left there, so switching the lines writes nothing and leaves that snap alone.
    private bool _resyncingShowIFrames;

    partial void OnShowIFramesChanged(bool value)
    {
        // The change is still raised (the Scrubber and the Timeline Settings menu show it); only what a switch by hand does is skipped.
        if (_resyncingShowIFrames)
            return;

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

    /// <summary>
    /// The step a hardware encoder starts at: its slowest and best (5.1-DogfoodPolish, the user's directive: NVENC's p7, AMF's quality, and
    /// QSV's veryslow, which is its target usage 1); null for an encoder that has no such default.
    /// </summary>
    public static string? BestStepOf(EncoderFamily family) => family switch
    {
        EncoderFamily.Qsv => "veryslow",
        EncoderFamily.Amf => "quality",
        EncoderFamily.Nvenc => "p7",
        _ => null,
    };

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

            // Another kind of encoder: a hardware one starts at its best step, whatever the last one's was (QSV has a "medium" too).
            // Software keeps its choice when it has it, else the middle step.
            EncoderPreset = BestStepOf(VideoEncoder.Family) is { } best ? best
                : presets.Contains(previous) ? previous
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

        // One stretch of the timeline: from the playhead (from earlier, when the timeline ends before a whole preview; from
        // the start, when the playhead is at the end).
        var length = Math.Max(SequenceSeconds, 0);
        var start = PositionMs / 1000 >= length - 0.05 ? 0 : Math.Clamp(Math.Min(PositionMs / 1000, length - seconds), 0, length);
        List<(double Start, double End)> ranges = [(start, start + seconds)];

        Directory.CreateDirectory(PreviewFolder);
        var path = Path.Combine(PreviewFolder, $"preview_{Guid.NewGuid():N}.mp4");

        // Captions for just the stretch being previewed, timed from its start (the compositor moves them to it).
        string? captionsPath = null;
        if (AutoCaptions)
        {
            captionsPath = Path.Combine(PreviewFolder, "captions_preview.ass");
            if (await PrepareCaptionsAsync(ranges, captionsPath, 0, cancellationToken) is { } problem)
                return problem;
        }

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

        // On the GPU compositor, as the export is (the user's call, 5.0-Verify Q5; the only renderer since 5.2-Audit): the same
        // frames, the sound engine's sound.
        await GpuExport.RunPreviewAsync(CompileExportState(), path, ranges[0].Start, ranges[0].End, settings.PreviewResolutionPercent, captionsPath, progress, cancellationToken);
        CrashReporter.Note("preview", "rendered on the GPU compositor");
        PreviewRendered?.Invoke(path);
        return $"Preview rendered: {seconds} s from {TimeDisplay.Format(start)} at {settings.PreviewResolutionPercent}% size.";
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

    // The file the sheet up now was made of.
    private string? _spriteSource;

    // The GPU engine could not draw the sequence's own Sequence Overview Scrubber pictures this session: the key frames of the file last opened stand in.
    private bool _sourceThumbnails;

    /// <summary>
    /// The Sequence Overview Scrubber's pictures cannot be the sequence's (GpuProgramStrip gave up): the key frames of the file last opened are made
    /// instead, now and for every file opened after.
    /// </summary>
    public void UseSourceThumbnails()
    {
        _sourceThumbnails = true;
        if (OpenedFile is { Length: > 0 } path)
            _ = GenerateSpriteSheetAsync(path);
    }

    /// <summary>
    /// Builds the hover-preview sheet of the key frames of the file last opened in the background: only where the GPU engine does not
    /// draw the sequence's own (<see cref="GpuProgramStrip"/>), and not while the setting is off.
    /// </summary>
    private async Task GenerateSpriteSheetAsync(string videoPath)
    {
        _spriteCancellation?.Cancel();
        if (GpuProgramStrip.IsWanted && !_sourceThumbnails)
        {
            (SpriteSheetPath, _spriteSource) = (null, null);
            return;
        }

        // The sheet up stays until the new one is ready when it is of the same file (made again at another spacing, or the
        // file opened again): the strip along the timeline never blinks out. A sheet of another file goes at once.
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
        OnPropertyChanged(nameof(ShowAdvancedPlayback));
        OnPropertyChanged(nameof(ShowSimplePlayback));
        OnPropertyChanged(nameof(ShowCommandPreviewTab));
        OnPropertyChanged(nameof(ShowAdvancedFiltersTab));
        OnPropertyChanged(nameof(IsMediaBrowserVisible));
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
    /// Finds the silent stretches of a sound and cuts them out of what plays it: the selected clip with sound of its own (it alone
    /// is cut up, its time left empty, as in 4.2.0); or else (5.2-AuditB: no clip is privileged) the sound marked as the
    /// voice, else the first stream of the video given or at the playhead (<see cref="ListenedSound"/>), and every clip of that
    /// sound's file is cut where the silences it plays are (heard in the file's own time, each clip placing them where it shows
    /// that time). Remove (and the classic way) takes them out and closes up what follows on every unlocked track, as a cut
    /// list's left-out stretches did; Split &amp; Mark folds them out the same way and keeps each at its cut, to be put back
    /// (4.3D). What counts as silence (how quiet, for how long) is in the settings, set from the Remove Dead Air dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task RemoveDeadAirAsync() => RunOperationAsync("Removing dead air", WorkLane.Interactive, async cancellationToken =>
    {
        // How, on what and by what: set by RunDeadAir; left alone, it is the classic way on the listened sound, by its silences.
        var (mode, target, by) = (_deadAirMode, _deadAirTarget, _deadAirSource);
        (_deadAirMode, _deadAirTarget, _deadAirSource) = (DeadAirMode.Classic, null, DeadAirSource.Audio);

        if (!HasSource)
            return "Load a video first.";

        IsProgressIndeterminate = true;
        var mark = mode == DeadAirMode.SplitAndMark;
        if (by == DeadAirSource.Captions)
            return await RemoveCaptionGapsAsync(target, mark, cancellationToken);

        StatusText = "Listening for silence...";
        var noiseDb = Math.Clamp(AppSettings.Current.DeadAirThresholdDb, -80, -10);
        var minimumSeconds = Math.Clamp(AppSettings.Current.DeadAirMinSeconds, 0.2, 30);

        // A clip with sound of its own: its own sound is listened to, and the clip is what gets cut up (its time left empty, as in 4.2.0).
        if (target is Layer { CarriesSound: true } layer && ClipOf(layer) is { } model)
        {
            var info = await MediaProbe.ProbeAsync(layer.ImagePath, cancellationToken);
            if (info is not { DurationSeconds: > 0, Audio.Count: > 0 })
                return $"{layer.Name} has no sound to listen to.";

            if (await Waveforms.PeaksOfAsync(layer.ImagePath, Math.Max(model.AudioStreamIndex, 0), cancellationToken) is not { } own)
                return $"The sound of {layer.Name} could not be read.";

            return ApplyDeadAir([layer], own.Silences(noiseDb, minimumSeconds, info.DurationSeconds), mark, closeUp: false);
        }

        // The sound marked as the voice (4.3E: a recording's first stream is usually the game, never silent), else the first stream
        // of the video asked for or at the playhead: every clip of its file is cut where the silences it plays are.
        if (ListenedSound(target) is not var (path, stream))
            return "There is no sound to listen to: select a clip with sound, or put a video on the timeline.";

        var heard = await MediaProbe.ProbeAsync(path, cancellationToken);
        if (heard is not { Audio.Count: > 0 })
            return $"{System.IO.Path.GetFileName(path)} has no audio to analyse.";
        if (await Waveforms.PeaksOfAsync(path, stream, cancellationToken) is not { } peaks)
            return $"The sound of {System.IO.Path.GetFileName(path)} could not be read.";

        var silences = peaks.Silences(noiseDb, minimumSeconds, Math.Max(heard.DurationSeconds, 0)).Where(s => s.End - s.Start > 0.05).ToList();
        if (silences.Count == 0)
            return string.Create(CultureInfo.InvariantCulture, $"Nothing quieter than {noiseDb:0} dB for {minimumSeconds:0.#} s was found: {System.IO.Path.GetFileName(path)} was left as it is.");

        var views = ClipsOfFile(path);
        return views.Count == 0 ? $"{System.IO.Path.GetFileName(path)} is not on the timeline." : ApplyDeadAir(views, silences, mark, closeUp: true);
    });

    /// <summary>
    /// The sound Remove Dead Air and Auto-Captions listen to when no clip with sound of its own is chosen (5.2-AuditB: where the
    /// main video's Voice track was): a sound clip marked as the voice, else the first audio stream of the video given (a row, a
    /// clip whose streams are on Sub-Lanes), else of the video clip the Crop boxes stand for (the selected one, else the one at
    /// the playhead). Its file and stream; null while there is none.
    /// </summary>
    private (string Path, int Stream)? ListenedSound(object? given = null)
    {
        var voice = Layers.FirstOrDefault(l => l is { IsVoice: true, CarriesSound: true, IsHidden: false } && ClipOf(l) is not null);
        if (voice is not null)
            return (voice.ImagePath.Trim().Trim('"'), Math.Max(ClipOf(voice)!.AudioStreamIndex, 0));

        if ((given is Layer { IsVideoFile: true } chosen ? chosen : CropTarget) is not { IsVideoFile: true } video || ClipOf(video) is not { SourcePath: { Length: > 0 } source } clip)
            return null;

        var path = source.Trim().Trim('"');
        if (clip.AudioStreamIndex >= 0)
            return (path, clip.AudioStreamIndex);

        // Its streams on Sub-Lanes: the first of them.
        var lanes = Document.ChildrenOf(clip).Where(c => c.Clip is { ContentKind: LayerKind.Audio, AudioStreamIndex: >= 0 }).Select(c => c.Clip.AudioStreamIndex).ToList();
        return lanes.Count > 0 ? (path, lanes.Min()) : MediaProbe.KnownInfo(path) is { Audio.Count: > 0 } info ? (path, info.Audio[0].Index) : (path, 0);
    }

    /// <summary>What Remove Dead Air cuts for what is given, in a few words (its dialog's title): a clip with sound of its own, or the clips of the listened sound's file.</summary>
    public string DeadAirTargetName(object? target) => target is Layer { CarriesSound: true } layer ? layer.Name
        : ListenedSound(target) is var (path, _) ? $"the clips of {Path.GetFileName(path)}"
        : "the video";

    /// <summary>The views of the top-level clips that play a file (its pictures, or its sound on a track of its own): what its dead air is cut out of.</summary>
    private List<Layer> ClipsOfFile(string path) =>
        Document.Tracks.SelectMany(t => t.Clips).Where(c => c is { ParentClipId: null, ContentKind: LayerKind.VideoFile or LayerKind.Audio } && SameFile(c.SourcePath, path))
            .Select(ViewOf).OfType<Layer>().ToList();

    /// <summary>
    /// Remove Dead Air by the captions (4.4): the gaps between the words heard in a sound, in its own time, at least the
    /// minimum gap long less a little kept before and after each word, cut out of what plays it exactly as silences are
    /// (ApplyDeadAir). The words are the anchored captions' when they were made from that sound; otherwise it is
    /// transcribed (once: the words are kept) without putting any captions on the timeline.
    /// </summary>
    private async Task<string?> RemoveCaptionGapsAsync(object? target, bool mark, CancellationToken cancellationToken)
    {
        // A clip with sound of its own is cut itself; otherwise every clip of the listened sound's file, by the source asked for
        // or the one Auto-Captions listen to when it is a clip's.
        IReadOnlyList<Layer> views;
        bool closeUp;
        CaptionSource source;
        if (target is Layer { CarriesSound: true } layer && ClipOf(layer) is { } clip)
        {
            views = [layer];
            closeUp = false;
            source = new CaptionSource(CaptionSourceKind.Clip, layer.ImagePath, Math.Max(clip.AudioStreamIndex, 0));
        }
        else
        {
            var listened = ListenedSound(target);
            if (target is not Layer && ResolveCaptionSource() is { Kind: CaptionSourceKind.Clip, Path.Length: > 0 } resolved)
                listened = (resolved.Path, resolved.Stream);
            if (listened is not var (path, stream))
                return "There is no sound to listen to: select a clip with sound, or put a video on the timeline.";

            views = ClipsOfFile(path);
            closeUp = true;
            if (views.Count == 0)
                return $"{System.IO.Path.GetFileName(path)} is not on the timeline.";
            source = new CaptionSource(CaptionSourceKind.Clip, path, Math.Max(stream, 0));
        }

        var words = AnchoredCaptionWords(source);
        if (words is null)
        {
            StatusText = "Listening for the words...";
            (words, var problem) = await GetCaptionWordsAsync(source, cancellationToken);
            if (words is null)
                return problem;
        }

        var settings = AppSettings.Current;
        var minimumGap = Math.Clamp(settings.DeadAirCaptionMinGapSeconds, 0.1, 3);
        var gaps = CaptionProjection.WordGaps(words, minimumGap, Math.Clamp(settings.DeadAirCaptionPadBeforeSeconds, 0, 0.5),
            Math.Clamp(settings.DeadAirCaptionPadAfterSeconds, 0, 0.5), CaptionSourceSeconds(source));
        if (gaps.Count == 0)
        {
            return words.Count == 0 ? "No words were heard, so there are no gaps between them to cut."
                : string.Create(CultureInfo.InvariantCulture, $"No gap of {minimumGap:0.##} s or more was found between the words: nothing was cut.");
        }

        // The gaps between words are many by nature: a clip may take far more of them than of silences.
        return ApplyDeadAir(views, gaps, mark, closeUp, layerLimit: 2000);
    }

    // ----- Chapters at cut points -----

    /// <summary>FFmpeg metadata file naming the chapters: one at each edge of a clip on a video track (Chapters at Cut Points), and the markers.</summary>
    public string ChaptersFilePath => Path.Combine(_workFolder, "chapters.txt");

    /// <param name="throughCuts">The export reads the cuts list (a Copy export of one file's clips end to end): its clock is the list's.</param>
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
