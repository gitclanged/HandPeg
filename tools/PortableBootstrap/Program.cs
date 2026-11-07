namespace HandPegBootstrap;

/// <summary>
/// HandPeg.exe of the portable zips (Pass 5.2.1, the user's answer: a locked-down PC refuses a .cmd or a PowerShell script, so no script
/// ships) and of both Setup installers (Pass 5.3-Stage1: never Windows' "You must install .NET" box). It finds a .NET 10 Desktop Runtime
/// for its processor (DotNetRoots: this copy's own first, then the PC's) and runs HandPeg.dll in this process through that runtime's
/// hostfxr, as .NET's own apphost does: HandPeg's start-up is the apphost's. A root whose hostfxr will not start HandPeg (one that looks
/// whole but is damaged) passes to the next one found. With none, it asks (ChoiceWindow): download Microsoft's own into this copy's own
/// root, with no admin (PortableDotNet, in ProgressWindow), or install it for every program with Microsoft's installer, which asks for
/// admin (SystemDotNet); then it runs HandPeg the same way. Never cmd.exe or PowerShell. Started by Velopack for an installer's hook, or as
/// one of HandPeg's own background processes, it never shows anything (Launch). HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET=1 looks only in the
/// copy's own places (a test of the download on a PC that has .NET).
/// </summary>
internal static class Program
{
    private const string Caption = "HandPeg";
    private const string DownloadPage = "https://dotnet.microsoft.com/download/dotnet/10.0";

    [STAThread]
    private static int Main(string[] arguments)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HandPeg.exe");
        var app = Path.GetDirectoryName(exe)!;
        var quiet = Launch.IsQuiet(arguments);
        var own = DotNetRoots.OwnRoot(app, arguments, Directory.Exists, File.Exists, LocalAppData());
        var dll = Path.Combine(app, "HandPeg.dll");
        if (!File.Exists(dll))
        {
            return quiet ? Launch.QuietExitCode(arguments) : Fail($"HandPeg.dll is not beside {Path.GetFileName(exe)} in {app}. " + (own.Kind switch
            {
                CopyKind.Portable => "Unzip the whole HandPeg folder and start HandPeg.exe from it.",
                CopyKind.Installed => "Install HandPeg again to put it back.",
                _ => "Copy the whole HandPeg folder and start HandPeg.exe from it.",
            }));
        }

