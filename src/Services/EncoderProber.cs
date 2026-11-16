using System.IO;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <summary>
/// Detects working GPU encoders by test-encoding with each one (FFmpeg lists every compiled-in encoder regardless of hardware).
/// </summary>
public static class EncoderProber
{
    /// <summary>
    /// Hardware encoders probed, in offer order: vendor encoders first, then Media Foundation (the route to ARM64 Snapdragon/Adreno
    /// encoders; see <see cref="MediaFoundationEncoder"/>).
    /// </summary>
    public static IReadOnlyList<string> HardwareEncoders { get; } =
    [
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_amf", "hevc_amf", "av1_amf",
        .. MediaFoundationEncoder.Names,
    ];

    /// <summary>
    /// Vendor encoder already covering a Media Foundation encoder's codec (h264_nvenc for h264_mf), else null (also for non-MF encoders).
    /// MF reaches the same silicon with fewer controls, so it is offered only for codecs no vendor encoder handles (ARM64 Adreno, or a
    /// vendor encoder FFmpeg lacks or cannot run). <see cref="HardwareEncoders"/> orders vendors first so they are known beforehand.
    /// </summary>
    /// <param name="supported">Encoders that have passed so far.</param>
    public static string? CoveringVendorEncoder(string encoder, IEnumerable<string> supported)
    {
        if (!MediaFoundationEncoder.Is(encoder) || encoder.IndexOf('_') is not (> 0 and var underscore))
            return null;
        var codec = encoder[..(underscore + 1)];
        return supported.FirstOrDefault(s => s.StartsWith(codec, StringComparison.Ordinal) && !MediaFoundationEncoder.Is(s));
    }

    /// <summary>
    /// Working hardware encoders, plus failure reasons for compiled-in ones that fail. Step 1: "ffmpeg -encoders" lists compiled-in
    /// encoders by name; step 2: each encodes a few frames, since compilation says nothing about the GPU or driver.
    /// </summary>
    public static async Task<(List<string> Supported, List<string> Problems)> ProbeAsync(CancellationToken cancellationToken)
    {
        var (supported, problems) = (new List<string>(), new List<string>());
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return (supported, problems);

        var listing = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-encoders"])
            .WithValidation(CommandResultValidation.None), cancellationToken);
        var built = HardwareEncoders.Where(name => System.Text.RegularExpressions.Regex.IsMatch(listing.StandardOutput, $@"^\s*V\S*\s+{name}\s", System.Text.RegularExpressions.RegexOptions.Multiline)).ToList();

        // The listing could not be read at all: fall back on trying every one.
        if (listing.ExitCode != 0 || listing.StandardOutput.Length == 0)
            built = [.. HardwareEncoders];

        Log($"Probing hardware encoders with {DependencyUpdater.FfmpegPath}: {(built.Count > 0 ? string.Join(", ", built) : "none are built in")}");

        var (refused, driverTooOld) = (new HashSet<string>(), false);

