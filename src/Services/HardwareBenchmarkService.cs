using System.IO;
using System.Text.Json;
using CliWrap;

namespace HandPegApp.Services;

/// <summary>
/// Test &amp; Auto-Tune Hardware (Pass 5.0-Core, micro-step 5.0-0): runs the hardware benchmark (HandPeg.exe --hw-benchmark,
/// a worker of its own, so that a driver that falls over takes only it down) on a short test clip, passes on its progress
/// as it measures, keeps the result in hardware-benchmark.json beside the settings, and puts the four GPU roles it
/// recommends into the settings. Only ever started by the user's press of the button (Settings): nothing here runs before
/// the main window's first frame. The first-run window's two-phase profile (5.5A: <see cref="EstimateAsync"/>, then
/// <see cref="RunQuickAsync"/>) runs only from that window, shown before the main window exists (or opened again from Settings,
/// where only the estimate runs): never on the normal start-up path.
/// </summary>
public static class HardwareBenchmarkService
{
    /// <summary>The test clip: 1.5 s of FFmpeg's moving test pattern, 1080p60 H.264 (an I-frame each half second), made once.</summary>
    public static string ClipPath => Path.Combine(AppPaths.Cache, "benchmark", "bench-1080p60-h264.mp4");

    /// <summary>Where the last result is kept.</summary>
    public static string ResultPath => Path.Combine(AppPaths.Settings, "hardware-benchmark.json");

    // The benchmark's lines and the saved result are camelCase JSON (HardwareBenchmarkModels.cs).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static HardwareBenchmarkResult? _saved;
    private static bool _savedRead;

    /// <summary>Why the last run gave no result, in a few words for the status line ("Cancelled.", "FFmpeg is not installed yet."); empty after one that did.</summary>
    public static string LastFailure { get; private set; } = "";

    /// <summary>
    /// Makes the test clip if it is not there yet, runs the benchmark on it and returns its result (also saved, and then
    /// what <see cref="LoadSaved"/> returns); null when it could not be run, failed or was cancelled (<see cref="LastFailure"/>
    /// says which). Progress is reported from a pool thread: give a <see cref="Progress{T}"/> made on the UI thread.
    /// The roles are not applied here: <see cref="ApplyRoles"/> does that.
    /// </summary>
    public static async Task<HardwareBenchmarkResult?> RunAsync(IProgress<HardwareBenchmarkProgress>? progress, CancellationToken token)
    {
        // One run at a time, refused visibly: a second caller (the first-run window over Settings) would otherwise join the
        // first through the work key, with no progress of its own and a Stop that stops nothing.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            LastFailure = "The test is already running (in the other window).";
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
        var clip = await EnsureClipAsync(work.Token);
        if (clip is null)
            return null;

        // With the FFmpeg the app uses: the codecs its fallback decodes where Media Foundation cannot are listed (5.2-Arm64AndNpu).
        var ffmpeg = DependencyUpdater.FfmpegPath;
        var arguments = ffmpeg.Length > 0 && !ffmpeg.Contains('"')
            ? $"{Compositor.HardwareBenchmark.Argument} \"{clip}\" {Compositor.CompositorWorker.FfmpegOption} \"{ffmpeg}\""
            : $"{Compositor.HardwareBenchmark.Argument} \"{clip}\"";
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
        Save(result);
        return result;
    }

    /// <summary>
    /// Runs the benchmark's worker with these arguments and reads its lines: each progress line passed on (to the work's progress
    /// too, when there is a work), the result line kept. Returns the exit code, the result (null when none came) and the last line
    /// that was not one of its JSON lines (its standard error comes down the same pipe), for the log.
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

    // ----- The first-run window's two-phase profile (5.5A) -----

