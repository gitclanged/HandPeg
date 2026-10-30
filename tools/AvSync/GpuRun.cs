using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// A few seconds of the sequence from one moment, as the GPU engine makes them, written into one file on the sequence's
/// clock (Matroska, the picture lossless): what "test" measures.
/// </summary>
/// <param name="Size">The output's size: the canvas scaled to at most --size pixels on its long side (the worker stretches the canvas to it).</param>
/// <param name="Scale">Output pixels a canvas pixel: where a layer's box (canvas pixels) is in the output.</param>
/// <param name="FirstFrameMs">From asking for the first exact frame to having it read back (Instant Seek's cold seek), ms.</param>
internal sealed record GpuRun(string Output, double Fps, (int Width, int Height) Size, double Scale, long FirstFrameMs, string? Error);

/// <summary>
/// A compositor worker of the harness's own (this program started again with --compositor-worker, as the golden tests'
/// compositor-parity and gpu-scene-differential do), drawing <see cref="GpuScenePlan"/>'s scenes as the player's engine
/// would, but every frame exact (FlagExactFrames: the worker waits for the frame the moment needs, as an export does) and
/// read back (FlagReadback) instead of presented on a window.
/// </summary>
internal sealed class GpuRenderer : IDisposable
{
    private readonly CompositorClient _client;
    private readonly GpuResourceBroker _broker;
    private byte[] _pixels = [];

    private GpuRenderer(CompositorClient client)
    {
        _client = client;

        // Masks, LUTs and stills made where they are asked for, so a plan's every layer is ready before its first frame.
        _broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
    }

    public string Description => _client.Message;

    /// <summary>Starts the worker on the default card (as the golden tests do) and waits until it is ready (8 s at most, as the engine waits).</summary>
    public static GpuRenderer Start()
    {
        var client = new CompositorClient(adapter: -1, keepStandby: false);
        var clock = Stopwatch.StartNew();
        while (client.State == CompositorWorkerState.Starting && clock.ElapsedMilliseconds < 8000)
            Thread.Sleep(10);
        if (client.State != CompositorWorkerState.Ready)
        {
            var why = $"The compositor did not start ({client.State}: {client.Message}).";
            client.Dispose();
            throw new InvalidOperationException(why);
        }

        return new GpuRenderer(client);
    }

    /// <summary>The plan of a state, as GpuPreviewEngine.SetState makes it (UI thread: text and captions are drawn by WPF).</summary>
    public GpuScenePlan Plan(SequenceExportState state) => GpuScenePlan.Create(state, _broker);

    /// <summary>
    /// Draws [<paramref name="at"/>, at + <paramref name="seconds"/>) at the timeline's frame rate and renders its sound,
    /// and writes both into <paramref name="output"/> with the sequence's moments as their time stamps.
    /// </summary>
    public GpuRun Render(GpuScenePlan plan, AudioProgram program, SequenceExportState state, double at, double seconds, string output, int longest, Shift shift)
    {
        var fps = state.Timeline.FrameRate.IsValid ? state.Timeline.FrameRate.Fps : state.SourceFrameRate > 1 ? state.SourceFrameRate : 30;
        var rate = state.Timeline.FrameRate.IsValid
            ? $"{state.Timeline.FrameRate.Numerator.ToString(CultureInfo.InvariantCulture)}/{state.Timeline.FrameRate.Denominator.ToString(CultureInfo.InvariantCulture)}"
            : fps.ToString("0.######", CultureInfo.InvariantCulture);
        var scale = Math.Min(1.0, longest / (double)Math.Max(plan.CanvasWidth, plan.CanvasHeight));
        var (width, height) = (Even(plan.CanvasWidth * scale), Even(plan.CanvasHeight * scale));
        scale = width / (double)plan.CanvasWidth;

        // The sound first (the mixer's offline render, from the moment), into a file of its own for FFmpeg to take.
        var frames = (int)Math.Round(seconds * Media.Rate);
        var sound = new float[frames * 2];
        AudioMixer.Render(program, at - shift.AudioSeconds, sound, Media.Rate);
        var soundFile = Path.ChangeExtension(output, ".f32");
        File.WriteAllBytes(soundFile, MemoryMarshal.AsBytes(sound.AsSpan()).ToArray());

        if (File.Exists(output))
            File.Delete(output);
        var psi = new ProcessStartInfo(Media.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true,
        };
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-v", "error",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-framerate", rate, "-i", "pipe:0",
            "-f", "f32le", "-ar", Media.Rate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", soundFile,
            "-map", "0:v", "-map", "1:a", "-c:v", "ffv1", "-c:a", "pcm_f32le", "-output_ts_offset", Media.N(at), "-y", output,
        ];
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        string? error = null;
        long firstMs = -1;
        using (var ffmpeg = Process.Start(psi)!)
        {
            var log = ffmpeg.StandardError.ReadToEndAsync();
            var bytes = (long)width * height * 4;
            if (_pixels.Length != bytes)
                _pixels = new byte[bytes];
            _client.EnsureReadback(bytes);
            plan.PrepareAround(at);

            var count = (int)Math.Floor(seconds * fps + 1e-6);
            var stream = ffmpeg.StandardInput.BaseStream;
            for (var k = 0; k < count && error is null; k++)
            {
                var moment = at + k / fps;
                if (k % 30 == 0)
                    plan.PrepareAround(moment);

                var clock = Stopwatch.StartNew();
                _client.ClearScene();
                ref var header = ref _client.Scene;
                var layers = MemoryMarshal.CreateSpan(ref _client.Layer(0), CompositorProtocol.MaxLayers);

                // (A hair into the frame, as the golden tests draw: the decoder never takes the frame before it.)
                plan.Evaluate(moment - shift.VideoSeconds + 0.0005, ref header, layers);
                header.PlayheadSeconds = moment;
                (header.OutputWidth, header.OutputHeight) = (width, height);
                header.Flags |= FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback;
                var sequence = _client.Publish();
                if (!_client.TryReadback(sequence, _pixels, out var w, out var h, 20_000) || w != width || h != height)
                {
                    error = $"frame {k} ({moment:0.###} s) could not be read back ({_client.State}: {_client.Message})";
                    break;
                }

                if (k == 0)
                    firstMs = clock.ElapsedMilliseconds;
                stream.Write(_pixels);
            }

            stream.Close();
            ffmpeg.WaitForExit();
            if (error is null && ffmpeg.ExitCode != 0)
                error = $"FFmpeg could not write the output: {log.Result.Trim()}";
        }

        File.Delete(soundFile);
        return new GpuRun(output, fps, (width, height), scale, firstMs, error);
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    public void Dispose()
    {
        _broker.Dispose();
        _client.Dispose();
    }
}
