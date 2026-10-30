using System.Globalization;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The Media Foundation hardware encoders (5.0, the user's directive 3c: the ARM64 path): the options an export gives them
/// (MediaFoundationEncoder), how they are offered (EncoderOption.FromHardwareName), and the software encoder that stands in
/// when one fails (HardwareFallback), its quality carried back to a CRF. Each answer is compared with what it should be as
/// well as with the golden file.
/// </summary>
public static class MediaFoundationTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("encoder-media-foundation",
            "# Media Foundation encoders (5.0): export options, the Video tab's names, and the software fallback's rewrite.", Options);
        yield return Case("encoder-media-foundation-coverage",
            "# Which Media Foundation encoders are offered (5.0-Verify Q3): only for a codec no vendor encoder here does.", Coverage);
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Expect(List<string> problems, string what, string actual, string expected)
    {
        if (actual != expected)
            problems.Add($"{what}: \"{actual}\", not \"{expected}\"");
    }

    /// <summary>
    /// The probe's walk (EncoderProber.ProbeAsync) over HardwareEncoders in their order, each machine saying which encoders
    /// would pass a test encode: a Media Foundation encoder a vendor encoder already covers is never tested, so never offered.
    /// </summary>
    private static void Coverage(StringBuilder text, List<string> problems)
    {
        (string Machine, string[] Passes, string[] Offered)[] machines =
        [
            ("x64, an RTX 30 card (NVENC H.264 and HEVC; Media Foundation reaches the same chip, no AV1 encoder behind it)",
                ["h264_nvenc", "hevc_nvenc", "h264_mf", "hevc_mf"], ["h264_nvenc", "hevc_nvenc"]),
            ("ARM64, a Snapdragon / Adreno PC (no vendor encoder at all)",
                ["h264_mf", "hevc_mf", "av1_mf"], ["h264_mf", "hevc_mf", "av1_mf"]),
            ("mixed: NVENC refused by its driver, AMF HEVC only",
                ["hevc_amf", "h264_mf", "hevc_mf"], ["hevc_amf", "h264_mf"]),
            ("x64, no hardware encoder passes", [], []),
        ];

        foreach (var (machine, passes, expected) in machines)
        {
            var supported = new List<string>();
            var notTested = new List<string>();
            foreach (var encoder in EncoderProber.HardwareEncoders)
            {
                if (EncoderProber.CoveringVendorEncoder(encoder, supported) is { } vendor)
                {
                    notTested.Add($"{encoder} (covered by {vendor})");
                    continue;
                }

                if (passes.Contains(encoder))
                    supported.Add(encoder);
            }

            Line(text, $"{machine}:");
            Line(text, $"  offered: {(supported.Count > 0 ? string.Join(", ", supported) : "none")}");
            Line(text, $"  not tested: {(notTested.Count > 0 ? string.Join(", ", notTested) : "none")}");
            Expect(problems, $"{machine}: offered", string.Join(", ", supported), string.Join(", ", expected));
        }

        // The rule on its own: only a Media Foundation encoder is ever covered, and only by a vendor's encoder of its codec.
        (string Encoder, string[] Supported, string? Expected)[] rules =
        [
            ("h264_mf", ["h264_nvenc"], "h264_nvenc"),
            ("hevc_mf", ["h264_nvenc", "hevc_qsv"], "hevc_qsv"),
            ("av1_mf", ["h264_nvenc", "hevc_nvenc"], null),
            ("h264_mf", ["h264_mf"], null),
            ("h264_nvenc", ["h264_qsv"], null),
        ];
        foreach (var (encoder, supported, expected) in rules)
        {
            var got = EncoderProber.CoveringVendorEncoder(encoder, supported);
            Line(text, $"{encoder} with [{string.Join(", ", supported)}]: {got ?? "not covered"}");
            Expect(problems, $"{encoder} with [{string.Join(", ", supported)}]", got ?? "not covered", expected ?? "not covered");
        }
    }

    private static void Options(StringBuilder text, List<string> problems)
    {
        // The slider's CRF as Media Foundation's quality: a straight line, never 0.
        foreach (var (crf, quality) in new[] { (0, 100), (18, 65), (23, 55), (28, 45), (51, 1) })
        {
            var got = MediaFoundationEncoder.QualityFromCrf(crf);
            Line(text, $"crf {crf} is quality {got}");
            if (got != quality)
                problems.Add($"crf {crf} gave quality {got}, not {quality}");
        }

        (string What, string Actual, string Expected)[] exports =
        [
            ("constant bitrate", MediaFoundationEncoder.ExportArguments(true, true, 8000, 23), "-rate_control cbr -b:v 8000k -maxrate 8000k -bufsize 16000k -hw_encoding 1"),
            ("average bitrate", MediaFoundationEncoder.ExportArguments(true, false, 8000, 23), "-rate_control u_vbr -b:v 8000k -hw_encoding 1"),
            ("constant quality", MediaFoundationEncoder.ExportArguments(false, false, 0, 23), "-rate_control quality -quality 55 -hw_encoding 1"),
        ];
        foreach (var (what, actual, expected) in exports)
        {
            Line(text, $"{what}: {actual}");
            Expect(problems, what, actual, expected);
        }

        foreach (var name in MediaFoundationEncoder.Names)
        {
            var option = EncoderOption.FromHardwareName(name);
            Line(text, $"{name}: \"{option.DisplayName}\", {option.Family}, hardware {option.IsHardware}, falls back to {HardwareFallback.GetSoftwareEncoder(name) ?? "nothing"}");
            if (option.Family != EncoderFamily.MediaFoundation || !option.IsHardware || !MediaFoundationEncoder.Is(name))
                problems.Add($"{name} is not offered as a Media Foundation hardware encoder");
        }

        (string Command, string Expected)[] fallbacks =
        [
            ("ffmpeg -i in.mp4 -c:v hevc_mf -rate_control quality -quality 55 -hw_encoding 1 -pix_fmt yuv420p out.mp4",
                "ffmpeg -i in.mp4 -c:v libx265 -preset medium -crf 23 -pix_fmt yuv420p out.mp4"),
            ("ffmpeg -i in.mp4 -c:v h264_mf -rate_control cbr -b:v 8000k -maxrate 8000k -bufsize 16000k -hw_encoding 1 out.mp4",
                "ffmpeg -i in.mp4 -c:v libx264 -preset medium -b:v 8000k -maxrate 8000k -bufsize 16000k out.mp4"),
        ];
        foreach (var (command, expected) in fallbacks)
        {
            if (!HardwareFallback.TryRewrite(command, out var rewritten, out var hardware, out var software))
            {
                problems.Add($"not rewritten: {command}");
                continue;
            }

            Line(text, $"{hardware} -> {software}: {rewritten}");
            Expect(problems, $"the fallback of {hardware}", rewritten, expected);
        }
    }
}
