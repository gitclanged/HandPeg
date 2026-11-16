namespace HandPegApp.Models;

/// <summary>
/// Style (layout) file: layers plus colour and blur settings. All sections are optional, and imports may apply a subset.
/// </summary>
public sealed class StylePreset
{
    /// <summary>The extension of a Game Preset file in the Styles folder.</summary>
    public const string FileExtension = ".hpstyle";

    [System.Text.Json.Serialization.JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    public LayoutSection? Layout { get; set; }
    public ColorSection? Color { get; set; }
    public BlurSection? Blur { get; set; }
    public SubtitleSection? Subtitles { get; set; }

    /// <summary>
    /// Embedded mask bitmaps by file name (Base64) so the style is self-contained; null when no layer has a bitmap mask.
    /// </summary>
    public Dictionary<string, string>? Masks { get; set; }
}

/// <summary>Auto-captions: whether they are on, how whisper is asked to listen, how they look and where they sit.</summary>
public sealed class SubtitleSection
{
    public bool AutoCaptions { get; set; }
    public string WhisperPrompt { get; set; } = "";
    public string WhisperLanguage { get; set; } = "en";
    public bool WhisperTranslate { get; set; }
    public CaptionStyle Style { get; set; } = new();
    public LayerState? Layer { get; set; }
}

/// <summary>The centre video and the layers. Everything is a fraction of the frame, so it fits any frame size.</summary>
public sealed class LayoutSection
{
    public double CenterZoom { get; set; } = 1;
    public double CenterOffsetX { get; set; }
    public double CenterOffsetY { get; set; }

    /// <summary>
    /// Source aspect ratio the layout was authored on (0 if unknown); fractional crops only match sources of the same aspect.
    /// </summary>
    public double SourceAspectRatio { get; set; }

    /// <summary>True for a vertical frame from a wide source; null leaves the frame orientation unchanged.</summary>
    public bool? Vertical { get; set; }

    /// <summary>How many of the layers lie under the main video; null in a style that does not say.</summary>
    public int? MainVideoIndex { get; set; }

    /// <summary>The look of the main video itself: its style, mask, filters and how it is turned.</summary>
    public LayerState? MainLayer { get; set; }

    /// <summary>Whether the blurred background is switched off, leaving the black canvas.</summary>
    public bool BackgroundHidden { get; set; }

    public List<LayerState> Layers { get; set; } = [];
}

public sealed class ColorSection
{
    public double Contrast { get; set; } = 1;
    public double Brightness { get; set; }
    public double Saturation { get; set; } = 1;
    public double Gamma { get; set; } = 1;
    public double Hue { get; set; }
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Sharpen { get; set; }
}

/// <summary>Blur fill look: continuous radius from 0, zoom and its own effect stack.</summary>
public sealed class BlurSection
{
    public double Radius { get; set; } = 20;
    public int Passes { get; set; } = 2;
    public double Dim { get; set; } = -0.15;
    public double Zoom { get; set; } = 1;

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HandPegApp.Models.Effects.EffectState>? Effects { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool EffectsBypassed { get; set; }
}
