using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

/// <summary>What a layer shows.</summary>
public enum LayerKind
{
    /// <summary>
    /// Legacy Cutout kind (a source rectangle such as a minimap). Presets still describe Cutouts this way, but placement turns them
    /// into VideoFile clips of the host clip's file (MainViewModel.FileCutoutOf); timeline clips of this kind only occur in legacy
    /// projects, converted on open (LegacyMainVideo). Kinds serialize by name.
    /// </summary>
    Video,

    /// <summary>A picture from a file (a logo, a frame, a caption).</summary>
    Image,

    /// <summary>The box the auto-captions are drawn in. There is one, and it is always the top layer.</summary>
    Captions,

    /// <summary>Video file clip with its own timebase; every video clip on any track.</summary>
    VideoFile,

    /// <summary>
    /// Legacy main-video kind; never created. Kept so legacy projects load and convert (LegacyMainVideo), presets referencing it
    /// parse, and golden fixtures can still use it.
    /// </summary>
    MainVideo,

    /// <summary>
    /// Legacy full-frame blur background kind; never rendered (blur fill is per clip, <see cref="Layer.BlurFill"/>). Kept for parsing
    /// and as the kind of the blur-fill settings row.
    /// </summary>
    Background,

    /// <summary>A sound from a file, with no picture: it has a place in time and nothing else.</summary>
    Audio,

    /// <summary>Text clip rendered with a system font; transformed and animated like an image.</summary>
    Text,

    /// <summary>Adjustment clip: no pixels; its effects apply to everything below within its rectangle for its duration.</summary>
    Adjustment,
}

/// <summary>
/// Keyframe: value at a time (seconds from clip start) and the easing toward the next keyframe.
/// </summary>
/// <remarks>Value type, stored inline in lists (no GC references). Deserialized via its constructor so a missing easing gets the
/// default. The four Bezier fields are Custom Bezier handles (<see cref="EasingCurves"/>), omitted from JSON while 0.</remarks>
[method: System.Text.Json.Serialization.JsonConstructor]
public readonly record struct Keyframe(
    double Time, double Value, EasingType Easing = EasingType.EaseInOut,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double BezierX1 = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double BezierY1 = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double BezierX2 = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double BezierY2 = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double PartStart = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double PartEnd = 0)
{
    /// <summary>Copy with a different easing (Custom Bezier handles), using the whole curve.</summary>
    public Keyframe WithEasing(EasingType easing, (double X1, double Y1, double X2, double Y2)? handles = null) => handles is { } h
        ? this with { Easing = easing, BezierX1 = h.X1, BezierY1 = h.Y1, BezierX2 = h.X2, BezierY2 = h.Y2, PartStart = 0, PartEnd = 0 }
        : this with { Easing = easing, PartStart = 0, PartEnd = 0 };

    /// <summary>
    /// True when the outgoing segment is a sub-range of its easing curve (left by a split between keyframes, EasingCurves.Cut), so
    /// each half moves exactly as the whole did.
    /// </summary>
    public bool IsPart => PartEnd > PartStart && (PartStart > 0 || PartEnd < 1);

    /// <summary>Curve sub-range as time fractions; (0, 1) for the whole curve.</summary>
    public (double Start, double End) Part => IsPart ? (PartStart, PartEnd) : (0, 1);

    /// <summary>Copy using the curve sub-range between two time fractions (0 to 1 = whole curve).</summary>
    public Keyframe WithPart(double start, double end) => end - start >= 1 - 1e-12 || end <= start
        ? this with { PartStart = 0, PartEnd = 0 }
        : this with { PartStart = Math.Clamp(start, 0, 1), PartEnd = Math.Clamp(end, 0, 1) };
}

/// <summary>What of a layer can be animated with keyframes.</summary>
public enum KeyProperty
{
    /// <summary>Where its left edge is, as a fraction of the frame's width.</summary>
    X,

    /// <summary>Where its top edge is, as a fraction of the frame's height.</summary>
    Y,

    /// <summary>Its width, as a fraction of the frame's width.</summary>
    Scale,

    /// <summary>Degrees it is turned clockwise.</summary>
    Rotation,

    /// <summary>How solid it is: 100 is solid, 0 is not there.</summary>
    Opacity,

    /// <summary>Pitch in degrees (top away from viewer), -60..60.</summary>
    TiltX,

    /// <summary>Yaw in degrees (right edge away from viewer), -60..60.</summary>
    TiltY,
}

/// <summary>
/// A region of the source frame as 0-1 fractions [<paramref name="UMin"/>, <paramref name="VMin"/>, <paramref name="UMax"/>,
/// <paramref name="VMax"/>] (the space of mask outlines and Cutout source rects; Pass 5.6.5 contract for Cutout tight bounds).
/// </summary>
public readonly record struct SourceBounds(double UMin, double VMin, double UMax, double VMax)
{
    public double Width => UMax - UMin;

    public double Height => VMax - VMin;

    /// <summary>True when it encloses some area (finite, non-zero width and height).</summary>
    public bool HasArea => double.IsFinite(Width) && double.IsFinite(Height) && Width > 1e-6 && Height > 1e-6;

    /// <summary>Bounds from saved values: four finite numbers, held to 0..1 and ordered; null for anything else or no area.</summary>
    public static SourceBounds? From(IReadOnlyList<double>? values)
    {
        if (values is not [var u0, var v0, var u1, var v1] || !double.IsFinite(u0 + v0 + u1 + v1))
            return null;

        (u0, v0, u1, v1) = (Math.Clamp(u0, 0, 1), Math.Clamp(v0, 0, 1), Math.Clamp(u1, 0, 1), Math.Clamp(v1, 0, 1));
        var bounds = new SourceBounds(Math.Min(u0, u1), Math.Min(v0, v1), Math.Max(u0, u1), Math.Max(v0, v1));
        return bounds.HasArea ? bounds : null;
    }

    /// <summary>As saved: [UMin, VMin, UMax, VMax].</summary>
    public double[] ToArray() => [UMin, VMin, UMax, VMax];

    /// <summary>The overlap with <paramref name="other"/>; null when they do not overlap.</summary>
    public SourceBounds? Intersect(SourceBounds other)
    {
        var both = new SourceBounds(Math.Max(UMin, other.UMin), Math.Max(VMin, other.VMin), Math.Min(UMax, other.UMax), Math.Min(VMax, other.VMax));
        return both.HasArea ? both : null;
    }
}

