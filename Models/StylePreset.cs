namespace HandPegApp.Models;

/// <summary>
/// A layout file: what the Frame &amp; Layer Engine is showing, with the colour and blur settings that go
/// with the look. Each part is optional, so a file may carry only some of them and an import may take
/// only some of what a file carries.
/// </summary>
public sealed class StylePreset
{
    [System.Text.Json.Serialization.JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    public LayoutSection? Layout { get; set; }
    public ColorSection? Color { get; set; }
    public BlurSection? Blur { get; set; }
    public SubtitleSection? Subtitles { get; set; }

    /// <summary>
    /// The mask pictures the layers use, by file name, each as Base64: carried inside the file, so that a
    /// style preset is one file that works on any machine. Null when no layer has a mask.
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
    /// Width divided by height of the video the layout was made on; 0 when not recorded. The layers are
    /// cut from the source by fractions of its frame, which only land on the same things in a video of the same shape.
    /// </summary>
    public double SourceAspectRatio { get; set; }

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

public sealed class BlurSection
{
    public int Radius { get; set; } = 20;
    public int Passes { get; set; } = 2;
    public double Dim { get; set; } = -0.15;
}
