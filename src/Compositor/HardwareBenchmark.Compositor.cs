using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Compositor stage of the hardware benchmark -----
//
// Recreates the compositor-parity golden's all-slots Timing scene so the benchmark figure matches field measurements (RX 7900 XTX
// 0.551 ms, GTX 1070 2.842 ms, Snapdragon X Elite Adreno 5.088 ms): 1920x1080, every shader slot on test-pattern layers (one full-frame
// layer with colour, matte, mask and blur passes; one smaller with blur fill, shadow, blend and two transitions; four with one social cut
// each) plus a LUT, mask and curve. Runs a real SceneRenderer on the adapter's device: 90 clock-up frames (mobile GPUs idle low), then 4
// timed by GPU timestamps (PassTimer), best kept. Needs no clip or FFmpeg. Do not change the scene: the tier anchors
// (PerformanceTiers.CompositorLadder) were measured on it.
public static unsafe partial class HardwareBenchmark
{
    /// <summary>Scene size, clock-up frames before timing (the golden's ClockUpFrames), and timed frames.</summary>
    internal const int CompositorWidth = 1920, CompositorHeight = 1080, CompositorClockUpFrames = 90, CompositorTimedFrames = 4;

    /// <summary>
    /// Adapters to time: the recommended Compositor and Export adapters plus any named via --adapters (roles chosen in Settings), first
    /// working listings only. Never an adapter that holds no role.
    /// </summary>
    internal static List<HardwareBenchmarkAdapter> CompositorTargets(HardwareBenchmarkResult result, IReadOnlySet<int> chosen)
    {
        var roles = Recommend(result);
        var wanted = new List<int> { roles.Compositor, roles.Export };
        wanted.AddRange(chosen.Order());
        var working = result.Adapters.Where(a => a.SameCardAs < 0 && a.Note.Length == 0).ToList();
        var targets = new List<HardwareBenchmarkAdapter>();
        foreach (var index in wanted)
        {
            if (working.FirstOrDefault(a => a.Index == index) is { } card && !targets.Contains(card))
                targets.Add(card);
        }

        // All roles Auto: time the adapter Auto resolves to, the first listing with the most VRAM.
        if (targets.Count == 0 && working.OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).FirstOrDefault() is { } auto)
            targets.Add(auto);
        return targets;
    }

    /// <summary>
    /// Times each target's all-slots frame into <see cref="HardwareBenchmarkAdapter.CompositorMs"/>, reporting "compositor" progress
    /// from <paramref name="from"/> to <paramref name="to"/>. Failures leave 0 without a note (other measurements stand).
    /// </summary>
    private static void TimeCompositors(HardwareBenchmarkResult result, List<HardwareBenchmarkAdapter> targets, double from, double to)
    {
        var share = (to - from) / Math.Max(targets.Count, 1);
        for (var i = 0; i < targets.Count; i++)
        {
            var card = targets[i];
            try
            {
                card.CompositorMs = Math.Round(TimeCompositor(card.Index), 3);
                Report("compositor", card.Index, from + share * (i + 1), card.CompositorMs > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{card.Name}: compositor {card.CompositorMs:0.00} ms a frame")
                    : $"{card.Name}: the compositor could not be timed", result);
            }
            catch (Exception ex) when (ex is GpuException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                card.CompositorMs = 0;
                Report("compositor", card.Index, from + share * (i + 1), $"{card.Name}: the compositor could not be timed ({ex.Message})", result);
            }
        }
    }

    /// <summary>All-slots frame GPU time on one adapter, ms: best of <see cref="CompositorTimedFrames"/> after clock-up; 0 when none was timed.</summary>
    internal static double TimeCompositor(int adapterIndex)
    {
        const int w = CompositorWidth, h = CompositorHeight;
        using var gpu = GpuDevice.Create(adapterIndex, allowWarp: false);
        using var shaders = new ShaderLibrary(gpu);
        using var shared = CompositorSharedMemory.Create(string.Create(CultureInfo.InvariantCulture, $"HandPeg.Benchmark.Compositor.{Environment.ProcessId}.{Guid.NewGuid():N}"));
        var sections = HandOverResources(shared);
        try
        {
            using var decoders = new DecoderPool(gpu, shared);
            using var resources = new ResourceCache(gpu, shared);
            using var renderer = new SceneRenderer(gpu, shaders, decoders, resources);
            using var output = gpu.CreateTexture(w, h, DxgiFormat.B8G8R8A8Unorm, renderTarget: true);
            var layers = new GpuLayerDescriptor[6];
            var header = TimingScene(layers);
            var best = double.MaxValue;
            for (var round = 0; round < CompositorClockUpFrames + CompositorTimedFrames + 1; round++)
            {
                renderer.Render(header, layers, output.Rtv, w, h, raw: false);

                // Wait for every frame's timestamps, as the worker does under FlagWaitForTimings (queries are reused next frame).
                if (renderer.Timer.Collect() && round > CompositorClockUpFrames && renderer.Timer.FrameMs > 0)
                    best = Math.Min(best, renderer.Timer.FrameMs);
            }

            return best < double.MaxValue ? best : 0;
        }
        finally
        {
            foreach (var section in sections)
                section.Dispose();
        }
    }

    /// <summary>The compositor-parity golden's Timing scene (keep identical): returns the header and writes six layers into <paramref name="layers"/>.</summary>
    internal static FrameSceneHeader TimingScene(GpuLayerDescriptor[] layers)
    {
        Array.Clear(layers);
        ref var a = ref Layer(layers, 0, 3, 0, 0, CompositorWidth, CompositorHeight);
        (a.MaskResourceId, a.LutResourceId, a.CurveResourceId) = (1, 0, 2);
        foreach (var packet in new GpuEffectPacket[]
        {
            new(GpuShaderSlot.Flip, 1, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.Lut3D, 0, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.Denoise, 4, 0, 0, 0, 0, 0, 0, 0),
            new(GpuShaderSlot.ColorGrade, 1.1f, 0, 1.1f, 1, 0, 0, 0, 0), new(GpuShaderSlot.ColorBalance, 0.1f, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.ColorTemperature, 5000, 1, 0, 0, 0, 0, 0, 0),
            new(GpuShaderSlot.Vignette, 0.6f, 100, 100, 0, 0, 0, 0, 0), new(GpuShaderSlot.HueSaturation, 10, 1.1f, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.ToneCurve, 0, 0, 0, 0, 0, 0, 0, 0),
            new(GpuShaderSlot.ChromaKey, 0, 1, 0, 0.15f, 0.05f, 0.3f, 0, 0), new(GpuShaderSlot.MatteChoke, 2, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.MatteSmooth, 5, 0, 0, 0, 0, 0, 0, 0),
            new(GpuShaderSlot.LayerMask, 0, 0, 0, 0, 0, 0, 0, 0), new(GpuShaderSlot.CornerFeather, 10, 1, 12, 0, 0, 0, 0, 0), new(GpuShaderSlot.BoxBlur, 10, 0, 0, 0, 0, 0, 0, 0),
            new(GpuShaderSlot.Sharpen, 0.5f, 0, 0, 0, 0, 0, 0, 0),
        })
            a.Effects[a.EffectCount++] = packet;

        ref var b = ref Layer(layers, 1, 1, 200, 100, 800, 450);
        (b.BlurFillRadius, b.BlurFillPasses) = (20, 2);
        b.Effects[b.EffectCount++] = new(GpuShaderSlot.DropShadow, 10, 135, 12, 0.6f, 0, 0, 0, 0);
        b.Effects[b.EffectCount++] = new(GpuShaderSlot.BlendMode, 4, 0, 0, 0, 0, 0, 0, 0);
        b.Effects[b.EffectCount++] = new(GpuShaderSlot.TransitionWipe, 0.6f, 1, 0, 0.6f, 1, 0, 0, 0);
        b.Effects[b.EffectCount++] = new(GpuShaderSlot.TransitionBlurDissolve, 0.7f, 0, 0, 0.7f, 1, 0, 0, 0);

        // One social cut per layer.
        var place = 2;
        foreach (var cut in new[] { GpuShaderSlot.TransitionWhipPan, GpuShaderSlot.TransitionZoomWarp, GpuShaderSlot.TransitionRgbGlitch, GpuShaderSlot.TransitionSpin })
        {
            ref var layer = ref Layer(layers, place++, 1, 200, 100, 800, 450);
            layer.Effects[layer.EffectCount++] = new(cut, 0.6f, 0, 0, 0.5f, 1, 0, 0, 0);
        }

        return new FrameSceneHeader
        {
            CanvasWidth = CompositorWidth, CanvasHeight = CompositorHeight, OutputWidth = CompositorWidth, OutputHeight = CompositorHeight,
            Flags = FrameSceneHeader.FlagTimePasses | FrameSceneHeader.FlagWaitForTimings, ActiveLayerCount = layers.Length,
        };
    }

    // Test-pattern layer as the golden harness builds it (Layer()): placed, centred anchor, opaque, no resources, uncropped.
    private static ref GpuLayerDescriptor Layer(GpuLayerDescriptor[] layers, int index, int pattern, float x, float y, float width, float height)
    {
        ref var layer = ref layers[index];
        layer.SourceKind = (int)GpuSourceKind.TestPattern;
        layer.PatternKind = pattern;
        (layer.X, layer.Y, layer.Width, layer.Height) = (x, y, width, height);
        (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
        (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
        (layer.CropRight, layer.CropBottom) = (1, 1);
        layer.Flags = GpuLayerDescriptor.FlagVisible;
        return ref layer;
    }

    /// <summary>
    /// Uploads the golden's three textures the way CompositorClient.SetResource does (one section each, generation written last): LUT at
    /// 0 (17^3), mask at 1 (64x36), curve at 2 (256x1). The caller disposes the sections after rendering.
    /// </summary>
    private static List<MemoryMappedFile> HandOverResources(CompositorSharedMemory shared)
    {
        const int n = 17, mw = 64, mh = 36;
        var lut = new float[n * n * n * 4];
        for (var b = 0; b < n; b++)
            for (var g = 0; g < n; g++)
                for (var r = 0; r < n; r++)
                {
                    var i = ((b * n + g) * n + r) * 4;
                    var (fr, fg, fb) = (r / (n - 1f), g / (n - 1f), b / (n - 1f));
                    (lut[i], lut[i + 1], lut[i + 2], lut[i + 3]) = (1 - fr * 0.8f, fg * 0.9f + 0.05f, fb * fb, 1);
                }

        var mask = new byte[mw * mh * 4];
        for (var y = 0; y < mh; y++)
            for (var x = 0; x < mw; x++)
            {
                var value = (byte)Math.Clamp((int)(x * 4 + 40 * MathF.Sin(y * 0.3f)), 0, 255);
                var i = (y * mw + x) * 4;
                (mask[i], mask[i + 1], mask[i + 2], mask[i + 3]) = (value, value, value, 255);
            }

        var curve = new float[256 * 4];
        for (var x = 0; x < 256; x++)
        {
            var t = x / 255f;
            (curve[x * 4], curve[x * 4 + 1], curve[x * 4 + 2], curve[x * 4 + 3]) = (t * t * (3 - 2 * t), MathF.Sqrt(t), t * t, t);
        }

        var sections = new List<MemoryMappedFile>();
        try
        {
            sections.Add(HandOver(shared, 0, GpuResourceKind.Lut3D, n, n, n, System.Runtime.InteropServices.MemoryMarshal.AsBytes(lut.AsSpan())));
            sections.Add(HandOver(shared, 1, GpuResourceKind.Mask2D, mw, mh, 1, mask));
            sections.Add(HandOver(shared, 2, GpuResourceKind.Curve1D, 256, 1, 1, System.Runtime.InteropServices.MemoryMarshal.AsBytes(curve.AsSpan())));
            return sections;
        }
        catch
        {
            foreach (var section in sections)
                section.Dispose();
            throw;
        }
    }

    private static MemoryMappedFile HandOver(CompositorSharedMemory shared, int id, GpuResourceKind kind, int width, int height, int depth, ReadOnlySpan<byte> pixels)
    {
        var needed = (int)CompositorClient.ResourceBytes(kind, width, height, depth);
        const int generation = 1;
        var section = MemoryMappedFile.CreateNew(CompositorProtocol.ResourceSection(shared.BaseName, id, generation), needed);
        using (var view = section.CreateViewAccessor())
        {
            byte* data = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref data);
            try
            {
                pixels[..needed].CopyTo(new Span<byte>(data + view.PointerOffset, needed));
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        ref var entry = ref shared.Resource(id);
        Volatile.Write(ref entry.Generation, 0);
        (entry.Kind, entry.Width, entry.Height, entry.Depth, entry.Bytes, entry.ContentHash) = ((int)kind, width, height, depth, needed, 0);
        Volatile.Write(ref entry.Generation, generation);
        Interlocked.Increment(ref shared.Control.ResourcesVersion);
        return section;
    }
}
