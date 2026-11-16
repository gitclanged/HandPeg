using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HandPegApp.Native;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp;

public static partial class Program
{
    /// <summary>
    /// Hand-written entry point (App.xaml is a Page, so none is generated) because Velopack's bootstrap must run first: installer/updater
    /// hook launches complete here and exit before WPF loads. Ordinary starts do not wait for it (<see cref="VelopackStarted"/>).
    ///
    /// Worker modes come even earlier: --crash-sentinel (Services.CrashSentinel), the compositor worker and the hardware benchmark run and
    /// exit without loading the updater, settings or WPF. Everything else lives in <see cref="Start"/> so JIT of Main touches none of it.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // Suppress Windows error boxes for this process and inherited by children: a tool missing a DLL (e.g. whisper-cli.exe without
        // libomp140.aarch64.dll on ARM64) then fails with STATUS_DLL_NOT_FOUND (0xC0000135), which is reported, instead of blocking on a modal.
        SetErrorMode(SetErrorMode(0) | FailCriticalErrors | NoGpFaultErrorBox);

        if (args.Length > 0 && args[0] == Services.CrashSentinel.Argument)
        {
            Environment.ExitCode = Services.CrashSentinel.Run(args);
            return;
        }

        // Compositor worker and hardware benchmark are HandPeg.exe modes launched by Services.WorkerProcess (no second binary). Like the
        // sentinel, neither loads the updater, settings or WPF.
        if (args.Length > 0 && args[0] == Compositor.CompositorWorker.Argument)
        {
            Environment.ExitCode = Compositor.CompositorWorker.Run(args);
            return;
        }

        if (args.Length > 0 && args[0] == Compositor.HardwareBenchmark.Argument)
        {
            Environment.ExitCode = Compositor.HardwareBenchmark.Run(args);
            return;
        }

        Start();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Start()
    {
        Services.StartupTrace.Mark("Main");

        // Windows shows the app-starting cursor until the first GetMessage (PeekMessage alone does not clear it; measured lasting until
        // WPF's first GetMessage, 120-160 ms in). Post WM_NULL to ourselves and GetMessage it immediately so the cursor reverts while the
        // window is still being built (Law 1: 0 ms busy cursor). PeekMessage first creates the thread's message queue.
        _ = PeekMessageW(out _, 0, 0, 0, 0);
        if (PostThreadMessageW(GetCurrentThreadId(), 0, 0, 0))
            _ = GetMessageW(out _, 0, 0, 0);

        // Multi-core JIT: records the ~2,500 methods compiled at start (mostly generics over HandPeg types that ReadyToRun cannot
        // precompile) and, from the next start, compiles them on other cores ahead of use. Profile stored in the cache folder.
        StartMulticoreJit();

        // Self-contained (portable.txt, --portable) copies have no installer hooks and nothing to migrate; they stay in their folder.
        if (!Services.AppPaths.IsSelfContained)
        {
            // Installer/updater hook launch (install, update, uninstall): run it here and exit. Normal launch: the bootstrap only prepares
            // later update checks (~22 ms), so it runs in parallel with window start-up instead of delaying Frame 1. AppPaths detects
            // portable copies from disk, not Velopack's locator, which does not exist until the bootstrap runs.
            if (Environment.GetCommandLineArgs().Skip(1).Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)))
                VelopackHooks().Run();
            else
                VelopackStarted = Task.Run(RunVelopack);

            // Before any settings read: migrate data an older build kept beside the program; a portable copy's first start imports the
            // installed settings into its own folder.
            Services.DataMigration.Run();
            Services.StartupTrace.Mark("data migration");
        }

        // Project/bundle launched while HandPeg runs: hand it to the running instance (which prompts for unsaved changes, opens it and
        // activates) and exit. Otherwise claim primary-instance status (named mutex now; pipe after Frame 1). No-project starts skip the hand-off.
        if (Services.SingleInstance.LaunchFile() is { } launched && Services.SingleInstance.HandOff(launched))
            return;
        Services.SingleInstance.Claim();

        // Crash capture from here on (cheap handlers and native fault filter only; shared memory and sentinel wait for Frame 1). The
        // version is already in every report.
        Services.CrashReporter.Install();
        Services.CrashReporter.Note("app", "start");
        Services.StartupTrace.Mark("crash reporter installed");

        // Parse settings and presets on pool threads while this thread starts WPF; they are ready before the first window needs them.
        _ = Task.Run(static () => Services.AppSettings.Current);
        Services.PresetStore.LoadEarly();

        // Load the hardware encoders (and adapters) found by a previous launch so a hardware default encoder is listed before Frame 1.
        Services.EncoderProber.LoadSavedResultEarly();
        Services.StartupTrace.Mark("early reads started");

        var app = new App();
        Services.StartupTrace.Mark("App constructed");
        app.InitializeComponent();
        Services.StartupTrace.Mark("App made");
        app.Run();

        // Normal shutdown.
        Services.CrashReporter.MarkCleanExit();
    }

    /// <summary>
    /// Starts a per-build JIT profile (named by module version id): replaying another build's profile only partially applies and was
    /// measured to slow start-up. Each start rewrites its profile, so profiles untouched for 14 days are deleted on a pool thread.
    /// </summary>
    private static void StartMulticoreJit()
    {
        try
        {
            var folder = Path.Combine(Services.AppPaths.Cache, "startup");
            Directory.CreateDirectory(folder);
            var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"startup-{typeof(Program).Module.ModuleVersionId:N}.jitprofile");
            System.Runtime.ProfileOptimization.SetProfileRoot(folder);
            System.Runtime.ProfileOptimization.StartProfile(name);
            ThreadPool.QueueUserWorkItem(_ => DeleteOtherProfiles(folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No profile: fall back to ordinary on-thread JIT.
        }
    }

    private static void DeleteOtherProfiles(string folder, string keep)
    {
        try
        {
            var unusedSince = DateTime.UtcNow - TimeSpan.FromDays(14);
            foreach (var file in Directory.EnumerateFiles(folder, "*.jitprofile"))
            {
                if (!string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(file) < unusedSince)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use by another instance or already gone; retried next start.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Message message, nint window, uint first, uint last);

    // Posts WM_NULL (0) to this thread for the immediate first GetMessage above.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);

    // SEM_FAILCRITICALERRORS (no critical-error box, including DLL-not-found) and SEM_NOGPFAULTERRORBOX.
    private const uint FailCriticalErrors = 0x0001, NoGpFaultErrorBox = 0x0002;

    /// <summary>
    /// Velopack bootstrap task on ordinary starts (runs in parallel with window start-up); <see cref="Services.AppUpdater"/> awaits it
    /// before calling Velopack. Already completed for hook starts and self-contained copies.
    /// </summary>
    public static Task VelopackStarted { get; private set; } = Task.CompletedTask;

    private static void RunVelopack()
    {
        try
        {
            VelopackHooks().Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.AppLog.Write("Velopack's start-up did not complete", ex);
        }
    }

    // Installer hooks (run on hook starts, only configured on normal starts): uninstall unregisters this user's Explorer thumbnail handler
    // (registry only, before settings or WPF).
    private static Velopack.VelopackApp VelopackHooks() =>
        Velopack.VelopackApp.Build().OnBeforeUninstallFastCallback(_ => Services.ExplorerThumbnails.Unregister());
}
