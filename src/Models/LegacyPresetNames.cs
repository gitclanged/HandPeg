namespace HandPegApp.Models;

/// <summary>
/// Maps legacy Game Preset names to current ones for Track Preset Nodes (<c>TimelineTrackModel.PresetNodeName</c>, on document read) and
/// drop-overlay entries (<c>AppSettings.SplashStylePresets</c>, on settings read), so references follow renamed files. Game Presets only;
/// Smart Rules and the default Export Preset are never mapped. All other names pass through unchanged.
/// </summary>
public static class LegacyPresetNames
{
    // The old names, each with the one it is now. Case is ignored, and so are spaces round the name.
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Overwatch 2 Vertical HUD"] = "Overwatch Vertical",
        ["Overwatch Vertical HUDv2"] = "Overwatch Vertical",
        ["Fortnite draft"] = "Fortnite Vertical",
    };

    /// <summary>The old names and the new, for a test or a report.</summary>
    public static IReadOnlyDictionary<string, string> All => Renamed;

    /// <summary>
    /// Current name for a legacy name (bare or with the Game Preset extension, form preserved); other values returned unchanged.
    /// </summary>
    public static string? Map(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var trimmed = name.Trim();
        if (Renamed.TryGetValue(trimmed, out var now))
            return now;

        if (trimmed.EndsWith(StylePreset.FileExtension, StringComparison.OrdinalIgnoreCase)
            && Renamed.TryGetValue(trimmed[..^StylePreset.FileExtension.Length].TrimEnd(), out var file))
        {
            return file + StylePreset.FileExtension;
        }

        return name;
    }
}
