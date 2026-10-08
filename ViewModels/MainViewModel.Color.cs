using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The Advanced tab: colour grading and sharpening, the single-frame preview that shows them, and layout files.
public partial class MainViewModel
{
    // What each control is at when it does nothing.
    private const double NeutralContrast = 1;
    private const double NeutralBrightness = 0;
    private const double NeutralSaturation = 1;
    private const double NeutralGamma = 1;

    [ObservableProperty] private double _colorContrast = NeutralContrast;
    [ObservableProperty] private double _colorBrightness = NeutralBrightness;
    [ObservableProperty] private double _colorSaturation = NeutralSaturation;
    [ObservableProperty] private double _colorGamma = NeutralGamma;

    /// <summary>Rotation of every colour around the colour wheel, in degrees: -180 to 180.</summary>
    [ObservableProperty] private double _colorHue;

    // RGB balance: how much of each primary is added (or, negative, taken away): -1 to 1.
    [ObservableProperty] private double _colorRed;
    [ObservableProperty] private double _colorGreen;
    [ObservableProperty] private double _colorBlue;

    /// <summary>Contrast adaptive sharpening, 0 (off) to 1.</summary>
    [ObservableProperty] private double _sharpenStrength;

    public bool ShowAdvancedFiltersTab => AppSettings.Current.ShowAdvancedFiltersTab;

    [RelayCommand]
    private void ResetColor()
    {
        (ColorContrast, ColorBrightness, ColorSaturation, ColorGamma) = (NeutralContrast, NeutralBrightness, NeutralSaturation, NeutralGamma);
        (ColorHue, ColorRed, ColorGreen, ColorBlue, SharpenStrength) = (0, 0, 0, 0, 0);
    }

    /// <summary>
    /// The grading filters for the current sliders: eq (with the RGB balance), hue, then cas. A control left where
    /// it does nothing adds no filter, so untouched video is not processed for no reason.
    /// </summary>
    private List<string> BuildColorFilters()
    {
        var filters = new List<string>();

        var contrast = Math.Clamp(ColorContrast, 0, 2);
        var brightness = Math.Clamp(ColorBrightness, -1, 1);
        var saturation = Math.Clamp(ColorSaturation, 0, 3);
        var gamma = Math.Clamp(ColorGamma, 0.1, 3);
        // The RGB balance goes through eq's per-channel gammas. They work on the video as it is; a filter
        // such as colorbalance would turn it into RGB, and the encoder would then be handed RGB video.
        var (red, green, blue) = (Math.Clamp(ColorRed, -1, 1), Math.Clamp(ColorGreen, -1, 1), Math.Clamp(ColorBlue, -1, 1));
        var isBalanced = IsChanged(red, 0) || IsChanged(green, 0) || IsChanged(blue, 0);

        if (isBalanced || IsChanged(contrast, NeutralContrast) || IsChanged(brightness, NeutralBrightness)
            || IsChanged(saturation, NeutralSaturation) || IsChanged(gamma, NeutralGamma))
        {
            var eq = $"eq=contrast={Number(contrast)}:brightness={Number(brightness)}:saturation={Number(saturation)}:gamma={Number(gamma)}";

            // A slider at 0 is a gamma of 1; the ends, -1 and 1, are a gamma of a half and of two.
            if (isBalanced)
                eq += $":gamma_r={Number(Math.Pow(2, red))}:gamma_g={Number(Math.Pow(2, green))}:gamma_b={Number(Math.Pow(2, blue))}";
            filters.Add(eq);
        }

        var hue = Math.Clamp(ColorHue, -180, 180);
        if (Math.Abs(hue) >= 0.05)
            filters.Add($"hue=h={Number(hue)}");

        var sharpen = Math.Clamp(SharpenStrength, 0, 1);
        if (IsChanged(sharpen, 0))
            filters.Add($"cas=strength={Number(sharpen)}");

        return filters;

        // The controls move in hundredths; anything closer to neutral than that is neutral.
        static bool IsChanged(double value, double neutral) => Math.Abs(value - neutral) >= 0.005;
    }

