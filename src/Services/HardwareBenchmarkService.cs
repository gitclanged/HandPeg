using System.IO;
using System.Text.Json;
using CliWrap;

namespace HandPegApp.Services;

/// <summary>
/// Test &amp; Auto-Tune Hardware: runs the benchmark in a separate worker (HandPeg.exe --hw-benchmark, isolating driver crashes) on a short
/// test clip, reports progress, saves hardware-benchmark.json beside the settings and applies the four recommended GPU roles. Started
/// only from the Settings button, never before Frame 1. The first-run two-phase profile (<see cref="EstimateAsync"/>, then
/// <see cref="RunQuickAsync"/>) runs only from the first-run window (estimate only when reopened from Settings), never on normal startup.
/// </summary>
public static class HardwareBenchmarkService
{
    /// <summary>Test clip: 1.5 s FFmpeg moving test pattern, 1080p60 H.264, GOP 0.5 s; generated once.</summary>
    public static string ClipPath => Path.Combine(AppPaths.Cache, "benchmark", "bench-1080p60-h264.mp4");

    /// <summary>Where the last result is kept.</summary>
    public static string ResultPath => Path.Combine(AppPaths.Settings, "hardware-benchmark.json");

    // The benchmark's lines and the saved result are camelCase JSON (HardwareBenchmarkModels.cs).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static HardwareBenchmarkResult? _saved;
    private static bool _savedRead;

    /// <summary>Short status-line reason the last run produced no result ("Cancelled.", ...); empty after success.</summary>
    public static string LastFailure { get; private set; } = "";

    /// <summary><see cref="LastFailure"/> text when another window holds the benchmark gate (shown as a warning in the first-run window).</summary>
    public const string BusyFailure = "The test is already running (in the other window).";

    /// <summary>
    /// Ensures the test clip, runs the benchmark and returns the result (saved unless a clip-less run would replace a kept decode result,
    /// <see cref="ClipLessMayReplace"/>); null on failure or cancel (<see cref="LastFailure"/>). Progress arrives on a pool thread (pass a
    /// UI-created <see cref="Progress{T}"/>). Roles are applied separately (<see cref="ApplyRoles"/>).
    /// </summary>
    public static async Task<HardwareBenchmarkResult?> RunAsync(IProgress<HardwareBenchmarkProgress>? progress, CancellationToken token)
    {
        // Single run at a time, visibly refused: joining via the work key would leave the second caller without progress or a working Stop.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            LastFailure = BusyFailure;
            return null;
        }

