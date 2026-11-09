using System.Text.Json.Serialization;

namespace HandPegApp.Models;

/// <summary>The export's channel choices for encoded audio, saved by these names (4.1.1).</summary>
public static class AudioChannelChoice
{
    /// <summary>Two channels: anything wider is folded down; a stereo or mono track is left as it is.</summary>
    public const string Stereo = "stereo";

    public const string Mono = "mono";

    /// <summary>As many channels as the source has.</summary>
    public const string Source = "source";

    public static string Normalize(string? value) => value is Mono or Source ? value : Stereo;

    public static IReadOnlyList<LabeledChoice<string>> Choices { get; } =
        [new(Stereo, "Stereo (2.0)"), new(Mono, "Mono (1.0)"), new(Source, "Match Source")];

    /// <summary>The sample rates offered, in Hz; 0 is Match Project Settings (5.4A: every render is mixed at the project's rate, 48 kHz unless it says otherwise).</summary>
    public static IReadOnlyList<LabeledChoice<int>> SampleRates { get; } =
        [new(0, "Match Project Settings (48 kHz)"), new(48000, "48000 Hz"), new(44100, "44100 Hz")];

    public static int NormalizeRate(int value) => value is 48000 or 44100 ? value : 0;
}

/// <summary>A choice for a box: the value saved (<see cref="Key"/>) and the words shown (<see cref="Value"/>), which are
/// also what it reads as wherever a box falls back to the item itself (screen readers, a template without a path).</summary>
public sealed record LabeledChoice<T>(T Key, string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// A named snapshot of the export settings (the Export tab), and the layout, filters and captions of a preset saved with them.
/// Nothing about the source or its cut points is stored.
/// Since 5.4B it is also the one model of an Export Preset: the built-in ones (ExportPresetCatalog) carry a <see cref="Category"/>,
/// the user's own (presets.json, .hppreset files) none.
/// </summary>
public sealed class EncodingPreset
{
    /// <summary>The Export Presets selector's groups (5.4B): the built-ins of each, then the user's own ("My Presets").</summary>
    public const string PlatformCategory = "Platform & Social", StudioCategory = "Studio & Archive", UserCategory = "My Presets";

    public string Name { get; set; } = "";

    /// <summary>The selector group of a built-in Export Preset; null (not written) for one the user saved, which is "My Presets".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>One line under the name in the Export Presets selector; null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    /// <summary>A built-in Export Preset (it has a <see cref="Category"/>): never written to presets.json, never overwritten by a save.</summary>
    [JsonIgnore]
    public bool IsBuiltIn => Category is not null;

    /// <summary>The selector group it is shown in: its <see cref="Category"/>, or "My Presets".</summary>
    [JsonIgnore]
    public string Group => Category ?? UserCategory;

    // Dimensions
    public string OutputWidth { get; set; } = "";
    public string OutputHeight { get; set; } = "";
    public bool KeepAspectRatio { get; set; } = true;
    /// <summary>The crop as percentages of the source: left, top, right, bottom. Null in presets saved before the crop became relative.</summary>
    public double[]? CropPercent { get; set; }

    // The crop in pixels, from those earlier presets. Read for conversion; a zero is not written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CropTop { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CropBottom { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CropLeft { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CropRight { get; set; }
    /// <summary>"Match Project Settings" (5.4A) for the project's own; a preset saved with "Same as source" reads as it.</summary>
    public string PixelAspectRatio { get; set; } = "Match Project Settings";
    public string CustomPixelAspectRatio { get; set; } = "";

    /// <summary>
    /// Whether the size is the source's shape turned on its side (16:9 becomes 9:16). Null in presets saved before the option
    /// existed, which reads as off (5.2-Arm64AndNpu: the old Frame &amp; Layer Engine switch, "VerticalFramer" in such a file, is
    /// no longer read in its place; a member of a file this version does not know is skipped).
    /// </summary>
    public bool? UseVerticalResolution { get; set; }

    /// <summary>Scale of the centre video: 1 fills the width of the frame.</summary>
    [JsonPropertyName("VerticalZoom")]
    public double CenterZoom { get; set; } = 1;

    /// <summary>How far the centre video is moved right (positive) or left, as a percentage of the frame width.</summary>
    public double CenterOffsetX { get; set; }

    /// <summary>How far the centre video is moved up (negative) or down, as a percentage of the frame height.</summary>
    [JsonPropertyName("VerticalOffset")]
    public double CenterOffsetY { get; set; }

    // The blurred background (5.0-Release: the main video's video-only child): its blur in pixels of the frame (0 sharp to
    // 64, continuous), its passes (1 to 4), its darkening, its zoom about the middle (1 covers the frame), and its own
    // effect stack (Look cards; null for none).
    public double BlurRadius { get; set; } = 20;
    public int BlurPasses { get; set; } = 2;
    public double BackgroundDim { get; set; } = -0.15;
    public double BackgroundZoom { get; set; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Models.Effects.EffectState>? BackgroundEffects { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BackgroundEffectsBypassed { get; set; }

    /// <summary>Pieces of the source and images placed on the frame, each with where it comes from and where it goes. In stacking order, bottom first.</summary>
    public List<LayerState> Layers { get; set; } = [];

    /// <summary>The look of the main video as a layer: its style, mask, filters and how it is turned.</summary>
    public LayerState? MainLayer { get; set; }

    /// <summary>The Sequence Output's effect stack (Effects pane), applied to the finished picture; null for none.</summary>
    public List<Models.Effects.EffectState>? SequenceEffects { get; set; }

    public bool SequenceEffectsBypassed { get; set; }

    /// <summary>Whether the blurred background is switched off, leaving the black canvas.</summary>
    public bool BackgroundHidden { get; set; }

    /// <summary>How many of those layers lie under the main video: 0 puts it beneath all of them, as it always was before layers could be reordered.</summary>
    public int MainVideoIndex { get; set; }

    /// <summary>
    /// Width divided by height of the video the layers were marked on; 0 when not recorded. Their source
    /// rectangles are fractions of the frame, so they fit any size of video, but only one shape.
    /// </summary>
    public double LayoutSourceAspectRatio { get; set; }

    // Auto-captions
    public bool AutoCaptions { get; set; }
    public CaptionStyle? CaptionStyle { get; set; }

    /// <summary>Words and names for whisper to expect, passed as its initial prompt.</summary>
    public string WhisperPrompt { get; set; } = "";

    /// <summary>The spoken language as whisper names it (en, es, ja...), or auto.</summary>
    public string WhisperLanguage { get; set; } = "en";

    public bool WhisperTranslate { get; set; }

    /// <summary>Where the caption box sits on the frame, its size and opacity. Null in presets saved before it could be moved.</summary>
    public LayerState? CaptionLayer { get; set; }

    // Filters
    public bool Deinterlace { get; set; }
    public bool Denoise { get; set; }
    public string LutPath { get; set; } = "";

    // (The watermark of versions before images were clips, WatermarkPath and its opacity and position, is not read any more,
    // 5.2-Arm64AndNpu: a preset that has one opens without it; an image clip does the same.)

    // Advanced: colour correction (eq) and sharpening (cas). The defaults leave the picture as it is.
    public double ColorContrast { get; set; } = 1;
    public double ColorBrightness { get; set; }
    public double ColorSaturation { get; set; } = 1;
    public double ColorGamma { get; set; } = 1;
    public double ColorHue { get; set; }
    public double ColorRed { get; set; }
    public double ColorGreen { get; set; }
    public double ColorBlue { get; set; }
    public double SharpenStrength { get; set; }

    public bool FadeIn { get; set; }
    public bool FadeOut { get; set; }

    // Video
    public string VideoEncoder { get; set; } = "libx264";

    /// <summary>
    /// The codec a built-in Export Preset wants ("h264", "hevc", "av1", "vp9"): the best encoder of it this PC has, a hardware one
    /// first (MainViewModel.ResolveEncoder); <see cref="VideoEncoder"/> when none. Null (not written): <see cref="VideoEncoder"/> as it is.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreferredCodec { get; set; }

    /// <summary>The encoder's speed step; empty for its own default (its best: <c>MainViewModel.BestStepOf</c>, veryslow for x264 and x265 since 5.4B).</summary>
    public string EncoderPreset { get; set; } = "";

    /// <summary>
    /// The encoder's fastest step, whichever it resolves to (5.4D, Q2 b: Fast Proxy, the one exception to the slowest step everywhere):
    /// NVENC p1, QuickSync veryfast, AMF speed, x264 and x265 ultrafast, Media Foundation -compression_level 0 (MainViewModel.FastestStepOf).
    /// A flag, not a step: <see cref="EncoderPreset"/> is one name across every family. False (not written) for every other preset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SpeedFirst { get; set; }

    public string Tune { get; set; } = "None";
    public string Profile { get; set; } = "Auto";
    public string Level { get; set; } = "Auto";

    /// <summary>The rate control as stored (5.4B: VBR, stored "Average Bitrate (ABR)", is the default).</summary>
    public string RateControl { get; set; } = "Average Bitrate (ABR)";
    public int Quality { get; set; } = 22;

    /// <summary>The video bitrate in kbps; empty (5.4B) for the automatic one worked out from the output's size and rate (MainViewModel.EffectiveBitrateKbps).</summary>
    public string TargetBitrate { get; set; } = "";
    public string Framerate { get; set; } = "Match Project Settings";
    public string CustomFramerate { get; set; } = "";

    /// <summary>The most frames a second a Match Project Settings rate is given (a platform's cap: 60); 0 (not written) for no cap.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FramerateCap { get; set; }

    /// <summary>The short side of the picture capped at this many pixels, never above the Sequence's own (a chat's size); 0 (not written) for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ShortSideCap { get; set; }
    public string Colorspace { get; set; } = "Match Project Settings";

    // (Its "HardwareDecoding", the dead -hwaccel switch, is not read any more, 5.4B: a file that has it reads without it.)

    /// <summary>Encoder options with no control of their own, for example "-g 1 -keyint_min 1".</summary>
    public string ExtraVideoArguments { get; set; } = "";

    // The selected encoder's own settings: each is only used while an encoder that has it is the one selected.
    public string NvencTune { get; set; } = "High Quality (hq)";
    public string NvencMultipass { get; set; } = "Disabled";
    public bool NvencSpatialAq { get; set; }
    public bool NvencTemporalAq { get; set; }
    public string AmfUsage { get; set; } = "Transcoding (transcoding)";

    /// <summary>QuickSync: constant quality as ICQ (-global_quality) instead of a constant QP (-q:v).</summary>
    public bool QsvIcq { get; set; }

    /// <summary>Frames of rate-control lookahead, for NVENC and QuickSync; 0 for none.</summary>
    public int Lookahead { get; set; }

    /// <summary>10-bit output instead of 8-bit, for the encoders that have it.</summary>
    public bool TenBit { get; set; }

    // The two above as they were saved while they were NVENC's alone. Read into them; never written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NvencLookahead { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NvencTenBit { get; set; }

    // Audio: the codec and bitrate apply to every track.
    public string AudioEncoder { get; set; } = "aac";
    public string AudioBitrate { get; set; } = "160k";
    public bool MergeAudioTracks { get; set; }
    public bool NormalizeAudio { get; set; }

    /// <summary>The loudness Normalize aims at, in LUFS: -14 or -16; 0 (not written) is the default, -16 (5.4B).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LoudnessTarget { get; set; }

    // (A preset's old "DuckAudio", one switch for the first two streams from before ducking was set lane by lane, is not read
    // any more, 5.2-Arm64AndNpu: each Sub-Lane ducks of its own. A file that has it reads without it.)

    /// <summary>The sample rate every encoded track is written at, in Hz; 0 (Auto, not written) keeps each track's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int AudioSampleRate { get; set; }

    /// <summary>The channels of every encoded track (<see cref="AudioChannelChoice"/>); null (not written) is Stereo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioChannels { get; set; }

    /// <summary>How far a track set to Auto-Duck is turned down while a voice speaks, in decibels (negative).</summary>
    public double DuckAmountDb { get; set; } = -15;

    /// <summary>
    /// What was set track by track (action, codec, bitrate, title), by track number. Applied on top of the
    /// codec and bitrate above to the tracks a source actually has.
    /// </summary>
    public List<AudioTrackState> AudioTracks { get; set; } = [];

    /// <summary>Target file size in MB. Only stored when the settings say presets should carry it.</summary>
    public string TargetFileSize { get; set; } = "";

    /// <summary>
    /// The container (mp4, mkv, mov...): set by built-in presets, and by every preset saved since 5.4B; null leaves it as it is.
    /// </summary>
    public string? Container { get; set; }

    /// <summary>Web Optimized (-movflags +faststart) as the preset has it; null (not written, presets from before 5.4B) leaves it as it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WebOptimized { get; set; }

    public override string ToString() => Name;
}
