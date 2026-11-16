using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

// A layer to and from the state that presets and projects keep (ToState, FromState), and that state (LayerState).
public sealed partial class Layer
{
    /// <summary>
    /// Unknown look fields (<see cref="LayerState.Extra"/>) captured by <see cref="FromState"/> and re-emitted by <see cref="ToState"/>
    /// so edits do not drop them. Immutable after read.
    /// </summary>
    internal Dictionary<string, System.Text.Json.JsonElement>? UnknownState { get; init; }

    public LayerState ToState() => new()
    {
        AudioStream = AudioStream,
        Name = Name,
        Kind = Kind,
        ImagePath = ImagePath,
        ImageWidth = ImageWidth,
        ImageHeight = ImageHeight,
        TextContent = TextContent,
        FontFamily = FontFamily,
        FontSize = FontSize,
        FontColor = FontColor,
        OutlineColor = OutlineColor,
        OutlineThickness = OutlineThickness,
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
        ShadowDistance = ShadowDistance,
        ShadowAngle = ShadowAngle,
        ShadowSoftness = ShadowSoftness,
        ShadowColor = ShadowColor,
        ChromaKey = ChromaKey,
        ChromaColor = ChromaColor,
        ChromaSimilarity = ChromaSimilarity,
        ChromaBlend = ChromaBlend,
        CustomMask = CustomMask,
        MaskPath = MaskPath,
        MaskFrame = MaskFrame,
        MaskGrow = MaskGrow,
        MaskInvert = MaskInvert,
        MaskShapes = MaskShapes,
        MaskOffsetX = MaskOffsetX,
        MaskOffsetY = MaskOffsetY,
        CropLeft = CropLeft,
        CropRight = CropRight,
        CropTop = CropTop,
        CropBottom = CropBottom,
        BlurFill = BlurFill,
        StartTime = StartTime,
        Duration = Duration,
        MediaOffset = MediaOffset,
        Speed = Speed,
        SmoothSlowMo = SmoothSlowMo ? true : null,
        KeepPitch = KeepPitch ? null : false,
        SpectralAudio = SpectralAudio ? true : null,
        Extra = UnknownState,
        IsHidden = IsHidden,
        ProxyChoice = ProxyChoice,
        IsEmptyTrack = IsEmptyTrack,
        IsDeleted = IsDeleted,
        HasAudio = HasAudio,
        AudioOffset = AudioOffset,
        TrackId = TrackId,
        MediaDuration = MediaDuration,
        AudioGainDb = AudioGainDb,
        FadeInSeconds = FadeInSeconds,
        FadeOutSeconds = FadeOutSeconds,
        FadeInStyle = FadeInStyle,
        FadeOutStyle = FadeOutStyle,
        TransitionIn = TransitionIn,
        Rotation = Rotation,
        TiltX = TiltX,
        TiltY = TiltY,
        AnchorX = AnchorX,
        AnchorY = AnchorY,
        Effects = [.. Effects.Select(e => e.ToState())],
        EffectsBypassed = EffectsBypassed,
        AutoDuck = AutoDuck,
        IsVoice = IsVoice,
        IsSoundMuted = IsSoundMuted,
        AudioCodec = AudioCodec,
        AudioChannels = AudioChannels,
        AudioFilters = AudioFilters.IsDefault ? null : AudioFilters.Clone(),
        KeysX = KeysOrNull(_keys[(int)KeyProperty.X]),
        KeysY = KeysOrNull(_keys[(int)KeyProperty.Y]),
        KeysScale = KeysOrNull(_keys[(int)KeyProperty.Scale]),
        KeysRotation = KeysOrNull(_keys[(int)KeyProperty.Rotation]),
        KeysOpacity = KeysOrNull(_keys[(int)KeyProperty.Opacity]),
        KeysTiltX = KeysOrNull(_keys[(int)KeyProperty.TiltX]),
        KeysTiltY = KeysOrNull(_keys[(int)KeyProperty.TiltY]),
    };

    /// <summary>How far a mask can be grown or shrunk, in pixels.</summary>
    public const int MaskGrowLimit = 40;

