using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// gpu-export-formats (Pass 5.2-Audit): what only FFmpeg's graph used to make, made the new way end to end, with a real worker and
/// HandPeg's FFmpeg: an H.264 MP4 whose two sound tracks are kept apart and loudness-normalized (each at -16 LUFS, the engine's
/// render, FFmpeg only encoding it), a GIF (HandPeg's own encoder: every frame there), and a WebP (where this FFmpeg has libwebp).
/// </summary>
public static partial class GpuExportTests
{
    public static GraphResult Formats()
    {
        var text = new StringBuilder("# Exports FFmpeg's graph alone used to make (5.2-Audit): two sound tracks kept apart and normalized, a GIF, a WebP.\n");
        var problems = new List<string>();
        try
        {
            text.Append("two tracks kept apart, normalized: an MP4 with two AAC streams, each -16 LUFS (within 0.6 LU)\n");
            text.Append("a GIF: every frame of the export, looping\n");
            text.Append("a WebP: the export's frames (where this FFmpeg has libwebp)\n");
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
            {
                Console.WriteLine("  note  no FFmpeg found (HANDPEG_FFMPEG, or %LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg or ffmpeg-arm64): the export formats were not made");
                return new GraphResult("gpu-export-formats", text.ToString(), problems, 0);
            }

            var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.Formats");
            Directory.CreateDirectory(folder);
            var media = MakeMedia(ffmpeg, folder);

            // A main file with two sound streams (a quiet 440 Hz and a louder 880 Hz), as a recorder's game and microphone.
            var two = Path.Combine(folder, "two-streams.mp4");
            FfmpegOnce(ffmpeg, $"-f lavfi -i \"testsrc2=s={W}x{H}:r=30:d=5\" -f lavfi -i \"sine=frequency=440:sample_rate=48000:duration=5\" -f lavfi -i \"sine=frequency=880:sample_rate=48000:duration=5\" "
                               + "-map 0:v -map 1:a -map 2:a -filter:a:1 volume=6dB -c:v libx264 -preset veryfast -crf 18 -c:a aac -b:a 192k -ac 2 -y \"" + two + "\"");
            var files = new Files(two, media.Inset);

            var sound = State(SingleClip(files), files, Path.Combine(folder, "two-tracks.mp4"), Stream("aac", "160k"), Stream("aac", "160k") with { Index = 1, Title = "Mic" }) with
            {
                FfmpegExecutable = $"\"{ffmpeg}\"",
                VideoCodecArguments = "-c:v libx264 -preset veryfast -crf 23",
                MergeAudioTracks = false,
                NormalizeAudio = true,
                MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [two] = 0 },
            };
            Export(sound);
            for (var stream = 0; stream < 2; stream++)
            {
                var pcm = Read(ffmpeg, sound.OutputPath, $"-map 0:a:{stream} -f s16le -ac 2 -ar 48000 -");
                var meter = new LoudnessMeter(48000, 2);
                for (var i = 0; i + 3 < pcm.Length; i += 4)
                    meter.Add([BitConverter.ToInt16(pcm, i) / 32768f, BitConverter.ToInt16(pcm, i + 2) / 32768f]);
                var lufs = meter.IntegratedLufs;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        two tracks: stream {stream} at {lufs:0.00} LUFS"));
                if (pcm.Length == 0)
                    problems.Add($"the export has no sound stream {stream}");
                else if (Math.Abs(lufs - ExportSoundPlan.TargetLufs) > 0.6)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"sound stream {stream} is {lufs:0.00} LUFS, not {ExportSoundPlan.TargetLufs}"));
            }

            // A GIF: HandPeg's own encoder.
            var gif = State(Timeline(media), media, Path.Combine(folder, "clip.gif")) with
            {
                FfmpegExecutable = $"\"{ffmpeg}\"", IsAnimatedOutput = true, TargetFramerate = "10",
            };
            Export(gif);
            var decoded = new GifBitmapDecoder(new Uri(gif.OutputPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frames = EncoderCommand.Timing(gif)!.Value.OutputFrames;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        gif: {decoded.Frames.Count} frames, {new FileInfo(gif.OutputPath).Length / 1024.0:0} KB"));
            if (decoded.Frames.Count != frames)
                problems.Add($"the GIF has {decoded.Frames.Count} frames, its timing {frames}");

            // A WebP, where this FFmpeg can write one.
            if (HasEncoder(ffmpeg, "libwebp"))
            {
                var webp = gif with { OutputPath = Path.Combine(folder, "clip.webp") };
                Export(webp);
                if (!File.Exists(webp.OutputPath) || new FileInfo(webp.OutputPath).Length < 1000)
                    problems.Add("the WebP was not written");
            }
            else
            {
                Console.WriteLine("  note  this FFmpeg has no libwebp: no WebP was made");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-export-formats", text.ToString(), problems, 0);
    }

    private static bool HasEncoder(string ffmpeg, string name)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -encoders") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var list = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return list.Contains(" " + name + " ", StringComparison.Ordinal);
    }
}
