using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

/// <summary>What a layer shows.</summary>
public enum LayerKind
{
    /// <summary>
    /// A rectangle cut out of "the video" (a minimap, an ammo counter): a Game Preset's or a template's Cutout piece, which becomes
    /// a video-file clip of the clip it goes on, cut to the same part of its picture (MainViewModel.FileCutoutOf). No clip on the
    /// timeline is of this kind since 5.2-AuditB: a project that holds one (a cut-out of the old main video) is of the shape before
    /// then, and is converted as it is opened (LegacyMainVideo, MainViewModel.ConvertLegacy). Kinds are saved by name.
    /// </summary>
    Video,

    /// <summary>A picture from a file (a logo, a frame, a caption).</summary>
    Image,

    /// <summary>The box the auto-captions are drawn in. There is one, and it is always the top layer.</summary>
    Captions,

    /// <summary>A video from a file, read at its own moments: every video clip of the Sequence, on any track (5.2-AuditB).</summary>
    VideoFile,

    /// <summary>
    /// Legacy: the main video, the primary file's chain of clips; nothing makes it any more (5.2-AuditB). Kept only so that a project
    /// that holds it (a 5.1 project) is read and converted as it is opened (LegacyMainVideo, MainViewModel.ConvertLegacy), presets that
    /// name it still read, and the golden tests' 4.2.0-shaped fixtures can still be made of it (converted the same way).
    /// </summary>
    MainVideo,

    /// <summary>
    /// Legacy: the blurred copy of the main video that filled the frame behind everything. A clip's blur fill is its own now
    /// (<see cref="Layer.BlurFill"/>, 5.2-AuditB); nothing draws this kind. Kept so old projects and presets still read, and
    /// as the kind of the sequence's blur-fill settings row while the view model keeps one.
    /// </summary>
    Background,

    /// <summary>A sound from a file, with no picture: it has a place in time and nothing else.</summary>
    Audio,

    /// <summary>Words typed in HandPeg (a title, a label), drawn in a font of the computer's. Placed, sized, turned and animated like a picture.</summary>
    Text,

    /// <summary>A clip with no picture of its own: its effect stack works on everything under it while it lasts, within its rectangle.</summary>
    Adjustment,
}

