using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace HandPegApp.Services;

// ----- Explorer project previews (AppSettings.ShowExplorerThumbnails) -----
// The thumbnail handler (src\ShellThumbnails: a NativeAOT COM DLL published into shell\) shows the thumbnail baked into a .hproj/.hpb.
// It needs no system .NET, so it is registered wherever HandPeg is installed and the setting is on. Per-user under HKCU\Software\Classes
// (no admin): CLSID\{HandlerClsid}\InprocServer32 (Apartment) plus each extension's ShellEx\{IThumbnailProvider}. Explorer's surrogate
// never unloads a NativeAOT DLL, so registration points at a content-hashed copy under the data folder (shell\<hash>\), never the
// installed folder updates replace; stale copies are deleted once unlocked. Only changed values are written, and Explorer is notified
// only on change. Skipped for self-contained/portable copies, when HANDPEG_DATA_DIR is set (tests), and under emulation.
//
// Double-click association: .hproj and .hpb only (never media extensions) map to ProgIDs HandPeg.Project / HandPeg.Bundle (DefaultIcon
// HandPeg.exe, shell\open\command) and OpenWithProgids. Written only by a Velopack install (current\HandPeg.exe is update-stable),
// regardless of the preview setting; extensions claimed by another program keep their default (HandPeg appears in Open With only).
// The uninstaller removes both.

/// <summary>Per-user registration and removal of the Explorer thumbnail handler and open command for .hproj and .hpb.</summary>
public static partial class ExplorerThumbnails
{
    /// <summary>The handler's class: HandPeg.ShellThumbnails' ProjectThumbnailProvider (the same GUID).</summary>
    public const string HandlerClsid = "{de4a57e8-2ca1-4db5-a7e2-af1299207a9e}";

    /// <summary>IThumbnailProvider IID: the ShellEx subkey naming an extension's thumbnail handler.</summary>
    public const string ThumbnailProviderKey = "{e357fccd-a995-4576-b01f-234630154e96}";

    /// <summary>What the handler's class is called in the registry.</summary>
    public const string HandlerName = "HandPeg Project Previews";

    /// <summary>Handler folder name beside HandPeg.exe (copied by HandPegApp.csproj) and under the data folder.</summary>
    public const string ShellFolder = "shell";

    /// <summary>The handler, a native DLL (its own COM server): what InprocServer32 names.</summary>
    public const string HandlerFile = "HandPeg.ShellThumbnails.dll";

    // The handler's files: the one DLL (NativeAOT carries its runtime inside it).
    private static readonly string[] HandlerFiles = [HandlerFile];

    private static readonly string[] Extensions = [ProjectStore.Extension, ProjectBundle.Extension];

    /// <summary>The ProgIDs a double-click opens .hproj and .hpb files with.</summary>
    public const string ProjectProgId = "HandPeg.Project", BundleProgId = "HandPeg.Bundle";

    // Each extension, its ProgID, and what Explorer calls that kind of file.
    private static readonly (string Extension, string ProgId, string Name)[] Kinds =
        [(ProjectStore.Extension, ProjectProgId, "HandPeg Project"), (ProjectBundle.Extension, BundleProgId, "HandPeg Project Bundle")];

    private const string ClassesPath = @"Software\Classes";

    // SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST): Explorer flushes cached handler associations.
    private const int AssociationsChanged = 0x08000000;
    private const uint IdListItems = 0;

    // One registration at a time: a start's and a Settings save's may meet.
    private static readonly object Gate = new();

    /// <summary>
    /// Applies AppSettings.ShowExplorerThumbnails on the Idle lane, off the UI thread. Never throws or blocks; no-op for self-contained or
    /// portable copies or with HANDPEG_DATA_DIR set.
    /// </summary>
    public static void ApplyLater()
    {
        if (!Applies)
            return;

        // Velopack's bootstrap (beside the window's start) says whether this copy is an installed one.
        _ = WorkScheduler.Run(new WorkSpec("Explorer project previews", WorkLane.Idle) { Visible = false, Cancellable = false }, async _ =>
        {
            await Program.VelopackStarted;
            await Task.Run(Apply);
        });
    }

