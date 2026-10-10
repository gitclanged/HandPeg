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

    /// <summary>A file of this name beside the program makes the copy self-contained: everything it reads and writes is in its own folder.</summary>
    public const string PortableSentinel = "portable.txt";

    /// <summary>The same, asked for on the command line.</summary>
    public const string PortableArgument = "--portable";

    // The program's own folder, when this copy is self-contained; null otherwise. Found first: the rest follows from it.
    private static readonly string? LocalRoot = FindLocalRoot();

    /// <summary>
    /// True for a self-contained copy (portable.txt beside the program, or --portable). Its tools are in
    /// "deps" and its working files in "temp", both beside the program, and everything else under "data":
    /// settings, presets, projects, caches, logs. Nothing of it is in %AppData%, %LocalAppData% or %TEMP%,
    /// and it neither looks for updates nor reads the registry for the Windows theme.
    /// </summary>
    public static bool IsSelfContained => LocalRoot is not null;

    private static string? FindLocalRoot()
    {
        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            return File.Exists(Path.Combine(folder, PortableSentinel))
                   || Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals(PortableArgument, StringComparison.OrdinalIgnoreCase))
                ? folder
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

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

    /// <summary>Voiceovers recorded onto the timeline: kept, because the projects that use them are.</summary>
    public static string Voiceovers { get; } = Path.Combine(UserRoot, "Voiceovers");

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
    public static string Deps { get; } = Path.Combine(LocalRoot ?? DataRoot, "deps");

    public static string Cache { get; } = Path.Combine(DataRoot, "cache");

    /// <summary>Videos yt-dlp downloaded, kept so the same address loads again without downloading.</summary>
    public static string Downloads { get; } = Path.Combine(Cache, "downloads");

    public static string Logs { get; } = Path.Combine(DataRoot, "logs");

    /// <summary>Working files of the running sessions: removed when each closes, and at the next start if it could not.</summary>
    public static string Temp { get; } = LocalRoot is not null ? Path.Combine(LocalRoot, "temp")
        : SingleRoot is null ? Path.Combine(Path.GetTempPath(), AppName)
        : Path.Combine(SingleRoot, "temp");

    private static string? FindSingleRoot()
    {
        var chosen = Environment.GetEnvironmentVariable(DataFolderVariable);
        if (!string.IsNullOrWhiteSpace(chosen))
            return Path.GetFullPath(chosen.Trim().Trim('"'));

        // Self-contained: the installer's locator is not even asked, since asking is itself a look outside this folder.
        if (LocalRoot is not null)
        {
            IsPortable = true;
            return Path.Combine(LocalRoot, "data");
        }

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
