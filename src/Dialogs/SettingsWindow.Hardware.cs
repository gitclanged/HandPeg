using System.Globalization;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Export & Hardware > GPU & NPU Roles (Pass 5.0-Core, micro-step 5.0-0; the NPUs 5.2-Arm64AndNpu): the compositor's
// and the timeline decode's lists (the background's and the export's are filled with the Performance lists,
// SettingsWindow.xaml.cs), Test & Auto-Tune Hardware, whose measurements the stage draws as they come in
// (SettingsStagePreview.ShowBenchmark), the NPUs found (listed off the UI thread once the screen is up) and what Auto-Captions
// run on (AppSettings.CaptionAccelerator).
public partial class SettingsWindow
{
    // The benchmark running from here, while it runs: pressing the button again cancels it.
    private CancellationTokenSource? _benchmarkRun;

    // The result the stage shows when nothing is running: the last saved, or this screen's own run.
    private HardwareBenchmarkResult? _benchmarkShown;

    // The Export GPU's Auto, under its own words (GpuAdapter.Automatic says "Automatic", which meant FFmpeg's choice before 5.4C).
    private static readonly GpuAdapter ExportGpuAuto = new(-1, "Auto (the Compositor GPU)");

    private void InitializeGpuRoles(IReadOnlyList<GpuAdapter> adapters)
    {
        // A card chosen earlier that has since gone is Auto again, for the Export GPU as for the other roles.
        if (adapters.All(a => a.Index != _settings.CompositorGpuAdapter))
            _settings.CompositorGpuAdapter = -1;
        if (adapters.All(a => a.Index != _settings.DecodeGpuAdapter))
            _settings.DecodeGpuAdapter = -1;
        if (adapters.All(a => a.Index != _settings.HardwareDecodeAdapter))
            _settings.HardwareDecodeAdapter = -1;

        // A card Windows lists twice (a headset's display on it, say) is offered once, as its first listing; a role saved
        // on the second listing shows (and keeps) the same card by the first one's number. The background's list is
        // filled after this, from DistinctCards too, and the export's again here.
        _settings.CompositorGpuAdapter = TwinOf(adapters, _settings.CompositorGpuAdapter);
        _settings.DecodeGpuAdapter = TwinOf(adapters, _settings.DecodeGpuAdapter);
        _settings.BackgroundGpu = TwinOf(adapters, _settings.BackgroundGpu);
        _settings.HardwareDecodeAdapter = TwinOf(adapters, _settings.HardwareDecodeAdapter);
        var distinct = DistinctCards(adapters);

        // The Export GPU (5.4C): the card exports composite on (GpuExport.ExportAdapter); Auto is the Compositor GPU.
        DecodeAdapterBox.ItemsSource = distinct.Prepend(ExportGpuAuto).ToList();
        DecodeAdapterBox.SelectedValue = _settings.HardwareDecodeAdapter;

        var cards = distinct.Select(a => new NumberChoice(a.Display, a.Index)).ToList();
        CompositorGpuBox.ItemsSource = cards.Prepend(new NumberChoice("Auto (the high-performance card)", -1)).ToList();
        DecodeGpuBox.ItemsSource = cards.Prepend(new NumberChoice("Auto (the compositor's card, zero-copy)", -1)).ToList();
        CompositorGpuBox.SelectedValue = _settings.CompositorGpuAdapter;
        DecodeGpuBox.SelectedValue = _settings.DecodeGpuAdapter;

        // What Auto-Captions run on: the choice, and (once the screen is up) what it comes to with the whisper build installed.
        if (!AppSettings.CaptionAccelerators.Contains(_settings.CaptionAccelerator))
            _settings.CaptionAccelerator = AppSettings.SpeechOnAuto;
        CaptionAcceleratorBox.ItemsSource = new List<Choice>
        {
            new(AppSettings.SpeechOnAuto, "Auto (NPU, else graphics card, else processor)"),
            new(AppSettings.SpeechOnNpu, "NPU"),
            new(AppSettings.SpeechOnGpu, "Graphics Card"),
            new(AppSettings.SpeechOnCpu, "Processor"),
        };
        CaptionAcceleratorBox.SelectedValue = _settings.CaptionAccelerator;
        CaptionAcceleratorBox.SelectionChanged += (_, _) => ShowSpeechPlan();

        // The last result, read off the UI thread once the screen is up (a small file), for the stage and the status line; the
        // NPUs, listed off it too (DXCore, once a session).
        Loaded += async (_, _) =>
        {
            ShowSpeechPlan();
            var saved = await Task.Run(() => HardwareBenchmarkService.LoadSaved());
            if (saved is not null && _benchmarkRun is null && _benchmarkShown is null)
                ShowBenchmarkResult(saved, fresh: false);
            try
            {
                ShowNpus(await NpuAdapters.ListAsync());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("The NPUs could not be listed", ex);
                NpuText.Text = "The NPUs could not be listed (the log says why).";
            }
        };

        // Closed while it runs: the run stops with the screen (nothing is left half-applied).
        Closed += (_, _) => _benchmarkRun?.Cancel();
        InitializeSyncTest();
    }

    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkRun is { } running)
        {
            running.Cancel();
            BenchmarkStatusText.Text = "Stopping...";
            return;
        }

        if (SyncTestCard.IsRunning)
            return;

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
        if (CanApplyRoles(result))
            SetRecommendedRoles(result);

        ShowBenchmarkResult(result, fresh: true);
    }

    /// <summary>Whether a result's roles can be set (5.5.0, Apply Recommended Roles; pure): one that ended, named cards, and holds for these cards.</summary>
    internal static bool CanApplyRoles(HardwareBenchmarkResult? result) =>
        result is { Error.Length: 0, Adapters.Count: > 0 } && HardwareBenchmarkService.IsCurrent(result);

    // A result's roles set: in this screen's copy (the lists show them) and in the settings in effect, saved at once, as a test that ends sets them.
    private void SetRecommendedRoles(HardwareBenchmarkResult result)
    {
        HardwareBenchmarkService.WriteRoles(result.Recommended, _settings);
        HardwareBenchmarkService.ApplyRoles(result.Recommended);
        CompositorGpuBox.SelectedValue = _settings.CompositorGpuAdapter;
        DecodeGpuBox.SelectedValue = _settings.DecodeGpuAdapter;
        BackgroundGpuBox.SelectedValue = _settings.BackgroundGpu;
        DecodeAdapterBox.SelectedValue = _settings.HardwareDecodeAdapter;
    }

    // 5.5.0 (the user's Q10 b): Apply Recommended Roles: the kept result's roles set again (after the user changed them by hand, say).
    private void ApplyRoles_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkRun is not null || !CanApplyRoles(_benchmarkShown))
            return;

        SetRecommendedRoles(_benchmarkShown!);
        BenchmarkStatusText.Text = _benchmarkShown!.Kind == HardwareBenchmarkResult.KindQuick
            ? "The roles the first start's quick check recommends are set (run the test for the full measurements)."
            : "The roles the last test recommends are set.";
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

    // The NPUs found, each with what it is used for (nothing yet: said as it is).
    private void ShowNpus(IReadOnlyList<NpuAdapter> npus)
    {
        NpuText.Text = npus.Count == 0
            ? "None found (an NPU's driver lists it to Windows: a Snapdragon X, an Intel Core Ultra or an AMD Ryzen AI laptop has one)."
            : string.Join(Environment.NewLine, npus.Select(NpuAdapters.Describe)) + Environment.NewLine + NpuAdapters.UsedFor;
    }

    // What Auto-Captions would run on with the choice shown, in a sentence (a few file checks, on a pool thread).
    private async void ShowSpeechPlan()
    {
        var choice = CaptionAcceleratorBox.SelectedValue as string ?? AppSettings.SpeechOnAuto;
        try
        {
            var (plan, installed) = await Task.Run(() => (CaptionGenerator.PlanSpeech(choice), System.IO.File.Exists(DependencyUpdater.WhisperPath)));
            if ((CaptionAcceleratorBox.SelectedValue as string ?? AppSettings.SpeechOnAuto) == choice)
                CaptionAcceleratorText.Text = installed ? plan.Why : "whisper.cpp is not installed yet (Tools). " + plan.Why;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("What Auto-Captions run on could not be worked out", ex);
            CaptionAcceleratorText.Text = "";
        }
    }

    // The button turns into Stop while the test runs, and the four lists wait for its answer.
    private void SetBenchmarkRunning(bool running)
    {
        BenchmarkButtonText.Text = running ? "Stop the Test" : "Test & Auto-Tune Hardware";
        foreach (var box in new[] { CompositorGpuBox, DecodeGpuBox, BackgroundGpuBox, DecodeAdapterBox })
            box.IsEnabled = !running;
        ApplyRolesButton.IsEnabled = !running && CanApplyRoles(_benchmarkShown);
        SyncTestCard.BlockedBy = running ? "Waits for the hardware test" : null;
    }

    // ----- The Playback & A/V Sync Test (Pass 5.5.2-Release, the user's Q2) -----
    // The last result kept is shown; a new one is kept at once, in this screen's copy and in the settings in effect (saved), as a hardware
    // test's roles are, so it holds whether this screen is then saved or cancelled. The hardware test waits while it runs, and it for that.
    private void InitializeSyncTest()
    {
        SyncTestCard.ShowKept(_settings.LastPlaybackSyncTest);
        SyncTestCard.RunningChanged += () => BenchmarkButton.IsEnabled = !SyncTestCard.IsRunning;
        SyncTestCard.Completed += result => KeepSyncResult(_settings, result);
    }

    /// <summary>A Playback &amp; A/V Sync Test's result kept (the golden test calls it): in the screen's copy, and in the settings in effect, saved.</summary>
    internal static void KeepSyncResult(AppSettings screenCopy, PlaybackSyncResult result)
    {
        screenCopy.LastPlaybackSyncTest = result;
        var current = AppSettings.Current;
        current.LastPlaybackSyncTest = result;
        try
        {
            current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The Playback & A/V Sync Test's result could not be saved", ex);
        }
    }

    // A result on the stage, in the status line, and the reasons for its roles under the button.
    private void ShowBenchmarkResult(HardwareBenchmarkResult result, bool fresh)
    {
        _benchmarkShown = result;
        var stale = !HardwareBenchmarkService.IsCurrent(result);
        ApplyRolesButton.IsEnabled = _benchmarkRun is null && CanApplyRoles(result);
        StagePreview.ShowBenchmark(result, null, "", stale);

        var tested = DateTime.TryParse(result.MeasuredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";
        // 5.5A: the first start's quick check (decoding and the bus on the cards that hold a role) reads as what it is.
        BenchmarkStatusText.Text = result.Error.Length > 0 ? $"Stopped short ({result.Error}): the roles were left as they were."
            : fresh ? string.Create(CultureInfo.InvariantCulture, $"Done in {result.TotalSeconds:0.0} s: the four roles are set from it.")
            : stale ? "Last tested on other cards or drivers: test again."
            : result.Kind == HardwareBenchmarkResult.KindQuick ? QuickResultStatus(result)
            : tested.Length > 0 ? $"Last tested {tested}." : "Tested before.";

        // The roles' reasons, then (5.2-Arm64AndNpu) what decodes what, the shared memory, and the NPUs.
        var reasons = result.Recommended.Reasons.ToList();
        if (result.FfmpegFallbackCodecs.Count > 0)
            reasons.Add($"Decoded by FFmpeg on the processor (Media Foundation has no decoder for it here): {string.Join(", ", result.FfmpegFallbackCodecs)}.");
        foreach (var card in result.Adapters.Where(a => a.UnifiedMemory && a.SameCardAs < 0))
            reasons.Add($"{card.Name} shares the computer's memory: the frame cache starts at up to 2 GB of it, grows to at most 4 GB (an eighth of the computer's memory below 32 GB), and is emptied when a project tab is put behind.");
        foreach (var npu in result.Npus)
            reasons.Add($"NPU: {npu.Name}. {npu.UsedFor}");
        BenchmarkReasonsText.Text = string.Join(Environment.NewLine, reasons);
        BenchmarkReasonsText.Visibility = reasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BenchmarkStatusText.ToolTip = reasons.Count > 0 ? BenchmarkReasonsText.Text : null;
    }

    /// <summary>
    /// The status line of a first start's quick check kept (5.5A review): what it measured, from its numbers (decoding only when a card
    /// decoded the test clip), on the cards that hold a role (the Export card may be measured too). Pure: the goldens check it.
    /// </summary>
    internal static string QuickResultStatus(HardwareBenchmarkResult result) => HardwareBenchmarkService.QuickMeasured(result) is { Length: > 0 } measured
        ? $"A quick check at the first start ({measured} on the cards that hold a role): run the test for the full measurements."
        : "A quick check at the first start could not measure the cards: run the test for the full measurements.";
}