    /// <summary>
    /// Uninstall hook (Velopack): removes the handler and open command from the registry only (no settings or WPF access). Entries owned
    /// by other programs are kept. Never throws.
    /// </summary>
    public static void Unregister()
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true);
            if (classes is not null && (Unregister(classes) | Dissociate(classes)))
                NotifyExplorer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or ObjectDisposedException)
        {
            // Ignore: with the folder gone, Explorer finds no handler and falls back to file icons.
        }
    }

    /// <summary>True when this copy may register: installed (or an installable build) with default data locations.</summary>
    private static bool Applies =>
        !AppPaths.IsSelfContained && !AppPaths.IsPortable && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataFolderVariable));

    private static void Apply()
    {
        lock (Gate)
        {
            try
            {
                if (AppSettings.Current.ShowExplorerThumbnails)
                    Install();
                else
                    TakeOut();

                if (InstalledExe() is { } exe)
                {
                    using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath);
                    if (Associate(classes, exe))
                    {
                        NotifyExplorer();
                        AppLog.Write($"Explorer: .hproj and .hpb files open with {exe}.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException
                                           or NotSupportedException or ObjectDisposedException or CryptographicException)
            {
                AppLog.Write("Explorer project previews: the thumbnail handler could not be set up", ex);
            }
        }
    }

    private static void Install()
    {
        // Explorer's surrogate runs native; an emulated copy (x64 on ARM64) would register a handler it cannot load.
        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
        {
            AppLog.Write($"Explorer project previews: not registered, as this HandPeg runs emulated ({RuntimeInformation.ProcessArchitecture} on {RuntimeInformation.OSArchitecture}).");
            return;
        }

        if (CopyHandler() is not { } folder)
            return;

        using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath);
        if (Register(classes, folder))
        {
            NotifyExplorer();
            AppLog.Write($"Explorer project previews: the thumbnail handler is registered for .hproj and .hpb ({folder}).");
        }

        DeleteCopies(except: folder);
    }

    private static void TakeOut()
    {
        using (var classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true))
        {
            if (classes is not null && Unregister(classes))
            {
                NotifyExplorer();
                AppLog.Write("Explorer project previews: the thumbnail handler is taken out.");
            }
        }

        DeleteCopies(except: null);
    }

    /// <summary>
    /// Copies shell\ into a content-hashed folder under the data folder (reusing an existing copy); null, logged, when absent (Debug, or a
    /// Release built without the C++ tools NativeAOT needs).
    /// </summary>
    private static string? CopyHandler()
    {
        var from = Path.Combine(AppContext.BaseDirectory, ShellFolder);
        var files = HandlerFiles.Select(name => new FileInfo(Path.Combine(from, name))).ToArray();
        if (!Array.TrueForAll(files, f => f.Exists))
        {
            AppLog.Write($"Explorer project previews: no thumbnail handler beside the program ({from}), so none is registered.");
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
            hash.AppendData(File.ReadAllBytes(file.FullName));
        var stamp = Convert.ToHexString(hash.GetHashAndReset(), 0, 8).ToLowerInvariant();

        var to = Path.Combine(AppPaths.DataRoot, ShellFolder, stamp);
        Directory.CreateDirectory(to);
        foreach (var file in files)
        {
            var target = Path.Combine(to, file.Name);
            if (File.Exists(target) && new FileInfo(target).Length == file.Length)
                continue;

            // Through a file beside it: Explorer never finds half a handler.
            var partial = target + ".part";
            file.CopyTo(partial, overwrite: true);
            File.Move(partial, target, overwrite: true);
        }

        return to;
    }

    /// <summary>Best-effort deletion of handler copies except <paramref name="except"/> (locked copies are retried later).</summary>
    private static void DeleteCopies(string? except)
    {
        var root = Path.Combine(AppPaths.DataRoot, ShellFolder);
        if (!Directory.Exists(root))
            return;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (except is not null && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still loaded by Explorer's surrogate: deleted at a later start.
            }
        }
    }

    /// <summary>
    /// Writes handler keys under <paramref name="classesRoot"/> (HKCU\Software\Classes or a test key) for the DLL in
    /// <paramref name="handlerFolder"/>, only where values differ (older registrations are overwritten). Returns true if anything changed.
    /// </summary>
    internal static bool Register(RegistryKey classesRoot, string handlerFolder)
    {
        var clsid = @"CLSID\" + HandlerClsid;
        var changed = SetString(classesRoot, clsid, "", HandlerName);
        changed |= SetString(classesRoot, clsid + @"\InprocServer32", "", Path.Combine(handlerFolder, HandlerFile));
        changed |= SetString(classesRoot, clsid + @"\InprocServer32", "ThreadingModel", "Apartment");
        foreach (var extension in Extensions)
            changed |= SetString(classesRoot, $@"{extension}\ShellEx\{ThumbnailProviderKey}", "", HandlerClsid);
        return changed;
    }

    /// <summary>
    /// Removes the handler class and extension entries naming it under <paramref name="classesRoot"/> (plus keys left empty); other
    /// classes are kept. Returns true if anything was removed.
    /// </summary>
    internal static bool Unregister(RegistryKey classesRoot)
    {
        var changed = false;
        foreach (var extension in Extensions)
        {
            var shellEx = extension + @"\ShellEx";
            var handler = shellEx + @"\" + ThumbnailProviderKey;
            using (var key = classesRoot.OpenSubKey(handler))
            {
                if (key?.GetValue("") is not string named || !string.Equals(named, HandlerClsid, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            classesRoot.DeleteSubKeyTree(handler, throwOnMissingSubKey: false);
            DeleteIfEmpty(classesRoot, shellEx);
            DeleteIfEmpty(classesRoot, extension);
            changed = true;
        }

        var clsid = @"CLSID\" + HandlerClsid;
        using (var key = classesRoot.OpenSubKey(clsid))
        {
            if (key is null)
                return changed;
        }

        classesRoot.DeleteSubKeyTree(clsid, throwOnMissingSubKey: false);
        return true;
    }

    /// <summary>HandPeg.exe path for a Velopack install (not portable); null otherwise.</summary>
    private static string? InstalledExe()
    {
        if (!Velopack.Locators.VelopackLocator.IsCurrentSet || Velopack.Locators.VelopackLocator.Current is not { } locator
            || locator.IsPortable || locator.CurrentlyInstalledVersion is null || !File.Exists(locator.UpdateExePath))
            return null;
        return Environment.ProcessPath is { } exe && File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Writes the open command under <paramref name="classesRoot"/>: ProgIDs targeting <paramref name="exe"/>, OpenWithProgids, and the
    /// extension default when unset or already HandPeg's. Only differing values. Returns true if anything changed.
    /// </summary>
    internal static bool Associate(RegistryKey classesRoot, string exe)
    {
        var changed = false;
        foreach (var (extension, progId, name) in Kinds)
        {
            changed |= SetString(classesRoot, progId, "", name);
            changed |= SetString(classesRoot, progId + @"\DefaultIcon", "", $"\"{exe}\",0");
            changed |= SetString(classesRoot, progId + @"\shell\open\command", "", $"\"{exe}\" \"%1\"");
            changed |= SetString(classesRoot, extension + @"\OpenWithProgids", progId, "");
            using (var key = classesRoot.OpenSubKey(extension))
            {
                if (key?.GetValue("") is string claimed && claimed.Length > 0 && !string.Equals(claimed, progId, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            changed |= SetString(classesRoot, extension, "", progId);
        }

        return changed;
    }

    /// <summary>
    /// Removes HandPeg ProgIDs, their OpenWithProgids entries and extension defaults naming them (others kept; emptied keys deleted).
    /// Returns true if anything was removed.
    /// </summary>
    internal static bool Dissociate(RegistryKey classesRoot)
    {
        var changed = false;
        foreach (var (extension, progId, _) in Kinds)
        {
            using (var key = classesRoot.OpenSubKey(progId))
                changed |= key is not null;
            classesRoot.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);

            using (var openWith = classesRoot.OpenSubKey(extension + @"\OpenWithProgids", writable: true))
            {
                if (openWith?.GetValue(progId) is not null)
                {
                    openWith.DeleteValue(progId, throwOnMissingValue: false);
                    changed = true;
                }
            }

            DeleteIfEmpty(classesRoot, extension + @"\OpenWithProgids");
            using (var key = classesRoot.OpenSubKey(extension, writable: true))
            {
                if (key?.GetValue("") is string named && string.Equals(named, progId, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue("", throwOnMissingValue: false);
                    changed = true;
                }
            }

            DeleteIfEmpty(classesRoot, extension);
        }

        return changed;
    }

    // A string value written only when it is not that already (the key made when it is missing). Whether it was written.
    private static bool SetString(RegistryKey root, string path, string name, string value)
    {
        using (var existing = root.OpenSubKey(path))
        {
            if (existing?.GetValue(name) is string current && string.Equals(current, value, StringComparison.Ordinal))
                return false;
        }

        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    // A key HandPeg made for its handler alone (an extension's, its ShellEx), deleted once it holds nothing more.
    private static void DeleteIfEmpty(RegistryKey root, string path)
    {
        using (var key = root.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
                return;
        }

        root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private static void NotifyExplorer() => SHChangeNotify(AssociationsChanged, IdListItems, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
