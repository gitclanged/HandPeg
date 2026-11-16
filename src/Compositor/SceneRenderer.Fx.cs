using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Game-engine effects (slots 22-29) -----
// Shaders: Motion.hlsl, Light.hlsl, Vfx.hlsl, PSFrostedGlass in Core.hlsl. Effects run on the layer's working texture (Look effects) or at
// composite time (rim, glass, shutter). None request decoder frames, so no effect can make a frame late: Motion Blur reads only what the
// FrameCache already holds, and an effect missing its inputs passes the picture through.
internal sealed unsafe partial class SceneRenderer
{
    // ----- Dual-Kawase pyramid -----

    private readonly GpuTexture?[] _kawase = new GpuTexture?[KawaseMostLevels];

    private const int KawaseMostLevels = 6;

    /// <summary>
    /// Downsample levels and tap offset for a blur of <paramref name="reach"/> texels at this size: reach ~ offset x 2^levels, so fractional
    /// reaches are continuous. Never halves below ~4 texels. CompositorReference.Fx.cs mirrors these numbers.
    /// </summary>
    internal static (int Levels, float Offset) KawaseShape(float reach, int width, int height)
    {
        reach = Math.Max(float.IsFinite(reach) ? reach : 1, 1);
        var most = Math.Clamp((int)MathF.Floor(MathF.Log2(Math.Max(Math.Min(width, height), 8) / 4f)), 1, KawaseMostLevels);
        var levels = Math.Clamp((int)MathF.Ceiling(MathF.Log2(reach)), 1, most);
        return (levels, Math.Clamp(reach / (1 << levels), 0.5f, 2));
    }

    /// <summary>Next pyramid level size: half the previous, at least 2 x 2.</summary>
    internal static int KawaseSide(int side) => Math.Max(side / 2, 2);

    /// <summary>
    /// Blurs <paramref name="source"/> (unchanged) by <paramref name="reach"/> texels into a new half-size texture, or <paramref name="keep"/>
    /// levels smaller (upsampling stops early; very wide light spreads lose nothing).
    /// </summary>
    private GpuTexture Kawase(GpuTexture source, float reach, int timedAs, int keep = 0)
    {
        var (levels, offset) = KawaseShape(reach, source.Width, source.Height);
        var input = source;
        var constants = default(PassConstants);
        constants.Params = new Vector4(_pxScale1080, 0, offset, _pixelScale);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        for (var level = 0; level < levels; level++)
        {
            var output = _gpu.Textures.Rent(KawaseSide(input.Width), KawaseSide(input.Height));
            Step("PSKawaseDown", input, output, ref constants, timedAs);
            _kawase[level] = input = output;
        }

        keep = Math.Clamp(keep, 0, levels - 1);
        for (var level = levels - 1; level > keep; level--)
        {
            Step("PSKawaseUp", _kawase[level]!, _kawase[level - 1]!, ref constants, timedAs);
            _gpu.Textures.Return(_kawase[level]!);
        }

        for (var level = 0; level < keep; level++)
            _gpu.Textures.Return(_kawase[level]!);
        return _kawase[keep]!;
    }

    private void Step(string shader, GpuTexture input, GpuTexture output, ref PassConstants constants, int timedAs, nint aux = 0, nint lut = 0)
    {
        _timer.Begin(timedAs);
        Draw(shader, input, output.Rtv, output.Width, output.Height, ref constants, blend: false, aux: aux, lut: lut);
        _timer.End();
    }

