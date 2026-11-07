namespace HandPegBootstrap;

/// <summary>
/// HandPeg.exe of the portable zips (Pass 5.2.1, the user's answer: a locked-down PC refuses a .cmd or a PowerShell script, so no script
/// ships). It finds a .NET 8 Desktop Runtime for its processor (DotNetRoots: this copy's own first, then the PC's) and runs HandPeg.dll
/// in this process through that runtime's hostfxr, as .NET's own apphost does: HandPeg's start-up is the apphost's. With none, it
/// downloads Microsoft's own into portable\dotnet in a window of its own (PortableDotNet, ProgressWindow), then runs HandPeg the same
/// way: never cmd.exe, PowerShell or an installer. HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET=1 looks only in the copy's own folder (a test of
/// the download on a PC that has .NET).
/// </summary>
internal static class Program
{
    private const string Caption = "HandPeg Portable";

    [STAThread]
    private static int Main(string[] arguments)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HandPeg.exe");
        var app = Path.GetDirectoryName(exe)!;
        var dll = Path.Combine(app, "HandPeg.dll");
        if (!File.Exists(dll))
            return Fail($"HandPeg.dll is not beside {Path.GetFileName(exe)} in {app}. Unzip the whole HandPeg folder and start HandPeg.exe from it.");

        var arch = DotNetRoots.Arch;
        var root = DotNetRoots.Find(Candidates(app, arch));
        if (root is null)
        {
            var cancelled = ProgressWindow.Run($"Downloading .NET 8 Desktop Runtime ({arch})",
                (progress, cancel) => PortableDotNet.Install(app, arch, progress, cancel), out var failure);
            if (cancelled)
                return 1;
            if (failure is not null)
                return Fail($"HandPeg needs the .NET 8 Desktop Runtime ({arch}), and it could not be put in this folder:\n\n{failure.Message}\n\n"
                    + $"Start HandPeg.exe again to try once more, or install the .NET 8 Desktop Runtime ({arch}) from https://dotnet.microsoft.com/download/dotnet/8.0");

            root = DotNetRoots.Usable(Path.Combine(app, DotNetRoots.PortableFolder), "this copy's portable\\dotnet");
            if (root is null)
                return Fail("The .NET downloaded into portable\\dotnet cannot be found. Start HandPeg.exe again to download it once more.");
        }

        var exit = HostFxr.Run(root, exe, dll, arguments, out var started);
        if (!started)
            return Fail($"HandPeg could not start on {root.Where} ({root.Folder}).\n\n{(HostFxr.LastErrors.Length > 0 ? HostFxr.LastErrors : $"hostfxr said 0x{exit:X8}.")}");
        return exit;
    }

    private static IEnumerable<(string Folder, string Where)> Candidates(string app, string arch)
    {
        var system = Environment.GetEnvironmentVariable("HANDPEG_BOOTSTRAP_NO_SYSTEM_DOTNET") != "1";
        var x64OnArm64 = arch == "x64" && Native.IsWow64Process2(Native.GetCurrentProcess(), out _, out var machine) && machine == Native.ImageFileMachineArm64;
        return DotNetRoots.Candidates(app, system, Environment.GetEnvironmentVariable, system ? Native.RegisteredDotNet(arch) : null,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), arch, x64OnArm64);
    }

    private static int Fail(string message)
    {
        Native.MessageBox(IntPtr.Zero, message, Caption, Native.MbOk | Native.MbIconError | Native.MbSetForeground);
        return 1;
    }
}
