using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>What a layer shows.</summary>
public enum LayerKind
{
    /// <summary>A rectangle cut out of the source video (a minimap, an ammo counter).</summary>
    Video,

    /// <summary>A picture from a file (a logo, a frame, a caption).</summary>
    Image,

    /// <summary>The box the auto-captions are drawn in. There is one, and it is always the top layer.</summary>
    Captions,

    /// <summary>A video from a file, played alongside the main one and repeated when it is shorter.</summary>
    VideoFile,

    /// <summary>The main video itself, as a place in the stacking order. There is one; it is placed with the Center controls.</summary>
    MainVideo,

    /// <summary>The blurred copy of the video that fills the frame behind everything. There is one, always at the bottom.</summary>
    Background,

    /// <summary>A sound from a file, with no picture: it has a place in time and nothing else.</summary>
    Audio,
}

/// <summary>
/// Something the Frame &amp; Layer Engine places on the output frame: a rectangle of the source video, or an
/// image file. Both kinds are positioned, sized and styled in exactly the same way.
///
/// Everything is stored as a fraction, 0 to 1: the source rectangle relative to the source frame, and the
/// position and size relative to the output frame. A layout made for one resolution therefore fits any
/// other: switching the frame from 1080 to 720 wide moves and sizes every layer with it.
/// </summary>
public sealed partial class Layer : ObservableObject
{
    [ObservableProperty] private string _name = "Layer";

    public LayerKind Kind { get; init; }

    /// <summary>The picture of an image layer.</summary>
    public string ImagePath { get; init; } = "";

    // The picture's own size in pixels, which gives a locked image layer its shape.
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    public bool IsImage => Kind == LayerKind.Image;

    public bool IsVideoFile => Kind == LayerKind.VideoFile;

    /// <summary>A picture or a video that comes from a file of its own, not from the main video.</summary>
    public bool IsFile => IsImage || IsVideoFile;

    public bool IsCaptions => Kind == LayerKind.Captions;

    public bool IsMainVideo => Kind == LayerKind.MainVideo;

    public bool IsBackground => Kind == LayerKind.Background;

    /// <summary>A sound with no picture.</summary>
    public bool IsAudio => Kind == LayerKind.Audio;

    /// <summary>Has sound of its own that goes into the output: a video from a file that has any, or a sound file.</summary>
    public bool CarriesSound => HasAudio && (IsVideoFile || IsAudio);

    /// <summary>Something added by hand, which can be removed again: the captions, the main video and the background cannot.</summary>
    public bool IsRemovable => Kind is LayerKind.Video or LayerKind.Image or LayerKind.VideoFile or LayerKind.Audio;

    /// <summary>A picture on the frame: everything but the background, which is the frame, and a sound.</summary>
    public bool IsPicture => !IsBackground && !IsAudio;

    /// <summary>Placed with the sliders of its row: its top-left corner and its width. The main video is placed about its middle instead.</summary>
    public bool HasPlacement => IsPicture && !IsMainVideo;

    /// <summary>Has a look of its own: opacity, corners, soft edges, a shadow.</summary>
    public bool HasStyle => IsPicture;

    /// <summary>Can be moved up and down the stack: the captions stay on top and the background at the bottom.</summary>
    public bool CanReorder => !IsCaptions && !IsBackground;

    /// <summary>Can have a color keyed out of it: the main video, and the pictures and videos added by hand.</summary>
    public bool HasKeying => IsPicture && !IsCaptions;

    /// <summary>Can have a mask of its own laid on it: those, and the caption box.</summary>
    public bool HasMask => IsPicture;

    /// <summary>Can be given filters of its own, and be turned: every picture but the caption box.</summary>
    public bool HasFilters => IsPicture && !IsCaptions;

    // Tracks: a row of the timeline. A layer is one clip on its track; splitting it makes a second clip on the
    // same track. What a track looks like (where it sits, its style, its filters) is the same for all its clips.

    /// <summary>Which track the clip is on. Clips with the same number share a row.</summary>
    [ObservableProperty] private int _trackId;

    /// <summary>What belongs to one clip alone, and is not copied to the others on its track when it changes.</summary>
    public static bool IsClipProperty(string? name) => name is nameof(StartTime) or nameof(Duration) or nameof(MediaOffset) or nameof(IsHidden)
        or nameof(AudioOffset) or nameof(Name) or nameof(TrackId) or nameof(PositionXPercent) or nameof(PositionYPercent) or nameof(SizePercent)
        or nameof(SizeHeightPercent) or nameof(IsFreeHeight) or nameof(HasFilterChanges);