    /// <summary>How far the mask picture can be moved either way, in percent of the layer's width or height.</summary>
    public const double MaskOffsetLimit = 50;

    /// <summary>A saved mask offset, held to the limit (0 when it is not a number).</summary>
    public static double MaskOffset(double value) => double.IsFinite(value) ? Math.Clamp(value, -MaskOffsetLimit, MaskOffsetLimit) : 0;

    /// <summary>A saved length of a fade or a crossfade: not below 0, nor beyond a minute.</summary>
    private static double Seconds(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 60) : 0;

    /// <summary>Validated transition style (unknown values map to dissolve).</summary>
    private static TransitionStyle Style(TransitionStyle saved) => Enum.IsDefined(saved) ? saved : TransitionStyle.Dissolve;

    /// <summary>A saved crop, held to what leaves something of the layer: at most 95% from one edge.</summary>
    private static double Crop(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 0.95) : 0;

    /// <summary>Applies a saved look (style, mask, key, filters, rotation), keeping position, size and timing.</summary>
    public void ApplyLook(LayerState? state)
    {
        state ??= new LayerState();
        (CornerRadius, Opacity) = (Math.Clamp(state.CornerRadius, 0, 50), Math.Clamp(state.Opacity, 0, 100));
        (Feather, FeatherRadius) = (state.Feather, state.FeatherRadius);
        TakeShadow(state);
        (ChromaKey, ChromaColor) = (state.ChromaKey, string.IsNullOrWhiteSpace(state.ChromaColor) ? "#00FF00" : state.ChromaColor);
        (ChromaSimilarity, ChromaBlend) = (Math.Clamp(state.ChromaSimilarity, 0.01, 1), Math.Clamp(state.ChromaBlend, 0, 1));
        (CustomMask, MaskPath) = (state.CustomMask, state.MaskPath ?? "");
        (MaskFrame, MaskGrow, MaskInvert, MaskShapes) = (state.MaskFrame, Math.Clamp(state.MaskGrow, -MaskGrowLimit, MaskGrowLimit), state.MaskInvert, state.MaskShapes ?? "");
        (MaskOffsetX, MaskOffsetY) = (MaskOffset(state.MaskOffsetX), MaskOffset(state.MaskOffsetY));
        (CropLeft, CropRight, CropTop, CropBottom) = (Crop(state.CropLeft), Crop(state.CropRight), Crop(state.CropTop), Crop(state.CropBottom));
        Rotation = Math.Clamp(state.Rotation, -360, 360);
        (TiltX, TiltY) = (Tilt(state.TiltX), Tilt(state.TiltY));
        (AnchorX, AnchorY) = (Anchor(state.AnchorX), Anchor(state.AnchorY));
        TakeSpeedOptions(state);
        LoadEffects(state);
    }

    /// <summary>Takes where a piece is in the source, from a state written over the clip's.</summary>
    public void ApplySource(LayerState state) =>
        (SourceX, SourceY, SourceWidth, SourceHeight) = (state.SourceX, state.SourceY, state.SourceWidth, state.SourceHeight);

    /// <summary>
    /// Applies saved placement: corner, size, shape, rotation, anchor, crop and blur fill. The corner lands exactly as saved regardless
    /// of resize anchoring.
    /// </summary>
    public void ApplyPlace(LayerState state)
    {
        FromCorner(() =>
        {
            LockAspectRatio = state.LockAspectRatio;
            (SizeWidth, SizeHeight) = (state.SizeWidth, state.SizeHeight);
            (PositionX, PositionY) = (state.PositionX, state.PositionY);
        });
        Rotation = Math.Clamp(state.Rotation, -360, 360);
        (TiltX, TiltY) = (Tilt(state.TiltX), Tilt(state.TiltY));
        (AnchorX, AnchorY) = (Anchor(state.AnchorX), Anchor(state.AnchorY));
        (CropLeft, CropRight, CropTop, CropBottom) = (Crop(state.CropLeft), Crop(state.CropRight), Crop(state.CropTop), Crop(state.CropBottom));
        BlurFill = state.BlurFill;
    }