        LastFailure = "";
        try
        {
            return await WorkScheduler.Run(new WorkSpec("Test & Auto-Tune Hardware", WorkLane.Interactive) { Key = "hardware-benchmark", Visible = true },
                work => MeasureAsync(work, progress), token);
        }
        catch (OperationCanceledException)
        {
            LastFailure = "Cancelled.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("The hardware benchmark could not be run", ex);
            LastFailure = ex.Message;
            return null;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static int _running;

    private static async Task<HardwareBenchmarkResult?> MeasureAsync(WorkContext work, IProgress<HardwareBenchmarkProgress>? progress)
    {
        work.Report(0, "Making the test clip");
        var clip = File.Exists(ClipPath) || File.Exists(DependencyUpdater.FfmpegPath) ? await EnsureClipAsync(work.Token) ?? "" : "";
        if (clip.Length == 0)
            AppLog.Write($"The hardware benchmark runs without its test clip (no decoding measured): {(LastFailure.Length > 0 ? LastFailure : "FFmpeg is not installed yet.")}");
        LastFailure = "";

        var settings = AppSettings.Current;
        var arguments = FullArguments(clip, DependencyUpdater.FfmpegPath, [settings.CompositorGpuAdapter, settings.ExportGpuAdapter]);
        var (exitCode, result, lastOther) = await RunWorkerAsync(arguments, progress, work, ProcessClass.Foreground, work.Token);
        work.Token.ThrowIfCancellationRequested();
        if (exitCode != 0 || result is null)
        {
            AppLog.Write(FormattableString.Invariant($"The hardware benchmark ended without a result (exit code {exitCode}){(lastOther is null ? "" : ": " + lastOther)}"));
            LastFailure = exitCode != 0 ? FormattableString.Invariant($"The test stopped short (exit code {exitCode}).") : "The test gave no result.";
            return null;
        }

        if (result.Error.Length > 0)
            AppLog.Write($"The hardware benchmark stopped short: {result.Error}");

        // A clip-less run (no FFmpeg) never replaces a kept same-hardware result with decode measurements; it is still returned.
        if (clip.Length > 0 || ClipLessMayReplace(LoadSaved(), result))
            Save(result);
        else
            AppLog.Write("The hardware benchmark ran without its test clip: the kept result, which measured decoding on these cards, is kept");
        return result;
    }

    /// <summary>
    /// True when a clip-less full run may replace the kept result (pure, golden-tested): never over a same-fingerprint full result with
    /// decode measurements (fingerprints come from the worker; no DXGI here); otherwise yes.
    /// </summary>
    internal static bool ClipLessMayReplace(HardwareBenchmarkResult? saved, HardwareBenchmarkResult result) =>
        saved is null || saved.Kind.Length > 0 || saved.Fingerprint != result.Fingerprint
        || !saved.Adapters.Any(a => a.HardwareDecodeFps > 0 || a.SoftwareDecodeFps > 0);

    /// <summary>
    /// Runs the benchmark worker and parses its output: forwards progress lines (also to the work item), keeps the result line. Returns exit
    /// code, result (or null) and the last non-JSON line (stderr shares the pipe) for logging.
    /// </summary>
    private static async Task<(int ExitCode, HardwareBenchmarkResult? Result, string? Other)> RunWorkerAsync(string arguments,
        IProgress<HardwareBenchmarkProgress>? progress, WorkContext? work, ProcessClass jobClass, CancellationToken token)
    {
        HardwareBenchmarkResult? result = null;
        string? lastOther = null;
        void OnLine(string text)
        {
            HardwareBenchmarkLine? line = null;
            if (text.StartsWith('{'))
            {
                try
                {
                    line = JsonSerializer.Deserialize<HardwareBenchmarkLine>(text, Json);
                }
                catch (JsonException)
                {
                }
            }

            if (line is null)
            {
                lastOther = text;
                return;
            }

            if (line.Type == "result" && line.Result is { } final)
            {
                result = final;
            }
            else if (line.Progress is { } step)
            {
                work?.Report(Math.Clamp(step.Fraction, 0, 1), step.Message);
                progress?.Report(step);
            }
        }

        var exitCode = await WorkerProcess.RunLinesAsync(arguments, OnLine, token, jobClass);
        return (exitCode, result, lastOther);
    }

    // ----- First-run two-phase hardware profile -----

    /// <summary>
    /// Phase 1 (--hw-benchmark --estimate, HardwareBenchmark.Profile.cs): enumerates adapters, hardware encoders and NPUs and derives roles
    /// without measuring; adapters reach <paramref name="progress"/> on the first line. Ungated (no video driver load, tens of ms), foreground
    /// job, never saved. Null on failure or cancel (kills the worker). Logs timings. Call from the pool (CreateProcess blocks).
    /// </summary>
    public static async Task<HardwareBenchmarkResult?> EstimateAsync(IProgress<HardwareBenchmarkProgress>? progress, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var (exitCode, result, lastOther) = await RunWorkerAsync($"{Compositor.HardwareBenchmark.Argument} {Compositor.HardwareBenchmark.EstimateOption}",
                progress, null, ProcessClass.Foreground, token).ConfigureAwait(false);
            if (result is null)
            {
                AppLog.Write(FormattableString.Invariant($"The hardware estimate ended without a result (exit code {exitCode}){(lastOther is null ? "" : ": " + lastOther)}"));
                return null;
            }

            var wall = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AppLog.Write(FormattableString.Invariant(
                $"Hardware estimate (5.5A): {result.Adapters.Count} graphics card(s), {result.Npus.Count} NPU(s) in {result.TotalSeconds * 1000:0} ms in the worker, {wall:0} ms with its start{(result.Error.Length > 0 ? ": " + result.Error : "")}"));
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("The hardware estimate could not be run", ex);
            return null;
        }
    }