    // When the layer is on screen, in seconds of the source's own time.

    /// <summary>When the layer appears.</summary>
    [ObservableProperty] private double _startTime;

    /// <summary>How long it stays; 0 for until the end of the video.</summary>
    [ObservableProperty] private double _duration;

    /// <summary>For a video layer: how far into its own video it is when it appears. Set when a layer is split, so the second part carries on where the first left off.</summary>
    [ObservableProperty] private double _mediaOffset;

    /// <summary>Kept in the list but left out of the picture, and (for a video layer) out of the sound.</summary>
    [ObservableProperty] private bool _isHidden;

    /// <summary>A video layer whose file has sound, which goes into the output with it.</summary>
    [ObservableProperty] private bool _hasAudio;

    /// <summary>How far that sound has been slipped against the layer's picture, in seconds. Only while audio and video are unlinked.</summary>
    [ObservableProperty] private double _audioOffset;

    /// <summary>A picture of the layer's sound, drawn behind its block on the Layers tab. Not saved: it is made again from the file.</summary>
    [ObservableProperty] private System.Windows.Media.ImageSource? _waveform;

    /// <summary>A strip of frames from a video layer's file, side by side, drawn in its block. Not saved.</summary>
    [ObservableProperty] private System.Windows.Media.ImageSource? _filmstrip;

    /// <summary>How long the layer's own video or sound is, in seconds; 0 when not known.</summary>
    [ObservableProperty] private double _mediaDuration;

    /// <summary>How much louder or quieter the layer's sound is made, in decibels.</summary>
    [ObservableProperty] private double _audioGainDb;

    // Turning and mirroring.

    /// <summary>Degrees the picture is turned clockwise about its middle.</summary>
    [ObservableProperty] private double _rotation;

    [ObservableProperty] private bool _flipHorizontal;
    [ObservableProperty] private bool _flipVertical;

    // Filters of the layer's own, applied to its picture before it is laid on the frame.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private double _filterContrast = 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private double _filterBrightness;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private double _filterSaturation = 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private double _filterGamma = 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private double _filterHue;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private double _filterSharpen;

    /// <summary>How far the picture is blurred, in pixels of a 1080-wide frame; 0 for not at all.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private double _filterBlur;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private bool _filterDenoise;

    /// <summary>A .cube file whose look is given to this layer alone.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFilterChanges))] private string _filterLut = "";

    /// <summary>Whether any filter of the layer's own is set to something.</summary>
    public bool HasFilterChanges => Math.Abs(FilterContrast - 1) >= 0.005 || Math.Abs(FilterBrightness) >= 0.005 || Math.Abs(FilterSaturation - 1) >= 0.005
        || Math.Abs(FilterGamma - 1) >= 0.005 || Math.Abs(FilterHue) >= 0.05 || FilterSharpen >= 0.005 || FilterBlur >= 0.05 || FilterDenoise || FilterLut.Trim().Length > 0;

    public void ResetFilters() =>
        (FilterContrast, FilterBrightness, FilterSaturation, FilterGamma, FilterHue, FilterSharpen, FilterBlur, FilterDenoise, FilterLut) = (1, 0, 1, 1, 0, 0, 0, false, "");

    public void ResetTransform() => (Rotation, FlipHorizontal, FlipVertical) = (0, false, false);

    // Growing about the middle. A layer is kept by its top-left corner, but when its size is changed (by its
    // slider, its number box, a dialog) it is its middle that stays where it is: the corner is moved to suit.

    /// <summary>Switched on once the layer is in use; while it is being built, sizes and corners are simply taken as given.</summary>
    public bool AnchorCenter { get; set; }

    // The layer's height for each unit of its width, both as fractions of the frame; learnt whenever it is laid out.
    private double _heightPerWidth;

    partial void OnSizeWidthChanged(double oldValue, double newValue)
    {
        if (!AnchorCenter)
            return;

        PositionX -= (newValue - oldValue) / 2;
        if (LockAspectRatio || SizeHeight <= 0)
            PositionY -= (newValue - oldValue) * _heightPerWidth / 2;
    }

