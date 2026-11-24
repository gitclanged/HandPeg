using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// Renders one <see cref="FrameSceneHeader"/> and its layers into a render target. Each layer, bottom first: its picture into a working
/// texture at on-screen size (Zone 0: source cropped and scaled with Catmull-Rom or a box prefilter), its effects in order on that texture
/// (Zones 1-3, ping-ponging two textures), then composited onto the canvas (Zone 4: blur fill, shadow, transform, opacity, transitions,
/// blend mode). The canvas (RGBA16F, premultiplied) is resolved to the output last. No readback and no graph rebuilds: a changed value is
/// just a different constant next frame.
/// </summary>
internal sealed unsafe partial class SceneRenderer : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ShaderLibrary _shaders;
    private readonly DecoderPool _decoders;
    private readonly ResourceCache _resources;
    private readonly PassTimer _timer;
    private GpuTexture? _canvas;
    private GpuTexture? _canvasCopy;

    // Flat-path canvases: full size and scaled (Dynamic Preview Scaling), each kept at its own capacity so play/pause and scrub start/stop
    // allocate nothing. _canvas aliases the current one: dispose these, never _canvas.
    private GpuTexture? _canvasWhole, _canvasScaled;

    // Matching canvas copies read by blend modes and Frosted Glass, kept alongside so switching allocates nothing. _canvasCopy aliases the
    // current one (3D planes and the loupe use their own): dispose these, never _canvasCopy.
    private GpuTexture? _canvasCopyWhole, _canvasCopyScaled;
    private long _frame;

    // Current frame state.
    private int _outWidth, _outHeight;
    private float _scaleX, _scaleY;
    private float _offsetX, _offsetY;
    private bool _letterbox;
    private (float X, float Y, float W, float H) _frameRect;
    private GpuTexture? _blank;
    private float _pxScale1080, _pixelScale;
    private double _time;

    public SceneRenderer(GpuDevice gpu, ShaderLibrary shaders, DecoderPool decoders, ResourceCache resources)
    {
        (_gpu, _shaders, _decoders, _resources) = (gpu, shaders, decoders, resources);
        _timer = new PassTimer(gpu);
    }

    public PassTimer Timer => _timer;

    /// <summary>Renders the scene into <paramref name="outputRtv"/> of the given size. <paramref name="raw"/>: premultiplied canvas, no background.</summary>
    public void Render(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, nint outputRtv, int outputWidth, int outputHeight, bool raw)
    {
        _frame++;
        _playing = header.IsPlaying != 0;
        (_audioBass, _audioMid, _audioHigh) = (Band(header.AudioBass), Band(header.AudioMid), Band(header.AudioHigh));
        _decoders.BeginFrame(_frame, _playing, header.PlayheadSeconds, exactOnly: (header.Flags & FrameSceneHeader.FlagExactFrames) != 0,
            scrubbing: (header.Flags & FrameSceneHeader.FlagScrubbing) != 0, ghost: (header.Flags & FrameSceneHeader.FlagGhostScrub) != 0);
        _resources.BeginFrame();
        try
        {
            RenderLayers(header, layers, outputRtv, outputWidth, outputHeight, raw, DrawScale(header, raw));
        }
        finally
        {
            // Always unpin frames and report missing ones to the decode lanes.
            _decoders.EndFrame();
        }
    }

    private bool _playing;

    /// <summary>
    /// Player canvas placement in an output of this size (<see cref="FrameSceneHeader.FlagLetterbox"/>): fit and centre, zoom about the
    /// centre, then pan. Returns the picture's top-left on the output and output pixels per canvas pixel. Shared by the flat path and the
    /// Live Encoded Preview compare pass.
    /// </summary>
    internal static (float OffsetX, float OffsetY, float Scale) Letterbox(in FrameSceneHeader header, int outputWidth, int outputHeight)
    {
        var canvasWidth = Math.Max(header.CanvasWidth, 1);
        var canvasHeight = Math.Max(header.CanvasHeight, 1);
        var fit = Math.Min((float)outputWidth / canvasWidth, (float)outputHeight / canvasHeight);
        var zoom = Math.Clamp(header.ViewScale > 0 ? header.ViewScale : 1, 0.05f, 64f);
        var (pictureWidth, pictureHeight) = (canvasWidth * fit, canvasHeight * fit);
        var scale = fit * zoom;
        return (outputWidth / 2f + header.ViewPanX * pictureWidth * zoom - canvasWidth * scale / 2,
            outputHeight / 2f + header.ViewPanY * pictureHeight * zoom - canvasHeight * scale / 2, scale);
    }

    /// <summary>
    /// Render scale for flat player frames (Dynamic Preview Scaling: <see cref="FrameSceneHeader.PreviewScale"/>, 0.25 to 1), stretched to the
    /// output by the final pass; 1 for readback, exact, raw, 3D or loupe frames.
    /// </summary>
    internal static float DrawScale(in FrameSceneHeader header, bool raw)
    {
        var scale = header.PreviewScale;
        if (!(scale > 0 && scale < 0.999f) || raw || header.ViewMode != 0 || header.LoupeClipKey != 0
            || (header.Flags & (FrameSceneHeader.FlagReadback | FrameSceneHeader.FlagExactFrames)) != 0 || (header.Flags & FrameSceneHeader.FlagLetterbox) == 0)
            return 1;
        return Math.Max(scale, 0.25f);
    }

    private void RenderLayers(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, nint outputRtv, int outputWidth, int outputHeight, bool raw, float drawScale = 1)
    {
        // 3D exploded view has its own path (SceneRenderer.Exploded.cs).
        if (header.ViewMode == 1)
        {
            RenderExploded(header, layers, outputRtv, outputWidth, outputHeight, raw);
            return;
        }

        // Scaled: everything below renders at the reduced size; only the final pass fills the full output.
        var (finalWidth, finalHeight) = (outputWidth, outputHeight);
        if (drawScale < 1)
            (outputWidth, outputHeight) = (Math.Max((int)MathF.Round(outputWidth * drawScale), 16), Math.Max((int)MathF.Round(outputHeight * drawScale), 16));

        // Release 3D view textures on the first flat frame.
        ForgetExplodedTextures();
        _outWidth = outputWidth;
        _outHeight = outputHeight;
        var canvasWidth = Math.Max(header.CanvasWidth, 1);
        var canvasHeight = Math.Max(header.CanvasHeight, 1);
        _letterbox = (header.Flags & FrameSceneHeader.FlagLetterbox) != 0;
        if (_letterbox)
        {
            (_offsetX, _offsetY, _scaleX) = Letterbox(header, outputWidth, outputHeight);
            _scaleY = _scaleX;
        }
        else
        {
            _scaleX = (float)outputWidth / canvasWidth;
            _scaleY = (float)outputHeight / canvasHeight;
            (_offsetX, _offsetY) = (0, 0);
        }

        // Frame rectangle on the output (scissor for every composited layer).
        _frameRect = (_offsetX, _offsetY, canvasWidth * _scaleX, canvasHeight * _scaleY);
        _pxScale1080 = canvasWidth * _scaleX / 1080f;
        _pixelScale = _scaleX;
        _time = header.PlayheadSeconds;

        var timing = (header.Flags & FrameSceneHeader.FlagTimePasses) != 0;
        _timer.BeginFrame(timing);
        _gpu.BindCommon();

        // The canvas is kept at a capacity (CompositorWorker.OutputCapacity) and drawn into its top-left, so dragging a dock splitter creates
        // a few canvases rather than one per pixel; exports use exactly their size. The blend copy matches the drawn region (CopyCanvas). The
        // scaled canvas only grows while the frame fits (smaller steps draw into its top-left), so returning to a run's start allocates nothing.
        var scaled = drawScale < 1;
        ref var slot = ref scaled ? ref _canvasScaled : ref _canvasWhole;
        var capacity = scaled && slot is not null && outputWidth <= slot.Width && outputHeight <= slot.Height
                       && (long)outputWidth * outputHeight * 8 >= (long)slot.Width * slot.Height
            ? (slot.Width, slot.Height)
            : CompositorWorker.OutputCapacity(slot?.Width ?? 0, slot?.Height ?? 0, outputWidth, outputHeight, (header.Flags & FrameSceneHeader.FlagReadback) != 0);
        if (slot is null || (slot.Width, slot.Height) != capacity)
        {
            slot?.Dispose();
            slot = _gpu.CreateTexture(capacity.Width, capacity.Height, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        }

        _canvas = slot;

        // The copy matches the drawn region exactly: recreated only on size change (CopyCanvas creates it when a blend first needs it).
        ref var copy = ref scaled ? ref _canvasCopyScaled : ref _canvasCopyWhole;
        if (copy is not null && (copy.Width != outputWidth || copy.Height != outputHeight))
        {
            copy.Dispose();
            copy = null;
        }

        _canvasCopy = copy;

        _gpu.Context.ClearRenderTargetView(_canvas.Rtv, 0, 0, 0, 0);

        // Letterboxed: the sequence background fills only the picture; the surround stays black.
        if (_letterbox)
        {
            var fill = default(PassConstants);
            fill.Color = new Vector4(header.BackgroundR, header.BackgroundG, header.BackgroundB, 1);
            fill.Transform = Matrix4.Box(_frameRect.X, _frameRect.Y, _frameRect.W, _frameRect.H, 0, 0.5f, 0.5f, _outWidth, _outHeight);
            fill.WholeUv();
            fill.Params4 = new Vector4(1, 1, 0, (float)_time);
            _blank ??= _gpu.CreateTexture(2, 2, DxgiFormat.R16G16B16A16Float, renderTarget: true);
            Quad("PSSolid", _blank, _canvas.Rtv, ref fill, blend: false);
        }

        foreach (ref readonly var layer in layers)
        {
            if ((layer.Flags & GpuLayerDescriptor.FlagVisible) == 0 || layer.Width == 0 || layer.Height == 0)
                continue;
            DrawLayer(layer);
            _gpu.Textures.ReturnAll();
        }

        // Magnifier loupe over the picture while an edge is tuned (player only).
        if (header.LoupeClipKey != 0 && _letterbox && !raw && PrepareLoupe(header, layers, outputWidth, outputHeight))
            CompositeLoupe(_canvas.Rtv, outputWidth, outputHeight);

        // Resolve the canvas to the output.
        var constants = default(PassConstants);
        constants.Color = _letterbox ? new Vector4(0, 0, 0, 1) : new Vector4(header.BackgroundR, header.BackgroundG, header.BackgroundB, 1);
        constants.Params4 = new Vector4(1, 1, raw ? 1 : 0, (float)_time);
        (constants.U0, constants.V0, constants.U1, constants.V1) = (0, 0, (float)outputWidth / _canvas.Width, (float)outputHeight / _canvas.Height);
        if ((finalWidth, finalHeight) != (outputWidth, outputHeight))
        {
            // Stretched with filtering: inset the far edges by half a texel so the filter never samples cleared texels beyond the drawn
            // region (otherwise a dark fringe appears along the right and bottom).
            constants.U1 = (outputWidth - 0.5f) / _canvas.Width;
            constants.V1 = (outputHeight - 0.5f) / _canvas.Height;
            constants.Params2 = new Vector4(0, 1, 0, 0);
        }
        _timer.Begin(0);
        Draw("PSFinal", _canvas, outputRtv, finalWidth, finalHeight, ref constants, blend: false);
        _timer.End();
        _gpu.Textures.EndFrame();
        _timer.EndFrame();
        ForgetOldHistories();
    }

    // ----- Layer rendering -----

    private void DrawLayer(in GpuLayerDescriptor layer)
    {
        _layerStep = 0;
        // Output box and working texture size (on-screen coverage, at least 2 x 2).
        var box = (X: layer.X * _scaleX + _offsetX, Y: layer.Y * _scaleY + _offsetY, W: layer.Width * _scaleX, H: layer.Height * _scaleY);
        var workWidth = (int)Math.Clamp(MathF.Round(MathF.Abs(box.W)), 2, 8192);
        var workHeight = (int)Math.Clamp(MathF.Round(MathF.Abs(box.H)), 2, 8192);
        if (OffScreen(box, layer.Rotation, tilted: layer.TiltX != 0 || layer.TiltY != 0))
            return;

        // Gather composite-time parameters from the packets, wherever they appear.
        var opacity = Math.Clamp(layer.Opacity, 0, 1);
        var blendMode = layer.BlendMode;
        var dissolve = 1f;
        GpuEffectPacket? shadow = null, wipe = null, slide = null, zoom = null, blurDissolve = null, snap = null, rim = null, glass = null, shutter = null, social = null;
        var effects = layer.Effects;
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        for (var k = 0; k < count; k++)
        {
            var packet = effects[k];
            switch (packet.Slot)
            {
                case GpuShaderSlot.TransformComposite:
                    opacity *= Math.Clamp(packet.P0 / 100f, 0, 1);
                    break;
                case GpuShaderSlot.DropShadow:
                    shadow = packet;
                    break;
                case GpuShaderSlot.BlendMode:
                    blendMode = (int)MathF.Round(packet.P0);
                    break;
                case GpuShaderSlot.TransitionDissolve:
                    dissolve *= Math.Clamp(packet.P0, 0, 1);
                    break;
                case GpuShaderSlot.TransitionWipe:
                    wipe = packet;
                    break;
                case GpuShaderSlot.TransitionSlide:
                    slide = packet;
                    break;
                case GpuShaderSlot.TransitionZoom:
                    zoom = packet;
                    var through = Math.Clamp(packet.P3, 0, 1);
                    dissolve *= Math.Clamp((packet.P4 > 0.5f ? through : 1 - through) * 5, 0, 1);
                    break;
                case GpuShaderSlot.TransitionBlurDissolve:
                    blurDissolve = packet;
                    dissolve *= Math.Clamp(packet.P0, 0, 1);
                    break;
                case GpuShaderSlot.TransitionParticleSnap:
                    snap = packet;
                    break;
                case GpuShaderSlot.TransitionWhipPan or GpuShaderSlot.TransitionZoomWarp or GpuShaderSlot.TransitionRgbGlitch or GpuShaderSlot.TransitionSpin:
                    social = packet;
                    dissolve *= SocialCutDissolve(packet);
                    break;
                case GpuShaderSlot.RimGlass when packet.P7 > 0.5f:
                    glass = packet;
                    break;
                case GpuShaderSlot.RimGlass when GpuEffectPacket.IsPulsePacket(packet):
                    // Orphan pulse packet (no preceding effect packet): nothing to scale.
                    break;
                case GpuShaderSlot.RimGlass:
                    // Optional following pulse packet (marked with GpuEffectPacket.PulseMarker; sent only while the pulse is used): pulse and
                    // band scale the rim strength.
                    if (k + 1 < count && effects[k + 1] is { Slot: GpuShaderSlot.RimGlass } pulse && GpuEffectPacket.IsPulsePacket(pulse))
                    {
                        var lift = PulseOf(pulse.P0, pulse.P1);
                        rim = lift == 1 ? packet : packet with { P5 = packet.P5 * lift };
                        k++;
                    }
                    else
                    {
                        rim = packet;
                    }

                    break;
                case GpuShaderSlot.MotionBlur when packet.P3 >= 0.5f && packet.P4 >= 0.5f && (layer.Flags & GpuLayerDescriptor.FlagShutterPlace) != 0:
                    shutter = packet;
                    break;
            }
        }

        // Drawn alone (loupe and its edge search, SceneRenderer.Loupe.cs): only the layer's picture and edges, at rest: no blur fill,
        // shadow, opacity, blend or transition.
        if (_alone)
        {
            (opacity, dissolve, blendMode) = (1, 1, 0);
            (shadow, wipe, slide, zoom, blurDissolve, snap, rim, glass, shutter, social) = (null, null, null, null, null, null, null, null, null, null);
        }

        if (opacity * dissolve <= 0.0005f)
            return;

        // Blur-fill-only layer (a clip's twin): full frame at its stack position. Radius 0 still draws (a sharp background).
        if ((layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0)
        {
            DrawBlurFill(layer, opacity * dissolve, withLook: true);
            return;
        }

        // Zone 0: source picture.
        var work = _gpu.Textures.Rent(workWidth, workHeight);
        var adjustment = (layer.Flags & GpuLayerDescriptor.FlagAdjustment) != 0;
        if (adjustment ? !CopyCanvasInto(work, box) : !DrawSource(layer, work, CropUv(layer), cover: false))
            return;

        // Zones 1-3: effects in order.
        for (var k = 0; k < count; k++)
        {
            var packet = effects[k];
            // Paired second packet, or none: a standalone effect never receives the next effect's packet.
            var second = k + 1 < count && Paired(packet, effects[k + 1]) ? effects[k + 1] : default;
            if (second.Slot != GpuShaderSlot.None)
                k++;
            work = RunEffect(layer, packet, second, work);
        }

        // Blur dissolve: maximum blur where the picture has not yet appeared.
        if (blurDissolve is { } blurring)
        {
            var most = Math.Clamp((int)MathF.Round(Math.Max(workWidth, workHeight) / 40f), 2, 64);
            var shown = blurring.P4 > 0.5f ? blurring.P3 : 1 - blurring.P3;
            var radius = Math.Max((int)MathF.Round(most * (1 - Math.Clamp(shown, 0, 1))), 1);
            work = Blur(work, radius, iterations: 1, (int)GpuShaderSlot.TransitionBlurDissolve);
        }

        // Social cut pass over the processed picture, before placement.
        if (social is { } cut)
            work = SocialCut(layer, cut, work);

        // Placement: slides translate the box, zooms scale it about its centre.
        if (slide is { } sliding)
        {
            var side = sliding.P4 > 0.5f ? -1 : 1;
            var away = 1 - Math.Clamp(sliding.P0, 0, 1);
            if (sliding.P1 != 0)
                box.X += Beyond(MathF.Sign(sliding.P1) * side, box.X - _frameRect.X, box.W, _frameRect.W) * away;
            if (sliding.P2 != 0)
                box.Y += Beyond(MathF.Sign(sliding.P2) * side, box.Y - _frameRect.Y, box.H, _frameRect.H) * away;
        }

        if (zoom is { } zooming)
        {
            var factor = Math.Max(zooming.P0, 0);
            var (cx, cy) = (box.X + box.W / 2, box.Y + box.H / 2);
            box = (cx - box.W * factor / 2, cy - box.H * factor / 2, box.W * factor, box.H * factor);
        }

        // Beneath it: blur fill, then shadow (fading with a particle-snapped picture).
        var under = opacity * dissolve * (snap is { } snapping ? Math.Clamp(snapping.P0, 0, 1) : 1);
        if (layer.BlurFillRadius > 0 && !adjustment && !_alone)
            DrawBlurFill(layer, under, withLook: false);
        if (shadow is { } shading)
            DrawShadow(layer, shading, work, box, under, wipe);

        // Particle Snap: embers fly beyond the box, so the box grows to contain them (pivot preserved).
        var (anchorX, anchorY) = (layer.AnchorX, layer.AnchorY);
        if (snap is { } snapped)
            (work, box, anchorX, anchorY) = ParticleSnap(layer, snapped, work, box);

        // Frosted Glass: blurred canvas under the shape, drawn before the remaining picture share; Neon Rim on the picture with glow padding;
        // shutter last over all of it (sub-frames of the composited picture).
        var rotation = layer.Rotation + (social is { Slot: GpuShaderSlot.TransitionSpin } spin ? SpinTurn(spin) : 0);
        var share = 1f;
        if (glass is { } frost)
        {
            DrawFrostedGlass(frost, work, box, rotation, anchorX, anchorY, opacity * dissolve, wipe, (layer.TiltX, layer.TiltY));
            share = Math.Clamp(frost.P5, 0, 1);
        }

        if (rim is { } lit)
            (work, box, anchorX, anchorY) = NeonRim(lit, work, box, anchorX, anchorY, share);
        else
            opacity *= share;

        if (opacity * dissolve <= 0.0005f)
            return;

        if (shutter is { } open)
        {
            (work, box, anchorX, anchorY, rotation) = Shutter(layer, open, work, box, anchorX, anchorY, wipe);
            wipe = null;
        }

        // Zone 4: composite (with optional 3D tilt in perspective).
        var constants = default(PassConstants);
        constants.Transform = Matrix4.Box(box.X, box.Y, box.W, box.H, rotation, anchorX, anchorY, _outWidth, _outHeight, layer.TiltX, layer.TiltY);
        constants.WholeUv();
        constants.Params4 = new Vector4(opacity, dissolve, Math.Clamp(blendMode, 0, 7), (float)_time);
        SetWipe(ref constants, wipe, box);
        if (blendMode is > 0 and <= 7)
        {
            // Blend modes read the canvas beneath via a copy, sampled where the layer lands.
            var canvasUnder = CopyCanvas();
            _timer.Begin((int)GpuShaderSlot.BlendMode);
            Quad("PSCompositeBlend", work, _canvas!.Rtv, ref constants, blend: false, aux: canvasUnder.Srv);
            _timer.End();
        }
        else
        {
            _timer.Begin((int)GpuShaderSlot.TransformComposite);
            Quad("PSComposite", work, _canvas!.Rtv, ref constants, blend: true);
            _timer.End();
        }
    }

    /// <summary>
    /// Particle Snap (slot 69, ParticleSnap.hlsl): draws the working picture into a larger texture padded for the embers and grows the box
    /// to match.
    /// </summary>
    private (GpuTexture Work, (float X, float Y, float W, float H) Box, float AnchorX, float AnchorY) ParticleSnap(
        in GpuLayerDescriptor layer, GpuEffectPacket snap, GpuTexture work, (float X, float Y, float W, float H) box)
    {
        if (!_shaders.Has("PSParticleSnap"))
            return (work, box, layer.AnchorX, layer.AnchorY);

        var (roomX, roomY) = ((int)MathF.Ceiling(work.Width * SnapRoom), (int)MathF.Ceiling(work.Height * SnapRoom));
        var longest = Math.Max(work.Width, work.Height);
        (roomX, roomY) = (Math.Max(roomX, (int)(longest * SnapRoom / 2)), Math.Max(roomY, (int)(longest * SnapRoom / 2)));
        var output = _gpu.Textures.Rent(work.Width + 2 * roomX, work.Height + 2 * roomY);
        var constants = default(PassConstants);
        constants.Packet = snap;
        constants.Params = new Vector4(_pxScale1080, 0, 0, _pixelScale);
        constants.Params2 = new Vector4((float)roomX / output.Width, (float)roomY / output.Height, (float)work.Width / output.Width, (float)work.Height / output.Height);
        constants.Params3 = new Vector4(work.Width, work.Height, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        _timer.Begin((int)GpuShaderSlot.TransitionParticleSnap);
        Draw("PSParticleSnap", work, output.Rtv, output.Width, output.Height, ref constants, blend: false);
        _timer.End();

        var (perX, perY) = (box.W / work.Width, box.H / work.Height);
        var grown = (box.X - roomX * perX, box.Y - roomY * perY, output.Width * perX, output.Height * perY);
        var anchorX = (roomX + layer.AnchorX * work.Width) / output.Width;
        var anchorY = (roomY + layer.AnchorY * work.Height) / output.Height;
        _gpu.Textures.Return(work);
        return (output, grown, anchorX, anchorY);
    }

    // Ember padding around a snapped picture, as a fraction of its size per side.
    private const float SnapRoom = 0.3f;

    /// <summary>Distance a slide must move the box to be fully off-frame (the slide transition's reach).</summary>
    private static float Beyond(float toward, float at, float size, float frame) => toward > 0 ? frame - at + 2 : -(at + size) - 2;

    private void SetWipe(ref PassConstants constants, GpuEffectPacket? wipe, (float X, float Y, float W, float H) box)
    {
        if (wipe is not { } w)
            return;
        var (dx, dy) = (w.P1, w.P2);
        var fadeIn = w.P4 > 0.5f;
        var keepLow = fadeIn ? dx + dy > 0 : dx + dy < 0;
        var size = (MathF.Abs(box.W), MathF.Abs(box.H));
        var (shown, feather) = (Math.Clamp(w.P0, 0, 1), Math.Max(2, MathF.Round(Math.Max(size.Item1, size.Item2) * 0.02f)));
        if (w.P5 > 0)
        {
            // Grade Before/After (GpuScenePlan.CompareWipe): P0 = divider position across the box, P5 = edge width in pixels; both
            // sides' edges are centred on the divider so the halves meet without gap or seam.
            feather = w.P5;
            var across = dx != 0 ? size.Item1 : size.Item2;
            var at = (shown * across + feather / 2) / (across + feather);
            shown = keepLow ? at : 1 - at;
        }

        constants.Params2 = new Vector4(1, keepLow ? 1 : 0, dx != 0 ? 0 : 1, shown);
        constants.Params3 = new Vector4(feather, 0, size.Item1, size.Item2);
    }

    private bool OffScreen((float X, float Y, float W, float H) box, float rotation, bool tilted = false)
    {
        // Conservative test: the box's circumscribed circle vs. the frame (doubled for 3D-tilted boxes, whose near side renders larger).
        var (cx, cy) = (box.X + box.W / 2, box.Y + box.H / 2);
        var radius = MathF.Sqrt(box.W * box.W + box.H * box.H) / 2 * (tilted ? 2 : 1) + 2;
        return cx + radius < 0 || cy + radius < 0 || cx - radius > _outWidth || cy - radius > _outHeight;
    }

    private static Vector4 CropUv(in GpuLayerDescriptor layer)
    {
        var (l, t, r, b) = (layer.CropLeft, layer.CropTop, layer.CropRight, layer.CropBottom);
        if (r <= l || b <= t)
            (l, t, r, b) = (0, 0, 1, 1);
        return new Vector4(Math.Clamp(l, 0, 1), Math.Clamp(t, 0, 1), Math.Clamp(r, 0, 1), Math.Clamp(b, 0, 1));
    }

    // ----- Zone 0 -----

    /// <summary>
    /// Draws the layer's picture region <paramref name="uv"/> into <paramref name="target"/>; with <paramref name="cover"/> the region is
    /// further cropped to the target's aspect (blur fill covers the frame).
    /// </summary>
    private bool DrawSource(in GpuLayerDescriptor layer, GpuTexture target, Vector4 uv, bool cover)
    {
        var constants = default(PassConstants);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        switch ((GpuSourceKind)layer.SourceKind)
        {
            case GpuSourceKind.Media:
                {
                    // Instant Seek: the exact frame, or the nearest decoded one while the exact frame is pending.
                    var frame = _decoders.Resolve(layer.SourceId, layer.MediaSeconds, layer.SourcePathHash, layer.ClipKey);
                    if (frame is null || frame.LumaSrv == 0)
                        return false;
                    if (!cover)
                        _layerStep = double.IsFinite(frame.Until) && frame.Until > frame.Time ? frame.Until - frame.Time : 0;
                    if (cover)
                        uv = Cover(uv, frame.Width, frame.Height, target.Width, target.Height);
                    // Smooth Slow-Mo: optical-flow in-between of this frame and the next (SceneRenderer.Interpolate.cs).
                    if (!cover && (layer.Flags & GpuLayerDescriptor.FlagInterpolate) != 0 && Interpolate(layer, frame, target, uv))
                        return true;
                    DrawFrame(frame, target, uv, layer.Flags);
                    return true;
                }

            case GpuSourceKind.Still:
                {
                    var still = _resources.Texture2D(layer.StillResourceId);
                    if (still is null)
                        return false;
                    if (cover)
                        uv = Cover(uv, still.Width, still.Height, target.Width, target.Height);
                    constants.U0 = uv.X;
                    constants.V0 = uv.Y;
                    constants.U1 = uv.Z;
                    constants.V1 = uv.W;
                    constants.Params2 = new Vector4(still.Width, still.Height, 0, 0);
                    constants.Params3 = new Vector4((uv.Z - uv.X) * still.Width / target.Width, (uv.W - uv.Y) * still.Height / target.Height, 0, 0);
                    _timer.Begin(0);
                    Draw("PSRgbSource", still, target.Rtv, target.Width, target.Height, ref constants, blend: false);
                    _timer.End();
                    return true;
                }

            case GpuSourceKind.SolidColor:
                constants.Color = new Vector4(layer.SolidR, layer.SolidG, layer.SolidB, layer.SolidA);
                _timer.Begin(0);
                Draw("PSSolid", null, target.Rtv, target.Width, target.Height, ref constants, blend: false);
                _timer.End();
                return true;

            case GpuSourceKind.TestPattern:
                {
                    var tinted = layer.SolidR != 0 || layer.SolidG != 0 || layer.SolidB != 0 || layer.SolidA != 0;
                    constants.Color = tinted ? new Vector4(layer.SolidR, layer.SolidG, layer.SolidB, layer.SolidA) : new Vector4(1, 1, 1, 1);
                    constants.Params2 = new Vector4(layer.PatternKind, 0, 0, 0);
                    _timer.Begin(0);
                    Draw("PSPattern", null, target.Rtv, target.Width, target.Height, ref constants, blend: false);
                    _timer.End();
                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Draws region <paramref name="uv"/> of a decoded frame into a target, deinterlaced and SDR tone-mapped per the layer flags
    /// (<see cref="GpuLayerDescriptor.FlagDeinterlace"/>, <see cref="GpuLayerDescriptor.FlagToneMapSdr"/>: PQ/HLG frames only).
    /// </summary>
    private void DrawFrame(CachedFrame frame, GpuTexture target, Vector4 uv, int layerFlags = 0)
    {
        var constants = default(PassConstants);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        SetYuv(ref constants, frame.Matrix, frame.FullRange);
        constants.U0 = uv.X;
        constants.V0 = uv.Y;
        constants.U1 = uv.Z;
        constants.V1 = uv.W;
        constants.Params2 = new Vector4(frame.Width, frame.Height, frame.Width / 2, frame.Height / 2);
        var toneMap = (layerFlags & GpuLayerDescriptor.FlagToneMapSdr) != 0 && frame.Transfer is 15 or 16 ? frame.Transfer : 0;
        constants.Params3 = new Vector4((uv.Z - uv.X) * frame.Width / target.Width, (uv.W - uv.Y) * frame.Height / target.Height,
            (layerFlags & GpuLayerDescriptor.FlagDeinterlace) != 0 ? 1 : 0, toneMap);
        var planes = stackalloc nint[2] { frame.LumaSrv, frame.ChromaSrv };
        _gpu.Context.PSSetShaderResources(3, 2, planes);
        _timer.Begin(0);
        Draw("PSYuvSource", null, target.Rtv, target.Width, target.Height, ref constants, blend: false);
        _timer.End();
        var none = stackalloc nint[2];
        _gpu.Context.PSSetShaderResources(3, 2, none);
    }

    /// <summary>Centre-crops a UV region to the target aspect (cover: no bars).</summary>
    private static Vector4 Cover(Vector4 uv, int width, int height, int targetWidth, int targetHeight)
    {
        var partWidth = (uv.Z - uv.X) * width;
        var partHeight = (uv.W - uv.Y) * height;
        if (partWidth <= 0 || partHeight <= 0)
            return uv;
        var targetAspect = (float)targetWidth / Math.Max(targetHeight, 1);
        if (partWidth / partHeight > targetAspect)
        {
            var keep = partHeight * targetAspect / partWidth;
            var cut = (uv.Z - uv.X) * (1 - keep) / 2;
            return new Vector4(uv.X + cut, uv.Y, uv.Z - cut, uv.W);
        }
        else
        {
            var keep = partWidth / targetAspect / partHeight;
            var cut = (uv.W - uv.Y) * (1 - keep) / 2;
            return new Vector4(uv.X, uv.Y + cut, uv.Z, uv.W - cut);
        }
    }

    /// <summary>Sets YUV-to-RGB matrix rows for a frame's matrix and range (limited: Y 16-235, C 16-240 in 8-bit terms).</summary>
    internal static void SetYuv(ref PassConstants constants, int matrix, bool fullRange)
    {
        var (kr, kb) = matrix switch
        {
            2 => (0.299f, 0.114f),        // BT.601
            4 => (0.2627f, 0.0593f),      // BT.2020
            _ => (0.2126f, 0.0722f),      // BT.709
        };
        var kg = 1 - kr - kb;
        var (yScale, yOffset, cScale) = fullRange ? (1f, 0f, 1f) : (255f / 219f, -16f / 255f, 255f / 224f);

        // rgb = M * (yScale * (y + yOffset), cScale * (cb - 0.5), cScale * (cr - 0.5))
        var rCr = 2 * (1 - kr) * cScale;
        var bCb = 2 * (1 - kb) * cScale;
        var gCb = -2 * kb * (1 - kb) / kg * cScale;
        var gCr = -2 * kr * (1 - kr) / kg * cScale;
        var lumaOffset = yScale * yOffset;
        constants.YuvRow0 = new Vector4(yScale, 0, rCr, lumaOffset - rCr * 0.5f);
        constants.YuvRow1 = new Vector4(yScale, gCb, gCr, lumaOffset - (gCb + gCr) * 0.5f);
        constants.YuvRow2 = new Vector4(yScale, bCb, 0, lumaOffset - bCb * 0.5f);
    }

    /// <summary>Adjustment layer source: copies the canvas under its box.</summary>
    private bool CopyCanvasInto(GpuTexture target, (float X, float Y, float W, float H) box)
    {
        var under = CopyCanvas();
        var constants = default(PassConstants);
        constants.U0 = box.X / _outWidth;
        constants.V0 = box.Y / _outHeight;
        constants.U1 = (box.X + box.W) / _outWidth;
        constants.V1 = (box.Y + box.H) / _outHeight;
        Draw("PSCopy", under, target.Rtv, target.Width, target.Height, ref constants, blend: false);
        return true;
    }

    /// <summary>
    /// Snapshots the canvas into its copy (created at drawn-region size if missing): the whole texture when sizes match (3D plane copies),
    /// else the drawn top-left region (the flat canvas is kept at a larger capacity).
    /// </summary>
    private GpuTexture CopyCanvas()
    {
        var canvas = _canvas!;
        if (_canvasCopy is not { } copy)
        {
            copy = _canvasCopy = _gpu.CreateTexture(_outWidth, _outHeight, DxgiFormat.R16G16B16A16Float, renderTarget: false);

            // Flat canvas copies are cached with their canvas; 3D plane and loupe copies belong to their caller.
            if (ReferenceEquals(canvas, _canvasWhole))
                _canvasCopyWhole = copy;
            else if (ReferenceEquals(canvas, _canvasScaled))
                _canvasCopyScaled = copy;
        }

        if (copy.Width == canvas.Width && copy.Height == canvas.Height)
        {
            _gpu.Context.CopyResource(copy.Texture, canvas.Texture);
        }
        else
        {
            var box = new Box { Right = (uint)Math.Min(copy.Width, canvas.Width), Bottom = (uint)Math.Min(copy.Height, canvas.Height), Back = 1 };
            _gpu.Context.CopySubresourceRegion(copy.Texture, 0, 0, 0, canvas.Texture, 0, &box);
        }

        return copy;
    }

    // ----- Zones 1-3 -----

    /// <summary>Runs one effect on the working texture; returns the result texture (the input when nothing was done).</summary>
    private GpuTexture RunEffect(in GpuLayerDescriptor layer, GpuEffectPacket packet, GpuEffectPacket second, GpuTexture work)
    {
        switch (packet.Slot)
        {
            case GpuShaderSlot.Flip:
                return packet.P0 > 0.5f || packet.P1 > 0.5f ? Effect("PSFlip", packet, default, work) : work;

            case GpuShaderSlot.Lut3D:
                {
                    var lut = _resources.Lut(layer.LutResourceId);
                    return lut is null ? work : Effect("PSLut3D", packet, default, work, lut: lut.Value.Srv, params2: new Vector4(lut.Value.Size, 0, 0, 0));
                }

            case GpuShaderSlot.Denoise:
                return packet.P0 < 0.05f ? work : Effect("PSDenoise", packet, default, work);

            case GpuShaderSlot.ColorGrade:
                return Effect("PSColorGrade", packet, default, work);

            // Color Studio grade (SceneRenderer.ColorStudio.cs): two packets plus curves and LUT.
            case GpuShaderSlot.ColorStudio:
                return RunColorStudio(layer, packet, second, work);

            case GpuShaderSlot.ColorBalance:
                return Effect("PSColorBalance", packet, second.Slot == GpuShaderSlot.ColorBalance ? second : default, work);

            case GpuShaderSlot.ColorTemperature:
                return Effect("PSColorTemperature", packet, default, work);

            case GpuShaderSlot.Vignette:
                return packet.P0 < 0.005f ? work : Effect("PSVignette", packet, default, work);

            case GpuShaderSlot.ChromaKey:
                {
                    work = Effect("PSChromaKey", packet, default, work);
                    if (packet.P5 > 0.001f)
                        work = Effect("PSDespill", packet, default, work, timedAs: (int)GpuShaderSlot.Despill);

                    // Advanced Choke (P6, px) and Smooth (P7, px): Matte Choke's square kernel and Matte Smooth's spatial Hann weights on
                    // the key's own alpha (erosion/dilation and convolution).
                    if (MathF.Abs(packet.P6) >= 0.5f)
                    {
                        var passes = Math.Max((int)MathF.Round(MathF.Abs(packet.P6) * _pixelScale), 1);
                        work = Separable("PSMatteChoke", packet with { P0 = packet.P6 }, work, passes, iterations: 1, (int)GpuShaderSlot.ChromaKey);
                    }

                    if ((int)MathF.Round(Math.Clamp(packet.P7, 0, 20) * _pixelScale) is >= 1 and var smooth)
                        work = Separable("PSMatteSmooth", packet, work, smooth, iterations: 1, (int)GpuShaderSlot.ChromaKey);
                    return work;
                }

            case GpuShaderSlot.MatteChoke:
                {
                    if (MathF.Abs(packet.P0) < 0.5f)
                        return work;
                    var passes = Math.Max((int)MathF.Round(MathF.Abs(packet.P0) * _pixelScale), 1);
                    return Separable("PSMatteChoke", packet, work, passes, iterations: 1);
                }

            case GpuShaderSlot.MatteSmooth:
                {
                    var frames = (int)MathF.Round(Math.Clamp(packet.P0, 1, 9));
                    if (frames % 2 == 0)
                        frames++;
                    if (frames <= 1)
                        return work;

                    // Temporal while playing or stepping sequentially (like FFmpeg tmix); spatial after a jump.
                    if (TemporalMatte(layer, frames, work) is { } smoothed)
                        return smoothed;
                    var radius = Math.Max((int)MathF.Round((frames - 1) / 2f * Math.Max(_pixelScale, 0.25f)), 1);
                    return Separable("PSMatteSmooth", packet, work, radius, iterations: 1);
                }

            case GpuShaderSlot.LayerMask:
                {
                    var mask = _resources.Texture2D(layer.MaskResourceId);
                    return mask is null ? work : Effect("PSLayerMask", packet, default, work, aux: mask.Srv);
                }

            case GpuShaderSlot.CornerFeather:
                return Effect("PSCornerFeather", packet, default, work);

            case GpuShaderSlot.HueSaturation:
                return Effect("PSHueSaturation", packet, default, work);

            case GpuShaderSlot.ToneCurve:
                {
                    var curve = _resources.Texture2D(layer.CurveResourceId);
                    return curve is null ? work : Effect("PSToneCurve", packet, default, work, aux: curve.Srv);
                }

            case GpuShaderSlot.BoxBlur:
                {
                    // FFmpeg boxblur semantics (separate vertical box), width matching a Gaussian of half the radius, run Passes times
                    // (P1; 0 in hand-built packets means the default 2), Horizontal/Vertical ratio (P2), then Dim (P3, %): EffectRegistry
                    // Blur and BlurSizes.
                    var radiusPx = Math.Clamp(packet.P0, 0, 50);
                    if (radiusPx >= 0.05f)
                    {
                        // Frame width = canvas width in target pixels (target equals canvas only in export; the letterboxed player,
                        // loupe and 3D planes render at their own scale).
                        var passes = packet.P1 >= 1 ? (int)MathF.Round(Math.Clamp(packet.P1, 1, 4)) : 2;
                        var frameWidth = (int)MathF.Round(_pxScale1080 * 1080);
                        var (across, down) = EffectRegistry.BlurSizes(radiusPx, packet.P2 > 0 ? packet.P2 : 1, passes, frameWidth, work.Width, work.Height);
                        if (across > 0 && down > 0)
                            work = Blur(work, across, down, passes, (int)GpuShaderSlot.BoxBlur);
                    }

                    return packet.P3 >= 0.5f ? Dim(work, -Math.Clamp(packet.P3, 0, 50) / 100 * EqBrightness, (int)GpuShaderSlot.BoxBlur) : work;
                }

            case GpuShaderSlot.Sharpen:
                {
                    // Unsharp mask: box-blur the picture into a separate texture (EffectRegistry.SharpenBox, one horizontal and vertical
                    // pass: unsharp's square kernel), then PSSharpen with it as Aux. Radius P1 (0 in hand-built packets = effect
                    // default), Threshold P2.
                    if (packet.P0 < 0.005f || !_shaders.Has("PSSharpen") || !_shaders.Has("PSBoxBlur"))
                        return work;
                    var frameWidth = (int)MathF.Round(_pxScale1080 * 1080);
                    var radius = EffectRegistry.SharpenBox(packet.P1 > 0 ? packet.P1 : EffectRegistry.SharpenDefaultRadius, frameWidth, work.Width, work.Height);
                    if (radius < 1)
                        return work;

                    var blurred = BlurCopy(work, radius, (int)GpuShaderSlot.Sharpen);
                    var sharpened = Effect("PSSharpen", packet, default, work, aux: blurred.Srv);
                    _gpu.Textures.Return(blurred);
                    return sharpened;
                }

            case GpuShaderSlot.MotionBlur:
                return MotionBlur(layer, packet, work);

            case GpuShaderSlot.HalationGlow:
                return Glow(packet, second, work);

            case GpuShaderSlot.ShockwaveLens:
                return Lens(packet, second, work);

            case GpuShaderSlot.Explosion or GpuShaderSlot.Sparks or GpuShaderSlot.AnimeLines or GpuShaderSlot.EnergyField:
                return Generated(packet, second, work);

            default:
                // Composite-stage packets (handled in Zone 4), reserved slots, unknown plugin slots.
                return work;
        }
    }

    // ----- Temporal Matte Smooth -----

    /// <summary>Frame duration of the layer's source (0 for non-media layers; a 30 fps step is assumed).</summary>
    private double _layerStep;

    private readonly Dictionary<long, AlphaHistory> _histories = [];

    /// <summary>
    /// Temporal Matte Smooth over the last <paramref name="frames"/> frames (Hann weights, like tmix): keeps each drawn frame's alpha (R8 at
    /// working size) while times advance one frame apart; redraws of the same time reuse it. Returns null after a jump (or before enough
    /// sequential frames): the caller smooths spatially and the history restarts from this frame.
    /// </summary>
    private GpuTexture? TemporalMatte(in GpuLayerDescriptor layer, int frames, GpuTexture work)
    {
        if (!_shaders.Has("PSMatteTemporal") || !_shaders.Has("PSAlphaOf"))
            return null;
        var moment = layer.SourceKind == (int)GpuSourceKind.Media ? layer.MediaSeconds : _time;
        var step = _layerStep > 1e-4 ? _layerStep : 1 / 30.0;

        // A layer redrawn alone at another size (loupe) uses a separate history (salted key) so the main one is never reset.
        var key = layer.ClipKey ^ _historySalt;
        if (!_histories.TryGetValue(key, out var history))
            _histories[key] = history = new AlphaHistory();
        if (history.Width != work.Width || history.Height != work.Height)
            history.Reset(work.Width, work.Height);
        history.LastFrame = _frame;

        var same = !double.IsNaN(history.Newest) && Math.Abs(moment - history.Newest) < step * 0.5;
        var next = !double.IsNaN(history.Newest) && Math.Abs(moment - history.Newest - step) < step * 0.5;
        if (!same && !next)
            history.Clear();
        if (!same)
            PushAlpha(history, work, moment);

        // Need frames-1 entries before the newest (current), all one frame apart.
        var needed = Math.Min(frames - 1, AlphaHistory.Slots);
        if (history.Count - 1 < needed || !history.InStep(needed, step))
            return null;

        Span<float> weights = stackalloc float[9];
        var sum = 0f;
        for (var k = 1; k <= frames; k++)
        {
            var s = MathF.Sin(MathF.PI * k / (frames + 1));
            weights[k - 1] = s * s;
            sum += weights[k - 1];
        }

        var views = stackalloc nint[AlphaHistory.Slots];
        var w = stackalloc float[AlphaHistory.Slots];
        for (var i = 0; i < needed; i++)
        {
            // Oldest first: history entry (needed - i) frames before the newest takes weight i.
            var texture = history.Back(needed - i);
            views[i] = texture?.Srv ?? 0;
            w[i] = texture is null ? 0 : weights[i];
        }

        var output = _gpu.Textures.Rent(work.Width, work.Height);
        var constants = default(PassConstants);
        constants.Params2 = new Vector4(w[0], w[1], w[2], w[3]);
        constants.Params3 = new Vector4(w[4], w[5], w[6], w[7]);
        constants.Color = new Vector4(weights[frames - 1], sum, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        _gpu.Context.PSSetShaderResources(5, AlphaHistory.Slots, views);
        _timer.Begin((int)GpuShaderSlot.MatteSmooth);
        Draw("PSMatteTemporal", work, output.Rtv, output.Width, output.Height, ref constants, blend: false);
        _timer.End();
        var none = stackalloc nint[AlphaHistory.Slots];
        _gpu.Context.PSSetShaderResources(5, AlphaHistory.Slots, none);
        _gpu.Textures.Return(work);
        return output;
    }

    private void PushAlpha(AlphaHistory history, GpuTexture work, double moment)
    {
        var slot = history.NextSlot(_gpu);
        var constants = default(PassConstants);
        constants.WholeUv();
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Draw("PSAlphaOf", work, slot.Rtv, slot.Width, slot.Height, ref constants, blend: false);
        history.Pushed(moment);
    }

    /// <summary>Evicts alpha histories (and flows) of layers not drawn for 120 frames; checked every 60 frames.</summary>
    private void ForgetOldHistories()
    {
        if (_frame % 60 == 0)
            ForgetOldFlows();
        if (_histories.Count == 0 || _frame % 60 != 0)
            return;
        foreach (var key in _histories.Where(p => _frame - p.Value.LastFrame > 120).Select(p => p.Key).ToList())
        {
            _histories[key].Dispose();
            _histories.Remove(key);
        }
    }

    /// <summary>Ring of a smoothed layer's recent alpha frames (R8 at working size) with their times, newest last.</summary>
    private sealed class AlphaHistory : IDisposable
    {
        // Current frame plus eight before it (Matte Smooth max 9 frames); the eight are bound at t5..t12.
        public const int Depth = 9, Slots = 8;
        private readonly GpuTexture?[] _ring = new GpuTexture?[Depth];
        private readonly double[] _times = new double[Depth];
        private int _next;

        public int Count { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public long LastFrame { get; set; }
        public double Newest => Count == 0 ? double.NaN : _times[(_next + Depth - 1) % Depth];

        public void Reset(int width, int height)
        {
            Dispose();
            (Width, Height) = (width, height);
        }

        public void Clear() => (Count, _next) = (0, 0);

        public GpuTexture NextSlot(GpuDevice gpu) =>
            _ring[_next] ??= gpu.CreateTexture(Width, Height, DxgiFormat.R8Unorm, renderTarget: true);

        public void Pushed(double moment)
        {
            _times[_next] = moment;
            _next = (_next + 1) % Depth;
            Count = Math.Min(Count + 1, Depth);
        }

        /// <summary>Entry <paramref name="back"/> frames before the newest (1 = immediately before).</summary>
        public GpuTexture? Back(int back) => back < Count ? _ring[(_next - 1 - back + 2 * Depth) % Depth] : null;

        /// <summary>True when the newest and the <paramref name="count"/> entries before it are each one frame apart.</summary>
        public bool InStep(int count, double step)
        {
            for (var i = 0; i < count; i++)
            {
                var later = _times[(_next - 1 - i + 2 * Depth) % Depth];
                var earlier = _times[(_next - 2 - i + 2 * Depth) % Depth];
                if (Math.Abs(later - earlier - step) > step * 0.5)
                    return false;
            }

            return true;
        }

        public void Dispose()
        {
            for (var i = 0; i < Depth; i++)
            {
                _ring[i]?.Dispose();
                _ring[i] = null;
            }

            Clear();
        }
    }

    /// <summary>Runs one full-texture effect pass into a new same-size texture; the input returns to the pool.</summary>
    private GpuTexture Effect(string shader, GpuEffectPacket packet, GpuEffectPacket second, GpuTexture input, nint aux = 0, nint lut = 0,
        Vector4 params2 = default, int timedAs = -1)
    {
        if (!_shaders.Has(shader))
            return input;
        var output = _gpu.Textures.Rent(input.Width, input.Height);
        var constants = default(PassConstants);
        constants.Packet = packet;
        constants.Packet1 = second;
        constants.Params = new Vector4(_pxScale1080, 0, 0, _pixelScale);
        constants.Params2 = params2;
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        _timer.Begin(timedAs >= 0 ? timedAs : (int)packet.Slot);
        Draw(shader, input, output.Rtv, output.Width, output.Height, ref constants, blend: false, aux: aux, lut: lut);
        _timer.End();
        _gpu.Textures.Return(input);
        return output;
    }

    /// <summary>Separable pass: horizontal then vertical (Params.y), <paramref name="radius"/> texels (Params.z), repeated <paramref name="iterations"/> times.</summary>
    private GpuTexture Separable(string shader, GpuEffectPacket packet, GpuTexture work, int radius, int iterations, int timedAs = -1) =>
        Separable(shader, packet, work, radius, (float)radius, iterations, timedAs);

    /// <summary>
    /// Separable pass with independent radii: <paramref name="radius"/> texels horizontally, <paramref name="down"/> vertically (Params.z; box
    /// blur uses the fractional part as the outer tap weight), repeated <paramref name="iterations"/> times.
    /// </summary>
    private GpuTexture Separable(string shader, GpuEffectPacket packet, GpuTexture work, float radius, float down, int iterations, int timedAs = -1)
    {
        if (!_shaders.Has(shader))
            return work;
        for (var i = 0; i < iterations; i++)
        {
            for (var direction = 0; direction < 2; direction++)
            {
                var output = _gpu.Textures.Rent(work.Width, work.Height);
                var constants = default(PassConstants);
                constants.Packet = packet;
                constants.Params = new Vector4(_pxScale1080, direction, direction == 0 ? radius : down, _pixelScale);
                constants.Params4 = new Vector4(1, 1, 0, (float)_time);
                constants.WholeUv();
                _timer.Begin(timedAs >= 0 ? timedAs : (int)packet.Slot);
                Draw(shader, work, output.Rtv, output.Width, output.Height, ref constants, blend: false);
                _timer.End();
                _gpu.Textures.Return(work);
                work = output;
            }
        }

        return work;
    }

    /// <summary>
    /// Box-blurs a texture into a new one (<paramref name="radius"/> texels, one horizontal and one vertical PSBoxBlur pass), keeping the
    /// source: the reference an unsharp mask subtracts.
    /// </summary>
    private GpuTexture BlurCopy(GpuTexture source, int radius, int timedAs)
    {
        var packet = new GpuEffectPacket(GpuShaderSlot.BoxBlur, radius, 0, 0, 0, 0, 0, 0, 0);
        GpuTexture? across = null;
        for (var direction = 0; direction < 2; direction++)
        {
            var input = across ?? source;
            var output = _gpu.Textures.Rent(source.Width, source.Height);
            var constants = default(PassConstants);
            constants.Packet = packet;
            constants.Params = new Vector4(_pxScale1080, direction, Math.Min(radius, 64), _pixelScale);
            constants.Params4 = new Vector4(1, 1, 0, (float)_time);
            constants.WholeUv();
            _timer.Begin(timedAs);
            Draw("PSBoxBlur", input, output.Rtv, output.Width, output.Height, ref constants, blend: false);
            _timer.End();
            if (across is not null)
                _gpu.Textures.Return(across);
            across = output;
        }

        return across!;
    }

    private GpuTexture Blur(GpuTexture work, int radius, int iterations, int timedAs) =>
        Separable("PSBoxBlur", new GpuEffectPacket(GpuShaderSlot.BoxBlur, radius, 0, 0, 0, 0, 0, 0, 0), work, Math.Min(radius, 64), iterations, timedAs);

    /// <summary>Box blur with independent horizontal and vertical radii (texels, fractional outer-tap weight; max 64), repeated.</summary>
    private GpuTexture Blur(GpuTexture work, float across, float down, int iterations, int timedAs) =>
        Separable("PSBoxBlur", new GpuEffectPacket(GpuShaderSlot.BoxBlur, across, 0, 0, 0, 0, 0, 0, 0), work, Math.Min(across, 64), Math.Min(down, 64), iterations, timedAs);

    /// <summary>
    /// FFmpeg eq brightness b in GPU RGB terms: eq adds 256 b to limited-range 8-bit luma, i.e. 256/219 b per channel after decode (used for
    /// blur fill dimming and the Blur effect's Dim; verified by gpu-scene-differential).
    /// </summary>
    private const float EqBrightness = 256f / 219f;

    /// <summary>Offsets brightness by <paramref name="amount"/> (PSDim: added equally to every channel).</summary>
    private GpuTexture Dim(GpuTexture work, float amount, int timedAs)
    {
        if (!_shaders.Has("PSDim"))
            return work;
        var output = _gpu.Textures.Rent(work.Width, work.Height);
        var dim = default(PassConstants);
        dim.Params2 = new Vector4(Math.Clamp(amount, -1, 1), 0, 0, 0);
        dim.Params4 = new Vector4(1, 1, 0, (float)_time);
        dim.WholeUv();
        _timer.Begin(timedAs);
        Draw("PSDim", work, output.Rtv, output.Width, output.Height, ref dim, blend: false);
        _timer.End();
        _gpu.Textures.Return(work);
        return output;
    }

    // ----- Zone 4: beneath the layer -----

    /// <summary>
    /// Drop shadow: the layer silhouette in the shadow colour and opacity, padded for the blur (two box-blur passes on alpha, as FFmpeg
    /// boxblur), offset along the light angle.
    /// </summary>
    private void DrawShadow(in GpuLayerDescriptor layer, GpuEffectPacket shadow, GpuTexture work, (float X, float Y, float W, float H) box, float alpha, GpuEffectPacket? wipe)
    {
        var distance = Math.Clamp(shadow.P0, 0, 100);
        var radians = shadow.P1 * MathF.PI / 180f;
        var shiftX = MathF.Round(-distance * MathF.Cos(radians), MidpointRounding.AwayFromZero) * _pixelScale;
        var shiftY = MathF.Round(distance * MathF.Sin(radians), MidpointRounding.AwayFromZero) * _pixelScale;
        var soft = (int)MathF.Round(Math.Clamp(shadow.P2, 0, 50) * _pixelScale);
        var room = soft + (soft & 1);

        var seed = _gpu.Textures.Rent(work.Width + 2 * room, work.Height + 2 * room);
        var constants = default(PassConstants);
        var (roomU, roomV) = ((float)room / work.Width, (float)room / work.Height);
        (constants.U0, constants.V0, constants.U1, constants.V1) = (-roomU, -roomV, 1 + roomU, 1 + roomV);
        constants.Color = new Vector4(shadow.P4, shadow.P5, shadow.P6, Math.Clamp(shadow.P3, 0, 1));
        _timer.Begin((int)GpuShaderSlot.DropShadow);
        Draw("PSShadowSeed", work, seed.Rtv, seed.Width, seed.Height, ref constants, blend: false);
        _timer.End();
        if (soft > 0)
            seed = Blur(seed, soft, iterations: 2, (int)GpuShaderSlot.DropShadow);

        // The padded box keeps the same pivot.
        var roomX = room * MathF.Sign(box.W == 0 ? 1 : box.W);
        var roomY = room * MathF.Sign(box.H == 0 ? 1 : box.H);
        var (w, h) = (box.W + 2 * roomX, box.H + 2 * roomY);
        var anchorX = w == 0 ? 0.5f : (layer.AnchorX * box.W + roomX) / w;
        var anchorY = h == 0 ? 0.5f : (layer.AnchorY * box.H + roomY) / h;
        var place = default(PassConstants);
        place.Transform = Matrix4.Box(box.X - roomX + shiftX, box.Y - roomY + shiftY, w, h, layer.Rotation, anchorX, anchorY, _outWidth, _outHeight, layer.TiltX, layer.TiltY);
        place.WholeUv();
        place.Params4 = new Vector4(alpha, 1, 0, (float)_time);
        SetWipe(ref place, wipe, (box.X, box.Y, w, h));
        _timer.Begin((int)GpuShaderSlot.DropShadow);
        Quad("PSComposite", seed, _canvas!.Rtv, ref place, blend: true);
        _timer.End();
    }

    /// <summary>
    /// Blur fill from the layer's picture (see GpuScenePlan.Create.cs Blur): the crop region (zoom) cropped to frame aspect, downscaled more
    /// for larger radii (1/2 from 6 output px, 1/4 from 16: keeps the box narrow and cheap while the blur hides the downscale), box-blurred
    /// BlurFillPasses times with fractional radius (continuous from 0 = sharp), dimmed, then (<paramref name="withLook"/>) its own Look
    /// effects at frame size, and composited over the whole frame.
    /// </summary>
    private void DrawBlurFill(in GpuLayerDescriptor layer, float alpha, bool withLook)
    {
        var (frameWidth, frameHeight) = (Math.Max((int)MathF.Round(_frameRect.W), 2), Math.Max((int)MathF.Round(_frameRect.H), 2));
        var radius = Math.Clamp(layer.BlurFillRadius, 0, (float)EffectRegistry.BackgroundMostRadius) * _scaleX;
        var shrink = BlurFillShrink(radius);
        var (smallWidth, smallHeight) = (Math.Max(frameWidth / shrink / 2 * 2, 16), Math.Max(frameHeight / shrink / 2 * 2, 16));
        var small = _gpu.Textures.Rent(smallWidth, smallHeight);
        if (!DrawSource(layer, small, CropUv(layer), cover: true))
            return;

        var texels = Math.Min(radius / shrink, Math.Max(Math.Min(smallWidth, smallHeight) / 4 - 1, 1));
        if (texels >= 0.05f)
            small = Blur(small, texels, texels, Math.Clamp(layer.BlurFillPasses, 1, 4), (int)GpuShaderSlot.BoxBlur);
        if (layer.BlurFillDim < 0)
            small = Dim(small, Math.Clamp(layer.BlurFillDim, -0.5f, 0) * EqBrightness, (int)GpuShaderSlot.BoxBlur);

        // Its own effects (colour, LUT, vignette...) at frame size so pixel-based parameters match the frame.
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        if (withLook && HasLook(layer, count))
        {
            var work = _gpu.Textures.Rent(frameWidth, frameHeight);
            var copy = default(PassConstants);
            copy.WholeUv();
            Draw("PSCopy", small, work.Rtv, frameWidth, frameHeight, ref copy, blend: false);
            _gpu.Textures.Return(small);
            var effects = layer.Effects;
            for (var k = 0; k < count; k++)
            {
                var packet = effects[k];
                var second = k + 1 < count && Paired(packet, effects[k + 1]) ? effects[k + 1] : default;
                if (second.Slot != GpuShaderSlot.None)
                    k++;
                work = RunEffect(layer, packet, second, work);
            }

            small = work;
        }

        var constants = default(PassConstants);
        constants.Transform = Matrix4.Box(_frameRect.X, _frameRect.Y, _frameRect.W, _frameRect.H, 0, 0.5f, 0.5f, _outWidth, _outHeight);
        constants.WholeUv();
        constants.Params4 = new Vector4(alpha, 1, 0, (float)_time);
        _timer.Begin((int)GpuShaderSlot.BoxBlur);
        Quad("PSComposite", small, _canvas!.Rtv, ref constants, blend: true);
        _timer.End();
    }

    /// <summary>Blur fill downscale factor for a radius in output pixels: 1, 2 or 4.</summary>
    internal static int BlurFillShrink(float radius) => radius >= 16 ? 4 : radius >= 6 ? 2 : 1;

    /// <summary>True when the layer has a Look or Key and Matte effect (anything other than Zone 4 composite and transition packets).</summary>
    private static bool HasLook(in GpuLayerDescriptor layer, int count)
    {
        for (var k = 0; k < count; k++)
        {
            if (layer.Effects[k].Slot is not (GpuShaderSlot.None or GpuShaderSlot.TransformComposite or GpuShaderSlot.DropShadow or GpuShaderSlot.BlendMode)
                && (int)layer.Effects[k].Slot < (int)GpuShaderSlot.TransitionDissolve)
                return true;
        }

        return false;
    }

    // ----- Drawing -----

    /// <summary>Full-target pass (VSFull): input in t0, aux in t1, LUT in t2.</summary>
    private void Draw(string shader, GpuTexture? input, nint target, int width, int height, ref PassConstants constants, bool blend, nint aux = 0, nint lut = 0)
    {
        if (input is not null)
            constants.Sizes(input.Width, input.Height, width, height);
        else
            constants.Sizes(width, height, width, height);
        Submit("VSFull", shader, input?.Srv ?? 0, aux, lut, target, width, height, ref constants, blend, vertices: 3, clipToFrame: false);
    }

    /// <summary>
    /// Stretches a finished frame onto a differently sized target with linear filtering (PSCopy), used by the Live Encoded Preview when the
    /// export size differs from the canvas.
    /// </summary>
    public void Stretch(GpuTexture picture, GpuTexture target)
    {
        _gpu.BindCommon();
        var constants = default(PassConstants);
        constants.WholeUv();
        Draw("PSCopy", picture, target.Rtv, target.Width, target.Height, ref constants, blend: false);
    }

    /// <summary>Converts a finished frame into NV12 planes (FlagReadbackNv12), each its own render target read back by the worker.</summary>
    public void ToNv12(GpuTexture picture, GpuTexture luma, GpuTexture chroma)
    {
        var constants = default(PassConstants);
        constants.WholeUv();
        Draw("PSRgbToNv12", picture, luma.Rtv, luma.Width, luma.Height, ref constants, blend: false);
        Draw("PSRgbToNv12Chroma", picture, chroma.Rtv, chroma.Width, chroma.Height, ref constants, blend: false);
    }

    /// <summary>
    /// Converts an HDR export frame into P010 planes (FlagReadbackP010): R16 and R16G16 targets, BT.2020 non-constant luminance, limited
    /// range, 10 bits in the top of each 16-bit sample. <paramref name="picture"/> is the RGBA16F frame.
    /// </summary>
    public void ToP010(GpuTexture picture, GpuTexture luma, GpuTexture chroma)
    {
        var constants = default(PassConstants);
        constants.WholeUv();
        Draw("PSRgbToP010", picture, luma.Rtv, luma.Width, luma.Height, ref constants, blend: false);
        Draw("PSRgbToP010Chroma", picture, chroma.Rtv, chroma.Width, chroma.Height, ref constants, blend: false);
    }

    /// <summary>
    /// Live Encoded Preview compare (Shaders/Loopback.hlsl): source NV12 planes (<paramref name="sourceLuma"/>, <paramref name="sourceChroma"/>)
    /// vs. decoded planes, drawn over the whole output with the picture at <paramref name="picture"/> (output pixels) and black around it.
    /// <paramref name="mode"/>: 0 encoded picture, 1 split wipe at <paramref name="wipe"/> (0..1 of picture width), 2 difference x
    /// <paramref name="gain"/>.
    /// </summary>
    public void DrawLoopbackCompare(nint sourceLuma, nint sourceChroma, nint decodedLuma, nint decodedChroma, nint target, int width, int height,
        (float X, float Y, float W, float H) picture, int mode, float wipe, float gain, float dividerHalfWidth)
    {
        _gpu.BindCommon();
        var constants = default(PassConstants);
        constants.WholeUv();
        constants.Sizes(width, height, width, height);
        SetYuv(ref constants, 1, fullRange: false);
        constants.Params = new Vector4(mode, wipe, gain, dividerHalfWidth);
        constants.Params2 = new Vector4(picture.X, picture.Y, picture.W, picture.H);
        var planes = stackalloc nint[2] { decodedLuma, decodedChroma };
        _gpu.Context.PSSetShaderResources(3, 2, planes);
        Submit("VSFull", "PSLoopbackCompare", sourceLuma, sourceChroma, 0, target, width, height, ref constants, blend: false, vertices: 3, clipToFrame: false);
        var none = stackalloc nint[2];
        _gpu.Context.PSSetShaderResources(3, 2, none);
    }

    /// <summary>Draws a placed quad (VSQuad) onto the canvas, scissored to the frame when letterboxed (never over the bars).</summary>
    private void Quad(string shader, GpuTexture input, nint target, ref PassConstants constants, bool blend, nint aux = 0)
    {
        constants.Sizes(input.Width, input.Height, _outWidth, _outHeight);
        Submit("VSQuad", shader, input.Srv, aux, 0, target, _outWidth, _outHeight, ref constants, blend, vertices: 4, clipToFrame: _letterbox);
    }

    private void Submit(string vertex, string pixel, nint input, nint aux, nint lut, nint target, int width, int height, ref PassConstants constants, bool blend, uint vertices, bool clipToFrame, bool additive = false)
    {
        var context = _gpu.Context;
        _gpu.SetConstants(constants);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(new Viewport { Width = width, Height = height, MaxDepth = 1 });
        if (clipToFrame)
        {
            var (left, top) = ((int)MathF.Floor(Math.Max(_frameRect.X, 0)), (int)MathF.Floor(Math.Max(_frameRect.Y, 0)));
            var (right, bottom) = ((int)MathF.Ceiling(Math.Min(_frameRect.X + _frameRect.W, width)), (int)MathF.Ceiling(Math.Min(_frameRect.Y + _frameRect.H, height)));
            context.RSSetScissor(left, top, Math.Max(right, left), Math.Max(bottom, top));
        }
        else
        {
            context.RSSetScissor(0, 0, width, height);
        }
        var views = stackalloc nint[3] { input, aux, lut };
        context.PSSetShaderResources(0, 3, views);
        context.IASetPrimitiveTopology(vertices == 4 ? D3D11.TopologyTriangleStrip : D3D11.TopologyTriangleList);
        context.VSSetShader(_shaders[vertex]);
        context.PSSetShader(_shaders[pixel]);
        if (additive)
            _gpu.SetAdditive();
        else
            _gpu.SetBlend(blend);
        context.Draw(vertices, 0);

        // Unbind inputs and target so the next pass may write to either.
        var none = stackalloc nint[3];
        context.PSSetShaderResources(0, 3, none);
        context.OMSetRenderTargets(0);
    }

    /// <summary>
    /// Worker trim: releases inter-frame textures that are recreated on demand: blend canvas copies, the 3D picture and plane (budget step
    /// kept so the redrawn 3D picture matches), and loupe scan, view, point and staging (tracked point kept). With <paramref name="history"/>
    /// (requested trim: tab deactivated, Settings Clear) also frame-to-frame state: Motion Blur flows, Explosion noise and Matte Smooth alpha
    /// histories; the idle trim keeps them so the paused redraw matches what is shown. The full canvas stays (every flat frame uses it); the
    /// scaled canvas is released and recreated on the next play. Between frames only (worker thread).
    /// </summary>
    public void ReleaseIdleTextures(bool history)
    {
        _canvasCopyWhole?.Dispose();
        _canvasCopyScaled?.Dispose();
        (_canvasCopy, _canvasCopyWhole, _canvasCopyScaled) = (null, null, null);
        if (ReferenceEquals(_canvas, _canvasScaled))
            _canvas = _canvasWhole;
        _canvasScaled?.Dispose();
        _canvasScaled = null;
        ReleaseExplodedTextures();
        DisposeLoupe();
        if (!history)
            return;
        DisposeFx();
        foreach (var kept in _histories.Values)
            kept.Dispose();
        _histories.Clear();
    }

    /// <summary>
    /// Goldens: counts of textures <see cref="ReleaseIdleTextures"/> would release now: always-released ones, and history-only ones (flows,
    /// noise, alpha histories).
    /// </summary>
    internal (int Textures, int History) IdleTexturesForTests()
    {
        var textures = (_canvasCopyWhole is null ? 0 : 1) + (_canvasCopyScaled is null ? 0 : 1) + (_canvasScaled is null ? 0 : 1) + (_view3D is null ? 0 : 1) + (_planeTarget is null ? 0 : 1) + KeptPlaneTextures()
            + (_loupeScan is null ? 0 : 1) + (_loupeView is null ? 0 : 1) + (_loupeFound is null ? 0 : 1) + _loupeStaging.Count(s => s != 0);
        return (textures, _flows.Count + (_noise3D == 0 ? 0 : 1) + _histories.Count);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _canvasWhole?.Dispose();
        _canvasScaled?.Dispose();
        (_canvas, _canvasWhole, _canvasScaled) = (null, null, null);
        _canvasCopyWhole?.Dispose();
        _canvasCopyScaled?.Dispose();
        (_canvasCopy, _canvasCopyWhole, _canvasCopyScaled) = (null, null, null);
        _blank?.Dispose();
        DisposeExploded();
        DisposeLoupe();
        DisposeFx();
        foreach (var history in _histories.Values)
            history.Dispose();
        _histories.Clear();
    }
}

/// <summary>
/// GPU timestamps around each pass, summed per shader slot (ControlBlock.SlotGpuMs) to verify per-slot cost. Active only when the scene
/// requests it (FrameSceneHeader.FlagTimePasses).
/// </summary>
internal sealed unsafe class PassTimer(GpuDevice gpu) : IDisposable
{
    private readonly List<nint> _queries = [];
    private readonly List<(int Slot, int Start, int End)> _passes = [];
    private nint _disjoint;
    private int _used;
    private bool _on;
    private int _open = -1;
    private int _openSlot;

    public float[] SlotMs { get; } = new float[128];
    public int[] SlotRuns { get; } = new int[128];
    public float FrameMs { get; private set; }

    public void BeginFrame(bool on)
    {
        _on = on;
        _used = 0;
        _passes.Clear();
        if (!on)
            return;
        if (_disjoint == 0)
            _disjoint = gpu.Device.CreateQuery(D3D11.QueryTimestampDisjoint);
        gpu.Context.Begin(_disjoint);
    }

    private int Next()
    {
        if (_used == _queries.Count)
            _queries.Add(gpu.Device.CreateQuery(D3D11.QueryTimestamp));
        var index = _used++;
        gpu.Context.End(_queries[index]);
        return index;
    }

    public void Begin(int slot)
    {
        if (!_on)
            return;
        _openSlot = slot;
        _open = Next();
    }

    public void End()
    {
        if (!_on || _open < 0)
            return;
        _passes.Add((_openSlot, _open, Next()));
        _open = -1;
    }

    public void EndFrame()
    {
        if (!_on)
            return;
        gpu.Context.End(_disjoint);
    }

    /// <summary>Blocks for this frame's timestamps (tests and benchmark only) and sums them per slot. False when the clock was disjoint.</summary>
    public bool Collect()
    {
        if (!_on || _disjoint == 0)
            return false;
        TimestampDisjoint disjoint;
        while (gpu.Context.GetData(_disjoint, &disjoint, (uint)sizeof(TimestampDisjoint), 0) == Com.SFalse)
            Thread.SpinWait(64);
        Array.Clear(SlotMs);
        Array.Clear(SlotRuns);
        if (disjoint.Disjoint != 0 || disjoint.Frequency == 0)
            return false;

        ulong first = ulong.MaxValue, last = 0;
        foreach (var (slot, start, end) in _passes)
        {
            ulong a, b;
            while (gpu.Context.GetData(_queries[start], &a, sizeof(ulong), 0) == Com.SFalse)
                Thread.SpinWait(16);
            while (gpu.Context.GetData(_queries[end], &b, sizeof(ulong), 0) == Com.SFalse)
                Thread.SpinWait(16);
            var ms = (float)((b - a) * 1000.0 / disjoint.Frequency);
            var at = Math.Clamp(slot, 0, 127);
            SlotMs[at] += ms;
            SlotRuns[at]++;
            first = Math.Min(first, a);
            last = Math.Max(last, b);
        }

        FrameMs = last > first ? (float)((last - first) * 1000.0 / disjoint.Frequency) : 0;
        return true;
    }

    public void Dispose()
    {
        for (var i = 0; i < _queries.Count; i++)
        {
            var query = _queries[i];
            Com.Release(ref query);
        }

        _queries.Clear();
        Com.Release(ref _disjoint);
    }
}