    /// <summary>
    /// Phase 2 (--hw-benchmark [clip] --quick --adapters ...): measures hardware decode (when a test clip exists or can be made) and bus
    /// bandwidth for <paramref name="cards"/> only (<see cref="QuickTargets"/>), background job and lane, ~1.5 s. Shares the benchmark gate.
    /// Saved unless a current full result exists (<see cref="QuickMayReplace"/>). Null on failure, gate refusal or cancel (kills the worker).
    /// </summary>
    public static async Task<HardwareBenchmarkResult?> RunQuickAsync(IReadOnlyList<int> cards, IProgress<HardwareBenchmarkProgress>? progress, CancellationToken token)
    {
        if (cards.Count == 0)
            return null;

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            LastFailure = BusyFailure;
            return null;
        }

        LastFailure = "";
        try
        {
            return await WorkScheduler.Run(new WorkSpec("Hardware profile", WorkLane.Background) { Key = "hardware-profile", Visible = false },
                work => MeasureQuickAsync(work, cards, progress), token);
        }
        catch (OperationCanceledException)
        {
            LastFailure = "Cancelled.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("The quick hardware check could not be run", ex);
            LastFailure = ex.Message;
            return null;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    // Runs entirely on the pool (CreateProcess, clip generation, result read/save); the UI thread only starts it.
    private static Task<HardwareBenchmarkResult?> MeasureQuickAsync(WorkContext work, IReadOnlyList<int> cards, IProgress<HardwareBenchmarkProgress>? progress) =>
        Task.Run(() => MeasureQuickOffThreadAsync(work, cards, progress), CancellationToken.None);

    private static async Task<HardwareBenchmarkResult?> MeasureQuickOffThreadAsync(WorkContext work, IReadOnlyList<int> cards, IProgress<HardwareBenchmarkProgress>? progress)
    {
        // Use the test clip when available or creatable; otherwise bus-only (rerun after FFmpeg is installed).
        var clip = File.Exists(ClipPath) ? ClipPath
            : File.Exists(DependencyUpdater.FfmpegPath) ? await EnsureClipAsync(work.Token).ConfigureAwait(false) ?? ""
            : "";
        var (exitCode, result, lastOther) = await RunWorkerAsync(QuickArguments(clip, cards), progress, work, ProcessClass.Background, work.Token).ConfigureAwait(false);
        work.Token.ThrowIfCancellationRequested();
        if (exitCode != 0 || result is null)
        {
            AppLog.Write(FormattableString.Invariant($"The quick hardware check ended without a result (exit code {exitCode}){(lastOther is null ? "" : ": " + lastOther)}"));
            LastFailure = result?.Error is { Length: > 0 } error ? error : "The quick check gave no result.";
            return null;
        }

        AppLog.Write(FormattableString.Invariant(
            $"Quick hardware check (5.5A): card(s) {string.Join(",", cards)}, {(clip.Length > 0 ? "decoding and the bus" : "the bus only")}, in {result.TotalSeconds:0.00} s"));
        SaveQuick(result);
        return result;
    }

    /// <summary>
    /// Adapters measured by the quick check: the compositor adapter (role, else first adapter with most VRAM) and a distinct Export adapter.
    /// No other adapter is ever measured or given work unless a role names it (decoding on a secondary Intel Arc beside an AMD primary
    /// caused VIDEO_TDR_FAILURE 0x116).
    /// </summary>
    internal static IReadOnlyList<int> QuickTargets(HardwareBenchmarkResult estimate)
    {
        var working = estimate.Adapters.Where(a => a.SameCardAs < 0 && a.Note.Length == 0).ToList();
        var compositor = working.FirstOrDefault(a => a.Index == estimate.Recommended.Compositor)
                         ?? working.OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).FirstOrDefault();
        if (compositor is null)
            return [];

        var export = estimate.Recommended.Export >= 0 ? working.FirstOrDefault(a => a.Index == estimate.Recommended.Export) : null;
        return export is not null && export.Index != compositor.Index ? [compositor.Index, export.Index] : [compositor.Index];
    }