    /// <summary>
    /// Phase 1: HandPeg.exe --hw-benchmark --estimate (HardwareBenchmark.Profile.cs), the cards, their hardware encoders, the NPUs and
    /// the roles that follow, nothing measured; the cards reach <paramref name="progress"/> on the first line. Not gated (it loads no
    /// card's video driver and takes a few tens of milliseconds), in the foreground job (the window waits for it), never saved. Null when
    /// it could not be run or was cancelled (cancelling kills the worker). Its own time and the time with the process start are logged.
    /// Call it from a pool thread (Task.Run): starting the process waits for CreateProcess.
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
    /// Phase 2: HandPeg.exe --hw-benchmark [clip] --quick --adapters ..., hardware decoding (when there is a test clip: made now when
    /// FFmpeg is here and the clip is not; with no FFmpeg, the bus only) and the bus of <paramref name="cards"/> only (see
    /// <see cref="QuickTargets"/>), in the background job and the Background lane, about a second and a half. One benchmark at a time:
    /// it shares Test &amp; Auto-Tune Hardware's gate. Saved unless a current full result is kept (<see cref="QuickMayReplace"/>). Null
    /// when it could not be run, was refused by the gate or was cancelled (cancelling kills the worker at once).
    /// </summary>
    public static async Task<HardwareBenchmarkResult?> RunQuickAsync(IReadOnlyList<int> cards, IProgress<HardwareBenchmarkProgress>? progress, CancellationToken token)
    {
        if (cards.Count == 0)
            return null;

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            LastFailure = "The test is already running (in the other window).";
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

    // All of it on a pool thread (the window's thread only starts it): the worker's CreateProcess, the clip, the result read and saved.
    private static Task<HardwareBenchmarkResult?> MeasureQuickAsync(WorkContext work, IReadOnlyList<int> cards, IProgress<HardwareBenchmarkProgress>? progress) =>
        Task.Run(() => MeasureQuickOffThreadAsync(work, cards, progress), CancellationToken.None);

    private static async Task<HardwareBenchmarkResult?> MeasureQuickOffThreadAsync(WorkContext work, IReadOnlyList<int> cards, IProgress<HardwareBenchmarkProgress>? progress)
    {
        // The test clip when it is here or FFmpeg can make it; else the bus only (the window runs it again once FFmpeg is installed).
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
    /// The cards the quick check measures: the compositor's (the role's card, or the first-listed card with the most video memory
    /// when the roles name none), and the Export card when it is another. Never any other card: a second card is never measured, nor
    /// given work, unless a role holds it (CLAUDE.md 8: decoding on the Desktop's Arc A310 beside its RX 7900 XTX ended in 0x116).
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

    /// <summary>The quick check's command line: the clip when there is one, then --quick --adapters with the cards ("0,2").</summary>
    internal static string QuickArguments(string clip, IReadOnlyList<int> cards)
    {
        var adapters = string.Join(",", cards.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var mode = $"{Compositor.HardwareBenchmark.QuickOption} {Compositor.HardwareBenchmark.AdaptersOption} {adapters}";
        return clip.Length > 0 && !clip.Contains('"')
            ? $"{Compositor.HardwareBenchmark.Argument} \"{clip}\" {mode}"
            : $"{Compositor.HardwareBenchmark.Argument} {mode}";
    }

    /// <summary>
    /// Whether a quick check may take the place of the result kept: never of a full result measured on these same cards and drivers
    /// (its fingerprint the quick check's own, both measured in a worker: nothing is asked of DXGI here); of anything else, yes.
    /// </summary>
    internal static bool QuickMayReplace(HardwareBenchmarkResult? saved, HardwareBenchmarkResult quick) =>
        saved is null || saved.Kind.Length > 0 || saved.Fingerprint.Length > 0 && saved.Fingerprint != quick.Fingerprint;

    /// <summary>
    /// What a quick check measured, from its numbers (5.5A review): "decoding and the bus", "decoding", "the bus" (no test clip yet, or the
    /// clip did not decode in hardware), or "" (no card gave a number). Never from the clip's name: a clip tried and failed is not decoding.
    /// </summary>
    internal static string QuickMeasured(HardwareBenchmarkResult result)
    {
        var (decoded, bus) = (QuickDecoded(result), result.Adapters.Any(a => a.ReadbackMBps > 0));
        return decoded && bus ? "decoding and the bus" : decoded ? "decoding" : bus ? "the bus" : "";
    }

    /// <summary>Whether a quick check decoded the test clip on some card (5.5A review: the first-run window's re-run gate; a failed decode is not one).</summary>
    internal static bool QuickDecoded(HardwareBenchmarkResult result) => result.Adapters.Any(a => a.HardwareDecodeFps > 0);

    /// <summary>
    /// The roles the first-run window writes (5.5A review): the profile's Compositor and Export, Decode and Background Auto whatever the
    /// profile holds (CLAUDE.md 8: the compositor's card; a result saved by a version before 5.4.4 may still name a second card for them).
    /// </summary>
    internal static HardwareBenchmarkRoles DecodeAndBackgroundAuto(HardwareBenchmarkRoles roles) =>
        new() { Compositor = roles.Compositor, Decode = -1, Background = -1, Export = roles.Export, Reasons = [.. roles.Reasons] };

    private static void SaveQuick(HardwareBenchmarkResult quick)
    {
        if (quick.Error.Length > 0 || !QuickMayReplace(LoadSaved(), quick))
            return;
        Save(quick);
    }

    /// <summary>The test clip, made now when it is not there yet (into a file beside it, then moved into place); null when FFmpeg is missing or failed.</summary>
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
            ProjectStore.WriteWhole(ResultPath, JsonSerializer.Serialize(result, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The hardware benchmark's result could not be saved", ex);
        }
    }

    /// <summary>
    /// The last result kept (read from disk once, then remembered); null when the benchmark has never run here or its file
    /// cannot be read. A small file: cheap, but never asked for before the first frame.
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

    /// <summary>Whether a result was measured on the cards and drivers there are now (not, after a card or driver changed).</summary>
    public static bool IsCurrent(HardwareBenchmarkResult result) => result.Fingerprint.Length == 0 || result.Fingerprint == GpuAdapters.Fingerprint;

    /// <summary>Puts the four roles into the settings in effect and saves them (the way the dialogs that save at once do).</summary>
    public static void ApplyRoles(HardwareBenchmarkRoles roles)
    {
        var settings = AppSettings.Current;
        WriteRoles(roles, settings);
        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The roles still hold for this session.
            AppLog.Write("The GPU roles could not be saved", ex);
        }
    }

    /// <summary>
    /// Writes the four roles into a copy of the settings (the one the Settings window edits, say) without saving it. A card
    /// that is not there now is Auto; Background may also be -2, the processor.
    /// </summary>
    public static void WriteRoles(HardwareBenchmarkRoles roles, AppSettings settings) => WriteRoles(roles, settings, [.. GpuAdapters.All.Select(a => a.Index)]);

    /// <summary>
    /// <see cref="WriteRoles(HardwareBenchmarkRoles, AppSettings)"/> against the cards a profile found itself (their DXGI numbers), so
    /// that nothing lists the cards in this process (5.5A: the first-run window's Start, on the UI thread).
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
