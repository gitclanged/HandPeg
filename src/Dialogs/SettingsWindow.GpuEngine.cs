using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Export & Hardware > GPU Preview Engine (Pass 5.0-AudioAnd3D, 5.0D): Instant Seek's frame cache (its maximum,
// put into effect in the running player when Save is pressed; a readout of what the running player's cache holds,
// refreshed while the page is shown; and Clear), and the compositor's decode threads. The threads are read when the
// player's compositor starts: they apply after a restart, as their row says.
//
// 5.4D-TabPerf (the user's U3): the cache is adaptive, a baseline grown on demand, so the setting is its Maximum Frame Cache in GB
// (AppSettings.GpuFrameCacheMb, still megabytes: 0 Auto, the hardware's ceiling; -1 Off): a list of Auto, Off, 1, 2, 4, 8, 12 and 16 GB
// that also takes a typed number (6, 2.5, "3 GB"). The readout says what is in use of the budget now and what it may grow to.
public partial class SettingsWindow
{
    // The readout's refresh, twice a second, only while it is on screen (its page shown, this screen open).
    private readonly DispatcherTimer _gpuCacheTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>The maximums the list offers, in GB (any other is typed, and joins the list).</summary>
    internal static readonly int[] FrameCacheChoicesGb = [1, 2, 4, 8, 12, 16];

    // The most a typed maximum may be (MB): 64 GB, as the old box's limit.
    private const int MostFrameCacheMb = 65536;

    private void InitializeGpuEngine()
    {
        _settings.GpuDecodeThreads = Math.Clamp(_settings.GpuDecodeThreads, 0, 4);
        GpuDecodeThreadsBox.ItemsSource = new List<NumberChoice> { new("Auto (2)", 0) }
            .Concat(Enumerable.Range(1, 4).Select(n => new NumberChoice(n.ToString(CultureInfo.InvariantCulture), n))).ToList();
        GpuDecodeThreadsBox.SelectedValue = _settings.GpuDecodeThreads;
        _settings.GpuFrameCacheMb = Math.Max(_settings.GpuFrameCacheMb, -1);

        var ceiling = GpuPreviewEngine.Current?.Client.CacheCeilingBytes ?? 0;
        GpuFrameCacheBox.ItemsSource = FrameCacheChoices(_settings.GpuFrameCacheMb, ceiling);
        GpuFrameCacheBox.SelectedValue = _settings.GpuFrameCacheMb;
        GpuFrameCacheBox.LostKeyboardFocus += (_, _) => TakeTypedFrameCache();
        GpuFrameCacheBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                TakeTypedFrameCache();
        };

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

    /// <summary>
    /// The list of maximums (MB values): Auto (with the hardware's ceiling when the running player knows it), Off, the GB steps, and
    /// <paramref name="current"/> in its place when it is none of them (a typed or an older setting).
    /// </summary>
    internal static List<NumberChoice> FrameCacheChoices(int current, long ceilingBytes)
    {
        var auto = ceilingBytes > 0 ? $"Auto (up to {PreviewCache.Describe(ceilingBytes)} here)" : "Auto";
        var sizes = FrameCacheChoicesGb.Select(gb => gb * 1024).ToList();
        if (current > 0 && !sizes.Contains(current))
        {
            sizes.Add(current);
            sizes.Sort();
        }

        return [new(auto, 0), new("Off", -1), .. sizes.Select(mb => new NumberChoice(DescribeFrameCacheMb(mb), mb))];
    }

    /// <summary>A maximum as the list shows it: whole gigabytes as "8 GB", others to two places ("2.5 GB").</summary>
    internal static string DescribeFrameCacheMb(int megabytes) => megabytes % 1024 == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024.0:0.##} GB");