    /// <summary>
    /// Full test command line: optional clip; the app's FFmpeg (to list fallback-decodable codecs Media Foundation lacks); --adapters with
    /// the Settings roles when any names an adapter (also timed by the compositor stage).
    /// </summary>
    internal static string FullArguments(string clip, string ffmpeg, IReadOnlyList<int> chosen)
    {
        var text = new System.Text.StringBuilder(Compositor.HardwareBenchmark.Argument);
        if (clip.Length > 0 && !clip.Contains('"'))
            text.Append(" \"").Append(clip).Append('"');
        if (ffmpeg.Length > 0 && !ffmpeg.Contains('"'))
            text.Append(' ').Append(Compositor.CompositorWorker.FfmpegOption).Append(" \"").Append(ffmpeg).Append('"');
        var cards = chosen.Where(c => c >= 0).Distinct().ToList();
        if (cards.Count > 0)
            text.Append(' ').Append(Compositor.HardwareBenchmark.AdaptersOption).Append(' ').Append(string.Join(",", cards.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return text.ToString();
    }

    /// <summary>Quick check command line: optional clip, then --quick --adapters "0,2".</summary>
    internal static string QuickArguments(string clip, IReadOnlyList<int> cards)
    {
        var adapters = string.Join(",", cards.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var mode = $"{Compositor.HardwareBenchmark.QuickOption} {Compositor.HardwareBenchmark.AdaptersOption} {adapters}";
        return clip.Length > 0 && !clip.Contains('"')
            ? $"{Compositor.HardwareBenchmark.Argument} \"{clip}\" {mode}"
            : $"{Compositor.HardwareBenchmark.Argument} {mode}";
    }

    /// <summary>
    /// True when a quick check may replace the kept result: never over a same-fingerprint full result (fingerprints from the worker).
    /// </summary>
    internal static bool QuickMayReplace(HardwareBenchmarkResult? saved, HardwareBenchmarkResult quick) =>
        saved is null || saved.Kind.Length > 0 || saved.Fingerprint.Length > 0 && saved.Fingerprint != quick.Fingerprint;

    /// <summary>
    /// Description of what the quick check measured, derived from its numbers: "decoding and the bus", "decoding", "the bus", or "" (never
    /// inferred from the clip name; a failed decode does not count).
    /// </summary>
    internal static string QuickMeasured(HardwareBenchmarkResult result)
    {
        var (decoded, bus) = (QuickDecoded(result), result.Adapters.Any(a => a.ReadbackMBps > 0));
        return decoded && bus ? "decoding and the bus" : decoded ? "decoding" : bus ? "the bus" : "";
    }

    /// <summary>True when the quick check successfully decoded the clip on any adapter (first-run re-run gate).</summary>
    internal static bool QuickDecoded(HardwareBenchmarkResult result) => result.Adapters.Any(a => a.HardwareDecodeFps > 0);

    /// <summary>
    /// Roles written by the first-run window: profile Compositor and Export; Decode and Background always Auto (the compositor adapter),
    /// ignoring older results that named a secondary adapter.
    /// </summary>
    internal static HardwareBenchmarkRoles DecodeAndBackgroundAuto(HardwareBenchmarkRoles roles) =>
        new() { Compositor = roles.Compositor, Decode = -1, Background = -1, Export = roles.Export, Reasons = [.. roles.Reasons] };

    private static void SaveQuick(HardwareBenchmarkResult quick)
    {
        if (quick.Error.Length > 0 || !QuickMayReplace(LoadSaved(), quick))
            return;
        Save(quick);
    }

    /// <summary>Ensures the test clip exists (written to a temp file then moved); null when FFmpeg is missing or fails.</summary>
    private static async Task<string?> EnsureClipAsync(CancellationToken token)
    {
        var clip = ClipPath;
        if (File.Exists(clip))
            return clip;

        if (!File.Exists(DependencyUpdater.FfmpegPath))
        {
            LastFailure = "FFmpeg is needed to make the test clip: install the required components first.";
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
        var making = Path.Combine(Path.GetDirectoryName(clip)!, "bench-1080p60-h264.making.mp4");
        var made = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60", "-t", "1.5",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-pix_fmt", "yuv420p", "-an", "-y", making])
            .WithValidation(CommandResultValidation.None), token);
        if (made.ExitCode != 0 || !File.Exists(making))
        {
            AppLog.Write(FormattableString.Invariant($"The hardware benchmark's test clip could not be made (exit code {made.ExitCode}): {made.StandardError.Trim()}"));
            LastFailure = "The test clip could not be made.";
            TryDelete(making);
            return null;
        }

        File.Move(making, clip, overwrite: true);
        return clip;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Save(HardwareBenchmarkResult result)
    {
        (_saved, _savedRead) = (result, true);
        try
        {
            ProjectStore.WriteWhole(ResultPath, JsonSerializer.Serialize(result, Json), durable: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The hardware benchmark's result could not be saved", ex);
        }
    }

    /// <summary>
    /// Last saved result (read once, then cached); null when absent or unreadable. Small file, but never read before Frame 1.
    /// </summary>
    public static HardwareBenchmarkResult? LoadSaved()
    {
        if (_savedRead)
            return _saved;

        HardwareBenchmarkResult? saved = null;
        try
        {
            if (File.Exists(ResultPath))
                saved = JsonSerializer.Deserialize<HardwareBenchmarkResult>(File.ReadAllText(ResultPath), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write("The hardware benchmark's saved result could not be read", ex);
        }

        // A run that finished meanwhile wins over what was on disk.
        if (!_savedRead)
            (_saved, _savedRead) = (saved, true);
        return _saved;
    }

    /// <summary>True when the result's hardware/driver fingerprint matches the current system.</summary>
    public static bool IsCurrent(HardwareBenchmarkResult result) => result.Fingerprint.Length == 0 || result.Fingerprint == GpuAdapters.Fingerprint;

    /// <summary>Applies the four roles to the active settings and saves immediately.</summary>
    public static void ApplyRoles(HardwareBenchmarkRoles roles)
    {
        try
        {
            // Save before returning: GPU work restarts on the new roles and a driver fault must not lose them.
            AppSettings.Update(settings => WriteRoles(roles, settings), saveNow: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The roles still hold for this session.
            AppLog.Write("The GPU roles could not be saved", ex);
        }
    }

    /// <summary>
    /// Writes the four roles into a settings copy (e.g. the Settings window's) without saving; missing adapters become Auto; Background
    /// may be -2 (CPU).
    /// </summary>
    public static void WriteRoles(HardwareBenchmarkRoles roles, AppSettings settings) => WriteRoles(roles, settings, [.. GpuAdapters.All.Select(a => a.Index)]);

    /// <summary>
    /// <see cref="WriteRoles(HardwareBenchmarkRoles, AppSettings)"/> using the profile's adapter indices, so this process never enumerates
    /// DXGI (first-run Start on the UI thread).
    /// </summary>
    public static void WriteRoles(HardwareBenchmarkRoles roles, AppSettings settings, IReadOnlyCollection<int> cardsHere)
    {
        int Known(int index) => index >= 0 && cardsHere.Contains(index) ? index : -1;
        settings.CompositorGpuAdapter = Known(roles.Compositor);
        settings.DecodeGpuAdapter = Known(roles.Decode);
        settings.BackgroundGpu = roles.Background == -2 ? -2 : Known(roles.Background);
        settings.ExportGpuAdapter = Known(roles.Export);
    }
}
