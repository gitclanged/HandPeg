using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- 3D exploded layer stack -----
// FrameSceneHeader.ViewMode 1: each layer is a perspective plane at its stack position (GpuLayerDescriptor.ZDepth x
// FrameSceneHeader.ExplodedZSpread), viewed through ExplodedCamera (shared with UI picking). Each plane's picture is the layer drawn
// alone exactly as the flat path draws it (DrawLayer: source, effects, shadow, blur fill, box, rotation, opacity, transitions) onto
// its own transparent canvas, so a masked HUD element shows only what its mask keeps. Planes are composited back to front
// (premultiplied over, no depth buffer; parallel planes are sorted by distance along their normal) over a backdrop and the canvas
// frame. Masked or keyed layers and the selected clip (GpuLayerDescriptor.FlagSelected) get a neon silhouette outline. Adjustment
// layers render as a glass pane over their box. Blend modes are not applied in this view. The flat path (ViewMode 0) is untouched:
// RenderLayers branches here first.
//
// Resolution: a plane is drawn at its source's native pixels (cropped region, one texel per source pixel, max 4096 per side), never
// coarser than the flat player, so close-ups stay sharp; but no finer than its blurs, shadow and chokes can follow (a pass reaches 64
// texels: RadiusLimit), stepped down in quarter octaves. Only the canvas region the layer can reach is drawn (rotated box plus shadow;
// the whole canvas for blur fill or slides), into the top-left of one persistent plane texture (grown in steps, shrunk after staying
// oversized, released when the view goes flat; blended layers have their own two, also stepped). Planes and working textures share one
// budget (PlaneBudgetBytes): over it, every plane's resolution above fitted steps down a quarter octave at a time, and back up when
// there is room. Native-resolution layers use source-sized working textures regardless of box size, so animated boxes allocate nothing
// per frame.
internal sealed unsafe partial class SceneRenderer
{
    // 3D output picture (output size) and the shared plane texture each layer draws into (top-left region).
    private GpuTexture? _view3D;
    private GpuTexture? _planeTarget;

    // Frames the plane texture has been over twice the needed size; last 3D frame time (Stopwatch) and the texture pool's frame then
    // (anything lent since belongs to the flat path).
    private int _planeTargetOversized;
    private long _explodedAt, _explodedFrame;

    // Plane draw order, sort keys and per-plane layouts (in draw order): allocated once.
    private readonly int[] _planeOrder = new int[CompositorProtocol.MaxLayers];
    private readonly float[] _planeKeys = new float[CompositorProtocol.MaxLayers];
    private readonly PlaneLayout[] _planeLayouts = new PlaneLayout[CompositorProtocol.MaxLayers];

    // Budget step-down level (0 = full resolution).
    private int _budgetStep;

    // Per-layer cached plane pictures (by stack position), redrawn only when their inputs change (KeepPlane), so camera orbits while
    // paused and high-refresh frames between video frames only composite. Cached while the budget allows (LayOutPlanes); the rest draw
    // into the shared plane texture.
    private readonly KeptPlane[] _keptPlanes = new KeptPlane[CompositorProtocol.MaxLayers];

    // 60 fps governor (Govern): scale below the budget's, frames until the next step, last camera and its last move time (Stopwatch), and
    // rolling 3D frame time on GPU and CPU (ms).
    private int _motionStep, _motionHold;
    private (float Yaw, float Pitch, float Zoom, float Spread) _governedCamera;
    private long _cameraMovedAt;
    private float _explodedGpuMs, _explodedCpuMs;

    /// <summary>Governor scales, full resolution first: step down while frames are slow, back up while fast.</summary>
    private static readonly float[] MotionScales = [1f, 0.85f, 0.70f, 0.55f];

    /// <summary>
    /// Step down above SlowFrameMs, back up below FastFrameMs; frames between steps (lets the rolling time settle); time since the last
    /// camera move after which it counts as still (ms).
    /// </summary>
    private const float SlowFrameMs = 14.5f, FastFrameMs = 10f;
    private const int MotionHoldFrames = 10;
    private const double CameraStillMs = 120;

    // Governor clock: GPU time per 3D frame (one disjoint query and two timestamps), read a few frames late without stalling.
    private const int ClockRing = 4;
    private readonly nint[] _clockQueries = new nint[ClockRing * 3];
    private readonly bool[] _clockPending = new bool[ClockRing];
    private int _clockNext;

    /// <summary>Maximum plane picture side, in pixels.</summary>
    private const int MaxPlaneSide = 4096;

    /// <summary>
    /// VRAM budget for planes plus working textures (RGBA16F, 8 bytes per texel): 512 MiB, or 1/16 of dedicated VRAM on 16 GB+ GPUs
    /// (which report slightly less; ~1 GiB on 16 GB, 1.5 GiB on 24 GB).
    /// </summary>
    private long PlaneBudgetBytes => _gpu.DedicatedVideoMemory >= 15L << 30 ? Math.Max(BasePlaneBudget, _gpu.DedicatedVideoMemory / 16) : BasePlaneBudget;

    private const long BasePlaneBudget = 512L << 20;

    /// <summary>Plane texture growth step in texels per side (animated box sizes rarely need a new texture).</summary>
    private const int PlaneTargetStep = 256;

    /// <summary>Last budget step (quarter octave each) before the fitted resolution.</summary>
    private const int LastBudgetStep = 16;

