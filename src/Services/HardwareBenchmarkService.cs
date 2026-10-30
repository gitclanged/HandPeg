using System.IO;
using System.Text.Json;
using CliWrap;

namespace HandPegApp.Services;

/// <summary>
/// Test &amp; Auto-Tune Hardware (Pass 5.0-Core, micro-step 5.0-0): runs the hardware benchmark (HandPeg.exe --hw-benchmark,
/// a worker of its own, so that a driver that falls over takes only it down) on a short test clip, passes on its progress
/// as it measures, keeps the result in hardware-benchmark.json beside the settings, and puts the four GPU roles it
/// recommends into the settings. Only ever started by the user's press of the button (Settings, the first-run window):
/// nothing here runs before the main window's first frame.
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

        HardwareBenchmarkResult? result = null;
        string? lastOther = null;
        void OnLine(string text)
        {
            // Its standard error comes down the same pipe: anything that is not one of its JSON lines is kept for the log only.
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
                work.Report(Math.Clamp(step.Fraction, 0, 1), step.Message);
                progress?.Report(step);
            }
        }

        var exitCode = await WorkerProcess.RunLinesAsync($"--hw-benchmark \"{clip}\"", OnLine, work.Token);
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
    public static void WriteRoles(HardwareBenchmarkRoles roles, AppSettings settings)
    {
        var cards = GpuAdapters.All;
        int Known(int index) => index >= 0 && cards.Any(a => a.Index == index) ? index : -1;
        settings.CompositorGpuAdapter = Known(roles.Compositor);
        settings.DecodeGpuAdapter = Known(roles.Decode);
        settings.BackgroundGpu = roles.Background == -2 ? -2 : Known(roles.Background);
        settings.ExportGpuAdapter = Known(roles.Export);
    }
}
