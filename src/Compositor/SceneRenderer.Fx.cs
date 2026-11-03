using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Pass 5.1-GpuFx: the game-engine effects, slots 22 to 29 -----
// Shaders: Motion.hlsl, Light.hlsl, Vfx.hlsl, PSFrostedGlass in Core.hlsl. Everything here works on the layer's working texture (a Look card) or as the layer is laid
// on (its rim, its glass, its shutter); nothing here asks the decoders for anything, so no effect can make a frame late for want of a frame: the Motion Blur reads what
// the FrameCache already holds, and an effect without what it needs draws the picture as it is.
internal sealed unsafe partial class SceneRenderer
{
    // ----- The dual-Kawase pyramid -----

    private readonly GpuTexture?[] _kawase = new GpuTexture?[KawaseMostLevels];

    private const int KawaseMostLevels = 6;

    /// <summary>
    /// How many halvings a blur reaching so many texels takes on a picture this size, and the taps' offset: its reach is about offset x 2^levels, so any
    /// reach between two whole levels is its own. A picture is never halved below about 4 texels. CompositorReference.Fx.cs takes the same numbers.
    /// </summary>
    internal static (int Levels, float Offset) KawaseShape(float reach, int width, int height)
    {
        reach = Math.Max(float.IsFinite(reach) ? reach : 1, 1);
        var most = Math.Clamp((int)MathF.Floor(MathF.Log2(Math.Max(Math.Min(width, height), 8) / 4f)), 1, KawaseMostLevels);
        var levels = Math.Clamp((int)MathF.Ceiling(MathF.Log2(reach)), 1, most);
        return (levels, Math.Clamp(reach / (1 << levels), 0.5f, 2));
    }

    /// <summary>A pass of the pyramid's size: half of the one before, at least 2 x 2.</summary>
    internal static int KawaseSide(int side) => Math.Max(side / 2, 2);

    /// <summary>A blur reaching <paramref name="reach"/> texels: a new texture half the size of <paramref name="source"/>, which is left as it was.</summary>
    private GpuTexture Kawase(GpuTexture source, float reach, int timedAs)
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

        for (var level = levels - 1; level >= 1; level--)
        {
            Step("PSKawaseUp", _kawase[level]!, _kawase[level - 1]!, ref constants, timedAs);
            _gpu.Textures.Return(_kawase[level]!);
        }

