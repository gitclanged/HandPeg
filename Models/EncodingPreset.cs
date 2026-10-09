using System.Text.Json.Serialization;

namespace HandPegApp.Models;

/// <summary>The groups of settings a preset is made of, for applying some of it and leaving the rest as it is.</summary>
[Flags]
public enum PresetParts
{
    None = 0,
    Size = 1,
    Video = 2,
    Audio = 4,
    Filters = 8,
    Layout = 16,
    Captions = 32,
    All = Size | Video | Audio | Filters | Layout | Captions,
}

/// <summary>
/// A named snapshot of the Dimensions, Filters, Video and Audio tabs.
/// Nothing about the source or its cut points is stored.
/// </summary>
public sealed class EncodingPreset
{
    public string Name { get; set; } = "";

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
    public string PixelAspectRatio { get; set; } = "Same as source";
    public string CustomPixelAspectRatio { get; set; } = "";

    /// <summary>
    /// Whether the size is the source's shape turned on its side (16:9 becomes 9:16). Null in presets saved
    /// before the option existed; those were vertical exactly when they used the framer.
    /// </summary>
    public bool? UseVerticalResolution { get; set; }

    // Frame & Layer Engine: the video on a blurred copy of itself, with elements on top.
    // The names in the file are the ones it had as the "vertical framer", so older presets still load.
    [JsonPropertyName("VerticalFramer")]
    public bool FrameEngine { get; set; }

    /// <summary>Scale of the centre video: 1 fills the width of the frame.</summary>
    [JsonPropertyName("VerticalZoom")]
    public double CenterZoom { get; set; } = 1;

    /// <summary>How far the centre video is moved right (positive) or left, as a percentage of the frame width.</summary>
    public double CenterOffsetX { get; set; }

    /// <summary>How far the centre video is moved up (negative) or down, as a percentage of the frame height.</summary>
    [JsonPropertyName("VerticalOffset")]
    public double CenterOffsetY { get; set; }

    // The blurred background
    public int BlurRadius { get; set; } = 20;
    public int BlurPasses { get; set; } = 2;
    public double BackgroundDim { get; set; } = -0.15;

    /// <summary>Pieces of the source and images placed on the frame, each with where it comes from and where it goes. In stacking order, bottom first.</summary>
    public List<OverlayRegionState> UiElements { get; set; } = [];

    /// <summary>How many of those layers lie under the main video: 0 puts it beneath all of them, as it always was before layers could be reordered.</summary>
    public int MainVideoIndex { get; set; }

    /// <summary>
    /// Width divided by height of the video the elements were marked on; 0 when not recorded. Their source
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
    public OverlayRegionState? CaptionLayer { get; set; }

    // Filters
    public bool Deinterlace { get; set; }
    public bool Denoise { get; set; }
    public string LutPath { get; set; } = "";

    // The watermark of earlier versions. Read so that it can be turned into an image element; never written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatermarkPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WatermarkOpacity { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatermarkPosition { get; set; }

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
    public string EncoderPreset { get; set; } = "medium";
    public string Tune { get; set; } = "None";
    public string Profile { get; set; } = "Auto";
    public string Level { get; set; } = "Auto";
    public string RateControl { get; set; } = "Constant Quality (CQ)";
    public int Quality { get; set; } = 22;
    public string TargetBitrate { get; set; } = "5000";
    public string Framerate { get; set; } = "Same as source";
    public string CustomFramerate { get; set; } = "";
    public string Colorspace { get; set; } = "Same as source";
    public bool HardwareDecoding { get; set; } = true;

    /// <summary>Encoder options with no control of their own, for example "-g 1 -keyint_min 1".</summary>
    public string ExtraVideoArguments { get; set; } = "";

    // Audio: the codec and bitrate apply to every track.
    public string AudioEncoder { get; set; } = "aac";
    public string AudioBitrate { get; set; } = "160k";
    public bool MergeAudioTracks { get; set; }
    public bool NormalizeAudio { get; set; }
    public bool DuckAudio { get; set; }

