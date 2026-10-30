using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Export & Hardware > GPU Preview Engine (Pass 5.0-AudioAnd3D, 5.0D): Instant Seek's frame cache (its budget,
// put into effect in the running player when Save is pressed; a readout of what the running player's cache holds,
// refreshed while the page is shown; and Clear), and the compositor's decode threads. The threads are read when the
// player's compositor starts: they apply after a restart, as their row says.
public partial class SettingsWindow
{
    // The readout's refresh, twice a second, only while it is on screen (its page shown, this screen open).
    private readonly DispatcherTimer _gpuCacheTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void InitializeGpuEngine()
    {
        _settings.GpuDecodeThreads = Math.Clamp(_settings.GpuDecodeThreads, 0, 4);
        GpuDecodeThreadsBox.ItemsSource = new List<NumberChoice> { new("Auto (2)", 0) }
            .Concat(Enumerable.Range(1, 4).Select(n => new NumberChoice(n.ToString(CultureInfo.InvariantCulture), n))).ToList();
        GpuDecodeThreadsBox.SelectedValue = _settings.GpuDecodeThreads;
        _settings.GpuFrameCacheMb = Math.Max(_settings.GpuFrameCacheMb, -1);

        _gpuCacheTimer.Tick += (_, _) => ShowGpuCache();
        GpuCacheReadout.IsVisibleChanged += (_, _) =>
        {
            if (GpuCacheReadout.IsVisible)
            {
                ShowGpuCache();
                _gpuCacheTimer.Start();
            }
            else
            {
                _gpuCacheTimer.Stop();
            }
        };
        Closed += (_, _) => _gpuCacheTimer.Stop();
        ShowGpuCache();
    }

    /// <summary>What the running player's frame cache holds and does now; a word on why there is none while the player is not running.</summary>
    private void ShowGpuCache()
    {
        var engine = GpuPreviewEngine.Current;
        ClearGpuCacheButton.IsEnabled = engine is not null;
        if (engine is null)
        {
            GpuCacheUsage.Text = "";
            GpuCacheReadout.Text = "Not running: the compositor starts with the player";
            return;
        }

        var stats = engine.CacheStats;
        GpuCacheUsage.Text = stats.BudgetBytes > 0
            ? $"{PreviewCache.Describe(stats.Bytes)} of {PreviewCache.Describe(stats.BudgetBytes)} in use"
            : $"{PreviewCache.Describe(stats.Bytes)} in use";

        var asked = stats.Hits + stats.Misses;
        var share = asked > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({100.0 * stats.Hits / asked:0}%)") : "";
        var latency = stats.LastExactLatencyMs > 0 ? string.Create(CultureInfo.InvariantCulture, $"{stats.LastExactLatencyMs:0.0} ms") : "none yet";
        GpuCacheReadout.Text = string.Create(CultureInfo.InvariantCulture,
            $"{stats.Frames:N0} frames  ·  {stats.Hits:N0} hits, {stats.Misses:N0} misses{share}  ·  last exact frame {latency}");
    }

    private void ClearGpuFrameCache_Click(object sender, RoutedEventArgs e)
    {
        GpuPreviewEngine.Current?.Client.ClearFrameCache();
        ShowGpuCache();
    }

    /// <summary>After Save: the frame cache's budget goes into effect in the running player at once (the worker reads it on its next turn).</summary>
    private static void ApplyGpuEngine()
    {
        if (GpuPreviewEngine.Current is { } engine)
            engine.Client.FrameCacheBudget = FrameCacheBytes(AppSettings.Current.GpuFrameCacheMb);
    }

    /// <summary>The setting as the compositor takes it (<see cref="CompositorClient.FrameCacheBudget"/>): 0 automatic, -1 off, else bytes.</summary>
    internal static long FrameCacheBytes(int megabytes) => megabytes switch
    {
        < 0 => -1L,
        0 => 0L,
        _ => (long)megabytes << 20,
    };
}
