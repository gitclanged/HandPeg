using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HandPegApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Services.StartupTrace.Mark("App.OnStartup");

        // Exceptions that escape the window's thread are reported (and still end the process, as they always have).
        Services.CrashReporter.WatchDispatcher(Dispatcher);
        LogBindingErrors();
        if (Environment.GetEnvironmentVariable("HANDPEG_LOG_STALLS") == "1")
            WatchForStalls(Dispatcher);

        // The lanes for background work, the jobs its tools run in, and this thread's place among the processors.
        Services.WorkScheduler.Attach(Dispatcher);

        // Before any window is made, so that the first thing drawn already has the theme and the Windows accent colour.
        Services.ThemeManager.Follow(this);

        // A first start, or one whose settings never went through the first-run window: ask for the theme and
        // the mode before the main window (named as StartupUri) is created with them.
        if (!Services.AppSettings.Current.FirstRunComplete)
        {
            Services.CrashReporter.Note("app", "first-run window");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new FirstRunWindow().ShowDialog();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnStartup(e);
        Services.StartupTrace.Mark("App.OnStartup done (theme, work lanes)");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.CrashReporter.Note("app", "exit");
        Services.Notifier.Dispose();

        // An update that was downloaded but not restarted into goes in once this process has gone.
        Services.AppUpdater.ApplyOnExit();
        base.OnExit(e);

        // An ordinary way out (the window closed, or a restart into an update): the crash sentinel has nothing to write.
        Services.CrashReporter.MarkCleanExit();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        // Windows is signing out or shutting down, and may end HandPeg before OnExit is reached: that is no crash.
        if (!e.Cancel)
        {
            Services.CrashReporter.Note("app", "session ending: " + e.ReasonSessionEnding);
            Services.CrashReporter.MarkCleanExit();
        }
    }

    /// <summary>
    /// HANDPEG_LOG_STALLS=1: a thread of its own asks the UI thread, twenty times a second, to answer at input
    /// priority, and writes to the log every time the answer took longer than two frames. What was running then is
    /// for the rest of the log (and a profiler) to say; this says when, and for how long.
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
    /// Debug builds write XAML binding errors to binding-errors.log in the logs folder. They are otherwise
    /// only visible in a debugger's output window, where a mistyped property name is easy to miss.
    /// </summary>
    [Conditional("DEBUG")]
    private static void LogBindingErrors()
    {
        var folder = Services.AppPaths.Logs;
        Directory.CreateDirectory(folder);

        var log = new StreamWriter(Path.Combine(folder, "binding-errors.log"), append: false) { AutoFlush = true };
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(log));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }
}