    /// <summary>
    /// Frames the plane texture may stay oversized; seconds 3D textures are kept after the view goes flat (0: released on the first flat
    /// frame so 3D planes never occupy VRAM during 2D editing; the player draws one extra flat frame 0.5 s after exiting,
    /// GpuPreviewEngine.Exploded.cs, so a paused player whose last 3D frame was at the flat camera releases them too).
    /// </summary>
    private const int PlaneTargetPatience = 240;
    internal const double ExplodedTexturesPatience = 0;

    // Outline colours (straight RGB): neon cyan for mask silhouettes, warm amber for the selected clip; amber strength for other clips on
    // the selected clip's track.
    private static readonly Vector4 MaskOutline = new(0.05f, 0.9f, 1f, 0.8f), SelectedOutline = new(1f, 0.72f, 0.18f, 1f);
    private const float SelectedTrackStrength = 0.45f;

    /// <summary>
    /// Plane placement and resolution: reachable canvas region (canvas pixels), layer box size, native-resolution scale (texels per canvas
    /// pixel; 0 = fitted), drawn scale, first texel on the canvas texel grid and size in texels; whether blended, empty (or adjustment
    /// glass), and cached in its own texture.
    /// </summary>
    private struct PlaneLayout
    {
        public float X, Y, W, H, BoxW, BoxH;
        public float Full, Scale;
        public int OriginX, OriginY, TexelsX, TexelsY;
        public bool Blends, Empty, Kept;
    }

    /// <summary>
    /// Cached plane picture: texture, layer as drawn (AsDrawn), other inputs (stamp), and whether used this frame (unused ones are released).
    /// </summary>
    private struct KeptPlane
    {
        public GpuTexture? Texture;
        public GpuLayerDescriptor Drawn;
        public PlaneStamp Stamp;
        public bool Used;
    }

    /// <summary>
    /// Plane picture inputs besides the layer: layout (PlaneLayout), canvas size, decoded frame drawn (a video frame spans many refreshes),
    /// resource table version, and for clock- or audio-driven layers (VariesWithClock) the time and audio bands.
    /// </summary>
    private readonly record struct PlaneStamp(float Scale, int OriginX, int OriginY, int TexelsX, int TexelsY, int CanvasWidth, int CanvasHeight,
        int FrameSource, double FrameTime, nint FrameLuma, int Resources, double Moment, float Bass, float Mid, float High);

    /// <summary>Draws the scene as the 3D exploded layer stack (ViewMode 1) into <paramref name="outputRtv"/>.</summary>
    private void RenderExploded(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, nint outputRtv, int outputWidth, int outputHeight, bool raw)
    {
        var camera = ExplodedCamera.From(header, outputWidth, outputHeight);
        var (canvasWidth, canvasHeight) = (camera.CanvasWidth, camera.CanvasHeight);
        _explodedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // Fitted resolution (canvas as the flat player draws it at no zoom) is each plane's minimum.
        var fit = Math.Min(camera.Fit, (float)MaxPlaneSide / Math.Max(canvasWidth, canvasHeight));
        _letterbox = false;
        _time = header.PlayheadSeconds;

        var timing = (header.Flags & FrameSceneHeader.FlagTimePasses) != 0;
        _timer.BeginFrame(timing);
        _gpu.BindCommon();

        // Governor scale for this frame and its GPU clock around the work (skipped while pass timing has its own disjoint query open).
        Govern(header);
        var clocked = !timing && BeginExplodedClock();

        if (_view3D is null || _view3D.Width != outputWidth || _view3D.Height != outputHeight)
        {
            _view3D?.Dispose();
            _view3D = _gpu.CreateTexture(outputWidth, outputHeight, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        }

        var amount = camera.Amount;
        _gpu.Context.ClearRenderTargetView(_view3D.Rtv, 0, 0, 0, raw ? 0 : 1);
        if (!raw)
            DrawBackdrop(amount, outputWidth, outputHeight);

        // Back to front by distance along the plane normal; ties keep stack order (at zero spread all planes are equidistant and the
        // stack draws like the flat picture).
        var count = SortPlanes(layers, camera);
        LayOutPlanes(layers, count, canvasWidth, canvasHeight, fit);

        // Depth rails: skipped near the flat picture (amount <= 0.01), where there is nothing to see.
        if (!raw && (header.Flags & FrameSceneHeader.FlagDepthRails) != 0 && amount > 0.01f)
            DrawDepthRails(layers, camera, amount, outputWidth, outputHeight);
        DrawCanvasFrame(layers, camera, header, amount, outputWidth, outputHeight);

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

                // Draw the layer alone onto its own cleared plane (temporarily the flat path's canvas). Tilted layers draw flat and the
                // plane is tilted instead (TiltsInDepth). Cached planes redraw only when their inputs change.
                var plane = _planeLayouts[n];
                GpuTexture? target = null;
                if (!plane.Empty)
                {
                    var drawn = layer;
                    if (TiltsInDepth(layer))
                        (drawn.TiltX, drawn.TiltY) = (0, 0);
                    if (plane.Kept)
                    {
                        target = KeepPlane(ref _keptPlanes[_planeOrder[n]], drawn, plane, canvasWidth, canvasHeight);
                    }
                    else
                    {
                        target = plane.Blends ? _gpu.Textures.Rent(plane.TexelsX, plane.TexelsY) : _planeTarget;
                        if (target is not null)
                            DrawAlone(drawn, plane, target, canvasWidth, canvasHeight);
                    }
                }

                // Rebind common state regardless of what the layer's passes left bound (the outline reads the border sampler and shared
                // constants), so no effect state leaks into the outline.
                _gpu.BindCommon();
                DrawPlane(target, plane, layer, camera, z, amount, outputWidth, outputHeight);

                // Amber for the selected clip, fainter amber for another clip on its track playing meanwhile (so the outline never blinks
                // off at a cut), else cyan for a masked or keyed silhouette.
                var selected = (layer.Flags & GpuLayerDescriptor.FlagSelected) != 0;
                var outline = selected ? SelectedOutline
                    : (layer.Flags & GpuLayerDescriptor.FlagSelectedTrack) != 0 ? SelectedOutline with { W = SelectedOutline.W * SelectedTrackStrength }
                    : HasMatte(layer) ? MaskOutline : default;
                if (target is not null && outline.W > 0)
                    DrawOutline(target, plane, layer, camera, z, outline, amount, outputWidth, outputHeight);

                // The selected layer (click, or Ctrl + wheel through the stack) also gets an accent box so the drag target is unambiguous.
                if (selected)
                    DrawSelectedBox(layer, camera, z, amount, outputWidth, outputHeight);
                _gpu.Textures.ReturnAll();
            }
        }
        finally
        {
            // Restore the flat path's canvas (recreated at its own size on its next draw).
            (_canvas, _canvasCopy) = (canvas, canvasCopy);
        }

