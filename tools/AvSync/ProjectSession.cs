using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// A project opened in the app's own view model, with no window: the graphs the harness plays are the ones the app
/// would send the player for the same moment (MainViewModel.BuildLiveFilterGraph, from the state CompileExportState
/// gathers: the files' starts and I-frames, the proxies in use, the decode threads), and the player's source is
/// the app's own (MainViewModel.PlayerSource: the EDL with the stand-in clock).
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

        // In this process only: nothing is downloaded, and no proxy is made while the harness runs.
        var settings = AppSettings.Current;
        settings.AutoUpdateDependencies = false;
        settings.ProxyIdleSeconds = 3600;
        settings.ProxyIdleSecondsWithBackgroundCard = 3600;
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

    /// <summary>The player's source as the app would open it now.</summary>
    public string PlayerSource => ViewModel.PlayerSource;

    /// <summary>
    /// Waits for what the app learns in the background after a project opens and builds its graphs with: the stand-in
    /// clock file, and where each video layer's picture starts. Returns what is still missing (empty when nothing is).
    /// </summary>
    public List<string> WaitForFacts(TimeSpan timeout)
    {
        List<string> Missing()
        {
            var missing = new List<string>();
            var timeline = FilterGraphBuilder.Lowering(State());
            if (!timeline.IsMainWholeSequence && !PlayerSource.Contains("clock_", StringComparison.OrdinalIgnoreCase))
                missing.Add("the stand-in clock file");
            foreach (var layer in timeline.Stack.Where(l => l.IsVideoFile))
            {
                var file = layer.ImagePath.Trim().Trim('"');
                if (MediaProbe.KnownVideoStart(file) is null)
                    missing.Add($"where the picture of {Path.GetExtension(file)} layer starts");
            }

            return missing;
        }

        // A first state asks the app to learn what it does not know yet (LearnVideoStart, StandInClock).
        _ = Missing();
        WaitUntil(() => Missing().Count == 0, timeout);
        Invalidate();
        return Missing();
    }

    /// <summary>
    /// Switches Settings' Per-Clip Video Proxies on or off (in this process), and with it the main video's proxy when
    /// <paramref name="mainProxy"/> names a file to stand for it; then lets the app bring its proxies in line, as it does
    /// after every change. Returns the proxies the app now plays, by the file they stand for.
    /// </summary>
    public IReadOnlyDictionary<string, string> UseProxies(bool on, string? mainProxy)
    {
        var settings = AppSettings.Current;
        settings.VideoProxies = on;
        settings.ProxyScope = on && mainProxy is not null ? AppSettings.ProxyAllVideos : AppSettings.ProxyLayersOnly;
        if (mainProxy is not null)
        {
            var made = (Dictionary<string, string>)typeof(MainViewModel).GetField("_proxies", Hidden)!.GetValue(ViewModel)!;
            made[ViewModel.LocalMediaPath.Trim().Trim('"')] = mainProxy;
        }

        typeof(MainViewModel).GetMethod("RefreshProxies", Hidden)!.Invoke(ViewModel, null);
        Invalidate();
        Pump(TimeSpan.FromMilliseconds(100));
        return new Dictionary<string, string>((Dictionary<string, string>)typeof(MainViewModel).GetField("_proxiesInUse", Hidden)!.GetValue(ViewModel)!,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The graph the app sends with a seek to <paramref name="seconds"/>: built for that moment, with the sound laid out
    /// from it (a seek while playing, or the paused player's graph once it has settled), as ApplyLiveFilterNow asks for it.
    /// </summary>
    public LiveGraph Graph(double seconds, (int Width, int Height) surface, bool sound = true)
    {
        var ms = (long)Math.Round(seconds * 1000);
        return ViewModel.BuildLiveFilterGraph(surface.Width, surface.Height, sound ? ms : null, ms, LiveLayerMode.Live);
    }

    /// <summary>The state the app compiles for its graphs (its copies of the layers, the files' starts, the proxies in use).</summary>
    public SequenceExportState State() =>
        (SequenceExportState)typeof(MainViewModel).GetMethod("CompileExportState", Hidden)!.Invoke(ViewModel, null)!;

    // Something the graphs are built from changed: the app gathers its state again for the next one.
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
