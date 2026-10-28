using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Brings over what earlier builds kept next to the program (appsettings.json, presets.json, the projects
/// folder and the "bin" folder of tools) and in %TEMP%\Handpeg, once per old location. Settings, presets and
/// projects are copied, so the originals stay as they were; the tools and downloaded videos, which are large
/// and can be fetched again, are moved. Nothing already in a new location is replaced. A portable copy's first
/// start brings over the installed places' settings (<see cref="CopyInstalledIntoPortable"/>).
/// </summary>
public static class DataMigration
{
    public static void Run()
    {
        try
        {
            CopyInstalledIntoPortable();
            MigrateProgramFolder(AppContext.BaseDirectory);
            MigrateTempFolder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Write("Data migration stopped", ex);
        }
    }

    /// <summary>
    /// Velopack's portable build kept its data in the installed places until Pass 4.1C.2 (AppPaths asked Velopack before
    /// Velopack could answer): its first start with its own HandPeg-Data folder, still without settings there, copies
    /// what the installed places hold, so nobody's settings, presets, projects, styles, effect presets or masks seem to
    /// vanish. Copied, never moved: the originals stay for an installed copy. Voiceover recordings (large, and found by
    /// the projects where they are), tools, caches and logs stay where they are; tools are downloaded again if wanted.
    /// </summary>
    private static void CopyInstalledIntoPortable()
    {
        if (!AppPaths.IsPortable || AppPaths.IsSelfContained || File.Exists(AppPaths.SettingsFile)
            || !File.Exists(Path.Combine(AppPaths.InstalledSettings, "appsettings.json")))
            return;

        var copied = CopyFolderIfAbsent(AppPaths.InstalledSettings, AppPaths.Settings);
        foreach (var (from, to) in new[] { ("Projects", AppPaths.Projects), ("Styles", AppPaths.Styles), ("Effect Presets", AppPaths.EffectPresets), ("Masks", AppPaths.Masks) })
            copied += CopyFolderIfAbsent(Path.Combine(AppPaths.InstalledUserRoot, from), to);
        AppLog.Write($"Portable copy: {copied} file(s) of the installed settings and user files brought into {AppPaths.DataRoot}.");
    }

    // Every file of a folder and its subfolders that the target lacks; the number copied. A file that cannot be read
    // (in use by an installed copy that is running) is left out, and the rest still copied.
    private static int CopyFolderIfAbsent(string source, string target)
    {
        if (!Directory.Exists(source))
            return 0;

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                copied += CopyIfAbsent(file, Path.Combine(target, Path.GetRelativePath(source, file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Could not copy {file}", ex);
            }
        }

        return copied;
    }

    private static void MigrateProgramFolder(string folder)
    {
        var settings = Path.Combine(folder, "appsettings.json");
        var presets = Path.Combine(folder, "presets.json");
        var projects = Path.Combine(folder, "projects");
        var tools = Path.Combine(folder, "bin");

        if (!File.Exists(settings) && !File.Exists(presets) && !Directory.Exists(projects) && !Directory.Exists(tools))
            return;

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var done = File.Exists(AppPaths.MigrationRecordFile) ? File.ReadAllLines(AppPaths.MigrationRecordFile) : [];
        if (done.Contains(key, StringComparer.OrdinalIgnoreCase))
            return;

        var copied = CopyIfAbsent(settings, AppPaths.SettingsFile) + CopyIfAbsent(presets, AppPaths.PresetsFile);
        if (Directory.Exists(projects))
        {
            foreach (var file in Directory.EnumerateFiles(projects, "*.txt"))
                copied += CopyIfAbsent(file, Path.Combine(AppPaths.Projects, Path.GetFileName(file)));
        }

        var moved = 0;
        if (Directory.Exists(tools))
        {
            foreach (var file in Directory.EnumerateFiles(tools))
            {
                if (DependencyUpdater.FolderForLegacyFile(Path.GetFileName(file)) is { } target)
                    moved += MoveIfAbsent(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        Directory.CreateDirectory(AppPaths.Settings);
        File.AppendAllLines(AppPaths.MigrationRecordFile, [key]);
        if (copied + moved > 0)
            AppLog.Write($"Migrated from {key}: {copied} file(s) copied, {moved} tool file(s) moved.");
    }

    /// <summary>
    /// The downloads cache used to be in the temp folder, which was spelled "Handpeg". Windows treats that
    /// as the same folder as "HandPeg", so the name on disk is corrected too, when nothing in it is in use.
    /// </summary>
    private static void MigrateTempFolder()
    {
        var temp = Path.Combine(Path.GetTempPath(), AppPaths.AppName);
        if (!Directory.Exists(temp))
            return;

        var downloads = Path.Combine(temp, "downloads");
        if (Directory.Exists(downloads) && !PathsEqual(downloads, AppPaths.Downloads))
        {
            foreach (var folder in Directory.EnumerateDirectories(downloads))
            {
                var target = Path.Combine(AppPaths.Downloads, Path.GetFileName(folder));
                if (Directory.Exists(target))
                    continue;

                try
                {
                    Directory.CreateDirectory(AppPaths.Downloads);
                    MoveFolder(folder, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write($"Could not move the download {folder}", ex);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(downloads).Any())
                Directory.Delete(downloads);
        }

        var nameOnDisk = new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(AppPaths.AppName).FirstOrDefault()?.Name;
        if (nameOnDisk is not null && nameOnDisk != AppPaths.AppName)
        {
            try
            {
                // By way of a third name: a rename that only changes the case is not one Windows always honours.
                var parked = temp + "_" + Guid.NewGuid().ToString("N");
                Directory.Move(temp, parked);
                Directory.Move(parked, temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another copy of the program is using it. The old spelling still works; try again another time.
            }
        }
    }

    private static int CopyIfAbsent(string source, string target)
    {
        if (!File.Exists(source) || File.Exists(target))
            return 0;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return 1;
    }

    private static int MoveIfAbsent(string source, string target)
    {
        if (File.Exists(target))
            return 0;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use by another copy of the program. It can be downloaded again from the settings.
            AppLog.Write($"Could not move {source}", ex);
            return 0;
        }
    }

    /// <summary>A move that also works from one drive to another, where a folder cannot simply be renamed.</summary>
    private static void MoveFolder(string source, string target)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, target);
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Move(file, Path.Combine(target, Path.GetFileName(file)));
        Directory.Delete(source, recursive: true);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
