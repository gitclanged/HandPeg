using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The Advanced tab: colour grading and sharpening (the single-frame preview that shows them is the GPU engine's capture,
// FramePreviewDialog), and layout files.
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
    /// The colour sliders' grade of the whole picture (5.2-AuditB: typed, it was FFmpeg eq / hue / unsharp text, with exactly the
    /// numbers that text wrote: each to three decimals): contrast, brightness, saturation, gamma and the RGB balance's per-channel
    /// gammas, a hue turn, the Sharpen strength. A control left where it does nothing adds nothing, so untouched video is not
    /// processed for no reason.
    /// </summary>
    public SequenceGrade GetSequenceGrade()
    {
        var contrast = Math.Clamp(ColorContrast, 0, 2);
        var brightness = Math.Clamp(ColorBrightness, -1, 1);
        var saturation = Math.Clamp(ColorSaturation, 0, 3);
        var gamma = Math.Clamp(ColorGamma, 0.1, 3);
        // The RGB balance is per-channel gammas: a slider at 0 is a gamma of 1; the ends, -1 and 1, are a gamma of a half and of two.
        var (red, green, blue) = (Math.Clamp(ColorRed, -1, 1), Math.Clamp(ColorGreen, -1, 1), Math.Clamp(ColorBlue, -1, 1));
        var isBalanced = IsChanged(red, 0) || IsChanged(green, 0) || IsChanged(blue, 0);

        ColorBalance? balance = isBalanced || IsChanged(contrast, NeutralContrast) || IsChanged(brightness, NeutralBrightness)
            || IsChanged(saturation, NeutralSaturation) || IsChanged(gamma, NeutralGamma)
            ? new ColorBalance(Math.Round(contrast, 3), Math.Round(brightness, 3), Math.Round(saturation, 3), Math.Round(gamma, 3),
                isBalanced ? Math.Round(Math.Pow(2, red), 3) : 1, isBalanced ? Math.Round(Math.Pow(2, green), 3) : 1, isBalanced ? Math.Round(Math.Pow(2, blue), 3) : 1)
            : null;

        var hue = Math.Clamp(ColorHue, -180, 180);

        // The Sharpen effect's strength (5.1-Polish), as its amount was written (Models.Effects.EffectRegistry.SharpenAmount times it).
        var sharpen = Math.Clamp(SharpenStrength, 0, 1);
        return new SequenceGrade
        {
            Balance = balance,
            Hue = Math.Abs(hue) >= 0.05 ? Math.Round(hue, 3) : null,
            Sharpen = IsChanged(sharpen, 0) ? Math.Round(Models.Effects.EffectRegistry.SharpenAmount * sharpen, 3) / Models.Effects.EffectRegistry.SharpenAmount : null,
        };

        // The controls move in hundredths; anything closer to neutral than that is neutral.
        static bool IsChanged(double value, double neutral) => Math.Abs(value - neutral) >= 0.005;
    }

    // ----- Layout files -----

    private static readonly JsonSerializerOptions LayoutJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// The layout (5.2-AuditB: the template of the clip it is taken from, TemplateBase: its Cutouts, its look, and its place
    /// written as the Game Preset file's Center values), the grading and the blur fill as they are now.
    /// </summary>
    private StylePreset CaptureLayout() => new()
    {
        Layout = new LayoutSection
        {
            CenterZoom = TemplatePlace(selected: true).Zoom,
            CenterOffsetX = TemplatePlace(selected: true).OffsetX,
            CenterOffsetY = TemplatePlace(selected: true).OffsetY,
            SourceAspectRatio = GetLayoutSourceAspect(),
            Vertical = UseVerticalResolution,
            MainVideoIndex = TemplateMainIndex(selected: true),
            MainLayer = TemplateBase(selected: true)?.ToState(),
            BackgroundHidden = _backgroundRow.IsHidden,
            Layers = TemplateLayers(selected: true),
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
        Blur = new BlurSection
        {
            Radius = BlurRadius, Passes = BlurPasses, Dim = BackgroundDim, Zoom = BackgroundZoom,
            Effects = CaptureBackgroundEffects(), EffectsBypassed = _backgroundRow.EffectsBypassed,
        },
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

        var parts = new[] { layout ? "layout and Cutouts" : null, color ? "color filters" : null, blur ? "blur settings" : null, subtitles ? "subtitle settings" : null }
            .Where(p => p is not null).ToList();
        if (parts.Count == 0)
            return StatusText = "Nothing was chosen to export.";

        if (settings.Layout is not null)
            EmbedMasks(settings);

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, LayoutJsonOptions));
            return StatusText = $"Game Preset exported ({string.Join(", ", parts)}{(settings.Masks is { Count: > 0 } m ? $", {m.Count} mask{(m.Count == 1 ? "" : "s")} inside" : "")}) to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusText = $"Could not export the layout: {ex.Message}";
        }
    }

    /// <summary>The Game Preset as it is now, with the mask pictures of its Cutouts inside it: what Export writes, whole.</summary>
    public StylePreset CaptureStyleWithMasks()
    {
        var style = CaptureLayout();
        EmbedMasks(style);
        return style;
    }

    /// <summary>
    /// Puts the mask pictures its Cutouts use into the Game Preset itself, as Base64, and has the Cutouts name them
    /// by file name alone: the file then needs nothing beside it.
    /// </summary>
    private static void EmbedMasks(StylePreset style)
    {
        var masks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in StyleLayers(style))
            MaskStore.Pack(layer, masks);
        style.Masks = masks.Count > 0 ? masks : null;
    }

    /// <summary>Everything in a Game Preset that can carry a mask: its Cutouts, its video's look, and the caption box.</summary>
    private static IEnumerable<LayerState> StyleLayers(StylePreset style)
    {
        foreach (var layer in style.Layout?.Layers ?? [])
            yield return layer;
        if (style.Layout?.MainLayer is { } main)
            yield return main;
        if (style.Subtitles?.Layer is { } captions)
            yield return captions;
    }

    /// <summary>Writes the masks a Game Preset carries into the masks folder, and points its Cutouts at them there.</summary>
    private void ExtractMasks(StylePreset style)
    {
        if (style.Masks is not { Count: > 0 } masks)
            return;

        foreach (var layer in StyleLayers(style))
        {
            if (MaskStore.Unpack(layer, masks) is { } problem)
                StatusText = problem;
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
    // The Game Preset in use: the last one applied. A double-click on a slider goes back to its values.
    private StylePreset? _activeStyle;

    /// <summary>The Game Presets in the Styles folder, by name, newest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> StylePresets { get; } = [];

    /// <summary>The Game Preset chosen from the list. Choosing one applies all of it.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _selectedStylePreset;

    private bool _listingStyles;

    /// <summary>The name a Game Preset is saved under, as typed in its dialog.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _styleName = "";

    /// <summary>
    /// Saves the layout and Cutouts, color, blur and subtitle settings as they are now as a Game Preset of the list: a
    /// .hpstyle file in the Styles folder, under the name typed. An existing one of that name is replaced.
    /// </summary>
    public void SaveStyle()
    {
        var name = string.Concat(StyleName.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length == 0)
        {
            StatusText = "Type a name for the Game Preset first.";
            return;
        }

        Directory.CreateDirectory(AppPaths.Styles);
        ExportStyle(Path.Combine(AppPaths.Styles, name + ".hpstyle"), layout: true, color: true, blur: true, subtitles: true);

        // It is now the Game Preset in use, and shown as chosen without being applied over itself.
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

        // With clips selected on the Timeline it goes on them alone, and the list lets go of it, so that it can be chosen again for the next clip.
        if (TimelineSelection?.Invoke() is { Count: > 0 } selected && GamePresetTargets(selected).Count > 0)
        {
            ApplyGamePreset(selected, value);
            _listingStyles = true;
            SelectedStylePreset = null;
            _listingStyles = false;
            return;
        }

        ApplyStyle(style, layout: true, color: true, blur: true, subtitles: true);
    }

    /// <summary>
    /// What a double-click puts a slider or a number box back to: the value the Game Preset in use has for
    /// it, when the settings ask for that and the Game Preset has one. Null leaves it to the built-in default.
    /// </summary>
    /// <param name="source">What the control is bound to: this view model, or a clip.</param>
    /// <param name="property">The property it is bound to.</param>
    public double? GetResetValue(object? source, string? property)
    {
        if (_activeStyle is not { } style || property is null || AppSettings.Current.DoubleClickResetsTo != AppSettings.ResetToPreset)
            return null;

        if (ReferenceEquals(source, this))
        {
            return property switch
            {
                nameof(BlurRadius) => style.Blur?.Radius,
                nameof(BlurPasses) => style.Blur?.Passes,
                nameof(BackgroundDim) => style.Blur?.Dim,
                nameof(BackgroundZoom) => style.Blur?.Zoom,
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

        // The clip as the Game Preset has it: the caption box, the Cutout of the same name and kind, or (a video clip the Game
        // Preset goes on, 5.2-AuditB) its video's look, placed by the file's Center values.
        var isBase = false;
        var saved = layer.IsCaptions ? style.Subtitles?.Layer
            : style.Layout?.Layers.FirstOrDefault(l => l.Kind == layer.Kind && l.Name == layer.Name);
        if (saved is null && layer.IsVideoFile && ClipOf(layer) is { ParentClipId: null })
            (saved, isBase) = (style.Layout?.MainLayer, true);
        if (saved is null)
            return null;

        return property switch
        {
            nameof(Layer.PositionXPercent) => isBase ? null : saved.PositionX * 100,
            nameof(Layer.PositionYPercent) => isBase ? null : saved.PositionY * 100,
            nameof(Layer.SizePercent) => isBase ? style.Layout?.CenterZoom * 100 : saved.SizeWidth * 100,
            nameof(Layer.SizeHeightPercent) => saved.SizeHeight * 100,
            nameof(Layer.PositionX) or nameof(Layer.PositionY) or nameof(Layer.SizeWidth) when isBase => null,
            _ => typeof(LayerState).GetProperty(property)?.GetValue(saved) switch
            {
                double number => number,
                int number => number,
                _ => null,
            },
        };
    }

    /// <summary>
    /// Puts a Game Preset's parts in place: one undo step for all of it, its colour grade and blur included (4.4: they are
    /// in the step), and no step of their own for the looks it sets on the way.
    /// </summary>
    public void ApplyStyle(StylePreset preset, bool layout, bool color, bool blur, bool subtitles)
    {
        if (layout || subtitles || color || blur)
            Checkpoint("apply a Game Preset");
        using var held = HoldLookSteps();

        _activeStyle = preset;
        var applied = new List<string>();

        if (layout && preset.Layout is { } layers)
        {
            // The shape of the frame first: the clips are placed by fractions of it.
            if (layers.Vertical is { } vertical && vertical != UseVerticalResolution)
                UseVerticalResolution = vertical;

            // On the selected video clips, or every top-level one (5.2-AuditB: the per-clip template path); with none, the next
            // file opened takes it. Its blur fill is the one a file opened from now on gets.
            ExtractMasks(preset);
            _backgroundRow.IsHidden = layers.BackgroundHidden;
            TakeBlurFill(layers.BackgroundHidden);
            var on = TemplateFrom(preset) is { } template ? PutTemplate(template, "apply a Game Preset") : 0;
            _layoutSourceAspect = layers.SourceAspectRatio;
            applied.Add(on == 0 ? "layout, for the next video opened" : $"layout on {on} clip{(on == 1 ? "" : "s")}");
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
            TakeBlur(background);
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

        // Settings may say the default encoding settings win over what a Game Preset sets (a tall frame, say).
        ApplyEncodingDefaultsOverStyle();

        Log(applied.Count > 0 ? $"Applied Game Preset: {string.Join(", ", applied)}" : "Nothing was imported");
        if (GetLayoutAspectWarning() is { } warning)
            StatusText += " " + warning;
    }

    /// <summary>A preset's settings for the blur fill's look (the sequence's one, every clip's that has a blur fill).</summary>
    private void TakeBlur(BlurSection background)
    {
        BlurRadius = Math.Clamp(background.Radius, 0, Models.Effects.EffectRegistry.BackgroundMostRadius);
        BlurPasses = Math.Clamp(background.Passes, 1, MostBlurPasses);
        BackgroundDim = Math.Clamp(background.Dim, -0.5, 0);
        BackgroundZoom = Math.Clamp(background.Zoom, 1, MostBackgroundZoom);
        SetBackgroundEffects(background.Effects, background.EffectsBypassed);
    }

    // ----- The shape of video a layout was made on -----

    // Width over height of the video the current Cutouts were marked on; 0 when not known.
    private double _layoutSourceAspect;

    private double CurrentSourceAspect => SourceWidth > 0 && SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 0;

    /// <summary>What a saved layout records: the shape its Cutouts were marked on, or failing that the loaded video's.</summary>
    private double GetLayoutSourceAspect() =>
        Math.Round(_layoutSourceAspect > 0 && Layers.Any(IsCutout) ? _layoutSourceAspect : CurrentSourceAspect, 4);

    /// <summary>
    /// The warning for a layout made on a video of another shape than the one loaded; null when they agree,
    /// when either shape is unknown, or when there is nothing cut from the video to go wrong.
    /// </summary>
    private string? GetLayoutAspectWarning()
    {
        var current = CurrentSourceAspect;
        var differs = _layoutSourceAspect > 0 && current > 0 && Math.Abs(_layoutSourceAspect - current) / current > 0.01;
        return differs && Layers.Any(IsCutout)
            ? "Layout aspect ratio mismatch: the Cutouts' crops may need adjusting by hand."
            : null;
    }
}
