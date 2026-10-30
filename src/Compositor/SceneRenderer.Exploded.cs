using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- The 3D exploded layer stack (Pass 5.0-AudioAnd3D, 5.0D; full resolution in 5.0-Release) -----
// FrameSceneHeader.ViewMode 1: every layer of the scene is a plane in perspective, at its place in the stack
// (GpuLayerDescriptor.ZDepth x FrameSceneHeader.ExplodedZSpread), seen by ExplodedCamera (the maths HandPeg picks with).
// Each plane's picture is the layer drawn alone, exactly as the flat path draws it (DrawLayer: its source, its effects, its
// shadow and blurred background, its box, turn, opacity and transitions) onto a transparent canvas of its own, so a masked
// HUD piece shows only what its mask keeps. The planes are laid back to front (premultiplied over, no depth buffer:
// parallel planes are ordered by their distance from the camera along their normal), over a quiet backdrop and the
// canvas's frame at the back. A layer with a mask or a key, and the clip selected in the editor
// (GpuLayerDescriptor.FlagSelected), gets a neon outline round its silhouette. An adjustment layer has no picture of its
// own: it is a pane of glass over its box. Blend modes are not mixed in this view (each plane shows its own picture).
// The flat path (ViewMode 0) is untouched: RenderLayers branches here first.
//
// Full resolution (5.0-Release, the user's directive 4): a plane's picture is drawn at its source's own pixels (the part
// its crop keeps, a texel a source pixel, up to 4096 a side), never coarser than the flat player draws it, so the camera
// brought close to any plane shows it sharp; but no finer than its own blurs, shadow and chokes can follow (a pass reaches
// 64 texels: RadiusLimit), stepped down by quarter octaves. Only the part of the canvas the layer can reach is drawn (its
// turned box and its shadow; the whole canvas for a blurred background or a slide), into the top left of one plane texture
// kept from frame to frame (grown in steps, made smaller after a while oversized, let go once the view has been flat a
// while; a blended layer's own two, stepped too). The planes and their working textures together are held to one budget
// (PlaneBudgetBytes): over it, every plane's resolution above the fitted one steps down a quarter of an octave at a time,
// and back up once there is room again. A layer drawn at its source's own pixels has working textures of its source's
// size whatever its box does, so a box animated in size rents nothing new frame by frame.
internal sealed unsafe partial class SceneRenderer
{
    // The 3D picture (the output's size), and the plane every layer is drawn into in turn (its top left part).
    private GpuTexture? _view3D;
    private GpuTexture? _planeTarget;

    // Frames the plane texture has been more than twice the size needed; when the last frame was drawn in 3D (Stopwatch),
    // and the texture pool's frame then (what it lent since is the flat path's).
    private int _planeTargetOversized;
    private long _explodedAt, _explodedFrame;

    // The order the planes are drawn in, its keys, and each plane's layout (in that order): allocated once.
    private readonly int[] _planeOrder = new int[CompositorProtocol.MaxLayers];
    private readonly float[] _planeKeys = new float[CompositorProtocol.MaxLayers];
    private readonly PlaneLayout[] _planeLayouts = new PlaneLayout[CompositorProtocol.MaxLayers];

    // How far the planes are stepped down to keep within the budget (0: their full resolution).
    private int _budgetStep;

    /// <summary>The largest side of a plane's picture, in pixels.</summary>
    private const int MaxPlaneSide = 4096;

    /// <summary>What the planes and their working textures may take of the card's memory together (RGBA16F: 8 bytes a texel).</summary>
    private const long PlaneBudgetBytes = 512L << 20;

    /// <summary>The plane texture grows in steps of this many texels a side (a box animated in size rarely needs a new one).</summary>
    private const int PlaneTargetStep = 256;

    /// <summary>The budget's last step (each a quarter of an octave) before the fitted resolution.</summary>
    private const int LastBudgetStep = 16;

    /// <summary>
    /// How many frames the plane texture is kept oversized; how long the 3D textures are kept once the view is flat (seconds:
    /// the player draws the flat picture once more after it, GpuPreviewEngine.Exploded.cs, so a paused player lets them go too).
    /// </summary>
    private const int PlaneTargetPatience = 240;
    internal const double ExplodedTexturesPatience = 2;

    // The outline's colours (straight RGB): neon cyan round a mask's silhouette, warm amber round the clip selected; how
    // strong the amber is round another clip of the selected one's track.
    private static readonly Vector4 MaskOutline = new(0.05f, 0.9f, 1f, 0.8f), SelectedOutline = new(1f, 0.72f, 0.18f, 1f);
    private const float SelectedTrackStrength = 0.45f;

