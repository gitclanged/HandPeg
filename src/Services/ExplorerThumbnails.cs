using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace HandPegApp.Services;

// ----- Project previews in Windows Explorer (Pass 5.2D, AppSettings.ShowExplorerThumbnails) -----
// HandPeg's thumbnail handler (src\ShellThumbnails: a COM class library, built beside HandPeg into shell\) shows the thumbnail a save
// baked into a .hproj or .hpb as the file's preview in Explorer. It is registered for this user alone, under HKCU\Software\Classes
// (no administrator): its class (CLSID\{HandlerClsid}\InprocServer32 naming its .comhost.dll, Apartment) and each extension's
// ShellEx\{IThumbnailProvider} naming that class. Explorer's thumbnail surrogate keeps the handler loaded, so it is registered from a
// copy of its own under the data folder (shell\<its content's hash>\), never from the installed folder an update replaces; older
// copies are deleted once nothing holds them. Only what differs is written, and Explorer is told only when something changed.
// Nothing of it in a self-contained or portable copy (which leaves nothing on the PC), nor while HANDPEG_DATA_DIR is set (tests).
//
// The handler needs a .NET of the PC's own (Pass 5.3-Stage1): its comhost.dll finds .NET as .NET's host does for a library, from
// DOTNET_ROOT_<ARCH> / DOTNET_ROOT, else the location .NET's installer registered, else Program Files\dotnet, and is never told of the
// .NET HandPeg.exe may have downloaded for itself alone (an installed copy's dotnet, %LOCALAPPDATA%\HandPeg\dotnet, a zip's
// portable\dotnet). So it is registered only when that lookup finds a released .NET Desktop Runtime (never a preview) of HandPeg's own
// major version (HandlerRuntime); otherwise the log says so once and a registration left from before is taken out. Each start looks
// again, so installing .NET for the PC later turns the previews on.
//
// Double-click opens them (Pass 5.2-Release, the user's directive): .hproj and .hpb, and only these (never a media extension), name a
// ProgID of HandPeg's (HandPeg.Project, HandPeg.Bundle) whose DefaultIcon is HandPeg.exe's own and whose shell\open\command starts it
// with the file, and list it in their OpenWithProgids. Written by an installed copy alone (Velopack's: its current\HandPeg.exe keeps its
// path through updates; never a build's folder), whatever the previews' setting; an extension another program claims keeps it (HandPeg
// is then only in its Open With list). The uninstaller takes both out.

/// <summary>Registers (or takes out) HandPeg's Explorer thumbnail handler and open command for .hproj and .hpb files, for this user.</summary>
public static partial class ExplorerThumbnails
{
    /// <summary>The handler's class: HandPeg.ShellThumbnails' ProjectThumbnailProvider (the same GUID).</summary>
    public const string HandlerClsid = "{de4a57e8-2ca1-4db5-a7e2-af1299207a9e}";

    /// <summary>IThumbnailProvider's interface: the key under an extension's ShellEx that names its thumbnail handler.</summary>
    public const string ThumbnailProviderKey = "{e357fccd-a995-4576-b01f-234630154e96}";

    /// <summary>What the handler's class is called in the registry.</summary>
    public const string HandlerName = "HandPeg Project Previews";

    /// <summary>The handler's folder beside HandPeg.exe (HandPegApp.csproj copies it there), and the folder its copies go in under the data folder.</summary>
    public const string ShellFolder = "shell";

    /// <summary>The handler's COM server: what InprocServer32 names.</summary>
    public const string ComHostFile = "HandPeg.ShellThumbnails.comhost.dll";

    // The handler's files: its assembly, the COM server, and what .NET is started with.
    private static readonly string[] HandlerFiles =
        ["HandPeg.ShellThumbnails.dll", ComHostFile, "HandPeg.ShellThumbnails.runtimeconfig.json", "HandPeg.ShellThumbnails.deps.json"];

    private static readonly string[] Extensions = [ProjectStore.Extension, ProjectBundle.Extension];

    /// <summary>The ProgIDs a double-click opens .hproj and .hpb files with.</summary>
    public const string ProjectProgId = "HandPeg.Project", BundleProgId = "HandPeg.Bundle";