    partial void OnSizeHeightChanged(double oldValue, double newValue)
    {
        if (AnchorCenter && !LockAspectRatio && oldValue > 0)
            PositionY -= (newValue - oldValue) / 2;
    }

    /// <summary>Changes the layer with its corner staying where it is: for a resize dragged by the opposite corner.</summary>
    public void FromCorner(Action change)
    {
        var anchored = AnchorCenter;
        AnchorCenter = false;
        try
        {
            change();
        }
        finally
        {
            AnchorCenter = anchored;
        }
    }

    /// <summary>Can be given a time to appear and a time to go: the layers added by hand.</summary>
    public bool HasTiming => IsRemovable;

    /// <summary>The seconds of the timeline the clip covers, on a video of the given length.</summary>
    public (double Start, double End) GetSpan(double totalSeconds) => (StartTime, Duration > 0.001 ? StartTime + Duration : Math.Max(totalSeconds, StartTime));

    // Chroma key: one color of the layer (a green or blue screen) made transparent.
    [ObservableProperty] private bool _chromaKey;

    /// <summary>The color to remove, as #RRGGBB.</summary>
    [ObservableProperty] private string _chromaColor = "#00FF00";

    /// <summary>How close to that color a pixel has to be to go: 0.01 only the color itself, 1 everything.</summary>
    [ObservableProperty] private double _chromaSimilarity = 0.15;

    /// <summary>How gradually pixels near the limit fade out: 0 a hard edge.</summary>
    [ObservableProperty] private double _chromaBlend = 0.05;

    // Custom mask: a black-and-white picture, stretched over the layer; the layer shows where it is white.
    [ObservableProperty] private bool _customMask;
    [ObservableProperty] private string _maskPath = "";

    /// <summary>Only a piece of the video has a place in the source to be marked.</summary>
    public bool IsVideo => Kind == LayerKind.Video;

    // Where the layer is in the source, as fractions of the source frame.
    [ObservableProperty] private double _sourceX;
    [ObservableProperty] private double _sourceY;
    [ObservableProperty] private double _sourceWidth = 0.25;
    [ObservableProperty] private double _sourceHeight = 0.25;

