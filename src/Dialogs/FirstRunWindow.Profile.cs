using System.Globalization;
using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// One role card (5.5A; shared with Settings since 5.6A, RoleCardsPanel): the role, the card that holds it, why, the badge of its
/// measurement ("Estimated", "Measuring...", "<fps> fps · <MB/s> MB/s"), whether that badge is a measurement, and its UI Automation id.
/// 5.6A: its S-F tier pill and the pill's tooltip (none where nothing was measured, and never on the NPU), and the value its menu shows
/// checked (a GPU role's card number, "-1" Auto; the NPU's Auto-Captions accelerator).
/// </summary>
public sealed record FirstRunRoleCard(string Role, string Card, string Detail, string Badge, bool Measured, string AutomationId)
{
    public PerformanceTier? Tier { get; init; }

    public string TierTip { get; init; } = "";

    public string Value { get; init; } = "-1";
}

/// <summary>The roles picked by hand on the first-run window's role cards (5.6A): null where the recommendation stands.</summary>
public sealed record FirstRunRolePicks(int? Compositor = null, int? Decode = null, int? Background = null, int? Export = null, string? Npu = null)
{
    public bool Any => Compositor is not null || Decode is not null || Background is not null || Export is not null || Npu is not null;
}

// ----- The hardware profile: an instant estimate, then the full test (Pass 5.5A; the full test 5.6A) -----
//
// Phase 1, as the window is made: HandPeg.exe --hw-benchmark --estimate, started from a pool thread (Task.Run: CreateProcess and every
// DXGI, DXCore and Media Foundation call stay off the UI thread and out of this process), its cards on the role cards the moment its
// first line comes, then the estimated hardware encoders (from the Media Foundation encoders each card has) until FFmpeg's own answer.
// Phase 2 (5.6A, the user's call), once the window is up, on a first start and opened from Settings alike: the full test
// (HardwareBenchmarkService.RunAsync, started from a pool thread), its progress feeding the cards' figures and tier pills while the roles
// stay the estimate's until its result; with no FFmpeg yet it measures the bus and the compositor, and runs once more with the decoding
// when FFmpeg is installed. While it runs the Playback & A/V Sync Test waits. Start Editing and closing kill both workers. The roles
// can be picked by hand on the cards (RoleCardsPanel's menus, from the profile's own cards: nothing lists them in this process).
public partial class FirstRunWindow
{
    private readonly CancellationTokenSource _profileCancellation = new();

    // The estimate, as it is running or as it ended (null: it could not be made); the profile whose roles are shown (the estimate, or
    // the test's result) and the one whose figures are (the test's progress while it runs, else the profile): UI thread only.
    private Task<HardwareBenchmarkResult?> _estimate = Task.FromResult<HardwareBenchmarkResult?>(null);
    private HardwareBenchmarkResult? _profile, _figures;

    // The full test: whether it is measuring, running (and whether its run had an FFmpeg for the decoding), to run again when it ends,
    // and whether the last result decoded the test clip.
    private bool _measuring, _fullRunning, _runHasFfmpeg, _fullAgain, _decoded;

    // The roles picked by hand on the cards (5.6A): written by Start Editing whatever the start (Finish).
    private FirstRunRolePicks _picks = new();
    private bool _rolesChosen;

    // 5.6A review: opened again (from Settings, or with the engines missing), the roles the settings hold, shown where nothing was picked
    // by hand. Display only: Start Editing writes the hand picks alone, and the recommended roles only on a first start (Finish).
    private readonly FirstRunRolePicks _saved = SavedRoles(AppSettings.Current);

    /// <summary>The roles picked by hand (the goldens read it).</summary>
    internal FirstRunRolePicks Picks => _picks;

    /// <summary>
    /// The roles the settings hold, as the cards show them when the window is opened again (5.6A review; pure: the goldens check it):
    /// every GPU role's card (-1 Auto, Background -2 Processor Only) and the Auto-Captions accelerator. Nothing on a first start, where
    /// the cards show the recommendation Start Editing writes.
    /// </summary>
    internal static FirstRunRolePicks SavedRoles(AppSettings settings) => !settings.FirstRunComplete ? new()
        : new(settings.CompositorGpuAdapter, settings.DecodeGpuAdapter, settings.BackgroundGpu, settings.ExportGpuAdapter,
            AppSettings.CaptionAccelerators.Contains(settings.CaptionAccelerator) ? settings.CaptionAccelerator : null);

    /// <summary>The roles the cards show (5.6A review; pure): each hand pick, else the role the settings hold (<see cref="SavedRoles"/>).</summary>
    internal static FirstRunRolePicks ShownPicks(FirstRunRolePicks picks, FirstRunRolePicks saved) => new(
        picks.Compositor ?? saved.Compositor, picks.Decode ?? saved.Decode, picks.Background ?? saved.Background, picks.Export ?? saved.Export, picks.Npu ?? saved.Npu);