    // Each extension, its ProgID, and what Explorer calls that kind of file.
    private static readonly (string Extension, string ProgId, string Name)[] Kinds =
        [(ProjectStore.Extension, ProjectProgId, "HandPeg Project"), (ProjectBundle.Extension, BundleProgId, "HandPeg Project Bundle")];

    private const string ClassesPath = @"Software\Classes";

    // SHChangeNotify: file associations changed (Explorer drops what it knew of the handlers); the items given as ID lists (none).
    private const int AssociationsChanged = 0x08000000;
    private const uint IdListItems = 0;

    // One registration at a time: a start's and a Settings save's may meet.
    private static readonly object Gate = new();

    // Whether the log has said this run that the handler has no .NET of the PC's to start on (said once, not at every Settings save).
    private static bool _saidNoRuntime;

    /// <summary>
    /// Registers the handler, or takes it out, as AppSettings.ShowExplorerThumbnails says when it runs: on the Idle work lane, off the
    /// window's thread. Never throws and never waits; nothing at all for a self-contained or portable copy, or with HANDPEG_DATA_DIR set.
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
    /// Takes the handler and the open command out (HandPeg uninstalled: its Velopack hook): the registry alone, nothing of the settings
    /// or of WPF read. Only what is HandPeg's goes (an extension's handler that names another class stays, and so does another program's
    /// claim on an extension). Never throws.
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
            // Left as it is: Explorer then finds no handler where it points (the folder went with HandPeg) and shows the files' icons.
        }
    }

    /// <summary>Whether this copy registers anything: an installed one (or a build of one), with its data in the usual places.</summary>
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
        // Explorer's surrogate runs at the machine's own processor: a copy running emulated (x64 on an ARM64 PC) would point it at a
        // handler it cannot load.
        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
        {
            AppLog.Write($"Explorer project previews: not registered, as this HandPeg runs emulated ({RuntimeInformation.ProcessArchitecture} on {RuntimeInformation.OSArchitecture}).");
            return;
        }

        // The handler starts on the PC's own .NET, never on one HandPeg.exe downloaded for itself: with none, no handler (and none left).
        if (FindHandlerRuntime() is null)
        {
            if (!_saidNoRuntime)
            {
                _saidNoRuntime = true;
                var major = Environment.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AppLog.Write($"Explorer project previews: not registered, as this PC has no .NET {major} Desktop Runtime of its own for the thumbnail handler "
                             + "(HandPeg's own .NET is not one Explorer can start it on); installing it for the PC turns them on at the next start.");
            }

            TakeOut();
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
    /// The .NET root Explorer's thumbnail surrogate would start the handler on, found as its comhost.dll finds one (.NET's host for a
    /// library, which looks at no folder of the app's own): DOTNET_ROOT_&lt;ARCH&gt;, else DOTNET_ROOT, else the location .NET's installer
    /// registered, else Program Files\dotnet (Program Files\dotnet\x64 for an x64 process on an ARM64 PC); the first of them that is set is
    /// the only one looked at, as the host does. Null unless it holds a hostfxr.dll and Microsoft.NETCore.App and
    /// Microsoft.WindowsDesktop.App of a released version of <paramref name="major"/> (the handler's runtimeconfig.json rolls forward within
    /// that major only, and never to a preview).
    /// Pure: the environment, the registered location and Program Files are given (the portable-bootstrap golden test's layouts).
    /// </summary>
    internal static string? HandlerRuntime(Func<string, string?> environment, string? registered, string programFiles, string arch, bool x64OnArm64, int major)
    {
        var root = environment($"DOTNET_ROOT_{arch.ToUpperInvariant()}") is { Length: > 0 } forArch ? forArch
            : environment("DOTNET_ROOT") is { Length: > 0 } any ? any
            : registered is { Length: > 0 } ? registered
            : x64OnArm64 ? Path.Combine(programFiles, "dotnet", "x64") : Path.Combine(programFiles, "dotnet");

        try
        {
            var fxr = Path.Combine(root, "host", "fxr");
            if (!Directory.Exists(fxr) || !Directory.EnumerateDirectories(fxr).Any(dir => File.Exists(Path.Combine(dir, "hostfxr.dll"))))
                return null;
            return HasMajor(Path.Combine(root, "shared", "Microsoft.NETCore.App"), major) && HasMajor(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App"), major)
                ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        // A framework folder holding a released version of that major ("10.0.14") with something in it. A preview's or release candidate's
        // ("10.0.0-rc.2.25502.107") never counts: the handler's runtimeconfig.json asks for the release, which the host never rolls forward to
        // a pre-release of, so its comhost could not start on a .NET holding only such frameworks.
        static bool HasMajor(string framework, int major) =>
            Directory.Exists(framework) && Directory.EnumerateDirectories(framework).Any(dir =>
            {
                var name = Path.GetFileName(dir);
                var dot = name.IndexOf('.');
                return dot > 0 && !name.Contains('-')
                       && int.TryParse(name.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var found)
                       && found == major && Directory.EnumerateFileSystemEntries(dir).Any();
            });
    }

    /// <summary>
    /// <see cref="HandlerRuntime"/> on this PC: Explorer's environment, the registered location (HKLM, in the 32-bit view the host reads),
    /// Program Files, this process's processor, and the major version HandPeg runs on (the handler is built for the same .NET as HandPeg).
    /// </summary>
    private static string? FindHandlerRuntime()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string? registered;
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        using (var key = machine.OpenSubKey($@"SOFTWARE\dotnet\Setup\InstalledVersions\{arch}"))
            registered = key?.GetValue("InstallLocation") as string;

        var x64OnArm64 = RuntimeInformation.ProcessArchitecture == Architecture.X64 && RuntimeInformation.OSArchitecture == Architecture.Arm64;
        return HandlerRuntime(ExplorerEnvironment, registered, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), arch, x64OnArm64, Environment.Version.Major);
    }

    // A variable as Explorer's processes have it: the user's own, else the machine's (as Windows builds a signed-in user's environment),
    // not this process's, which a console may have changed.
    private static string? ExplorerEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is { Length: > 0 } user
            ? user : Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    /// <summary>
    /// The handler's files beside the program (shell\) copied into a folder of their own under the data folder, named by their content
    /// (a copy already there is used as it is); null when the program has none beside it (a build without them), said in the log.
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

    /// <summary>The handler's copies under the data folder but <paramref name="except"/>, deleted as far as they can be (one Explorer still holds goes another time).</summary>
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
    /// Writes the handler's keys under <paramref name="classesRoot"/> (HKCU\Software\Classes, or a test's own key), naming the COM server in
    /// <paramref name="handlerFolder"/>: only the values that differ. Whether anything was written.
    /// </summary>
    internal static bool Register(RegistryKey classesRoot, string handlerFolder)
    {
        var clsid = @"CLSID\" + HandlerClsid;
        var changed = SetString(classesRoot, clsid, "", HandlerName);
        changed |= SetString(classesRoot, clsid + @"\InprocServer32", "", Path.Combine(handlerFolder, ComHostFile));
        changed |= SetString(classesRoot, clsid + @"\InprocServer32", "ThreadingModel", "Apartment");
        foreach (var extension in Extensions)
            changed |= SetString(classesRoot, $@"{extension}\ShellEx\{ThumbnailProviderKey}", "", HandlerClsid);
        return changed;
    }

    /// <summary>
    /// Takes the handler's keys out from under <paramref name="classesRoot"/>: its class, and each extension's thumbnail handler that names
    /// it (with the ShellEx and extension keys left empty by that; another class named there stays). Whether anything was taken out.
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

    /// <summary>This copy's HandPeg.exe when it is an installed one (Velopack's, not its portable copy); null for a build or anything else.</summary>
    private static string? InstalledExe()
    {
        if (!Velopack.Locators.VelopackLocator.IsCurrentSet || Velopack.Locators.VelopackLocator.Current is not { } locator
            || locator.IsPortable || locator.CurrentlyInstalledVersion is null || !File.Exists(locator.UpdateExePath))
            return null;
        return Environment.ProcessPath is { } exe && File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Writes the open command under <paramref name="classesRoot"/> (HKCU\Software\Classes, or a test's own key): HandPeg's ProgIDs naming
    /// <paramref name="exe"/>, each extension's OpenWithProgids, and the extension's default where it names nothing or HandPeg's own. Only
    /// the values that differ. Whether anything was written.
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
    /// Takes the open command out from under <paramref name="classesRoot"/>: HandPeg's ProgIDs, their entries in the OpenWithProgids, and an
    /// extension's default that names them (another program's stays; keys left empty by it go). Whether anything was taken out.
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