    /// <summary>Bright-pass of a picture in linear light at half size: the input for glow and lens streaks.</summary>
    private GpuTexture Highlights(GpuTexture work, float threshold, int width, int height, int timedAs)
    {
        var bright = _gpu.Textures.Rent(width, height);
        var constants = default(PassConstants);
        constants.Params2 = new Vector4(threshold, 0, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        Step("PSGlowBright", work, bright, ref constants, timedAs);
        return bright;
    }

    // ----- Film Halation & Glow (slot 23) -----

    /// <summary>Spread reach in px of a 1080-wide frame (one slider step per stop of light spread).</summary>
    internal static float GlowReach(float spread) => MathF.Pow(2, Math.Clamp(spread, 1, 8));

    // Optional second packet for the Audio Reactive Pulse (marked; sent only while the pulse is used): pulse (Q0) and band (Q1) scale the
    // glow amount (P0). A lone pulse packet (Q7 marker, below 0.05 as Mix) draws nothing.
    private GpuTexture Glow(GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        if (GpuEffectPacket.IsPulsePacket(second) && PulseOf(second.P0, second.P1) is var lift && lift != 1)
            packet = packet with { P0 = packet.P0 * lift };
        if (packet.P0 < 0.001f || packet.P7 < 0.05f)
            return work;

        var slot = (int)GpuShaderSlot.HalationGlow;
        var bright = Highlights(work, packet.P1, KawaseSide(work.Width), KawaseSide(work.Height), slot);
        var blurred = Kawase(bright, GlowReach(packet.P2) * _pxScale1080 / 2, slot);
        _gpu.Textures.Return(bright);
        var output = Effect("PSGlowMix", packet, default, work, aux: blurred.Srv);
        _gpu.Textures.Return(blurred);
        return output;
    }

    // ----- Impact Shockwave and Anamorphic Lens (slot 25) -----

    /// <summary>Streak tap spacing per pass: each pass triples the last, so seven taps reach far.</summary>
    internal const int StreakPasses = 3;

    // Impact Shockwave (optional marked pulse packet: pulse Q0 and band Q1 scale ripple and shake) or Anamorphic Lens (P7 variant; second
    // packet's Q0 is the flare Rotation).
    private GpuTexture Lens(GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        // A pulse packet without its effect's first packet draws nothing.
        if (GpuEffectPacket.IsPulsePacket(packet))
            return work;
        if (packet.P7 < 0.5f)
        {
            if (GpuEffectPacket.IsPulsePacket(second) && PulseOf(second.P0, second.P1) is var lift && lift != 1)
                packet = packet with { P3 = packet.P3 * lift, P6 = packet.P6 * lift };
            return packet.P0 <= 0 || packet.P0 >= 1 ? work : Effect("PSShockwave", packet, default, work);
        }
        if (packet.P0 < 0.001f && packet.P6 < 0.001f)
            return Effect("PSAnamorphic", packet, default, work);

        var slot = (int)GpuShaderSlot.ShockwaveLens;
        var streak = Highlights(work, packet.P1, KawaseSide(work.Width), Math.Max(work.Height / 4, 2), slot);
        var constants = default(PassConstants);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        var stride = 0.5f + Math.Clamp(packet.P2, 0, 1) * 1.5f;
        var turn = second.Slot == GpuShaderSlot.ShockwaveLens ? second.P0 * MathF.PI / 180 : 0;
        var aspect = work.Width / (float)Math.Max(work.Height, 1);
        for (var pass = 0; pass < StreakPasses; pass++, stride *= 3)
        {
            var output = _gpu.Textures.Rent(streak.Width, streak.Height);
            constants.Params = new Vector4(_pxScale1080, turn, stride, aspect);
            Step("PSStreak", streak, output, ref constants, slot);
            _gpu.Textures.Return(streak);
            streak = output;
        }

        var lensed = Effect("PSAnamorphic", packet, default, work, aux: streak.Srv);
        _gpu.Textures.Return(streak);
        return lensed;
    }

    // ----- Procedural effects (slots 26-29) -----

    /// <summary>
    /// True when a packet pairs with the next one of its slot: generators (two packets, like Color Balance), the Anamorphic Lens (Rotation in a
    /// second packet; P7 variant, 0 for Impact Shockwave), and Glow/Impact Shockwave with a pulse packet only when the next is marked
    /// (GpuEffectPacket.PulseMarker; sent only while the pulse is used), so two adjacent same-slot effects at pulse 0 draw separately. A lone
    /// packet of these slots (as tests write) draws alone.
    /// </summary>
    private static bool Paired(in GpuEffectPacket packet, in GpuEffectPacket next) =>
        next.Slot == packet.Slot && packet.Slot switch
        {
            GpuShaderSlot.ColorBalance or GpuShaderSlot.Explosion or GpuShaderSlot.Sparks or GpuShaderSlot.AnimeLines or GpuShaderSlot.EnergyField
                or GpuShaderSlot.ColorStudio => true,
            GpuShaderSlot.ShockwaveLens when packet.P7 >= 0.5f => true,
            GpuShaderSlot.ShockwaveLens or GpuShaderSlot.HalationGlow => GpuEffectPacket.IsPulsePacket(next),
            _ => false,
        };

    private GpuTexture Generated(GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        switch (packet.Slot)
        {
            case GpuShaderSlot.Explosion when packet.P0 is > 0 and < 1:
                // Half resolution: soft content, and the most expensive generator.
                return Generator("PSExplosion", packet, second, work, second.P0, shrink: 2, lut: Noise3D());
            case GpuShaderSlot.Sparks when packet.P0 is > 0 and < 1:
                return Sparks(packet, second, work);
            case GpuShaderSlot.AnimeLines:
                // The second packet's last two lanes (audio-reactive pulse and band) scale line weight and opacity.
                if (PulseOf(second.P6, second.P7) is var lift && lift != 1)
                    packet = packet with { P4 = packet.P4 * lift, P7 = packet.P7 * lift };
                return packet.P7 < 0.05f ? work : Generator("PSAnimeLines", packet, second, work, second.P3, shrink: 1);
            case GpuShaderSlot.EnergyField when packet.P0 > 0:
                return Generator(second.P7 > 0.5f ? "PSSaber" : "PSEnergyShield", packet, second, work, second.P0, shrink: 1);
            default:
                return work;
        }
    }

    /// <summary>Renders a generator effect from scratch and composites it over the input with its light spill.</summary>
    private GpuTexture Generator(string shader, GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work, float spill, int shrink, nint lut = 0)
    {
        if (!_shaders.Has(shader))
            return work;
        var fx = _gpu.Textures.Rent(Math.Max(work.Width / shrink, 2), Math.Max(work.Height / shrink, 2));
        var constants = FxConstants(packet, second);
        _timer.Begin((int)packet.Slot);
        Draw(shader, null, fx.Rtv, fx.Width, fx.Height, ref constants, blend: false, lut: lut);
        _timer.End();
        return Lit(fx, work, spill, (int)packet.Slot);
    }

    private PassConstants FxConstants(GpuEffectPacket packet, GpuEffectPacket second)
    {
        var constants = default(PassConstants);
        constants.Packet = packet;
        constants.Packet1 = second;
        constants.Params = new Vector4(_pxScale1080, 0, 0, _pixelScale);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        return constants;
    }

    /// <summary>Sparks and debris: shards blended, then sparks added; each particle is six vertices generated from its index (no vertex buffer).</summary>
    private GpuTexture Sparks(GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        var fx = _gpu.Textures.Rent(work.Width, work.Height);
        _gpu.Context.ClearRenderTargetView(fx.Rtv, 0, 0, 0, 0);
        var count = Math.Clamp((int)MathF.Round(packet.P3), 1, 800);
        var constants = FxConstants(packet, second);
        constants.Sizes(fx.Width, fx.Height, fx.Width, fx.Height);
        _timer.Begin((int)GpuShaderSlot.Sparks);
        if (second.P3 > 0.001f)
        {
            constants.Params2 = new Vector4(1, 0, 0, 0);
            Submit("VSSparks", "PSDebris", 0, 0, 0, fx.Rtv, fx.Width, fx.Height, ref constants, blend: true, vertices: (uint)(6 * count), clipToFrame: false);
        }

        constants.Params2 = default;
        Submit("VSSparks", "PSSparks", 0, 0, 0, fx.Rtv, fx.Width, fx.Height, ref constants, blend: false, vertices: (uint)(6 * count), clipToFrame: false, additive: true);
        _timer.End();
        return Lit(fx, work, second.P4, (int)GpuShaderSlot.Sparks);
    }

    /// <summary>Light Spill: a very wide blur of the effect lights the underlying picture, then the effect is composited over it.</summary>
    private GpuTexture Lit(GpuTexture fx, GpuTexture work, float spill, int timedAs)
    {
        var output = _gpu.Textures.Rent(work.Width, work.Height);
        var light = spill >= 0.005f ? Kawase(fx, Math.Max(fx.Height * 0.4f, 8), timedAs, keep: 2) : null;
        var constants = default(PassConstants);
        constants.Params2 = new Vector4(Math.Clamp(spill, 0, 1), 0, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        var view = light?.Srv ?? 0;
        _gpu.Context.PSSetShaderResources(5, 1, &view);
        Step("PSVfxComposite", work, output, ref constants, timedAs, aux: fx.Srv);
        view = 0;
        _gpu.Context.PSSetShaderResources(5, 1, &view);
        _gpu.Textures.Return(fx);
        if (light is not null)
            _gpu.Textures.Return(light);
        _gpu.Textures.Return(work);
        return output;
    }

    private nint _noise3D, _noiseView;

    /// <summary>Explosion noise volume (32^3 RGBA8, four tiling fractal noises, 128 KB): baked once on first use in a few milliseconds.</summary>
    private nint Noise3D()
    {
        if (_noiseView != 0)
            return _noiseView;

        const int size = 32;
        var texels = new byte[size * size * size * 4];
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    for (var channel = 0; channel < 4; channel++)
                        texels[(((z * size + y) * size + x) * 4) + channel] = (byte)(Math.Clamp(Fractal((float)x / size, (float)y / size, (float)z / size, channel), 0, 1) * 255);
                }
            }
        }

