using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// Draws one <see cref="FrameSceneHeader"/> and its layers into a render target (Pass 5.0-Core, 5.0B). Every layer, bottom
/// first: its picture into a working texture at the size it has on screen (Zone 0: the source, cropped, scaled with
/// Catmull-Rom or a box prefilter), its effects in order on that texture (Zones 1-3, ping-ponging two textures), then laid
/// on the canvas (Zone 4: its blurred background, its shadow, its transform, opacity, transitions and blend mode). The
/// canvas (RGBA16F, premultiplied) goes to the output last. Nothing is read back; no graph is rebuilt; a changed value is
/// just a different constant on the next frame.
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
    private long _frame;

    // The frame being drawn.
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

    /// <summary>Draws the scene into <paramref name="outputRtv"/> (its size given). <paramref name="raw"/>: the canvas as it is, premultiplied, no background.</summary>
    public void Render(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, nint outputRtv, int outputWidth, int outputHeight, bool raw)
    {
        _frame++;
        _playing = header.IsPlaying != 0;
        _decoders.BeginFrame(_frame, _playing, header.PlayheadSeconds, exactOnly: (header.Flags & FrameSceneHeader.FlagExactFrames) != 0);
        try
        {
            RenderLayers(header, layers, outputRtv, outputWidth, outputHeight, raw);
        }
        finally
        {
            // Whatever happened, the frames it pinned are let go and the decode lanes hear what is missing.
            _decoders.EndFrame();
        }
    }

    private bool _playing;

    private void RenderLayers(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, nint outputRtv, int outputWidth, int outputHeight, bool raw)
    {
        // The 3D exploded layer stack (5.0D) has a path of its own (SceneRenderer.Exploded.cs); the flat one follows unchanged.
        if (header.ViewMode == 1)
        {
            RenderExploded(header, layers, outputRtv, outputWidth, outputHeight, raw);
            return;
        }

        // (The 3D view's textures go a while after it was last drawn.)
        ForgetExplodedTextures();
        _outWidth = outputWidth;
        _outHeight = outputHeight;
        var canvasWidth = Math.Max(header.CanvasWidth, 1);
        var canvasHeight = Math.Max(header.CanvasHeight, 1);
        _letterbox = (header.Flags & FrameSceneHeader.FlagLetterbox) != 0;
        if (_letterbox)
        {
            // As the player shows it: fitted and centred, then zoomed about the middle and moved by the pan.
            var fit = Math.Min((float)outputWidth / canvasWidth, (float)outputHeight / canvasHeight);
            var zoom = Math.Clamp(header.ViewScale > 0 ? header.ViewScale : 1, 0.05f, 64f);
            var (pictureWidth, pictureHeight) = (canvasWidth * fit, canvasHeight * fit);
            _scaleX = _scaleY = fit * zoom;
            _offsetX = outputWidth / 2f + header.ViewPanX * pictureWidth * zoom - canvasWidth * _scaleX / 2;
            _offsetY = outputHeight / 2f + header.ViewPanY * pictureHeight * zoom - canvasHeight * _scaleY / 2;
        }
        else
        {
            _scaleX = (float)outputWidth / canvasWidth;
            _scaleY = (float)outputHeight / canvasHeight;
            (_offsetX, _offsetY) = (0, 0);
        }

        // The frame on the output (the scissor of every layer laid on), within the output.
        _frameRect = (_offsetX, _offsetY, canvasWidth * _scaleX, canvasHeight * _scaleY);
        _pxScale1080 = canvasWidth * _scaleX / 1080f;
        _pixelScale = _scaleX;
        _time = header.PlayheadSeconds;

        var timing = (header.Flags & FrameSceneHeader.FlagTimePasses) != 0;
        _timer.BeginFrame(timing);
        _gpu.BindCommon();

        if (_canvas is null || _canvas.Width != outputWidth || _canvas.Height != outputHeight)
        {
            _canvas?.Dispose();
            _canvasCopy?.Dispose();
            _canvas = _gpu.CreateTexture(outputWidth, outputHeight, DxgiFormat.R16G16B16A16Float, renderTarget: true);
            _canvasCopy = null;
        }

        _gpu.Context.ClearRenderTargetView(_canvas.Rtv, 0, 0, 0, 0);

        // Letterboxed, the sequence's background fills only the picture; around it the player is black.
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

        // The magnifier loupe over the picture, while an edge is tuned (the player only).
        if (header.LoupeClipKey != 0 && _letterbox && !raw)
            DrawLoupe(header, layers);

        // The canvas to the output.
        var constants = default(PassConstants);
        constants.Color = _letterbox ? new Vector4(0, 0, 0, 1) : new Vector4(header.BackgroundR, header.BackgroundG, header.BackgroundB, 1);
        constants.Params4 = new Vector4(1, 1, raw ? 1 : 0, (float)_time);
        constants.WholeUv();
        _timer.Begin(0);
        Draw("PSFinal", _canvas, outputRtv, outputWidth, outputHeight, ref constants, blend: false);
        _timer.End();
        _gpu.Textures.EndFrame();
        _timer.EndFrame();
        ForgetOldHistories();
    }

    // ----- One layer -----

    private void DrawLayer(in GpuLayerDescriptor layer)
    {
        _layerStep = 0;
        // The box on the output, and the working texture's size (what it covers on screen, at least 2 x 2).
        var box = (X: layer.X * _scaleX + _offsetX, Y: layer.Y * _scaleY + _offsetY, W: layer.Width * _scaleX, H: layer.Height * _scaleY);
        var workWidth = (int)Math.Clamp(MathF.Round(MathF.Abs(box.W)), 2, 8192);
        var workHeight = (int)Math.Clamp(MathF.Round(MathF.Abs(box.H)), 2, 8192);
        if (OffScreen(box, layer.Rotation))
            return;

        // What lays it on, gathered from its packets wherever they stand.
        var opacity = Math.Clamp(layer.Opacity, 0, 1);
        var blendMode = layer.BlendMode;
        var dissolve = 1f;
        GpuEffectPacket? shadow = null, wipe = null, slide = null, zoom = null, blurDissolve = null;
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
            }
        }

        // Drawn alone (the loupe and its edge search, SceneRenderer.Loupe.cs): the layer's own picture and edge only, whole,
        // as it is laid on at rest: no blurred background, no shadow, no opacity, blend or transition.
        if (_alone)
        {
            (opacity, dissolve, blendMode) = (1, 1, 0);
            (shadow, wipe, slide, zoom, blurDissolve) = (null, null, null, null, null);
        }

        if (opacity * dissolve <= 0.0005f)
            return;

        // Only its blurred background (the chain's, a blur twin's): over the whole frame, at its place in the stack.
        // (5.0-Release: a radius of 0 is a sharp background, drawn all the same.)
        if ((layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0)
        {
            DrawBlurFill(layer, opacity * dissolve, withLook: true);
            return;
        }

        // Zone 0: the picture.
        var work = _gpu.Textures.Rent(workWidth, workHeight);
        var adjustment = (layer.Flags & GpuLayerDescriptor.FlagAdjustment) != 0;
        if (adjustment ? !CopyCanvasInto(work, box) : !DrawSource(layer, work, CropUv(layer), cover: false))
            return;

        // Zones 1-3: the effects, in order.
        for (var k = 0; k < count; k++)
        {
            var packet = effects[k];
            var second = k + 1 < count && effects[k + 1].Slot == packet.Slot ? effects[k + 1] : default;
            if (packet.Slot == GpuShaderSlot.ColorBalance && second.Slot == packet.Slot)
                k++;
            work = RunEffect(layer, packet, second, work);
        }

        // A blur dissolve's blur: all of it where the picture is not there yet.
        if (blurDissolve is { } blurring)
        {
            var most = Math.Clamp((int)MathF.Round(Math.Max(workWidth, workHeight) / 40f), 2, 64);
            var shown = blurring.P4 > 0.5f ? blurring.P3 : 1 - blurring.P3;
            var radius = Math.Max((int)MathF.Round(most * (1 - Math.Clamp(shown, 0, 1))), 1);
            work = Blur(work, radius, iterations: 1, (int)GpuShaderSlot.TransitionBlurDissolve);
        }

        // Where it goes: slides move it, zooms scale it about its middle.
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

        // Under it: its blurred background, then its shadow.
        if (layer.BlurFillRadius > 0 && !adjustment && !_alone)
            DrawBlurFill(layer, opacity * dissolve, withLook: false);
        if (shadow is { } shading)
            DrawShadow(layer, shading, work, box, opacity * dissolve, wipe);

        // Zone 4: laid on.
        var constants = default(PassConstants);
        constants.Transform = Matrix4.Box(box.X, box.Y, box.W, box.H, layer.Rotation, layer.AnchorX, layer.AnchorY, _outWidth, _outHeight);
        constants.WholeUv();
        constants.Params4 = new Vector4(opacity, dissolve, Math.Clamp(blendMode, 0, 7), (float)_time);
        SetWipe(ref constants, wipe, box);
        if (blendMode is > 0 and <= 7)
        {
            // The mixed colours need the canvas under it: a copy of it, read where the layer lands.
            _canvasCopy ??= _gpu.CreateTexture(_outWidth, _outHeight, DxgiFormat.R16G16B16A16Float, renderTarget: false);
            _gpu.Context.CopyResource(_canvasCopy.Texture, _canvas!.Texture);
            _timer.Begin((int)GpuShaderSlot.BlendMode);
            Quad("PSCompositeBlend", work, _canvas.Rtv, ref constants, blend: false, aux: _canvasCopy.Srv);
            _timer.End();
        }
        else
        {
            _timer.Begin((int)GpuShaderSlot.TransformComposite);
            Quad("PSComposite", work, _canvas!.Rtv, ref constants, blend: true);
            _timer.End();
        }
    }

    /// <summary>How far a slide must carry the box to be wholly off the frame (FilterGraphBuilder.Video.cs WithTransitions).</summary>
    private static float Beyond(float toward, float at, float size, float frame) => toward > 0 ? frame - at + 2 : -(at + size) - 2;

    private void SetWipe(ref PassConstants constants, GpuEffectPacket? wipe, (float X, float Y, float W, float H) box)
    {
        if (wipe is not { } w)
            return;
        var (dx, dy) = (w.P1, w.P2);
        var fadeIn = w.P4 > 0.5f;
        var keepLow = fadeIn ? dx + dy > 0 : dx + dy < 0;
        var size = (MathF.Abs(box.W), MathF.Abs(box.H));
        constants.Params2 = new Vector4(1, keepLow ? 1 : 0, dx != 0 ? 0 : 1, Math.Clamp(w.P0, 0, 1));
        constants.Params3 = new Vector4(Math.Max(2, MathF.Round(Math.Max(size.Item1, size.Item2) * 0.02f)), 0, size.Item1, size.Item2);
    }

    private bool OffScreen((float X, float Y, float W, float H) box, float rotation)
    {
        // A generous test: the box's circumscribed circle against the frame.
        var (cx, cy) = (box.X + box.W / 2, box.Y + box.H / 2);
        var radius = MathF.Sqrt(box.W * box.W + box.H * box.H) / 2 + 2;
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
    /// Draws the layer's picture (the part <paramref name="uv"/> names) into <paramref name="target"/>; with
    /// <paramref name="cover"/> the part is cut further to the target's shape (a blurred background fills the frame).
    /// </summary>
    private bool DrawSource(in GpuLayerDescriptor layer, GpuTexture target, Vector4 uv, bool cover)
    {
        var constants = default(PassConstants);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        switch ((GpuSourceKind)layer.SourceKind)
        {
            case GpuSourceKind.Media:
                {
                    // Instant Seek: the exact frame, or the nearest one decoded while the exact one is on its way.
                    var frame = _decoders.Resolve(layer.SourceId, layer.MediaSeconds, layer.SourcePathHash, layer.ClipKey);
                    if (frame is null || frame.LumaSrv == 0)
                        return false;
                    if (!cover)
                        _layerStep = double.IsFinite(frame.Until) && frame.Until > frame.Time ? frame.Until - frame.Time : 0;
                    if (cover)
                        uv = Cover(uv, frame.Width, frame.Height, target.Width, target.Height);
                    SetYuv(ref constants, frame.Matrix, frame.FullRange);
                    constants.U0 = uv.X;
                    constants.V0 = uv.Y;
                    constants.U1 = uv.Z;
                    constants.V1 = uv.W;
                    constants.Params2 = new Vector4(frame.Width, frame.Height, frame.Width / 2, frame.Height / 2);
                    constants.Params3 = new Vector4((uv.Z - uv.X) * frame.Width / target.Width, (uv.W - uv.Y) * frame.Height / target.Height, 0, 0);
                    var planes = stackalloc nint[2] { frame.LumaSrv, frame.ChromaSrv };
                    _gpu.Context.PSSetShaderResources(3, 2, planes);
                    _timer.Begin(0);
                    Draw("PSYuvSource", null, target.Rtv, target.Width, target.Height, ref constants, blend: false);
                    _timer.End();
                    var none = stackalloc nint[2];
                    _gpu.Context.PSSetShaderResources(3, 2, none);
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

    /// <summary>A part of a picture cut further, about its middle, to the shape of the target (cover: no bars).</summary>
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

    /// <summary>The YUV to RGB rows for a frame's matrix and range (limited: Y 16-235, C 16-240 in 8-bit terms).</summary>
    private static void SetYuv(ref PassConstants constants, int matrix, bool fullRange)
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

    /// <summary>An adjustment layer's picture: the canvas under its box.</summary>
    private bool CopyCanvasInto(GpuTexture target, (float X, float Y, float W, float H) box)
    {
        _canvasCopy ??= _gpu.CreateTexture(_outWidth, _outHeight, DxgiFormat.R16G16B16A16Float, renderTarget: false);
        _gpu.Context.CopyResource(_canvasCopy.Texture, _canvas!.Texture);
        var constants = default(PassConstants);
        constants.U0 = box.X / _outWidth;
        constants.V0 = box.Y / _outHeight;
        constants.U1 = (box.X + box.W) / _outWidth;
        constants.V1 = (box.Y + box.H) / _outHeight;
        Draw("PSCopy", _canvasCopy, target.Rtv, target.Width, target.Height, ref constants, blend: false);
        return true;
    }

    // ----- Zones 1-3 -----

    /// <summary>Runs one effect on the working texture; returns the texture holding the result (the same one when it did nothing).</summary>
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

                    // Its Advanced Choke (P6, px) and Smooth (P7, px), 5.0-Release: Matte Choke's square and Matte Smooth's
                    // Hann weights in space, on its own transparency (the builder's erosion or dilation and convolution).
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

                    // Over time while it plays or steps in order (the user's Q3, as FFmpeg's tmix); over space after a jump.
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
                    // FFmpeg's boxblur (avgblur with a box of its own down), as wide as a Gaussian of half the radius, run
                    // Passes times (P1; 0 in a packet made by hand: the 2 it always was), Horizontal / Vertical (P2), then
                    // the Dim (P3, %): EffectRegistry's Blur and BlurSizes.
                    var radiusPx = Math.Clamp(packet.P0, 0, 50);
                    if (radiusPx >= 0.05f)
                    {
                        // The frame's width is the canvas's in the target's pixels (the target is the canvas only on an
                        // export: a letterboxed player, the loupe and a 3D plane draw it at their own scale).
                        var passes = packet.P1 >= 1 ? (int)MathF.Round(Math.Clamp(packet.P1, 1, 4)) : 2;
                        var frameWidth = (int)MathF.Round(_pxScale1080 * 1080);
                        var (across, down) = EffectRegistry.BlurSizes(radiusPx, packet.P2 > 0 ? packet.P2 : 1, passes, frameWidth, work.Width, work.Height);
                        if (across > 0 && down > 0)
                            work = Blur(work, across, down, passes, (int)GpuShaderSlot.BoxBlur);
                    }

                    return packet.P3 >= 0.5f ? Dim(work, -Math.Clamp(packet.P3, 0, 50) / 100 * EqBrightness, (int)GpuShaderSlot.BoxBlur) : work;
                }

            case GpuShaderSlot.Sharpen:
                return packet.P0 < 0.005f ? work : Effect("PSSharpen", packet, default, work);

            default:
                // Composite-stage packets (handled when the layer is laid on), reserved slots, a plugin's unknown slot.
                return work;
        }
    }

    // ----- Matte Smooth over time (the user's Q3) -----

    /// <summary>The layer's source's frame length (0: not a media layer, a 30 fps step is assumed).</summary>
    private double _layerStep;

    private readonly Dictionary<long, AlphaHistory> _histories = [];

    /// <summary>
    /// Matte Smooth over the last <paramref name="frames"/> frames (Hann weights, as the FFmpeg path's tmix): the layer's
    /// alpha of each frame drawn is kept (R8, its working size) while the moments follow one another a frame apart; a
    /// redraw of the same moment reuses them. Null after a jump (or before enough frames came in order): the caller
    /// smooths over space instead, and the history starts again from this frame.
    /// </summary>
    private GpuTexture? TemporalMatte(in GpuLayerDescriptor layer, int frames, GpuTexture work)
    {
        if (!_shaders.Has("PSMatteTemporal") || !_shaders.Has("PSAlphaOf"))
            return null;
        var moment = layer.SourceKind == (int)GpuSourceKind.Media ? layer.MediaSeconds : _time;
        var step = _layerStep > 1e-4 ? _layerStep : 1 / 30.0;

        // A layer drawn again alone at another size (the loupe) keeps a history of its own: the picture's is never reset by it.
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

        // The frames-1 before the newest (the current one), all a frame apart.
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

    /// <summary>Histories of layers not drawn for two seconds' worth of frames go.</summary>
    private void ForgetOldHistories()
    {
        if (_histories.Count == 0 || _frame % 60 != 0)
            return;
        foreach (var key in _histories.Where(p => _frame - p.Value.LastFrame > 120).Select(p => p.Key).ToList())
        {
            _histories[key].Dispose();
            _histories.Remove(key);
        }
    }

    /// <summary>A smoothed layer's last frames' transparency (R8 at its working size), newest last, with their moments.</summary>
    private sealed class AlphaHistory : IDisposable
    {
        // The current frame and the eight before it (Matte Smooth's most, 9 frames); eight are bound at t5..t12.
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

        /// <summary>The entry <paramref name="back"/> frames before the newest (1 the one just before it).</summary>
        public GpuTexture? Back(int back) => back < Count ? _ring[(_next - 1 - back + 2 * Depth) % Depth] : null;

        /// <summary>Whether the newest and the <paramref name="count"/> before it follow one another a frame apart.</summary>
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

    /// <summary>One full-texture pass of an effect, into a fresh texture of the same size; the old one goes back to the pool.</summary>
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

    /// <summary>A separable pass: across then down (Params.y), <paramref name="radius"/> texels (Params.z), so many times.</summary>
    private GpuTexture Separable(string shader, GpuEffectPacket packet, GpuTexture work, int radius, int iterations, int timedAs = -1) =>
        Separable(shader, packet, work, radius, (float)radius, iterations, timedAs);

    /// <summary>
    /// A separable pass with a radius of its own down: across <paramref name="radius"/> texels, down <paramref name="down"/>
    /// (Params.z; a box blur takes a fraction of a texel as the outer taps' weight), so many times.
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

    private GpuTexture Blur(GpuTexture work, int radius, int iterations, int timedAs) =>
        Separable("PSBoxBlur", new GpuEffectPacket(GpuShaderSlot.BoxBlur, radius, 0, 0, 0, 0, 0, 0, 0), work, Math.Min(radius, 64), iterations, timedAs);

    /// <summary>A box blur with a box of its own down (texels, a fraction weighing the outer taps; held to 64), so many times.</summary>
    private GpuTexture Blur(GpuTexture work, float across, float down, int iterations, int timedAs) =>
        Separable("PSBoxBlur", new GpuEffectPacket(GpuShaderSlot.BoxBlur, across, 0, 0, 0, 0, 0, 0, 0), work, Math.Min(across, 64), Math.Min(down, 64), iterations, timedAs);

    /// <summary>
    /// What eq's brightness b comes to on the RGB the GPU works in (5.0-Release): eq adds 256 b to the 8-bit luma of the
    /// export's limited-range YUV, which is 256 / 219 of b on each channel once decoded (the blurred background's dimming and
    /// the Blur card's Dim, measured against the export by gpu-scene-differential).
    /// </summary>
    private const float EqBrightness = 256f / 219f;

    /// <summary>The picture's brightness changed by <paramref name="amount"/> (PSDim: added to every channel alike).</summary>
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

    // ----- Zone 4: under the layer -----

    /// <summary>
    /// The layer's drop shadow (4.1.1-B): its outline in the shadow's colour at its opacity, with room round it for the
    /// blur (boxblur's alpha twice, as FFmpeg makes it), laid down moved along the light's angle.
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

        // The bigger box keeps the same turning point.
        var roomX = room * MathF.Sign(box.W == 0 ? 1 : box.W);
        var roomY = room * MathF.Sign(box.H == 0 ? 1 : box.H);
        var (w, h) = (box.W + 2 * roomX, box.H + 2 * roomY);
        var anchorX = w == 0 ? 0.5f : (layer.AnchorX * box.W + roomX) / w;
        var anchorY = h == 0 ? 0.5f : (layer.AnchorY * box.H + roomY) / h;
        var place = default(PassConstants);
        place.Transform = Matrix4.Box(box.X - roomX + shiftX, box.Y - roomY + shiftY, w, h, layer.Rotation, anchorX, anchorY, _outWidth, _outHeight);
        place.WholeUv();
        place.Params4 = new Vector4(alpha, 1, 0, (float)_time);
        SetWipe(ref place, wipe, (box.X, box.Y, w, h));
        _timer.Begin((int)GpuShaderSlot.DropShadow);
        Quad("PSComposite", seed, _canvas!.Rtv, ref place, blend: true);
        _timer.End();
    }

    /// <summary>
    /// The blurred background made of the layer's picture (FilterGraphBuilder.Video.cs BackgroundRecipe): the part its crop
    /// names (the background's zoom) cut to the frame's shape, drawn smaller the more it is blurred (a half from 6 output
    /// pixels of radius, a quarter from 16: the box stays narrow and cheap and the blur hides the smaller picture), box-blurred
    /// BlurFillPasses times with the radius's fraction its own (5.0-Release: continuous from 0, sharp, and drawn whole then),
    /// dimmed, then (<paramref name="withLook"/>) its own Look cards on the frame-sized picture, and laid over the whole frame.
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

        // Its own effects (colour, a LUT, a vignette...), on the picture at the frame's size, so that their pixel sizes are the frame's.
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
                var second = k + 1 < count && effects[k + 1].Slot == packet.Slot ? effects[k + 1] : default;
                if (packet.Slot == GpuShaderSlot.ColorBalance && second.Slot == packet.Slot)
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

    /// <summary>How many times smaller than the frame a blurred background is drawn for its radius in output pixels: 1, 2 or 4.</summary>
    internal static int BlurFillShrink(float radius) => radius >= 16 ? 4 : radius >= 6 ? 2 : 1;

    /// <summary>Whether a layer's packets hold a Look or Key and Matte effect (anything but what lays it on: zone 4's and the transitions').</summary>
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

    /// <summary>A full-target pass (VSFull): input in t0, aux in t1, the LUT in t2.</summary>
    private void Draw(string shader, GpuTexture? input, nint target, int width, int height, ref PassConstants constants, bool blend, nint aux = 0, nint lut = 0)
    {
        if (input is not null)
            constants.Sizes(input.Width, input.Height, width, height);
        else
            constants.Sizes(width, height, width, height);
        Submit("VSFull", shader, input?.Srv ?? 0, aux, lut, target, width, height, ref constants, blend, vertices: 3, clipToFrame: false);
    }

    /// <summary>A placed quad (VSQuad) onto the canvas, kept within the frame (letterboxed: never over the bars).</summary>
    private void Quad(string shader, GpuTexture input, nint target, ref PassConstants constants, bool blend, nint aux = 0)
    {
        constants.Sizes(input.Width, input.Height, _outWidth, _outHeight);
        Submit("VSQuad", shader, input.Srv, aux, 0, target, _outWidth, _outHeight, ref constants, blend, vertices: 4, clipToFrame: _letterbox);
    }

    private void Submit(string vertex, string pixel, nint input, nint aux, nint lut, nint target, int width, int height, ref PassConstants constants, bool blend, uint vertices, bool clipToFrame)
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
        _gpu.SetBlend(blend);
        context.Draw(vertices, 0);

        // Let go of the inputs and the target, so the next pass may write into either.
        var none = stackalloc nint[3];
        context.PSSetShaderResources(0, 3, none);
        context.OMSetRenderTargets(0);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _canvas?.Dispose();
        _canvasCopy?.Dispose();
        _blank?.Dispose();
        DisposeExploded();
        DisposeLoupe();
        foreach (var history in _histories.Values)
            history.Dispose();
        _histories.Clear();
    }
}

/// <summary>
/// GPU timestamps around each pass, summed per shader slot (ControlBlock.SlotGpuMs): the proof that every slot runs in
/// well under half a millisecond. Only when the scene asks (FrameSceneHeader.FlagTimePasses).
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

    /// <summary>Waits for this frame's timestamps (tests and the benchmark only) and sums them per slot. False when the clock was disjoint.</summary>
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
