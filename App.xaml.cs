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
        LogBindingErrors();

        // Before any window is made, so that the first thing drawn already has the theme and the Windows accent colour.
        Services.ThemeManager.Follow(this);

        // A first start, or one whose settings never went through the first-run window: ask for the theme and
        // the mode before the main window (named as StartupUri) is created with them.
        if (!Services.AppSettings.Current.FirstRunComplete)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new FirstRunWindow().ShowDialog();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.Notifier.Dispose();

        // An update that was downloaded but not restarted into goes in once this process has gone.
        Services.AppUpdater.ApplyOnExit();
        base.OnExit(e);
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
