using System.Globalization;
using System.Text;
using System.Text.Json;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// startup-json-source-generation (Pass 5.2D): the files read before the first frame (the settings, the export
/// presets, the encoders found before, the panes' arrangement, and since 5.4C the side windows) go through generated metadata (AppSettingsJsonContext) instead of
/// reflection. Each is written by both, byte for byte the same, and read back by the generated one to the same bytes again.
/// </summary>
public static class StartupJsonTests
{
    public static GraphResult Run()
    {
        var text = new StringBuilder("# The start-up reads through generated metadata: every file the same bytes as reflection writes it, and read back to them.\n");
        var problems = new List<string>();
        try
        {
            Body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("startup-json-source-generation", text.ToString(), problems, 0);
    }

    private static readonly JsonSerializerOptions Reflection = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Generated = new() { WriteIndented = true, TypeInfoResolver = AppSettingsJsonContext.Default };
    private static readonly JsonSerializerOptions ReflectionCompact = new();

    private static void Body(StringBuilder text, List<string> problems)
    {
        // The settings: as a first start makes them, and as a long-used copy has them (keys, the dial, layouts, rules, captions).
        Same(text, problems, "settings as a first start makes them", new AppSettings(), AppSettingsJsonContext.Default.AppSettings);
        Same(text, problems, "settings in long use", Used(), AppSettingsJsonContext.Default.AppSettings);

        // The export presets HandPeg comes with (5.4B: ExportPresetCatalog, in code; List<T> read, IEnumerable<T> written; a user's file holds
        // presets of the same shape), and one with every kind of part a preset holds.
        var presets = ExportPresetCatalog.BuiltIns.ToList();
        Same(text, problems, $"the built-in export presets ({presets.Count})", presets, AppSettingsJsonContext.Default.ListEncodingPreset);
        SameWritten(text, problems, "the presets as a user's presets.json holds them", (IEnumerable<EncodingPreset>)presets);
        Same(text, problems, "a preset with layers, keys, effects, captions and sound tracks", [Full()], AppSettingsJsonContext.Default.ListEncodingPreset);

        // The encoders found before, and the panes' arrangement (compact, as they are written).
        var probe = new EncoderProber.SavedResult("ffmpeg|N-1|card 1|31.0", ["h264_nvenc", "hevc_amf"], ["av1_qsv: no device"], ["hevc_nvenc"], true);
        SameCompact(text, problems, "the encoders found before", probe, AppSettingsJsonContext.Default.SavedResult);
        var layout = new DockLayoutState
        {
            Main = DockNode.Split(false, (DockNode.Group("player", "media"), 0.6), (DockNode.Split(true, (DockNode.Group("timeline"), 0.7), (DockNode.Group("effects", "keys"), 0.3)), 0.4)),
            Side = DockNode.Group("loupe"), SideOpen = true, SideBounds = [10, 20, 800, 600],
        };
        SameCompact(text, problems, "the panes' arrangement", layout, AppSettingsJsonContext.Default.DockLayoutState);
        if (DockLayoutState.Read(JsonSerializer.Serialize(layout, ReflectionCompact)) is not { } read || JsonSerializer.Serialize(read, ReflectionCompact) != JsonSerializer.Serialize(layout, ReflectionCompact))
            problems.Add("DockLayoutState.Read did not give the arrangement back");

        // The side windows (5.4C: AppSettings.SideWindows), a Pane Docker with panes and a place, and a Live Video Preview full screen.
        var sides = SideWindows();
        SameCompact(text, problems, "the side windows", sides, AppSettingsJsonContext.Default.ListSideWindowState);
        if (JsonSerializer.Serialize(SideWindowStates.Read(JsonSerializer.Serialize(sides, ReflectionCompact)), ReflectionCompact) != JsonSerializer.Serialize(sides, ReflectionCompact))
            problems.Add("SideWindowStates.Read did not give the side windows back");
    }

    // A settings copy as it looks after a while: none of the lists empty, the nullable ones set, values off their defaults.
    private static AppSettings Used()
    {
        var settings = new AppSettings
        {
            FfmpegPath = @"C:\Tools\ffmpeg\ffmpeg.exe", Theme = "Midnight", UiScalePercent = 125, AutosaveMinutes = 3, DevVerboseLogging = true,
            ShowExplorerThumbnails = false, ShowTimelineThumbnails = true, DefaultQuality = 23,
            DefaultCaptionStyle = new CaptionStyle(), HiddenTabs = ["Color", "Sound"],
            CustomKeybinds = new Dictionary<string, List<string>> { ["marker.ping"] = ["Ctrl+Shift+J"], ["edit.split"] = ["S", "Mouse4"] },
            LightningDial = [new DialCategoryState { Name = "Cut", Glyph = "\uE8C6", Actions = ["edit.split", "edit.delete"] }],
            SavedLayouts = [new SavedDockLayout { Slot = 1, Name = "Mine & \"yours\"", Layout = "{\"Version\":3}" }],
            SmartRules = [new SmartRule { Type = "Folder", Path = @"D:\Clips", Preset = "YouTube 1080p" }],
            SquisherPresets = SquishPreset.Defaults(),
            SideWindows = JsonSerializer.Serialize(SideWindows(), ReflectionCompact),
            DefaultStartupLayout = "saved:2", PerTabWindowLayouts = false, ExportQualityPreviewLoupe = true,
        };
        settings.SplashStylePresets.Add("Shorts");
        return settings;
    }

    private static List<SideWindowState> SideWindows() =>
    [
        new SideWindowState
        {
            Id = "side-1", Mode = SideWindowState.PanesMode, Placement = [1930, 40, 2650, 760], Maximized = true,
            Tree = DockNode.Split(true, (DockNode.Group("media", "effects"), 0.6), (DockNode.Group("keys"), 0.4)),
        },
        new SideWindowState { Id = "side-2", Mode = SideWindowState.PreviewMode, Bounds = [10, 20, 800, 450], FullScreen = true },
    ];

    private static EncodingPreset Full()
    {
        var keys = new List<Keyframe> { new(0, 0.1), new(1.5, 0.9, EasingType.Linear), new(3, 0.4) { BezierX1 = 0.2, BezierY1 = 0.1 } };
        var layer = new LayerState
        {
            Kind = LayerKind.Video, Name = "HUD \u00e9t\u00e9", PositionX = 0.1, SizeWidth = 0.5, KeysX = keys, KeysOpacity = keys, TiltX = 12,
            Effects = [new EffectState { Type = EffectRegistry.Mask, On = true, Values = new Dictionary<string, JsonElement> { ["amount"] = JsonSerializer.SerializeToElement(0.5) } }],
            AudioFilters = new TrackAudioFilters { Limiter = true, LimiterCeiling = -3, StudioVoice = true, StudioVoiceMixKeys = keys },
        };
        return new EncodingPreset
        {
            Name = "Everything", CropPercent = [1, 2, 3, 4], Layers = [layer], MainLayer = layer, CaptionLayer = layer, CaptionStyle = new CaptionStyle(),
            SequenceEffects = [new EffectState { Type = EffectRegistry.Edges, On = false }], AudioTracks = [new AudioTrackState(0, "keep", "aac", "192k", "Mic", -3, new TrackAudioFilters { DeNoise = true }, 0.25, [new AudioPiece(0, 1, false)], true)],
        };
    }

    // Indented, as SaveAsCurrent and PresetStore.Save write them; the generated reading of the reflection's bytes written again.
    private static void Same<T>(StringBuilder text, List<string> problems, string what, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var (theirs, ours) = (JsonSerializer.Serialize(value, Reflection), JsonSerializer.Serialize(value, Generated));
        Check(text, problems, what, theirs, ours, JsonSerializer.Serialize(JsonSerializer.Deserialize(theirs, info), Generated));
    }

    private static void SameWritten(StringBuilder text, List<string> problems, string what, IEnumerable<EncodingPreset> value)
    {
        var (theirs, ours) = (JsonSerializer.Serialize(value, Reflection), JsonSerializer.Serialize(value, Generated));
        Check(text, problems, what, theirs, ours, ours);
    }

    private static void SameCompact<T>(StringBuilder text, List<string> problems, string what, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var (theirs, ours) = (JsonSerializer.Serialize(value, ReflectionCompact), JsonSerializer.Serialize(value, info));
        Check(text, problems, what, theirs, ours, JsonSerializer.Serialize(JsonSerializer.Deserialize(theirs, info)!, info));
    }

    private static void Check(StringBuilder text, List<string> problems, string what, string theirs, string ours, string again)
    {
        text.Append(CultureInfo.InvariantCulture, $"{what}: written the same, read back to the same\n");
        if (ours != theirs)
            problems.Add($"{what}: the generated writing differs from reflection's at character {FirstDifference(theirs, ours)}: ...{Around(theirs, ours)}");
        else if (again != theirs)
            problems.Add($"{what}: read back and written again, it differs at character {FirstDifference(theirs, again)}: ...{Around(theirs, again)}");
    }

    private static int FirstDifference(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
            i++;
        return i;
    }

    private static string Around(string a, string b)
    {
        var at = FirstDifference(a, b);
        string Cut(string s) => s.Substring(Math.Max(0, at - 60), Math.Min(120, s.Length - Math.Max(0, at - 60)));
        return $"reflection \"{Cut(a)}\" against \"{Cut(b)}\"";
    }
}
