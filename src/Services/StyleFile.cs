using System.IO;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>A style preset as the launch window and the settings list it: a file in the Styles folder.</summary>
public sealed record StyleFile(string Path)
{
    public const string Extension = Models.StylePreset.FileExtension;

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The style presets in the Styles folder, most recently saved first.</summary>
    public static List<StyleFile> All()
    {
        try
        {
            return Directory.Exists(AppPaths.Styles)
                ? new DirectoryInfo(AppPaths.Styles).EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTime).Select(f => new StyleFile(f.FullName)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The ones the drop overlay offers, in order: those picked in the settings, in the order they were put in,
    /// or failing that the most recent; up to the number set.
    /// </summary>
    public static List<StyleFile> ForLaunch()
    {
        var settings = AppSettings.Current;
        return Picked(All(), settings.SplashStylePresets).Take(Math.Clamp(settings.SplashPresetCount, 0, 8)).ToList();
    }

    /// <summary>The styles named in a saved list, in its order and leaving out any whose file has gone; all of them, most recent first, when it names none that are there.</summary>
    public static List<StyleFile> Picked(List<StyleFile> all, IEnumerable<string> names)
    {
        var picked = names.Select(name => all.FirstOrDefault(s => string.Equals(s.FileName, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<StyleFile>().Distinct().ToList();
        return picked.Count > 0 ? picked : all;
    }
}
