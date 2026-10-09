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
    private StylePreset CaptureLayout() => new()
    {
        Layout = new LayoutSection
        {
            CenterZoom = CenterZoom,
            CenterOffsetX = CenterOffsetX,
            CenterOffsetY = CenterOffsetY,
            SourceAspectRatio = GetLayoutSourceAspect(),
            Vertical = UseVerticalResolution,
            MainVideoIndex = MainVideoIndex,
            MainLayer = _mainVideoRow.ToState(),
            BackgroundHidden = _backgroundRow.IsHidden,
            Layers = Layers.Select(e => e.ToState()).ToList(),
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
    public StylePreset CaptureStyle() => CaptureLayout();

    /// <summary>Writes the chosen parts of the project settings to a file. Returns a line for the status bar.</summary>
    public string ExportStyle(string path, bool layout, bool color, bool blur, bool subtitles)
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

        var parts = new[] { layout ? "layers" : null, color ? "color filters" : null, blur ? "blur settings" : null, subtitles ? "subtitle settings" : null }
            .Where(p => p is not null).ToList();
        if (parts.Count == 0)
            return StatusText = "Nothing was chosen to export.";

        if (settings.Layout is not null)
            EmbedMasks(settings);

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, LayoutJsonOptions));
            return StatusText = $"Style preset exported ({string.Join(", ", parts)}{(settings.Masks is { Count: > 0 } m ? $", {m.Count} mask{(m.Count == 1 ? "" : "s")} inside" : "")}) to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusText = $"Could not export the layout: {ex.Message}";
        }
    }

    /// <summary>The style as it is now, with the mask pictures of its layers inside it: what Export writes, whole.</summary>
    public StylePreset CaptureStyleWithMasks()
    {
        var style = CaptureLayout();
        EmbedMasks(style);
        return style;
    }

    /// <summary>
    /// Puts the mask pictures the layers use into the style itself, as Base64, and has the layers name them
    /// by file name alone: the file then needs nothing beside it.
    /// </summary>
    private static void EmbedMasks(StylePreset style)
    {
        foreach (var layer in StyleLayers(style))
        {
            var path = layer.MaskPath.Trim().Trim('"');
            if (!layer.CustomMask || path.Length == 0 || !File.Exists(path))
                continue;

            try
            {
                var name = Path.GetFileName(path);
                (style.Masks ??= [])[name] = Convert.ToBase64String(File.ReadAllBytes(path));
                layer.MaskPath = name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left as a path: the style still works on this machine.
            }
        }
    }

    /// <summary>Everything in a style that can carry a mask: its layers, the main video, and the caption box.</summary>
    private static IEnumerable<LayerState> StyleLayers(StylePreset style)
    {
        foreach (var layer in style.Layout?.Layers ?? [])
            yield return layer;
        if (style.Layout?.MainLayer is { } main)
            yield return main;
        if (style.Subtitles?.Layer is { } captions)
            yield return captions;
    }

    /// <summary>Writes the masks a style carries into the masks folder, and points its layers at them there.</summary>
    private void ExtractMasks(StylePreset style)
    {
        if (style.Masks is not { Count: > 0 } masks)
            return;

        foreach (var layer in StyleLayers(style))
        {
            if (!masks.TryGetValue(layer.MaskPath, out var encoded))
                continue;

            try
            {
                Directory.CreateDirectory(AppPaths.Masks);
                var path = Path.Combine(AppPaths.Masks, Path.GetFileName(layer.MaskPath));
                File.WriteAllBytes(path, Convert.FromBase64String(encoded));
                layer.MaskPath = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                StatusText = $"The mask {layer.MaskPath} could not be unpacked: {ex.Message}";
            }
        }
    }

    /// <summary>Reads a layout file, or returns null with the reason in the status bar.</summary>
    public StylePreset? ReadStyle(string path)
    {
        try
        {
            var layout = JsonSerializer.Deserialize<StylePreset>(File.ReadAllText(path));
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
    // The style preset in use: the last one applied. A double-click on a slider goes back to its values.
    private StylePreset? _activeStyle;

    /// <summary>The style presets in the Styles folder, by name, newest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> StylePresets { get; } = [];

    /// <summary>The style preset chosen from the list. Choosing one applies all of it.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _selectedStylePreset;

    private bool _listingStyles;

    /// <summary>The name a style preset is saved under, as typed in its dialog.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _styleName = "";

    /// <summary>
    /// Saves the layers, color, blur and subtitle settings as they are now as a style preset of the list: a
    /// .hpstyle file in the Styles folder, under the name typed. An existing one of that name is replaced.
    /// </summary>
    public void SaveStyle()
    {
        var name = string.Concat(StyleName.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length == 0)
        {
            StatusText = "Type a name for the style preset first.";
            return;
        }

        Directory.CreateDirectory(AppPaths.Styles);
        ExportStyle(Path.Combine(AppPaths.Styles, name + ".hpstyle"), layout: true, color: true, blur: true, subtitles: true);

        // It is now the style in use, and shown as chosen without being applied over itself.
        _activeStyle = CaptureStyle();
        RefreshStylePresets();
        _listingStyles = true;
        SelectedStylePreset = StylePresets.Contains(name) ? name : null;
        _listingStyles = false;
    }

    /// <summary>Reads the Styles folder again: called as the list is opened, so that it shows what is there now.</summary>
    public void RefreshStylePresets()
    {
        var chosen = SelectedStylePreset;
        _listingStyles = true;
        try
        {
            StylePresets.Clear();
            if (Directory.Exists(AppPaths.Styles))
            {
                foreach (var file in new DirectoryInfo(AppPaths.Styles).EnumerateFiles("*.hpstyle").OrderByDescending(f => f.LastWriteTime))
                    StylePresets.Add(Path.GetFileNameWithoutExtension(file.Name));
            }

            SelectedStylePreset = chosen is not null && StylePresets.Contains(chosen) ? chosen : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _listingStyles = false;
        }
    }

    partial void OnSelectedStylePresetChanged(string? value)
    {
        if (_listingStyles || value is null || ReadStyle(Path.Combine(AppPaths.Styles, value + ".hpstyle")) is not { } style)
            return;

        // A style is layers: the Layer Engine is what shows them.
        FrameEngine = true;
        ApplyStyle(style, layout: true, color: true, blur: true, subtitles: true);
    }

    /// <summary>
    /// What a double-click puts a slider or a number box back to: the value the style preset in use has for
    /// it, when the settings ask for that and the style has one. Null leaves it to the built-in default.
    /// </summary>
    /// <param name="source">What the control is bound to: this view model, or a layer.</param>
    /// <param name="property">The property it is bound to.</param>
    public double? GetResetValue(object? source, string? property)
    {
        if (_activeStyle is not { } style || property is null || AppSettings.Current.DoubleClickResetsTo != AppSettings.ResetToStyle)
            return null;

        if (ReferenceEquals(source, this))
        {
            return property switch
            {
                nameof(CenterZoom) => style.Layout?.CenterZoom,
                nameof(CenterOffsetX) => style.Layout?.CenterOffsetX,
                nameof(CenterOffsetY) => style.Layout?.CenterOffsetY,
                nameof(BlurRadius) => style.Blur?.Radius,
                nameof(BlurPasses) => style.Blur?.Passes,
                nameof(BackgroundDim) => style.Blur?.Dim,
                nameof(ColorContrast) => style.Color?.Contrast,
                nameof(ColorBrightness) => style.Color?.Brightness,
                nameof(ColorSaturation) => style.Color?.Saturation,
                nameof(ColorGamma) => style.Color?.Gamma,
                nameof(ColorHue) => style.Color?.Hue,
                nameof(ColorRed) => style.Color?.Red,
                nameof(ColorGreen) => style.Color?.Green,
                nameof(ColorBlue) => style.Color?.Blue,
                nameof(SharpenStrength) => style.Color?.Sharpen,
                _ => null,
            };
        }

        if (source is not Layer layer)
            return null;

        // The layer as the style has it: the main video, the caption box, or the layer of the same name and kind.
        var saved = layer.IsMainVideo ? style.Layout?.MainLayer
            : layer.IsCaptions ? style.Subtitles?.Layer
            : style.Layout?.Layers.FirstOrDefault(l => l.Kind == layer.Kind && l.Name == layer.Name);
        if (saved is null)
            return null;

        return property switch
        {
            nameof(Layer.PositionXPercent) => layer.IsMainVideo ? null : saved.PositionX * 100,
            nameof(Layer.PositionYPercent) => layer.IsMainVideo ? null : saved.PositionY * 100,
            nameof(Layer.SizePercent) => layer.IsMainVideo ? style.Layout?.CenterZoom * 100 : saved.SizeWidth * 100,
            nameof(Layer.SizeHeightPercent) => saved.SizeHeight * 100,
            nameof(Layer.PositionX) or nameof(Layer.PositionY) or nameof(Layer.SizeWidth) when layer.IsMainVideo => null,
            _ => typeof(LayerState).GetProperty(property)?.GetValue(saved) switch
            {
                double number => number,
                int number => number,
                _ => null,
            },
        };
    }

    public void ApplyStyle(StylePreset preset, bool layout, bool color, bool blur, bool subtitles)
    {
        _activeStyle = preset;
        var applied = new List<string>();

        if (layout && preset.Layout is { } layers)
        {
            CenterZoom = layers.CenterZoom is > 0 and var zoom ? Math.Clamp(zoom, SmallestCenterZoom, LargestCenterZoom) : 1;
            CenterOffsetX = Math.Clamp(layers.CenterOffsetX, -100, 100);
            CenterOffsetY = Math.Clamp(layers.CenterOffsetY, -100, 100);
            // The shape of the frame first: the layers are placed by fractions of it.
            if (layers.Vertical is { } vertical && vertical != UseVerticalResolution)
                UseVerticalResolution = vertical;

            ExtractMasks(preset);
            SetLayers(layers.Layers ?? []);
            _mainVideoRow.ApplyLook(layers.MainLayer);
            _backgroundRow.IsHidden = layers.BackgroundHidden;
            if (layers.MainVideoIndex is { } under)
                MainVideoIndex = Math.Clamp(under, 0, Layers.Count);
            _layoutSourceAspect = layers.SourceAspectRatio;

            // A layout is only seen through the engine.
            FrameEngine = true;
            applied.Add($"layout with {Layers.Count} layer{(Layers.Count == 1 ? "" : "s")}");
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

    // Width over height of the video the current layers were marked on; 0 when not known.
    private double _layoutSourceAspect;

    private double CurrentSourceAspect => SourceWidth > 0 && SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 0;

    /// <summary>What a saved layout records: the shape its layers were marked on, or failing that the loaded video's.</summary>
    private double GetLayoutSourceAspect() =>
        Math.Round(_layoutSourceAspect > 0 && Layers.Any(e => e.IsVideo) ? _layoutSourceAspect : CurrentSourceAspect, 4);

    /// <summary>
    /// The warning for a layout made on a video of another shape than the one loaded; null when they agree,
    /// when either shape is unknown, or when there is nothing cut from the video to go wrong.
    /// </summary>
    private string? GetLayoutAspectWarning()
    {
        var current = CurrentSourceAspect;
        var differs = _layoutSourceAspect > 0 && current > 0 && Math.Abs(_layoutSourceAspect - current) / current > 0.01;
        return differs && Layers.Any(e => e.IsVideo)
            ? "Layout aspect ratio mismatch: UI layer crops may require manual adjustment."
            : null;
    }
}