        return _kawase[0]!;
    }

    private void Step(string shader, GpuTexture input, GpuTexture output, ref PassConstants constants, int timedAs, nint aux = 0, nint lut = 0)
    {
        _timer.Begin(timedAs);
        Draw(shader, input, output.Rtv, output.Width, output.Height, ref constants, blend: false, aux: aux, lut: lut);
        _timer.End();
    }

    /// <summary>The bright part of a picture in linear light, at half its size: what a glow and a lens's streaks start from.</summary>
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

    /// <summary>How far a Spread reaches, in px of a 1080-wide frame: a stop of light's spread a step of the slider.</summary>
    internal static float GlowReach(float spread) => MathF.Pow(2, Math.Clamp(spread, 1, 8));

    private GpuTexture Glow(GpuEffectPacket packet, GpuTexture work)
    {
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

    // ----- The Impact Shockwave and the Anamorphic Lens (slot 25) -----

    /// <summary>How far apart the streaks' taps go, pass by pass: each pass three times the last, so seven taps reach far.</summary>
    internal const int StreakPasses = 3;

    private GpuTexture Lens(GpuEffectPacket packet, GpuTexture work)
    {
        if (packet.P7 < 0.5f)
            return packet.P0 <= 0 || packet.P0 >= 1 ? work : Effect("PSShockwave", packet, default, work);
        if (packet.P0 < 0.001f && packet.P6 < 0.001f)
            return Effect("PSAnamorphic", packet, default, work);

        var slot = (int)GpuShaderSlot.ShockwaveLens;
        var streak = Highlights(work, packet.P1, KawaseSide(work.Width), Math.Max(work.Height / 4, 2), slot);
        var constants = default(PassConstants);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        var stride = 0.5f + Math.Clamp(packet.P2, 0, 1) * 1.5f;
        for (var pass = 0; pass < StreakPasses; pass++, stride *= 3)
        {
            var output = _gpu.Textures.Rent(streak.Width, streak.Height);
            constants.Params = new Vector4(_pxScale1080, 0, stride, _pixelScale);
            Step("PSStreak", streak, output, ref constants, slot);
            _gpu.Textures.Return(streak);
            streak = output;
        }

        var lensed = Effect("PSAnamorphic", packet, default, work, aux: streak.Srv);
        _gpu.Textures.Return(streak);
        return lensed;
    }

    // ----- The procedural effects (slots 26 to 29) -----

    /// <summary>A generator's two packets, which always go together, as Color Balance's do.</summary>
    private static bool Paired(GpuShaderSlot slot) => slot is GpuShaderSlot.ColorBalance or GpuShaderSlot.Explosion or GpuShaderSlot.Sparks or GpuShaderSlot.AnimeLines or GpuShaderSlot.EnergyField;

    private GpuTexture Generated(GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        switch (packet.Slot)
        {
            case GpuShaderSlot.Explosion when packet.P0 is > 0 and < 1:
                // At half the size: a soft thing, and the costliest of them.
                return Generator("PSExplosion", packet, second, work, second.P0, shrink: 2, lut: Noise3D());
            case GpuShaderSlot.Sparks when packet.P0 is > 0 and < 1:
                return Sparks(packet, second, work);
            case GpuShaderSlot.AnimeLines:
                return packet.P7 < 0.05f ? work : Generator("PSAnimeLines", packet, second, work, second.P3, shrink: 1);
            case GpuShaderSlot.EnergyField when packet.P0 > 0:
                return Generator(second.P7 > 0.5f ? "PSSaber" : "PSEnergyShield", packet, second, work, second.P0, shrink: 1);
            default:
                return work;
        }
    }

    /// <summary>An effect's picture, drawn from nothing, laid over the input with its light.</summary>
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

    /// <summary>Sparks and debris: the shards laid over, then the sparks added; each particle six vertices worked out from its index (no buffer).</summary>
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

    /// <summary>The Light Spill: the effect's picture, blurred very wide, lights what is under it; the effect itself over that.</summary>
    private GpuTexture Lit(GpuTexture fx, GpuTexture work, float spill, int timedAs)
    {
        var output = _gpu.Textures.Rent(work.Width, work.Height);
        var light = spill >= 0.005f ? Kawase(fx, Math.Max(fx.Height * 0.4f, 8), timedAs) : null;
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

    /// <summary>The Explosion's noise (32 cubed, four tiling fractal noises): baked once, the first time one goes off, in a few milliseconds; half a megabyte at most.</summary>
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
            // A small integer hash of the lattice point, wrapped so the noise tiles.
            var h = (uint)(((a + ix) % period) * 73856093 ^ ((b + iy) % period) * 19349663 ^ ((c + iz) % period) * 83492791 ^ seed * 2654435);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }

        var near = Lerp(Lerp(At(0, 0, 0), At(1, 0, 0), fx), Lerp(At(0, 1, 0), At(1, 1, 0), fx), fy);
        var far = Lerp(Lerp(At(0, 0, 1), At(1, 0, 1), fx), Lerp(At(0, 1, 1), At(1, 1, 1), fx), fy);
        return Lerp(near, far, fz);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ----- The Motion Blur (slot 22): what moves inside the picture -----

    /// <summary>
    /// The optical flow of a frame of a source, or of a canvas under an adjustment layer: kept a while, since the main video, its HUD pieces and a redraw while
    /// paused all ask for one and the same.
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

    /// <summary>The pyramid's levels, and the most flows kept: four sources or adjustment layers at a time, about a megabyte each.</summary>
    private const int FlowLevels = 4, MostFlows = 4;

    /// <summary>The flow's size for a picture: a quarter of it, at most 480 wide, at least 16 high.</summary>
    private static (int Width, int Height) FlowSize(int width, int height)
    {
        var across = Math.Min(Math.Max(width / 4, 32), 480);
        return (across & ~1, Math.Max((int)MathF.Round((float)across * height / Math.Max(width, 1)) & ~1, 16));
    }

    /// <summary>A free entry of the flow's size: a new one, or the one used longest ago.</summary>
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

    /// <summary>The solve from coarse to fine, this frame and the one before at the flow's size, into <paramref name="into"/>. Both given back to the pool.</summary>
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
            // The finest level is solved twice: its first solve's warp is what makes the second exact.
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

    /// <summary>The flow of a media layer's frame from the frame the cache holds before it. False when there is none (a clip's first frame, a frozen moment).</summary>
    private bool MediaFlow(in GpuLayerDescriptor layer, out GpuTexture flow, out float perFrame)
    {
        (flow, perFrame) = (null!, 0);
        if ((layer.Flags & GpuLayerDescriptor.FlagPreviousMoment) == 0)
            return false;
        var step = layer.MediaSeconds - layer.PreviousMoment;
        if (Math.Abs(step) < 1e-5 || _decoders.Resolve(layer.SourceId, layer.MediaSeconds, layer.SourcePathHash, layer.ClipKey) is not { LumaSrv: not 0 } frame)
            return false;

        FlowEntry? entry = null;
        foreach (var known in _flows)
        {
            if (known.Source == frame.Source && known.Ready && Math.Abs(known.Time - frame.Time) < 1e-6)
                entry = known;
        }

        if (entry is null)
        {
            if (_decoders.FrameBefore(frame) is not { LumaSrv: not 0 } before)
                return false;

            var (width, height) = FlowSize(frame.Width, frame.Height);
            entry = TakeFlow(width, height);
            var whole = new Vector4(0, 0, 1, 1);
            var (now, was) = (_gpu.Textures.Rent(width, height), _gpu.Textures.Rent(width, height));
            DrawFrame(frame, now, whole);
            DrawFrame(before, was, whole);
            SolveFlow(now, was, entry.Field!);
            (entry.Source, entry.Time, entry.Gap, entry.Ready) = (frame.Source, frame.Time, frame.Time - before.Time, true);
        }

        entry.LastFrame = _frame;
        (flow, perFrame) = (entry.Field!, (float)(step / Math.Max(entry.Gap, 1e-4)));
        return true;
    }

    /// <summary>The flow of the canvas under an adjustment layer (or the Sequence Output) from the last one it was drawn on, while frames come in order.</summary>
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

        // A redraw of the same moment changes nothing.
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
            // The frame kept goes to the solve, this one takes its place.
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

    /// <summary>The picture smeared along what moves in it, across the shutter (two passes of the samples: their square, without its cost).</summary>
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
        var (stepX, stepY) = (share / flow.Width / Math.Max(part.Z - part.X, 1e-4f), share / flow.Height / Math.Max(part.W - part.Y, 1e-4f));
        var longest = Math.Clamp(packet.P5, 0.1f, 50) / 100;
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

    // ----- The Motion Blur: what the layer itself does -----

    /// <summary>
    /// Sub-frames of the laid-on picture, summed along the way from the shutter's opening to its closing (the plan's lanes): a picture of the whole target, laid on as the
    /// layer is. Exact: every sub-frame is the layer where it really is at that moment (linearly between the two: a shutter's moment is short).
    /// </summary>
    private (GpuTexture Work, (float X, float Y, float W, float H) Box, float AnchorX, float AnchorY, float Rotation) Shutter(
        in GpuLayerDescriptor layer, GpuEffectPacket shutter, GpuTexture work, (float X, float Y, float W, float H) box, float anchorX, float anchorY, GpuEffectPacket? wipe)
    {
        var amount = Math.Clamp(shutter.P4, 0, 100) / 100;
        var samples = Math.Clamp((int)MathF.Round(shutter.P1), 8, 16);
        var target = _gpu.Textures.Rent(_outWidth, _outHeight);
        _gpu.Context.ClearRenderTargetView(target.Rtv, 0, 0, 0, 0);

        // The point the layer is placed by, on the canvas as drawn now (a rim's room or a slide has moved the box: the shutter's way is added to where it is).
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
            constants.Transform = Matrix4.Box(x, y, w, h, turn, anchorX, anchorY, _outWidth, _outHeight);
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

    // ----- The Neon Rim Light and Frosted Glass (slot 24) -----

    /// <summary>How far a rim's light reaches from its shape, in texels.</summary>
    private float RimRoom(GpuEffectPacket rim) => Math.Clamp(MathF.Ceiling((Math.Max(rim.P3, 0) / 2 + Math.Max(rim.P4, 0) * 1.5f) * _pixelScale) + 2, 3, 512);

    /// <summary>
    /// The rim on the working picture, on a larger one with room round it for its glow, and the box grown to match (about the same point of the picture). Its distance
    /// field by jump flooding, a pass for each halving of how far it must reach; at half the size for a large picture, where a glow loses nothing by it.
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
    /// Frosted Glass: the canvas under the layer's shape, blurred, tinted and lit along its edge, laid down before the layer's picture (at what share of it stays). The
    /// canvas is copied and blurred whole, so a turned layer and its neighbours read it the same way.
    /// </summary>
    private void DrawFrostedGlass(GpuEffectPacket glass, GpuTexture work, (float X, float Y, float W, float H) box, float rotation, float anchorX, float anchorY, float alpha, GpuEffectPacket? wipe)
    {
        if (!_shaders.Has("PSFrostedGlass"))
            return;

        var slot = (int)GpuShaderSlot.RimGlass;
        _canvasCopy ??= _gpu.CreateTexture(_outWidth, _outHeight, DxgiFormat.R16G16B16A16Float, renderTarget: false);
        _gpu.Context.CopyResource(_canvasCopy.Texture, _canvas!.Texture);
        var frosted = Kawase(_canvasCopy, Math.Max(glass.P0 * _pixelScale, 2), slot);

        var constants = default(PassConstants);
        constants.Packet = glass;
        constants.Transform = Matrix4.Box(box.X, box.Y, box.W, box.H, rotation, anchorX, anchorY, _outWidth, _outHeight);
        constants.Params = new Vector4(_pxScale1080, 0, 0, _pixelScale);
        constants.Params4 = new Vector4(alpha, 1, 0, (float)_time);
        constants.WholeUv();
        SetWipe(ref constants, wipe, box);
        _timer.Begin(slot);
        Quad("PSFrostedGlass", work, _canvas.Rtv, ref constants, blend: true, aux: frosted.Srv);
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

    /// <summary>Flows not used for two seconds' worth of frames go (their textures are only worth keeping while a source plays).</summary>
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