    /// <summary>Phase 1: the estimate, started off the UI thread; its cards shown as soon as they come.</summary>
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
    /// The warning line over the roles (5.5.0, the user's Q10 b; 5.6A the full test's; pure: the goldens check it): the hardware test
    /// stopped, why, and that the roles shown are the estimate's.
    /// </summary>
    internal static string ProfileWarning(string why) => $"Warning: the hardware test stopped ({why.Trim().TrimEnd('.')}); the roles shown are estimates.";

    /// <summary>The warning line while Settings' own test holds the one benchmark gate (5.6A): a warning, never an error.</summary>
    internal const string BusyWarning = "Warning: the hardware test is running in Settings; the roles shown are estimates.";

    private void ShowProfileWarning(string? why) => ShowWarningLine(why is { Length: > 0 } ? ProfileWarning(why) : null);

    private void ShowWarningLine(string? text)
    {
        ProfileWarningText.Text = text ?? "";
        ProfileWarningText.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // Phase 2, once the window is up: the full test, on a first start and opened from Settings alike (5.6A).
    private void Profile_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Profile_Loaded;
        MeasureFully();
    }

    /// <summary>
    /// Phase 2: the full test (5.6A), once the estimate has named the roles; again when asked meanwhile (FFmpeg installed). Never while the
    /// Playback &amp; A/V Sync Test runs: it starts once that ends.
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
                    // From a pool thread: the worker's CreateProcess, the test clip and the saving never wait on this window's thread.
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

    // The Playback & A/V Sync Test is running: the full test starts once it ends (both would measure the same cards at once).
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
    /// FFmpeg is here now (installed: DependencyUpdater.Installed): a full test that could not decode (no test clip without FFmpeg) is run
    /// once more with the decoding, after the one running ends.
    /// </summary>
    private void MeasureAgainWithDecoding()
    {
        if (!(_fullRunning ? _runHasFfmpeg : _decoded))
            MeasureFully();
    }

    private void SetMeasuring(bool measuring)
    {
        _measuring = measuring;

        // 5.5.2: the Playback & A/V Sync Test waits for the hardware test (both would measure the same cards at once).
        SyncTestCard.BlockedBy = measuring ? "Waits for the hardware check" : null;
        // Done measuring: the figures are the profile's again (its result, shown next, or the roles' own when it gave none).
        if (!measuring && _profile is { } profile)
            _figures = profile;
        if (!_closed)
            ShowCards();
    }

    // The profile's kinds, from the least to the most measured: one never gives way to a lesser one.
    private static int MeasuredRank(HardwareBenchmarkResult result) => result.Kind switch
    {
        HardwareBenchmarkResult.KindEstimate => 0,
        HardwareBenchmarkResult.KindQuick => 1,
        _ => 2,
    };

    /// <summary>
    /// Shows a hardware profile: its roles and figures on the role cards, the roles Start Editing writes (a first start), and the encoders
    /// estimated from it (until FFmpeg's answer, which always wins). A profile less measured than the one shown is ignored: an estimate
    /// that comes after the test's result.
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

    /// <summary>The test's progress (5.6A): its figures and pills on the cards; the roles stay those shown until its result.</summary>
    private void ShowFigures(HardwareBenchmarkResult partial)
    {
        _figures = partial;
        ShowCards();
    }