    /// <summary>
    /// What was set track by track (action, codec, bitrate, title), by track number. Applied on top of the
    /// codec and bitrate above to the tracks a source actually has.
    /// </summary>
    public List<AudioTrackState> AudioTracks { get; set; } = [];

    /// <summary>Target file size in MB. Only stored when the settings say presets should carry it.</summary>
    public string TargetFileSize { get; set; } = "";

    /// <summary>
    /// Set only by built-in presets whose codecs need a particular container (ProRes and PCM need MOV).
    /// </summary>
    public string? Container { get; set; }

    public override string ToString() => Name;

    /// <summary>Takes the chosen groups of settings from another preset, leaving the others as they are here.</summary>
    public void TakeFrom(EncodingPreset other, PresetParts parts)
    {
        if (parts.HasFlag(PresetParts.Size))
        {
            (OutputWidth, OutputHeight, KeepAspectRatio) = (other.OutputWidth, other.OutputHeight, other.KeepAspectRatio);
            (CropPercent, CropTop, CropBottom, CropLeft, CropRight) = (other.CropPercent, other.CropTop, other.CropBottom, other.CropLeft, other.CropRight);
            (PixelAspectRatio, CustomPixelAspectRatio) = (other.PixelAspectRatio, other.CustomPixelAspectRatio);
            UseVerticalResolution = other.UseVerticalResolution ?? other.FrameEngine;
        }

        if (parts.HasFlag(PresetParts.Video))
        {
            (VideoEncoder, EncoderPreset, Tune, Profile, Level) = (other.VideoEncoder, other.EncoderPreset, other.Tune, other.Profile, other.Level);
            (RateControl, Quality, TargetBitrate, TargetFileSize) = (other.RateControl, other.Quality, other.TargetBitrate, other.TargetFileSize);
            (Framerate, CustomFramerate, Colorspace) = (other.Framerate, other.CustomFramerate, other.Colorspace);
            (HardwareDecoding, ExtraVideoArguments, Container) = (other.HardwareDecoding, other.ExtraVideoArguments, other.Container);
        }

        if (parts.HasFlag(PresetParts.Audio))
        {
            (AudioEncoder, AudioBitrate, AudioTracks) = (other.AudioEncoder, other.AudioBitrate, other.AudioTracks);
            (MergeAudioTracks, NormalizeAudio, DuckAudio) = (other.MergeAudioTracks, other.NormalizeAudio, other.DuckAudio);
        }

        if (parts.HasFlag(PresetParts.Filters))
        {
            (Deinterlace, Denoise, LutPath, FadeIn, FadeOut) = (other.Deinterlace, other.Denoise, other.LutPath, other.FadeIn, other.FadeOut);
            (ColorContrast, ColorBrightness, ColorSaturation, ColorGamma, ColorHue) =
                (other.ColorContrast, other.ColorBrightness, other.ColorSaturation, other.ColorGamma, other.ColorHue);
            (ColorRed, ColorGreen, ColorBlue, SharpenStrength) = (other.ColorRed, other.ColorGreen, other.ColorBlue, other.SharpenStrength);
        }

        if (parts.HasFlag(PresetParts.Layout))
        {
            (FrameEngine, CenterZoom, CenterOffsetX, CenterOffsetY) = (other.FrameEngine, other.CenterZoom, other.CenterOffsetX, other.CenterOffsetY);
            (BlurRadius, BlurPasses, BackgroundDim) = (other.BlurRadius, other.BlurPasses, other.BackgroundDim);
            (UiElements, LayoutSourceAspectRatio, MainVideoIndex) = (other.UiElements, other.LayoutSourceAspectRatio, other.MainVideoIndex);
            (WatermarkPath, WatermarkOpacity, WatermarkPosition) = (other.WatermarkPath, other.WatermarkOpacity, other.WatermarkPosition);
        }

        if (parts.HasFlag(PresetParts.Captions))
        {
            (AutoCaptions, CaptionStyle, CaptionLayer) = (other.AutoCaptions, other.CaptionStyle, other.CaptionLayer);
            (WhisperPrompt, WhisperLanguage, WhisperTranslate) = (other.WhisperPrompt, other.WhisperLanguage, other.WhisperTranslate);
        }
    }
}
