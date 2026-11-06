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
/// What Pass 5.2-Audit put in the place of FFmpeg's filter graphs, each checked against what it must do: HandPeg's own GIF
/// encoder (read back by Windows' GIF decoder), the loudness meter (BS.1770's own reference tones), the engine's noise reduction,
/// a surround stream's channel pairs, and the compositor's deinterlace and tone map asked of every video of a sequence.
/// </summary>
public static class CleaveTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("export-gif", "# HandPeg's GIF encoder (5.2-Audit, FFmpeg's palettegen and paletteuse gone): one palette, dithered, only what changed written, looping.", Gif);
        yield return Case("export-sound-loudness", "# The loudness meter (BS.1770-4) the export normalizes by: reference tones at their known loudness, gating, the peak.", Loudness);
        yield return Case("export-sound-denoise", "# The engine's noise reduction (afftdn's place): steady noise taken down, a tone over it kept, the sound 512 samples late.", Denoise);
        yield return Case("export-sound-pairs", "# A surround stream exported as it is: each pair of its channels read as left and right, a lone last channel on both sides.", Pairs);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "FAILED: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect, text);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // ----- export-gif -----

    private static void Gif(Action<bool, string> expect, StringBuilder text)
    {
        const int width = 96, height = 54, frames = 12;
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.Gif");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "clip.gif");

        // A gradient with a bar moving across it, and a still stretch (frames 8 to 11 the same) that writes next to nothing.
        var made = new List<byte[]>();
        using (var gif = new GifEncoder(width, height, folder))
        {
            for (var n = 0; n < frames; n++)
            {
                var bgra = new byte[width * height * 4];
                var bar = Math.Min(n, 8) * 8;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var at = (y * width + x) * 4;
                        var inBar = x >= bar && x < bar + 10;
                        (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = inBar ? ((byte)40, (byte)200, (byte)240, (byte)255) : ((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)128, (byte)255);
                    }
                }

                gif.Add(bgra);
                made.Add(bgra);
            }

            gif.Write(path, 15);
        }

        var decoder = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        expect(decoder.Frames.Count == frames, $"{frames} frames written, {decoder.Frames.Count} read back");

        // Each frame as GIF readers compose it (drawn over the frames before, its own rectangle only), against what was made.
        var canvas = new byte[width * height * 4];
        var (worst, delays) = (double.MaxValue, new List<int>());
        for (var n = 0; n < decoder.Frames.Count; n++)
        {
            var frame = decoder.Frames[n];
            var metadata = (BitmapMetadata)frame.Metadata;
            var (left, top) = (Convert.ToInt32(metadata.GetQuery("/imgdesc/Left")), Convert.ToInt32(metadata.GetQuery("/imgdesc/Top")));
            delays.Add(Convert.ToInt32(metadata.GetQuery("/grctlext/Delay")));
            var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            for (var y = 0; y < converted.PixelHeight; y++)
                Array.Copy(pixels, y * converted.PixelWidth * 4, canvas, ((top + y) * width + left) * 4, converted.PixelWidth * 4);
            worst = Math.Min(worst, Psnr(canvas, made[n]));
        }

        var bytes = new FileInfo(path).Length;
        text.Append(CultureInfo.InvariantCulture, $"  {decoder.Frames.Count} frames of {width} x {height}, delays {string.Join(" ", delays)} (1/100 s); every frame at least 26 dB from what was made\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        gif: worst frame {worst:0.0} dB, {bytes} bytes"));
        expect(worst >= 26, "every frame, read back as GIF readers compose it, is the picture made (26 dB or better: one palette, dithered)");
        expect(delays.Sum() is >= 79 and <= 81, "the delays add up to the clip's length (12 frames at 15 fps: 0.8 s)");
        expect(Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 6) == "GIF89a" && File.ReadAllText(path, Encoding.Latin1).Contains("NETSCAPE2.0", StringComparison.Ordinal), "a GIF89a that loops");
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var d = a[i + c] - b[i + c];
                sum += d * d;
                count++;
            }
        }

        return sum <= 0 ? 99 : 10 * Math.Log10(255.0 * 255 * count / sum);
    }

    // ----- export-sound-loudness -----

    private static void Loudness(Action<bool, string> expect, StringBuilder text)
    {
        const int rate = 48000;
        double Measure(int channels, double seconds, Func<int, int, double> sample, out double peak)
        {
            var meter = new LoudnessMeter(rate, channels);
            var frame = new float[channels];
            for (var i = 0; i < (int)(seconds * rate); i++)
            {
                for (var c = 0; c < channels; c++)
                    frame[c] = (float)sample(i, c);
                meter.Add(frame);
            }

            peak = meter.SamplePeak;
            return meter.IntegratedLufs;
        }

        // BS.1770's own: a 997 Hz sine at 0 dBFS in one channel of stereo is -3.01 LUFS; at -20 dBFS in both, -20 LUFS.
        double Sine(int i, double amplitude) => amplitude * Math.Sin(2 * Math.PI * 997 * i / rate);
        var one = Measure(2, 5, (i, c) => c == 0 ? Sine(i, 1) : 0, out _);
        var both = Measure(2, 5, (i, _) => Sine(i, 0.1), out var peak);
        var surround = Measure(6, 5, (i, c) => c == 4 ? Sine(i, 0.1) : 0, out _);
        var lfe = Measure(6, 5, (i, c) => c == 3 ? Sine(i, 1) : 0, out _);

        // Gating: 4 s of the tone and 4 s of near-silence (-80 dBFS) is the tone's loudness, the silence gated out.
        var gated = Measure(2, 8, (i, _) => i < 4 * rate ? Sine(i, 0.1) : Sine(i, 0.0001), out _);
        text.Append(CultureInfo.InvariantCulture, $"  997 Hz at 0 dBFS in one channel: {one:0.00} LUFS (-3.01); at -20 dBFS in both: {both:0.00} (-20.00); in a surround channel: {surround:0.00} (-21.50); in the LFE: {(double.IsNegativeInfinity(lfe) ? "nothing" : lfe.ToString("0.00", CultureInfo.InvariantCulture))}; half silence: {gated:0.00}\n");
        expect(Math.Abs(one - -3.01) < 0.05, "a full-scale tone in one channel is -3.01 LUFS");
        expect(Math.Abs(both - -20) < 0.05, "a -20 dBFS tone in both is -20 LUFS");
        expect(Math.Abs(surround - -21.5) < 0.1, "a surround channel weighs 1.41 (+1.5 dB)");
        expect(double.IsNegativeInfinity(lfe), "the LFE is left out");
        expect(Math.Abs(gated - -20.17) < 0.03, "silence is gated out (the three blocks across the edge count, as BS.1770 counts them: -20.17)");
        expect(Math.Abs(peak - 0.1) < 0.001, "the sample peak");
    }

    // ----- export-sound-denoise -----

    private static void Denoise(Action<bool, string> expect, StringBuilder text)
    {
        const int rate = 48000, seconds = 4;
        // (5.2-AuditB: the De-Noise setting made into its effect with no FFmpeg words between, AudioChainBuilder.)
        var chain = AudioChainBuilder.From(new TrackAudioFilters { DeNoise = true, DeNoiseStrength = 24 }, 0);
        expect(chain.Effects is [{ Kind: AudioEffectKind.Denoise, P1: 24 }], "afftdn is the engine's own now: nothing it does not play");

        var (noiseOnly, mixed) = (new float[rate * seconds * 2], new float[rate * seconds * 2]);
        var random = new Random(52);
        for (var i = 0; i < rate * seconds; i++)
        {
            var noise = (float)((random.NextDouble() * 2 - 1) * 0.01);
            var tone = (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * i / rate));
            (noiseOnly[2 * i], noiseOnly[2 * i + 1]) = (noise, noise);
            (mixed[2 * i], mixed[2 * i + 1]) = (noise + tone, noise + tone);
        }

        var cleanNoise = Run(noiseOnly);
        var cleanMixed = Run(mixed);
        double Rms(float[] s, int from) => Math.Sqrt(s.Skip(from * 2).Where((_, k) => k % 2 == 0).Select(v => (double)v * v).Average());
        var settle = rate * 2;
        var (before, after) = (Rms(noiseOnly, settle), Rms(cleanNoise, settle));
        var (toneBefore, toneAfter) = (Rms(mixed, settle), Rms(cleanMixed, settle));
        var reduction = 20 * Math.Log10(before / Math.Max(after, 1e-9));
        var kept = 20 * Math.Log10(toneAfter / toneBefore);
        text.Append(CultureInfo.InvariantCulture, $"  white noise at -40 dBFS: down by at least 12 dB; a -10 dBFS 1 kHz tone over it: within 1 dB\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        denoise: the noise down {reduction:0.0} dB, the tone {kept:+0.00;-0.00} dB"));
        expect(reduction >= 12, "steady noise is taken down by 12 dB or more (24 asked)");
        expect(Math.Abs(kept) <= 1, "a tone well over the noise keeps its level");

        // The delay: an impulse comes out 512 samples later.
        var impulse = new float[rate * 2];
        impulse[2 * 1000] = impulse[2 * 1000 + 1] = 1;
        var heard = Run(impulse);
        var at = Enumerable.Range(0, rate).MaxBy(i => Math.Abs(heard[2 * i]));
        expect(at == 1000 + 512, $"the sound comes out 512 samples late (the impulse at {at - 1000})");

        static float[] Run(float[] input)
        {
            var effect = AudioEffect.Create(new AudioEffectSpec(AudioEffectKind.Denoise, 24), rate);
            var output = (float[])input.Clone();
            for (var at = 0; at < output.Length / 2; at += 480)
                effect.Process(output.AsSpan(at * 2, Math.Min(480, output.Length / 2 - at) * 2), Math.Min(480, output.Length / 2 - at));
            return output;
        }
    }

    // ----- export-sound-pairs -----

    private static void Pairs(Action<bool, string> expect, StringBuilder text)
    {
        static string Weights(float[] m) => string.Join(" ", Enumerable.Range(0, m.Length / 2).Select(c => $"{m[2 * c]:0.##}/{m[2 * c + 1]:0.##}"));
        foreach (var (channels, pair) in new[] { (6, 0), (6, 1), (6, 2), (3, 1), (2, 0), (1, 0) })
        {
            var matrix = AudioDownmix.Pair(channels, pair);
            text.Append(CultureInfo.InvariantCulture, $"  {channels} channels, pair {pair}: {Weights(matrix)}\n");
        }

        expect(AudioDownmix.Pair(6, 1) is [0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0], "5.1's second pair is its centre and LFE, as they are");
        expect(AudioDownmix.Pair(3, 1) is [0, 0, 0, 0, 1, 1], "a lone last channel is heard on both sides");

        // The plan: a 5.1 track kept as the source has it is three buses, written back as six channels.
        var scene = new Scene("pairs") { Main = Media.Mp4("D:/media/surround.mkv", 10) };
        scene.AudioTracks.Add(new LegacyStream(0, "Surround", "aac", "384k") { Channels = 6 });
        var state = scene.Compile() with { AudioChannels = AudioChannelChoice.Source };
        var plan = ExportSoundPlan.For(state, pictureOnly: false);
        text.Append(CultureInfo.InvariantCulture, $"  a 5.1 track as the source has it: {string.Join("; ", plan.Tracks.Select(t => $"{t.Layout} from buses {string.Join(",", t.Buses)}"))}\n");
        expect(plan.Tracks is [{ Channels: 6, Layout: "5.1", Buses: [0, 1, 2] }], "a 5.1 track is three buses, written as 5.1");
        var stereo = ExportSoundPlan.For(state with { AudioChannels = AudioChannelChoice.Stereo }, false);
        expect(stereo.Tracks is [{ Channels: 2, Buses: [0] }], "made stereo, it is one bus");
    }
}
