using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// A project opened in the app's own view model, with no window: what the harness draws and plays is what the app hands
/// its GPU engine (MainViewModel.LivePreviewState, the state CompileExportState gathers: the files' starts and I-frames,
/// the proxies in use, the preview's own reading of them).
///
/// The settings file is never written: the view model saves some settings when they change, so the file is read
/// before the project is opened and put back as it was when the session ends. The project file is only read.
/// </summary>
internal sealed class ProjectSession : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly byte[]? _settingsBefore;
    private readonly Application _application;

    public MainViewModel ViewModel { get; }

    private ProjectSession(string project, TextWriter log)
    {
        _settingsBefore = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;

        // An application object, never run, so that what the view model hands to the interface thread
        // (Application.Current.Dispatcher) runs while the harness pumps that thread (see Pump).
        _application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // In this process only: nothing is downloaded, and no proxy is made while the harness runs (see UseProxies).
        var settings = AppSettings.Current;
        settings.AutoUpdateDependencies = false;
        settings.ClearProjectProxiesOnExit = false;
        settings.SendCrashReports = false;

        EncoderProber.LoadSavedResultEarly();
        ViewModel = new MainViewModel();
        ViewModel.TakeEarlyHardwareEncoders();
        var loading = ViewModel.LoadProjectAsync(project);
        if (!WaitUntil(() => loading.IsCompleted, TimeSpan.FromSeconds(120)))
            throw new TimeoutException("The project did not open within two minutes.");
        loading.GetAwaiter().GetResult();
        log.WriteLine($"Opened: {ViewModel.StatusText}");
    }

    public static ProjectSession Open(string project, TextWriter log) => new(project, log);

    /// <summary>
    /// Waits for what the app learns in the background after a project opens and places its pictures and sounds with:
    /// where each video layer's picture starts. Returns what is still missing (empty when nothing is).
    /// </summary>
    public List<string> WaitForFacts(TimeSpan timeout)
    {
        List<string> Missing()
        {
            var missing = new List<string>();
            var timeline = FilterGraphBuilder.Lowering(State());
            foreach (var layer in timeline.Stack.Where(l => l.IsVideoFile))
            {
                var file = layer.ImagePath.Trim().Trim('"');
                if (MediaProbe.KnownVideoStart(file) is null)
                    missing.Add($"where the picture of {Path.GetExtension(file)} layer starts");
            }

            return missing;
        }

        // A first state asks the app to learn what it does not know yet (LearnVideoStart).
        _ = Missing();
        WaitUntil(() => Missing().Count == 0, timeout);
        Invalidate();
        return Missing();
    }

    /// <summary>
    /// Switches the proxies on or off (in this process): the clips whose file has a proxy made already are set to Optimize
    /// Long-GOP Clip for Scrubbing (on) or back (off), the main video's too when <paramref name="mainProxy"/> names a file
    /// to stand for it (5.0-Verify: proxies are asked for clip by clip, nothing is made by itself; a file with no proxy is
    /// left alone, so none is made while the harness runs); then lets the app bring its proxies in line, as it does after
    /// every change. Returns the proxies the app now plays, by the file they stand for.
    /// </summary>
    public IReadOnlyDictionary<string, string> UseProxies(bool on, string? mainProxy)
    {
        if (mainProxy is not null)
        {
            var made = (Dictionary<string, string>)typeof(MainViewModel).GetField("_proxies", Hidden)!.GetValue(ViewModel)!;
            made[ViewModel.LocalMediaPath.Trim().Trim('"')] = mainProxy;
        }

        foreach (var clip in ViewModel.Layers.Where(l => !l.IsDeleted).ToList())
        {
            var hasProxy = clip.IsVideoFile ? ProxyCache.Find(clip.ImagePath.Trim().Trim('"'), ViewModel.CurrentProjectPath) is not null
                : clip.IsMainVideo && mainProxy is not null;
            if (hasProxy)
                ViewModel.SetProxyChoice(clip, on ? ProxyChoice.Make : ProxyChoice.Auto);
        }

        typeof(MainViewModel).GetMethod("RefreshProxies", Hidden)!.Invoke(ViewModel, null);
        Invalidate();
        Pump(TimeSpan.FromMilliseconds(100));
        return new Dictionary<string, string>((Dictionary<string, string>)typeof(MainViewModel).GetField("_proxiesInUse", Hidden)!.GetValue(ViewModel)!,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The state the app hands the GPU engine (its copies of the clips, the files' starts, the proxies in use, the soloed tracks).</summary>
    public SequenceExportState State() => ViewModel.LivePreviewState();

    // Something the state is gathered from changed: the app gathers it again for the next one.
    private void Invalidate() => typeof(MainViewModel).GetMethod("InvalidateLiveFilter", Hidden)!.Invoke(ViewModel, null);

    /// <summary>Runs the interface thread's queue for a while: what the app hands to it (a fact learnt, a file made) happens.</summary>
    public static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = time };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Pumps the interface thread until a condition holds. Returns whether it did in time.</summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeout.TotalMilliseconds)
                return false;
            Pump(TimeSpan.FromMilliseconds(50));
        }

        return true;
    }

    public void Dispose()
    {
        // The settings file as it was before the project was opened, byte for byte.
        try
        {
            var now = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
            if (_settingsBefore is null && now is not null)
                File.Delete(AppPaths.SettingsFile);
            else if (_settingsBefore is not null && (now is null || !now.AsSpan().SequenceEqual(_settingsBefore)))
                File.WriteAllBytes(AppPaths.SettingsFile, _settingsBefore);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"The settings file could not be put back as it was: {ex.Message}");
        }

        _ = _application;
    }
}
