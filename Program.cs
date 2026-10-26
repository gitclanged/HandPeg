using System.IO;
using System.Runtime.CompilerServices;

namespace HandPegApp;

public static class Program
{
    /// <summary>
    /// The entry point, written out by hand (App.xaml is built as a Page, so none is generated) because
    /// Velopack's bootstrap has to be the very first thing that runs: when the installer or updater starts
    /// the program to finish an install, update or uninstall, it does that here and exits, before any of
    /// WPF has been touched. On an ordinary start it is not waited for (<see cref="VelopackStarted"/>).
    ///
    /// One thing comes before even that: HandPeg.exe started as its own crash sentinel (--crash-sentinel, see
    /// Services.CrashSentinel) does only that and leaves, without the updater, the settings or WPF ever being loaded.
    /// Everything else is in <see cref="Start"/>, so that compiling this method reaches none of them either.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Services.CrashSentinel.Argument)
        {
            Environment.ExitCode = Services.CrashSentinel.Run(args);
            return;
        }

        Start();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Start()
    {
        Services.StartupTrace.Mark("Main");

        // Multi-core JIT (Pass 4.1C): the methods this start had to compile (mostly generic code over HandPeg's own
        // types, which ReadyToRun cannot compile ahead of time: about 2,500 at each start) are recorded, and from the
        // next start on they are compiled on other cores ahead of this thread needing them. A small file in the cache.
        StartMulticoreJit();

        // A self-contained copy (portable.txt, --portable) is not an installed one: it has no installer to answer,
        // and nothing from an earlier build elsewhere on the computer to bring over. It stays in its folder.
        if (!Services.AppPaths.IsSelfContained)
        {
            // Started by the installer or the updater to finish its work (an install, update or uninstall hook): done
            // here, before anything else, and the process ends. Started by the user: the bootstrap only sets up what
            // the update check needs later, about 22 ms that no longer hold up the first frame (Pass 4.1C), so it
            // runs beside the window's start. AppPaths (read just above) finds a portable copy from the disk, not from
            // Velopack's locator, which does not exist until the bootstrap has run.
            if (Environment.GetCommandLineArgs().Skip(1).Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)))
                Velopack.VelopackApp.Build().Run();
            else
                VelopackStarted = Task.Run(RunVelopack);

            // Before anything reads a setting: what an earlier build kept beside the program is brought over, and a
            // portable copy's first start copies the installed places' settings into its own folder.
            Services.DataMigration.Run();
            Services.StartupTrace.Mark("data migration");
        }

        // What makes HandPeg fall over is caught from here on (the handlers and the native fault filter only: cheap;
        // the shared memory and the sentinel wait for the first frame). The version is in every report already.
        Services.CrashReporter.Install();
        Services.CrashReporter.Note("app", "start");
        Services.StartupTrace.Mark("crash reporter installed");

        // While this thread starts WPF, the settings and the presets are read on others: both are files to
        // parse, neither needs this thread, and by the time the first window asks for them they are there.
        _ = Task.Run(static () => Services.AppSettings.Current);
        Services.PresetStore.LoadEarly();

        // And the hardware encoders an earlier launch found (with the cards, which take a moment to list): the window
        // puts them in its list before its first frame, so a hardware default encoder is there from the start.
        Services.EncoderProber.LoadSavedResultEarly();
        Services.StartupTrace.Mark("early reads started");

        var app = new App();
        Services.StartupTrace.Mark("App constructed");
        app.InitializeComponent();
        Services.StartupTrace.Mark("App made");
        app.Run();

        // The window closed and the application ended in the ordinary way.
        Services.CrashReporter.MarkCleanExit();
    }

    /// <summary>
    /// The profile is this build's own (named after its module version id): one recorded by another build (the one
    /// before an update, or another copy used for comparison) would be replayed only in part, and was measured to
    /// slow the start it was meant to speed up. Each start rewrites its profile, so one unused for two weeks belongs to
    /// a build that is gone: those are deleted on a pool thread.
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
            // No profile: this start, and the next, compile on this thread as they always did.
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
            // Another copy of HandPeg is using it, or it is gone already: the next start tries again.
        }
    }

    /// <summary>
    /// Velopack's bootstrap on an ordinary start, run beside the window's start: the update checker waits for it before
    /// it asks Velopack anything (<see cref="Services.AppUpdater"/>). Done at once where it never ran (a hook start,
    /// a self-contained copy).
    /// </summary>
    public static Task VelopackStarted { get; private set; } = Task.CompletedTask;

    private static void RunVelopack()
    {
        try
        {
            Velopack.VelopackApp.Build().Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.AppLog.Write("Velopack's start-up did not complete", ex);
        }
    }
}
