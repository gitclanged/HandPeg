using System.Globalization;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Export & Hardware > GPU & NPU Roles: Compositor and Decode lists (Background and Export are filled with the Performance
// lists in SettingsWindow.xaml.cs), Test & Auto-Tune Hardware with live stage figures (SettingsStagePreview.ShowBenchmark), NPU list
// (enumerated off the UI thread after show), Auto-Captions accelerator (AppSettings.CaptionAccelerator), and stage role cards
// (RoleCardsPanel: pickable roles, S-F tier pills, System tier, last A/V sync result).
public partial class SettingsWindow
{
    // Benchmark run started here; pressing the button again cancels it.
    private CancellationTokenSource? _benchmarkRun;

    // Result shown when idle: last saved, or this screen's run.
    private HardwareBenchmarkResult? _benchmarkShown;

    // Export GPU Auto label (distinct from GpuAdapter.Automatic's legacy "Automatic" meaning FFmpeg's choice).
    private static readonly GpuAdapter ExportGpuAuto = new(-1, "Auto (the Compositor GPU)");

    // Stage role card inputs: listed GPUs (first listings), first NPU name, and live test progress.
    private IReadOnlyList<GpuAdapter> _stageCards = [];
    private string? _npuName;
    private HardwareBenchmarkResult? _benchmarkPartial;

    private void InitializeGpuRoles(IReadOnlyList<GpuAdapter> adapters)
    {
        // Roles (including Export) on a removed GPU revert to Auto.
        if (adapters.All(a => a.Index != _settings.CompositorGpuAdapter))
            _settings.CompositorGpuAdapter = -1;
        if (adapters.All(a => a.Index != _settings.DecodeGpuAdapter))
            _settings.DecodeGpuAdapter = -1;
        if (adapters.All(a => a.Index != _settings.HardwareDecodeAdapter))
            _settings.HardwareDecodeAdapter = -1;

        // GPUs DXGI lists twice (e.g. a headset display on it) appear once as the first listing; roles saved on a duplicate map to the
        // first's index. Background and Export lists also use DistinctCards.
        _settings.CompositorGpuAdapter = TwinOf(adapters, _settings.CompositorGpuAdapter);
        _settings.DecodeGpuAdapter = TwinOf(adapters, _settings.DecodeGpuAdapter);
        _settings.BackgroundGpu = TwinOf(adapters, _settings.BackgroundGpu);
        _settings.HardwareDecodeAdapter = TwinOf(adapters, _settings.HardwareDecodeAdapter);
        var distinct = DistinctCards(adapters);

        // Export GPU: export compositing adapter (GpuExport.ExportAdapter); Auto = Compositor GPU.
        DecodeAdapterBox.ItemsSource = distinct.Prepend(ExportGpuAuto).ToList();
        DecodeAdapterBox.SelectedValue = _settings.HardwareDecodeAdapter;

        var cards = distinct.Select(a => new NumberChoice(a.Display, a.Index)).ToList();
        CompositorGpuBox.ItemsSource = cards.Prepend(new NumberChoice("Auto (the high-performance card)", -1)).ToList();
        DecodeGpuBox.ItemsSource = cards.Prepend(new NumberChoice("Auto (the compositor's card, zero-copy)", -1)).ToList();
        CompositorGpuBox.SelectedValue = _settings.CompositorGpuAdapter;
        DecodeGpuBox.SelectedValue = _settings.DecodeGpuAdapter;

        // Auto-Captions accelerator choice, and (after show) its effective backend for the installed whisper build.
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

        // Stage role cards (RoleCardsPanel, as in first run) over the same GPUs; card picks write the copy and sync the lists, and list
        // edits redraw the cards.
        _stageCards = distinct;
        StageRoleCards.Devices = [.. distinct.Select(a => new RoleDevice(a.Index, a.Name))];
        StageRoleCards.RoleChosen += StageRole_Chosen;
        StageSyncCheck.Foreground = RoleCardsPanel.MeasuredBrush;
        foreach (var box in new[] { CompositorGpuBox, DecodeGpuBox, BackgroundGpuBox, DecodeAdapterBox, CaptionAcceleratorBox })
            box.SelectionChanged += (_, _) => ShowStageRoles();

        // After show, load the last result (small file) and enumerate NPUs (DXCore, once per session) off the UI thread.
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

        // Closing cancels the run; nothing is half-applied.
        Closed += (_, _) => _benchmarkRun?.Cancel();
        InitializeSyncTest();
    }

    private void Benchmark_Click(object sender, RoutedEventArgs e) => Faults.Forget(Benchmark_ClickAsync(sender, e), nameof(Benchmark_Click));

    private async Task Benchmark_ClickAsync(object sender, RoutedEventArgs e)
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

        // Progress posts to the UI thread (its creation context); reports after the run ends are dropped.
        var progress = new Progress<HardwareBenchmarkProgress>(step =>
        {
            if (!ReferenceEquals(_benchmarkRun, run))
                return;

            var fraction = Math.Clamp(step.Fraction, 0, 1);
            BenchmarkStatusText.Text = step.Message;
            StagePreview.ShowBenchmark(step.Partial ?? _benchmarkShown, fraction,
                string.Create(CultureInfo.InvariantCulture, $"{step.Message}  {fraction * 100:0}%"), stale: false);

            // Stream figures and tier pills to the stage cards.
            if (step.Partial is { } partial)
            {
                _benchmarkPartial = partial;
                ShowStageRoles();
            }
        });

        HardwareBenchmarkResult? result;
        try
        {
            result = await HardwareBenchmarkService.RunAsync(progress, run.Token);
        }
        finally
        {
            _benchmarkRun = null;
            _benchmarkPartial = null;
            SetBenchmarkRunning(false);

            // Stopped/failed run: clear partial figures and pills and restore the kept result's System tier.
            ShowStageRoles();
        }

        if (result is null)
        {
            BenchmarkStatusText.Text = HardwareBenchmarkService.LastFailure is { Length: > 0 } why ? why : "The test could not be run.";
            StagePreview.ShowBenchmark(_benchmarkShown, null, "", _benchmarkShown is { } shown && !HardwareBenchmarkService.IsCurrent(shown));
            return;
        }

        // A completed run sets all four roles in both the copy and the live settings (saved immediately), so they persist even on
        // Cancel; an incomplete run changes nothing.
        if (CanApplyRoles(result))
            SetRecommendedRoles(result);

        ShowBenchmarkResult(result, fresh: true);
    }

    /// <summary>Whether a result's roles are applicable (Apply Recommended Roles; pure): completed, named GPUs, matching current hardware.</summary>
    internal static bool CanApplyRoles(HardwareBenchmarkResult? result) =>
        result is { Error.Length: 0, Adapters.Count: > 0 } && HardwareBenchmarkService.IsCurrent(result);

    // Applies a result's roles to the copy and the live settings (saved immediately), as a completed test does.
    private void SetRecommendedRoles(HardwareBenchmarkResult result)
    {
        HardwareBenchmarkService.WriteRoles(result.Recommended, _settings);
        HardwareBenchmarkService.ApplyRoles(result.Recommended);
        CompositorGpuBox.SelectedValue = _settings.CompositorGpuAdapter;
        DecodeGpuBox.SelectedValue = _settings.DecodeGpuAdapter;
        BackgroundGpuBox.SelectedValue = _settings.BackgroundGpu;
        DecodeAdapterBox.SelectedValue = _settings.HardwareDecodeAdapter;
        ShowStageRoles();
    }

    // Apply Recommended Roles: re-applies the kept result's roles (e.g. after manual changes).
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
    /// Physical GPUs for role lists: each once, as its first DXGI listing, deduplicated by the benchmark's rule (SameCardAs: same
    /// vendor and device) so lists and benchmark roles agree.
    /// </summary>
    internal static List<GpuAdapter> DistinctCards(IReadOnlyList<GpuAdapter> adapters) =>
        adapters.Where(a => TwinOf(adapters, a.Index) == a.Index).ToList();

    /// <summary>
    /// Maps <paramref name="index"/> to its card's first listing index; unchanged for unique cards and sentinels (Auto -1, Processor
    /// Only -2, removed cards).
    /// </summary>
    internal static int TwinOf(IReadOnlyList<GpuAdapter> adapters, int index)
    {
        var card = adapters.FirstOrDefault(a => a.Index == index);
        return card is null ? index : adapters.First(a => (a.VendorId, a.DeviceId) == (card.VendorId, card.DeviceId)).Index;
    }

    // Detected NPUs and their current use.
    private void ShowNpus(IReadOnlyList<NpuAdapter> npus)
    {
        (_npuName, StageRoleCards.HasNpu) = (npus.FirstOrDefault()?.Name, npus.Count > 0);
        ShowStageRoles();
        NpuText.Text = npus.Count == 0
            ? "None found (an NPU's driver lists it to Windows: a Snapdragon X, an Intel Core Ultra or an AMD Ryzen AI laptop has one)."
            : string.Join(Environment.NewLine, npus.Select(NpuAdapters.Describe)) + Environment.NewLine + NpuAdapters.UsedFor;
    }

    // One-line effective Auto-Captions backend for the shown choice (file checks on a pool thread).
    private void ShowSpeechPlan() => Faults.Forget(ShowSpeechPlanAsync(), nameof(ShowSpeechPlan));

    private async Task ShowSpeechPlanAsync()
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

    // During the test the button becomes Stop and the four role lists are disabled.
    private void SetBenchmarkRunning(bool running)
    {
        BenchmarkButtonText.Text = running ? "Stop the Test" : "Test & Auto-Tune Hardware";
        foreach (var box in new[] { CompositorGpuBox, DecodeGpuBox, BackgroundGpuBox, DecodeAdapterBox })
            box.IsEnabled = !running;
        StageRoleCards.IsEnabled = !running;
        ApplyRolesButton.IsEnabled = !running && CanApplyRoles(_benchmarkShown);
        SyncTestCard.BlockedBy = running ? "Waits for the hardware test" : null;
    }

    // ----- Playback & A/V Sync Test -----
    // Shows the last kept result; a new result is written to the copy and live settings immediately (persists on Cancel). It and the
    // hardware test are mutually exclusive.
    private void InitializeSyncTest()
    {
        SyncTestCard.ShowKept(_settings.LastPlaybackSyncTest);
        SyncTestCard.RunningChanged += () => BenchmarkButton.IsEnabled = !SyncTestCard.IsRunning;
        SyncTestCard.Completed += result =>
        {
            KeepSyncResult(_settings, result);
            ShowStageRoles();
        };
    }

    /// <summary>Stores an A/V Sync Test result in the copy and live settings (saved); called by goldens.</summary>
    internal static void KeepSyncResult(AppSettings screenCopy, PlaybackSyncResult result)
    {
        screenCopy.LastPlaybackSyncTest = result;
        try
        {
            AppSettings.Update(current => current.LastPlaybackSyncTest = result, saveNow: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The Playback & A/V Sync Test's result could not be saved", ex);
        }
    }

    // Shows a result on the stage, status line and role reasons under the button.
    private void ShowBenchmarkResult(HardwareBenchmarkResult result, bool fresh)
    {
        _benchmarkShown = result;
        var stale = !HardwareBenchmarkService.IsCurrent(result);
        ShowStageRoles();
        ApplyRolesButton.IsEnabled = _benchmarkRun is null && CanApplyRoles(result);
        StagePreview.ShowBenchmark(result, null, "", stale);

        var tested = DateTime.TryParse(result.MeasuredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";
        // Label first-start quick checks (decode + bus on role GPUs) as such.
        BenchmarkStatusText.Text = result.Error.Length > 0 ? $"Stopped short ({result.Error}): the roles were left as they were."
            : fresh ? string.Create(CultureInfo.InvariantCulture, $"Done in {result.TotalSeconds:0.0} s: the four roles are set from it.")
            : stale ? "Last tested on other cards or drivers: test again."
            : result.Kind == HardwareBenchmarkResult.KindQuick ? QuickResultStatus(result)
            : tested.Length > 0 ? $"Last tested {tested}." : "Tested before.";

        // Role reasons, then decode mapping, shared memory and NPUs.
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

    // ----- Stage role cards (RoleCardsPanel above the stage picture; Export & Hardware only) -----

    /// <summary>Refreshes stage role cards, System tier and sync line from the copy and the shown result.</summary>
    private void ShowStageRoles()
    {
        if (_stageKind != StageKind.Hardware)
            return;

        // No figures from results for other GPUs/drivers; live progress supplies them during a run.
        var result = _benchmarkPartial ?? (_benchmarkShown is { } shown && HardwareBenchmarkService.IsCurrent(shown) ? shown : null);
        StageRoleCards.ItemsSource = StageRoleCardsOf(_settings, _stageCards, _npuName, result, GpuAdapters.CompositorIndex);
        var system = _benchmarkPartial is null && result is not null ? PerformanceTiers.System(result) : null;
        StageSystemTier.Child = system is { } tier ? RoleCardsPanel.TierPill(tier, PerformanceTiers.SystemTip(result!, tier)) : null;
        StageSystemTier.Visibility = system is null ? Visibility.Collapsed : Visibility.Visible;

        // Last A/V sync result, read-only here (the test lives in the GPU & NPU Roles section).
        var sync = _settings.LastPlaybackSyncTest;
        StageSyncText.Text = sync is null ? "Sync test not run" : PlaybackSyncTest.Summary(sync);
        StageSyncText.ToolTip = sync is null ? null : PlaybackSyncTest.Detail(sync);
        StageSyncCheck.Visibility = sync is { Passed: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    // Stage card pick: write to the copy as the list would and sync the list (refused during a test).
    private void StageRole_Chosen(string role, string value)
    {
        if (_benchmarkRun is not null)
            return;

        if (role == HardwareRoles.Npu)
        {
            if (AppSettings.CaptionAccelerators.Contains(value))
                (_settings.CaptionAccelerator, CaptionAcceleratorBox.SelectedValue) = (value, value);
        }
        else if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var card))
        {
            switch (role)
            {
                case HardwareRoles.Compositor:
                    (_settings.CompositorGpuAdapter, CompositorGpuBox.SelectedValue) = (card, card);
                    break;
                case HardwareRoles.Decode:
                    (_settings.DecodeGpuAdapter, DecodeGpuBox.SelectedValue) = (card, card);
                    break;
                case HardwareRoles.Background:
                    (_settings.BackgroundGpu, BackgroundGpuBox.SelectedValue) = (card, card);
                    break;
                case HardwareRoles.Export:
                    (_settings.HardwareDecodeAdapter, DecodeAdapterBox.SelectedValue) = (card, card);
                    break;
            }
        }

        ShowStageRoles();
        RefreshStageSoon();
    }

    /// <summary>
    /// Builds stage role cards (pure, golden-tested) from the copy: listed GPU names (Auto resolves to the running compositor GPU when
    /// known), figures and tier pills from a matching result ("Not tested" otherwise), and the NPU Auto-Captions choice.
    /// </summary>
    internal static IReadOnlyList<FirstRunRoleCard> StageRoleCardsOf(AppSettings settings, IReadOnlyList<GpuAdapter> cards, string? npu,
        HardwareBenchmarkResult? result, int runningCompositor)
    {
        GpuAdapter? Listed(int index) => index >= 0 ? cards.FirstOrDefault(a => a.Index == index) : null;
        var compositor = Listed(settings.CompositorGpuAdapter) ?? Listed(runningCompositor)
                         ?? (result is not null && PerformanceTiers.CompositorCard(result) is { } measured ? Listed(measured.Index) : null)
                         ?? cards.OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).FirstOrDefault();
        var compositorName = compositor?.Name ?? "No graphics card found";
        string Named(int value) => value == HardwareRoles.ProcessorOnly ? "Processor Only" : Listed(value)?.Name ?? $"Auto · {compositorName}";

        FirstRunRoleCard Card(string role, string detail, GpuAdapter? holder, int value, string id)
        {
            var tierCard = value == HardwareRoles.ProcessorOnly ? value : holder?.Index ?? HardwareRoles.Auto;
            var (tier, tip) = PerformanceTiers.ForRole(result, role, tierCard);
            var measured = holder is null || result is null ? null : PerformanceTiers.Card(result, holder.Index);
            var figures = measured is null ? "" : RoleCardsPanel.Figures(measured);
            var badge = figures.Length > 0 ? figures : result is null ? "Not tested" : tier is null ? "Not measured" : "";
            var isMeasured = figures.Length > 0 || measured is { CompositorMs: > 0 } || tier is not null && value == HardwareRoles.ProcessorOnly;
            return new(role, Named(value), detail, badge, isMeasured, id)
            {
                Tier = tier, TierTip = tip, Value = value.ToString(CultureInfo.InvariantCulture),
            };
        }

        var decode = Listed(settings.DecodeGpuAdapter);
        var background = Listed(settings.BackgroundGpu);
        var processorOnly = settings.BackgroundGpu == HardwareRoles.ProcessorOnly;
        var export = Listed(settings.HardwareDecodeAdapter);
        var encoding = export ?? compositor;
        var encoders = encoding is null || result is null ? null : PerformanceTiers.Card(result, encoding.Index)?.EncodeCodecs;
        var codecs = encoders is null ? "Exports composite on this card"
            : encoders.Count == 0 ? "No hardware encoder listed: exports use the software encoders"
            : string.Join(" · ", new[] { "AV1", "HEVC", "H.264" }.Where(encoders.Contains)) + " in hardware";
        var accelerator = AppSettings.CaptionAccelerators.Contains(settings.CaptionAccelerator) ? settings.CaptionAccelerator : AppSettings.SpeechOnAuto;
        var npuDetail = accelerator switch
        {
            AppSettings.SpeechOnNpu => "Auto-Captions on the NPU, once a speech build for it is here",
            AppSettings.SpeechOnGpu => "Auto-Captions on the graphics card",
            AppSettings.SpeechOnCpu => "Auto-Captions on the processor",
            _ => npu is null ? "Auto-Captions run on the graphics card or the processor" : "Detected, not used yet: Auto-Captions on the NPU are planned with the AI worker",
        };

        return
        [
            Card(HardwareRoles.Compositor, "The player, its effects and shaders", compositor, settings.CompositorGpuAdapter, "CompositorRole"),
            Card(HardwareRoles.Decode, decode is null || decode == compositor
                ? "Timeline Clips decode on the compositor's card: zero-copy, nothing crosses the bus"
                : "Timeline Clips decode here, each frame copied to the compositor's card", decode ?? compositor, settings.DecodeGpuAdapter, "DecodeRole"),
            Card(HardwareRoles.Background, processorOnly ? "Thumbnails, waveforms and proxies, on the processor"
                : background is null || background == compositor ? "Thumbnails, waveforms and proxies, on the compositor's card"
                : "Thumbnails, waveforms and proxies, on this card", processorOnly ? null : background ?? compositor, settings.BackgroundGpu, "BackgroundRole"),
            Card(HardwareRoles.Export, codecs, encoding, settings.HardwareDecodeAdapter, "ExportRole"),
            new(HardwareRoles.Npu, npu ?? "No NPU found", npuDetail, npu is null ? "" : "Detected", false, "NpuRole") { Value = accelerator },
        ];
    }

    /// <summary>
    /// Status line for a kept first-start quick check (pure, golden-tested): what was measured on role GPUs (decode only when the test
    /// clip decoded; the Export GPU may be included).
    /// </summary>
    internal static string QuickResultStatus(HardwareBenchmarkResult result) => HardwareBenchmarkService.QuickMeasured(result) is { Length: > 0 } measured
        ? $"A quick check at the first start ({measured} on the cards that hold a role): run the test for the full measurements."
        : "A quick check at the first start could not measure the cards: run the test for the full measurements.";
}
