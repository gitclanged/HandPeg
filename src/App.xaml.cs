using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HandPegApp;

/// <summary>
/// Application lifetime: start-up service wiring (crash reporting, work lanes, scaling, theme), first-run gating, and clean-exit handling.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Services.StartupTrace.Mark("App.OnStartup");

        // Report unhandled UI-thread exceptions (they still terminate the process).
        Services.CrashReporter.WatchDispatcher(Dispatcher);
        LogBindingErrors();
        if (Environment.GetEnvironmentVariable("HANDPEG_LOG_STALLS") == "1")
            WatchForStalls(Dispatcher);

        // Background work lanes, tool job objects, and the UI thread's processor placement.
        Services.WorkScheduler.Attach(Dispatcher);

        // Suppress menu beeps in every window without altering Windows accessibility settings.
        Services.QuietKeys.Follow();

        // Render windows at native monitor pixels unless the Windows-scaling setting is enabled.
        Services.UiScale.Follow();

        // Apply theme and Windows accent before any window exists, so Frame 1 is already themed.
        Services.ThemeManager.Follow(this);

        // First run, settings that never completed first-run, or settings migrated to a PC without FFmpeg: show the first-run
        // window (theme, workspace, components) modally before the StartupUri main window is created.
        if (!Services.AppSettings.Current.FirstRunComplete || CoreEnginesMissing())
        {
            Services.CrashReporter.Note("app", "first-run window");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new FirstRunWindow().ShowDialog();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnStartup(e);
        Services.StartupTrace.Mark("App.OnStartup done (theme, work lanes)");
    }

    /// <summary>
    /// True when FFmpeg is missing on this PC (settings copied from another machine, or tools folder deleted), which blocks probing,
    /// filmstrips and export. Costs at most two File.Exists in the local tools folder; a custom FFmpeg path in settings counts as present
    /// without a check.
    /// </summary>
    private static bool CoreEnginesMissing() =>
        Services.DependencyUpdater.GetLocalState(Services.DependencyUpdater.Ffmpeg) == Services.DependencyState.Missing;

    protected override void OnExit(ExitEventArgs e)
    {
        Services.CrashReporter.Note("app", "exit");
        Services.Notifier.Dispose();

        // Flush any debounced settings save before exit.
        Services.AppSettings.FlushPendingSave();

        // Apply a downloaded, not-yet-installed update after this process exits.
        Services.AppUpdater.ApplyOnExit();
        base.OnExit(e);

        // Normal exit (window closed or update restart): tell the crash sentinel there is nothing to report.
        Services.CrashReporter.MarkCleanExit();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        // Sign-out or shutdown may terminate the process before OnExit; record it as a clean exit, not a crash.
        if (!e.Cancel)
        {
            Services.CrashReporter.Note("app", "session ending: " + e.ReasonSessionEnding);
            Services.CrashReporter.MarkCleanExit();
        }
    }

    /// <summary>
    /// HANDPEG_LOG_STALLS=1: a background thread pings the UI thread at Input priority every 50 ms and logs each response slower
    /// than 32 ms (two frames). It records when and how long; the rest of the log (or a profiler) shows what was running.
    /// </summary>
    private static void WatchForStalls(System.Windows.Threading.Dispatcher dispatcher)
    {
        new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            while (!dispatcher.HasShutdownStarted)
            {
                Thread.Sleep(50);
                var asked = clock.Elapsed.TotalMilliseconds;
                try
                {
                    dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, () => { });
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                var waited = clock.Elapsed.TotalMilliseconds - asked;
                if (waited > 32)
                    Services.AppLog.Write(FormattableString.Invariant($"UI thread stall: input waited {waited:0} ms."));
            }
        }) { IsBackground = true, Name = "stall watch" }.Start();
    }

    /// <summary>
    /// Debug builds log XAML binding errors to binding-errors.log in the logs folder (otherwise only visible in a debugger's output,
    /// where a mistyped property is easy to miss). A second instance falls back to binding-errors.&lt;process id&gt;.log; if neither
    /// opens, errors go unlogged, never throwing (an IOException from OnStartup once crashed two concurrent development instances).
    /// </summary>
    [Conditional("DEBUG")]
    internal static void LogBindingErrors()
    {
        var folder = Services.AppPaths.Logs;
        StreamWriter? log = null;
        foreach (var name in new[] { "binding-errors.log", $"binding-errors.{Environment.ProcessId}.log" })
        {
            try
            {
                Directory.CreateDirectory(folder);
                log = new StreamWriter(new FileStream(Path.Combine(folder, name), FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by another instance or not writable: try the next name.
            }
        }

        if (log is null)
            return;

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(log));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }
}