    /// <summary>
    /// Rebuilds a layer from saved state, converting legacy pixel positions with the given sizes (default 1920 x 1080 / 1080 x 1920).
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
            TextContent = state.TextContent ?? "",
            FontFamily = string.IsNullOrWhiteSpace(state.FontFamily) ? TextDefaults.Font : state.FontFamily,
            FontSize = double.IsFinite(state.FontSize) && state.FontSize > 0
                ? Math.Clamp(state.FontSize, TextDefaults.SmallestSize, TextDefaults.LargestSize)
                : TextDefaults.Size,
            FontColor = string.IsNullOrWhiteSpace(state.FontColor) ? "#FFFFFF" : state.FontColor,
            OutlineColor = string.IsNullOrWhiteSpace(state.OutlineColor) ? "#000000" : state.OutlineColor,
            OutlineThickness = Math.Clamp(state.OutlineThickness, 0, TextDefaults.LargestOutline),
            CornerRadius = Math.Clamp(state.CornerRadius, 0, 50),
            Opacity = Math.Clamp(state.Opacity, 0, 100),
            LockAspectRatio = state.LockAspectRatio,
            Feather = state.Feather,
            FeatherRadius = state.FeatherRadius,
            Shadow = state.Shadow,
            ShadowOpacity = state.ShadowOpacity,
            ShadowDistance = state.ReadShadowDistance(),
            ShadowAngle = state.ReadShadowAngle(),
            ShadowSoftness = state.ReadShadowSoftness(),
            ShadowColor = state.ReadShadowColor(),
            ChromaKey = state.ChromaKey,
            ChromaColor = string.IsNullOrWhiteSpace(state.ChromaColor) ? "#00FF00" : state.ChromaColor,
            ChromaSimilarity = Math.Clamp(state.ChromaSimilarity, 0.01, 1),
            ChromaBlend = Math.Clamp(state.ChromaBlend, 0, 1),
            CustomMask = state.CustomMask,
            MaskPath = state.MaskPath ?? "",
            MaskFrame = state.MaskFrame,
            MaskGrow = Math.Clamp(state.MaskGrow, -MaskGrowLimit, MaskGrowLimit),
            MaskInvert = state.MaskInvert,
            MaskShapes = state.MaskShapes ?? "",
            MaskOffsetX = MaskOffset(state.MaskOffsetX),
            MaskOffsetY = MaskOffset(state.MaskOffsetY),
            CropLeft = Crop(state.CropLeft),
            CropRight = Crop(state.CropRight),
            CropTop = Crop(state.CropTop),
            CropBottom = Crop(state.CropBottom),
            BlurFill = state.BlurFill,
            StartTime = Math.Max(state.StartTime, 0),
            Duration = Math.Max(state.Duration, 0),
            MediaOffset = Math.Max(state.MediaOffset, 0),
            AudioStream = state.AudioStream,
            Speed = ReadSpeed(state.Speed),
            SmoothSlowMo = state.SmoothSlowMo == true,
            KeepPitch = state.KeepPitch != false,
            SpectralAudio = state.SpectralAudio == true,
            UnknownState = state.Extra,
            IsHidden = state.IsHidden,
            ProxyChoice = state.ProxyChoice,
            IsEmptyTrack = state.IsEmptyTrack,
            HasAudio = state.HasAudio,
            AudioOffset = state.AudioOffset,
            TrackId = state.TrackId,
            MediaDuration = Math.Max(state.MediaDuration, 0),
            AudioGainDb = Math.Clamp(state.AudioGainDb, -40, 24),
            FadeInSeconds = Seconds(state.FadeInSeconds),
            FadeOutSeconds = Seconds(state.FadeOutSeconds),
            FadeInStyle = Style(state.FadeInStyle),
            FadeOutStyle = Style(state.FadeOutStyle),
            TransitionIn = Seconds(state.TransitionIn),
            Rotation = Math.Clamp(state.Rotation, -360, 360),
            TiltX = Tilt(state.TiltX),
            TiltY = Tilt(state.TiltY),
            AnchorX = Anchor(state.AnchorX),
            AnchorY = Anchor(state.AnchorY),
            AutoDuck = state.AutoDuck,
            IsVoice = state.IsVoice,
            IsSoundMuted = state.IsSoundMuted,
            AudioCodec = state.AudioCodec ?? "",
            AudioChannels = state.AudioChannels ?? "",
            AudioFilters = state.AudioFilters?.Clone() ?? new TrackAudioFilters(),
        };
        region.ApplyKeys(state);
        region.LoadEffects(state);

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
    /// <summary>
    /// Shallow copy sharing lists (keys, effects, filters) for building a layer from a modified look via <see cref="Layer.FromState"/>
    /// (which copies the lists).
    /// </summary>
    internal LayerState ShallowCopy() => (LayerState)MemberwiseClone();

    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public string Name { get; set; } = "Layer";

    public LayerKind Kind { get; set; }
    public string ImagePath { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    // A text layer's words and font. Files from before there were text layers have none of these, and get the defaults.
    public string TextContent { get; set; } = "";
    public string FontFamily { get; set; } = TextDefaults.Font;
    public double FontSize { get; set; } = TextDefaults.Size;
    public string FontColor { get; set; } = "#FFFFFF";
    public string OutlineColor { get; set; } = "#000000";
    public int OutlineThickness { get; set; } = 2;

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

    /// <summary>Legacy two-axis shadow offset; read into the distance, never written.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ShadowOffset { get; set; }

    /// <summary>Null in legacy layers (use <see cref="ShadowOffset"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ShadowDistance { get; set; }

    public double ShadowAngle { get; set; } = DefaultShadowAngle;

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ShadowSoftness { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ShadowColor { get; set; }

    // A new shadow: 14 px at 135 degrees is the 10 px right and 10 px down every shadow had before, now feathered by 8 px.
    public const int DefaultShadowDistance = 14;
    public const double DefaultShadowAngle = 135;
    public const int DefaultShadowSoftness = 8;

    /// <summary>Softness for legacy shadows: their fixed 2 px blur.</summary>
    private const int EarlierShadowSoftness = 2;

    /// <summary>
    /// Shadow distance, or the equivalent distance for a legacy two-axis offset (at 135 degrees, offset 10 becomes distance 14).
    /// </summary>
    public int ReadShadowDistance() =>
        ShadowDistance is { } distance ? Math.Clamp(distance, 0, 100)
        : ShadowOffset is { } offset ? Math.Clamp((int)Math.Round(Math.Max(offset, 0) * Math.Sqrt(2), MidpointRounding.AwayFromZero), 0, 100)
        : DefaultShadowDistance;

    public double ReadShadowAngle() => ShadowDistance is null || !double.IsFinite(ShadowAngle) ? DefaultShadowAngle : ((ShadowAngle % 360) + 360) % 360;

    public int ReadShadowSoftness() => Math.Clamp(ShadowSoftness ?? (ShadowDistance is null && ShadowOffset is not null ? EarlierShadowSoftness : DefaultShadowSoftness), 0, 50);

    public string ReadShadowColor() => string.IsNullOrWhiteSpace(ShadowColor) ? "#000000" : ShadowColor;

    public bool ChromaKey { get; set; }
    public string ChromaColor { get; set; } = "#00FF00";
    public double ChromaSimilarity { get; set; } = 0.15;
    public double ChromaBlend { get; set; } = 0.05;
    public bool CustomMask { get; set; }
    public string MaskPath { get; set; } = "";
    public bool MaskFrame { get; set; }
    public int MaskGrow { get; set; }
    public bool MaskInvert { get; set; }
    public string? MaskShapes { get; set; }

    // Percent of layer width/height; omitted at 0 so older projects read unchanged.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public double MaskOffsetX { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public double MaskOffsetY { get; set; }
    public double CropLeft { get; set; }
    public double CropRight { get; set; }
    public double CropTop { get; set; }
    public double CropBottom { get; set; }

    /// <summary>Clip blur fill (<see cref="Layer.BlurFill"/>); null (kind default) is omitted.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? BlurFill { get; set; }

    public double StartTime { get; set; }
    public double Duration { get; set; }
    public double MediaOffset { get; set; }
    public double Speed { get; set; } = 1;

    // Speed & Time card options (Layer.Speed.cs): null = defaults (Smooth Slow-Mo off, Keep Pitch on, Spectral Audio off), omitted
    // from the file so older projects read unchanged.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? SmoothSlowMo { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? KeepPitch { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? SpectralAudio { get; set; }

    public bool IsHidden { get; set; }
    public ProxyChoice ProxyChoice { get; set; }
    public bool IsEmptyTrack { get; set; }
    public bool IsDeleted { get; set; }
    public bool HasAudio { get; set; }

    /// <summary>Audio stream index of a lowered clip; owned by TimelineClip.AudioStreamIndex, never saved here.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int AudioStream { get; set; }
    public double AudioOffset { get; set; }

    public int TrackId { get; set; }
    public double MediaDuration { get; set; }
    public double AudioGainDb { get; set; }
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public TransitionStyle FadeInStyle { get; set; }
    public TransitionStyle FadeOutStyle { get; set; }
    public double TransitionIn { get; set; }
    public double Rotation { get; set; }

    /// <summary>3D pitch and yaw in degrees (Layer.TiltX); 0 (omitted) for flat layers.</summary>
    public double TiltX { get; set; }
    public double TiltY { get; set; }

    /// <summary>The anchor point (see Layer.AnchorX); what was saved before it was there has the middle.</summary>
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
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

    /// <summary>
    /// Ordered effect stack (Models/Effects). Null in legacy files: flips, filters, key, mask, edges and shadow are converted to cards in
    /// original order. The legacy Flip/Filter fields are read only then and written as defaults.
    /// </summary>
    public List<EffectState>? Effects { get; set; }

    /// <summary>Every effect of the clip set aside at once (the Effects pane's Bypass All), without losing them.</summary>
    public bool EffectsBypassed { get; set; }

    public bool AutoDuck { get; set; }
    public bool IsVoice { get; set; }
    public bool IsSoundMuted { get; set; }
    public string AudioCodec { get; set; } = "";
    public string AudioChannels { get; set; } = "";

    /// <summary>The sound's filters; left out while none is switched on.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TrackAudioFilters? AudioFilters { get; set; }

    // The motion: the keyframes of each animated property; null for one that is not animated.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? KeysX { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? KeysY { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? KeysScale { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? KeysRotation { get; set; }
    public List<Keyframe>? KeysOpacity { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? KeysTiltX { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? KeysTiltY { get; set; }

    /// <summary>
    /// All keyframed motion channels of a look. Timing transforms (split, join, trim shift, speed change; TimelineTransaction.Looks)
    /// iterate this list; golden motion-channels fails if any <see cref="List{Keyframe}"/> property is missing.
    /// </summary>
    internal static readonly MotionChannel[] MotionChannels =
    [
        new(nameof(KeysX), s => s.KeysX, (s, k) => s.KeysX = k),
        new(nameof(KeysY), s => s.KeysY, (s, k) => s.KeysY = k),
        new(nameof(KeysScale), s => s.KeysScale, (s, k) => s.KeysScale = k),
        new(nameof(KeysRotation), s => s.KeysRotation, (s, k) => s.KeysRotation = k),
        new(nameof(KeysOpacity), s => s.KeysOpacity, (s, k) => s.KeysOpacity = k),
        new(nameof(KeysTiltX), s => s.KeysTiltX, (s, k) => s.KeysTiltX = k),
        new(nameof(KeysTiltY), s => s.KeysTiltY, (s, k) => s.KeysTiltY = k),
    ];

    /// <summary>One keyframed channel of a look: its property name, and how to read and write its keys.</summary>
    internal sealed record MotionChannel(string Name, Func<LayerState, List<Keyframe>?> Get, Action<LayerState, List<Keyframe>?> Set);

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
