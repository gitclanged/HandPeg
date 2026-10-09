using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Every folder and file HandPeg writes to, in one place. Nothing is kept next to the program: the updater
/// (Velopack) replaces that folder as a whole with each new version.
///
/// Installed: projects and layouts under Documents\HandPeg, the settings under %AppData%\HandPeg, and the
/// downloaded tools, caches and logs under %LocalAppData%\HandPeg-Data. That last one is deliberately not
/// %LocalAppData%\HandPeg, which is the folder the installer owns and removes on uninstall or reinstall.
///
/// Portable: all of it in a HandPegData folder beside Update.exe, so the whole thing can be carried around.
///
/// Folders are not created by asking for their path: whoever writes into one creates it first.
/// </summary>
public static class AppPaths
{
    public const string AppName = "HandPeg";

    /// <summary>When set, everything is kept in this folder, laid out as for a portable copy. For testing.</summary>
    public const string DataFolderVariable = "HANDPEG_DATA_DIR";

    private const string PortableDataFolder = "HandPegData";

    // One folder holding everything (portable, or the override), or null when installed.
    private static readonly string? SingleRoot = FindSingleRoot();

    /// <summary>True when this is Velopack's portable build, as Velopack itself reports it.</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>What the user makes and may want to find in Explorer: Documents\HandPeg.</summary>
    public static string UserRoot { get; } = SingleRoot ?? Path.Combine(DocumentsFolder(), AppName);

    /// <summary>Saved projects.</summary>
    public static string Projects { get; } = Path.Combine(UserRoot, "Projects");

    /// <summary>Style presets (.hpstyle): a look and a layout in one file, to keep or to share.</summary>
    public static string Styles { get; } = Path.Combine(UserRoot, "Styles");

    /// <summary>Mask pictures: the ones that arrive inside style presets, and the ones made for the HUD layers.</summary>
    public static string Masks { get; } = Path.Combine(UserRoot, "Masks");

    /// <summary>The settings and the encoding presets.</summary>
    public static string Settings { get; } = SingleRoot is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName)
        : Path.Combine(SingleRoot, "Settings");

    public static string SettingsFile { get; } = Path.Combine(Settings, "appsettings.json");
    public static string PresetsFile { get; } = Path.Combine(Settings, "presets.json");

    /// <summary>The legacy locations already brought over; see <see cref="DataMigration"/>.</summary>
    public static string MigrationRecordFile { get; } = Path.Combine(Settings, "migrated.txt");

    /// <summary>What can be downloaded or made again: tools, caches, logs.</summary>
    public static string DataRoot { get; } = SingleRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName + "-Data");

    /// <summary>FFmpeg, yt-dlp, whisper.cpp and the speech models, a folder each.</summary>
    public static string Deps { get; } = Path.Combine(DataRoot, "deps");

    public static string Cache { get; } = Path.Combine(DataRoot, "cache");

    /// <summary>Videos yt-dlp downloaded, kept so the same address loads again without downloading.</summary>
    public static string Downloads { get; } = Path.Combine(Cache, "downloads");

    public static string Logs { get; } = Path.Combine(DataRoot, "logs");

    /// <summary>Working files of the running sessions: removed when each closes, and at the next start if it could not.</summary>
    public static string Temp { get; } = SingleRoot is null
        ? Path.Combine(Path.GetTempPath(), AppName)
        : Path.Combine(SingleRoot, "temp");

    private static string? FindSingleRoot()
    {
        var chosen = Environment.GetEnvironmentVariable(DataFolderVariable);
        if (!string.IsNullOrWhiteSpace(chosen))
            return Path.GetFullPath(chosen.Trim().Trim('"'));

        try
        {
            // Velopack knows whether this copy came from the portable archive, and where its root is:
            // the folder holding Update.exe and "current". The data goes beside those, never inside "current".
            var locator = Velopack.Locators.VelopackLocator.Current;
            if (locator.IsPortable && locator.RootAppDir is { Length: > 0 } root)
            {
                IsPortable = true;
                return Path.Combine(root, PortableDataFolder);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No locator: this copy was not installed by Velopack at all (a build folder). The installed layout applies.
        }

        return null;
    }

    /// <summary>The user's Documents folder, wherever it has been moved to (OneDrive, another drive).</summary>
    private static string DocumentsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return documents.Length > 0 ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