    // Where its top-left corner goes, as fractions of the output frame.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionXPercent))]
    private double _positionX = 0.28;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionYPercent))]
    private double _positionY = 0.03;

    // A layer may lie partly or wholly outside the frame: its corner can be anywhere from a frame to the left of it to a frame to the right.
    public const double SmallestPosition = -200, LargestPosition = 200;

    /// <summary>The position as percentages of the frame, for the number boxes beside the X and Y sliders.</summary>
    public double PositionXPercent
    {
        get => Math.Round(PositionX * 100, 1);
        set => PositionX = Math.Clamp(value, SmallestPosition, LargestPosition) / 100;
    }

    public double PositionYPercent
    {
        get => Math.Round(PositionY * 100, 1);
        set => PositionY = Math.Clamp(value, SmallestPosition, LargestPosition) / 100;
    }

    /// <summary>Width as a fraction of the output frame's width.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizePercent))]
    private double _sizeWidth = 0.44;

    /// <summary>Height as a fraction of the output frame's height. Only used while the aspect ratio is unlocked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeHeightPercent))]
    private double _sizeHeight;

    /// <summary>While locked, the layer keeps the shape of its source rectangle; unlocked, it can be stretched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeHeight))]
    private bool _lockAspectRatio = true;

    /// <summary>The height is set on its own: an unlocked layer, and always the caption box.</summary>
    public bool IsFreeHeight => !LockAspectRatio;

    /// <summary>The height as a whole percentage of the frame height, for typing.</summary>
    public int SizeHeightPercent
    {
        get => (int)Math.Round(SizeHeight * 100);
        set => SizeHeight = Math.Clamp(value, 1, 400) / 100.0;
    }

    // Style: rounded corners, soft edges, transparency and a drop shadow, so the layer sits on the
    // picture instead of being pasted on it.

    /// <summary>Corner rounding as a percentage of the layer's shorter side: 0 is square, 50 a full half-circle.</summary>
    [ObservableProperty] private int _cornerRadius;

    /// <summary>100 is solid; lower lets the picture underneath show through.</summary>
    [ObservableProperty] private int _opacity = 100;

    [ObservableProperty] private bool _feather;
    [ObservableProperty] private int _featherRadius = 12;
    [ObservableProperty] private bool _shadow;
    [ObservableProperty] private double _shadowOpacity = 0.5;
    [ObservableProperty] private int _shadowOffset = 10;

    /// <summary>The width as a whole percentage of the frame width, for typing.</summary>
    public int SizePercent
    {
        get => (int)Math.Round(SizeWidth * 100);
        set => SizeWidth = Math.Clamp(value, 1, 400) / 100.0;
    }

    public bool IsUsable => !IsHidden && !IsAudio && SizeWidth > 0 && (IsFile ? ImagePath.Length > 0 : SourceWidth > 0 && SourceHeight > 0);

    /// <summary>The source rectangle in pixels of a source of the given size.</summary>
    public (int X, int Y, int Width, int Height) GetSourceRect(int sourceWidth, int sourceHeight) =>
        ((int)Math.Round(SourceX * sourceWidth), (int)Math.Round(SourceY * sourceHeight),
            Math.Max(Even(SourceWidth * sourceWidth), 2), Math.Max(Even(SourceHeight * sourceHeight), 2));

    /// <summary>
    /// Where and how large the layer is on an output frame of the given size, in pixels. The source
    /// size is needed for the shape of a locked layer; when it is unknown, 16:9 is assumed.
    /// </summary>
    public (int X, int Y, int Width, int Height) GetOutputRect(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight)
    {
        var width = Math.Max(Even(SizeWidth * frameWidth), 2);

        var height = LockAspectRatio || SizeHeight <= 0
            ? GetProportionalHeight(width, sourceWidth, sourceHeight)
            : Math.Max(Even(SizeHeight * frameHeight), 2);
        if (frameWidth > 0 && frameHeight > 0)
            _heightPerWidth = ((double)height / frameHeight) / ((double)width / frameWidth);

        return ((int)Math.Round(PositionX * frameWidth), (int)Math.Round(PositionY * frameHeight), width, height);
    }

    /// <summary>The height that keeps the layer's own shape at a given width: the source rectangle's, or the picture's.</summary>
    private int GetProportionalHeight(int width, int sourceWidth, int sourceHeight)
    {
        if (IsFile)
            return ImageWidth > 0 && ImageHeight > 0 ? Math.Max(Even((double)width * ImageHeight / ImageWidth), 2) : width;

        var (_, _, cutWidth, cutHeight) = GetSourceRect(sourceWidth > 0 ? sourceWidth : 1920, sourceHeight > 0 ? sourceHeight : 1080);
        return Math.Max(Even((double)width * cutHeight / cutWidth), 2);
    }

    /// <summary>Called when the lock is released: the free height starts from the shape the layer has now.</summary>
    public void StartFreeHeight(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight)
    {
        if (frameHeight <= 0)
            return;

        var width = Math.Max(Even(SizeWidth * frameWidth), 2);
        FromCorner(() => SizeHeight = (double)GetProportionalHeight(width, sourceWidth, sourceHeight) / frameHeight);
    }

    private static int Even(double value) => (int)Math.Round(value / 2) * 2;

    public LayerState ToState() => new()
    {
        Name = Name,
        Kind = Kind,
        ImagePath = ImagePath,
        ImageWidth = ImageWidth,
        ImageHeight = ImageHeight,
        CornerRadius = CornerRadius,
        Opacity = Opacity,
        SourceX = SourceX,
        SourceY = SourceY,
        SourceWidth = SourceWidth,
        SourceHeight = SourceHeight,
        PositionX = PositionX,
        PositionY = PositionY,
        SizeWidth = SizeWidth,
        SizeHeight = SizeHeight,
        LockAspectRatio = LockAspectRatio,
        Feather = Feather,
        FeatherRadius = FeatherRadius,
        Shadow = Shadow,
        ShadowOpacity = ShadowOpacity,
        ShadowOffset = ShadowOffset,
        ChromaKey = ChromaKey,
        ChromaColor = ChromaColor,
        ChromaSimilarity = ChromaSimilarity,
        ChromaBlend = ChromaBlend,
        CustomMask = CustomMask,
        MaskPath = MaskPath,
        StartTime = StartTime,
        Duration = Duration,
        MediaOffset = MediaOffset,
        IsHidden = IsHidden,
        HasAudio = HasAudio,
        AudioOffset = AudioOffset,
        TrackId = TrackId,
        MediaDuration = MediaDuration,
        AudioGainDb = AudioGainDb,
        Rotation = Rotation,
        FlipHorizontal = FlipHorizontal,
        FlipVertical = FlipVertical,
        FilterContrast = FilterContrast,
        FilterBrightness = FilterBrightness,
        FilterSaturation = FilterSaturation,
        FilterGamma = FilterGamma,
        FilterHue = FilterHue,
        FilterSharpen = FilterSharpen,
        FilterBlur = FilterBlur,
        FilterDenoise = FilterDenoise,
        FilterLut = FilterLut,
    };

    /// <summary>Takes the look of a saved layer (style, mask, key, filters, turning) and leaves place, size and timing as they are.</summary>
    public void ApplyLook(LayerState? state)
    {
        state ??= new LayerState();
        (CornerRadius, Opacity) = (Math.Clamp(state.CornerRadius, 0, 50), Math.Clamp(state.Opacity, 0, 100));
        (Feather, FeatherRadius, Shadow, ShadowOpacity, ShadowOffset) = (state.Feather, state.FeatherRadius, state.Shadow, state.ShadowOpacity, state.ShadowOffset);
        (ChromaKey, ChromaColor) = (state.ChromaKey, string.IsNullOrWhiteSpace(state.ChromaColor) ? "#00FF00" : state.ChromaColor);
        (ChromaSimilarity, ChromaBlend) = (Math.Clamp(state.ChromaSimilarity, 0.01, 1), Math.Clamp(state.ChromaBlend, 0, 1));
        (CustomMask, MaskPath, IsHidden) = (state.CustomMask, state.MaskPath ?? "", state.IsHidden);
        (Rotation, FlipHorizontal, FlipVertical) = (Math.Clamp(state.Rotation, -360, 360), state.FlipHorizontal, state.FlipVertical);
        (FilterContrast, FilterBrightness, FilterSaturation) = (Math.Clamp(state.FilterContrast, 0, 2), Math.Clamp(state.FilterBrightness, -1, 1), Math.Clamp(state.FilterSaturation, 0, 3));
        (FilterGamma, FilterHue, FilterSharpen) = (Math.Clamp(state.FilterGamma, 0.1, 3), Math.Clamp(state.FilterHue, -180, 180), Math.Clamp(state.FilterSharpen, 0, 1));
        (FilterBlur, FilterDenoise, FilterLut) = (Math.Clamp(state.FilterBlur, 0, 50), state.FilterDenoise, state.FilterLut ?? "");
    }

    /// <summary>
    /// Rebuilds a layer from saved state. Presets saved before positions became fractions hold pixel
    /// values; those are converted using the sizes given (or 1920 x 1080 and 1080 x 1920 when unknown).
    /// </summary>
    public static Layer FromState(LayerState state, int sourceWidth, int sourceHeight, int frameWidth, int frameHeight)
    {
        var region = new Layer
        {
            Name = state.Name,
            Kind = state.Kind,
            ImagePath = state.ImagePath,
            ImageWidth = state.ImageWidth,
            ImageHeight = state.ImageHeight,
            CornerRadius = Math.Clamp(state.CornerRadius, 0, 50),
            Opacity = Math.Clamp(state.Opacity, 0, 100),
            LockAspectRatio = state.LockAspectRatio,
            Feather = state.Feather,
            FeatherRadius = state.FeatherRadius,
            Shadow = state.Shadow,
            ShadowOpacity = state.ShadowOpacity,
            ShadowOffset = state.ShadowOffset,
            ChromaKey = state.ChromaKey,
            ChromaColor = string.IsNullOrWhiteSpace(state.ChromaColor) ? "#00FF00" : state.ChromaColor,
            ChromaSimilarity = Math.Clamp(state.ChromaSimilarity, 0.01, 1),
            ChromaBlend = Math.Clamp(state.ChromaBlend, 0, 1),
            CustomMask = state.CustomMask,
            MaskPath = state.MaskPath ?? "",
            StartTime = Math.Max(state.StartTime, 0),
            Duration = Math.Max(state.Duration, 0),
            MediaOffset = Math.Max(state.MediaOffset, 0),
            IsHidden = state.IsHidden,
            HasAudio = state.HasAudio,
            AudioOffset = state.AudioOffset,
            TrackId = state.TrackId,
            MediaDuration = Math.Max(state.MediaDuration, 0),
            AudioGainDb = Math.Clamp(state.AudioGainDb, -40, 24),
            Rotation = Math.Clamp(state.Rotation, -360, 360),
            FlipHorizontal = state.FlipHorizontal,
            FlipVertical = state.FlipVertical,
            FilterContrast = Math.Clamp(state.FilterContrast, 0, 2),
            FilterBrightness = Math.Clamp(state.FilterBrightness, -1, 1),
            FilterSaturation = Math.Clamp(state.FilterSaturation, 0, 3),
            FilterGamma = Math.Clamp(state.FilterGamma, 0.1, 3),
            FilterHue = Math.Clamp(state.FilterHue, -180, 180),
            FilterSharpen = Math.Clamp(state.FilterSharpen, 0, 1),
            FilterBlur = Math.Clamp(state.FilterBlur, 0, 50),
            FilterDenoise = state.FilterDenoise,
            FilterLut = state.FilterLut ?? "",
        };

        if (state.SourceWidth <= 0 && state.Width is > 0 && state.Height is > 0)
        {
            double sw = sourceWidth > 0 ? sourceWidth : 1920, sh = sourceHeight > 0 ? sourceHeight : 1080;
            double fw = frameWidth > 0 ? frameWidth : 1080, fh = frameHeight > 0 ? frameHeight : 1920;
            region.SourceX = (state.X ?? 0) / sw;
            region.SourceY = (state.Y ?? 0) / sh;
            region.SourceWidth = state.Width.Value / sw;
            region.SourceHeight = state.Height.Value / sh;
            region.PositionX = (state.OffsetX ?? 0) / fw;
            region.PositionY = (state.OffsetY ?? 0) / fh;
            region.SizeWidth = (state.OutputWidth ?? 480) / fw;
            region.SizeHeight = (state.FreeHeight ?? 0) / fh;
        }
        else
        {
            region.SourceX = state.SourceX;
            region.SourceY = state.SourceY;
            region.SourceWidth = state.SourceWidth;
            region.SourceHeight = state.SourceHeight;
            region.PositionX = state.PositionX;
            region.PositionY = state.PositionY;
            region.SizeWidth = state.SizeWidth;
            region.SizeHeight = state.SizeHeight;
        }

        return region;
    }
}

