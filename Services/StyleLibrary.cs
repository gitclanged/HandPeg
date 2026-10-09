using System.IO;
using System.Text.Json;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// The style presets HandPeg comes with: one for each game whose HUD it knows, as ordinary .hpstyle files in
/// the Styles folder. They are written there once, so that they sit beside the user's own in every list (the
/// launch window, Import, the settings) and can be renamed, changed or deleted like any other.
/// </summary>
public static class StyleLibrary
{
    // Raised when the built-in styles change, so that the new ones are written out.
    private const int Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FileNameFor(HudGame game) => $"{game.Name} HUD (Vertical).hpstyle";

    /// <summary>Writes the built-in styles that have not been written before. Needs the UI thread: the masks are drawn.</summary>
    public static void EnsureBuiltIns()
    {
        var settings = AppSettings.Current;
        if (settings.BuiltInStylesVersion >= Version)
            return;

        try
        {
            Directory.CreateDirectory(AppPaths.Styles);

            // The lists show the newest first: written in reverse, the first game is the first style.
            var stamp = DateTime.Now.AddMinutes(-HudLibrary.Games.Count - 1);
            foreach (var game in HudLibrary.Games.Reverse())
            {
                var path = Path.Combine(AppPaths.Styles, FileNameFor(game));
                stamp = stamp.AddMinutes(1);
                if (File.Exists(path))
                    continue;

                File.WriteAllText(path, JsonSerializer.Serialize(Build(game), JsonOptions));
                File.SetLastWriteTime(path, stamp);
            }

            settings.BuiltInStylesVersion = Version;
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start.
        }
    }

    /// <summary>
    /// The style for a game: a tall frame, the video across its middle, and the pieces of the game's HUD cut
    /// out as masked layers above and below it. The masks are inside the style, as in any exported one.
    /// </summary>
    public static StylePreset Build(HudGame game)
    {
        var layers = HudLibrary.BuildLayers(game, vertical: true);
        var masks = new Dictionary<string, string>();
        var track = 0;
        foreach (var layer in layers)
        {
            layer.TrackId = ++track;
            if (!layer.CustomMask || !File.Exists(layer.MaskPath))
                continue;

            var name = Path.GetFileName(layer.MaskPath);
            masks[name] = Convert.ToBase64String(File.ReadAllBytes(layer.MaskPath));
            layer.MaskPath = name;
        }

        return new StylePreset
        {
            Layout = new LayoutSection { CenterZoom = 1, SourceAspectRatio = 1.7778, Vertical = true, MainVideoIndex = 0, Layers = layers },
            Masks = masks.Count > 0 ? masks : null,
        };
    }
}