    /// <summary>
    /// The command that writes one frame of the source, taken at a given time and passed through the
    /// grading filters, to a picture file. Seeking before the input makes it quick.
    /// </summary>
    /// <param name="showHistogram">Draws a histogram of the graded frame in its bottom-right corner.</param>
    public string BuildFramePreviewCommand(double seconds, string outputPath, bool showHistogram)
    {
        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? Quote(DependencyUpdater.FfmpegPath) : "ffmpeg";
        var filters = BuildColorFilters();

        if (showHistogram)
        {
            // Measured after the grading, so it shows what the sliders have done. A fifth of the frame wide,
            // but never smaller than the histogram is drawn, so it stays readable on any source.
            var width = Math.Max(SourceWidth / 5 / 2 * 2, 256);
            var margin = Math.Max(SourceWidth / 100, 10);
            filters.Add($"split[frame][scope_in];[scope_in]histogram=display_mode=overlay,scale={width}:-2[scope];"
                        + $"[frame][scope]overlay=W-w-{margin}:H-h-{margin}");
        }

        var filterOption = filters.Count > 0 ? $" -vf \"{string.Join(",", filters)}\"" : "";
        var time = Math.Max(seconds, 0).ToString("0.###", CultureInfo.InvariantCulture);

        return $"{ffmpeg} -hide_banner{(HardwareDecoding ? " -hwaccel auto" : "")} -ss {time} -i {Quote(LocalMediaPath)} -frames:v 1{filterOption} -q:v 3 -y {Quote(outputPath)}";
    }

    // ----- Layout files -----

    private static readonly JsonSerializerOptions LayoutJsonOptions = new() { WriteIndented = true };

    /// <summary>The engine's layers, the grading and the background blur as they are now.</summary>
    private LayoutPreset CaptureLayout() => new()
    {
        Layout = new LayoutSection
        {
            CenterZoom = CenterZoom,
            CenterOffsetX = CenterOffsetX,
            CenterOffsetY = CenterOffsetY,
            SourceAspectRatio = GetLayoutSourceAspect(),
            Elements = UiElements.Select(e => e.ToState()).ToList(),
        },
        Color = new ColorSection
        {
            Contrast = ColorContrast,
            Brightness = ColorBrightness,
            Saturation = ColorSaturation,
            Gamma = ColorGamma,
            Hue = ColorHue,
            Red = ColorRed,
            Green = ColorGreen,
            Blue = ColorBlue,
            Sharpen = SharpenStrength,
        },
        Blur = new BlurSection { Radius = BlurRadius, Passes = BlurPasses, Dim = BackgroundDim },
        Subtitles = new SubtitleSection
        {
            AutoCaptions = AutoCaptions,
            WhisperPrompt = WhisperPrompt,
            WhisperLanguage = WhisperLanguage,
            WhisperTranslate = WhisperTranslate,
            Style = CaptionStyle.Clone(),
            Layer = CaptionLayer.ToState(),
        },
    };

    /// <summary>The project settings as they are now, for the export dialog to describe and choose from.</summary>
    public LayoutPreset CaptureProjectSettings() => CaptureLayout();