/// <summary>
/// One point of a motion: a value at a moment, counted in seconds from where its clip begins, and how the
/// clip moves on from it to the next keyframe.
/// </summary>
/// <remarks>A value, not an object: a motion's keyframes lie side by side in their list, and none is a thing for the collector to follow.
/// Read from JSON through its constructor, so that a keyframe saved without an easing gets the default one, as it always did.
/// The four Bézier numbers are a Custom Bézier's handles (see <see cref="EasingCurves"/>); they are left out of JSON while 0.</remarks>
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
    /// <summary>The same keyframe leaving by another easing (a Custom Bézier with these handles), the whole of its curve.</summary>
    public Keyframe WithEasing(EasingType easing, (double X1, double Y1, double X2, double Y2)? handles = null) => handles is { } h
        ? this with { Easing = easing, BezierX1 = h.X1, BezierY1 = h.Y1, BezierX2 = h.X2, BezierY2 = h.Y2, PartStart = 0, PartEnd = 0 }
        : this with { Easing = easing, PartStart = 0, PartEnd = 0 };

    /// <summary>
    /// Whether the stretch this keyframe leaves is a part of its easing's curve (4.3D): what is left on one side of a split
    /// made between two keyframes (EasingCurves.Cut), so the half moves exactly as the whole did.
    /// </summary>
    public bool IsPart => PartEnd > PartStart && (PartStart > 0 || PartEnd < 1);

    /// <summary>The part of the curve the stretch is, as fractions of the curve's time: (0, 1) for the whole of it.</summary>
    public (double Start, double End) Part => IsPart ? (PartStart, PartEnd) : (0, 1);

    /// <summary>The same keyframe leaving by the part of its curve between two fractions of its time (the whole curve: 0 to 1).</summary>
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

    /// <summary>Degrees it is tilted about its horizontal axis (5.2A, Pitch): its top away from the viewer, -60 to 60.</summary>
    TiltX,

    /// <summary>Degrees it is swung about its upright axis (5.2A, Yaw): its right edge away from the viewer, -60 to 60.</summary>
    TiltY,
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Description))]
    private string _name = "Layer";

    public LayerKind Kind { get; init; }

    /// <summary>The picture of an image layer.</summary>
    public string ImagePath { get; init; } = "";

    // The picture's own size in pixels, which gives a locked image layer its shape.
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>
    /// Dropped onto the timeline a moment ago, and still being looked at: its block is there, but its size,
    /// its length and whether it has sound are not known yet, and nothing of it is rendered until they are.
    /// </summary>
    [ObservableProperty] private bool _isLoading;

    public bool IsImage => Kind == LayerKind.Image;

    public bool IsText => Kind == LayerKind.Text;

    /// <summary>One picture that does not change as time passes: an image from a file, or words.</summary>
    public bool IsStill => IsImage || IsText;

    // ----- Text -----
    // A text layer's words and how they are drawn. Its box on the frame is the words' own: it is measured from
    // them (see TextLayout), kept in ImageWidth and ImageHeight as a picture's size is, and gives the layer its shape.

    /// <summary>The words. A line break starts a new line.</summary>
    [ObservableProperty] private string _textContent = "";

    /// <summary>The font, by the name Windows lists it under.</summary>
    [ObservableProperty] private string _fontFamily = HandPegApp.Services.TextLayout.DefaultFont;

    /// <summary>The font's size in pixels of the output frame, before the layer is scaled.</summary>
    [ObservableProperty] private double _fontSize = HandPegApp.Services.TextLayout.DefaultSize;

    /// <summary>The colour of the letters, as #RRGGBB.</summary>
    [ObservableProperty] private string _fontColor = "#FFFFFF";

    /// <summary>The colour of the line drawn around the letters, as #RRGGBB.</summary>
    [ObservableProperty] private string _outlineColor = "#000000";

    /// <summary>How thick that line is, in pixels at the font's set size; 0 for none.</summary>
    [ObservableProperty] private int _outlineThickness = 2;

    /// <summary>What of a text layer decides the size of its box: when one of these changes, the box is measured again.</summary>
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

    /// <summary>Whether its track can be hidden (the eye on its row, Hide on its menu): the captions have their own switch.</summary>
    public bool CanHide => IsRemovable || IsBackground;

    /// <summary>Can have a color keyed out of it: the pictures and videos.</summary>
    public bool HasKeying => IsPicture && !IsCaptions;

    /// <summary>Can have a mask of its own laid on it: those, and the caption box.</summary>
    public bool HasMask => IsPicture;

    /// <summary>Can be given filters of its own, and be turned: every picture but the caption box.</summary>
    public bool HasFilters => IsPicture && !IsCaptions;

    // Tracks: a row of the timeline. A layer is one clip on its track; splitting it makes a second clip on the
    // same track. Everything a clip looks like is its own (5.1-DogfoodPolish): place, mask, matte, key and effects alike.

    /// <summary>Which track the clip is on. Clips with the same number share a row.</summary>
    [ObservableProperty] private int _trackId;

    /// <summary>
    /// The clip of the timeline document this layer is the view of (4.3C: MainViewModel.Document); empty for a layer that
    /// is no clip (the caption box, the background, an empty track's row, a layer of a preset or of the graph builder).
    /// </summary>
    public Guid ClipId { get; set; }

    /// <summary>
    /// What a speed set in a box (<see cref="ClipSpeed"/>) does to a layer that is a clip's view: an edit of the clip, which
    /// the view then follows. Null for a layer that is no clip's, which changes its own speed (<see cref="SetSpeed"/>).
    /// </summary>
    public Action<Layer, double>? SpeedEditor { get; set; }

    // When the layer is on screen, in seconds of the source's own time.

    /// <summary>When the layer appears.</summary>
    [ObservableProperty] private double _startTime;

    /// <summary>How long it stays; 0 for until the end of the video.</summary>
    [ObservableProperty] private double _duration;

    /// <summary>For a video layer: how far into its own video it is when it appears. Set when a layer is split, so the second part carries on where the first left off.</summary>
    [ObservableProperty] private double _mediaOffset;

    // Speed: how fast the clip's own video or sound runs. Its length on the timeline is the length of what
    // it plays divided by its speed, so a clip that is slowed down is longer and one that is sped up shorter.

    public const double SlowestSpeed = 0.1, FastestSpeed = 10;

    /// <summary>How many times as fast as recorded the clip runs: 1 as it is, 0.5 at half speed, 2 at double.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipSpeed))]
    private double _speed = 1;

    /// <summary>Has a running time of its own that can be played faster or slower: a video from a file, or a sound.</summary>
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

    /// <summary>Says again what the speed is, for a box that asked for one that could not be (its clip's edit was refused).</summary>
    public void NotifySpeed() => OnPropertyChanged(nameof(ClipSpeed));

    /// <summary>
    /// Changes the speed, and with it everything that is counted in the timeline's seconds: the clip plays the
    /// same stretch of its file as before, so it is as much shorter or longer as it is faster or slower, and
    /// its keyframes stay on the frames they were set on.
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

        Speed = value;
        if (length > 0.001)
            Duration = Math.Max(Math.Round(length * stretch, 3), 0.05);
    }

    /// <summary>Kept in the list but left out of the picture, and (for a video layer) out of the sound.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Action))]
    [NotifyPropertyChangedFor(nameof(IsMuted))]
    private bool _isHidden;

    /// <summary>In the recycle bin: taken off the timeline, and kept so that it can be put back.</summary>
    [ObservableProperty] private bool _isDeleted;

    /// <summary>Whether Live Preview plays this clip's file from a proxy: when it is heavy (Auto), always (Make Proxy), or never (Use Original).</summary>
    [ObservableProperty] private ProxyChoice _proxyChoice;

    /// <summary>
    /// A track with nothing on it yet (Add Layer's click): it stands for the track in the list, takes the first clip dropped
    /// onto it (which takes its place), and draws nothing and goes into no graph.
    /// </summary>
    [ObservableProperty] private bool _isEmptyTrack;

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainDb))]
    private double _audioGainDb;

    /// <summary>
    /// How long the clip takes to come on at its start, in seconds of the timeline: its picture in its
    /// <see cref="FadeInStyle"/> over whatever is under it, and its sound up from silence. The handle at the block's
    /// top-left corner.
    /// </summary>
    [ObservableProperty] private double _fadeInSeconds;

    /// <summary>How long it takes to go before its end, picture and sound (the handle at the block's top-right corner).</summary>
    [ObservableProperty] private double _fadeOutSeconds;

    /// <summary>How the picture comes on over its fade in: a dissolve, a wipe, a slide, a zoom, a blur.</summary>
    [ObservableProperty] private TransitionStyle _fadeInStyle;

    /// <summary>How the picture goes over its fade out.</summary>
    [ObservableProperty] private TransitionStyle _fadeOutStyle;

    /// <summary>
    /// On a copy a graph is built from only (never saved): the picture stays as it is through its fade in (or out), only
    /// its sound fading, because the clip laid over it fades in (or out) across the same stretch and carries the
    /// picture's side of that crossfade. Set by the lowering of the timeline (Services/Gpu/TimelineLowering.cs:
    /// LayOverAcrossCut and PairLayerCrossfades).
    /// </summary>
    public bool PictureFadeInOff { get; set; }

    public bool PictureFadeOutOff { get; set; }

    /// <summary>
    /// A crossfade from the clip just before it on its track (the one whose end meets its start), this many seconds long,
    /// centred on the cut: both run on into what their files have beyond it, the picture dissolving from the one to the
    /// other and the sound crossfading. 0 for a plain cut. Laid out by TimelineLowering (LayOverAcrossCut).
    /// </summary>
    [ObservableProperty] private double _transitionIn;

    // ----- As a row of the Audio tab -----
    // A sound added from a file is listed there with the video's own audio tracks, in rows made from one
    // template. These are the names that template binds to, which an audio track has too (see AudioTrack):
    // some are this clip's own settings under the track's name for them, and the rest say that a clip has
    // no such thing (a codec of its own, filters, a solo button), so that the row leaves the place empty.

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

    /// <summary>A voiceover recorded in HandPeg: its file is in the Voiceovers folder.</summary>
    public bool IsVoiceoverRecording =>
        IsAudio && ImagePath.StartsWith(HandPegApp.Services.AppPaths.Voiceovers + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the sound's stream is encoded as in its file (aac, pcm_s16le...), when that is known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private string _audioCodec = "";

    /// <summary>Its channels as FFmpeg names them (stereo, mono, 5.1...), when known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private string _audioChannels = "";

    /// <summary>
    /// The second line of its row: what it is (a recorded voiceover, the sound of a video clip, the first
    /// stream of a file), what the clip was named when that is not its file's name, and its format.
    /// </summary>
    public string Description
    {
        get
        {
            var origin = IsVoiceoverRecording ? "Voiceover Recording" : IsVideoFile ? "Stream #1 (video clip)" : "Stream #1";
            if (!IsVideoFile && Name != SourceFileName && !string.IsNullOrWhiteSpace(Name))
                origin += $" ({Name})";
            var format = AudioCodec.Length > 0 ? (AudioChannels.Length > 0 ? $"{AudioCodec}, {AudioChannels}" : AudioCodec) : "mixed into the first track";
            return $"{origin}  \u00B7  {format}";
        }
    }

    public IReadOnlyList<string> Actions => ClipActions;

    /// <summary>The sound is left out, while a video layer's picture stays. (A sound clip is simply hidden.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Action))]
    [NotifyPropertyChangedFor(nameof(IsMuted))]
    private bool _isSoundMuted;

    /// <summary>Mixed in, or left out. For a sound clip that is the same as hiding it; a video layer keeps its picture.</summary>
    /// <summary>Muted: left out of the mix. For the Mute button of its row; the same as its Action being Ignore / Drop.</summary>
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

    // It has no stream of its own in the output: it goes into the first track, and is encoded as that is.
    // The format boxes of its row say so, and cannot be changed.
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

    /// <summary>The sound's processing: compressor, gate, equalizer and so on, as an audio track has. Replaced as a whole by its dialog.</summary>
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

    /// <summary>The picture behind a track's whole row. A clip's waveform is drawn on its block instead.</summary>
    public System.Windows.Media.ImageSource? TrackWaveform => null;

    // Turning and mirroring.

    /// <summary>Degrees the picture is turned clockwise about its middle.</summary>
    [ObservableProperty] private double _rotation;

    /// <summary>
    /// True 3D (5.2A): degrees the picture is tilted about its horizontal axis through the anchor point (Pitch: its top away from the viewer),
    /// drawn in perspective by the compositor. With <see cref="TiltY"/> and <see cref="Rotation"/> (the Roll), its Yaw / Pitch / Roll.
    /// </summary>
    [ObservableProperty] private double _tiltX;

    /// <summary>True 3D (5.2A): degrees the picture is swung about its upright axis through the anchor point (Yaw: its right edge away from the viewer).</summary>
    [ObservableProperty] private double _tiltY;

    /// <summary>How far a picture can be tilted either way (5.2A): past it, it is seen too nearly edge-on to read.</summary>
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

    // Mirroring and the layer's own filters are cards of its effect stack (Layer.Effects.cs): FlipHorizontal,
    // FilterContrast and the rest are views onto those cards.

    /// <summary>Whether any filter of the layer's own is set to something.</summary>
    public bool HasFilterChanges => Math.Abs(FilterContrast - 1) >= 0.005 || Math.Abs(FilterBrightness) >= 0.005 || Math.Abs(FilterSaturation - 1) >= 0.005
        || Math.Abs(FilterGamma - 1) >= 0.005 || Math.Abs(FilterHue) >= 0.05 || FilterSharpen >= 0.005 || FilterBlur >= 0.05 || FilterDenoise || FilterLut.Trim().Length > 0 || HasNewLook;

    public void ResetFilters() =>
        (FilterContrast, FilterBrightness, FilterSaturation, FilterGamma, FilterHue, FilterSharpen, FilterBlur, FilterDenoise, FilterLut) = (1, 0, 1, 1, 0, 0, 0, false, "");

    public void ResetTransform() => (Rotation, TiltX, TiltY, FlipHorizontal, FlipVertical) = (0, 0, 0, false, false);

    // Growing about the anchor point. A layer is kept by its top-left corner, but when its size is changed (by its
    // slider, its number box, a dialog, the layout pane) it is its anchor point (its middle, unless it was moved) that
    // stays where it is: the corner is moved to suit. It turns about the same point (the GPU scene plan, the layout pane).

    /// <summary>Switched on once the layer is in use; while it is being built, sizes and corners are simply taken as given.</summary>
    public bool AnchorCenter { get; set; }

    /// <summary>Where in the layer it turns and grows about, as a fraction of its width from its left edge: 0.5 is its middle.</summary>
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
    /// How far a layer's middle moves when it is turned about its anchor point instead of its middle: the turn of the
    /// line from the anchor to the middle (dx, dy, in pixels, y pointing down, degrees clockwise), less that line. The
    /// rotate filter turns about the middle; the overlay then puts the middle where the turn about the anchor takes it.
    /// </summary>
    public static (double X, double Y) Pivot(double degrees, double dx, double dy)
    {
        var angle = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        return (dx * (cos - 1) - dy * sin, dx * sin + dy * (cos - 1));
    }

    /// <summary>
    /// Moves the anchor point without moving the layer on the frame (the layout pane's anchor dot): a turned layer turns
    /// about the new point from now on, so its corner is moved by as much as that would move it.
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

    // The layer's height for each unit of its width, both as fractions of the frame; learnt whenever it is laid out.
    private double _heightPerWidth;

    /// <summary>The layer's height for each unit of its width, both as fractions of the frame, as last laid out (1 before).</summary>
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

    /// <summary>
    /// The seconds of the timeline the clip covers, on a sequence of the given length. A layer with no length
    /// of its own stays to the end.
    /// </summary>
    public (double Start, double End) GetSpan(double totalSeconds) =>
        Duration > 0.001 ? (StartTime, StartTime + Duration) : (StartTime, Math.Max(totalSeconds, StartTime));

    // ----- Keyframes -----
    // A property with keyframes moves between them in straight lines, and holds the first value before the
    // first and the last after the last. Their times are counted from where the clip begins, so a clip that
    // is moved along the timeline takes its motion with it.

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

    /// <summary>Whether it is tilted in 3D (5.2A) anywhere: a tilt set, or keyframes of one.</summary>
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

    /// <summary>The easing of the keyframe at a moment, or null when the clip has no keyframe there.</summary>
    public EasingType? GetEasingAt(double time) => GetKeyAt(time)?.Easing;

    /// <summary>The keyframe at a moment: of one property, or (null) the first of any; null when there is none there.</summary>
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
    /// Sets how the clip moves on from the keyframes at a moment: of one property (its lane in the Keyframes pane), or
    /// (null) of all of them, place, size and turn leaving it the same way. A Custom Bezier takes its handles with it.
    /// False when there is no keyframe there, or it already leaves that way.
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
    /// Moves the keyframes at one moment to another (a diamond dragged along its lane): of one property, or (null) of
    /// all of them. One already at the new moment is replaced. False when there was none to move.
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

    /// <summary>Sets a property's keyframe at a moment to its value there, or takes the one there away: its lane's diamond button.</summary>
    /// <returns>Whether there is a keyframe there now.</returns>
    public bool ToggleKey(KeyProperty property, double time)
    {
        if (RemoveKey(property, time))
            return false;

        SetKey(property, time, GetValue(property));
        return true;
    }

    // Writes a keyframe without announcing it. The first keyframe of the place while the size is animated: until then the
    // place was the anchor's, held still as the size changed about it (the export's graph before 5.2-Audit); from then on it is the
    // corner's, keyframe by keyframe. So that nothing jumps, the corner is first keyed where the anchor puts it at each of
    // the size's keyframes, leaving them as the size does.
    private void PutKey(KeyProperty property, double time, double value)
    {
        var list = _keys[(int)property];
        if (list.Count == 0 && property is KeyProperty.X or KeyProperty.Y && _keys[(int)KeyProperty.Scale].Count > 0)
        {
            var (place, anchor, across) = property == KeyProperty.X ? (PositionX, AnchorX, 1.0) : (PositionY, AnchorY, HeightPerWidth);
            foreach (var size in _keys[(int)KeyProperty.Scale])
                list.Add(size with { Value = place + anchor * (SizeWidth - size.Value) * across });
        }

        var at = list.FindIndex(k => Math.Abs(k.Time - time) <= KeyTolerance);
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

    /// <summary>Sets a keyframe at a moment that holds the clip as it is now: where it is, how large, how far turned. Announced once.</summary>
    public void SetMasterKey(double time)
    {
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            if (property == KeyProperty.Rotation && !HasFilters || property == KeyProperty.Opacity && !IsPicture)
                continue;

            // A tilt (5.2A) only on a clip that is tilted: a master key does not give every clip two lanes more.
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

    /// <summary>The keyframe nearest a moment, or null when the property has none.</summary>
    public Keyframe? GetNearestKey(KeyProperty property, double time) =>
        _keys[(int)property].Count == 0 ? null : _keys[(int)property].MinBy(k => Math.Abs(k.Time - time));

    /// <summary>Moves a whole motion: every keyframe of the property by the same amount.</summary>
    public void OffsetKeys(KeyProperty property, double by)
    {
        var list = _keys[(int)property];
        for (var i = 0; i < list.Count; i++)
            list[i] = list[i] with { Value = list[i].Value + by };
        NotifyKeys(property);
    }

    /// <summary>
    /// Moves every keyframe in time, and its effects' keyframes and ranges with them: for a clip whose beginning moved
    /// while what it shows stayed (the second half of a split, a trim of its start), so they stay on their frames.
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

        foreach (var effect in Effects)
        {
            foreach (var (key, keys) in effect.Keys.ToList())
                effect.SetKeys(key, keys.Select(k => k with { Time = Math.Round(k.Time + seconds, 3) }).ToList());
            if (effect.Range is { } range)
                effect.SetRange((Math.Max(Math.Round(range.Start + seconds, 3), 0), Math.Max(Math.Round(range.End + seconds, 3), 0.01)));
        }

        // Studio Voice's mix keys (5.2B) count from the clip's start as its motion does: moved with it, in filters of its own (the dialog
        // replaces them as a whole, so nothing else holding the old ones sees them move).
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

    /// <summary>A saved speed, made one that can be used: files from before clips had a speed have none, which is 1.</summary>
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
    }

    // ----- Ducking -----

    /// <summary>The layer's sound is turned down while a voice is speaking.</summary>
    [ObservableProperty] private bool _autoDuck;

    /// <summary>The layer's sound is a voice: what the ducked sounds make room for.</summary>
    [ObservableProperty] private bool _isVoice;

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
    /// The mask picture is of the whole source frame, not of this layer: the layer takes the part of it that
    /// lies under its own cut-out, as it takes the picture. One mask then serves every piece cut from the
    /// video, and moving a piece's cut-out keeps the mask where it is on the frame. Pieces cut from the video only.
    /// </summary>
    [ObservableProperty] private bool _maskFrame;

    /// <summary>Grows what the mask shows by about so many pixels (above 0), or shrinks it (below 0).</summary>
    [ObservableProperty] private int _maskGrow;

    /// <summary>The mask shows where its picture is black instead of where it is white.</summary>
    [ObservableProperty] private bool _maskInvert;

    /// <summary>
    /// Moves the mask picture across the layer, as a percentage of the layer's width (negative: to the left) and
    /// height (negative: up), at most <see cref="MaskOffsetLimit"/> either way: one HUD mask nudged to fit a
    /// variation of the HUD without being drawn again. What it uncovers at the far edge is hidden.
    /// </summary>
    [ObservableProperty] private double _maskOffsetX;

    /// <inheritdoc cref="MaskOffsetX"/>
    [ObservableProperty] private double _maskOffsetY;

    /// <summary>
    /// The outlines the mask was drawn from, as text (see <see cref="Services.MaskStore"/>), in fractions of the
    /// source frame; empty for a mask that was not drawn here. Kept so that they can be opened and changed again.
    /// </summary>
    [ObservableProperty] private string _maskShapes = "";

    // Crop: how much of each edge of the clip's picture is cut away, as fractions of its width or height. Every clip's crop
    // reframes its picture, on every track (4.4-BugHunt, the user's Q2): what is left of the picture is what fills its box,
    // which keeps its width and takes the shape the crop leaves (a locked box; a box of a free height keeps its height and
    // the cut picture is fitted to it). It is never a mask of the box. The graph cuts every clip so (CropFractions).
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

    // The crop as percentages, for the sliders and the number boxes beside them. Set by hand, the box takes its new shape
    // about its anchor point, as it does when it is sized (see KeepingAnchor).
    public double CropLeftPercent { get => Math.Round(CropLeft * 100, 1); set => KeepingAnchor(() => CropLeft = Crop(value / 100)); }
    public double CropRightPercent { get => Math.Round(CropRight * 100, 1); set => KeepingAnchor(() => CropRight = Crop(value / 100)); }
    public double CropTopPercent { get => Math.Round(CropTop * 100, 1); set => KeepingAnchor(() => CropTop = Crop(value / 100)); }
    public double CropBottomPercent { get => Math.Round(CropBottom * 100, 1); set => KeepingAnchor(() => CropBottom = Crop(value / 100)); }

    /// <summary>Some of an edge is cut away.</summary>
    public bool IsCropped => CropLeft + CropRight + CropTop + CropBottom > 0.0005;

    /// <summary>The most of a picture's width or of its height a crop may take, both edges together: something of it always remains.</summary>
    public const double LargestCropTotal = 0.95;

    /// <summary>
    /// A crop as fractions (0 to 1) of a picture's width and height, held to what leaves a picture: each edge, then each
    /// pair at most <see cref="LargestCropTotal"/> (the right and the bottom edge giving way); an edge that is not a number
    /// is none. The one rule every clip's crop is cut by (4.4-BugHunt): the graph's, its box's shape, the Crop boxes'.
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
    /// The crop the clip's picture is cut by, held (<see cref="HeldCrop(double, double, double, double)"/>); none while it is
    /// not <see cref="IsCropped"/>, and none for what has no picture of its own to cut (an adjustment layer, the caption box).
    /// </summary>
    public (double Left, double Top, double Right, double Bottom) CropFractions =>
        IsCropped && !IsAdjustment && !IsCaptions ? HeldCrop(CropLeft, CropTop, CropRight, CropBottom) : default;

    /// <summary>
    /// The part of the source a piece cut from the video shows (<see cref="IsVideo"/>), as fractions of the source frame: its
    /// source rectangle with its crop taken off. What the graph cuts from the frame, and its mask of the whole frame with it.
    /// </summary>
    public (double X, double Y, double Width, double Height) CroppedSource
    {
        get
        {
            var (left, top, right, bottom) = CropFractions;
            return (SourceX + left * SourceWidth, SourceY + top * SourceHeight, SourceWidth * (1 - left - right), SourceHeight * (1 - top - bottom));
        }
    }

    /// <summary>The height the crop leaves of the picture for each unit of the width it leaves, as a share of the whole picture's: 1 uncropped.</summary>
    private double CroppedShape
    {
        get
        {
            var (left, top, right, bottom) = CropFractions;
            return (1 - top - bottom) / (1 - left - right);
        }
    }

    /// <summary>
    /// A crop changed by hand: a box whose height follows its shape (locked) grows or shrinks about its anchor point, as it does
    /// when it is sized (<see cref="AnchorCenter"/>); its width stays. Only once the layer is in use and has been laid out;
    /// a crop put back with the rest of a place (ApplyPlace, an undo step, Apply Placement) is taken as given.
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
    /// Whether the frame around the clip is filled with a blurred copy of it (4.4): true gives a video from a file a blurred
    /// copy of its whole picture under it, covering the frame (the sequence's blur-fill settings: radius, passes, dim, zoom
    /// and effects); false or null, none (5.2-AuditB: null no longer follows a switch; the conversion of a project from before
    /// wrote the old switch into each former main clip).
    /// </summary>
    [ObservableProperty] private bool? _blurFill;

    /// <summary>The mask is taken from the frame: a piece cut from the video, with a mask of the whole frame.</summary>
    public bool UsesFrameMask => MaskFrame && IsVideo;

    /// <summary>
    /// What the mask is, for whatever draws it: a drawn mask's key when the layer has outlines and no picture of its own (filled
    /// in memory, no file: 5.1-Release), else its picture file; "" for none. See <c>MaskStore.SourceOf</c>.
    /// </summary>
    public string MaskSource => Services.MaskStore.SourceOf(MaskPath, MaskShapes);

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

    /// <summary>How far the drop shadow falls from the layer, in output pixels, 0 to 100 (4.1.1-B; it was one offset on both axes).</summary>
    [ObservableProperty] private int _shadowDistance = LayerState.DefaultShadowDistance;

    /// <summary>
    /// Where the light comes from, in degrees, as design tools write it: 135 lights the layer from the upper left, so
    /// the shadow falls right and down (as every shadow did before 4.1.1-B). See <see cref="ShadowShift"/>.
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

    /// <summary>Takes a saved layer's drop shadow: distance, angle, softness and colour, or those that keep the look of one saved with an offset.</summary>
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

    /// <summary>
    /// The height that keeps the layer's own shape at a given width: the source rectangle's, or the picture's, as its crop
    /// leaves it (4.4-BugHunt: the crop reframes the picture, and the box takes the shape of what is left). One rule for every
    /// clip (5.2-AuditB): a former main clip whose box the crop filter's evened sides drew otherwise has its height of its own
    /// (as 5.2-AuditB's conversion wrote it; KeepsShape, the golden tests' LegacyMainVideo since 5.2-Arm64AndNpu).
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
