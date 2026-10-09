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

        foreach (var encoder in built)
        {
            // A real frame size and rate, in the pixel format the encodes use: NVENC and AMF reject frames
            // below their minimum size, which would make a working GPU look unsupported.
            var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                .WithArguments(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=640x360:rate=30",
                    "-frames:v", "3", "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-"])
                .WithValidation(CommandResultValidation.None), cancellationToken);

            if (result.ExitCode == 0)
            {
                supported.Add(encoder);
                continue;
            }

            // Why it would not start, in FFmpeg's own words: an outdated driver, no such card, a busy one.
            var reason = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^\[[^\]]+\]\s*", ""))
                .FirstOrDefault(line => line.Length > 0) ?? $"exit code {result.ExitCode}";
            AppLog.Write($"Hardware encoder {encoder} is in FFmpeg but did not start: {reason}");

            // Said once per vendor, and only for the vendor's H.264 encoder: the others fail for the same reason.
            if (encoder.StartsWith("h264_", StringComparison.Ordinal))
                problems.Add($"{encoder}: {reason}");
        }

        return (supported, problems);
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
                _openClDevice = device;
                return OpenClBlurAvailable = true;
            }

            reason = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {result.ExitCode}";

            // Only a machine with several devices is worth asking again, one device at a time.
            if (!reason.Contains("More than one", StringComparison.OrdinalIgnoreCase) && device == OpenClDevices[0])
                break;
        }

        AppLog.Write($"The OpenCL blur is not available, and the processor blurs instead: {reason}");
        return OpenClBlurAvailable = false;
    }
}
