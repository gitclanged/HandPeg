using System.Text.Json.Serialization;

namespace HandPegApp.Models;

/// <summary>Encoded audio channel choices (serialized by name).</summary>
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

    /// <summary>Offered sample rates (Hz); 0 = Match Project Settings (project rate, default 48 kHz).</summary>
    public static IReadOnlyList<LabeledChoice<int>> SampleRates { get; } =
        [new(0, "Match Project Settings (48 kHz)"), new(48000, "48000 Hz"), new(44100, "44100 Hz")];

    public static int NormalizeRate(int value) => value is 48000 or 44100 ? value : 0;
}

/// <summary>Combo box item: stored <see cref="Key"/> and display <see cref="Value"/> (also returned by ToString for screen readers and
/// path-less templates).</summary>
public sealed record LabeledChoice<T>(T Key, string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Named snapshot of Export tab settings plus saved layout, filters and captions (no source or cut data). Single model for Export
/// Presets: built-ins (ExportPresetCatalog) have a <see cref="Category"/>; custom ones (presets.json, .hppreset) do not.
/// </summary>
public sealed class EncodingPreset : IJsonOnDeserialized
{
    /// <summary>Export Preset selector groups: built-in categories, then "My Presets".</summary>
    public const string PlatformCategory = "Platform & Social", StudioCategory = "Studio & Archive", UserCategory = "My Presets";

    /// <summary>
    /// Unknown JSON fields preserved on save; retired keys (<see cref="RetiredKeys"/>) are dropped on read.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>Retired setting keys (e.g. "VerticalFramer", the removed Frame &amp; Layer Engine switch).</summary>
    private static readonly string[] RetiredKeys = ["VerticalFramer"];

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Extra is null)
            return;
        foreach (var key in RetiredKeys)
            Extra.Remove(key);
        if (Extra.Count == 0)
            Extra = null;
    }

    public string Name { get; set; } = "";

    /// <summary>Selector group of a built-in preset; null (omitted) for custom presets ("My Presets").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>One line under the name in the Export Presets selector; null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    /// <summary>True for built-ins (has a <see cref="Category"/>): never written to presets.json or overwritten.</summary>
    [JsonIgnore]
    public bool IsBuiltIn => Category is not null;

    /// <summary>The selector group it is shown in: its <see cref="Category"/>, or "My Presets".</summary>
    [JsonIgnore]
    public string Group => Category ?? UserCategory;

    // Dimensions
    public string OutputWidth { get; set; } = "";
    public string OutputHeight { get; set; } = "";
    public bool KeepAspectRatio { get; set; } = true;
    /// <summary>Crop percentages (left, top, right, bottom); null in legacy presets with absolute crops.</summary>
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
    /// <summary>"Match Project Settings" for the project resolution; legacy "Same as source" maps to it.</summary>
    public string PixelAspectRatio { get; set; } = "Match Project Settings";
    public string CustomPixelAspectRatio { get; set; } = "";

    /// <summary>
    /// Rotate the output aspect to vertical (16:9 becomes 9:16). Null in legacy presets reads as off; legacy "VerticalFramer" is ignored.
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

    // Blur-fill background: radius (0-64 frame px, continuous), passes (1-4), darkening, centre zoom (1 = cover), Look cards (null for none).
    public double BlurRadius { get; set; } = 20;
    public int BlurPasses { get; set; } = 2;
    public double BackgroundDim { get; set; } = -0.15;
    public double BackgroundZoom { get; set; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Models.Effects.EffectState>? BackgroundEffects { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BackgroundEffectsBypassed { get; set; }

    /// <summary>Preset layers (Cutouts and images) with source and placement, bottom-first.</summary>
    public List<LayerState> Layers { get; set; } = [];

    /// <summary>The look of the main video as a layer: its style, mask, filters and how it is turned.</summary>
    public LayerState? MainLayer { get; set; }

    /// <summary>The Sequence Output's effect stack (Effects pane), applied to the finished picture; null for none.</summary>
    public List<Models.Effects.EffectState>? SequenceEffects { get; set; }

    public bool SequenceEffectsBypassed { get; set; }

    /// <summary>Whether the blurred background is switched off, leaving the black canvas.</summary>
    public bool BackgroundHidden { get; set; }

    /// <summary>Number of preset layers below the host video (0 = video at the bottom, legacy default).</summary>
    public int MainVideoIndex { get; set; }

    /// <summary>
    /// Aspect ratio of the video the layers were authored on (0 if unknown); fractional rects fit any size but only this aspect.
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

    /// <summary>Caption box placement, size and opacity; null in legacy presets.</summary>
    public LayerState? CaptionLayer { get; set; }

    // Filters
    public bool Deinterlace { get; set; }
    public bool Denoise { get; set; }
    public string LutPath { get; set; } = "";

    // (Legacy watermark fields are ignored; image clips replace them.)

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
    /// Target codec of a built-in preset ("h264", "hevc", "av1", "vp9"), resolved to the best local encoder, hardware first
    /// (MainViewModel.ResolveEncoder), falling back to <see cref="VideoEncoder"/>. Null (omitted): use <see cref="VideoEncoder"/> as-is.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreferredCodec { get; set; }

    /// <summary>Encoder speed preset; empty for the highest-quality default (<c>MainViewModel.BestStepOf</c>, e.g. veryslow for x264/x265).</summary>
    public string EncoderPreset { get; set; } = "";

    /// <summary>
    /// Use the encoder's fastest step (Fast Proxy, the only speed-first preset): NVENC p1, QSV veryfast, AMF speed, x264/x265 ultrafast, MF
    /// -compression_level 0 (MainViewModel.FastestStepOf). A flag because <see cref="EncoderPreset"/> is family-specific. False (omitted) otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SpeedFirst { get; set; }

    public string Tune { get; set; } = "None";
    public string Profile { get; set; } = "Auto";
    public string Level { get; set; } = "Auto";

    /// <summary>Stored rate control (default VBR, stored as "Average Bitrate (ABR)").</summary>
    public string RateControl { get; set; } = "Average Bitrate (ABR)";
    public int Quality { get; set; } = 22;

    /// <summary>Video bitrate (kbps); empty for automatic (MainViewModel.EffectiveBitrateKbps from output size and rate).</summary>
    public string TargetBitrate { get; set; } = "";
    public string Framerate { get; set; } = "Match Project Settings";
    public string CustomFramerate { get; set; } = "";

    /// <summary>Frame-rate cap applied to Match Project Settings (platform limit, e.g. 60); 0 (omitted) for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FramerateCap { get; set; }

    /// <summary>Short-side pixel cap (never upscales the Sequence); 0 (omitted) for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ShortSideCap { get; set; }
    public string Colorspace { get; set; } = "Match Project Settings";

    // (Legacy "HardwareDecoding" is ignored.)

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

    /// <summary>Normalize target LUFS (-14 or -16); 0 (omitted) means the default -16.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LoudnessTarget { get; set; }

    // (Legacy "DuckAudio" is ignored; ducking is per Sub-Lane.)

    /// <summary>Encoded sample rate (Hz); 0 (Auto, omitted) keeps each track's rate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int AudioSampleRate { get; set; }

    /// <summary>Encoded channel layout (<see cref="AudioChannelChoice"/>); null (omitted) = Stereo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioChannels { get; set; }

    /// <summary>How far a track set to Auto-Duck is turned down while a voice speaks, in decibels (negative).</summary>
    public double DuckAmountDb { get; set; } = -15;

    /// <summary>
    /// Per-track overrides (action, codec, bitrate, title) by track number, applied over the defaults to tracks that exist.
    /// </summary>
    public List<AudioTrackState> AudioTracks { get; set; } = [];

    /// <summary>Target file size in MB. Only stored when the settings say presets should carry it.</summary>
    public string TargetFileSize { get; set; } = "";

    /// <summary>
    /// Container (mp4, mkv, mov, ...); null leaves the current container unchanged (legacy presets).
    /// </summary>
    public string? Container { get; set; }

    /// <summary>Web Optimized (-movflags +faststart); null (legacy) leaves it unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WebOptimized { get; set; }

    public override string ToString() => Name;
}
