using System.IO;
using CliWrap;

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

    /// <summary>Returns the hardware encoders that completed a one-frame test encode.</summary>
    public static async Task<List<string>> ProbeAsync(CancellationToken cancellationToken)
    {
        var supported = new List<string>();
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return supported;

        foreach (var encoder in HardwareEncoders)
        {
            // 256x256 rather than something smaller: NVENC and AMF reject frames below their
            // minimum size, which would make a working GPU look unsupported.
            var result = await Cli.Wrap(DependencyUpdater.FfmpegPath)
                .WithArguments(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=256x256:rate=1",
                    "-frames:v", "1", "-c:v", encoder, "-f", "null", "-"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);

            if (result.ExitCode == 0)
                supported.Add(encoder);
        }

        return supported;
    }
}
