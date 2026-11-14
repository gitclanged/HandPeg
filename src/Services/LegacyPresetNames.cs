namespace HandPegApp.Services;

/// <summary>
/// The Game Presets' names before 5.5.0, read as today's (Pass 5.5.1, the user's Q2(b)): a Track Preset Node of a project
/// (<c>TimelineTrackModel.PresetNodeName</c>, mapped as the document is read) and the drop overlay's Game Presets of the settings
/// (<c>AppSettings.SplashStylePresets</c>, mapped as they are read) that still name one by an old name are given the new one, so a node or
/// an overlay entry never points at a file that took its new name. Game Preset surfaces only (Pass 5.5.2, the user's Q2): Smart Rules and
/// the default preset name Export Presets and are never mapped. One central table: every other name, the user's own included, is left
/// exactly as it was.
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
    /// A preset's name as it is today: an old name (alone, or as a file name with the Game Preset extension) its new one, in the same form;
    /// any other name (null, empty, the user's own) as it is.
    /// </summary>
    public static string? Map(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var trimmed = name.Trim();
        if (Renamed.TryGetValue(trimmed, out var now))
            return now;

        if (trimmed.EndsWith(StyleFile.Extension, StringComparison.OrdinalIgnoreCase)
            && Renamed.TryGetValue(trimmed[..^StyleFile.Extension.Length].TrimEnd(), out var file))
        {
            return file + StyleFile.Extension;
        }

        return name;
    }
}
