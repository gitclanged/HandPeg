using System.Globalization;
using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// One role card of the first-run window's second screen (5.5A): the role, the card that holds it, why, the badge of its measurement
/// ("Estimated", "Measuring...", "<fps> fps · <MB/s> MB/s"), whether that badge is a measurement, and its UI Automation id.
/// </summary>
public sealed record FirstRunRoleCard(string Role, string Card, string Detail, string Badge, bool Measured, string AutomationId);

// ----- The two-phase hardware profile (Pass 5.5A) -----
//
// Phase 1, as the window is made: HandPeg.exe --hw-benchmark --estimate, started from a pool thread (Task.Run: CreateProcess and every
// DXGI, DXCore and Media Foundation call stay off the UI thread and out of this process), its cards on the role cards the moment its
// first line comes, then the estimated hardware encoders (from the Media Foundation encoders each card has) until FFmpeg's own answer.
// Phase 2, on the first start only (no owner: no editor and no compositor alive), once the window is up: the quick check of the cards
// that hold a role (HardwareBenchmarkService.QuickTargets), in the background job, the badges going from Estimated to Measuring... to
// their figures; the bus only while there is no FFmpeg, again with the decoding once FFmpeg is installed. Opened from Settings, nothing
// is measured on its own: the last result kept is shown when it holds for these cards. Start Editing and closing kill both workers.
public partial class FirstRunWindow
{
    private readonly CancellationTokenSource _profileCancellation = new();

    // The estimate, as it is running or as it ended (null: it could not be made); the profile shown (the estimate, the quick check, or
    // a result kept: UI thread only).
    private Task<HardwareBenchmarkResult?> _estimate = Task.FromResult<HardwareBenchmarkResult?>(null);
    private HardwareBenchmarkResult? _profile;

