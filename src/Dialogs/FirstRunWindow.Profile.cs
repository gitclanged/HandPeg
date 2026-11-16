using System.Globalization;
using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Role card model (shared with Settings via RoleCardsPanel): role, assigned device, reason, measurement badge ("Estimated",
/// "Measuring...", "fps · MB/s") and whether it is measured, UIA id, S-F tier pill and tooltip (absent when unmeasured; never on NPU),
/// and the checked menu value (GPU card index or "-1" Auto; NPU Auto-Captions accelerator).
/// </summary>
public sealed record FirstRunRoleCard(string Role, string Card, string Detail, string Badge, bool Measured, string AutomationId)
{
    public PerformanceTier? Tier { get; init; }

    public string TierTip { get; init; } = "";

    public string Value { get; init; } = "-1";
}

/// <summary>Manual role picks from first-run role cards; null keeps the recommendation.</summary>
public sealed record FirstRunRolePicks(int? Compositor = null, int? Decode = null, int? Background = null, int? Export = null, string? Npu = null)
{
    public bool Any => Compositor is not null || Decode is not null || Background is not null || Export is not null || Npu is not null;
}

// ----- Hardware profile: instant estimate, then full test -----
// Phase 1 (window construction): HandPeg.exe --hw-benchmark --estimate launched from a pool thread, keeping CreateProcess and all
// DXGI/DXCore/MF calls off the UI thread and out of process; cards populate on its first line, then MF-derived encoder estimates
// until FFmpeg answers.
// Phase 2 (after show, every open): full test (HardwareBenchmarkService.RunAsync, pool thread) streaming figures and tier pills while
// roles stay estimated until the result; without FFmpeg it measures bus and compositor, re-running with decode once FFmpeg is installed.
// The A/V Sync Test waits for it. Start Editing and close kill both workers. Roles are pickable per card (RoleCardsPanel menus,
// populated from the profile's own GPU list since this process does not enumerate GPUs).
public partial class FirstRunWindow
{
    private readonly CancellationTokenSource _profileCancellation = new();

    // Estimate (null if it failed), the profile driving roles (estimate or test result) and the one driving figures (test progress
    // while running, else the role profile). UI thread only.
    private Task<HardwareBenchmarkResult?> _estimate = Task.FromResult<HardwareBenchmarkResult?>(null);
    private HardwareBenchmarkResult? _profile, _figures;

    // Full test state: measuring, running (with/without FFmpeg decode), rerun pending, and whether the last result included decode.
    private bool _measuring, _fullRunning, _runHasFfmpeg, _fullAgain, _decoded;

    // Manual card picks, always written by Start Editing (Finish).
    private FirstRunRolePicks _picks = new();
    private bool _rolesChosen;

    // When reopened (Settings or missing engines), saved roles display where nothing is picked; display only (Start Editing writes
    // manual picks, and recommendations only on a first start).
    private readonly FirstRunRolePicks _saved = SavedRoles(AppSettings.Current);

    /// <summary>Manual role picks (read by goldens).</summary>
    internal FirstRunRolePicks Picks => _picks;

    /// <summary>
    /// Saved roles for display on reopen (pure, golden-tested): each GPU role's card (-1 Auto, Background -2 Processor Only) and the
    /// Auto-Captions accelerator. Empty on a first start, where recommendations show.
    /// </summary>
    internal static FirstRunRolePicks SavedRoles(AppSettings settings) => !settings.FirstRunComplete ? new()
        : new(settings.CompositorGpuAdapter, settings.DecodeGpuAdapter, settings.BackgroundGpu, settings.ExportGpuAdapter,
            AppSettings.CaptionAccelerators.Contains(settings.CaptionAccelerator) ? settings.CaptionAccelerator : null);

    /// <summary>Displayed roles (pure): manual picks, else saved roles (<see cref="SavedRoles"/>).</summary>
    internal static FirstRunRolePicks ShownPicks(FirstRunRolePicks picks, FirstRunRolePicks saved) => new(
        picks.Compositor ?? saved.Compositor, picks.Decode ?? saved.Decode, picks.Background ?? saved.Background, picks.Export ?? saved.Export, picks.Npu ?? saved.Npu);

    /// <summary>Phase 1: starts the estimate off the UI thread, showing cards as they arrive.</summary>
    private void StartProfile()
    {
        var progress = new UiProgress(this, step =>
        {
            if (step.Partial is { } partial)
                ShowProfile(partial);
        });
        var token = _profileCancellation.Token;
        _estimate = Task.Run(() => HardwareBenchmarkService.EstimateAsync(progress, token), CancellationToken.None);
        _ = ShowEstimateAsync();
    }

    private async Task ShowEstimateAsync()
    {
        var estimate = await _estimate;
        if (_closed)
            return;

        if (estimate is { Error.Length: 0 } && estimate.Adapters.Count > 0)
            ShowProfile(estimate);
        else if (_profile is null)
            ShowProfileWarning(estimate?.Error is { Length: > 0 } error ? error : "no graphics card was listed");
    }

    /// <summary>
    /// Warning above the roles (pure, golden-tested): the hardware test stopped, why, and that roles are estimated.
    /// </summary>
    internal static string ProfileWarning(string why) => $"Warning: the hardware test stopped ({why.Trim().TrimEnd('.')}); the roles shown are estimates.";

    /// <summary>Warning (not error) while Settings' test holds the single benchmark gate.</summary>
    internal const string BusyWarning = "Warning: the hardware test is running in Settings; the roles shown are estimates.";

    private void ShowProfileWarning(string? why) => ShowWarningLine(why is { Length: > 0 } ? ProfileWarning(why) : null);

    private void ShowWarningLine(string? text)
    {
        ProfileWarningText.Text = text ?? "";
        ProfileWarningText.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // Phase 2 after show: the full test, on every open.
    private void Profile_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Profile_Loaded;
        MeasureFully();
    }

    /// <summary>
    /// Phase 2: runs the full test after the estimate names roles, re-running if requested meanwhile (FFmpeg installed). Deferred while the
    /// Playback &amp; A/V Sync Test runs.
    /// </summary>
    private void MeasureFully() => Faults.Forget(MeasureFullyAsync(), nameof(MeasureFully));

    private async Task MeasureFullyAsync()
    {
        if (_closed)
            return;
        if (_fullRunning)
        {
            _fullAgain = true;
            return;
        }

        _fullRunning = true;
        try
        {
            do
            {
                _fullAgain = false;
                await _estimate;
                if (_closed)
                    return;
                if (SyncTestCard.IsRunning)
                {
                    MeasureAfterSyncTest();
                    return;
                }

                _runHasFfmpeg = File.Exists(DependencyUpdater.FfmpegPath);
                SetMeasuring(true);
                var progress = new UiProgress(this, step =>
                {
                    if (_measuring && step.Partial is { } partial)
                        ShowFigures(partial);
                });
                var token = _profileCancellation.Token;

                HardwareBenchmarkResult? result;
                try
                {
                    // Pool thread: CreateProcess, test clip generation and saving never block the UI thread.
                    result = await Task.Run(() => HardwareBenchmarkService.RunAsync(progress, token), CancellationToken.None);
                }
                finally
                {
                    SetMeasuring(false);
                }

                if (_closed)
                    return;

                if (result is { Error.Length: 0 } && result.Adapters.Count > 0)
                {
                    _decoded = result.Adapters.Any(a => a.HardwareDecodeFps > 0 || a.SoftwareDecodeFps > 0);
                    ShowProfileWarning(null);
                    ShowProfile(result);
                }
                else if (result is null && HardwareBenchmarkService.LastFailure == HardwareBenchmarkService.BusyFailure)
                {
                    ShowWarningLine(BusyWarning);
                }
                else
                {
                    var why = HardwareBenchmarkService.LastFailure is { Length: > 0 } said ? said : result?.Error is { Length: > 0 } error ? error : "it stopped short";
                    ShowProfileWarning(why);
                }
            }
            while (_fullAgain && !_closed);
        }
        finally
        {
            _fullRunning = false;
        }
    }

    // A/V Sync Test running: defer the full test (both would load the same GPUs).
    private void MeasureAfterSyncTest()
    {
        void Resume()
        {
            if (SyncTestCard.IsRunning)
                return;
            SyncTestCard.RunningChanged -= Resume;
            if (!_closed)
                MeasureFully();
        }

        SyncTestCard.RunningChanged += Resume;
    }

    /// <summary>
    /// On FFmpeg install (DependencyUpdater.Installed): re-runs a decode-less full test with decoding after the current run ends.
    /// </summary>
    private void MeasureAgainWithDecoding()
    {
        if (!(_fullRunning ? _runHasFfmpeg : _decoded))
            MeasureFully();
    }

    private void SetMeasuring(bool measuring)
    {
        _measuring = measuring;

        // The A/V Sync Test waits for the hardware test (both would load the same GPUs).
        SyncTestCard.BlockedBy = measuring ? "Waits for the hardware check" : null;
        // Measurement done: figures revert to the role profile (the result shown next, or the existing one if none).
        if (!measuring && _profile is { } profile)
            _figures = profile;
        if (!_closed)
            ShowCards();
    }

    // Profile kinds ordered least to most measured; a profile is never replaced by a lesser kind.
    private static int MeasuredRank(HardwareBenchmarkResult result) => result.Kind switch
    {
        HardwareBenchmarkResult.KindEstimate => 0,
        HardwareBenchmarkResult.KindQuick => 1,
        _ => 2,
    };

    /// <summary>
    /// Shows a profile: roles and figures on the cards, roles for Start Editing (first start), and estimated encoders (until FFmpeg
    /// answers). Ignores profiles less measured than the current one (e.g. a late estimate after the test result).
    /// </summary>
    internal void ShowProfile(HardwareBenchmarkResult result)
    {
        if (_profile is { } shown && MeasuredRank(result) < MeasuredRank(shown))
            return;

        (_profile, _figures) = (result, result);
        var usable = result.Error.Length == 0 && result.Adapters.Count > 0;
        _benchmarkRoles = usable ? result.Recommended : null;
        ShowCards();

        _estimatedHardware = usable ? EstimatedEncoders(result) : [];
        RefreshEncoders();
        ShowEstimatedEncoderStatus();
    }

    /// <summary>Test progress: updates figures and pills; roles stay until the result.</summary>
    private void ShowFigures(HardwareBenchmarkResult partial)
    {
        _figures = partial;
        ShowCards();
    }

    // Rebuilds role cards (roles, figures, picks), menu device lists and the System tier beside the title.
    private void ShowCards()
    {
        if ((_profile ?? _figures) is not { } roles)
            return;

        var figures = _figures ?? roles;
        RoleCards.Devices = [.. roles.Adapters.Where(a => a.SameCardAs < 0).Select(a => new RoleDevice(a.Index, a.Name))];
        RoleCards.HasNpu = roles.Npus.Count > 0;
        RoleCards.ItemsSource = RoleCardsOf(roles, figures, _measuring, ShownPicks(_picks, _saved));

        // System tier from final results only; progress figures arrive stage by stage.
        var system = !_measuring && _profile is { } profile && MeasuredRank(profile) > 0 ? PerformanceTiers.System(profile) : null;
        SystemTierBadge.Child = system is { } tier ? RoleCardsPanel.TierPill(tier, PerformanceTiers.SystemTip(_profile!, tier)) : null;
        SystemTierBadge.Visibility = system is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Card role pick (RoleCardsPanel.RoleChosen): stored for Start Editing and reflected on the cards.
    private void RoleCards_RoleChosen(string role, string value)
    {
        int? Card() => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index) ? index : null;
        _picks = role switch
        {
            HardwareRoles.Compositor => _picks with { Compositor = Card() },
            HardwareRoles.Decode => _picks with { Decode = Card() },
            HardwareRoles.Background => _picks with { Background = Card() },
            HardwareRoles.Export => _picks with { Export = Card() },
            HardwareRoles.Npu => _picks with { Npu = AppSettings.CaptionAccelerators.Contains(value) ? value : null },
            _ => _picks,
        };
        _rolesChosen = _picks.Any;
        ShowCards();
    }

    /// <summary>
    /// Writes manual picks into the settings saved by Start Editing: unknown cards become Auto; Background may be Processor Only;
    /// the NPU pick selects the Auto-Captions accelerator. Never assigns a card that was not explicitly picked.
    /// </summary>
    internal static void WritePicks(FirstRunRolePicks picks, AppSettings settings, IReadOnlyCollection<int> cardsHere)
    {
        int Known(int index) => index >= 0 && cardsHere.Contains(index) ? index : HardwareRoles.Auto;
        if (picks.Compositor is { } compositor)
            settings.CompositorGpuAdapter = Known(compositor);
        if (picks.Decode is { } decode)
            settings.DecodeGpuAdapter = Known(decode);
        if (picks.Background is { } background)
            settings.BackgroundGpu = background == HardwareRoles.ProcessorOnly ? HardwareRoles.ProcessorOnly : Known(background);
        if (picks.Export is { } export)
            settings.ExportGpuAdapter = Known(export);
        if (picks.Npu is { } npu)
            settings.CaptionAccelerator = npu;
    }

    /// <summary>Role cards for a profile with its own figures and no picks (pure, golden-tested).</summary>
    internal static IReadOnlyList<FirstRunRoleCard> RoleCardsOf(HardwareBenchmarkResult result, bool measuring) => RoleCardsOf(result, result, measuring, new FirstRunRolePicks());

    /// <summary>
    /// Builds role cards (pure): Compositor (the profiled card), Decode and Background (Auto = compositor GPU, never a secondary GPU),
    /// Export (Auto, or the stronger encoder) and NPU, overridden by <paramref name="picks"/>. Roles from <paramref name="roles"/>; figures
    /// and tier pills from <paramref name="figures"/>. Badge: measurement ("62 fps · 9,870 MB/s", bus-only before decode), else
    /// "Measuring..." during the test, else "Estimated".
    /// </summary>
    internal static IReadOnlyList<FirstRunRoleCard> RoleCardsOf(HardwareBenchmarkResult roles, HardwareBenchmarkResult figures, bool measuring, FirstRunRolePicks picks)
    {
        var cards = roles.Adapters.Where(a => a.SameCardAs < 0).ToList();
        HardwareBenchmarkAdapter? Listed(int index) => cards.FirstOrDefault(a => a.Index == index);
        HardwareBenchmarkAdapter? Measurement(HardwareBenchmarkAdapter? card) => card is null ? null : figures.Adapters.FirstOrDefault(a => a.Index == card.Index) ?? card;

        // Roles without a measured compositor (Auto) show the listed card with the most VRAM (where Auto resolves), marked Auto and
        // Not measured; never another unmeasured card.
        var auto = roles.Recommended.Compositor < 0;
        var recommended = Listed(roles.Recommended.Compositor) ?? cards.OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).FirstOrDefault();
        var compositor = picks.Compositor is >= 0 && Listed(picks.Compositor.Value) is { } pickedCompositor ? pickedCompositor : recommended;
        var compositorName = compositor?.Name ?? "No graphics card found";
        var exportRecommended = roles.Recommended.Export >= 0 ? Listed(roles.Recommended.Export) : null;
        var export = picks.Export is { } pickedExport ? pickedExport >= 0 ? Listed(pickedExport) : null : exportRecommended;

        string Badge(HardwareBenchmarkAdapter? listed)
        {
            var card = Measurement(listed);
            return card is null ? ""
                : RoleCardsPanel.Figures(card) is { Length: > 0 } shown ? shown
                : measuring && card.Note.Length == 0 ? "Measuring..."
                : card.Note.Length > 0 && figures.Kind != HardwareBenchmarkResult.KindEstimate ? "Not measured"
                : "Estimated";
        }

        bool Measured(HardwareBenchmarkAdapter? listed) => Measurement(listed) is { } card && (RoleCardsPanel.Figures(card).Length > 0 || card.CompositorMs > 0);
        FirstRunRoleCard Card(string role, string name, string detail, HardwareBenchmarkAdapter? holder, int tierCard, string id, int? picked)
        {
            // Processor Only uses the CPU tier (as Settings does); a saved role on a missing card shows Auto so one menu item stays checked.
            var (tier, tip) = PerformanceTiers.ForRole(figures, role, tierCard);
            var value = picked is >= 0 && Listed(picked.Value) is null ? HardwareRoles.Auto : picked ?? HardwareRoles.Auto;
            return new(role, name, detail, Badge(holder), Measured(holder) || tier is not null && tierCard == HardwareRoles.ProcessorOnly, id)
            {
                Tier = tier, TierTip = tip, Value = value.ToString(CultureInfo.InvariantCulture),
            };
        }

        var memory = compositor is null ? "Auto: the card Windows calls high-performance"
            : compositor.UnifiedMemory ? "Shares the computer's memory · the player, its effects and shaders"
            : string.Create(CultureInfo.InvariantCulture, $"{compositor.DedicatedVideoMemoryMb / 1024.0:0.#} GB of video memory · the player, its effects and shaders");
        var compositorCard = picks.Compositor is >= 0 || !auto || compositor is null ? compositorName : $"Auto · {compositorName}";

        var decode = picks.Decode is >= 0 && Listed(picks.Decode.Value) is { } pickedDecode ? pickedDecode : null;
        var decodeCard = decode is null ? $"Auto · {compositorName}" : decode.Name;
        var decodeDetail = decode is null || decode == compositor
            ? "Timeline Clips decode on the compositor's card: zero-copy, nothing crosses the bus"
            : "Timeline Clips decode here, each frame copied to the compositor's card";

        var processorOnly = picks.Background == HardwareRoles.ProcessorOnly;
        var background = picks.Background is >= 0 && Listed(picks.Background.Value) is { } pickedBackground ? pickedBackground : null;
        var backgroundCard = processorOnly ? "Processor Only" : background is null ? $"Auto · {compositorName}" : background.Name;
        var backgroundDetail = processorOnly ? "Thumbnails, waveforms and proxies, on the processor"
            : background is null || background == compositor ? "Thumbnails, waveforms and proxies, on the compositor's card"
            : "Thumbnails, waveforms and proxies, on this card";

        var encoding = export ?? compositor;
        var codecs = encoding is null || encoding.EncodeCodecs.Count == 0
            ? "No hardware encoder listed: exports use the software encoders"
            : string.Join(" · ", new[] { "AV1", "HEVC", "H.264" }.Where(encoding.EncodeCodecs.Contains)) + " in hardware";
        var exportCard = export is null ? $"Auto · {compositorName}" : export.Name;

        var npu = roles.Npus.FirstOrDefault();
        var npuDetail = picks.Npu switch
        {
            AppSettings.SpeechOnNpu => "Auto-Captions on the NPU, once a speech build for it is here",
            AppSettings.SpeechOnGpu => "Auto-Captions on the graphics card",
            AppSettings.SpeechOnCpu => "Auto-Captions on the processor",
            _ => npu is null ? "Auto-Captions run on the graphics card or the processor" : "Detected, not used yet: Auto-Captions on the NPU are planned with the AI worker",
        };

        // With no pick, Compositor and Export menus check the recommended card, so selecting Auto is an explicit change and selecting the
        // shown card is a no-op.
        return
        [
            Card(HardwareRoles.Compositor, compositorCard, memory, compositor, compositor?.Index ?? HardwareRoles.Auto, "CompositorRole", picks.Compositor ?? (auto ? null : recommended?.Index)),
            Card(HardwareRoles.Decode, decodeCard, decodeDetail, decode ?? compositor, (decode ?? compositor)?.Index ?? HardwareRoles.Auto, "DecodeRole", picks.Decode),
            Card(HardwareRoles.Background, backgroundCard, backgroundDetail, processorOnly ? null : background ?? compositor,
                processorOnly ? HardwareRoles.ProcessorOnly : (background ?? compositor)?.Index ?? HardwareRoles.Auto, "BackgroundRole", picks.Background),
            Card(HardwareRoles.Export, exportCard, codecs, encoding, encoding?.Index ?? HardwareRoles.Auto, "ExportRole", picks.Export ?? exportRecommended?.Index),
            new(HardwareRoles.Npu, npu?.Name ?? "No NPU found", npuDetail, npu is null ? "" : "Detected", false, "NpuRole") { Value = picks.Npu ?? AppSettings.SpeechOnAuto },
        ];
    }

    // ----- Encoder estimates from GPUs -----

    // Best hardware encoder: the first available H.264 entry, Export GPU family first.
    private static readonly string[] PreferredHardware = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"];

    /// <summary>Encoder family by GPU vendor: NVENC (NVIDIA), QSV (Intel), AMF (AMD), else MF (e.g. Qualcomm Adreno).</summary>
    internal static string EncoderSuffix(uint vendorId) => vendorId switch
    {
        0x10DE => "_nvenc",
        0x8086 => "_qsv",
        0x1002 => "_amf",
        _ => "_mf",
    };

    /// <summary>
    /// Pre-FFmpeg encoder estimate: per GPU, its vendor encoder for each codec MF reports hardware support for (H.264, HEVC, AV1), in
    /// probe order (vendor encoders first); MF only for codecs no vendor encoder covers (EncoderProber.CoveringVendorEncoder).
    /// </summary>
    internal static IReadOnlyList<string> EstimatedEncoders(HardwareBenchmarkResult result)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in result.Adapters.Where(a => a.SameCardAs < 0))
        {
            foreach (var (codec, prefix) in new[] { ("H.264", "h264"), ("HEVC", "hevc"), ("AV1", "av1") })
            {
                if (card.EncodeCodecs.Contains(codec))
                    names.Add(prefix + EncoderSuffix(card.VendorId));
            }
        }

        var vendors = EncoderProber.HardwareEncoders.Where(n => !MediaFoundationEncoder.Is(n) && names.Contains(n)).ToList();
        var mediaFoundation = EncoderProber.HardwareEncoders.Where(n => MediaFoundationEncoder.Is(n) && names.Contains(n) && EncoderProber.CoveringVendorEncoder(n, vendors) is null);
        return [.. vendors, .. mediaFoundation];
    }

    /// <summary>Encoder family of the Export GPU (compositor GPU when Auto); null without a profile or card.</summary>
    internal static string? ExportEncoderSuffix(HardwareBenchmarkResult? profile)
    {
        if (profile is null || profile.Adapters.Count == 0)
            return null;

        var index = profile.Recommended.Export >= 0 ? profile.Recommended.Export : profile.Recommended.Compositor;
        var card = profile.Adapters.FirstOrDefault(a => a.Index == index && a.SameCardAs < 0);
        return card is null ? null : EncoderSuffix(card.VendorId);
    }

    /// <summary>
    /// Ordered hardware encoders and the default pick: Export GPU family first, then NVENC, QSV, AMF, MF; default is the first available
    /// H.264 entry, else libx264.
    /// </summary>
    internal static (IReadOnlyList<string> Ordered, string Best) RankEncoders(IReadOnlyList<string> hardware, string? preferredSuffix)
    {
        static int Vendor(string name) => name.EndsWith("_nvenc", StringComparison.Ordinal) ? 0 : name.EndsWith("_qsv", StringComparison.Ordinal) ? 1
            : MediaFoundationEncoder.Is(name) ? 3 : 2;
        int Rank(string name) => (preferredSuffix is not null && name.EndsWith(preferredSuffix, StringComparison.Ordinal) ? 0 : 4) + Vendor(name);

        var ordered = hardware.OrderBy(Rank).ToList();
        var best = PreferredHardware.OrderBy(Rank).FirstOrDefault(hardware.Contains) ?? Models.EncoderOption.Software[0].Name;
        return (ordered, best);
    }

    // With a Qualcomm Adreno present, MF encoders are labelled with it ("H.264 (Qualcomm Adreno)").
    private static bool HasQualcommAdreno(HardwareBenchmarkResult? profile) => profile?.Adapters.Any(a => a.Vendor == "Qualcomm") ?? false;

    /// <summary>IProgress that marshals to the window's dispatcher regardless of the reporting thread or creation context.</summary>
    private sealed class UiProgress(FirstRunWindow window, Action<HardwareBenchmarkProgress> show) : IProgress<HardwareBenchmarkProgress>
    {
        public void Report(HardwareBenchmarkProgress value) => window.Dispatcher.BeginInvoke(() =>
        {
            if (!window._closed)
                show(value);
        });
    }
}
