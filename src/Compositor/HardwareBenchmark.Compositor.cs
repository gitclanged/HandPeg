using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- The compositor stage of the hardware benchmark -----
//
// The all-slots frame of the compositor-parity golden's Timing scene, re-created here so that the benchmark's figure is the very one the
// field numbers come from (measured on v5.6.0: a Radeon RX 7900 XTX 0.551 ms, a GeForce GTX 1070 2.842 ms, a Snapdragon X Elite's Adreno 5.088 ms): 1920 x 1080, every shader slot of 5.5 on
// test-pattern layers (a full-frame layer with the colour, matte, mask and blur passes, a smaller one with its blur fill, shadow, blend
// and two transitions, four more with one social cut each), a LUT, a mask and a curve handed over as the golden hands them. A real
// SceneRenderer in this process on the card's own device: 90 frames that raise its clocks (a mobile GPU idles low), then four timed by
// GPU timestamps (PassTimer), the best kept. No clip and no FFmpeg are needed, so it runs on a fresh install too. Keep the scene as it
// is: the tier anchors (PerformanceTiers.CompositorLadder) were measured on it.
public static unsafe partial class HardwareBenchmark
{
    /// <summary>The scene's size, and the frames drawn before any is timed (the golden's ClockUpFrames) and timed after them.</summary>
    internal const int CompositorWidth = 1920, CompositorHeight = 1080, CompositorClockUpFrames = 90, CompositorTimedFrames = 4;

    /// <summary>
    /// The cards the compositor stage times: the compositor's and the Export card the measurements point to, and any card named on the
    /// command line (--adapters: the roles chosen in Settings), first listings that work only. Never a card no role holds (D-01).
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

        // Roles naming no card (every one Auto): the card Auto lands on, the first listing with the most video memory.
        if (targets.Count == 0 && working.OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).FirstOrDefault() is { } auto)
            targets.Add(auto);
        return targets;
    }

    /// <summary>
    /// Times each target's all-slots frame into its <see cref="HardwareBenchmarkAdapter.CompositorMs"/>, reporting "compositor" lines from
    /// <paramref name="from"/> to <paramref name="to"/> of the run. A card it cannot time keeps 0 (and no note: its other measurements stand).
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

    /// <summary>The all-slots frame on one card, GPU ms: the best of <see cref="CompositorTimedFrames"/> after the clock-up; 0 when no frame was timed.</summary>
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

                // Every frame's timestamps are waited for, as the worker does under FlagWaitForTimings (its queries are reused next frame).
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

    /// <summary>The compositor-parity golden's Timing scene (keep them the same): its header, its six layers written into <paramref name="layers"/>.</summary>
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

        // The social cuts, one a layer.
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

    // A test-pattern layer as the golden's harness makes one (Layer()): placed, centred anchor, opaque, nothing handed over, uncropped.
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
    /// The golden's three textures handed over as the client hands them (CompositorClient.SetResource: a section of their own, the entry's
    /// generation written last): the LUT at 0 (17 cubed), the mask at 1 (64 x 36), the curve at 2 (256 x 1). The sections are the
    /// caller's to dispose after the frames.
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
