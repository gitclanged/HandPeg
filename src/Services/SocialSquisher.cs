using System.Globalization;
using System.IO;
using System.Windows.Media;
using CliWrap;

namespace HandPegApp.Services;

/// <summary>
/// Target size preset for a destination's upload limit, e.g. "Discord (10 MB)" (free-tier limit; legacy untouched "Discord
/// (Non-Nitro)" 20 MB entries are migrated).
/// </summary>
public sealed class SquishPreset
{
    public string Name { get; set; } = "Preset";

    /// <summary>The largest the file may be, in megabytes.</summary>
    public double TargetSizeMb { get; set; } = 20;

    /// <summary>What the sound is given, in kilobits a second (AAC).</summary>
    public int AudioKbps { get; set; } = 96;

    /// <summary>Which of the icons it is drawn with on the startup dialog: one of <see cref="Icons"/>.</summary>
    public string Icon { get; set; } = "squish";

    public string SizeText => string.Create(CultureInfo.InvariantCulture, $"under {TargetSizeMb:0.#} MB");

    public SquishPreset Clone() => new() { Name = Name, TargetSizeMb = TargetSizeMb, AudioKbps = AudioKbps, Icon = Icon };

    // Icons: simple 26-unit path geometry for the destination type (chat bubble, envelope, code brackets), never brand logos.
    private static readonly Dictionary<string, string> IconPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        // Arrows pressing in on a square.
        ["squish"] = "M 9,9 H 17 V 17 H 9 Z M 1,13 H 6 M 4,11 L 6,13 L 4,15 M 25,13 H 20 M 22,11 L 20,13 L 22,15 M 13,1 V 6 M 11,4 L 13,6 L 15,4 M 13,25 V 20 M 11,22 L 13,20 L 15,22",
        // A game pad's face: a chat among players.
        ["gamepad"] = "M 5,8 Q 13,4 21,8 L 24,19 Q 20,22 17,19 L 16,17 H 10 L 9,19 Q 6,22 2,19 Z M 8,13 A 1.5,1.5 0 1 0 11,13 A 1.5,1.5 0 1 0 8,13 Z M 15,13 A 1.5,1.5 0 1 0 18,13 A 1.5,1.5 0 1 0 15,13 Z",
        // A round speech bubble with a tail.
        ["bubble"] = "M 13,3 A 10,10 0 1 1 6.5,20.6 L 3,23 L 4.4,18.2 A 10,10 0 0 1 13,3 Z M 9,11 H 17 M 9,15 H 14",
        // An envelope.
        ["mail"] = "M 3,6 H 23 V 20 H 3 Z M 3,6 L 13,14 L 23,6",
        // Two strokes crossed.
        ["cross"] = "M 5,5 L 21,21 M 21,5 L 5,21",
        // Code brackets.
        ["code"] = "M 9,7 L 3,13 L 9,19 M 17,7 L 23,13 L 17,19 M 15,5 L 11,21",
        // A card with a T, and a person beside it.
        ["team"] = "M 3,8 H 15 V 20 H 3 Z M 6,11 H 12 M 9,11 V 17 M 20,10 A 2.6,2.6 0 1 0 19.9,10 Z M 17,13 H 23 V 18 A 3.5,3.5 0 0 1 17,20",
        // A circle inside a broken ring: a private line.
        ["ring"] = "M 13,2 A 11,11 0 0 1 24,13 M 13,24 A 11,11 0 0 1 2,13 M 20,13 A 7,7 0 1 0 6,13 A 7,7 0 1 0 20,13 Z",
        // Two wings.
        ["wings"] = "M 13,12 C 10,5 4,3 3,6 C 2,10 5,14 9,14 C 5,15 5,20 9,21 C 11,21 13,17 13,15 C 13,17 15,21 17,21 C 21,20 21,15 17,14 C 21,14 24,10 23,6 C 22,3 16,5 13,12 Z",
        // A play triangle in a frame.
        ["play"] = "M 3,5 H 23 V 21 H 3 Z M 10,9 L 17,13 L 10,17 Z",
    };

    /// <summary>The names an icon can be chosen by.</summary>
    public static IReadOnlyList<string> Icons { get; } = [.. IconPaths.Keys];

    /// <summary>The icon as path geometry, for a Path's Data.</summary>
    public string IconData => IconPaths.GetValueOrDefault(Icon ?? "", IconPaths["squish"]);

    /// <summary>
    /// Default presets for common sharing destinations with their max file size. All output H.264/AAC .mp4 (plays inline everywhere).
    /// Limits reflect known upload caps at authoring time and are editable under Settings > Startup Dialog.
    /// </summary>
    public static List<SquishPreset> Defaults() =>
    [
        new() { Name = "Discord (10 MB)", TargetSizeMb = 10, AudioKbps = 96, Icon = "gamepad" },
        new() { Name = "Discord (Nitro Basic)", TargetSizeMb = 50, AudioKbps = 128, Icon = "gamepad" },
        new() { Name = "Discord (Nitro)", TargetSizeMb = 500, AudioKbps = 160, Icon = "gamepad" },
        new() { Name = "WhatsApp", TargetSizeMb = 16, AudioKbps = 96, Icon = "bubble" },
        new() { Name = "Email (Gmail / Outlook)", TargetSizeMb = 18, AudioKbps = 96, Icon = "mail" },
        new() { Name = "GitHub (Issue / PR)", TargetSizeMb = 10, AudioKbps = 64, Icon = "code" },
        new() { Name = "X / Twitter", TargetSizeMb = 512, AudioKbps = 160, Icon = "cross" },
        new() { Name = "Bluesky", TargetSizeMb = 100, AudioKbps = 128, Icon = "wings" },
        new() { Name = "Microsoft Teams", TargetSizeMb = 250, AudioKbps = 128, Icon = "team" },
        new() { Name = "Signal", TargetSizeMb = 100, AudioKbps = 128, Icon = "ring" },
    ];
}

