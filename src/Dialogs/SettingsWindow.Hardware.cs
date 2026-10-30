using System.Globalization;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Export & Hardware > GPU Roles (Pass 5.0-Core, micro-step 5.0-0): the compositor's and the timeline decode's
// lists (the background's and the export's are filled with the Performance lists, SettingsWindow.xaml.cs), and Test &
// Auto-Tune Hardware, whose measurements the stage draws as they come in (SettingsStagePreview.ShowBenchmark).
public partial class SettingsWindow
{
    // The benchmark running from here, while it runs: pressing the button again cancels it.
    private CancellationTokenSource? _benchmarkRun;

    // The result the stage shows when nothing is running: the last saved, or this screen's own run.
    private HardwareBenchmarkResult? _benchmarkShown;

    private void InitializeGpuRoles(IReadOnlyList<GpuAdapter> adapters)
    {
        // A card chosen earlier that has since gone is Auto again, as for the other two roles.
        if (adapters.All(a => a.Index != _settings.CompositorGpuAdapter))
            _settings.CompositorGpuAdapter = -1;
        if (adapters.All(a => a.Index != _settings.DecodeGpuAdapter))
            _settings.DecodeGpuAdapter = -1;

        // A card Windows lists twice (a headset's display on it, say) is offered once, as its first listing; a role saved
        // on the second listing shows (and keeps) the same card by the first one's number. The background's list is
        // filled after this, from DistinctCards too, and the export's again here.
        _settings.CompositorGpuAdapter = TwinOf(adapters, _settings.CompositorGpuAdapter);
        _settings.DecodeGpuAdapter = TwinOf(adapters, _settings.DecodeGpuAdapter);
        _settings.BackgroundGpu = TwinOf(adapters, _settings.BackgroundGpu);
        _settings.HardwareDecodeAdapter = TwinOf(adapters, _settings.HardwareDecodeAdapter);
        var distinct = DistinctCards(adapters);
        DecodeAdapterBox.ItemsSource = distinct.Prepend(GpuAdapter.Automatic).ToList();
        DecodeAdapterBox.SelectedValue = _settings.HardwareDecodeAdapter;

        var cards = distinct.Select(a => new NumberChoice(a.Display, a.Index)).ToList();
        CompositorGpuBox.ItemsSource = cards.Prepend(new NumberChoice("Auto (the high-performance card)", -1)).ToList();
        DecodeGpuBox.ItemsSource = cards.Prepend(new NumberChoice("Auto (the compositor's card, zero-copy)", -1)).ToList();
        CompositorGpuBox.SelectedValue = _settings.CompositorGpuAdapter;
        DecodeGpuBox.SelectedValue = _settings.DecodeGpuAdapter;

        // The last result, read off the UI thread once the screen is up (a small file), for the stage and the status line.
        Loaded += async (_, _) =>
        {
            var saved = await Task.Run(() => HardwareBenchmarkService.LoadSaved());
            if (saved is not null && _benchmarkRun is null && _benchmarkShown is null)
                ShowBenchmarkResult(saved, fresh: false);
        };

        // Closed while it runs: the run stops with the screen (nothing is left half-applied).
        Closed += (_, _) => _benchmarkRun?.Cancel();
    }

    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkRun is { } running)
        {
            running.Cancel();
            BenchmarkStatusText.Text = "Stopping...";
            return;
        }

        using var run = _benchmarkRun = new CancellationTokenSource();
        SetBenchmarkRunning(true);
        BenchmarkStatusText.Text = "Starting the test...";
        StagePreview.ShowBenchmark(_benchmarkShown, 0, "Starting", stale: false);

        // Reported on the UI thread (Progress posts to the context it was made on); a report that arrives after the run
        // ended is dropped.
        var progress = new Progress<HardwareBenchmarkProgress>(step =>
        {
            if (!ReferenceEquals(_benchmarkRun, run))
                return;

            var fraction = Math.Clamp(step.Fraction, 0, 1);
            BenchmarkStatusText.Text = step.Message;
            StagePreview.ShowBenchmark(step.Partial ?? _benchmarkShown, fraction,
                string.Create(CultureInfo.InvariantCulture, $"{step.Message}  {fraction * 100:0}%"), stale: false);
        });

        HardwareBenchmarkResult? result;
        try
        {
            result = await HardwareBenchmarkService.RunAsync(progress, run.Token);
        }
        finally
        {
            _benchmarkRun = null;
            SetBenchmarkRunning(false);
        }

        if (result is null)
        {
            BenchmarkStatusText.Text = HardwareBenchmarkService.LastFailure is { Length: > 0 } why ? why : "The test could not be run.";
            StagePreview.ShowBenchmark(_benchmarkShown, null, "", _benchmarkShown is { } shown && !HardwareBenchmarkService.IsCurrent(shown));
            return;
        }

        // A full run sets the four roles: in this screen's copy (the lists show them) and in the settings in effect, saved
        // at once, so they hold whether this screen is then saved or cancelled. One that stopped short changes nothing.
        if (result.Error.Length == 0 && result.Adapters.Count > 0)
        {
            HardwareBenchmarkService.WriteRoles(result.Recommended, _settings);
            HardwareBenchmarkService.ApplyRoles(result.Recommended);
            CompositorGpuBox.SelectedValue = _settings.CompositorGpuAdapter;
            DecodeGpuBox.SelectedValue = _settings.DecodeGpuAdapter;
            BackgroundGpuBox.SelectedValue = _settings.BackgroundGpu;
            DecodeAdapterBox.SelectedValue = _settings.HardwareDecodeAdapter;
        }

        ShowBenchmarkResult(result, fresh: true);
    }

    /// <summary>
    /// The cards a GPU list offers: each physical card once, as the first listing DXGI gives it. One card listed twice
    /// is told by the hardware benchmark's own rule (HardwareBenchmark.EnumerateAdapters' SameCardAs: the same vendor and
    /// device), so the lists and the benchmark's roles (always first listings) agree.
    /// </summary>
    internal static List<GpuAdapter> DistinctCards(IReadOnlyList<GpuAdapter> adapters) =>
        adapters.Where(a => TwinOf(adapters, a.Index) == a.Index).ToList();

    /// <summary>
    /// The number of the first listing of the card listed as <paramref name="index"/>: the number itself for a card
    /// listed once, and for a number that is no card's (Auto -1, Processor Only -2, a card that has gone).
    /// </summary>
    internal static int TwinOf(IReadOnlyList<GpuAdapter> adapters, int index)
    {
        var card = adapters.FirstOrDefault(a => a.Index == index);
        return card is null ? index : adapters.First(a => (a.VendorId, a.DeviceId) == (card.VendorId, card.DeviceId)).Index;
    }

    // The button turns into Stop while the test runs, and the four lists wait for its answer.
    private void SetBenchmarkRunning(bool running)
    {
        BenchmarkButtonText.Text = running ? "Stop the Test" : "Test & Auto-Tune Hardware";
        foreach (var box in new[] { CompositorGpuBox, DecodeGpuBox, BackgroundGpuBox, DecodeAdapterBox })
            box.IsEnabled = !running;
    }

    // A result on the stage, in the status line, and the reasons for its roles under the button.
    private void ShowBenchmarkResult(HardwareBenchmarkResult result, bool fresh)
    {
        _benchmarkShown = result;
        var stale = !HardwareBenchmarkService.IsCurrent(result);
        StagePreview.ShowBenchmark(result, null, "", stale);

        var tested = DateTime.TryParse(result.MeasuredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";
        BenchmarkStatusText.Text = result.Error.Length > 0 ? $"Stopped short ({result.Error}): the roles were left as they were."
            : fresh ? string.Create(CultureInfo.InvariantCulture, $"Done in {result.TotalSeconds:0.0} s: the four roles are set from it.")
            : stale ? "Last tested on other cards or drivers: test again."
            : tested.Length > 0 ? $"Last tested {tested}." : "Tested before.";

        var reasons = result.Recommended.Reasons;
        BenchmarkReasonsText.Text = string.Join(Environment.NewLine, reasons);
        BenchmarkReasonsText.Visibility = reasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BenchmarkStatusText.ToolTip = reasons.Count > 0 ? BenchmarkReasonsText.Text : null;
    }
}