    /// <summary>
    /// Where a plane's picture lies and how finely it is drawn: the part of the canvas it can reach (canvas pixels), its
    /// layer's box size, its scale at its source's own pixels (texels a canvas pixel; 0: as fitted), the scale drawn at, its
    /// first texel on the canvas's texel grid and its size in texels; whether its layer is blended, and whether it has
    /// nothing on the canvas (or is an adjustment layer's glass).
    /// </summary>
    private struct PlaneLayout
    {
        public float X, Y, W, H, BoxW, BoxH;
        public float Full, Scale;
        public int OriginX, OriginY, TexelsX, TexelsY;
        public bool Blends, Empty;
    }

    /// <summary>Draws the scene as the 3D exploded layer stack (ViewMode 1) into <paramref name="outputRtv"/>.</summary>
    private void RenderExploded(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, nint outputRtv, int outputWidth, int outputHeight, bool raw)
    {
        var camera = ExplodedCamera.From(header, outputWidth, outputHeight);
        var (canvasWidth, canvasHeight) = (camera.CanvasWidth, camera.CanvasHeight);
        _explodedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // The fitted resolution (the canvas as the flat player draws it at no zoom) is each plane's least.
        var fit = Math.Min(camera.Fit, (float)MaxPlaneSide / Math.Max(canvasWidth, canvasHeight));
        _letterbox = false;
        _time = header.PlayheadSeconds;

        var timing = (header.Flags & FrameSceneHeader.FlagTimePasses) != 0;
        _timer.BeginFrame(timing);
        _gpu.BindCommon();

        if (_view3D is null || _view3D.Width != outputWidth || _view3D.Height != outputHeight)
        {
            _view3D?.Dispose();
            _view3D = _gpu.CreateTexture(outputWidth, outputHeight, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        }

        var amount = camera.Amount;
        _gpu.Context.ClearRenderTargetView(_view3D.Rtv, 0, 0, 0, raw ? 0 : 1);
        if (!raw)
            DrawBackdrop(amount, outputWidth, outputHeight);
        DrawCanvasFrame(layers, camera, header, amount, outputWidth, outputHeight);

        // Back to front: the farthest from the camera along the planes' normal first; a tie keeps the stack's order (at no
        // spread every plane is the same distance away, and the stack is drawn as the flat picture is).
        var count = SortPlanes(layers, camera);
        LayOutPlanes(layers, count, canvasWidth, canvasHeight, fit);

        var (canvas, canvasCopy) = (_canvas, _canvasCopy);
        try
        {
            for (var n = 0; n < count; n++)
            {
                ref readonly var layer = ref layers[_planeOrder[n]];
                var z = camera.DepthOf(layer.ZDepth);
                if ((layer.Flags & GpuLayerDescriptor.FlagAdjustment) != 0)
                {
                    DrawGlass(layer, camera, z, amount, outputWidth, outputHeight);
                    continue;
                }

                // The layer alone, onto a clear plane of its own: the flat path's canvas is this plane while it draws.
                var plane = _planeLayouts[n];
                var target = plane.Empty ? null : plane.Blends ? _gpu.Textures.Rent(plane.TexelsX, plane.TexelsY) : _planeTarget;
                if (target is not null)
                    DrawAlone(layer, plane, target, canvasWidth, canvasHeight);

                // Whatever a pass of the layer's own left bound, the planes' passes start from the frame's common state (the
                // outline reads the border sampler and the shared constants): nothing an effect does reaches the outline.
                _gpu.BindCommon();
                DrawPlane(target, plane, camera, z, amount, outputWidth, outputHeight);

                // Amber round the clip selected, fainter round another clip of its track playing while it is not (its plane
                // never blinks off at a cut), else cyan round a mask's or a key's silhouette.
                var selected = (layer.Flags & GpuLayerDescriptor.FlagSelected) != 0;
                var outline = selected ? SelectedOutline
                    : (layer.Flags & GpuLayerDescriptor.FlagSelectedTrack) != 0 ? SelectedOutline with { W = SelectedOutline.W * SelectedTrackStrength }
                    : HasMatte(layer) ? MaskOutline : default;
                if (target is not null && outline.W > 0)
                    DrawOutline(target, plane, camera, z, outline, amount, outputWidth, outputHeight);

                // The layer selected (a click, or Ctrl + the wheel through the stack) also gets its box in the accent, so
                // which one a drag will move is never in doubt (5.0-Verify, the user's bug 5).
                if (selected)
                    DrawSelectedBox(layer, camera, z, amount, outputWidth, outputHeight);
                _gpu.Textures.ReturnAll();
            }
        }
        finally
        {
            // The flat path's own canvas back, as it was (it is made again at its own size when it next draws).
            (_canvas, _canvasCopy) = (canvas, canvasCopy);
        }

        var constants = default(PassConstants);
        constants.Color = new Vector4(0, 0, 0, 1);
        constants.Params4 = new Vector4(1, 1, raw ? 1 : 0, (float)_time);
        constants.WholeUv();
        _timer.Begin(0);
        Draw("PSFinal", _view3D, outputRtv, outputWidth, outputHeight, ref constants, blend: false);
        _timer.End();
        _gpu.Textures.EndFrame();
        _explodedFrame = _gpu.Textures.Frame;
        _timer.EndFrame();
        ForgetOldHistories();
    }

    /// <summary>
    /// Draws a layer alone into the top left of <paramref name="target"/>: as the flat path draws it on a canvas of the
    /// plane's scale moved so the plane's first texel is the target's first (every pass of DrawLayer, its sizes, pixel
    /// lanes, slides and blurred background, works as it does on the player's canvas).
    /// </summary>
    private void DrawAlone(in GpuLayerDescriptor layer, in PlaneLayout plane, GpuTexture target, int canvasWidth, int canvasHeight)
    {
        var scale = plane.Scale;
        (_outWidth, _outHeight) = (plane.TexelsX, plane.TexelsY);
        (_scaleX, _scaleY) = (scale, scale);
        (_offsetX, _offsetY) = (-plane.OriginX, -plane.OriginY);
        _frameRect = (_offsetX, _offsetY, canvasWidth * scale, canvasHeight * scale);
        _pxScale1080 = canvasWidth * scale / 1080f;
        _pixelScale = scale;

        // Cleared: the picture's part and a texel beyond it (as far as the planes' passes read), not the whole texture.
        var clear = default(PassConstants);
        clear.WholeUv();
        Draw("PSSolid", null, target.Rtv, Math.Min(plane.TexelsX + 1, target.Width), Math.Min(plane.TexelsY + 1, target.Height), ref clear, blend: false);

        // A blended layer reads the canvas under it: a copy of its own plane, which is its own size (LayOutPlanes).
        _canvas = target;
        _canvasCopy = plane.Blends ? _gpu.Textures.Rent(target.Width, target.Height) : null;
        DrawLayer(layer);

        // A copy DrawLayer made itself (a blend Blends did not foresee) is no plane's size: never kept.
        if (_canvasCopy is { Rented: false } made)
            made.Dispose();
        _canvasCopy = null;
    }

    // ----- How finely each plane is drawn -----

    /// <summary>
    /// Each plane's part of the canvas and its scale (into <see cref="_planeLayouts"/>, in drawing order), held to the
    /// budget, and the plane texture made large enough for the largest of them.
    /// </summary>
    private void LayOutPlanes(ReadOnlySpan<GpuLayerDescriptor> layers, int count, int canvasWidth, int canvasHeight, float fit)
    {
        for (var n = 0; n < count; n++)
        {
            ref readonly var layer = ref layers[_planeOrder[n]];
            ref var plane = ref _planeLayouts[n];
            plane = default;
            if ((layer.Flags & GpuLayerDescriptor.FlagAdjustment) != 0)
            {
                plane.Empty = true;
                continue;
            }

            (plane.X, plane.Y, plane.W, plane.H) = RegionOf(layer, canvasWidth, canvasHeight);
            (plane.BoxW, plane.BoxH) = (MathF.Abs(layer.Width), MathF.Abs(layer.Height));
            plane.Empty = !(plane.W > 0 && plane.H > 0);

            // WARP draws on the processor, and a blurred background is a blur: both at the fitted resolution, as always. No
            // finer than its own radii allow (a blur, a shadow, a choke held at the 64 texels a pass reaches would be less
            // than the flat picture's).
            plane.Full = _gpu.IsWarp || (layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0 || plane.Empty ? 0 : FullScaleOf(layer);
            if (plane.Full > fit)
                plane.Full = WithinRadii(plane.Full, RadiusLimit(layer, canvasWidth), fit);
            plane.Blends = !plane.Empty && Blends(layer);
        }

        // Within the budget: down a step while over it, back up one once that fits with a fifth to spare.
        var step = Math.Clamp(_budgetStep, 0, LastBudgetStep + 1);
        while (step <= LastBudgetStep && BytesAt(count, StepFactor(step), fit) > PlaneBudgetBytes)
            step++;
        while (step > 0 && BytesAt(count, StepFactor(step - 1), fit) <= PlaneBudgetBytes / 5 * 4)
            step--;
        _budgetStep = step;

        var factor = StepFactor(step);
        var (largestX, largestY) = (0, 0);
        for (var n = 0; n < count; n++)
        {
            ref var plane = ref _planeLayouts[n];
            if (plane.Empty)
                continue;
            Place(ref plane, ScaleAt(plane, factor, fit));
            if (!plane.Blends)
                (largestX, largestY) = (Math.Max(largestX, plane.TexelsX), Math.Max(largestY, plane.TexelsY));
        }

        EnsurePlaneTarget(largestX, largestY);
    }

    /// <summary>The share of a plane's resolution above the fitted one a budget step keeps: a quarter of an octave less a step; none past the last.</summary>
    private static float StepFactor(int step) => step > LastBudgetStep ? 0 : MathF.Pow(2, -step / 4f);

    /// <summary>A plane's scale at a budget step: the fitted one and that share of what its source's own pixels add; within the largest side.</summary>
    private static float ScaleAt(in PlaneLayout plane, float factor, float fit)
    {
        var scale = plane.Full > fit ? fit + (plane.Full - fit) * factor : fit;
        return Math.Min(scale, (MaxPlaneSide - 2) / Math.Max(Math.Max(plane.W, plane.H), 1));
    }

    /// <summary>
    /// A plane's texels at a scale: on the canvas's texel grid (a box moved by a fraction keeps its picture's texels), at
    /// least 2 x 2. A blended plane's are taken up to the plane texture's steps: its two textures are its size exactly (its
    /// canvas copy must be), and the pool keeps only sizes it is asked for again, so a region or a scale that changes from
    /// frame to frame (a zoom, a turn, a slide over the edge) rents a few sizes, never a new pair a frame. The margin is
    /// cleared with the rest and lies outside the layer's reach: nothing is drawn or read there.
    /// </summary>
    private static void Place(ref PlaneLayout plane, float scale)
    {
        plane.Scale = scale;
        plane.OriginX = (int)MathF.Floor(plane.X * scale);
        plane.OriginY = (int)MathF.Floor(plane.Y * scale);
        plane.TexelsX = Math.Clamp((int)MathF.Ceiling((plane.X + plane.W) * scale) - plane.OriginX, 2, MaxPlaneSide);
        plane.TexelsY = Math.Clamp((int)MathF.Ceiling((plane.Y + plane.H) * scale) - plane.OriginY, 2, MaxPlaneSide);
        if (plane.Blends)
            (plane.TexelsX, plane.TexelsY) = (Stepped(plane.TexelsX), Stepped(plane.TexelsY));
    }

    /// <summary>Texels taken up to the plane texture's next step (<see cref="PlaneTargetStep"/>), within the largest side.</summary>
    private static int Stepped(int texels) => Math.Min((texels + PlaneTargetStep - 1) / PlaneTargetStep * PlaneTargetStep, MaxPlaneSide);

    /// <summary>
    /// What the planes take at a budget step: the plane texture (as large as the largest plane), each blended plane's own
    /// two (it and its copy), and two working textures of each layer's box (what an effect ping-pongs between).
    /// </summary>
    private long BytesAt(int count, float factor, float fit)
    {
        long largest = 0, rest = 0;
        for (var n = 0; n < count; n++)
        {
            var plane = _planeLayouts[n];
            if (plane.Empty)
                continue;
            Place(ref plane, ScaleAt(plane, factor, fit));
            var texels = (long)plane.TexelsX * plane.TexelsY;
            if (plane.Blends)
                rest += 2 * texels;
            else
                largest = Math.Max(largest, texels);
            rest += 2 * (long)Math.Clamp(plane.BoxW * plane.Scale, 2, 8192) * (long)Math.Clamp(plane.BoxH * plane.Scale, 2, 8192);
        }

        return (largest + rest) * 8;
    }

    /// <summary>
    /// The plane texture, at least as large as the largest plane of the frame: grown in steps (as large as it was each way,
    /// or as needed), made smaller once it has been more than twice the size needed for a while.
    /// </summary>
    private void EnsurePlaneTarget(int neededX, int neededY)
    {
        if (neededX <= 0 || neededY <= 0)
            return;

        var (width, height) = (Stepped(neededX), Stepped(neededY));
        if (_planeTarget is { } target)
        {
            if (target.Width >= neededX && target.Height >= neededY)
            {
                var oversized = (long)target.Width * target.Height > 2L * width * height;
                _planeTargetOversized = oversized ? _planeTargetOversized + 1 : 0;
                if (_planeTargetOversized < PlaneTargetPatience)
                    return;
            }
            else
            {
                (width, height) = (Math.Max(width, target.Width), Math.Max(height, target.Height));
            }

            target.Dispose();
        }

        _planeTarget = _gpu.CreateTexture(width, height, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        _planeTargetOversized = 0;
    }

    /// <summary>
    /// The part of the canvas a layer can draw on (canvas pixels): its box as turned, with room for its shadow, within the
    /// canvas; the whole canvas for a blurred background, and while a slide or a zoom transition carries the box; empty
    /// when it is wholly off the canvas.
    /// </summary>
    private static (float X, float Y, float W, float H) RegionOf(in GpuLayerDescriptor layer, int canvasWidth, int canvasHeight)
    {
        if ((layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0 || layer.BlurFillRadius > 0)
            return (0, 0, canvasWidth, canvasHeight);

        var reach = 1f;
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        for (var k = 0; k < count; k++)
        {
            var packet = layer.Effects[k];
            if (packet.Slot is GpuShaderSlot.TransitionSlide or GpuShaderSlot.TransitionZoom)
                return (0, 0, canvasWidth, canvasHeight);
            if (packet.Slot == GpuShaderSlot.DropShadow)
                reach = Math.Max(reach, Math.Clamp(packet.P0, 0, 100) + 2 * Math.Clamp(packet.P2, 0, 50) + 2);
        }

        var (left, top, right, bottom) = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        for (var corner = 0; corner < 4; corner++)
        {
            var point = ExplodedCamera.BoxPoint(layer.X, layer.Y, layer.Width, layer.Height, layer.Rotation, layer.AnchorX, layer.AnchorY, corner & 1, corner >> 1);
            (left, top) = (Math.Min(left, point.X), Math.Min(top, point.Y));
            (right, bottom) = (Math.Max(right, point.X), Math.Max(bottom, point.Y));
        }

        (left, top) = (Math.Max(left - reach, 0), Math.Max(top - reach, 0));
        (right, bottom) = (Math.Min(right + reach, canvasWidth), Math.Min(bottom + reach, canvasHeight));
        return float.IsFinite(left + top + right + bottom) && right > left && bottom > top ? (left, top, right - left, bottom - top) : (0, 0, 0, 0);
    }

    /// <summary>
    /// A layer's scale at its source's own pixels: texels a canvas pixel for one source pixel a texel, across or down
    /// (whichever is finer) of the part its crop keeps; 1 (the canvas's own pixels) for a colour, a pattern, or a source with
    /// nothing decoded yet.
    /// </summary>
    private float FullScaleOf(in GpuLayerDescriptor layer)
    {
        var (boxWidth, boxHeight) = (MathF.Abs(layer.Width), MathF.Abs(layer.Height));
        if (boxWidth < 0.5f || boxHeight < 0.5f)
            return 0;

        var (width, height) = (0, 0);
        switch ((GpuSourceKind)layer.SourceKind)
        {
            case GpuSourceKind.Media:
                // The frame the layer draws: DrawSource resolves it again next, which asks for nothing more (a second
                // resolve of a moment in a frame is a repeat; its pins are let go together at the frame's end).
                if (_decoders.Resolve(layer.SourceId, layer.MediaSeconds, layer.SourcePathHash, layer.ClipKey) is { LumaSrv: not 0 } frame)
                    (width, height) = (frame.Width, frame.Height);
                break;
            case GpuSourceKind.Still:
                if (_resources.Texture2D(layer.StillResourceId) is { } still)
                    (width, height) = (still.Width, still.Height);
                break;
        }

        if (width <= 0 || height <= 0)
            return 1;
        var uv = CropUv(layer);
        return Math.Max((uv.Z - uv.X) * width / boxWidth, (uv.W - uv.Y) * height / boxHeight);
    }

    /// <summary>The most texels a pass's radius reaches (Effects.hlsl MaxRadius; Blur holds its boxes to it too).</summary>
    private const float MostPassRadius = 64;

    /// <summary>
    /// The finest scale a layer's radii leave room for: its widest radius in texels a canvas pixel (a Blur card's box as
    /// RunEffect works it out, its shadow's softness, a key's choke and smooth, a Matte Choke, a Matte Smooth over space, a blur
    /// dissolve's blur) within <see cref="MostPassRadius"/>; no limit for a layer with none.
    /// </summary>
    private static float RadiusLimit(in GpuLayerDescriptor layer, int canvasWidth)
    {
        var widest = 0f;
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        for (var k = 0; k < count; k++)
        {
            var packet = layer.Effects[k];
            switch (packet.Slot)
            {
                case GpuShaderSlot.BoxBlur when packet.P0 >= 0.05f:
                    {
                        // The boxes at 16 times the canvas's pixels, a sixteenth of them: their texels a canvas pixel, finely.
                        var passes = packet.P1 >= 1 ? (int)MathF.Round(Math.Clamp(packet.P1, 1, 4)) : 2;
                        var (across, down) = EffectRegistry.BlurSizes(Math.Clamp(packet.P0, 0, 50), packet.P2 > 0 ? packet.P2 : 1, passes, canvasWidth * 16, int.MaxValue, int.MaxValue);
                        widest = Math.Max(widest, Math.Max(across, down) / 16f);
                        break;
                    }

                case GpuShaderSlot.DropShadow:
                    widest = Math.Max(widest, Math.Clamp(packet.P2, 0, 50));
                    break;
                case GpuShaderSlot.ChromaKey:
                    widest = Math.Max(widest, Math.Max(MathF.Abs(packet.P6) >= 0.5f ? MathF.Abs(packet.P6) : 0, Math.Clamp(packet.P7, 0, 20)));
                    break;
                case GpuShaderSlot.MatteChoke when MathF.Abs(packet.P0) >= 0.5f:
                    widest = Math.Max(widest, MathF.Abs(packet.P0));
                    break;
                case GpuShaderSlot.MatteSmooth:
                    widest = Math.Max(widest, (Math.Clamp(packet.P0, 1, 9) - 1) / 2);
                    break;
                case GpuShaderSlot.TransitionBlurDissolve:
                    widest = Math.Max(widest, Math.Max(MathF.Abs(layer.Width), MathF.Abs(layer.Height)) / 40);
                    break;
            }
        }

        return widest > 0 ? MostPassRadius / widest : float.MaxValue;
    }

    /// <summary>
    /// A plane's scale at its source's own pixels held to its radii's limit: stepped down a quarter of an octave at a time
    /// until it is within it (a working texture then keeps its size while the box animates, as at the source's own pixels),
    /// never below the fitted one.
    /// </summary>
    private static float WithinRadii(float full, float limit, float fit)
    {
        if (full <= limit)
            return full;
        var steps = MathF.Ceiling(4 * MathF.Log2(full / limit));
        return Math.Max(full * MathF.Pow(2, -steps / 4), fit);
    }

    /// <summary>Whether DrawLayer lays a layer on with a blend mode (it then reads a copy of the canvas under it), as it decides it.</summary>
    private static bool Blends(in GpuLayerDescriptor layer)
    {
        if ((layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0)
            return false;
        var mode = layer.BlendMode;
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        for (var k = 0; k < count; k++)
        {
            if (layer.Effects[k].Slot == GpuShaderSlot.BlendMode)
                mode = (int)MathF.Round(layer.Effects[k].P0);
        }

        return mode is > 0 and <= 7;
    }

    /// <summary>
    /// The 3D view's textures let go once the view has been flat a while (the flat path asks at every frame it draws): its
    /// own, and the pool's working textures nothing has used since its last frame (full-resolution planes' sizes, which the
    /// pool would otherwise keep until 240 more frames are drawn: never, while paused).
    /// </summary>
    private void ForgetExplodedTextures()
    {
        if ((_planeTarget is not null || _view3D is not null)
            && System.Diagnostics.Stopwatch.GetElapsedTime(_explodedAt).TotalSeconds > ExplodedTexturesPatience)
        {
            DisposeExploded();
            _gpu.Textures.ReleaseUnusedSince(_explodedFrame);
        }
    }

    /// <summary>
    /// The shown layers in the order to draw them (into <see cref="_planeOrder"/>): farthest first by
    /// <see cref="ExplodedCamera.DistanceTo"/>, ties in the stack's order. An insertion sort: at most 64, nearly always in
    /// order already.
    /// </summary>
    private int SortPlanes(ReadOnlySpan<GpuLayerDescriptor> layers, in ExplodedCamera camera)
    {
        var count = 0;
        for (var i = 0; i < layers.Length && count < _planeOrder.Length; i++)
        {
            ref readonly var layer = ref layers[i];
            if ((layer.Flags & GpuLayerDescriptor.FlagVisible) == 0 || layer.Width == 0 || layer.Height == 0)
                continue;

            var key = camera.DistanceTo(camera.DepthOf(layer.ZDepth));
            var at = count++;
            while (at > 0 && _planeKeys[at - 1] < key)
            {
                _planeKeys[at] = _planeKeys[at - 1];
                _planeOrder[at] = _planeOrder[at - 1];
                at--;
            }

            _planeKeys[at] = key;
            _planeOrder[at] = i;
        }

        return count;
    }

    /// <summary>Whether a layer is cut by a mask or a key (its silhouette is what the outline follows).</summary>
    private static bool HasMatte(in GpuLayerDescriptor layer)
    {
        if (layer.MaskResourceId >= 0)
            return true;
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        for (var k = 0; k < count; k++)
        {
            if (layer.Effects[k].Slot is GpuShaderSlot.LayerMask or GpuShaderSlot.ChromaKey)
                return true;
        }

        return false;
    }

    // ----- What is drawn -----

    private void DrawBackdrop(float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Color = new Vector4(0.085f * amount, 0.09f * amount, 0.105f * amount, 1);
        constants.Params3 = new Vector4(0.025f * amount, 0.027f * amount, 0.033f * amount, 1);
        constants.WholeUv();
        Draw("PSExplodedBackdrop", null, _view3D!.Rtv, outputWidth, outputHeight, ref constants, blend: false);
    }

    /// <summary>
    /// The canvas's frame, half a place behind the bottom of the stack (or the lowest plane, when one is lower: a layer
    /// lifted through it): the sequence's background colour (what the flat picture shows where nothing covers it), dimmed
    /// and edged as the view comes out of the flat picture, with its thirds.
    /// </summary>
    private void DrawCanvasFrame(ReadOnlySpan<GpuLayerDescriptor> layers, in ExplodedCamera camera, in FrameSceneHeader header, float amount, int outputWidth, int outputHeight)
    {
        var lowest = 0f;
        foreach (ref readonly var layer in layers)
        {
            if ((layer.Flags & GpuLayerDescriptor.FlagVisible) != 0)
                lowest = Math.Min(lowest, layer.ZDepth);
        }

        var z = camera.DepthOf(lowest - 0.5f);
        var (r, g, b) = (header.BackgroundR, header.BackgroundG, header.BackgroundB);
        var dim = 1 - 0.55f * amount;
        var constants = default(PassConstants);
        constants.Transform = Matrix(camera.PlaneMatrix(0, 0, camera.CanvasWidth, camera.CanvasHeight, 0, 0.5f, 0.5f, z));
        constants.WholeUv();
        constants.Color = new Vector4(r * dim + 0.03f * amount, g * dim + 0.035f * amount, b * dim + 0.045f * amount, 1 - 0.2f * amount);
        constants.Params2 = new Vector4(1.25f, 0.18f, 0, 0);
        constants.Params3 = new Vector4(0.85f, 0.88f, 0.95f, 0.45f * amount);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Plane("PSExplodedFrame", null, ref constants, outputWidth, outputHeight);
    }

    /// <summary>
    /// A layer's plane: the canvas at its depth with its picture (the top left of <paramref name="target"/>, mapped back
    /// onto its part of the canvas; none for an empty plane), and a hairline round the canvas (by how far the view is out of
    /// the flat picture).
    /// </summary>
    private void DrawPlane(GpuTexture? target, in PlaneLayout plane, in ExplodedCamera camera, float z, float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Transform = Matrix(camera.PlaneMatrix(0, 0, camera.CanvasWidth, camera.CanvasHeight, 0, 0.5f, 0.5f, z));
        if (target is not null)
        {
            var (width, height) = ((float)target.Width, (float)target.Height);
            (constants.U0, constants.V0) = (-plane.OriginX / width, -plane.OriginY / height);
            (constants.U1, constants.V1) = ((camera.CanvasWidth * plane.Scale - plane.OriginX) / width, (camera.CanvasHeight * plane.Scale - plane.OriginY) / height);
            constants.Params3 = new Vector4(plane.TexelsX / width, plane.TexelsY / height, 0, 0);
        }
        else
        {
            constants.WholeUv();
            constants.Params3 = new Vector4(-1, -1, 0, 0);
        }

        constants.Color = new Vector4(0.8f, 0.86f, 1f, 0.22f * amount);
        constants.Params2 = new Vector4(1, 0, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        _timer.Begin((int)GpuShaderSlot.TransformComposite);
        Plane("PSExplodedPlane", target, ref constants, outputWidth, outputHeight);
        _timer.End();
    }

    /// <summary>
    /// The neon outline round a plane's silhouette: on its picture's part of the canvas grown by 2 % of the canvas's width
    /// each way, so the glow has room outside it (and only there: the rest of the canvas has no edge to show).
    /// </summary>
    private void DrawOutline(GpuTexture target, in PlaneLayout plane, in ExplodedCamera camera, float z, Vector4 color, float amount, int outputWidth, int outputHeight)
    {
        var scale = Math.Max(plane.Scale, 1e-4f);
        var room = 0.02f * camera.CanvasWidth;
        var (x, y) = (plane.OriginX / scale - room, plane.OriginY / scale - room);
        var (w, h) = (plane.TexelsX / scale + 2 * room, plane.TexelsY / scale + 2 * room);
        var (width, height, texels) = ((float)target.Width, (float)target.Height, room * scale);

        var constants = default(PassConstants);
        constants.Transform = Matrix(camera.PlaneMatrix(x, y, w, h, 0, 0.5f, 0.5f, z));
        (constants.U0, constants.V0) = (-texels / width, -texels / height);
        (constants.U1, constants.V1) = ((plane.TexelsX + texels) / width, (plane.TexelsY + texels) / height);
        constants.Params3 = new Vector4(plane.TexelsX / width, plane.TexelsY / height, 0, 0);
        constants.Color = color with { W = color.W * amount };
        constants.Params2 = new Vector4(1.5f, 4.5f, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Plane("PSExplodedOutline", target, ref constants, outputWidth, outputHeight);
    }

    /// <summary>An adjustment layer: a pane of glass over its box (it has no picture of its own), tinted and edged.</summary>
    private void DrawGlass(in GpuLayerDescriptor layer, in ExplodedCamera camera, float z, float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Transform = Matrix(camera.PlaneMatrix(layer.X, layer.Y, layer.Width, layer.Height, layer.Rotation, layer.AnchorX, layer.AnchorY, z));
        constants.WholeUv();
        constants.Color = new Vector4(0.55f, 0.78f, 1f, 0.08f * amount);
        constants.Params2 = new Vector4(1.25f, 0, 0, 0);
        var selected = (layer.Flags & GpuLayerDescriptor.FlagSelected) != 0;
        constants.Params3 = selected ? SelectedOutline with { W = amount } : new Vector4(0.55f, 0.78f, 1f, 0.55f * amount);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Plane("PSExplodedFrame", null, ref constants, outputWidth, outputHeight);
    }

    /// <summary>The selected layer's box on its plane: an accent frame two and a half pixels wide over a faint tint of it.</summary>
    private void DrawSelectedBox(in GpuLayerDescriptor layer, in ExplodedCamera camera, float z, float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Transform = Matrix(camera.PlaneMatrix(layer.X, layer.Y, layer.Width, layer.Height, layer.Rotation, layer.AnchorX, layer.AnchorY, z));
        constants.WholeUv();
        constants.Color = SelectedOutline with { W = 0.06f * amount };
        constants.Params2 = new Vector4(2.5f, 0, 0, 0);
        constants.Params3 = SelectedOutline with { W = amount };
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Plane("PSExplodedFrame", null, ref constants, outputWidth, outputHeight);
    }

    /// <summary>A quad placed in perspective (VSExploded) over the 3D picture, premultiplied over; the input in t0.</summary>
    private void Plane(string shader, GpuTexture? input, ref PassConstants constants, int outputWidth, int outputHeight)
    {
        if (input is not null)
            constants.Sizes(input.Width, input.Height, outputWidth, outputHeight);
        else
            constants.Sizes(outputWidth, outputHeight, outputWidth, outputHeight);
        Submit("VSExploded", shader, input?.Srv ?? 0, 0, 0, _view3D!.Rtv, outputWidth, outputHeight, ref constants, blend: true, vertices: 4, clipToFrame: false);
    }

    /// <summary>System.Numerics' matrix as the constant buffer's (the same sixteen floats, row by row).</summary>
    private static Matrix4 Matrix(in System.Numerics.Matrix4x4 m) => new()
    {
        M11 = m.M11, M12 = m.M12, M13 = m.M13, M14 = m.M14,
        M21 = m.M21, M22 = m.M22, M23 = m.M23, M24 = m.M24,
        M31 = m.M31, M32 = m.M32, M33 = m.M33, M34 = m.M34,
        M41 = m.M41, M42 = m.M42, M43 = m.M43, M44 = m.M44,
    };

    /// <summary>The 3D path's own textures (SceneRenderer.Dispose, and a while after the view went flat).</summary>
    private void DisposeExploded()
    {
        _view3D?.Dispose();
        _view3D = null;
        _planeTarget?.Dispose();
        _planeTarget = null;
        (_planeTargetOversized, _budgetStep) = (0, 0);
    }
}
