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

        var listing = await Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-encoders"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        var built = HardwareEncoders.Where(name => System.Text.RegularExpressions.Regex.IsMatch(listing.StandardOutput, $@"^\s*V\S*\s+{name}\s", System.Text.RegularExpressions.RegexOptions.Multiline)).ToList();

        // The listing could not be read at all: fall back on trying every one.
        if (listing.ExitCode != 0 || listing.StandardOutput.Length == 0)
            built = [.. HardwareEncoders];

        foreach (var encoder in built)
        {
            // A real frame size and rate, in the pixel format the encodes use: NVENC and AMF reject frames
            // below their minimum size, which would make a working GPU look unsupported.
            var result = await Cli.Wrap(DependencyUpdater.FfmpegPath)
                .WithArguments(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=640x360:rate=30",
                    "-frames:v", "3", "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

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
}
