using System.IO;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <summary>
/// Finds out which GPU encoders actually work on this machine. FFmpeg lists every encoder it was
/// built with, so the only reliable test is to encode a frame with each one.
/// </summary>
public static class EncoderProber
{
    public static IReadOnlyList<string> HardwareEncoders { get; } =
    [
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_amf", "hevc_amf", "av1_amf",
    ];

    /// <summary>
    /// The hardware encoders that work here, and for those that are in FFmpeg but do not, why not. Two steps:
    /// "ffmpeg -encoders" says which ones this FFmpeg was built with (h264_nvenc, hevc_nvenc and the rest are
    /// looked for by name), and each of those is then made to encode a few frames, because being built in
    /// says nothing about the graphics card or its driver.
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
            var result = await TestEncodeAsync(encoder, "yuv420p", [], cancellationToken);
            if (result.ExitCode == 0)
            {
                supported.Add(encoder);
                Log($"Hardware encoder {encoder} passed its test encode.");

                // What an NVENC encoder can do depends on the generation of the card, and asking for something
                // it cannot do stops the whole encode: each of these is asked for once, here, instead. Of AMF
                // and QuickSync only 10-bit is asked: their H.264 encoders have none, and older cards none at all.
                var isNvenc = encoder.EndsWith("_nvenc", StringComparison.Ordinal);
                foreach (var (feature, pixelFormat, options) in FeatureTests.Where(t => isNvenc || t.Feature == TenBit))
                {
                    // QuickSync has no 10-bit H.264, and its test would not say so: FFmpeg hands the encoder
                    // 8-bit frames instead of stopping.
                    if (encoder == "h264_qsv" && feature == TenBit)
                    {
                        refused.Add($"{encoder}:{feature}");
                        continue;
                    }

                    var test = await TestEncodeAsync(encoder, pixelFormat, options, cancellationToken);
                    if (test.ExitCode == 0)
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

            // Said once per vendor, and only for the vendor's H.264 encoder: the others fail for the same reason.
            if (encoder.StartsWith("h264_", StringComparison.Ordinal))
                problems.Add($"{encoder}: {reason}");
        }

        (_refused, NvencNeedsOlderFfmpeg) = (refused, driverTooOld);
        (_lastSupported, _lastProblems, _openClKnown) = (supported, problems, false);
        SaveResult();
        return (supported, problems);
    }

    // ----- The result, kept between launches -----
    // Every test encode loads a graphics driver: a burst of memory and processor at every start, for an answer
    // that only changes with FFmpeg or a driver. The answer is kept, with what it was found for (the FFmpeg
    // executable and each card's driver), and asked again only when one of those has changed, or from Settings.

    private sealed record SavedResult(string Key, List<string> Supported, List<string> Problems, List<string> Refused, bool NvencNeedsOlderFfmpeg,
        bool OpenClKnown, bool OpenClBlur, string OpenClDevice);

    // Raised when what the tests check changes, so that a result saved by an older HandPeg is not used.
    private const int TestsVersion = 1;

    private static List<string> _lastSupported = [], _lastProblems = [];
    private static bool _openClKnown;

    private static string SavedPath => Path.Combine(AppPaths.Cache, "encoder-probe.json");

    /// <summary>What the result was found for: this FFmpeg (where, how large, when written) and these cards with these drivers.</summary>
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
    /// Starts reading the saved result away from the calling thread, at launch, so that the window can have its encoders
    /// before its first frame. Only FFmpeg's part of what it was found for is compared then: listing the cards (DXGI) loads
    /// each card's driver, up to a quarter of a second while a card wakes up, and the window's thread, loading its own
    /// libraries meanwhile, waited for it (Pass 4.2C). The cards are compared after the first frame (<see cref="CardsStillMatch"/>).
    /// </summary>
    public static void LoadSavedResultEarly() => _early = Task.Run(() => LoadSaved(checkCards: false));

    /// <summary>
    /// After the first frame, away from the window's thread: whether a result taken at launch was found for these cards and
    /// drivers too. False when one has changed since: the hardware encoders are then probed again.
    /// </summary>
    public static bool CardsStillMatch() => Interlocked.Exchange(ref _cardsUnchecked, null) is not { } key || key == MachineKey();

    /// <summary>
    /// The saved result read at launch, once, when it is there within <paramref name="patience"/>; null when there is none
    /// for this FFmpeg and these drivers, or it is still being read (the probe then reads it as it always did).
    /// </summary>
    public static (List<string> Supported, List<string> Problems)? TakeEarlyResult(TimeSpan patience) =>
        Interlocked.Exchange(ref _early, null) is { } early && early.Wait(patience) ? early.Result : null;

    /// <summary>
    /// The result saved by an earlier probe for this FFmpeg and these drivers, put back in place (what each
    /// encoder can do, the OpenCL blur); null when there is none or it was found for something else.
    /// </summary>
    public static (List<string> Supported, List<string> Problems)? LoadSavedResult() => LoadSaved(checkCards: true);

