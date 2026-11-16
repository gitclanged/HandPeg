using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Legacy whole-frame slider grade (shown only in the Color tab's Legacy Grade fold, whose Convert turns it into Sequence Output cards)
// and layout files.
public partial class MainViewModel
{
    // Neutral value of each control.
    private const double NeutralContrast = 1;
    private const double NeutralBrightness = 0;
    private const double NeutralSaturation = 1;
    private const double NeutralGamma = 1;

    [ObservableProperty] private double _colorContrast = NeutralContrast;
    [ObservableProperty] private double _colorBrightness = NeutralBrightness;
    [ObservableProperty] private double _colorSaturation = NeutralSaturation;
    [ObservableProperty] private double _colorGamma = NeutralGamma;

    /// <summary>Hue rotation in degrees (-180..180).</summary>
    [ObservableProperty] private double _colorHue;

    // RGB balance per primary (-1..1; negative reduces).
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
    /// Typed legacy slider grade (values rounded to 3 decimals, matching the original eq/hue/unsharp semantics): contrast, brightness,
    /// saturation, gamma, per-channel RGB gammas, hue rotation and Sharpen strength. Neutral controls contribute nothing.
    /// </summary>
    public SequenceGrade GetSequenceGrade()
    {
        var contrast = Math.Clamp(ColorContrast, 0, 2);
        var brightness = Math.Clamp(ColorBrightness, -1, 1);
        var saturation = Math.Clamp(ColorSaturation, 0, 3);
        var gamma = Math.Clamp(ColorGamma, 0.1, 3);
        // RGB balance maps to per-channel gamma: 0 -> 1.0, -1 -> 0.5, +1 -> 2.0.
        var (red, green, blue) = (Math.Clamp(ColorRed, -1, 1), Math.Clamp(ColorGreen, -1, 1), Math.Clamp(ColorBlue, -1, 1));
        var isBalanced = IsChanged(red, 0) || IsChanged(green, 0) || IsChanged(blue, 0);

        ColorBalance? balance = isBalanced || IsChanged(contrast, NeutralContrast) || IsChanged(brightness, NeutralBrightness)
            || IsChanged(saturation, NeutralSaturation) || IsChanged(gamma, NeutralGamma)
            ? new ColorBalance(Math.Round(contrast, 3), Math.Round(brightness, 3), Math.Round(saturation, 3), Math.Round(gamma, 3),
                isBalanced ? Math.Round(Math.Pow(2, red), 3) : 1, isBalanced ? Math.Round(Math.Pow(2, green), 3) : 1, isBalanced ? Math.Round(Math.Pow(2, blue), 3) : 1)
            : null;

        var hue = Math.Clamp(ColorHue, -180, 180);

        // Sharpen strength (amount = Models.Effects.EffectRegistry.SharpenAmount x strength).
        var sharpen = Math.Clamp(SharpenStrength, 0, 1);
        return new SequenceGrade
        {
            Balance = balance,
            Hue = Math.Abs(hue) >= 0.05 ? Math.Round(hue, 3) : null,
            Sharpen = IsChanged(sharpen, 0) ? Math.Round(Models.Effects.EffectRegistry.SharpenAmount * sharpen, 3) / Models.Effects.EffectRegistry.SharpenAmount : null,
        };

        // Controls step by 0.01; anything closer to neutral counts as neutral.
        static bool IsChanged(double value, double neutral) => Math.Abs(value - neutral) >= 0.005;
    }

    // ----- Legacy grade to Sequence Output cards (Color tab Legacy Grade fold) -----

    /// <summary>Whether a legacy slider grade or legacy LUT remains (the Legacy Grade fold shows while true).</summary>
    public bool HasLegacyGrade => !GetSequenceGrade().IsNeutral || !string.IsNullOrWhiteSpace(LutPath);

    /// <summary>
    /// Whether a Sequence Output card already holds a LUT (LUT 3D with a file, or Color Grade with a LUT; enabled or not). Convert then
    /// leaves the legacy LUT in place (drawn before all cards) so no stack holds two LUT cards.
    /// </summary>
    public bool SequenceOutputHoldsLut => SequenceEffects.Any(HoldsLutFile);

    // Card with a LUT file (the plan binds the first such card's table: GpuScenePlan.Create.LookCards).
    private static bool HoldsLutFile(EffectInstance card) =>
        card.Type == EffectRegistry.Lut ? card.GetText("file").Trim().Trim('"').Length > 0
        : card.Type == EffectRegistry.ColorStudio && card.GetText(ColorGradeKeys.Lut).Trim().Trim('"').Length > 0;

    /// <summary>
    /// Converts the legacy grade into equivalent Sequence Output cards (<see cref="LegacyGradeCards"/>) at the stack front (legacy drew
    /// first), resetting sliders and clearing the LUT path in one undo step (SequenceLook includes the path). Never duplicates Color Grade
    /// or LUT: an existing LUT card keeps the legacy LUT in place (only sliders move); an existing Color Grade card gets the LUT as a LUT 3D
    /// card (the existing card stays the Color Studio target); else a new Color Grade card holds it at 100 %. False when nothing to convert.
    /// </summary>
    public bool ConvertLegacyGrade()
    {
        var grade = GetSequenceGrade();
        var lut = (LutPath ?? "").Trim().Trim('"');
        if (lut.Length > 0 && SequenceOutputHoldsLut)
            lut = "";
        if (grade.IsNeutral && lut.Length == 0)
            return false;

        var hasGrade = SequenceEffects.Any(c => c.Type == EffectRegistry.ColorStudio);
        var cards = LegacyGradeCards(grade, lut, FrameWidth, lutAsCard: hasGrade);
        Checkpoint("convert Legacy Grade");
        using (HoldLookSteps())
        using (OwnLookScope())
        {
            for (var i = 0; i < cards.Count; i++)
                SequenceEffects.Insert(i, cards[i]);
            ResetColor();
        }

        // After the step (which captures the path, so undo/redo move it with the cards).
        if (lut.Length > 0)
            LutPath = "";
        return true;
    }

    /// <summary>
    /// Legacy Grade Reset: neutral sliders and no legacy LUT in one undo step (SequenceLook holds the path), so a LUT kept by Convert
    /// can still be removed. False when nothing to reset.
    /// </summary>
    public bool ResetLegacyGrade()
    {
        if (!HasLegacyGrade)
            return false;

        Checkpoint("reset Legacy Grade");
        using (HoldLookSteps())
        {
            ResetColor();
            LutPath = "";
        }

        return true;
    }

    /// <summary>
    /// Pure (golden-tested) legacy-grade card list in GpuScenePlan's Whole-entry order (LUT, grade, hue, sharpen), lanes matching its packet:
    /// Color Grade with the LUT at 100 % (others neutral) or a LUT 3D card when <paramref name="lutAsCard"/>; Brightness &amp; Contrast
    /// (gamma x green gamma, channel balance as Cb/Cr gammas per PSColorGrade); Hue (saturation 1); Sharpen (radius of a 5 x 5 unsharp
    /// kernel at <paramref name="frameWidth"/>). No LUT card for an empty <paramref name="lut"/>.
    /// </summary>
    public static List<EffectInstance> LegacyGradeCards(in SequenceGrade grade, string lut, int frameWidth, bool lutAsCard = false)
    {
        var cards = new List<EffectInstance>();
        EffectInstance? Card(string type) => EffectRegistry.Find(type) is { } definition ? definition.NewEffect() : null;

        if (lut.Length > 0 && Card(lutAsCard ? EffectRegistry.Lut : EffectRegistry.ColorStudio) is { } holder)
        {
            if (lutAsCard)
            {
                holder.SetText("file", lut);
            }
            else
            {
                holder.SetText(ColorGradeKeys.Lut, lut);
                holder.Set(ColorGradeKeys.LutMix, 100);
            }

            cards.Add(holder);
        }

        if (grade.Balance is { } balance && Card(EffectRegistry.Grade) is { } eq)
        {
            var (red, green, blue) = (balance.GammaRed, balance.GammaGreen, balance.GammaBlue);
            var balanced = Math.Abs(red - 1) > 0.005 || Math.Abs(green - 1) > 0.005 || Math.Abs(blue - 1) > 0.005;
            eq.Set("contrast", balance.Contrast);
            eq.Set("brightness", balance.Brightness);
            eq.Set("saturation", balance.Saturation);
            eq.Set("gamma", balance.Gamma * green);
            eq.Set(EffectRegistry.GradeBalanceBlue, balanced ? Math.Sqrt(blue / green) : 0);
            eq.Set(EffectRegistry.GradeBalanceRed, balanced ? Math.Sqrt(red / green) : 0);
            cards.Add(eq);
        }

        if (grade.Hue is { } hue && Card(EffectRegistry.Hue) is { } turn)
        {
            turn.Set("h", hue);
            turn.Set("s", 1);
            cards.Add(turn);
        }

        if (grade.Sharpen is { } strength && Card(EffectRegistry.Sharpen) is { } sharpen)
        {
            sharpen.Set("strength", strength);
            sharpen.Set("radius", 2 * 1080.0 / Math.Max(frameWidth, 1));
            sharpen.Set("threshold", 0);
            cards.Add(sharpen);
        }

        return cards;
    }

    // ----- Layout files -----

    private static readonly JsonSerializerOptions LayoutJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Current layout (TemplateBase's Cutouts, look and placement as legacy Center values), grading and blur fill.
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

    /// <summary>Current project settings for the export dialog.</summary>
    public StylePreset CaptureStyle() => CaptureLayout();

    /// <summary>Exports selected project setting sections to a file; returns a status line.</summary>
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
            return Fail($"Could not export the layout: {ex.Message}", "Export a layout", ex);
        }
    }

    /// <summary>Self-contained current Game Preset with embedded Cutout masks, as written by Export.</summary>
    public StylePreset CaptureStyleWithMasks()
    {
        var style = CaptureLayout();
        EmbedMasks(style);
        return style;
    }

    /// <summary>Embeds Cutout mask images as Base64 and rewrites references to bare file names, making the preset self-contained.</summary>
    private static void EmbedMasks(StylePreset style)
    {
        var masks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in StyleLayers(style))
            MaskStore.Pack(layer, masks);
        style.Masks = masks.Count > 0 ? masks : null;
    }

    /// <summary>Mask-bearing parts of a Game Preset: Cutouts, video look and caption box.</summary>
    private static IEnumerable<LayerState> StyleLayers(StylePreset style)
    {
        foreach (var layer in style.Layout?.Layers ?? [])
            yield return layer;
        if (style.Layout?.MainLayer is { } main)
            yield return main;
        if (style.Subtitles?.Layer is { } captions)
            yield return captions;
    }

    /// <summary>Extracts embedded masks to the masks folder and repoints Cutouts at them.</summary>
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

    /// <summary>Reads a layout file; null (reason in the status bar) on failure.</summary>
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
            Fail($"Could not read the layout: {ex.Message}", "Read a layout", ex);
            return null;
        }
    }

    /// <summary>Applies selected sections of a layout file, skipping absent ones.</summary>
    // Active Game Preset (last applied); slider double-click resets to its values.
    private StylePreset? _activeStyle;

    /// <summary>Game Presets in the Styles folder, newest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> StylePresets { get; } = [];

    /// <summary>Selected Game Preset; selecting applies it fully.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _selectedStylePreset;

    private bool _listingStyles;

    /// <summary>Save name typed in the Game Preset dialog.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _styleName = "";

    /// <summary>
    /// Saves current layout, Cutouts, colour, blur and subtitle settings as a .hpstyle Game Preset in the Styles folder, replacing
    /// a same-named one.
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

        // Mark it active and selected without re-applying it.
        _activeStyle = CaptureStyle();
        RefreshStylePresets();
        _listingStyles = true;
        SelectedStylePreset = StylePresets.Contains(name) ? name : null;
        _listingStyles = false;
    }

    /// <summary>Rescans the Styles folder (called when the list opens).</summary>
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

        // With a Timeline selection, apply to those clips only and clear the list selection so it can be re-chosen for the next clip.
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
    /// Double-click reset value for a slider or number box: the active Game Preset's value when enabled and present; null uses the
    /// built-in default.
    /// </summary>
    /// <param name="source">Binding source: this VM or a clip.</param>
    /// <param name="property">Bound property name.</param>
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

        // Preset counterpart: caption box, same-name/kind Cutout, or for a target video clip the preset video look placed by Center values.
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

    /// <summary>Applies a Game Preset as one undo step (grade and blur included), suppressing per-look steps.</summary>
    public void ApplyStyle(StylePreset preset, bool layout, bool color, bool blur, bool subtitles)
    {
        if (layout || subtitles || color || blur)
            Checkpoint("apply a Game Preset");
        using var held = HoldLookSteps();

        _activeStyle = preset;
        var applied = new List<string>();

        if (layout && preset.Layout is { } layers)
        {
            // Frame shape first: clip placement is in frame fractions.
            if (layers.Vertical is { } vertical && vertical != UseVerticalResolution)
                UseVerticalResolution = vertical;

            // Apply to TemplateTargets, or pend for the next opened file; its blur fill becomes the default for future opens.
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

        // A setting may let default encoding settings override preset values (e.g. a vertical frame).
        ApplyEncodingDefaultsOverStyle();

        Log(applied.Count > 0 ? $"Applied Game Preset: {string.Join(", ", applied)}" : "Nothing was imported");
        if (GetLayoutAspectWarning() is { } warning)
            StatusText += " " + warning;
    }

    /// <summary>Applies a preset's blur fill look (Sequence-level, used by every clip with blur fill).</summary>
    private void TakeBlur(BlurSection background)
    {
        BlurRadius = Math.Clamp(background.Radius, 0, Models.Effects.EffectRegistry.BackgroundMostRadius);
        BlurPasses = Math.Clamp(background.Passes, 1, MostBlurPasses);
        BackgroundDim = Math.Clamp(background.Dim, -0.5, 0);
        BackgroundZoom = Math.Clamp(background.Zoom, 1, MostBackgroundZoom);
        SetBackgroundEffects(background.Effects, background.EffectsBypassed);
    }

    // ----- The shape of video a layout was made on -----

    // Aspect (w/h) of the video the current Cutouts were marked on; 0 when unknown.
    private double _layoutSourceAspect;

    private double CurrentSourceAspect => SourceWidth > 0 && SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 0;

    /// <summary>Aspect recorded in a saved layout: the Cutouts' marking aspect, else the loaded video's.</summary>
    private double GetLayoutSourceAspect() =>
        Math.Round(_layoutSourceAspect > 0 && Layers.Any(IsCutout) ? _layoutSourceAspect : CurrentSourceAspect, 4);

    /// <summary>
    /// Aspect mismatch warning for a layout made on a different-aspect video; null when matching, unknown, or no Cutouts.
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