        fixed (byte* data = texels)
        {
            var initial = new SubresourceData { SysMem = data, SysMemPitch = size * 4, SysMemSlicePitch = size * size * 4 };
            _noise3D = _gpu.Device.CreateTexture3D(new Texture3DDesc
            {
                Width = size, Height = size, Depth = size, MipLevels = 1, Format = DxgiFormat.R8G8B8A8Unorm,
                Usage = D3D11.UsageImmutable, BindFlags = D3D11.BindShaderResource,
            }, &initial);
        }

        _noiseView = _gpu.Device.CreateShaderResourceView(_noise3D);
        return _noiseView;
    }

    // Four octaves of value noise, each tiling over the unit cube.
    private static float Fractal(float x, float y, float z, int channel)
    {
        var (sum, weight, total) = (0f, 0.5f, 0f);
        for (var octave = 0; octave < 4; octave++)
        {
            sum += ValueNoise(x, y, z, 2 << octave, channel * 97 + octave * 13) * weight;
            total += weight;
            weight *= 0.5f;
        }

        return sum / total;
    }

    private static float ValueNoise(float x, float y, float z, int period, int seed)
    {
        static float Smooth(float t) => t * t * (3 - 2 * t);
        var (px, py, pz) = (x * period, y * period, z * period);
        var (ix, iy, iz) = ((int)px, (int)py, (int)pz);
        var (fx, fy, fz) = (Smooth(px - ix), Smooth(py - iy), Smooth(pz - iz));
        float At(int a, int b, int c)
        {
            // Integer hash of the lattice point, wrapped by period so the noise tiles.
            var h = (uint)(((a + ix) % period) * 73856093 ^ ((b + iy) % period) * 19349663 ^ ((c + iz) % period) * 83492791 ^ seed * 2654435);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }

        var near = Lerp(Lerp(At(0, 0, 0), At(1, 0, 0), fx), Lerp(At(0, 1, 0), At(1, 1, 0), fx), fy);
        var far = Lerp(Lerp(At(0, 0, 1), At(1, 0, 1), fx), Lerp(At(0, 1, 1), At(1, 1, 1), fx), fy);
        return Lerp(near, far, fz);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ----- Motion Blur (slot 22): in-picture motion -----

    /// <summary>
    /// Optical flow of a source frame, or of the canvas under an adjustment layer; cached because a video, its Cutouts and paused redraws
    /// all request the same flow.
    /// </summary>
    private sealed class FlowEntry : IDisposable
    {
        public int Source = -1;
        public double Time, Gap, Moment = double.NaN;
        public long Clip, LastFrame;
        public bool Ready;
        public GpuTexture? Field, Previous;

        public void Dispose()
        {
            Field?.Dispose();
            Previous?.Dispose();
        }
    }

    private readonly List<FlowEntry> _flows = [];
    private readonly GpuTexture?[] _flowThis = new GpuTexture?[FlowLevels], _flowBefore = new GpuTexture?[FlowLevels];

    /// <summary>Flow pyramid levels, and maximum cached flows (four sources or adjustment layers, about 1 MB each).</summary>
    private const int FlowLevels = 4, MostFlows = 4;

    /// <summary>Flow resolution for a picture: a quarter size, at most 480 wide, at least 16 high.</summary>
    private static (int Width, int Height) FlowSize(int width, int height)
    {
        var across = Math.Min(Math.Max(width / 4, 32), 480);
        return (across & ~1, Math.Max((int)MathF.Round((float)across * height / Math.Max(width, 1)) & ~1, 16));
    }

    /// <summary>Returns a reset flow entry of this size: new, or the least recently used.</summary>
    private FlowEntry TakeFlow(int width, int height)
    {
        FlowEntry? entry = null;
        if (_flows.Count >= MostFlows)
        {
            foreach (var flow in _flows)
            {
                if (entry is null || flow.LastFrame < entry.LastFrame)
                    entry = flow;
            }
        }

        if (entry is null)
            _flows.Add(entry = new FlowEntry());
        (entry.Source, entry.Clip, entry.Ready, entry.Moment) = (-1, 0, false, double.NaN);
        if (entry.Field is null || entry.Field.Width != width || entry.Field.Height != height)
        {
            entry.Dispose();
            entry.Field = _gpu.CreateTexture(width, height, DxgiFormat.R16G16B16A16Float, renderTarget: true);
            entry.Previous = null;
        }

        return entry;
    }

    /// <summary>Coarse-to-fine flow solve between this frame and the previous (both at flow size) into <paramref name="into"/>; both are returned to the pool.</summary>
    private void SolveFlow(GpuTexture now, GpuTexture before, GpuTexture into)
    {
        var slot = (int)GpuShaderSlot.MotionBlur;
        var copy = default(PassConstants);
        copy.WholeUv();
        (_flowThis[0], _flowBefore[0]) = (now, before);
        var levels = 1;
        for (; levels < FlowLevels && Math.Min(_flowThis[levels - 1]!.Width, _flowThis[levels - 1]!.Height) >= 24; levels++)
        {
            var (width, height) = (_flowThis[levels - 1]!.Width / 2, _flowThis[levels - 1]!.Height / 2);
            (_flowThis[levels], _flowBefore[levels]) = (_gpu.Textures.Rent(width, height), _gpu.Textures.Rent(width, height));
            Step("PSCopy", _flowThis[levels - 1]!, _flowThis[levels]!, ref copy, slot);
            Step("PSCopy", _flowBefore[levels - 1]!, _flowBefore[levels]!, ref copy, slot);
        }

        GpuTexture? above = null;
        for (var level = levels - 1; level >= 0; level--)
        {
            // Solve the finest level twice: the first solve's warp makes the second accurate.
            for (var run = level == 0 ? 2 : 1; run > 0; run--)
            {
                var (here, was) = (_flowThis[level]!, _flowBefore[level]!);
                var output = _gpu.Textures.Rent(here.Width, here.Height);
                var constants = default(PassConstants);
                constants.Params2 = new Vector4(above is null ? 0 : above.Width == here.Width ? 1 : 2, 0, 0, 0);
                constants.WholeUv();
                var view = above?.Srv ?? 0;
                _gpu.Context.PSSetShaderResources(5, 1, &view);
                Step("PSFlowSolve", here, output, ref constants, slot, aux: was.Srv);
                view = 0;
                _gpu.Context.PSSetShaderResources(5, 1, &view);
                if (above is not null)
                    _gpu.Textures.Return(above);
                above = output;
            }

            _gpu.Textures.Return(_flowThis[level]!);
            _gpu.Textures.Return(_flowBefore[level]!);
            (_flowThis[level], _flowBefore[level]) = (null, null);
        }

        var smooth = default(PassConstants);
        smooth.WholeUv();
        _timer.Begin(slot);
        Draw("PSFlowSmooth", above, into.Rtv, into.Width, into.Height, ref smooth, blend: false);
        _timer.End();
        _gpu.Textures.Return(above!);
    }

    /// <summary>Flow of a media layer's frame from the preceding cached frame. False when none exists (clip's first frame, freeze frame).</summary>
    private bool MediaFlow(in GpuLayerDescriptor layer, out GpuTexture flow, out float perFrame)
    {
        (flow, perFrame) = (null!, 0);
        if ((layer.Flags & GpuLayerDescriptor.FlagPreviousMoment) == 0)
            return false;
        var step = layer.MediaSeconds - layer.PreviousMoment;
        if (Math.Abs(step) < 1e-5 || _decoders.Resolve(layer.SourceId, layer.MediaSeconds, layer.SourcePathHash, layer.ClipKey) is not { LumaSrv: not 0 } frame
            || FlowOf(frame, null, out var gap) is not { } field)
        {
            return false;
        }

        (flow, perFrame) = (field, (float)(step / Math.Max(gap, 1e-4)));
        return true;
    }

    /// <summary>
    /// Flow of a source frame from an earlier frame (this(x) = before(x - f), in flow texels), solved once and cached by the later frame (Motion
    /// Blur and Smooth Slow-Mo's preceding in-between share one solve). Null <paramref name="before"/> uses the preceding cached frame (no
    /// demand). Returns null when none; <paramref name="gap"/> is the seconds between the two solved frames.
    /// </summary>
    private GpuTexture? FlowOf(CachedFrame frame, CachedFrame? before, out double gap)
    {
        gap = 0;
        FlowEntry? entry = null;
        foreach (var known in _flows)
        {
            if (known.Source == frame.Source && known.Ready && Math.Abs(known.Time - frame.Time) < 1e-6)
                entry = known;
        }

        if (entry is null)
        {
            if ((before ?? _decoders.FrameBefore(frame)) is not { LumaSrv: not 0 } was || !(was.Time < frame.Time))
                return null;

            var (width, height) = FlowSize(frame.Width, frame.Height);
            entry = TakeFlow(width, height);
            var whole = new Vector4(0, 0, 1, 1);
            var (nowSmall, wasSmall) = (_gpu.Textures.Rent(width, height), _gpu.Textures.Rent(width, height));
            DrawFrame(frame, nowSmall, whole);
            DrawFrame(was, wasSmall, whole);
            SolveFlow(nowSmall, wasSmall, entry.Field!);
            (entry.Source, entry.Time, entry.Gap, entry.Ready) = (frame.Source, frame.Time, frame.Time - was.Time, true);
        }

        entry.LastFrame = _frame;
        gap = entry.Gap;
        return entry.Field;
    }

    /// <summary>Flow of the canvas under an adjustment layer (or Sequence Output) from its previous draw, while frames arrive in order.</summary>
    private bool CanvasFlow(in GpuLayerDescriptor layer, GpuTexture work, out GpuTexture flow)
    {
        flow = null!;
        var step = _time - layer.PreviousMoment;
        if ((layer.Flags & GpuLayerDescriptor.FlagPreviousMoment) == 0 || step <= 1e-5)
            return false;

        var key = layer.ClipKey ^ _historySalt;
        FlowEntry? entry = null;
        foreach (var known in _flows)
        {
            if (known.Clip == key)
                entry = known;
        }

        var (width, height) = FlowSize(work.Width, work.Height);
        if (entry is null || entry.Field!.Width != width || entry.Field.Height != height)
        {
            entry = TakeFlow(width, height);
            entry.Clip = key;
            entry.Previous ??= _gpu.CreateTexture(width, height, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        }

        entry.LastFrame = _frame;

        // Redrawing the same time reuses the existing flow.
        if (!double.IsNaN(entry.Moment) && Math.Abs(entry.Moment - _time) < step * 0.5)
        {
            flow = entry.Field!;
            return entry.Ready;
        }

        var inOrder = !double.IsNaN(entry.Moment) && Math.Abs(entry.Moment - layer.PreviousMoment) < step * 0.5;
        var slot = (int)GpuShaderSlot.MotionBlur;
        var now = _gpu.Textures.Rent(width, height);
        var copy = default(PassConstants);
        copy.WholeUv();
        Step("PSCopy", work, now, ref copy, slot);
        if (inOrder)
        {
            // The stored frame feeds the solve; this frame replaces it.
            var was = _gpu.Textures.Rent(width, height);
            Step("PSCopy", entry.Previous!, was, ref copy, slot);
            Step("PSCopy", now, entry.Previous!, ref copy, slot);
            SolveFlow(now, was, entry.Field!);
        }
        else
        {
            Step("PSCopy", now, entry.Previous!, ref copy, slot);
            _gpu.Textures.Return(now);
        }

        (entry.Moment, entry.Ready) = (_time, inOrder);
        flow = entry.Field!;
        return inOrder;
    }

    /// <summary>Smears the picture along its motion across the shutter (two sample passes: squared sample count at linear cost).</summary>
    private GpuTexture MotionBlur(in GpuLayerDescriptor layer, GpuEffectPacket packet, GpuTexture work)
    {
        if (packet.P0 < 0.5f || packet.P2 < 0.5f || packet.P4 < 0.5f || !_shaders.Has("PSMotionBlur"))
            return work;

        var adjustment = (layer.Flags & GpuLayerDescriptor.FlagAdjustment) != 0;
        var media = (GpuSourceKind)layer.SourceKind == GpuSourceKind.Media;
        GpuTexture flow;
        var perFrame = 1f;
        if (adjustment ? !CanvasFlow(layer, work, out flow) : !media || !MediaFlow(layer, out flow, out perFrame))
            return work;

        var part = adjustment ? new Vector4(0, 0, 1, 1) : CropUv(layer);
        var share = Math.Clamp(packet.P0, 0, 720) / 360 * perFrame * Math.Clamp(packet.P4, 0, 100) / 100;
        var samples = Math.Clamp((int)MathF.Round(packet.P1), 8, 16);
        var longest = Math.Clamp(packet.P5, 0.1f, 50) / 100;

        // Amounts above 50 % extend reach (MotionBoost.Span, 2.5x at 100 %) and the longest streak (at most half the picture) with the same
        // samples and passes, at no extra cost. Up to 50 % output is bit-identical.
        if (packet.P4 > MotionBoost.Plain)
        {
            var span = (float)MotionBoost.Span(packet.P4);
            share *= span;
            longest = Math.Min(longest * span, MotionBoost.LongestStreak);
        }

        var (stepX, stepY) = (share / flow.Width / Math.Max(part.Z - part.X, 1e-4f), share / flow.Height / Math.Max(part.W - part.Y, 1e-4f));
        for (var pass = 0; pass < 2; pass++)
        {
            var output = _gpu.Textures.Rent(work.Width, work.Height);
            var constants = default(PassConstants);
            constants.Params2 = part;
            constants.Params3 = new Vector4(stepX, stepY, longest, samples);
            constants.Params4 = new Vector4(1, 1, 0, (float)_time);
            constants.WholeUv();
            Step("PSMotionBlur", work, output, ref constants, (int)GpuShaderSlot.MotionBlur, aux: flow.Srv);
            _gpu.Textures.Return(work);
            work = output;
            (stepX, stepY, longest) = (stepX / samples, stepY / samples, longest / samples);
        }

        return work;
    }

    // ----- Motion Blur: layer transform motion -----

    /// <summary>
    /// Sums sub-frames of the composited layer from shutter open to close (the plan's lanes) into a full-target texture placed like the layer.
    /// Each sub-frame uses the layer's actual transform at that time (linear between the endpoints; a shutter interval is short).
    /// </summary>
    private (GpuTexture Work, (float X, float Y, float W, float H) Box, float AnchorX, float AnchorY, float Rotation) Shutter(
        in GpuLayerDescriptor layer, GpuEffectPacket shutter, GpuTexture work, (float X, float Y, float W, float H) box, float anchorX, float anchorY, GpuEffectPacket? wipe)
    {
        var amount = Math.Clamp(shutter.P4, 0, 100) / 100;
        var samples = Math.Clamp((int)MathF.Round(shutter.P1), 8, 16);
        var target = _gpu.Textures.Rent(_outWidth, _outHeight);
        _gpu.Context.ClearRenderTargetView(target.Rtv, 0, 0, 0, 0);

        // The layer's pivot on the canvas as currently drawn (rim padding or a slide may have moved the box; shutter motion is relative to it).
        var (pivotX, pivotY) = (box.X + anchorX * box.W, box.Y + anchorY * box.H);
        var (fromW, toW) = (layer.ShutterFromWidth / Math.Max(MathF.Abs(layer.Width), 1e-3f), layer.ShutterToWidth / Math.Max(MathF.Abs(layer.Width), 1e-3f));
        var (fromH, toH) = (layer.ShutterFromHeight / Math.Max(MathF.Abs(layer.Height), 1e-3f), layer.ShutterToHeight / Math.Max(MathF.Abs(layer.Height), 1e-3f));
        var (ownX, ownY) = (layer.X + layer.AnchorX * layer.Width, layer.Y + layer.AnchorY * layer.Height);
        var fromX = layer.ShutterFromX + layer.AnchorX * layer.ShutterFromWidth - ownX;
        var fromY = layer.ShutterFromY + layer.AnchorY * layer.ShutterFromHeight - ownY;
        var toX = layer.ShutterToX + layer.AnchorX * layer.ShutterToWidth - ownX;
        var toY = layer.ShutterToY + layer.AnchorY * layer.ShutterToHeight - ownY;

        _timer.Begin((int)GpuShaderSlot.MotionBlur);
        for (var k = 0; k < samples; k++)
        {
            var at = 0.5f + ((k + 0.5f) / samples - 0.5f) * amount;
            var (w, h) = (box.W * Lerp(fromW, toW, at), box.H * Lerp(fromH, toH, at));
            var (x, y) = (pivotX + Lerp(fromX, toX, at) * _scaleX - anchorX * w, pivotY + Lerp(fromY, toY, at) * _scaleY - anchorY * h);
            var turn = Lerp(layer.ShutterFromRotation, layer.ShutterToRotation, at);
            var constants = default(PassConstants);
            constants.Transform = Matrix4.Box(x, y, w, h, turn, anchorX, anchorY, _outWidth, _outHeight, layer.TiltX, layer.TiltY);
            constants.WholeUv();
            constants.Params4 = new Vector4(1f / samples, 1, 0, (float)_time);
            SetWipe(ref constants, wipe, (x, y, w, h));
            constants.Sizes(work.Width, work.Height, _outWidth, _outHeight);
            Submit("VSQuad", "PSComposite", work.Srv, 0, 0, target.Rtv, _outWidth, _outHeight, ref constants, blend: false, vertices: 4, clipToFrame: false, additive: true);
        }

        _timer.End();
        _gpu.Textures.Return(work);
        return (target, (0, 0, _outWidth, _outHeight), 0.5f, 0.5f, 0);
    }

    // ----- Audio-reactive pulse -----
    // Sequence audio energy at the frame time in three bands (FrameSceneHeader.AudioBass/AudioMid/AudioHigh, 0-1; Services/Audio/
    // AudioReactive.cs). Glow, Neon Rim, Shockwave and Anime Lines carry an Audio Reactive Pulse (%) and band in extra lanes; their strength
    // (glow amount, rim strength, ripple and shake, line weight and opacity) is scaled here by 1 + pulse x band energy before the shader. Pulse 0
    // or silence gives exactly 1 and the packet is passed through untouched.

    private float _audioBass, _audioMid, _audioHigh;

    private static float Band(float energy) => float.IsFinite(energy) ? Math.Clamp(energy, 0, 1) : 0;

    /// <summary>Strength multiplier this frame: 1 + pulse (0-100 %) x band energy (0 Bass, 1 Mid, 2 High, 3 loudest band).</summary>
    private float PulseOf(float pulse, float band)
    {
        if (!(pulse > 0))
            return 1;

        var energy = (int)MathF.Round(band) switch
        {
            1 => _audioMid,
            2 => _audioHigh,
            3 => MathF.Max(_audioBass, MathF.Max(_audioMid, _audioHigh)),
            _ => _audioBass,
        };
        return energy > 0 ? 1 + Math.Min(pulse, 100) / 100 * energy : 1;
    }

    // ----- Neon Rim Light and Frosted Glass (slot 24) -----

    /// <summary>Rim light reach beyond the shape, in texels.</summary>
    private float RimRoom(GpuEffectPacket rim) => Math.Clamp(MathF.Ceiling((Math.Max(rim.P3, 0) / 2 + Math.Max(rim.P4, 0) * 1.5f) * _pixelScale) + 2, 3, 512);

    /// <summary>
    /// Draws the rim around the working picture into a larger texture padded for the glow, growing the box to match (pivot preserved). The
    /// distance field uses jump flooding, one pass per halving of the reach, at half size for large pictures (no visible loss in a glow).
    /// </summary>
    private (GpuTexture Work, (float X, float Y, float W, float H) Box, float AnchorX, float AnchorY) NeonRim(GpuEffectPacket rim, GpuTexture work,
        (float X, float Y, float W, float H) box, float anchorX, float anchorY, float pictureShare)
    {
        if (!_shaders.Has("PSRimLight"))
            return (work, box, anchorX, anchorY);

        var slot = (int)GpuShaderSlot.RimGlass;
        var room = (int)RimRoom(rim);
        var output = _gpu.Textures.Rent(work.Width + 2 * room, work.Height + 2 * room);
        var shrink = Math.Max(work.Width, work.Height) > 1024 ? 2 : 1;
        var field = _gpu.Textures.Rent(Math.Max(output.Width / shrink, 2), Math.Max(output.Height / shrink, 2));
        var (roomU, roomV) = ((float)room / work.Width, (float)room / work.Height);

        var constants = default(PassConstants);
        (constants.U0, constants.V0, constants.U1, constants.V1) = (-roomU, -roomV, 1 + roomU, 1 + roomV);
        constants.Params = new Vector4(_pxScale1080, 0, 0, _pixelScale);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Step("PSJfaSeed", work, field, ref constants, slot);

        var flood = default(PassConstants);
        flood.WholeUv();
        var stride = 1;
        while (stride * 2 < room / shrink + 1)
            stride *= 2;
        for (var last = false; stride >= 1; last = stride == 1 && !last, stride = last ? 1 : stride / 2)
        {
            var next = _gpu.Textures.Rent(field.Width, field.Height);
            flood.Params = new Vector4(_pxScale1080, 0, stride, _pixelScale);
            Step("PSJfaStep", field, next, ref flood, slot);
            _gpu.Textures.Return(field);
            field = next;
        }

        constants.Packet = rim;
        constants.Params2 = new Vector4(shrink, pictureShare, 0, 0);
        Step("PSRimLight", work, output, ref constants, slot, aux: field.Srv);
        _gpu.Textures.Return(field);

        var (perX, perY) = (box.W / work.Width, box.H / work.Height);
        var grown = (box.X - room * perX, box.Y - room * perY, output.Width * perX, output.Height * perY);
        var pivotX = (room + anchorX * work.Width) / output.Width;
        var pivotY = (room + anchorY * work.Height) / output.Height;
        _gpu.Textures.Return(work);
        return (output, grown, pivotX, pivotY);
    }

    /// <summary>
    /// Frosted Glass: the canvas under the layer's shape, blurred, tinted and edge-lit, drawn before the layer's picture (at its remaining
    /// share). The whole canvas is copied and blurred, so rotated layers and their neighbours sample it consistently.
    /// </summary>
    private void DrawFrostedGlass(GpuEffectPacket glass, GpuTexture work, (float X, float Y, float W, float H) box, float rotation, float anchorX, float anchorY, float alpha, GpuEffectPacket? wipe,
        (float X, float Y) tilt = default)
    {
        if (!_shaders.Has("PSFrostedGlass"))
            return;

        var slot = (int)GpuShaderSlot.RimGlass;
        var frosted = Kawase(CopyCanvas(), Math.Max(glass.P0 * _pixelScale, 2), slot);

        var constants = default(PassConstants);
        constants.Packet = glass;
        constants.Transform = Matrix4.Box(box.X, box.Y, box.W, box.H, rotation, anchorX, anchorY, _outWidth, _outHeight, tilt.X, tilt.Y);
        constants.Params = new Vector4(_pxScale1080, 0, 0, _pixelScale);
        constants.Params4 = new Vector4(alpha, 1, 0, (float)_time);
        constants.WholeUv();
        SetWipe(ref constants, wipe, box);
        _timer.Begin(slot);
        Quad("PSFrostedGlass", work, _canvas!.Rtv, ref constants, blend: true, aux: frosted.Srv);
        _timer.End();
        _gpu.Textures.Return(frosted);
    }

    private void DisposeFx()
    {
        foreach (var flow in _flows)
            flow.Dispose();
        _flows.Clear();
        Com.Release(ref _noiseView);
        Com.Release(ref _noise3D);
    }

    /// <summary>Evicts flows unused for 120 frames (their textures are only worth keeping while a source plays).</summary>
    private void ForgetOldFlows()
    {
        for (var k = _flows.Count - 1; k >= 0; k--)
        {
            if (_frame - _flows[k].LastFrame <= 120)
                continue;
            _flows[k].Dispose();
            _flows.RemoveAt(k);
        }
    }
}