    // The quick check: whether this window runs it (the first start), whether it is running (and whether its run had an FFmpeg for
    // the decoding), whether it is to run again when it ends, and whether the last one decoded.
    private bool _measuresAutomatically, _measuring, _quickRunning, _runHasFfmpeg, _quickAgain, _quickDecoded;

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
        {
            ProfileStatusText.Text = "The graphics cards could not be looked at: every GPU role stays on Auto, the card Windows calls high-performance.";
            ShowProfileWarning(estimate?.Error is { Length: > 0 } error ? error : "no graphics card was listed");
        }
    }

    /// <summary>
    /// The warning line under the roles' status (5.5.0, the user's Q10 b; pure: the goldens check it): the quick hardware check failed, why,
    /// and where to set the roles from a full test later.
    /// </summary>
    internal static string ProfileWarning(string why) =>
        $"Warning: the quick hardware check failed ({why.Trim().TrimEnd('.')}). The roles below are estimates: run Test & Auto-Tune Hardware in Settings > Export & Hardware > GPU & NPU Roles, then Apply Recommended Roles.";

    private void ShowProfileWarning(string? why)
    {
        ProfileWarningText.Text = why is { Length: > 0 } ? ProfileWarning(why) : "";
        ProfileWarningText.Visibility = why is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // Phase 2 on the first start (no owner); opened from Settings, the result kept, when it holds for these cards.
    private async void Profile_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Profile_Loaded;
        if (Owner is null)
        {
            _measuresAutomatically = true;
            MeasureQuickly();
            return;
        }

        try
        {
            var kept = await Task.Run(() => HardwareBenchmarkService.LoadSaved() is { Error.Length: 0 } saved && saved.Adapters.Count > 0
                && HardwareBenchmarkService.IsCurrent(saved) ? saved : null);
            if (kept is not null && !_closed)
                ShowProfile(kept);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("The hardware benchmark's saved result could not be shown", ex);
        }
    }

    /// <summary>Phase 2: the quick check of the role cards' cards, once the estimate has named them; again when asked meanwhile.</summary>
    private async void MeasureQuickly()
    {
        if (!_measuresAutomatically || _closed)
            return;
        if (_quickRunning)
        {
            _quickAgain = true;
            return;
        }

        _quickRunning = true;
        try
        {
            do
            {
                _quickAgain = false;
                var estimate = await _estimate;
                if (_closed || estimate is not { Error.Length: 0 })
                    return;

                var cards = HardwareBenchmarkService.QuickTargets(estimate);
                if (cards.Count == 0)
                    return;

                _runHasFfmpeg = File.Exists(DependencyUpdater.FfmpegPath);
                SetMeasuring(true);
                var progress = new UiProgress(this, step =>
                {
                    if (_measuring && step.Partial is { } partial)
                        ShowProfile(partial);
                });

                HardwareBenchmarkResult? result;
                try
                {
                    result = await HardwareBenchmarkService.RunQuickAsync(cards, progress, _profileCancellation.Token);
                }
                finally
                {
                    SetMeasuring(false);
                }

                if (_closed)
                    return;

                if (result is { Error.Length: 0 })
                {
                    // 5.5A review: decoded from its numbers, not from the clip's name (a clip tried and failed may be tried again).
                    _quickDecoded = HardwareBenchmarkService.QuickDecoded(result);
                    ShowProfileWarning(null);
                    ShowProfile(result);
                }
                else
                {
                    var why = HardwareBenchmarkService.LastFailure is { Length: > 0 } said ? said : result?.Error is { Length: > 0 } error ? error : "it stopped short";
                    if (_profile is not null)
                        ProfileStatusText.Text = $"Estimated from the cards; the quick check did not finish ({why.TrimEnd('.')}).{RolesNote()}";
                    ShowProfileWarning(why);
                }
            }
            while (_quickAgain && !_closed);
        }
        finally
        {
            _quickRunning = false;
        }
    }

    /// <summary>
    /// FFmpeg is here now (installed: DependencyUpdater.Installed): a quick check that could measure the bus only (no test clip without FFmpeg) is
    /// made again with the decoding, once the one running ends.
    /// </summary>
    private void MeasureAgainWithDecoding()
    {
        if (_measuresAutomatically && !(_quickRunning ? _runHasFfmpeg : _quickDecoded))
            MeasureQuickly();
    }

    private void SetMeasuring(bool measuring)
    {
        _measuring = measuring;

        // 5.5.2: the Playback & A/V Sync Test waits for the quick check (both would measure the same cards at once).
        SyncTestCard.BlockedBy = measuring ? "Waits for the hardware check" : null;
        if (!_closed && _profile is { } shown)
            ShowProfile(shown, again: true);
    }

    // The profile's kinds, from the least to the most measured: one never gives way to a lesser one.
    private static int MeasuredRank(HardwareBenchmarkResult result) => result.Kind switch
    {
        HardwareBenchmarkResult.KindEstimate => 0,
        HardwareBenchmarkResult.KindQuick => 1,
        _ => 2,
    };

    /// <summary>
    /// Shows a hardware profile: the role cards, the status line, the roles Start Editing writes (a first start), and the encoders
    /// estimated from it (until FFmpeg's answer, which always wins). A profile less measured than the one shown is ignored: an estimate
    /// that comes after the quick check's figures, or after the full test's result kept.
    /// </summary>
    internal void ShowProfile(HardwareBenchmarkResult result) => ShowProfile(result, again: false);

    private void ShowProfile(HardwareBenchmarkResult result, bool again)
    {
        if (!again && _profile is { } shown && MeasuredRank(result) < MeasuredRank(shown))
            return;

        _profile = result;
        var usable = result.Error.Length == 0 && result.Adapters.Count > 0;
        _benchmarkRoles = usable ? result.Recommended : null;
        RoleCards.ItemsSource = RoleCardsOf(result, _measuring);
        ProfileStatusText.Text = ProfileStatus(result);

        _estimatedHardware = usable ? EstimatedEncoders(result) : [];
        RefreshEncoders();
        ShowEstimatedEncoderStatus();
    }

    // The status line over the role cards: what the badges are, and what Start Editing does with the roles.
    private string ProfileStatus(HardwareBenchmarkResult result)
    {
        if (result.Error.Length > 0 || result.Adapters.Count == 0)
            return "The graphics cards could not be looked at: every GPU role stays on Auto, the card Windows calls high-performance.";

        var what = result.Kind switch
        {
            HardwareBenchmarkResult.KindEstimate when _measuring => "Estimated from the cards; measuring them in the background now (about a second and a half).",
            HardwareBenchmarkResult.KindEstimate when _measuresAutomatically => "Estimated from the cards; measured in the background next.",
            HardwareBenchmarkResult.KindEstimate => "Estimated from the cards (the full test is in Settings).",
            HardwareBenchmarkResult.KindQuick when _measuring => "Measuring in the background...",
            HardwareBenchmarkResult.KindQuick => QuickStatus(result),
            _ => DateTime.TryParse(result.MeasuredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? $"From the full test of {at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)}."
                : "From the full test.",
        };
        return what + RolesNote();
    }

    /// <summary>
    /// A quick check's status line (5.5A review): what it measured, from its numbers (HardwareBenchmarkService.QuickMeasured: decoding only
    /// when a card decoded the test clip), on the cards that hold a role. Pure: the goldens check it.
    /// </summary>
    internal static string QuickStatus(HardwareBenchmarkResult result)
    {
        var measured = HardwareBenchmarkService.QuickMeasured(result);
        if (measured.Length == 0)
            return "Estimated from the cards: the quick check could not measure them, and a card it could not measure holds no role.";
        if (!HardwareBenchmarkService.QuickDecoded(result))
        {
            return result.Clip.Length == 0
                ? "The bus measured in the background; the decoding is measured once FFmpeg is downloaded."
                : string.Create(CultureInfo.InvariantCulture, $"The bus measured in the background in {result.TotalSeconds:0.0} s; the test clip did not decode in hardware.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"Measured in the background in {result.TotalSeconds:0.0} s: {measured} on the cards that hold a role.");
    }

    // Start Editing gives each role its card on a first start; opened again, the roles stay as Settings has them.
    private static string RolesNote() => AppSettings.Current.FirstRunComplete
        ? " Your GPU roles stay as Settings has them."
        : " Start Editing gives each role its card.";

    /// <summary>
    /// The role cards of a profile: the Compositor (the card the roles name), Decode and Background (always Auto, the compositor's card:
    /// CLAUDE.md 8), Export (Auto, or the card that encodes more) and the NPU. A card's badge is its measurement ("62 fps · 9,870 MB/s";
    /// the bus alone before the decoding can be measured), else "Measuring..." while the quick check runs, else "Estimated".
    /// </summary>
    internal static IReadOnlyList<FirstRunRoleCard> RoleCardsOf(HardwareBenchmarkResult result, bool measuring)
    {
        var cards = result.Adapters.Where(a => a.SameCardAs < 0).ToList();

        // 5.5A review: roles that name no compositor (Auto: a quick check whose compositor's card could not be measured) show the card the
        // listing named, with the most video memory (where Auto lands on such a machine), marked Auto and Not measured: never another card
        // the check did not measure.
        var auto = result.Recommended.Compositor < 0;
        var compositor = cards.FirstOrDefault(a => a.Index == result.Recommended.Compositor)
                         ?? cards.OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).FirstOrDefault();
        var export = result.Recommended.Export >= 0 ? cards.FirstOrDefault(a => a.Index == result.Recommended.Export) : null;
        var compositorName = compositor?.Name ?? "No graphics card found";

        string Badge(HardwareBenchmarkAdapter? card) => card is null ? ""
            : Figures(card) is { Length: > 0 } figures ? figures
            : measuring && card.Note.Length == 0 ? "Measuring..."
            : card.Note.Length > 0 && result.Kind != HardwareBenchmarkResult.KindEstimate ? "Not measured"
            : "Estimated";
        bool Measured(HardwareBenchmarkAdapter? card) => card is not null && Figures(card).Length > 0;

        var memory = compositor is null ? "Auto: the card Windows calls high-performance"
            : compositor.UnifiedMemory ? "Shares the computer's memory · the player, its effects and shaders"
            : string.Create(CultureInfo.InvariantCulture, $"{compositor.DedicatedVideoMemoryMb / 1024.0:0.#} GB of video memory · the player, its effects and shaders");
        var encoding = export ?? compositor;
        var codecs = encoding is null || encoding.EncodeCodecs.Count == 0
            ? "No hardware encoder listed: exports use the software encoders"
            : string.Join(" · ", new[] { "AV1", "HEVC", "H.264" }.Where(encoding.EncodeCodecs.Contains)) + " in hardware";
        var npu = result.Npus.FirstOrDefault();

        return
        [
            new("Compositor", auto && compositor is not null ? $"Auto · {compositorName}" : compositorName, memory, Badge(compositor), Measured(compositor), "CompositorRole"),
            new("Decode", $"Auto · {compositorName}", "Timeline Clips decode on the compositor's card: zero-copy, nothing crosses the bus", Badge(compositor), Measured(compositor), "DecodeRole"),
            new("Background", $"Auto · {compositorName}", "Thumbnails, waveforms and proxies, on the compositor's card", Badge(compositor), Measured(compositor), "BackgroundRole"),
            new("Export", export is null ? $"Auto · {compositorName}" : export.Name, codecs, Badge(encoding), Measured(encoding), "ExportRole"),
            new("NPU", npu?.Name ?? "No NPU found", npu is null
                ? "Auto-Captions run on the graphics card or the processor"
                : "Detected, not used yet: Auto-Captions on the NPU are planned with the AI worker", npu is null ? "" : "Detected", false, "NpuRole"),
        ];
    }

    // A card's measurement on its badge: decoding and the bus, or the bus alone; empty when nothing was measured on it.
    private static string Figures(HardwareBenchmarkAdapter card) => (card.HardwareDecodeFps, card.ReadbackMBps) switch
    {
        ( > 0, > 0) => string.Create(CultureInfo.InvariantCulture, $"{card.HardwareDecodeFps:#,0} fps · {card.ReadbackMBps:#,0} MB/s"),
        ( > 0, _) => string.Create(CultureInfo.InvariantCulture, $"{card.HardwareDecodeFps:#,0} fps"),
        (_, > 0) => string.Create(CultureInfo.InvariantCulture, $"{card.ReadbackMBps:#,0} MB/s"),
        _ => "",
    };

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