        // Every .NET 10 found, in order, looked at one at a time: HandPeg runs on the first whose hostfxr starts it. One that hostfxr refuses
        // (a root that looks whole but cannot start: damaged after it was put there) passes to the next with nothing shown, in a quiet start
        // too (HandPeg's background processes start through this program as well, and need the root HandPeg itself runs on). With none, or
        // none that starts, a quiet start ends at once and any other asks, naming the one that could not start. A .NET the dialog has just
        // put in place that hostfxr refuses is said, and the start ends rather than offering the same again.
        var arch = DotNetRoots.Arch;
        PortableDotNet.FinishPending(PortableDotNet.DownloadTarget(own));
        var refused = new List<Refused>();
        using var roots = DotNetRoots.AllUsable(Candidates(app, arch, own, SystemLookup)).GetEnumerator();
        var root = roots.MoveNext() ? roots.Current : null;
        var fresh = false;
        while (true)
        {
            if (root is null)
            {
                if (Launch.Decide(false, arguments) == StartAction.QuietExit)
                    return Launch.QuietExitCode(arguments);
                root = Ask(app, arch, own, refused);
                if (root is null)
                    return 1;
                fresh = true;
            }

            var exit = HostFxr.Run(root, exe, dll, arguments, out var started);
            if (started)
                return exit;
            if (fresh)
            {
                return Fail($"HandPeg could not start on {root.Where} ({root.Folder}).\n\n{(HostFxr.LastErrors.Length > 0 ? HostFxr.LastErrors : $"hostfxr said 0x{exit:X8}.")}"
                    + $"\n\nInstall the .NET {DotNetRoots.Major} Desktop Runtime ({arch}) from {DownloadPage}, then start HandPeg again.");
            }

            refused.Add(new Refused(root, DotNetRoots.StampOf(root)));
            root = roots.MoveNext() ? roots.Current : null;
        }
    }

    /// <summary>A root hostfxr refused in this start, and its stamp (DotNetRoots.StampOf): a .NET put in its place since has another.</summary>
    private sealed record Refused(DotNetRoot Root, string Stamp);

    /// <summary>Whether the PC's own .NET is looked at (HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET=1: only this copy's own, to try the download).</summary>
    private static bool SystemLookup => Environment.GetEnvironmentVariable("HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET") != "1";

    private static string LocalAppData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local ? local : Path.GetTempPath();

    private static IEnumerable<(string Folder, string Where)> Candidates(string app, string arch, OwnDotNet own, bool system)
    {
        var x64OnArm64 = arch == "x64" && Native.IsWow64Process2(Native.GetCurrentProcess(), out _, out var machine) && machine == Native.ImageFileMachineArm64;
        return DotNetRoots.Candidates(own, app, system, Environment.GetEnvironmentVariable, system ? Native.RegisteredDotNet(arch) : null,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), arch, x64OnArm64);
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name one folder (full paths, in any letter case, with a trailing separator or not).</summary>
    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first root found that is not one hostfxr refused in this start (the same folder with the same stamp: one another HandPeg has put
    /// in its place since is found); null for none.
    /// </summary>
    private static DotNetRoot? FindAgain(string app, string arch, OwnDotNet own, bool system, List<Refused> refused) =>
        DotNetRoots.AllUsable(Candidates(app, arch, own, system))
            .FirstOrDefault(r => !refused.Exists(x => SameFolder(x.Root.Folder, r.Folder) && x.Stamp == DotNetRoots.StampOf(r)));

    /// <summary>HandPeg.exe started again with this start's arguments (and environment); this start then ends with nothing more shown.</summary>
    private static void Restart()
    {
        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HandPeg.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
            info.ArgumentList.Add(argument);
        try
        {
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Fail($"HandPeg could not start itself again to put the new .NET in place ({ex.Message}). Start HandPeg again.");
        }
    }

    /// <summary>
    /// No .NET 10, or none that starts (<paramref name="refused"/>): the choice dialog until .NET is there or the user cancels; the root to
    /// run on, or null to end. A way that was cancelled or failed (with its message) comes back to the dialog, after a fresh look for .NET:
    /// an installer whose window was closed may have finished since, or another HandPeg may have put its download in place.
    /// </summary>
    private static DotNetRoot? Ask(string app, string arch, OwnDotNet own, List<Refused> refused)
    {
        // The one to name: this copy's own when hostfxr refused it (a download replaces it, even though it looks whole), else the first refused.
        var target = PortableDotNet.DownloadTarget(own);
        var mine = refused.Find(r => SameFolder(r.Root.Folder, target));
        var broken = mine?.Root ?? refused.FirstOrDefault()?.Root;
        for (var first = true; ; first = false)
        {
            if (!first && FindAgain(app, arch, own, SystemLookup, refused) is { } found)
                return found;

            var again = false;
            DotNetRoot? root = null;
            switch (ChoiceWindow.Show(Words(own, broken)))
            {
                case DotNetChoice.OwnCopy:
                    root = DownloadOwn(arch, own, mine?.Stamp, out again);
                    break;
                case DotNetChoice.SystemWide:
                    root = InstallSystemWide(app, arch, own, refused, out again);
                    break;
            }

            if (root is not null || !again)
                return root;
        }
    }

    /// <summary>The dialog's words for this kind of copy, naming <paramref name="broken"/> when a .NET 10 was found that could not start HandPeg.</summary>
    private static ChoiceText Words(OwnDotNet own, DotNetRoot? broken)
    {
        var major = DotNetRoots.Major;
        var target = PortableDotNet.DownloadTarget(own);
        var body = broken is null
            ? ((own.Kind is CopyKind.Portable or CopyKind.SelfContained) ? "This portable copy of HandPeg" : "HandPeg")
              + $" runs on .NET {major}, which this PC does not have (an older .NET, such as .NET 8, is not enough). HandPeg can download it for itself alone, or install it for every program on this PC."
            : SameFolder(broken.Folder, target)
                ? $"The copy of .NET {major} at {broken.Folder} could not start HandPeg (it may be damaged) and will be replaced. HandPeg can download a new one for itself alone, or install .NET {major} for every program on this PC."
                : $"The copy of .NET {major} at {broken.Folder} could not start HandPeg (it may be damaged), so HandPeg will use a new one in its place. It can download one for itself alone, or install .NET {major} for every program on this PC.";
        return new ChoiceText(
            broken is null ? $"HandPeg needs Microsoft's .NET {major} Desktop Runtime" : $"HandPeg could not start on the .NET {major} it found",
            body,
            own.Kind switch
            {
                CopyKind.Portable => "About 70 MB from Microsoft, kept in this folder (portable\\dotnet): nothing is installed and no admin rights are needed.",
                CopyKind.SelfContained => "About 70 MB from Microsoft, kept in this folder (dotnet) with the rest of this copy: nothing is installed and no admin rights are needed.",
                CopyKind.Installed => $"About 70 MB from Microsoft, kept for HandPeg alone in {target} and removed with HandPeg. No admin rights are needed.",
                _ => $"About 70 MB from Microsoft, kept for HandPeg alone in {target}. No admin rights are needed.",
            },
            "About 55 MB: Microsoft's own installer, for every program on this PC. Windows asks for administrator rights first.");
    }

    /// <summary>
    /// .NET 10 downloaded into this copy's own root (<see cref="PortableDotNet.DownloadTarget"/>), in place of the one there whose stamp
    /// is <paramref name="refused"/> (hostfxr refused it in this start); its root, or null (<paramref name="again"/>: back to the dialog).
    /// </summary>
    private static DotNetRoot? DownloadOwn(string arch, OwnDotNet own, string? refused, out bool again)
    {
        again = true;
        var target = PortableDotNet.DownloadTarget(own);
        var body = own.Kind switch
        {
            CopyKind.Portable => "It is put in this folder (portable\\dotnet), checked against Microsoft's SHA-512: nothing is installed and no admin rights are needed.",
            CopyKind.SelfContained => "It is put in this folder (dotnet), checked against Microsoft's SHA-512: nothing is installed and no admin rights are needed.",
            _ => $"It is put in {target} for HandPeg alone, checked against Microsoft's SHA-512: no admin rights are needed.",
        };
        var cancelled = ProgressWindow.Run($"Downloading .NET {DotNetRoots.Major} Desktop Runtime ({arch})", body,
            (progress, cancel) => PortableDotNet.Install(target, arch, progress, cancel, refused), out var failure);
        if (cancelled)
            return null;
        if (failure is PendingSwapException)
        {
            // The refused .NET stays loaded in this process: a new start puts the download in its place before loading anything.
            Restart();
            again = false;
            return null;
        }

        if (failure is not null)
        {
            Fail($"HandPeg needs the .NET {DotNetRoots.Major} Desktop Runtime ({arch}), and it could not be put in {target}:\n\n{failure.Message}\n\n"
                + $"Choose again to try once more, or install the .NET {DotNetRoots.Major} Desktop Runtime ({arch}) from {DownloadPage}");
            return null;
        }

        if (DotNetRoots.Usable(target, own.Where) is { } root)
            return root;

        Fail($"The .NET downloaded into {target} cannot be found. Choose again to download it once more.");
        return null;
    }

    /// <summary>
    /// .NET 10 installed for every program by Microsoft's installer (Windows asks for admin); the root it put it in (never one hostfxr
    /// refused in this start), or null (<paramref name="again"/>: back to the dialog, after a declined prompt, a busy Windows Installer or a
    /// failure).
    /// </summary>
    private static DotNetRoot? InstallSystemWide(string app, string arch, OwnDotNet own, List<Refused> refused, out bool again)
    {
        again = true;
        var code = 0;
        var cancelled = ProgressWindow.Run($"Installing .NET {DotNetRoots.Major} Desktop Runtime ({arch})",
            "Microsoft's own installer is downloaded, checked against Microsoft's SHA-512 and run for every program on this PC. Windows asks for administrator rights first.",
            (progress, cancel) => code = SystemDotNet.Install(arch, ProgressWindow.Handle, progress, cancel), out var failure);
        if (cancelled)
            return null;
        if (failure is not null)
        {
            Fail($"The .NET {DotNetRoots.Major} Desktop Runtime ({arch}) could not be installed:\n\n{failure.Message}\n\nChoose again, or download it for HandPeg alone.");
            return null;
        }

        var outcome = Launch.OutcomeOf(code);
        switch (outcome)
        {
            case InstallerOutcome.Cancelled:
                return null;
            case InstallerOutcome.Busy:
                Tell("Another installation is running on this PC, so Microsoft's .NET installer could not start its own. Wait for it to finish, then choose again.", Native.MbIconWarning);
                return null;
            case InstallerOutcome.Failed:
                Fail($"Microsoft's .NET {DotNetRoots.Major} installer stopped with code {code} (0x{code:X8}); its log is in %TEMP%.\n\nChoose again, or download .NET {DotNetRoots.Major} for HandPeg alone.");
                return null;
        }

        // Installed: found where Windows' own .NET is, whatever HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET says, past a copy of HandPeg's own that
        // hostfxr refused (it is still first in the list).
        again = false;
        if (FindAgain(app, arch, own, system: true, refused) is { } root)
            return root;

        Fail(outcome == InstallerOutcome.RestartSuggested
            ? $"Microsoft's installer has put .NET {DotNetRoots.Major} on this PC and asks for Windows to be restarted. Restart Windows, then start HandPeg again."
            : $"Microsoft's installer finished, but HandPeg cannot find .NET {DotNetRoots.Major} ({arch}) where it put it. Start HandPeg again, or install it from {DownloadPage}");
        return null;
    }

    private static int Fail(string message) => Tell(message, Native.MbIconError);

    private static int Tell(string message, uint icon)
    {
        Native.MessageBox(IntPtr.Zero, message, Caption, Native.MbOk | icon | Native.MbSetForeground);
        return 1;
    }
}