    /// <summary>
    /// What a typed maximum means, in MB (the setting's unit): "Auto" 0, "Off" -1, a number of gigabytes (a "GB" or "G" after it, the
    /// user's own decimal mark or a point) that many, at most 64 GB, 0 Auto, below 0 Off; null when it is none of these.
    /// </summary>
    internal static int? ParseFrameCacheGb(string? typed)
    {
        var text = (typed ?? "").Trim();
        if (text.Length == 0)
            return null;
        if (text.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (text.StartsWith("off", StringComparison.OrdinalIgnoreCase))
            return -1;
        if (text.EndsWith("gb", StringComparison.OrdinalIgnoreCase))
            text = text[..^2].TrimEnd();
        else if (text.EndsWith('g') || text.EndsWith('G'))
            text = text[..^1].TrimEnd();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var gb)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out gb))
            return null;
        if (!double.IsFinite(gb))
            return null;
        if (gb < 0)
            return -1;
        var megabytes = Math.Round(gb * 1024);
        return megabytes <= 0 ? 0 : (int)Math.Min(megabytes, MostFrameCacheMb);
    }

    // A number typed into the list (Enter, or the focus leaving it): taken as the maximum, joining the list; anything else puts back
    // what is chosen.
    private void TakeTypedFrameCache()
    {
        var chosen = GpuFrameCacheBox.SelectedItem as NumberChoice;
        if (chosen is not null && string.Equals(GpuFrameCacheBox.Text, chosen.Display, StringComparison.Ordinal))
            return;

        if (ParseFrameCacheGb(GpuFrameCacheBox.Text) is { } megabytes)
        {
            if (GpuFrameCacheBox.ItemsSource is List<NumberChoice> choices && choices.All(c => c.Value != megabytes))
                GpuFrameCacheBox.ItemsSource = FrameCacheChoices(megabytes, GpuPreviewEngine.Current?.Client.CacheCeilingBytes ?? 0);
            GpuFrameCacheBox.SelectedValue = megabytes;
            _settings.GpuFrameCacheMb = megabytes;
        }
        else
        {
            GpuFrameCacheBox.SelectedValue = _settings.GpuFrameCacheMb;
        }

        if (GpuFrameCacheBox.SelectedItem is NumberChoice shown)
            GpuFrameCacheBox.Text = shown.Display;
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

        // In use of the budget now, and what the budget may grow to when a project needs it (5.4D-TabPerf).
        var stats = engine.CacheStats;
        var client = engine.Client;
        var maximum = client.CacheMaximumBytes;
        GpuCacheUsage.Text = stats.BudgetBytes > 0
            ? $"{PreviewCache.Describe(stats.Bytes)} of {PreviewCache.Describe(stats.BudgetBytes)} in use" +
              (maximum > stats.BudgetBytes ? $", grows to {PreviewCache.Describe(maximum)}" : "")
            : $"{PreviewCache.Describe(stats.Bytes)} in use";

        var asked = stats.Hits + stats.Misses;
        var share = asked > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({100.0 * stats.Hits / asked:0}%)") : "";
        var latency = stats.LastExactLatencyMs > 0 ? string.Create(CultureInfo.InvariantCulture, $"{stats.LastExactLatencyMs:0.0} ms") : "none yet";
        var held = client.WorkerPrivateBytes > 0 ? $"  ·  the compositor holds {PreviewCache.Describe(client.WorkerPrivateBytes)} in all" : "";
        GpuCacheReadout.Text = string.Create(CultureInfo.InvariantCulture,
            $"{stats.Frames:N0} frames  ·  {stats.Hits:N0} hits, {stats.Misses:N0} misses{share}  ·  last exact frame {latency}{held}");
    }

    // Clear: every frame, and what the worker kept beside them (5.4D-TabPerf: a trim after the clear, both counted requests).
    private void ClearGpuFrameCache_Click(object sender, RoutedEventArgs e)
    {
        if (GpuPreviewEngine.Current is { } engine)
        {
            engine.Client.ClearFrameCache();
            engine.Client.TrimWorker();
        }

        ShowGpuCache();
    }

    /// <summary>After Save: the frame cache's maximum goes into effect in the running player at once (the worker reads it on its next turn).</summary>
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