/// <summary>What a squish ended as.</summary>
public sealed record SquishResult(string Path, long Bytes, string Encoder, int VideoKbps, int Attempts);

/// <summary>
/// Social Sharing Squisher: encodes one video to an H.264/AAC .mp4 under a target size. Size sets the bitrate (total bits minus margin,
/// over duration, minus audio), bitrate sets the resolution, and the slowest preset maximizes detail. Prefers hardware (NVENC, QSV,
/// AMF, then MF for ARM64), else libx264. Oversized results are re-encoded with the bitrate reduced proportionally.
/// </summary>
public static class SocialSquisher
{
    /// <summary>The encoders, best first for this job.</summary>
    private static readonly string[] Hierarchy = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264"];

    private const int MostAttempts = 5;

    /// <summary>
    /// Video bitrate (kbps): ((target - offset) MB x 8192 / seconds) - audio kbps.
    /// </summary>
    public static int VideoKbps(double targetMb, double offsetMb, double seconds, int audioKbps) =>
        (int)Math.Max(Math.Floor((targetMb - offsetMb) * 8192 / Math.Max(seconds, 0.1) - audioKbps), 40);

    /// <summary>Encoder fallback order: available hardware encoders by priority, then libx264.</summary>
    public static List<string> ChooseEncoders(IReadOnlyCollection<string> available) =>
        Hierarchy.Where(name => name == "libx264" || available.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>How tall a picture the bitrate can carry: there is no use in 1080 lines of blocks.</summary>
    private static int HeightFor(int videoKbps) => videoKbps switch
    {
        >= 2500 => 1080,
        >= 1200 => 720,
        >= 500 => 480,
        _ => 360,
    };

    /// <summary>The encoder's own options: its most careful preset, or the one named in the settings.</summary>
    private static string EncoderOptions(string encoder, string? presetOverride)
    {
        // "veryslow" (default override) maps to each encoder's slowest step (NVENC/AMF lack that name).
        var preset = string.IsNullOrWhiteSpace(presetOverride) ? null : presetOverride.Trim();
        if (string.Equals(preset, "veryslow", StringComparison.OrdinalIgnoreCase) && encoder is "h264_nvenc" or "h264_amf")
            preset = null;
        return encoder switch
        {
            "h264_nvenc" => $"-preset {preset ?? "p7"} -tune hq -rc vbr -multipass fullres",
            "h264_qsv" => $"-preset {preset ?? "veryslow"}",
            "h264_amf" => $"-quality {preset ?? "high_quality"} -rc vbr_peak",
            // MF has no preset step: peak-constrained VBR capped by the following -maxrate, slowest quality, hardware only.
            "h264_mf" => $"-rate_control pc_vbr {MediaFoundationEncoder.SlowestStepArgument} {MediaFoundationEncoder.HardwareOnlyArgument}",
            _ => $"-preset {preset ?? "veryslow"}",
        };
    }

    /// <summary>Still frame at 20 % of duration; null on failure.</summary>
    public static async Task<ImageSource?> ExtractThumbnailAsync(string input, string imagePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return null;

        var info = await MediaProbe.ProbeAsync(input, cancellationToken);
        var at = Math.Max((info?.DurationSeconds ?? 0) * 0.2, 0).ToString("0.###", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-loglevel", "error", "-y", "-ss", at, "-i", input, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", imagePath])
            .WithValidation(CommandResultValidation.None), cancellationToken);
        return result.ExitCode == 0 && File.Exists(imagePath) ? Waveforms.Load(imagePath) : null;
    }

    /// <summary>
    /// Encodes under the preset size; throws InvalidOperationException with the reason on failure. Foreground work (WorkScheduler), shown
    /// in the progress chip and the Squisher window.
    /// </summary>
    public static Task<SquishResult> SquishAsync(
        string input, SquishPreset preset, double offsetMb, IReadOnlyCollection<string> availableEncoders, string? presetOverride,
        string outputPath, IProgress<double> progress, CancellationToken cancellationToken) =>
        WorkScheduler.Run(new WorkSpec($"Squishing {Path.GetFileName(input)}", WorkLane.Foreground), work =>
            SquishNowAsync(input, preset, offsetMb, availableEncoders, presetOverride, outputPath, new Progress<double>(fraction =>
            {
                progress.Report(fraction);
                work.Report(fraction);
            }), work.Token), cancellationToken);

    /// <summary>Encodes under the preset size; throws InvalidOperationException with the reason on failure.</summary>
    /// <param name="offsetMb">Safety margin (MB) below the target for container overhead and bitrate overshoot.</param>
    /// <param name="presetOverride">Encoder preset override; null or empty for the slowest.</param>
    /// <param name="progress">Current encode progress, 0 to 1.</param>
    private static async Task<SquishResult> SquishNowAsync(
        string input, SquishPreset preset, double offsetMb, IReadOnlyCollection<string> availableEncoders, string? presetOverride,
        string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (await MediaProbe.ProbeAsync(input, cancellationToken) is not { DurationSeconds: > 0.05 } info)
            throw new InvalidOperationException("The video's length could not be read, so there is no working out a bitrate for it.");

        var limit = (long)(preset.TargetSizeMb * 1024 * 1024);
        var audioKbps = info.Audio.Count > 0 ? Math.Clamp(preset.AudioKbps, 16, 320) : 0;
        var videoKbps = VideoKbps(preset.TargetSizeMb, Math.Clamp(offsetMb, 0, preset.TargetSizeMb * 0.9), info.DurationSeconds, audioKbps);
        var encoders = ChooseEncoders(availableEncoders);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var report = new Progress<FfmpegProgress>(p =>
        {
            if (p.Position is { } position)
                progress.Report(Math.Clamp(position.TotalSeconds / info.DurationSeconds, 0, 1));
        });

        string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        for (var attempt = 1; ; attempt++)
        {
            var encoder = encoders[0];
            var sound = audioKbps > 0 ? $"-map 0:a:0 -c:a aac -b:a {Number(audioKbps)}k -ac 2" : "-an";
            var command = $"ffmpeg -hide_banner -y -i \"{input}\" -map 0:v:0 {sound} -vf \"scale=-2:'min({Number(HeightFor(videoKbps))},ih)'\" "
                          + $"-c:v {encoder} {EncoderOptions(encoder, presetOverride)} -b:v {Number(videoKbps)}k -maxrate {Number(videoKbps)}k -bufsize {Number(videoKbps * 2)}k "
                          + $"-pix_fmt {(MediaFoundationEncoder.Is(encoder) ? MediaFoundationEncoder.PixelFormat(false) : "yuv420p")} -movflags +faststart \"{outputPath}\"";
            AppLog.Write($"Squisher, attempt {attempt}: {encoder} at {videoKbps} kb/s for {preset.Name} ({preset.TargetSizeMb.ToString("0.#", CultureInfo.InvariantCulture)} MB)");
            try
            {
                progress.Report(0);
                await FfmpegRunner.RunAsync(command, report, cancellationToken);
            }
            catch (InvalidOperationException ex) when (encoders.Count > 1)
            {
                // Encoder failed (unsupported options, old driver): fall back to the next.
                AppLog.Write($"Squisher: {encoder} failed and the next encoder is tried", ex);
                encoders.RemoveAt(0);
                attempt--;
                continue;
            }

            var bytes = new FileInfo(outputPath).Length;
            if (bytes <= limit)
                return new SquishResult(outputPath, bytes, encoder, videoKbps, attempt);
            if (attempt >= MostAttempts || videoKbps <= 40)
                throw new InvalidOperationException($"The video is still {bytes / 1048576.0:0.0} MB after {attempt} tries: it is too long for {preset.TargetSizeMb:0.#} MB.");

            // Over: the bitrate comes down by as much as it was over, and a little more.
            videoKbps = Math.Max((int)(videoKbps * ((double)limit / bytes) * 0.96), 40);
        }
    }
}
