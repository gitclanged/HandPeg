using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Central registry of every folder and file HandPeg writes. Nothing lives beside the program (Velopack replaces that folder on update).
///
/// Installed: projects and layouts in Documents\HandPeg, settings in %AppData%\HandPeg, tools/caches/logs in
/// %LocalAppData%\HandPeg-Data (not %LocalAppData%\HandPeg, which the installer owns and removes).
///
/// Portable (Velopack portable archive): everything in HandPeg-Data beside HandPeg.exe and Update.exe, never inside "current", so the
/// folder is movable and updates preserve data.
///
/// Path getters never create folders; writers create them.
/// </summary>
public static class AppPaths
{
    public const string AppName = "HandPeg";

    /// <summary>When set, everything is kept in this folder, laid out as for a portable copy. For testing.</summary>
    public const string DataFolderVariable = "HANDPEG_DATA_DIR";

    private const string PortableDataFolder = "HandPeg-Data";

    /// <summary>Marker file beside the program enabling self-contained mode (all I/O in its own folder).</summary>
    public const string PortableSentinel = "portable.txt";

    /// <summary>The same, asked for on the command line.</summary>
    public const string PortableArgument = "--portable";

    /// <summary>
    /// Folder beside the program that enables self-contained mode and holds its data; zero-install portable zips ship it empty.
    /// </summary>
    public const string PortableFolder = "portable";

    // Self-contained root ("portable" folder, else the program folder); null otherwise. Resolved first; other paths derive from it.
    private static readonly string? LocalRoot = FindLocalRoot();

    /// <summary>
    /// True for self-contained mode ("portable" folder, portable.txt, or --portable): tools in "deps", temp files in "temp", all else
    /// in "data", inside the portable folder or beside the program. Never touches %AppData%, %LocalAppData% or %TEMP%, never checks
    /// for updates or reads the registry theme.
    /// </summary>
    public static bool IsSelfContained => LocalRoot is not null;

    private static string? FindLocalRoot()
    {
        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            var portable = Path.Combine(folder, PortableFolder);
            if (Directory.Exists(portable))
                return portable;

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

    /// <summary>True for Velopack's portable build (or a self-contained copy): its data is in its own folder.</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>Installed settings and presets folder; imported by a portable copy's first start.</summary>
    public static string InstalledSettings { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>Installed user content folder (Documents\HandPeg): projects, styles, effect presets, masks.</summary>
    public static string InstalledUserRoot { get; } = Path.Combine(DocumentsFolder(), AppName);

    /// <summary>What the user makes and may want to find in Explorer: Documents\HandPeg.</summary>
    public static string UserRoot { get; } = SingleRoot ?? InstalledUserRoot;

    /// <summary>Saved projects.</summary>
    public static string Projects { get; } = Path.Combine(UserRoot, "Projects");

    /// <summary>Style presets (.hpstyle): a look and a layout in one file, to keep or to share.</summary>
    public static string Styles { get; } = Path.Combine(UserRoot, "Styles");

    /// <summary>Effect presets (.hpfx): a stack of effect cards saved from the Effects pane.</summary>
    public static string EffectPresets { get; } = Path.Combine(UserRoot, "Effect Presets");

    /// <summary>Mask pictures: the ones that arrive inside style presets, and the ones made for the HUD layers.</summary>
    public static string Masks { get; } = Path.Combine(UserRoot, "Masks");

    /// <summary>Voiceovers recorded onto the timeline: kept, because the projects that use them are.</summary>
    public static string Voiceovers { get; } = Path.Combine(UserRoot, "Voiceovers");

    /// <summary>The settings and the encoding presets.</summary>
    public static string Settings { get; } = SingleRoot is null ? InstalledSettings : Path.Combine(SingleRoot, "Settings");

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

    /// <summary>
    /// Crash reports (.hpcrash), one per file, beside the logs under the data folder (follows portable mode and HANDPEG_DATA_DIR).
    /// </summary>
    public static string Crashes { get; } = Path.Combine(DataRoot, "crashes");

    /// <summary>Per-session temp files, removed on close or at the next start.</summary>
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

        // Velopack portable: runs from "current" with Update.exe and the ".portable" marker beside it (Velopack's own test). Checked
        // on disk because AppPaths is used in Main before Velopack's locator exists. Installs have Update.exe but no marker; builds
        // have neither.
        if (PortableRoot() is { } root)
        {
            IsPortable = true;
            return Path.Combine(root, PortableDataFolder);
        }

        return null;
    }

    // The portable archive's root (the folder holding Update.exe, .portable and "current"), or null.
    private static string? PortableRoot()
    {
        try
        {
            var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)));
            return root is not null && File.Exists(Path.Combine(root, ".portable")) && File.Exists(Path.Combine(root, "Update.exe")) ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The user's Documents folder, wherever it has been moved to (OneDrive, another drive).</summary>
    private static string DocumentsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return documents.Length > 0 ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