        // Each encoder is tested on its own: AV1 failing on a card that has no AV1 says nothing about its H.264.
        foreach (var encoder in built)
        {
            // A Media Foundation encoder whose codec a vendor encoder here already does is not tested, so never offered.
            if (CoveringVendorEncoder(encoder, supported) is { } vendor)
            {
                Log($"Hardware encoder {encoder} not tested: {vendor} does {encoder[..encoder.IndexOf('_')]} here.");
                continue;
            }

            var result = await TestEncodeAsync(encoder, MediaFoundationEncoder.Is(encoder) ? MediaFoundationEncoder.PixelFormat(false) : "yuv420p", [], cancellationToken);
            if (result.ExitCode == 0)
            {
                supported.Add(encoder);
                Log($"Hardware encoder {encoder} passed its test encode.");

                // NVENC capabilities vary by GPU generation and an unsupported option aborts the encode, so probe each once here.
                // AMF/QSV are probed for 10-bit only (no 10-bit H.264; older cards have none).
                var isNvenc = encoder.EndsWith("_nvenc", StringComparison.Ordinal);
                foreach (var (feature, pixelFormat, options) in FeatureTests.Where(t => isNvenc || t.Feature == TenBit))
                {
                    // QSV has no 10-bit H.264 and the probe would not reveal it (FFmpeg silently converts to 8-bit); same for MF.
                    if (encoder is "h264_qsv" or "h264_mf" && feature == TenBit)
                    {
                        refused.Add($"{encoder}:{feature}");
                        continue;
                    }

                    // MF encoders without 10-bit input are silently fed 8-bit; detect via FFmpeg's warning output.
                    var mf = MediaFoundationEncoder.Is(encoder);
                    var test = await TestEncodeAsync(encoder, pixelFormat, options, cancellationToken, warnings: mf);
                    if (test.ExitCode == 0 && !(mf && test.StandardError.Contains("Incompatible pixel format", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    refused.Add($"{encoder}:{feature}");
                    Log($"Hardware encoder {encoder} does not do {feature} on this card: {FirstLine(test)}");
                }

                continue;
            }

            // Why it would not start, in FFmpeg's own words: an outdated driver, no such card, a busy one.
            var reason = FirstLine(result);
            Log($"Hardware encoder {encoder} is in FFmpeg but did not start (exit code {result.ExitCode}): {reason}"
                + $"{Environment.NewLine}{Indent(result.StandardError)}");

            driverTooOld |= encoder.EndsWith("_nvenc", StringComparison.Ordinal)
                && result.StandardError.Contains("required nvenc API version", StringComparison.OrdinalIgnoreCase);

            // Report once per vendor (H.264 only; others fail identically), and never for absent vendors (NVENC-enabled FFmpeg fails to
            // load nvcuda.dll on every non-NVIDIA machine).
            if (encoder.StartsWith("h264_", StringComparison.Ordinal) && VendorPresent(encoder, GpuAdapters.All))
                problems.Add($"{encoder}: {reason}");
        }

        (_refused, NvencNeedsOlderFfmpeg) = (refused, driverTooOld);
        (_lastSupported, _lastProblems) = (supported, problems);
        SaveResult();
        return (supported, problems);
    }

    /// <summary>
    /// True when a probe problem or encoder belongs to a present vendor (NVENC/NVIDIA, AMF/AMD, QSV/Intel); MF and others always qualify.
    /// True when adapters could not be listed.
    /// </summary>
    public static bool VendorPresent(string encoderOrProblem, IReadOnlyList<GpuAdapter> adapters)
    {
        if (adapters.Count == 0)
            return true;

        var colon = encoderOrProblem.IndexOf(':');
        var encoder = (colon >= 0 ? encoderOrProblem[..colon] : encoderOrProblem).Trim();
        return encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? adapters.Any(a => a.IsNvidia)
            : encoder.EndsWith("_amf", StringComparison.Ordinal) ? adapters.Any(a => a.IsAmd)
            : !encoder.EndsWith("_qsv", StringComparison.Ordinal) || adapters.Any(a => a.IsIntel);
    }

    /// <summary>
    /// Problems to report: present vendors only (<see cref="VendorPresent"/>) once adapters are listed (<see cref="GpuAdapters.IsListed"/>);
    /// all before that (never listed here: pre-Frame-1 enumeration would load every driver).
    /// </summary>
    public static List<string> ProblemsForCardsHere(IEnumerable<string> problems) =>
        GpuAdapters.IsListed ? problems.Where(p => VendorPresent(p, GpuAdapters.All)).ToList() : [.. problems];

    // ----- Persisted probe result -----
    // Test encodes load GPU drivers (CPU/memory spike), yet the answer only changes with FFmpeg or drivers. The result is cached with
    // its fingerprint (FFmpeg executable, per-adapter driver) and re-probed only when that changes or from Settings.

    // (Legacy OpenCL-blur field is skipped like any unknown member.) Internal for source-generated metadata (AppSettingsJsonContext, pre-Frame-1).
    internal sealed record SavedResult(string Key, List<string> Supported, List<string> Problems, List<string> Refused, bool NvencNeedsOlderFfmpeg);

    // Bumped when probe semantics change so older results are discarded (2: MF encoders added; 3: NV12 testing, re-probing ARM64 "none found").
    private const int TestsVersion = 3;

    private static List<string> _lastSupported = [], _lastProblems = [];

    private static string SavedPath => Path.Combine(AppPaths.Cache, "encoder-probe.json");

    /// <summary>Result fingerprint: FFmpeg path, size and write time, plus adapters and driver versions.</summary>
    private static string MachineKey() => FfmpegKey() + "|" + GpuAdapters.Fingerprint;

    // The key without its last part, the cards (GpuAdapters.Fingerprint, which has no '|' in it).
    private static string FfmpegKey()
    {
        var ffmpeg = new FileInfo(DependencyUpdater.FfmpegPath);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{TestsVersion}|{ffmpeg.FullName}|{(ffmpeg.Exists ? ffmpeg.Length : 0)}|{(ffmpeg.Exists ? ffmpeg.LastWriteTimeUtc.Ticks : 0)}");
    }

    private static Task<(List<string> Supported, List<string> Problems)?>? _early;

    // The key of a result taken at launch for this FFmpeg, its cards not yet compared (see CardsStillMatch).
    private static string? _cardsUnchecked;

    /// <summary>
    /// Starts loading the saved result off-thread at launch so encoders are known before Frame 1. Only the FFmpeg fingerprint is checked
    /// then: DXGI enumeration loads every driver (up to 250 ms on wake) and stalled the UI thread. Adapters are checked after Frame 1
    /// (<see cref="CardsStillMatch"/>).
    /// </summary>
    public static void LoadSavedResultEarly() => _early = Task.Run(() => LoadSaved(checkCards: false));

    /// <summary>
    /// Post-Frame-1, off the UI thread: true when the launch result's adapter/driver fingerprint still matches; false triggers a re-probe.
    /// </summary>
    public static bool CardsStillMatch() => Interlocked.Exchange(ref _cardsUnchecked, null) is not { } key || key == MachineKey();

    /// <summary>
    /// Launch-loaded result if ready within <paramref name="patience"/>; null when absent, mismatched or still loading (the probe then
    /// loads it itself).
    /// </summary>
    public static (List<string> Supported, List<string> Problems)? TakeEarlyResult(TimeSpan patience) =>
        Interlocked.Exchange(ref _early, null) is { } early && early.Wait(patience) ? early.Result : null;

    /// <summary>
    /// Restores a saved probe result (per-encoder capabilities) matching this FFmpeg and drivers; null when absent or mismatched.
    /// </summary>
    public static (List<string> Supported, List<string> Problems)? LoadSavedResult() => LoadSaved(checkCards: true);

    private static (List<string> Supported, List<string> Problems)? LoadSaved(bool checkCards)
    {
        try
        {
            if (!File.Exists(SavedPath) || System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(SavedPath), AppSettingsJsonContext.Default.SavedResult) is not { } saved
                || !(checkCards ? saved.Key == MachineKey() : saved.Key.LastIndexOf('|') is var bar and >= 0 && saved.Key[..bar] == FfmpegKey()))
            {
                return null;
            }

            (_refused, NvencNeedsOlderFfmpeg) = ([.. saved.Refused], saved.NvencNeedsOlderFfmpeg);
            (_lastSupported, _lastProblems) = (saved.Supported, saved.Problems);
            _cardsUnchecked = checkCards ? null : saved.Key;
            Log($"Hardware encoders as found before for this FFmpeg{(checkCards ? " and these drivers" : " (its cards compared after the first frame)")}: {(saved.Supported.Count > 0 ? string.Join(", ", saved.Supported) : "none")}.");
            return (saved.Supported, saved.Problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Deletes the saved result so the next probe retests everything (Settings "Check Hardware Again").</summary>
    public static void ForgetSavedResult()
    {
        try
        {
            File.Delete(SavedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void SaveResult()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Cache);
            var saved = new SavedResult(MachineKey(), _lastSupported, _lastProblems, [.. _refused], NvencNeedsOlderFfmpeg);
            File.WriteAllText(SavedPath, System.Text.Json.JsonSerializer.Serialize(saved, AppSettingsJsonContext.Default.SavedResult));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept: it is asked again next time.
        }
    }

    /// <summary>Temporal adaptive quantization: not for HEVC on the GTX 10 series, for one.</summary>
    public const string TemporalAq = "temporal AQ";

    /// <summary>10-bit output (p010le): not for H.264 on the GTX 10 series.</summary>
    public const string TenBit = "10-bit";

    /// <summary>B-frames: not for HEVC before the RTX 20 series.</summary>
    public const string BFrames = "B-frames";

    // How each is asked for: the pixel format of the test frames, and the encoder options.
    private static readonly (string Feature, string PixelFormat, string[] Options)[] FeatureTests =
    [
        (TemporalAq, "yuv420p", ["-temporal-aq", "1"]),
        (TenBit, "p010le", []),
        (BFrames, "yuv420p", ["-bf", "2"]),
    ];

    // "encoder:feature" for everything the last probe saw a hardware encoder turn down. Replaced as a whole.
    private static volatile HashSet<string> _refused = [];

    /// <summary>False when the last probe found that this hardware encoder cannot do the feature on this card.</summary>
    public static bool Supports(string encoder, string feature) => !_refused.Contains($"{encoder}:{feature}");

    /// <summary>
    /// True when NVENC failed because the driver is older than FFmpeg requires; end-of-life GPUs (GTX 10 series) need an older FFmpeg.
    /// </summary>
    public static bool NvencNeedsOlderFfmpeg { get; private set; }

    // Realistic size, rate and pixel format: NVENC/AMF reject undersized frames and NVENC unexpected formats, faking "unsupported".
    // MF is tested in NV12 (MediaFoundationEncoder.PixelFormat; Adreno rejects yuv420p) with "-hw_encoding 1" like exports, so the
    // Windows software encoder cannot pass as hardware.
    private static Task<BufferedCommandResult> TestEncodeAsync(string encoder, string pixelFormat, string[] options, CancellationToken cancellationToken,
        bool warnings = false) =>
        ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-loglevel", warnings ? "warning" : "error", "-f", "lavfi", "-i", "color=size=640x360:rate=30",
                "-frames:v", "3", "-pix_fmt", pixelFormat, "-c:v", encoder, .. options,
                .. MediaFoundationEncoder.Is(encoder) ? MediaFoundationEncoder.HardwareOnly : [], "-f", "null", "-"])
            .WithValidation(CommandResultValidation.None), cancellationToken);

    private static string FirstLine(BufferedCommandResult result) =>
        result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^\[[^\]]+\]\s*", ""))
            .FirstOrDefault(line => line.Length > 0) ?? $"exit code {result.ExitCode}";

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => "    " + line));

    // To the log file, and to the debugger's output window while one is attached.
    private static void Log(string message)
    {
        System.Diagnostics.Trace.WriteLine($"[EncoderProber] {message}");
        AppLog.Write(message);
    }
}