    private static (List<string> Supported, List<string> Problems)? LoadSaved(bool checkCards)
    {
        try
        {
            if (!File.Exists(SavedPath) || System.Text.Json.JsonSerializer.Deserialize<SavedResult>(File.ReadAllText(SavedPath)) is not { } saved
                || !(checkCards ? saved.Key == MachineKey() : saved.Key.LastIndexOf('|') is var bar and >= 0 && saved.Key[..bar] == FfmpegKey()))
            {
                return null;
            }

            (_refused, NvencNeedsOlderFfmpeg) = ([.. saved.Refused], saved.NvencNeedsOlderFfmpeg);
            (_lastSupported, _lastProblems, _openClKnown) = (saved.Supported, saved.Problems, saved.OpenClKnown);
            if (saved.OpenClKnown)
                (OpenClBlurAvailable, _openClDevice) = (saved.OpenClBlur, OpenClDevices.Contains(saved.OpenClDevice) ? saved.OpenClDevice : OpenClDevices[0]);
            _cardsUnchecked = checkCards ? null : saved.Key;
            Log($"Hardware encoders as found before for this FFmpeg{(checkCards ? " and these drivers" : " (its cards compared after the first frame)")}: {(saved.Supported.Count > 0 ? string.Join(", ", saved.Supported) : "none")}.");
            return (saved.Supported, saved.Problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether the OpenCL blur's test has been made for the result in place, or has yet to be.</summary>
    public static bool OpenClKnown => _openClKnown;

    /// <summary>Forgets the saved result: the next probe tests every encoder again (Settings, "Check Hardware Again").</summary>
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
            var saved = new SavedResult(MachineKey(), _lastSupported, _lastProblems, [.. _refused], NvencNeedsOlderFfmpeg, _openClKnown, OpenClBlurAvailable, _openClDevice);
            File.WriteAllText(SavedPath, System.Text.Json.JsonSerializer.Serialize(saved));
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
    /// True when the last probe found NVENC stopped by the driver being older than this FFmpeg asks for. A card
    /// whose drivers have ended (the GTX 10 series) can only be given an older FFmpeg.
    /// </summary>
    public static bool NvencNeedsOlderFfmpeg { get; private set; }

    // A real frame size and rate, in a pixel format the encodes use: NVENC and AMF reject frames below their
    // minimum size, and NVENC ones in a format it was not told to expect, which would make a working GPU
    // look unsupported.
    private static Task<BufferedCommandResult> TestEncodeAsync(string encoder, string pixelFormat, string[] options, CancellationToken cancellationToken) =>
        ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=640x360:rate=30",
                "-frames:v", "3", "-pix_fmt", pixelFormat, "-c:v", encoder, .. options, "-f", "null", "-"])
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

    // The OpenCL devices to try, in order: whichever there is, and then, for a machine with more than one
    // (where FFmpeg will not choose), each of the first few by its platform and device number.
    private static readonly string[] OpenClDevices = ["opencl=ocl", "opencl=ocl:0.0", "opencl=ocl:1.0", "opencl=ocl:0.1"];

    // The one of them that worked here.
    private static string _openClDevice = OpenClDevices[0];

    /// <summary>The filter options that give a graph an OpenCL device to upload its frames to.</summary>
    public static string OpenClDeviceArguments => $"-init_hw_device {_openClDevice} -filter_hw_device ocl";

    /// <summary>Whether the background blur can be done on the graphics card here: set by <see cref="ProbeOpenClBlurAsync"/>.</summary>
    public static bool OpenClBlurAvailable { get; private set; }

    /// <summary>
    /// Finds out whether boxblur_opencl runs on this machine, the same way the encoders are tested: by blurring
    /// a frame with it. FFmpeg having the filter says nothing about there being an OpenCL driver to run it.
    /// </summary>
    public static async Task<bool> ProbeOpenClBlurAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return OpenClBlurAvailable = false;

        var reason = "";
        foreach (var device in OpenClDevices)
        {
            var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                .WithArguments(["-hide_banner", "-loglevel", "error", "-init_hw_device", device, "-filter_hw_device", "ocl",
                    "-f", "lavfi", "-i", "color=size=640x360:rate=30", "-frames:v", "2",
                    "-vf", "format=yuv420p,hwupload,boxblur_opencl=8:2,hwdownload,format=yuv420p", "-f", "null", "-"])
                .WithValidation(CommandResultValidation.None), cancellationToken);

            if (result.ExitCode == 0)
            {
                (_openClDevice, OpenClBlurAvailable, _openClKnown) = (device, true, true);
                SaveResult();
                return true;
            }

            reason = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {result.ExitCode}";

            // Only a machine with several devices is worth asking again, one device at a time.
            if (!reason.Contains("More than one", StringComparison.OrdinalIgnoreCase) && device == OpenClDevices[0])
                break;
        }

        AppLog.Write($"The OpenCL blur is not available, and the processor blurs instead: {reason}");
        (OpenClBlurAvailable, _openClKnown) = (false, true);
        SaveResult();
        return false;
    }
}