    // The role cards from the roles shown, the figures shown and the picks; the menus' cards; the System tier beside the title.
    private void ShowCards()
    {
        if ((_profile ?? _figures) is not { } roles)
            return;

        var figures = _figures ?? roles;
        RoleCards.Devices = [.. roles.Adapters.Where(a => a.SameCardAs < 0).Select(a => new RoleDevice(a.Index, a.Name))];
        RoleCards.HasNpu = roles.Npus.Count > 0;
        RoleCards.ItemsSource = RoleCardsOf(roles, figures, _measuring, ShownPicks(_picks, _saved));

        // The System tier of a result, never of the test's progress (its figures come in a stage at a time).
        var system = !_measuring && _profile is { } profile && MeasuredRank(profile) > 0 ? PerformanceTiers.System(profile) : null;
        SystemTierBadge.Child = system is { } tier ? RoleCardsPanel.TierPill(tier, PerformanceTiers.SystemTip(_profile!, tier)) : null;
        SystemTierBadge.Visibility = system is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // A role picked on a card (RoleCardsPanel.RoleChosen): kept for Start Editing, the cards shown with it.
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
    /// The roles picked by hand written into the settings Start Editing saves (5.6A): a card the profile does not list is Auto; Background
    /// may be Processor Only; the NPU's pick is what Auto-Captions run on. Never a card the user did not pick (CLAUDE.md 8).
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

    /// <summary>The role cards of a profile (5.5A, pure: the goldens check it), its figures its own, nothing picked.</summary>
    internal static IReadOnlyList<FirstRunRoleCard> RoleCardsOf(HardwareBenchmarkResult result, bool measuring) => RoleCardsOf(result, result, measuring, new FirstRunRolePicks());

    /// <summary>
    /// The role cards (5.5A; 5.6A tiers and picks; pure): the Compositor (the card the roles name), Decode and Background (Auto, the
    /// compositor's card: CLAUDE.md 8), Export (Auto, or the card that encodes more) and the NPU, each as <paramref name="picks"/> has it
    /// where it was picked by hand. The roles come from <paramref name="roles"/>, the figures and tier pills from <paramref name="figures"/>
    /// (the test's progress while it runs). A card's badge is its measurement ("62 fps · 9,870 MB/s"; the bus alone before the decoding
    /// can be measured), else "Measuring..." while the test runs, else "Estimated".
    /// </summary>
    internal static IReadOnlyList<FirstRunRoleCard> RoleCardsOf(HardwareBenchmarkResult roles, HardwareBenchmarkResult figures, bool measuring, FirstRunRolePicks picks)
    {
        var cards = roles.Adapters.Where(a => a.SameCardAs < 0).ToList();
        HardwareBenchmarkAdapter? Listed(int index) => cards.FirstOrDefault(a => a.Index == index);
        HardwareBenchmarkAdapter? Measurement(HardwareBenchmarkAdapter? card) => card is null ? null : figures.Adapters.FirstOrDefault(a => a.Index == card.Index) ?? card;

        // 5.5A review: roles that name no compositor (Auto: a quick check whose compositor's card could not be measured) show the card the
        // listing named, with the most video memory (where Auto lands on such a machine), marked Auto and Not measured: never another card
        // the check did not measure.
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
            // 5.6A review: Processor Only is measured where the processor's tier is (Settings' rule); a card the profile does not list
            // (a saved role on a card gone) shows Auto, so its menu still has one item checked.
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

        // 5.6A review: with nothing picked, the Compositor's and Export's menus check the card shown (the recommendation Start Editing
        // writes), so choosing Auto (Recommended) is a deliberate move to Auto, and choosing the card shown changes nothing.
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

    // ----- The encoders estimated from the cards (5.5A) -----

    // The best hardware encoder is the first of these (an H.264 one) that is here, the Export card's own family first.
    private static readonly string[] PreferredHardware = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"];

    /// <summary>The FFmpeg encoder family of a card's vendor: NVENC for NVIDIA, QuickSync for Intel, AMF for AMD, else Media Foundation (a Qualcomm Adreno).</summary>
    internal static string EncoderSuffix(uint vendorId) => vendorId switch
    {
        0x10DE => "_nvenc",
        0x8086 => "_qsv",
        0x1002 => "_amf",
        _ => "_mf",
    };

    /// <summary>
    /// The hardware encoders a profile points to before FFmpeg can be asked: for each card, its vendor's encoder of each codec Media
    /// Foundation has a hardware encoder of on that card (H.264, HEVC, AV1), in the order the probe offers them (every vendor's own
    /// first), a Media Foundation one only for a codec no vendor's encoder here covers (EncoderProber.CoveringVendorEncoder).
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

    /// <summary>The encoder family of the Export role's card (the compositor's when Export is Auto); null with no profile or no card.</summary>
    internal static string? ExportEncoderSuffix(HardwareBenchmarkResult? profile)
    {
        if (profile is null || profile.Adapters.Count == 0)
            return null;

        var index = profile.Recommended.Export >= 0 ? profile.Recommended.Export : profile.Recommended.Compositor;
        var card = profile.Adapters.FirstOrDefault(a => a.Index == index && a.SameCardAs < 0);
        return card is null ? null : EncoderSuffix(card.VendorId);
    }

    /// <summary>
    /// The hardware encoders in the order they are offered, and the one chosen when none was chosen by hand (5.5A, D1): the Export card's
    /// family first, then NVENC, QuickSync, AMF and Media Foundation; the best is the first H.264 one of that order that is here, else
    /// libx264.
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

    // A Qualcomm Adreno among the profile's cards: Media Foundation's encoders are named for it ("H.264 (Qualcomm Adreno)").
    private static bool HasQualcommAdreno(HardwareBenchmarkResult? profile) => profile?.Adapters.Any(a => a.Vendor == "Qualcomm") ?? false;

    /// <summary>Progress passed to this window's thread whatever thread reports it (and whatever context the window was made in).</summary>
    private sealed class UiProgress(FirstRunWindow window, Action<HardwareBenchmarkProgress> show) : IProgress<HardwareBenchmarkProgress>
    {
        public void Report(HardwareBenchmarkProgress value) => window.Dispatcher.BeginInvoke(() =>
        {
            if (!window._closed)
                show(value);
        });
    }
}