/// <summary>A layer as it is written to presets and projects.</summary>
public sealed class LayerState
{
    public string Name { get; set; } = "Layer";

    public LayerKind Kind { get; set; }
    public string ImagePath { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public int CornerRadius { get; set; }
    public int Opacity { get; set; } = 100;

    public double SourceX { get; set; }
    public double SourceY { get; set; }
    public double SourceWidth { get; set; }
    public double SourceHeight { get; set; }

    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double SizeWidth { get; set; }
    public double SizeHeight { get; set; }

    public bool LockAspectRatio { get; set; } = true;

    public bool Feather { get; set; }
    public int FeatherRadius { get; set; } = 12;
    public bool Shadow { get; set; }
    public double ShadowOpacity { get; set; } = 0.5;
    public int ShadowOffset { get; set; } = 10;

    public bool ChromaKey { get; set; }
    public string ChromaColor { get; set; } = "#00FF00";
    public double ChromaSimilarity { get; set; } = 0.15;
    public double ChromaBlend { get; set; } = 0.05;
    public bool CustomMask { get; set; }
    public string MaskPath { get; set; } = "";

    public double StartTime { get; set; }
    public double Duration { get; set; }
    public double MediaOffset { get; set; }
    public bool IsHidden { get; set; }
    public bool HasAudio { get; set; }
    public double AudioOffset { get; set; }

    public int TrackId { get; set; }
    public double MediaDuration { get; set; }
    public double AudioGainDb { get; set; }
    public double Rotation { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public double FilterContrast { get; set; } = 1;
    public double FilterBrightness { get; set; }
    public double FilterSaturation { get; set; } = 1;
    public double FilterGamma { get; set; } = 1;
    public double FilterHue { get; set; }
    public double FilterSharpen { get; set; }
    public double FilterBlur { get; set; }
    public bool FilterDenoise { get; set; }
    public string FilterLut { get; set; } = "";

    // Pixel values from presets saved by earlier versions. Read for conversion, never written.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputWidth { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? OffsetX { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? OffsetY { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? FreeHeight { get; set; }
}