    /// <summary>Writes the chosen parts of the project settings to a file. Returns a line for the status bar.</summary>
    public string ExportLayout(string path, bool layout, bool color, bool blur, bool subtitles)
    {
        var settings = CaptureLayout();
        if (!layout)
            settings.Layout = null;
        if (!color)
            settings.Color = null;
        if (!blur)
            settings.Blur = null;
        if (!subtitles)
            settings.Subtitles = null;

        var parts = new[] { layout ? "layout" : null, color ? "color filters" : null, blur ? "blur settings" : null, subtitles ? "subtitle settings" : null }
            .Where(p => p is not null).ToList();
        if (parts.Count == 0)
            return StatusText = "Nothing was chosen to export.";

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, LayoutJsonOptions));
            return StatusText = $"Project settings exported ({string.Join(", ", parts)}) to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusText = $"Could not export the layout: {ex.Message}";
        }
    }

    /// <summary>Reads a layout file, or returns null with the reason in the status bar.</summary>
    public LayoutPreset? ReadLayout(string path)
    {
        try
        {
            var layout = JsonSerializer.Deserialize<LayoutPreset>(File.ReadAllText(path));
            if (layout is { Layout: null, Color: null, Blur: null, Subtitles: null } or null)
            {
                StatusText = "That file holds no layout, color, blur or subtitle settings.";
                return null;
            }

            return layout;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText = $"Could not read the layout: {ex.Message}";
            return null;
        }
    }

    /// <summary>Takes over the chosen parts of a layout file. Parts the file does not have are skipped.</summary>
    public void ApplyLayout(LayoutPreset preset, bool layout, bool color, bool blur, bool subtitles)
    {
        var applied = new List<string>();

        if (layout && preset.Layout is { } layers)
        {
            CenterZoom = layers.CenterZoom is > 0 and var zoom ? Math.Clamp(zoom, SmallestCenterZoom, LargestCenterZoom) : 1;
            CenterOffsetX = Math.Clamp(layers.CenterOffsetX, -100, 100);
            CenterOffsetY = Math.Clamp(layers.CenterOffsetY, -100, 100);
            SetUiElements(layers.Elements ?? []);
            _layoutSourceAspect = layers.SourceAspectRatio;

            // A layout is only seen through the engine.
            FrameEngine = true;
            applied.Add($"layout with {UiElements.Count} element{(UiElements.Count == 1 ? "" : "s")}");
        }

        if (color && preset.Color is { } grade)
        {
            ColorContrast = Math.Clamp(grade.Contrast, 0, 2);
            ColorBrightness = Math.Clamp(grade.Brightness, -1, 1);
            ColorSaturation = Math.Clamp(grade.Saturation, 0, 3);
            ColorGamma = Math.Clamp(grade.Gamma, 0.1, 3);
            ColorHue = Math.Clamp(grade.Hue, -180, 180);
            ColorRed = Math.Clamp(grade.Red, -1, 1);
            ColorGreen = Math.Clamp(grade.Green, -1, 1);
            ColorBlue = Math.Clamp(grade.Blue, -1, 1);
            SharpenStrength = Math.Clamp(grade.Sharpen, 0, 1);
            applied.Add("color filters");
        }

        if (blur && preset.Blur is { } background)
        {
            BlurRadius = Math.Clamp(background.Radius, 5, 50);
            BlurPasses = Math.Clamp(background.Passes, 1, 5);
            BackgroundDim = Math.Clamp(background.Dim, -0.5, 0);
            applied.Add("blur settings");
        }

        if (subtitles && preset.Subtitles is { } captions)
        {
            AutoCaptions = captions.AutoCaptions;
            WhisperPrompt = captions.WhisperPrompt ?? "";
            WhisperLanguage = Pick(WhisperLanguages, captions.WhisperLanguage ?? "", "en");
            WhisperTranslate = captions.WhisperTranslate;
            SetCaptionStyle((captions.Style ?? new CaptionStyle()).Clone());
            SetCaptionLayer(captions.Layer);
            applied.Add("subtitle settings");
        }

        StatusText = applied.Count > 0 ? $"Imported {string.Join(", ", applied)}." : "Nothing was imported.";
        if (GetLayoutAspectWarning() is { } warning)
            StatusText += " " + warning;
    }

    // ----- The shape of video a layout was made on -----

    // Width over height of the video the current elements were marked on; 0 when not known.
    private double _layoutSourceAspect;

    private double CurrentSourceAspect => SourceWidth > 0 && SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 0;

    /// <summary>What a saved layout records: the shape its elements were marked on, or failing that the loaded video's.</summary>
    private double GetLayoutSourceAspect() =>
        Math.Round(_layoutSourceAspect > 0 && UiElements.Any(e => e.IsVideo) ? _layoutSourceAspect : CurrentSourceAspect, 4);

    /// <summary>
    /// The warning for a layout made on a video of another shape than the one loaded; null when they agree,
    /// when either shape is unknown, or when there is nothing cut from the video to go wrong.
    /// </summary>
    private string? GetLayoutAspectWarning()
    {
        var current = CurrentSourceAspect;
        var differs = _layoutSourceAspect > 0 && current > 0 && Math.Abs(_layoutSourceAspect - current) / current > 0.01;
        return differs && UiElements.Any(e => e.IsVideo)
            ? "Layout aspect ratio mismatch: UI element crops may require manual adjustment."
            : null;
    }
}