        SweepKeptPlanes();
        var constants = default(PassConstants);
        constants.Color = new Vector4(0, 0, 0, 1);
        constants.Params4 = new Vector4(1, 1, raw ? 1 : 0, (float)_time);
        constants.WholeUv();
        _timer.Begin(0);
        Draw("PSFinal", _view3D, outputRtv, outputWidth, outputHeight, ref constants, blend: false);
        _timer.End();
        if (clocked)
            EndExplodedClock();
        _explodedCpuMs = Rolling(_explodedCpuMs, (float)System.Diagnostics.Stopwatch.GetElapsedTime(_explodedAt).TotalMilliseconds);
        _gpu.Textures.EndFrame();
        _explodedFrame = _gpu.Textures.Frame;
        _timer.EndFrame();
        ForgetOldHistories();
    }

    /// <summary>
    /// Draws a layer alone into the top-left of <paramref name="target"/>, as the flat path would on a canvas at the plane's scale offset so
    /// the plane's first texel is the target's first (all DrawLayer passes, sizes, pixel lanes, slides and blur fill behave as on the player
    /// canvas).
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

        // Clear only the picture region plus one texel (as far as plane passes read), not the whole texture.
        var clear = default(PassConstants);
        clear.WholeUv();
        Draw("PSSolid", null, target.Rtv, Math.Min(plane.TexelsX + 1, target.Width), Math.Min(plane.TexelsY + 1, target.Height), ref clear, blend: false);

        // Blended layers read the canvas beneath: a copy of their own exactly sized plane (LayOutPlanes).
        _canvas = target;
        _canvasCopy = plane.Blends ? _gpu.Textures.Rent(target.Width, target.Height) : null;
        DrawLayer(layer);

        // A copy DrawLayer created itself (blend not predicted by Blends) is not plane-sized: dispose it.
        if (_canvasCopy is { Rented: false } made)
            made.Dispose();
        _canvasCopy = null;
    }

    // ----- Cached planes -----

    /// <summary>
    /// Returns a cached plane's texture with its picture, redrawn (DrawAlone) only when the layer as drawn (AsDrawn) or other inputs
    /// (PlaneStamp) changed, or the texture was recreated. Blended planes use exactly sized textures (their canvas copy must match: Place);
    /// others reuse theirs while large enough and not far too large (governor down-scales draw into the existing one).
    /// </summary>
    private GpuTexture KeepPlane(ref KeptPlane kept, in GpuLayerDescriptor layer, in PlaneLayout plane, int canvasWidth, int canvasHeight)
    {
        var clock = VariesWithClock(layer);
        var drawn = AsDrawn(layer, clock);
        var stamp = StampOf(layer, plane, canvasWidth, canvasHeight, clock);
        kept.Used = true;

        var (width, height) = plane.Blends ? (plane.TexelsX, plane.TexelsY) : (Stepped(plane.TexelsX), Stepped(plane.TexelsY));
        var fits = kept.Texture is { } texture && (plane.Blends
            ? texture.Width == width && texture.Height == height
            : texture.Width >= plane.TexelsX && texture.Height >= plane.TexelsY && (_motionStep > 0 || (long)texture.Width * texture.Height <= 2L * width * height));
        if (!fits)
        {
            kept.Texture?.Dispose();
            kept.Texture = _gpu.CreateTexture(width, height, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        }
        else if (kept.Stamp == stamp && Same(kept.Drawn, drawn))
        {
            return kept.Texture!;
        }

        DrawAlone(layer, plane, kept.Texture!, canvasWidth, canvasHeight);
        (kept.Drawn, kept.Stamp) = (drawn, stamp);
        return kept.Texture!;
    }

    /// <summary>
    /// The layer as relevant to its plane picture: depth and outline flags cleared (not part of the picture) and, unless clock-driven, media
    /// times cleared (the resolved frame is in PlaneStamp instead, since a video frame spans many refreshes while its time advances).
    /// </summary>
    private static GpuLayerDescriptor AsDrawn(in GpuLayerDescriptor layer, bool clock)
    {
        var drawn = layer;
        drawn.ZDepth = 0;
        drawn.Flags &= ~(GpuLayerDescriptor.FlagSelected | GpuLayerDescriptor.FlagSelectedTrack);
        if (!clock)
            (drawn.MediaSeconds, drawn.PreviousMoment) = (0, 0);
        return drawn;
    }

    /// <summary>Builds the plane's other inputs: layout, canvas, the video frame shown (same as DrawSource gets: DecoderPool.Resolve), resource version, and time/audio for clock-driven layers.</summary>
    private PlaneStamp StampOf(in GpuLayerDescriptor layer, in PlaneLayout plane, int canvasWidth, int canvasHeight, bool clock)
    {
        var frame = layer.SourceKind == (int)GpuSourceKind.Media ? _decoders.Resolve(layer.SourceId, layer.MediaSeconds, layer.SourcePathHash, layer.ClipKey) : null;
        return new PlaneStamp(plane.Scale, plane.OriginX, plane.OriginY, plane.TexelsX, plane.TexelsY, canvasWidth, canvasHeight,
            frame?.Source ?? 0, frame?.Time ?? 0, frame?.LumaSrv ?? 0, _resources.Version,
            clock ? _time : 0, clock ? _audioBass : 0, clock ? _audioMid : 0, clock ? _audioHigh : 0);
    }

    /// <summary>
    /// True when a layer's picture varies with time or audio alone: a shutter (Motion Blur), or an effect whose shader reads the clock,
    /// audio bands or history (Film Grain, temporal Matte Smooth, game-engine effects, particle and social-cut transitions, unknown slots).
    /// Such planes are cached only while time stands still. Color Grade (slot 30) is static (reads only packets and tables, tracked via the
    /// stamp's resource version), so graded planes cache like ungraded ones. Pure; used by goldens.
    /// </summary>
    internal static bool VariesWithClock(in GpuLayerDescriptor layer)
    {
        if ((layer.Flags & (GpuLayerDescriptor.FlagShutterPlace | GpuLayerDescriptor.FlagPreviousMoment)) != 0)
            return true;
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        for (var k = 0; k < count; k++)
        {
            if (layer.Effects[k].Slot is not (GpuShaderSlot.None or (>= GpuShaderSlot.TransformComposite and <= GpuShaderSlot.Vignette)
                or (>= GpuShaderSlot.ChromaKey and <= GpuShaderSlot.MatteChoke) or (>= GpuShaderSlot.LayerMask and <= GpuShaderSlot.Sharpen)
                or GpuShaderSlot.ColorStudio
                or (>= GpuShaderSlot.TransitionDissolve and <= GpuShaderSlot.TransitionBlurDissolve)))
                return true;
        }

        return false;
    }

    /// <summary>Byte-wise equality of two layer descriptors.</summary>
    private static bool Same(in GpuLayerDescriptor a, in GpuLayerDescriptor b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<GpuLayerDescriptor>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<GpuLayerDescriptor>(in b)));

    /// <summary>Releases cached planes unused this frame (layer gone or no longer cached) and resets usage marks for the next frame.</summary>
    private void SweepKeptPlanes()
    {
        for (var i = 0; i < _keptPlanes.Length; i++)
        {
            ref var kept = ref _keptPlanes[i];
            if (!kept.Used && kept.Texture is not null)
            {
                kept.Texture.Dispose();
                kept = default;
            }

            kept.Used = false;
        }
    }

    // ----- 60 fps governor -----

    /// <summary>
    /// Keeps full plane resolution unless the camera moves during playback and the rolling frame time (max of GPU and CPU) exceeds
    /// <see cref="SlowFrameMs"/>: then planes draw one <see cref="MotionScales"/> step coarser (into existing textures), one step finer
    /// again below <see cref="FastFrameMs"/>, and back to full resolution as soon as the camera or playback stops.
    /// </summary>
    private void Govern(in FrameSceneHeader header)
    {
        ReadExplodedClock();
        var camera = (header.CameraYaw, header.CameraPitch, header.CameraZoom, header.ExplodedZSpread);
        if (camera != _governedCamera)
            (_governedCamera, _cameraMovedAt) = (camera, _explodedAt);

        var moving = header.IsPlaying != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_cameraMovedAt, _explodedAt).TotalMilliseconds < CameraStillMs;
        if (!moving)
        {
            (_motionStep, _motionHold) = (0, 0);
            return;
        }

        if (_motionHold > 0)
        {
            _motionHold--;
            return;
        }

        var ms = Math.Max(_explodedGpuMs, _explodedCpuMs);
        if (ms > SlowFrameMs && _motionStep < MotionScales.Length - 1)
            (_motionStep, _motionHold) = (_motionStep + 1, MotionHoldFrames);
        else if (ms < FastFrameMs && _motionStep > 0)
            (_motionStep, _motionHold) = (_motionStep - 1, MotionHoldFrames);
    }

    private static float Rolling(float rolling, float ms) => rolling <= 0 ? ms : rolling + (ms - rolling) * 0.25f;

    /// <summary>Starts the GPU clock around a 3D frame; false while the ring's next entry is still pending (GPU far behind: frame untimed).</summary>
    private bool BeginExplodedClock()
    {
        var at = _clockNext * 3;
        if (_clockPending[_clockNext])
            return false;
        if (_clockQueries[at] == 0)
        {
            _clockQueries[at] = _gpu.Device.CreateQuery(D3D11.QueryTimestampDisjoint);
            _clockQueries[at + 1] = _gpu.Device.CreateQuery(D3D11.QueryTimestamp);
            _clockQueries[at + 2] = _gpu.Device.CreateQuery(D3D11.QueryTimestamp);
        }

        _gpu.Context.Begin(_clockQueries[at]);
        _gpu.Context.End(_clockQueries[at + 1]);
        return true;
    }

    private void EndExplodedClock()
    {
        var at = _clockNext * 3;
        _gpu.Context.End(_clockQueries[at + 2]);
        _gpu.Context.End(_clockQueries[at]);
        _clockPending[_clockNext] = true;
        _clockNext = (_clockNext + 1) % ClockRing;
    }

    /// <summary>Folds completed GPU frame timings into the rolling time; unfinished ones are left for later (never waited on).</summary>
    private void ReadExplodedClock()
    {
        var context = _gpu.Context;
        for (var k = 0; k < ClockRing; k++)
        {
            if (!_clockPending[k])
                continue;
            var at = k * 3;
            TimestampDisjoint disjoint;
            ulong begin, end;
            if (context.GetData(_clockQueries[at], &disjoint, (uint)sizeof(TimestampDisjoint), D3D11.AsyncGetDataDoNotFlush) != 0
                || context.GetData(_clockQueries[at + 1], &begin, sizeof(ulong), D3D11.AsyncGetDataDoNotFlush) != 0
                || context.GetData(_clockQueries[at + 2], &end, sizeof(ulong), D3D11.AsyncGetDataDoNotFlush) != 0)
                continue;

            _clockPending[k] = false;
            if (disjoint.Disjoint == 0 && disjoint.Frequency != 0 && end > begin)
                _explodedGpuMs = Rolling(_explodedGpuMs, (float)((end - begin) * 1000.0 / disjoint.Frequency));
        }
    }

    // ----- Plane resolution -----

    /// <summary>
    /// Computes each plane's canvas region and scale (into <see cref="_planeLayouts"/>, draw order) within the budget, and sizes the plane
    /// texture for the largest.
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

            // WARP (CPU rendering) and blur fill draw at fitted resolution. Otherwise no finer than the layer's radii allow (a blur,
            // shadow or choke clamped at the 64-texel pass reach would look weaker than in the flat picture).
            plane.Full = _gpu.IsWarp || (layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0 || plane.Empty ? 0 : FullScaleOf(layer);
            if (plane.Full > fit)
                plane.Full = WithinRadii(plane.Full, RadiusLimit(layer, canvasWidth), fit);
            plane.Blends = !plane.Empty && Blends(layer);
        }

        // Budget: step down while over it; step back up when the finer step fits with 20 % headroom.
        var step = Math.Clamp(_budgetStep, 0, LastBudgetStep + 1);
        while (step <= LastBudgetStep && BytesAt(count, StepFactor(step), fit) > PlaneBudgetBytes)
            step++;
        while (step > 0 && BytesAt(count, StepFactor(step - 1), fit) <= PlaneBudgetBytes / 5 * 4)
            step--;
        _budgetStep = step;

        // Apply the governor scale below the budget scale (smaller planes fit existing textures). A plane gets its own cached texture
        // while the remaining budget has room (blended planes are already counted); the shared plane texture fits the largest of the rest.
        var factor = StepFactor(step);
        var motion = MotionScales[_motionStep];
        var spare = PlaneBudgetBytes - BytesAt(count, factor, fit);
        var (largestX, largestY) = (0, 0);
        for (var n = 0; n < count; n++)
        {
            ref var plane = ref _planeLayouts[n];
            if (plane.Empty)
                continue;
            Place(ref plane, ScaleAt(plane, factor, fit) * motion);
            var own = plane.Blends ? 0 : 8L * Stepped(plane.TexelsX) * Stepped(plane.TexelsY);
            plane.Kept = own <= spare;
            if (plane.Kept)
                spare -= own;
            else if (!plane.Blends)
                (largestX, largestY) = (Math.Max(largestX, plane.TexelsX), Math.Max(largestY, plane.TexelsY));
        }

        EnsurePlaneTarget(largestX, largestY);
    }

    /// <summary>Fraction of above-fitted resolution kept at a budget step: 2^(-step/4); 0 past the last step.</summary>
    private static float StepFactor(int step) => step > LastBudgetStep ? 0 : MathF.Pow(2, -step / 4f);

    /// <summary>Plane scale at a budget step: fitted plus that fraction of the native-resolution excess, capped by the max side.</summary>
    private static float ScaleAt(in PlaneLayout plane, float factor, float fit)
    {
        var scale = plane.Full > fit ? fit + (plane.Full - fit) * factor : fit;
        return Math.Min(scale, (MaxPlaneSide - 2) / Math.Max(Math.Max(plane.W, plane.H), 1));
    }

    /// <summary>
    /// Plane texels at a scale, aligned to the canvas texel grid (sub-pixel box moves keep texels stable), at least 2 x 2. Blended planes
    /// round up to plane texture steps: their two textures must match exactly (canvas copy) and the pool keeps only reused sizes, so
    /// per-frame region or scale changes (zoom, rotation, slide off edge) rent a few sizes instead of a new pair every frame. The margin is
    /// cleared and lies outside the layer's reach.
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

    /// <summary>Rounds texels up to the next <see cref="PlaneTargetStep"/>, capped at the max side.</summary>
    private static int Stepped(int texels) => Math.Min((texels + PlaneTargetStep - 1) / PlaneTargetStep * PlaneTargetStep, MaxPlaneSide);

    /// <summary>
    /// VRAM bytes at a budget step: the shared plane texture (largest plane), two per blended plane (plane and copy), and two box-sized
    /// working textures per layer (effect ping-pong).
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
    /// Ensures the shared plane texture fits the frame's largest plane: grown in steps (keeping the larger previous side), shrunk after
    /// staying over twice the needed size for <see cref="PlaneTargetPatience"/> frames.
    /// </summary>
    private void EnsurePlaneTarget(int neededX, int neededY)
    {
        if (neededX <= 0 || neededY <= 0)
        {
            // Unused (every plane cached in its own texture): release after the patience period.
            if (_planeTarget is not null && ++_planeTargetOversized >= PlaneTargetPatience)
            {
                _planeTarget.Dispose();
                (_planeTarget, _planeTargetOversized) = (null, 0);
            }

            return;
        }

        var (width, height) = (Stepped(neededX), Stepped(neededY));
        if (_planeTarget is { } target)
        {
            if (target.Width >= neededX && target.Height >= neededY)
            {
                // Never shrink while the governor is down-scaling: planes return to full size shortly.
                var oversized = _motionStep == 0 && (long)target.Width * target.Height > 2L * width * height;
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
    /// Canvas region a layer can draw into (canvas pixels): rotated box plus shadow/rim/shutter reach, clipped to the canvas; the whole
    /// canvas for blur fill or during slide, zoom or spin transitions; empty when fully off-canvas.
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
            if (packet.Slot is GpuShaderSlot.TransitionSlide or GpuShaderSlot.TransitionZoom or GpuShaderSlot.TransitionSpin)
                return (0, 0, canvasWidth, canvasHeight);
            if (packet.Slot == GpuShaderSlot.DropShadow)
                reach = Math.Max(reach, Math.Clamp(packet.P0, 0, 100) + 2 * Math.Clamp(packet.P2, 0, 50) + 2);
            if (packet.Slot == GpuShaderSlot.RimGlass && packet.P7 < 0.5f && !GpuEffectPacket.IsPulsePacket(packet))
                reach = Math.Max(reach, Math.Max(packet.P3, 0) / 2 + Math.Max(packet.P4, 0) * 1.5f + 2);
        }

        // Add the area swept by the shutter.
        if ((layer.Flags & GpuLayerDescriptor.FlagShutterPlace) != 0)
            reach += MathF.Abs(layer.ShutterToX - layer.ShutterFromX) + MathF.Abs(layer.ShutterToY - layer.ShutterFromY) + MathF.Abs(layer.ShutterToWidth - layer.ShutterFromWidth);

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
    /// Native-resolution scale: texels per canvas pixel so one source pixel maps to one texel (finer of horizontal and vertical, over the
    /// cropped region); 1 for solids, patterns, or sources with nothing decoded yet.
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
                // The frame the layer will draw: DrawSource resolves it again, which adds no demand (repeat resolves within a frame are
                // deduplicated; pins are released together at frame end).
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

    /// <summary>Maximum per-pass radius in texels (Effects.hlsl MaxRadius; Blur clamps its boxes to it too).</summary>
    private const float MostPassRadius = 64;

    /// <summary>
    /// Finest scale the layer's radii allow: <see cref="MostPassRadius"/> divided by its widest radius in canvas pixels (Blur box as
    /// RunEffect computes it, shadow softness, key choke/smooth, Matte Choke, spatial Matte Smooth, blur dissolve); unlimited when none.
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
                        // Compute boxes at 16x canvas resolution, then divide by 16 for sub-pixel precision.
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
    /// Clamps a native-resolution scale to the radius limit in quarter-octave steps (so working textures keep their size while the box
    /// animates), never below fitted.
    /// </summary>
    private static float WithinRadii(float full, float limit, float fit)
    {
        if (full <= limit)
            return full;
        var steps = MathF.Ceiling(4 * MathF.Log2(full / limit));
        return Math.Max(full * MathF.Pow(2, -steps / 4), fit);
    }

    /// <summary>True when DrawLayer composites with a blend mode or Frosted Glass (reading a canvas-sized copy of what is beneath), matching its logic.</summary>
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
            if (layer.Effects[k] is { Slot: GpuShaderSlot.RimGlass, P7: > 0.5f })
                return true;
        }

        return mode is > 0 and <= 7;
    }

    /// <summary>
    /// Releases 3D textures on the first flat frame (called by every flat draw): its own, per-layer cached planes (KeptPlane), and pool
    /// working textures unused since the last 3D frame (native-resolution sizes the pool would otherwise keep for 240 more frames, i.e.
    /// forever while paused).
    /// </summary>
    private void ForgetExplodedTextures()
    {
        if ((_planeTarget is not null || _view3D is not null || KeptPlaneTextures() > 0)
            && System.Diagnostics.Stopwatch.GetElapsedTime(_explodedAt).TotalSeconds >= ExplodedTexturesPatience)
        {
            DisposeExploded();
            _gpu.Textures.ReleaseUnusedSince(_explodedFrame);
        }
    }

    /// <summary>
    /// Sorts visible layers into draw order (<see cref="_planeOrder"/>): farthest first by <see cref="ExplodedCamera.DistanceTo"/>, ties
    /// in stack order. Insertion sort: at most 64 items, nearly always already ordered.
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

    /// <summary>True when a layer is masked or keyed (the outline follows its silhouette).</summary>
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

    // ----- Drawing -----

    private void DrawBackdrop(float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Color = new Vector4(0.085f * amount, 0.09f * amount, 0.105f * amount, 1);
        constants.Params3 = new Vector4(0.025f * amount, 0.027f * amount, 0.033f * amount, 1);
        constants.WholeUv();
        Draw("PSExplodedBackdrop", null, _view3D!.Rtv, outputWidth, outputHeight, ref constants, blend: false);
    }

    /// <summary>
    /// Depth rails: four hairlines from the canvas corners running back through the stack to show its depth at a glance. Four quads;
    /// no readback and no plane drawn twice (no reflections or lighting passes).
    /// </summary>
    private void DrawDepthRails(ReadOnlySpan<GpuLayerDescriptor> layers, in ExplodedCamera camera, float amount, int outputWidth, int outputHeight)
    {
        var (near, far) = (0f, 0f);
        foreach (ref readonly var layer in layers)
        {
            if ((layer.Flags & GpuLayerDescriptor.FlagVisible) == 0)
                continue;
            var z = camera.DepthOf(layer.ZDepth);
            (near, far) = (Math.Max(near, z), Math.Min(far, z));
        }

        far -= camera.Spread * 0.5f;
        if (near - far < 1)
            return;

        var guide = default(PassConstants);
        guide.Color = new Vector4(0.62f, 0.72f, 0.9f, 0.22f * amount);
        guide.WholeUv();
        guide.Params4 = new Vector4(1, 1, 0, (float)_time);
        for (var corner = 0; corner < 4; corner++)
        {
            var (x, y) = ((corner & 1) * (float)camera.CanvasWidth, (corner >> 1) * (float)camera.CanvasHeight);
            var from = new System.Numerics.Vector3(x, y, far - camera.Spread * 0.5f);
            var to = new System.Numerics.Vector3(x, y, near + camera.Spread * 0.5f);
            guide.Transform = Matrix(camera.LineMatrix(from, to, 2.5f));
            Plane("PSExplodedLine", null, ref guide, outputWidth, outputHeight);
        }
    }

    /// <summary>
    /// Canvas frame half a step behind the stack bottom (or the lowest plane if lower): the sequence background colour, dimmed and edged
    /// as the view leaves the flat picture, with rule-of-thirds guides.
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
    /// Draws a layer's plane: the canvas at its depth with its picture (top-left of <paramref name="target"/> mapped back onto its canvas
    /// region; none when empty) and a canvas hairline scaled by the exploded amount.
    /// </summary>
    private void DrawPlane(GpuTexture? target, in PlaneLayout plane, in GpuLayerDescriptor layer, in ExplodedCamera camera, float z, float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Transform = Matrix(OnPlane(camera, layer, 0, 0, camera.CanvasWidth, camera.CanvasHeight, z));
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
    /// Neon silhouette outline over the picture region grown by 2 % of canvas width on each side, giving the glow room only where edges
    /// exist.
    /// </summary>
    private void DrawOutline(GpuTexture target, in PlaneLayout plane, in GpuLayerDescriptor layer, in ExplodedCamera camera, float z, Vector4 color, float amount,
        int outputWidth, int outputHeight)
    {
        var scale = Math.Max(plane.Scale, 1e-4f);
        var room = 0.02f * camera.CanvasWidth;
        var (x, y) = (plane.OriginX / scale - room, plane.OriginY / scale - room);
        var (w, h) = (plane.TexelsX / scale + 2 * room, plane.TexelsY / scale + 2 * room);
        var (width, height, texels) = ((float)target.Width, (float)target.Height, room * scale);

        var constants = default(PassConstants);
        constants.Transform = Matrix(OnPlane(camera, layer, x, y, w, h, z));
        (constants.U0, constants.V0) = (-texels / width, -texels / height);
        (constants.U1, constants.V1) = ((plane.TexelsX + texels) / width, (plane.TexelsY + texels) / height);
        constants.Params3 = new Vector4(plane.TexelsX / width, plane.TexelsY / height, 0, 0);
        constants.Color = color with { W = color.W * amount };
        constants.Params2 = new Vector4(1.5f, 4.5f, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Plane("PSExplodedOutline", target, ref constants, outputWidth, outputHeight);
    }

    /// <summary>Adjustment layer as a tinted, edged glass pane over its box (no picture of its own).</summary>
    private void DrawGlass(in GpuLayerDescriptor layer, in ExplodedCamera camera, float z, float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Transform = Matrix(BoxOnPlane(camera, layer, z));
        constants.WholeUv();
        constants.Color = new Vector4(0.55f, 0.78f, 1f, 0.08f * amount);
        constants.Params2 = new Vector4(1.25f, 0, 0, 0);
        var selected = (layer.Flags & GpuLayerDescriptor.FlagSelected) != 0;
        constants.Params3 = selected ? SelectedOutline with { W = amount } : new Vector4(0.55f, 0.78f, 1f, 0.55f * amount);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Plane("PSExplodedFrame", null, ref constants, outputWidth, outputHeight);
    }

    /// <summary>Selected layer box on its plane: a 2.5 px accent frame over a faint accent tint.</summary>
    private void DrawSelectedBox(in GpuLayerDescriptor layer, in ExplodedCamera camera, float z, float amount, int outputWidth, int outputHeight)
    {
        var constants = default(PassConstants);
        constants.Transform = Matrix(BoxOnPlane(camera, layer, z));
        constants.WholeUv();
        constants.Color = SelectedOutline with { W = 0.06f * amount };
        constants.Params2 = new Vector4(2.5f, 0, 0, 0);
        constants.Params3 = SelectedOutline with { W = amount };
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Plane("PSExplodedFrame", null, ref constants, outputWidth, outputHeight);
    }

    /// <summary>
    /// True when a layer's plane tilts out of the stack: tilted layers (Pitch/Yaw, GpuLayerDescriptor.TiltX/TiltY) draw flat onto their
    /// plane and the plane is tilted about the layer anchor, viewed by the 3D camera. Blur fill covers the frame regardless, so such layers
    /// keep flat perspective on an upright plane.
    /// </summary>
    internal static bool TiltsInDepth(in GpuLayerDescriptor layer) =>
        (layer.TiltX != 0 || layer.TiltY != 0) && (layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) == 0 && layer.BlurFillRadius <= 0;

    /// <summary>Matrix for a canvas region (canvas pixels) at a plane's depth: upright, or tilted with the layer's plane about its anchor.</summary>
    private static System.Numerics.Matrix4x4 OnPlane(in ExplodedCamera camera, in GpuLayerDescriptor layer, float x, float y, float w, float h, float z)
    {
        if (!TiltsInDepth(layer) || w == 0 || h == 0)
            return camera.PlaneMatrix(x, y, w, h, 0, 0.5f, 0.5f, z);

        var (pivotX, pivotY) = (layer.X + layer.AnchorX * layer.Width, layer.Y + layer.AnchorY * layer.Height);
        return camera.PlaneMatrix(x, y, w, h, 0, (pivotX - x) / w, (pivotY - y) / h, z, layer.TiltX, layer.TiltY);
    }

    /// <summary>Matrix for the layer's box on its plane, rotated about its anchor and tilted with the plane when applicable (<see cref="TiltsInDepth"/>).</summary>
    private static System.Numerics.Matrix4x4 BoxOnPlane(in ExplodedCamera camera, in GpuLayerDescriptor layer, float z) => TiltsInDepth(layer)
        ? camera.PlaneMatrix(layer.X, layer.Y, layer.Width, layer.Height, layer.Rotation, layer.AnchorX, layer.AnchorY, z, layer.TiltX, layer.TiltY)
        : camera.PlaneMatrix(layer.X, layer.Y, layer.Width, layer.Height, layer.Rotation, layer.AnchorX, layer.AnchorY, z);

    /// <summary>Draws a perspective quad (VSExploded) over the 3D picture, premultiplied over; input in t0.</summary>
    private void Plane(string shader, GpuTexture? input, ref PassConstants constants, int outputWidth, int outputHeight)
    {
        if (input is not null)
            constants.Sizes(input.Width, input.Height, outputWidth, outputHeight);
        else
            constants.Sizes(outputWidth, outputHeight, outputWidth, outputHeight);
        Submit("VSExploded", shader, input?.Srv ?? 0, 0, 0, _view3D!.Rtv, outputWidth, outputHeight, ref constants, blend: true, vertices: 4, clipToFrame: false);
    }

    /// <summary>Converts a System.Numerics matrix to the constant-buffer layout (same 16 floats, row-major).</summary>
    private static Matrix4 Matrix(in System.Numerics.Matrix4x4 m) => new()
    {
        M11 = m.M11, M12 = m.M12, M13 = m.M13, M14 = m.M14,
        M21 = m.M21, M22 = m.M22, M23 = m.M23, M24 = m.M24,
        M31 = m.M31, M32 = m.M32, M33 = m.M33, M34 = m.M34,
        M41 = m.M41, M42 = m.M42, M43 = m.M43, M44 = m.M44,
    };

    /// <summary>Releases all 3D-path resources (SceneRenderer.Dispose, and ForgetExplodedTextures on the first flat frame).</summary>
    private void DisposeExploded()
    {
        ReleaseExplodedTextures();
        _budgetStep = 0;
        for (var i = 0; i < _clockQueries.Length; i++)
            Com.Release(ref _clockQueries[i]);
        Array.Clear(_clockPending);
        (_clockNext, _motionStep, _motionHold, _explodedGpuMs, _explodedCpuMs) = (0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Releases the 3D picture, plane texture and cached planes (worker trim: SceneRenderer.ReleaseIdleTextures); recreated at needed sizes
    /// on the next 3D frame (RenderExploded, EnsurePlaneTarget). The budget step is kept: its 20 % hysteresis (LayOutPlanes) means resetting
    /// to 0 could draw planes a step finer than the previous frame.
    /// </summary>
    private void ReleaseExplodedTextures()
    {
        _view3D?.Dispose();
        _view3D = null;
        _planeTarget?.Dispose();
        _planeTarget = null;
        _planeTargetOversized = 0;
        foreach (ref var kept in _keptPlanes.AsSpan())
        {
            kept.Texture?.Dispose();
            kept = default;
        }
    }

    /// <summary>Number of planes cached in their own textures (goldens: IdleTexturesForTests).</summary>
    private int KeptPlaneTextures()
    {
        var count = 0;
        foreach (ref readonly var kept in _keptPlanes.AsSpan())
            count += kept.Texture is null ? 0 : 1;
        return count;
    }
}
