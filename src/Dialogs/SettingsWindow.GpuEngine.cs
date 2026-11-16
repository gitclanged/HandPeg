using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Export & Hardware > GPU Preview Engine: Instant Seek frame cache (maximum applied to the running player on Save; live
// readout while the page is visible; Clear) and compositor decode threads (read at compositor start, so they need a restart).
//
// The cache is adaptive (baseline grown on demand), so the setting is Maximum Frame Cache (AppSettings.GpuFrameCacheMb in MB: 0 Auto
// = hardware ceiling, -1 Off), offered as Auto, Off, 1/2/4/8/12/16 GB or a typed value (6, 2.5, "3 GB"). The readout shows current
// use and growth headroom.
public partial class SettingsWindow
{
    // Readout refresh at 2 Hz, only while the page is visible.
    private readonly DispatcherTimer _gpuCacheTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Preset maximums in GB (typed values are appended).</summary>
    internal static readonly int[] FrameCacheChoicesGb = [1, 2, 4, 8, 12, 16];

    // Upper bound for a typed maximum (MB): 64 GB.
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
    /// Maximum choices (MB): Auto (with hardware ceiling when known), Off, GB steps, plus <paramref name="current"/> in order when it
    /// is a custom or legacy value.
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

    /// <summary>Display label: whole GB as "8 GB", otherwise up to two decimals ("2.5 GB").</summary>
    internal static string DescribeFrameCacheMb(int megabytes) => megabytes % 1024 == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024.0:0.##} GB");

    /// <summary>
    /// Parses a typed maximum to MB: "Auto" = 0, "Off" = -1, or a GB number (optional "GB"/"G" suffix, culture or invariant decimal
    /// separator) capped at 64 GB, 0 = Auto, negative = Off. Null when unparseable.
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

    // Commit typed text on Enter or focus loss: valid values become the maximum and join the list; invalid text reverts.
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

    /// <summary>Live frame cache status of the running player, or why none is available.</summary>
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

        // Current budget use and the ceiling it may grow to on demand.
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

    // Clear: drops all frames and worker-side state (clear followed by trim, both counted as requests).
    private void ClearGpuFrameCache_Click(object sender, RoutedEventArgs e)
    {
        if (GpuPreviewEngine.Current is { } engine)
        {
            engine.Client.ClearFrameCache();
            engine.Client.TrimWorker();
        }

        ShowGpuCache();
    }

    /// <summary>On Save, pushes the cache maximum to the running player (the worker applies it on its next cycle).</summary>
    private static void ApplyGpuEngine()
    {
        if (GpuPreviewEngine.Current is { } engine)
            engine.Client.FrameCacheBudget = FrameCacheBytes(AppSettings.Current.GpuFrameCacheMb);
    }

    /// <summary>Setting in compositor units (<see cref="CompositorClient.FrameCacheBudget"/>): 0 auto, -1 off, else bytes.</summary>
    internal static long FrameCacheBytes(int megabytes) => megabytes switch
    {
        < 0 => -1L,
        0 => 0L,
        _ => (long)megabytes << 20,
    };
}