/// <summary>
/// Clip look and placement on the output frame (video, image, text, adjustment), all positioned and styled the same way.
/// Geometry is stored as 0-1 fractions (source rect of the source frame; position/size of the output frame), so layouts are
/// resolution-independent.
/// </summary>
public sealed partial class Layer : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = "Layer";

    public LayerKind Kind { get; init; }

    /// <summary>The picture of an image layer.</summary>
    public string ImagePath { get; init; } = "";

    // The picture's own size in pixels, which gives a locked image layer its shape.
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>
    /// Placeholder while a dropped file is being probed: size, duration and audio presence are unknown, so nothing renders yet.
    /// </summary>
    [ObservableProperty] private bool _isLoading;

    public bool IsImage => Kind == LayerKind.Image;

    public bool IsText => Kind == LayerKind.Text;

    /// <summary>One picture that does not change as time passes: an image from a file, or words.</summary>
    public bool IsStill => IsImage || IsText;

    // ----- Text -----
    // Text content and styling. The box is measured from the text (TextLayout) and stored in ImageWidth/ImageHeight like an image size.

    /// <summary>The words. A line break starts a new line.</summary>
    [ObservableProperty] private string _textContent = "";

    /// <summary>The font, by the name Windows lists it under.</summary>
    [ObservableProperty] private string _fontFamily = TextDefaults.Font;

    /// <summary>The font's size in pixels of the output frame, before the layer is scaled.</summary>
    [ObservableProperty] private double _fontSize = TextDefaults.Size;

    /// <summary>The colour of the letters, as #RRGGBB.</summary>
    [ObservableProperty] private string _fontColor = "#FFFFFF";

    /// <summary>The colour of the line drawn around the letters, as #RRGGBB.</summary>
    [ObservableProperty] private string _outlineColor = "#000000";

    /// <summary>How thick that line is, in pixels at the font's set size; 0 for none.</summary>
    [ObservableProperty] private int _outlineThickness = 2;

    /// <summary>Text properties that affect box size; a change triggers re-measurement.</summary>
    public static bool IsTextShapeProperty(string? name) =>
        name is nameof(TextContent) or nameof(FontFamily) or nameof(FontSize) or nameof(OutlineThickness);

    public bool IsVideoFile => Kind == LayerKind.VideoFile;

    /// <summary>Which audio stream of its file a lowered clip is heard from.</summary>
    public int AudioStream { get; set; }

    /// <summary>A picture or a video that comes from a file of its own.</summary>
    public bool IsFile => IsImage || IsVideoFile;

    public bool IsCaptions => Kind == LayerKind.Captions;

    public bool IsBackground => Kind == LayerKind.Background;

    /// <summary>A sound with no picture.</summary>
    public bool IsAudio => Kind == LayerKind.Audio;

    /// <summary>Has sound of its own that goes into the output: a video from a file that has any, or a sound file.</summary>
    public bool CarriesSound => HasAudio && (IsVideoFile || IsAudio);

    /// <summary>Something added by hand, which can be removed again: the captions and the background cannot.</summary>
    public bool IsRemovable => Kind is LayerKind.Video or LayerKind.Image or LayerKind.VideoFile or LayerKind.Audio or LayerKind.Text or LayerKind.Adjustment;

    /// <summary>An adjustment layer: its effects work on the picture under it.</summary>
    public bool IsAdjustment => Kind == LayerKind.Adjustment;

    /// <summary>A picture on the frame: everything but the background, which is the frame, and a sound.</summary>
    public bool IsPicture => !IsBackground && !IsAudio;

    /// <summary>Placed with the sliders of its row: its top-left corner and its width.</summary>
    public bool HasPlacement => IsPicture;

    /// <summary>Has a look of its own: opacity, corners, soft edges, a shadow.</summary>
    public bool HasStyle => IsPicture;

    /// <summary>Can be moved up and down the stack: the captions stay on top and the background at the bottom.</summary>
    public bool CanReorder => !IsCaptions && !IsBackground;

    /// <summary>True when the track supports Hide (captions use their own switch).</summary>
    public bool CanHide => IsRemovable || IsBackground;

    /// <summary>Can have a color keyed out of it: the pictures and videos.</summary>
    public bool HasKeying => IsPicture && !IsCaptions;

    /// <summary>Can have a mask of its own laid on it: those, and the caption box.</summary>
    public bool HasMask => IsPicture;

    /// <summary>Can be given filters of its own, and be turned: every picture but the caption box.</summary>
    public bool HasFilters => IsPicture && !IsCaptions;

    // Track membership: a layer is one clip on its track; a split creates a second clip there. Each clip owns its full look
    // (placement, mask, matte, key, effects).

    /// <summary>Which track the clip is on. Clips with the same number share a row.</summary>
    [ObservableProperty] private int _trackId;

    /// <summary>
    /// Id of the document clip this layer views (MainViewModel.Document); empty for non-clip layers (caption box, background,
    /// empty track row, preset or lowering layers).
    /// </summary>
    public Guid ClipId { get; set; }

    /// <summary>
    /// Routes <see cref="ClipSpeed"/> edits on a clip view to a document edit that the view then follows. Null for non-clip layers,
    /// which set their own speed (<see cref="SetSpeed"/>).
    /// </summary>
    public Action<Layer, double>? SpeedEditor { get; set; }

    // When the layer is on screen, in seconds of the source's own time.

    /// <summary>When the layer appears.</summary>
    [ObservableProperty] private double _startTime;

    /// <summary>How long it stays; 0 for until the end of the video.</summary>
    [ObservableProperty] private double _duration;

    /// <summary>Source offset at clip start (video); set on split so the second part continues from the first.</summary>
    [ObservableProperty] private double _mediaOffset;

    // Speed: timeline length = source range / speed.

    // Max 16x (Speed & Time card upper bound).
    public const double SlowestSpeed = 0.1, FastestSpeed = 16;

    /// <summary>How many times as fast as recorded the clip runs: 1 as it is, 0.5 at half speed, 2 at double.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipSpeed))]
    private double _speed = 1;

    /// <summary>True for time-based media (video file or audio) that supports speed changes.</summary>
    public bool HasSpeed => IsVideoFile || IsAudio;

    /// <summary>The speed, for the boxes that set it: setting it here changes how long the clip is with it.</summary>
    public double ClipSpeed
    {
        get => Speed;
        set
        {
            if (SpeedEditor is { } edit)
                edit(this, value);
            else
                SetSpeed(value);
        }
    }

    /// <summary>Re-raises the speed change so a bound box reverts after a refused edit.</summary>
    public void NotifySpeed() => OnPropertyChanged(nameof(ClipSpeed));

    /// <summary>
    /// Sets speed over the same source range, rescaling timeline length and all timeline-relative times so keyframes stay on their frames.
    /// </summary>
    public void SetSpeed(double value)
    {
        if (!double.IsFinite(value))
            return;

        value = Math.Round(Math.Clamp(value, SlowestSpeed, FastestSpeed), 3);
        var stretch = Speed / value;
        if (Math.Abs(stretch - 1) < 1e-6)
            return;

        var length = Duration;
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            var list = _keys[(int)property];
            for (var i = 0; i < list.Count; i++)
                list[i] = list[i] with { Time = Math.Round(list[i].Time * stretch, 3) };
            if (list.Count > 0)
                NotifyKeys(property);
        }

        // The speed ramp and the manual ducking curve are clip-relative too (LayerState.MotionChannels).
        RetimeClipKeys(k => k with { Time = Math.Round(k.Time * stretch, 3) });
        Speed = value;
        if (length > 0.001)
            Duration = Math.Max(Math.Round(length * stretch, 3), 0.05);
    }

    /// <summary>Kept in the list but left out of the picture, and (for a video layer) out of the sound.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Action))]
    [NotifyPropertyChangedFor(nameof(IsMuted))]
    private bool _isHidden;

    /// <summary>True while in the recycle bin (removed but restorable).</summary>
    [ObservableProperty] private bool _isDeleted;

    /// <summary>Preview proxy policy: Auto (heavy files), Make Proxy (always) or Use Original (never).</summary>
    [ObservableProperty] private ProxyChoice _proxyChoice;

    /// <summary>
    /// Empty-track placeholder (Add Layer): represents the track, is replaced by the first dropped clip, and never renders.
    /// </summary>
    [ObservableProperty] private bool _isEmptyTrack;

    /// <summary>A video layer whose file has sound, which goes into the output with it.</summary>
    [ObservableProperty] private bool _hasAudio;

    /// <summary>Audio slip versus video in seconds, while unlinked.</summary>
    [ObservableProperty] private double _audioOffset;

    /// <summary>How long the layer's own video or sound is, in seconds; 0 when not known.</summary>
    [ObservableProperty] private double _mediaDuration;

    /// <summary>How much louder or quieter the layer's sound is made, in decibels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainDb))]
    private double _audioGainDb;

    /// <summary>
    /// Fade-in length (timeline seconds): video per <see cref="FadeInStyle"/>, audio from silence. Top-left block handle.
    /// </summary>
    [ObservableProperty] private double _fadeInSeconds;

    /// <summary>Fade-out length, video and audio (top-right block handle).</summary>
    [ObservableProperty] private double _fadeOutSeconds;

    /// <summary>How the picture comes on over its fade in: a dissolve, a wipe, a slide, a zoom, a blur.</summary>
    [ObservableProperty] private TransitionStyle _fadeInStyle;

    /// <summary>How the picture goes over its fade out.</summary>
    [ObservableProperty] private TransitionStyle _fadeOutStyle;

    /// <summary>
    /// Lowering-only flag (never saved): keep video opaque through the fade and fade only audio, because the overlapping clip
    /// carries the video side of the crossfade (TimelineLowering.LayOverAcrossCut, PairLayerCrossfades).
    /// </summary>
    public bool PictureFadeInOff { get; set; }

    public bool PictureFadeOutOff { get; set; }

    /// <summary>
    /// Crossfade length (s) from the abutting previous clip, centred on the cut using both clips' handles (video dissolve, audio
    /// crossfade); 0 = cut. Expanded by TimelineLowering (LayOverAcrossCut).
    /// </summary>
    [ObservableProperty] private double _transitionIn;

    // ----- Audio tab row binding -----
    // Audio clips share the AudioTrack row template. These members mirror AudioTrack's binding names: some map to clip settings,
    // the rest report unsupported features (own codec, filters, solo) so the row leaves them empty.

    /// <summary>What is done with a sound from a file: it is mixed into the output's first track, or left out.</summary>
    public const string MixIntoFirstTrack = "Mix into First Track";

    private static readonly IReadOnlyList<string> ClipActions = [MixIntoFirstTrack, AudioTrack.Drop];

    public string Title
    {
        get => Name;
        set => Name = string.IsNullOrWhiteSpace(value) ? Name : value.Trim();
    }

    /// <summary>The file the sound comes from, as its row is headed.</summary>
    public string SourceFileName => ImagePath.Length > 0 ? System.IO.Path.GetFileName(ImagePath) : Name;

    /// <summary>What the sound's stream is encoded as in its file (aac, pcm_s16le...), when that is known.</summary>
    [ObservableProperty]
    private string _audioCodec = "";

    /// <summary>Its channels as FFmpeg names them (stereo, mono, 5.1...), when known.</summary>
    [ObservableProperty]
    private string _audioChannels = "";

    public IReadOnlyList<string> Actions => ClipActions;

    /// <summary>The sound is left out, while a video layer's picture stays. (A sound clip is simply hidden.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Action))]
    [NotifyPropertyChangedFor(nameof(IsMuted))]
    private bool _isSoundMuted;

    /// <summary>Included in the mix; for audio clips equivalent to hiding, video keeps its picture.</summary>
    /// <summary>Muted (row Mute button); equivalent to Action Ignore / Drop.</summary>
    public bool IsMuted
    {
        get => Action == AudioTrack.Drop;
        set => Action = value ? AudioTrack.Drop : MixIntoFirstTrack;
    }

    public string Action
    {
        get => (IsAudio ? IsHidden : IsSoundMuted) ? AudioTrack.Drop : MixIntoFirstTrack;
        set
        {
            if (IsAudio)
                IsHidden = value == AudioTrack.Drop;
            else
                IsSoundMuted = value == AudioTrack.Drop;
        }
    }

    // No output stream of its own: mixed into the first track and encoded with it; the row's format boxes are read-only.
    private static readonly IReadOnlyList<string> FirstTrackCodec = ["as first track"];
    private static readonly IReadOnlyList<string> FirstTrackBitrate = ["mixed"];

    public IReadOnlyList<string> Codecs => FirstTrackCodec;
    public string Codec { get => FirstTrackCodec[0]; set { } }
    public IReadOnlyList<string> Bitrates => FirstTrackBitrate;
    public string Bitrate { get => FirstTrackBitrate[0]; set { } }

    /// <summary>Whether its codec and bitrate can be chosen: never, see above.</summary>
    public bool HasOwnFormat => false;

    /// <summary>Always decoded and mixed, so its gain and filters always apply.</summary>
    public bool IsReencoded => true;

    /// <summary>Audio processing chain (compressor, gate, EQ, ...); replaced wholesale by its dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiltersCountText))]
    private TrackAudioFilters _audioFilters = new();

    public double GainDb
    {
        get => AudioGainDb;
        set => AudioGainDb = Math.Clamp(value, -40, 24);
    }

    /// <summary>One of the video's own audio streams? No: a clip.</summary>
    public bool IsStream => false;

    public string FiltersCountText => AudioFilters.ActiveCount > 0 ? AudioFilters.ActiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    // Turning and mirroring.

    /// <summary>Degrees the picture is turned clockwise about its middle.</summary>
    [ObservableProperty] private double _rotation;

    /// <summary>
    /// Pitch in degrees about the anchor's horizontal axis (top away), rendered in perspective. With <see cref="TiltY"/> (Yaw) and
    /// <see cref="Rotation"/> (Roll) forms the 3D orientation.
    /// </summary>
    [ObservableProperty] private double _tiltX;

    /// <summary>Yaw in degrees about the anchor's vertical axis (right edge away).</summary>
    [ObservableProperty] private double _tiltY;

    /// <summary>Tilt limit in degrees; beyond it the plane is too close to edge-on to read.</summary>
    public const double TiltLimit = 60;

    /// <summary>A saved or typed tilt, held to <see cref="TiltLimit"/> (0 when it is not a number).</summary>
    public static double Tilt(double value) => double.IsFinite(value) ? Math.Clamp(value, -TiltLimit, TiltLimit) : 0;

    partial void OnTiltXChanged(double value)
    {
        if (Tilt(value) != value)
            TiltX = Tilt(value);
    }

    partial void OnTiltYChanged(double value)
    {
        if (Tilt(value) != value)
            TiltY = Tilt(value);
    }

    // Mirroring and filters live as effect-stack cards (Layer.Effects.cs); FlipHorizontal, FilterContrast, etc. are views onto them.

    /// <summary>Whether any filter of the layer's own is set to something.</summary>
    public bool HasFilterChanges => Math.Abs(FilterContrast - 1) >= 0.005 || Math.Abs(FilterBrightness) >= 0.005 || Math.Abs(FilterSaturation - 1) >= 0.005
        || Math.Abs(FilterGamma - 1) >= 0.005 || Math.Abs(FilterHue) >= 0.05 || FilterSharpen >= 0.005 || FilterBlur >= 0.05 || FilterDenoise || FilterLut.Trim().Length > 0 || HasNewLook;

    public void ResetFilters() =>
        (FilterContrast, FilterBrightness, FilterSaturation, FilterGamma, FilterHue, FilterSharpen, FilterBlur, FilterDenoise, FilterLut) = (1, 0, 1, 1, 0, 0, 0, false, "");

    public void ResetTransform() => (Rotation, TiltX, TiltY, FlipHorizontal, FlipVertical) = (0, 0, 0, false, false);

    // Anchor-relative sizing: position is stored as the top-left corner, but resizing keeps the anchor (default centre) fixed and
    // moves the corner. Rotation uses the same anchor (GPU scene plan, layout pane).

    /// <summary>Enabled after construction; during initialization sizes and corners are taken verbatim.</summary>
    public bool AnchorCenter { get; set; }

    /// <summary>Anchor X as a fraction of width from the left edge (0.5 = centre).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorXPercent))]
    private double _anchorX = 0.5;

    /// <summary>The same, as a fraction of its height from its top edge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorYPercent))]
    private double _anchorY = 0.5;

    // The anchor as percentages of the layer, for the number boxes.
    public double AnchorXPercent { get => Math.Round(AnchorX * 100, 1); set => AnchorX = Anchor(value / 100); }
    public double AnchorYPercent { get => Math.Round(AnchorY * 100, 1); set => AnchorY = Anchor(value / 100); }

    /// <summary>
    /// Centre displacement when rotating about the anchor instead of the centre: rotated anchor-to-centre vector minus the original
    /// (dx, dy pixels, y down, degrees clockwise). Rotation happens about the centre, then the centre is offset by this.
    /// </summary>
    public static (double X, double Y) Pivot(double degrees, double dx, double dy)
    {
        var angle = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        return (dx * (cos - 1) - dy * sin, dx * sin + dy * (cos - 1));
    }

    /// <summary>
    /// Moves the anchor without visually moving the layer: compensates the corner for the rotation-centre change.
    /// </summary>
    public void MoveAnchor(double x, double y, double widthPixels, double heightPixels, int frameWidth, int frameHeight)
    {
        (x, y) = (Anchor(x), Anchor(y));
        var turn = HasFilters ? Rotation : 0;
        if (Math.Abs(turn % 360) >= 0.05 && frameWidth > 0 && frameHeight > 0)
        {
            var before = Pivot(turn, (0.5 - AnchorX) * widthPixels, (0.5 - AnchorY) * heightPixels);
            var after = Pivot(turn, (0.5 - x) * widthPixels, (0.5 - y) * heightPixels);
            PositionX += (before.X - after.X) / frameWidth;
            PositionY += (before.Y - after.Y) / frameHeight;
        }

        (AnchorX, AnchorY) = (x, y);
    }

    /// <summary>How far outside the layer its anchor point may be put: a layer's width or height beyond each edge.</summary>
    public const double SmallestAnchor = -1, LargestAnchor = 2;

    /// <summary>Whether the anchor point is anywhere but the middle.</summary>
    public bool HasOffsetAnchor => Math.Abs(AnchorX - 0.5) > 1e-4 || Math.Abs(AnchorY - 0.5) > 1e-4;

    /// <summary>A saved anchor, held to the reach above; the middle for one that is not a number.</summary>
    public static double Anchor(double value) => double.IsFinite(value) ? Math.Clamp(value, SmallestAnchor, LargestAnchor) : 0.5;

    // ----- Cutout tight bounds and auto-centred anchor (Pass 5.6.5 contract) -----
    // TightBounds: the visible pixels of a Cutout (drawn outline vertices, or non-zero mask alpha) in source-frame fractions, the space
    // of MaskShapes and the source rect; null = unknown or the whole source rect. Set by whoever changes the mask, so the selection box
    // and gizmo hug the HUD element. AutoCenterAnchor: the anchor follows TightAnchor (rotation and tilt spin the element in place); a
    // manual anchor move clears it. Both are look values (LayerState TightBounds and AutoCenterAnchor).

    /// <summary>The Cutout's visible-pixel bounds in source-frame fractions; null when unknown or the whole source rect.</summary>
    [ObservableProperty] private SourceBounds? _tightBounds;

    /// <summary>The anchor is kept at the centre of <see cref="TightBounds"/> (<see cref="TightAnchor"/>) instead of where it was put.</summary>
    [ObservableProperty] private bool _autoCenterAnchor;

    /// <summary>
    /// The part of the source frame the layer's box shows: its source rect (the whole frame when unset) less its crop (<see cref="HeldCrop"/>).
    /// </summary>
    public SourceBounds ShownSource()
    {
        var (x, y, width, height) = SourceWidth > 0 && SourceHeight > 0 ? (SourceX, SourceY, SourceWidth, SourceHeight) : (0.0, 0.0, 1.0, 1.0);
        var (left, top, right, bottom) = HeldCrop(CropLeft, CropTop, CropRight, CropBottom);
        return new SourceBounds(x + width * left, y + height * top, x + width * (1 - right), y + height * (1 - bottom));
    }

    /// <summary>
    /// <see cref="TightBounds"/> as fractions of the layer's box (0-1 from its left and top edges: what it shows of them); null when unknown
    /// or not on what the box shows.
    /// </summary>
    public SourceBounds? TightBoxBounds()
    {
        var shown = ShownSource();
        if (TightBounds is not { } tight || !shown.HasArea || tight.Intersect(shown) is not { } seen)
            return null;
        return new SourceBounds((seen.UMin - shown.UMin) / shown.Width, (seen.VMin - shown.VMin) / shown.Height,
            (seen.UMax - shown.UMin) / shown.Width, (seen.VMax - shown.VMin) / shown.Height);
    }

    /// <summary>The anchor at the centre of the visible pixels (<see cref="TightBoxBounds"/>), as AnchorX/AnchorY fractions; null without bounds.</summary>
    public (double X, double Y)? TightAnchor() =>
        TightBoxBounds() is { } box ? (Anchor((box.UMin + box.UMax) / 2), Anchor((box.VMin + box.VMax) / 2)) : null;

    // The layer's height for each unit of its width, both as fractions of the frame; learnt whenever it is laid out.
    private double _heightPerWidth;

    /// <summary>Last laid-out height per unit width in frame fractions (1 before first layout).</summary>
    public double HeightPerWidth => _heightPerWidth > 0 ? _heightPerWidth : 1;

    partial void OnSizeWidthChanged(double oldValue, double newValue)
    {
        if (!AnchorCenter)
            return;

        PositionX -= (newValue - oldValue) * AnchorX;
        if (LockAspectRatio || SizeHeight <= 0)
            PositionY -= (newValue - oldValue) * _heightPerWidth * AnchorY;
    }

    partial void OnSizeHeightChanged(double oldValue, double newValue)
    {
        if (AnchorCenter && !LockAspectRatio && oldValue > 0)
            PositionY -= (newValue - oldValue) * AnchorY;
    }

    /// <summary>Resizes keeping the top-left corner fixed (opposite-corner drag).</summary>
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

    /// <summary>
    /// Timeline span for a Sequence of the given length; layers without their own length extend to the end.
    /// </summary>
    public (double Start, double End) GetSpan(double totalSeconds) =>
        Duration > 0.001 ? (StartTime, StartTime + Duration) : (StartTime, Math.Max(totalSeconds, StartTime));

    // ----- Keyframes -----
    // Interpolated between keys, held constant outside the first/last. Times are relative to clip start, so motion moves with the clip.

    /// <summary>Two keyframes closer together than this are the same one: about half a frame.</summary>
    public const double KeyTolerance = 0.02;

    private readonly List<Keyframe>[] _keys = [[], [], [], [], [], [], []];

    public IReadOnlyList<Keyframe> GetKeys(KeyProperty property) => _keys[(int)property];

    public bool HasKeys(KeyProperty property) => _keys[(int)property].Count > 0;

    /// <summary>Whether anything about the layer is animated.</summary>
    public bool IsAnimated => _keys.Any(k => k.Count > 0);

    // For the diamonds of the layer's row.
    public bool AnimatesX => HasKeys(KeyProperty.X);
    public bool AnimatesY => HasKeys(KeyProperty.Y);
    public bool AnimatesScale => HasKeys(KeyProperty.Scale);
    public bool AnimatesRotation => HasKeys(KeyProperty.Rotation);
    public bool AnimatesOpacity => HasKeys(KeyProperty.Opacity);
    public bool AnimatesTiltX => HasKeys(KeyProperty.TiltX);
    public bool AnimatesTiltY => HasKeys(KeyProperty.TiltY);

    /// <summary>True when any 3D tilt is set or keyframed.</summary>
    public bool IsTilted => TiltX != 0 || TiltY != 0 || HasKeys(KeyProperty.TiltX) || HasKeys(KeyProperty.TiltY);

    /// <summary>Every moment that has a keyframe of any kind, for the marks on the clip's block.</summary>
    public IEnumerable<double> KeyTimes => _keys.SelectMany(k => k).Select(k => k.Time).Distinct().Order();

    /// <summary>The property as it is set now: what is shown, and what a property without keyframes always is.</summary>
    public double GetValue(KeyProperty property) => property switch
    {
        KeyProperty.X => PositionX,
        KeyProperty.Y => PositionY,
        KeyProperty.Scale => SizeWidth,
        KeyProperty.Opacity => Opacity,
        KeyProperty.TiltX => TiltX,
        KeyProperty.TiltY => TiltY,
        _ => Rotation,
    };

    public void SetValue(KeyProperty property, double value)
    {
        switch (property)
        {
            case KeyProperty.X: PositionX = value; break;
            case KeyProperty.Y: PositionY = value; break;
            case KeyProperty.Scale: SizeWidth = value; break;
            case KeyProperty.Opacity: Opacity = (int)Math.Round(Math.Clamp(value, 0, 100)); break;
            case KeyProperty.TiltX: TiltX = Tilt(value); break;
            case KeyProperty.TiltY: TiltY = Tilt(value); break;
            default: Rotation = value; break;
        }
    }

    /// <summary>Which animated property a changed property is, or null for one that cannot be animated.</summary>
    public static KeyProperty? GetKeyProperty(string? name) => name switch
    {
        nameof(PositionX) => KeyProperty.X,
        nameof(PositionY) => KeyProperty.Y,
        nameof(SizeWidth) => KeyProperty.Scale,
        nameof(Rotation) => KeyProperty.Rotation,
        nameof(Opacity) => KeyProperty.Opacity,
        nameof(TiltX) => KeyProperty.TiltX,
        nameof(TiltY) => KeyProperty.TiltY,
        _ => null,
    };

    /// <summary>The value of an animated property at a moment of the clip; its set value when it has no keyframes.</summary>
    public double Evaluate(KeyProperty property, double time) =>
        _keys[(int)property] is { Count: > 0 } keys ? EasingCurves.ValueAt(keys, time) : GetValue(property);

    /// <summary>Keyframe at a time for one property, or (null) the first of any; null when none.</summary>
    public Keyframe? GetKeyAt(double time, KeyProperty? only = null)
    {
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            if (only is { } wanted && wanted != property)
                continue;

            foreach (var key in _keys[(int)property])
            {
                if (Math.Abs(key.Time - time) <= KeyTolerance)
                    return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets the outgoing easing of keyframes at a time for one property lane or (null) all of them, including Custom Bezier handles.
    /// Returns false when no keyframe exists there or it already has that easing.
    /// </summary>
    public bool SetEasingAt(double time, EasingType easing, (double X1, double Y1, double X2, double Y2)? handles = null, KeyProperty? only = null)
    {
        var changed = false;
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            if (only is { } wanted && wanted != property)
                continue;

            var keys = _keys[(int)property];
            for (var i = 0; i < keys.Count; i++)
            {
                if (Math.Abs(keys[i].Time - time) > KeyTolerance)
                    continue;

                var eased = keys[i].WithEasing(easing, easing == EasingType.CubicBezier ? handles ?? EasingCurves.Handles(keys[i]) : null);
                if (eased != keys[i])
                {
                    keys[i] = eased;
                    changed = true;
                }
            }
        }

        if (changed)
            NotifyAllKeys();
        return changed;
    }

    /// <summary>
    /// Moves keyframes at one time to another (diamond drag) for one property or (null) all; replaces any at the target.
    /// Returns false when none moved.
    /// </summary>
    public bool MoveKeys(double from, double to, KeyProperty? only = null)
    {
        to = Math.Round(Math.Max(to, 0), 3);
        var moved = false;
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            if (only is { } wanted && wanted != property)
                continue;

            var list = _keys[(int)property];
            var at = list.FindIndex(k => Math.Abs(k.Time - from) <= KeyTolerance);
            if (at < 0)
                continue;

            var key = list[at] with { Time = to };
            list.RemoveAt(at);
            list.RemoveAll(k => Math.Abs(k.Time - to) <= KeyTolerance);
            var next = list.FindIndex(k => k.Time > to);
            list.Insert(next < 0 ? list.Count : next, key);
            moved = true;
            NotifyKeys(property);
        }

        return moved;
    }

    /// <summary>Replaces a property's keyframes; none switches its animation off.</summary>
    public void SetKeys(KeyProperty property, IEnumerable<Keyframe>? keys)
    {
        var list = _keys[(int)property];
        if (list.Count == 0 && keys is null)
            return;

        list.Clear();
        if (keys is not null)
            list.AddRange(keys.Where(k => double.IsFinite(k.Time) && double.IsFinite(k.Value)).OrderBy(k => k.Time));
        NotifyKeys(property);
    }

    /// <summary>Sets a keyframe at a moment: a new one, or the value of the one that is already there.</summary>
    public void SetKey(KeyProperty property, double time, double value)
    {
        PutKey(property, time, value);
        NotifyKeys(property);
    }

    /// <summary>Toggles a property keyframe at a time using the current value (lane diamond button).</summary>
    /// <returns>True when a keyframe exists there afterwards.</returns>
    public bool ToggleKey(KeyProperty property, double time)
    {
        if (RemoveKey(property, time))
            return false;

        SetKey(property, time, GetValue(property));
        return true;
    }

    // Writes a keyframe silently. When position gets its first key while size is animated, position switches from anchor-held to
    // per-key corner positions; to avoid a jump, the corner is first keyed at each size key where the anchor places it, with matching easing.
    private void PutKey(KeyProperty property, double time, double value)
    {
        var list = _keys[(int)property];
        if (list.Count == 0 && property is KeyProperty.X or KeyProperty.Y && _keys[(int)KeyProperty.Scale].Count > 0)
        {
            var (place, anchor, across) = property == KeyProperty.X ? (PositionX, AnchorX, 1.0) : (PositionY, AnchorY, HeightPerWidth);
            foreach (var size in _keys[(int)KeyProperty.Scale])
                list.Add(size with { Value = place + anchor * (SizeWidth - size.Value) * across });
        }

        var at = KeyEdit.IndexAt(list, time);
        if (at >= 0)
        {
            list[at] = list[at] with { Value = value };
        }
        else
        {
            at = list.FindIndex(k => k.Time > time);
            list.Insert(at < 0 ? list.Count : at, new Keyframe(Math.Round(time, 3), value));
        }
    }

    /// <summary>Takes away the keyframe at a moment. False when there is none there.</summary>
    public bool RemoveKey(KeyProperty property, double time)
    {
        if (_keys[(int)property].RemoveAll(k => Math.Abs(k.Time - time) <= KeyTolerance) == 0)
            return false;

        NotifyKeys(property);
        return true;
    }

    // A keyframe pins everything at once: the clip's place, its size and its turn.

    /// <summary>Whether the clip has a keyframe at a moment.</summary>
    public bool HasKeyAt(double time)
    {
        foreach (var keys in _keys)
        {
            foreach (var key in keys)
            {
                if (Math.Abs(key.Time - time) <= KeyTolerance)
                    return true;
            }
        }

        return false;
    }

    /// <summary>The moment of the keyframe nearest a moment, or null when the clip has none.</summary>
    public double? GetNearestKeyTime(double time)
    {
        double? nearest = null;
        foreach (var keys in _keys)
        {
            foreach (var key in keys)
            {
                if (nearest is null || Math.Abs(key.Time - time) < Math.Abs(nearest.Value - time))
                    nearest = key.Time;
            }
        }

        return nearest;
    }

    /// <summary>Keys current position, size and rotation at a time; raises one change notification.</summary>
    public void SetMasterKey(double time)
    {
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            if (property == KeyProperty.Rotation && !HasFilters || property == KeyProperty.Opacity && !IsPicture)
                continue;

            // Key tilt only on tilted clips, so a master key does not add two lanes to every clip.
            if (property is KeyProperty.TiltX or KeyProperty.TiltY && (!HasFilters || (GetValue(property) == 0 && !HasKeys(property))))
                continue;

            PutKey(property, time, GetValue(property));
        }

        NotifyAllKeys();
    }

    /// <summary>Takes away the keyframe at a moment. False when there is none there.</summary>
    public bool RemoveMasterKey(double time)
    {
        var removed = 0;
        foreach (var keys in _keys)
            removed += keys.RemoveAll(k => Math.Abs(k.Time - time) <= KeyTolerance);
        if (removed == 0)
            return false;

        NotifyAllKeys();
        return true;
    }

    private void NotifyAllKeys()
    {
        OnPropertyChanged(nameof(IsAnimated));
        OnPropertyChanged(KeysChanged);
    }

    /// <summary>Moves a whole motion: every keyframe of the property by the same amount.</summary>
    public void OffsetKeys(KeyProperty property, double by)
    {
        var list = _keys[(int)property];
        for (var i = 0; i < list.Count; i++)
            list[i] = list[i] with { Value = list[i].Value + by };
        NotifyKeys(property);
    }

    /// <summary>
    /// Shifts all keyframes and effect keyframes/ranges in time when the clip start moves but content stays (split, head trim).
    /// </summary>
    public void ShiftKeys(double seconds)
    {
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            var list = _keys[(int)property];
            if (list.Count == 0)
                continue;

            for (var i = 0; i < list.Count; i++)
                list[i] = list[i] with { Time = Math.Round(list[i].Time + seconds, 3) };
            NotifyKeys(property);
        }

        RetimeClipKeys(k => k with { Time = Math.Round(k.Time + seconds, 3) });
        foreach (var effect in Effects)
        {
            foreach (var (key, keys) in effect.Keys.ToList())
                effect.SetKeys(key, keys.Select(k => k with { Time = Math.Round(k.Time + seconds, 3) }).ToList());
            if (effect.Range is { } range)
                effect.SetRange((Math.Max(Math.Round(range.Start + seconds, 3), 0), Math.Max(Math.Round(range.End + seconds, 3), 0.01)));
        }

        // Studio Voice mix keys are clip-relative too: shift them in a new filter list (copy-on-write; the dialog replaces lists wholesale).
        if (AudioFilters.StudioVoiceMixKeys is { Count: > 0 } mix)
        {
            var filters = AudioFilters.Clone();
            filters.StudioVoiceMixKeys = mix.Select(k => k with { Time = Math.Round(k.Time + seconds, 3) }).ToList();
            AudioFilters = filters;
        }
    }

    public void ClearKeys()
    {
        foreach (var property in Enum.GetValues<KeyProperty>())
            SetKeys(property, null);
    }

    /// <summary>Re-times the clip-relative keys kept beside the motion lanes (speed ramp, manual ducking curve) with one mapping.</summary>
    private void RetimeClipKeys(Func<Keyframe, Keyframe> retime)
    {
        if (_speedKeys.Count > 0)
            SetSpeedKeys(_speedKeys.Select(retime).ToList());
        if (_duckOverrideKeys.Count > 0)
            SetDuckOverrideKeys(_duckOverrideKeys.Select(retime).ToList());
    }

    /// <summary>The name a change of keyframes is announced under: it is not a property of its own.</summary>
    public const string KeysChanged = "Keys";

    private void NotifyKeys(KeyProperty property)
    {
        OnPropertyChanged(property switch
        {
            KeyProperty.X => nameof(AnimatesX),
            KeyProperty.Y => nameof(AnimatesY),
            KeyProperty.Scale => nameof(AnimatesScale),
            KeyProperty.Opacity => nameof(AnimatesOpacity),
            KeyProperty.TiltX => nameof(AnimatesTiltX),
            KeyProperty.TiltY => nameof(AnimatesTiltY),
            _ => nameof(AnimatesRotation),
        });
        OnPropertyChanged(nameof(IsAnimated));
        OnPropertyChanged(KeysChanged);
    }

    private static List<Keyframe>? KeysOrNull(List<Keyframe> keys) => keys.Count > 0 ? [.. keys] : null;

    /// <summary>Takes when a saved clip is on screen, and its motion: what <see cref="ApplyLook"/> leaves alone.</summary>
    public void ApplyTiming(LayerState? state)
    {
        (StartTime, Duration, MediaOffset) = (Math.Max(state?.StartTime ?? 0, 0), Math.Max(state?.Duration ?? 0, 0), Math.Max(state?.MediaOffset ?? 0, 0));
        Speed = ReadSpeed(state?.Speed ?? 1);
        (FadeInSeconds, FadeOutSeconds, TransitionIn) = (Seconds(state?.FadeInSeconds ?? 0), Seconds(state?.FadeOutSeconds ?? 0), Seconds(state?.TransitionIn ?? 0));
        (FadeInStyle, FadeOutStyle) = (Style(state?.FadeInStyle ?? 0), Style(state?.FadeOutStyle ?? 0));
        ApplyKeys(state);
    }

    /// <summary>Normalizes a stored speed; missing (legacy) means 1.</summary>
    private static double ReadSpeed(double saved) => double.IsFinite(saved) && saved > 0 ? Math.Clamp(saved, SlowestSpeed, FastestSpeed) : 1;

    private void ApplyKeys(LayerState? state)
    {
        SetKeys(KeyProperty.X, state?.KeysX);
        SetKeys(KeyProperty.Y, state?.KeysY);
        SetKeys(KeyProperty.Scale, state?.KeysScale);
        SetKeys(KeyProperty.Rotation, state?.KeysRotation);
        SetKeys(KeyProperty.Opacity, state?.KeysOpacity);
        SetKeys(KeyProperty.TiltX, state?.KeysTiltX);
        SetKeys(KeyProperty.TiltY, state?.KeysTiltY);
        SetSpeedKeys(state?.KeysSpeed);
        SetDuckOverrideKeys(state?.DuckOverrideKeys);
    }

    // ----- Ducking -----

    /// <summary>The layer's sound is turned down while a voice is speaking.</summary>
    [ObservableProperty] private bool _autoDuck;

    /// <summary>The layer's sound is a voice: what the ducked sounds make room for.</summary>
    [ObservableProperty] private bool _isVoice;

    // Manual ducking curve (Pass 5.6.5 contract): keys drawn on the ducked waveform's gain-reduction envelope, value = reduction in dB
    // (DuckOverrideFloorDb..0; 0 = not ducked) at clip-relative seconds, eased between keys. From the first key to the last they replace
    // the automatic envelope; outside them the voice side chain ducks as before. Double-clicking a node removes it. A look value
    // (LayerState.DuckOverrideKeys, one of LayerState.MotionChannels); changes raise DuckOverrideKeys (callers checkpoint first).

    /// <summary>Deepest manual duck, in dB (the Duck Amount range).</summary>
    public const double DuckOverrideFloorDb = -40;

    private readonly List<Keyframe> _duckOverrideKeys = [];

    /// <summary>Manual gain-reduction keys (dB, ascending by time); empty while the automatic envelope rules the whole clip.</summary>
    public IReadOnlyList<Keyframe> DuckOverrideKeys => _duckOverrideKeys;

    /// <summary>True while part of the ducking curve is drawn by hand.</summary>
    public bool HasDuckOverrides => _duckOverrideKeys.Count > 0;

    /// <summary>
    /// The manual reduction (dB) at a clip-relative moment, or null where the automatic envelope rules (no keys, or before the first or
    /// after the last).
    /// </summary>
    public double? DuckOverrideAt(double clipSeconds) =>
        _duckOverrideKeys is { Count: > 0 } keys && clipSeconds >= keys[0].Time && clipSeconds <= keys[^1].Time
            ? Math.Clamp(EasingCurves.ValueAt(keys, clipSeconds), DuckOverrideFloorDb, 0)
            : null;

    /// <summary>Replaces the manual keys (null or empty: none): finite only, held to DuckOverrideFloorDb..0, ascending; raises when they differ.</summary>
    public void SetDuckOverrideKeys(IEnumerable<Keyframe>? keys)
    {
        List<Keyframe> held = keys is null
            ? []
            : [.. keys.Where(k => double.IsFinite(k.Time) && double.IsFinite(k.Value))
                .Select(k => k with { Value = Math.Clamp(k.Value, DuckOverrideFloorDb, 0) }).OrderBy(k => k.Time)];
        if (held.SequenceEqual(_duckOverrideKeys))
            return;

        _duckOverrideKeys.Clear();
        _duckOverrideKeys.AddRange(held);
        OnPropertyChanged(nameof(DuckOverrideKeys));
        OnPropertyChanged(nameof(HasDuckOverrides));
    }

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

    /// <summary>
    /// Mask is in source-frame space: the layer samples the region under its source rect, so one mask serves all Cutouts and moving
    /// a Cutout keeps the mask fixed on the frame. Cutouts only.
    /// </summary>
    [ObservableProperty] private bool _maskFrame;

    /// <summary>Grows what the mask shows by about so many pixels (above 0), or shrinks it (below 0).</summary>
    [ObservableProperty] private int _maskGrow;

    /// <summary>The mask shows where its picture is black instead of where it is white.</summary>
    [ObservableProperty] private bool _maskInvert;

    /// <summary>
    /// Mask offset in percent of layer width/height (negative = left/up), at most <see cref="MaskOffsetLimit"/>; nudges one HUD mask
    /// to fit HUD variants. Uncovered edges are hidden.
    /// </summary>
    [ObservableProperty] private double _maskOffsetX;

    /// <inheritdoc cref="MaskOffsetX"/>
    [ObservableProperty] private double _maskOffsetY;

    /// <summary>
    /// Editable mask outlines as text (<see cref="Services.MaskStore"/>) in source-frame fractions; empty for imported masks.
    /// </summary>
    [ObservableProperty] private string _maskShapes = "";

    // Crop: per-edge fractions of width/height. Crop reframes (never masks): the remainder fills the box, which keeps its width and
    // takes the cropped aspect when locked; a free-height box keeps its height and fits the picture. Applied via CropFractions.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropLeftPercent))]
    private double _cropLeft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropRightPercent))]
    private double _cropRight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropTopPercent))]
    private double _cropTop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropBottomPercent))]
    private double _cropBottom;

    // Crop as percentages for UI controls; manual changes reshape the box about its anchor (KeepingAnchor).
    public double CropLeftPercent { get => Math.Round(CropLeft * 100, 1); set => KeepingAnchor(() => CropLeft = Crop(value / 100)); }
    public double CropRightPercent { get => Math.Round(CropRight * 100, 1); set => KeepingAnchor(() => CropRight = Crop(value / 100)); }
    public double CropTopPercent { get => Math.Round(CropTop * 100, 1); set => KeepingAnchor(() => CropTop = Crop(value / 100)); }
    public double CropBottomPercent { get => Math.Round(CropBottom * 100, 1); set => KeepingAnchor(() => CropBottom = Crop(value / 100)); }

    /// <summary>Some of an edge is cut away.</summary>
    public bool IsCropped => CropLeft + CropRight + CropTop + CropBottom > 0.0005;

    /// <summary>Maximum combined crop per axis, so some picture always remains.</summary>
    public const double LargestCropTotal = 0.95;

    /// <summary>
    /// Clamps crop fractions (0-1): each edge, then each axis pair to <see cref="LargestCropTotal"/> (right/bottom yield); NaN = 0.
    /// The single crop rule used by rendering, box shape and Crop controls.
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) HeldCrop(double left, double top, double right, double bottom)
    {
        static double Edge(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        (left, top) = (Math.Min(Edge(left), LargestCropTotal), Math.Min(Edge(top), LargestCropTotal));
        return (left, top, Math.Min(Edge(right), LargestCropTotal - left), Math.Min(Edge(bottom), LargestCropTotal - top));
    }

    /// <summary>A saved look's crop, held (<see cref="HeldCrop(double, double, double, double)"/>).</summary>
    public static (double Left, double Top, double Right, double Bottom) HeldCrop(LayerState look) => HeldCrop(look.CropLeft, look.CropTop, look.CropRight, look.CropBottom);

    /// <summary>
    /// Effective clamped crop (<see cref="HeldCrop(double, double, double, double)"/>); zero when not <see cref="IsCropped"/> or for
    /// pixel-less layers (adjustment, caption box).
    /// </summary>
    public (double Left, double Top, double Right, double Bottom) CropFractions =>
        IsCropped && !IsAdjustment && !IsCaptions ? HeldCrop(CropLeft, CropTop, CropRight, CropBottom) : default;

    /// <summary>
    /// Visible source region of a Cutout (<see cref="IsVideo"/>) in source-frame fractions: source rect minus crop. Also selects the
    /// region of its frame-space mask.
    /// </summary>
    public (double X, double Y, double Width, double Height) CroppedSource
    {
        get
        {
            var (left, top, right, bottom) = CropFractions;
            return (SourceX + left * SourceWidth, SourceY + top * SourceHeight, SourceWidth * (1 - left - right), SourceHeight * (1 - top - bottom));
        }
    }

    /// <summary>Cropped aspect factor (remaining height per remaining width relative to the full picture); 1 uncropped.</summary>
    private double CroppedShape
    {
        get
        {
            var (left, top, right, bottom) = CropFractions;
            return (1 - top - bottom) / (1 - left - right);
        }
    }

    /// <summary>
    /// Manual crop change: a locked-aspect box resizes about its anchor (<see cref="AnchorCenter"/>) keeping its width. Only after
    /// first layout; crops restored with a placement (ApplyPlace, undo, Apply Placement) are taken verbatim.
    /// </summary>
    private void KeepingAnchor(Action change)
    {
        var before = CroppedShape;
        change();
        var after = CroppedShape;
        if (!AnchorCenter || _heightPerWidth <= 0 || !(LockAspectRatio || SizeHeight <= 0) || Math.Abs(after - before) < 1e-12)
            return;

        var grown = _heightPerWidth * (after / before - 1);
        PositionY -= SizeWidth * grown * AnchorY;
        _heightPerWidth += grown;
    }

    /// <summary>
    /// Blur fill: true draws a frame-covering blurred copy of the video under it (Sequence blur-fill settings: radius, passes, dim,
    /// zoom, effects); false or null disables it.
    /// </summary>
    [ObservableProperty] private bool? _blurFill;

    /// <summary>The mask is taken from the frame: a piece cut from the video, with a mask of the whole frame.</summary>
    public bool UsesFrameMask => MaskFrame && IsVideo;

    /// <summary>Takes the mask off and puts back every mask setting as a new layer has it.</summary>
    public void ResetMask() =>
        (CustomMask, MaskPath, MaskFrame, MaskGrow, MaskInvert, MaskShapes, MaskOffsetX, MaskOffsetY, CropLeft, CropRight, CropTop, CropBottom, CornerRadius, Feather, FeatherRadius) =
        (false, "", false, 0, false, "", 0, 0, 0, 0, 0, 0, 0, false, 12);

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

    // Layers may extend off-frame: the corner ranges from one frame width left to one frame width right.
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

    // Style: corner radius, feather, opacity and drop shadow.

    /// <summary>Corner rounding as a percentage of the layer's shorter side: 0 is square, 50 a full half-circle.</summary>
    [ObservableProperty] private int _cornerRadius;

    /// <summary>100 is solid; lower lets the picture underneath show through.</summary>
    [ObservableProperty] private int _opacity = 100;

    [ObservableProperty] private bool _feather;
    [ObservableProperty] private int _featherRadius = 12;
    [ObservableProperty] private bool _shadow;
    [ObservableProperty] private double _shadowOpacity = 0.5;

    /// <summary>Drop shadow distance in output pixels, 0-100.</summary>
    [ObservableProperty] private int _shadowDistance = LayerState.DefaultShadowDistance;

    /// <summary>
    /// Light angle in degrees (design-tool convention): 135 = light from upper left, shadow down-right. See <see cref="ShadowShift"/>.
    /// </summary>
    [ObservableProperty] private double _shadowAngle = LayerState.DefaultShadowAngle;

    /// <summary>How far the shadow's edge is feathered, in output pixels, 0 (a hard edge) to 50.</summary>
    [ObservableProperty] private int _shadowSoftness = LayerState.DefaultShadowSoftness;

    [ObservableProperty] private string _shadowColor = "#000000";

    /// <summary>How far the shadow is moved from the layer, in output pixels: <see cref="ShadowDistance"/> along <see cref="ShadowAngle"/>.</summary>
    public (int X, int Y) ShadowShift => ShadowShiftOf(ShadowDistance, ShadowAngle);

    public static (int X, int Y) ShadowShiftOf(int distance, double angle)
    {
        var d = Math.Clamp(distance, 0, 100);
        var radians = (double.IsFinite(angle) ? angle : LayerState.DefaultShadowAngle) * Math.PI / 180;
        return ((int)Math.Round(-d * Math.Cos(radians), MidpointRounding.AwayFromZero), (int)Math.Round(d * Math.Sin(radians), MidpointRounding.AwayFromZero));
    }

    /// <summary>Copies a saved drop shadow (distance, angle, softness, colour), converting legacy single-offset shadows.</summary>
    public void TakeShadow(LayerState? state)
    {
        state ??= new LayerState();
        (Shadow, ShadowOpacity) = (state.Shadow, Math.Clamp(state.ShadowOpacity, 0, 1));
        (ShadowDistance, ShadowAngle, ShadowSoftness, ShadowColor) = (state.ReadShadowDistance(), state.ReadShadowAngle(), state.ReadShadowSoftness(), state.ReadShadowColor());
    }

    /// <summary>The width as a whole percentage of the frame width, for typing.</summary>
    public int SizePercent
    {
        get => (int)Math.Round(SizeWidth * 100);
        set => SizeWidth = Math.Clamp(value, 1, 400) / 100.0;
    }

    public bool IsUsable => !IsLoading && !IsHidden && !IsDeleted && !IsAudio && SizeWidth > 0
        && (IsText ? !string.IsNullOrWhiteSpace(TextContent) : IsFile ? ImagePath.Length > 0 : SourceWidth > 0 && SourceHeight > 0);

    /// <summary>The source rectangle in pixels of a source of the given size.</summary>
    public (int X, int Y, int Width, int Height) GetSourceRect(int sourceWidth, int sourceHeight) =>
        ((int)Math.Round(SourceX * sourceWidth), (int)Math.Round(SourceY * sourceHeight),
            Math.Max(Even(SourceWidth * sourceWidth), 2), Math.Max(Even(SourceHeight * sourceHeight), 2));

    /// <summary>
    /// Output-pixel rectangle for a frame of the given size. Source size determines locked aspect; 16:9 is assumed when unknown.
    /// </summary>
    public (int X, int Y, int Width, int Height) GetOutputRect(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight)
    {
        var rect = ComputeOutputRect(frameWidth, frameHeight, sourceWidth, sourceHeight);
        if (frameWidth > 0 && frameHeight > 0)
            _heightPerWidth = ((double)rect.Height / frameHeight) / ((double)rect.Width / frameWidth);
        return rect;
    }

    /// <summary>
    /// Side-effect-free output rectangle, safe for lowered layers read concurrently by preview, program strip and export. Source size
    /// determines locked aspect; 16:9 when unknown.
    /// </summary>
    public (int X, int Y, int Width, int Height) ComputeOutputRect(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight)
    {
        var width = Math.Max(Even(SizeWidth * frameWidth), 2);

        var height = LockAspectRatio || SizeHeight <= 0
            ? GetProportionalHeight(width, sourceWidth, sourceHeight)
            : Math.Max(Even(SizeHeight * frameHeight), 2);
        return ((int)Math.Round(PositionX * frameWidth), (int)Math.Round(PositionY * frameHeight), width, height);
    }

    /// <summary>
    /// Height preserving the layer's aspect at a given width: source rect or picture aspect after crop. Converted legacy clips with an
    /// explicit height keep it (KeepsShape).
    /// </summary>
    private int GetProportionalHeight(int width, int sourceWidth, int sourceHeight)
    {
        var cropped = CroppedShape;
        if (IsFile || IsText)
            return ImageWidth > 0 && ImageHeight > 0 ? Math.Max(Even((double)width * ImageHeight / ImageWidth * cropped), 2) : Math.Max(Even(width * cropped), 2);

        var (_, _, cutWidth, cutHeight) = GetSourceRect(sourceWidth > 0 ? sourceWidth : 1920, sourceHeight > 0 ? sourceHeight : 1080);
        return Math.Max(Even((double)width * cutHeight / cutWidth * cropped), 2);
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
}
