using HandPegApp.Compositor.Native;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Edge magnifier loupe -----
// While an edge-shaping slider is held (feather, mask grow/offset, matte choke/smooth, chroma key), the client names the layer
// (FrameSceneHeader.LoupeClipKey) and the player draws that layer alone in a corner at LoupeZoom (400 %) x the player scale around an
// edge point, over a checkerboard:
// - Re-rendered at that scale (source resampled, mask and effects evaluated there), not upscaled player pixels, so sub-pixel edge steps
//   show. Up to 4096 px for the whole layer; larger layers render below 400 %.
// - Edge point: from a small isolated render (no shadow or blur fill), the edge nearest the box's top-centre (PSLoupeFind: soft pixel or
//   alpha-0.5 crossing), read back a frame later via a two-slot staging ring (never stalls) and smoothed; falls back to the box's top-left.
// - Placed in the picture corner farthest from the point, so it never covers what it magnifies.
// - Export Quality Preview: PrepareLoupe locates the point outside the output's keyed mutex, the compare pass point-samples the encoded
//   picture (or its x10 difference) into the view, and CompositeLoupe composites it under the mutex. The encoded frame never holds a loupe.
// - Preview Magnifier: with no edge tuned, PrepareLoupeAt centres the loupe on the pointer (FrameSceneHeader.LoopbackLoupeX/Y) with the
//   same size and corner rules (LoupeSide, LoupeCorner), drawing and scanning no layer.
internal sealed unsafe partial class SceneRenderer
{
    private GpuTexture? _loupeScan, _loupeView, _loupeFound;
    private readonly nint[] _loupeStaging = new nint[2];
    private readonly bool[] _loupePending = new bool[2];
    private int _loupeTurn;
    private long _loupeKey;
    private bool _loupeHasPoint;
    private (float X, float Y) _loupePoint;

    // Isolated layer drawing (DrawLayer without blur fill, shadow, opacity, blend or transition) and its alpha-history key salt (TemporalMatte).
    private bool _alone;
    private long _historySalt;

    /// <summary>Scan render's longest side in px, and its search grid.</summary>
    private const int LoupeScanSide = 192, LoupeGrid = 64;

    /// <summary>Maximum side the layer renders at for the loupe.</summary>
    private const int LoupeMostSide = 4096;

    // Loupe prepared this frame (PrepareLoupe) awaiting CompositeLoupe: corner and side on the output, the picture rect it is clamped to,
    // and the focus point with its render scale.
    private bool _loupeReady;
    private (float X, float Y, int Side) _loupePlace;
    private (float X, float Y, float W, float H) _loupePicture;
    private (float X, float Y, float Scale) _loupeFocus;

    /// <summary>
    /// The prepared loupe view (Export Quality Preview): render target and side, canvas focus point, and canvas-to-view scale. The compare
    /// pass draws the encoded picture into it (ExportLoopbackPreview.DrawLoupeView) instead of the layer.
    /// </summary>
    internal (nint Rtv, int Side, float X, float Y, float Scale)? LoupeFocus =>
        _loupeReady && _loupeView is { } view ? (view.Rtv, _loupePlace.Side, _loupeFocus.X, _loupeFocus.Y, _loupeFocus.Scale) : null;

    /// <summary>
    /// Prepares the loupe for an <paramref name="outputWidth"/> x <paramref name="outputHeight"/> player picture placed by <see cref="Letterbox"/>:
    /// finds the named layer, its edge point (scan) and the loupe corner (farthest from the point); with <paramref name="drawLayer"/>, renders
    /// the isolated layer at LoupeZoom into the view. False when the layer is not in the scene. Restores the flat path's frame state.
    /// </summary>
    internal bool PrepareLoupe(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, int outputWidth, int outputHeight, bool drawLayer = true)
    {
        _loupeReady = false;

        // The clip's own picture (not its blur fill, not an adjustment layer).
        var index = -1;
        for (var i = 0; i < layers.Length; i++)
        {
            ref readonly var candidate = ref layers[i];
            if (candidate.ClipKey == header.LoupeClipKey && (candidate.Flags & GpuLayerDescriptor.FlagVisible) != 0 && candidate.Width != 0 && candidate.Height != 0
                && (candidate.Flags & (GpuLayerDescriptor.FlagBlurFillOnly | GpuLayerDescriptor.FlagAdjustment)) == 0 && !IsCompareBefore(candidate))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return false;
        ref readonly var layer = ref layers[index];
        if (_loupeKey != header.LoupeClipKey)
            (_loupeKey, _loupeHasPoint) = (header.LoupeClipKey, false);

        // Picture rect on the output, placed as the flat path and the compare pass place it.
        var canvasWidth = Math.Max(header.CanvasWidth, 1);
        var (pictureX, pictureY, pictureScale) = Letterbox(header, outputWidth, outputHeight);
        var pictureRect = (X: pictureX, Y: pictureY, W: canvasWidth * pictureScale, H: Math.Max(header.CanvasHeight, 1) * pictureScale);

        // Save the flat frame state to restore afterwards.
        var saved = (_canvas, _canvasCopy, _outWidth, _outHeight, _scaleX, _scaleY, _offsetX, _offsetY, _letterbox, _frameRect, _pxScale1080, _pixelScale, _time);
        var side = 0;
        var scale = 0f;
        var (pointX, pointY) = (0f, 0f);
        try
        {
            _gpu.BindCommon();
            _time = header.PlayheadSeconds;
            _alone = true;
            TakeLoupePoint(layer);
            ScanLoupeEdge(layer, canvasWidth);

            // Focus: the found edge, else near the box's top-left (a feathered rectangle's softest area).
            (pointX, pointY) = _loupeHasPoint ? _loupePoint : (layer.X + layer.Width * 0.04f, layer.Y + layer.Height * 0.04f);

            // Loupe side ~1/3 of the picture's shorter side; layer rendered at LoupeZoom (default 400 %) of the player scale.
            side = LoupeSide(pictureRect);
            var zoom = header.LoupeZoom > 0 ? header.LoupeZoom : 4;
            scale = pictureScale * zoom;
            var view = Target(ref _loupeView, side, side);
            if (drawLayer)
            {
                scale = Math.Min(scale, LoupeMostSide / Math.Max(Math.Max(MathF.Abs(layer.Width), MathF.Abs(layer.Height)), 1));
                Frame(scale, side / 2f - pointX * scale, side / 2f - pointY * scale, side, side, canvasWidth, view);
                _historySalt = 0x4C4F555045; // "LOUPE"
                DrawLayer(layer);
                _gpu.Textures.ReturnAll();
            }
        }
        finally
        {
            _alone = false;
            _historySalt = 0;
            (_canvas, _canvasCopy, _outWidth, _outHeight, _scaleX, _scaleY, _offsetX, _offsetY, _letterbox, _frameRect, _pxScale1080, _pixelScale, _time) = saved;
        }

        // Place in the picture corner farthest from the point, inside the output.
        var (shownX, shownY) = (_loupeHasPoint ? _loupePoint.X : layer.X, _loupeHasPoint ? _loupePoint.Y : layer.Y);
        var (x, y) = LoupeCorner(pictureRect, outputWidth, outputHeight, side, shownX * pictureScale + pictureX, shownY * pictureScale + pictureY);
        (_loupePlace, _loupePicture, _loupeFocus, _loupeReady) = ((x, y, side), pictureRect, (pointX, pointY, scale), true);
        return true;
    }

    /// <summary>
    /// True for the grade compare's Before half (GpuScenePlan.WriteSplit writes it just before the graded half of the same clip): a compare
    /// wipe (lane 5 > 0, hard edge; real wipes use 0) keeping the left side (lane 4). Split layers hold no other wipe (GpuScenePlan.Splittable).
    /// Pure (goldens).
    /// </summary>
    internal static bool IsCompareBefore(in GpuLayerDescriptor layer)
    {
        var count = Math.Clamp(layer.EffectCount, 0, CompositorProtocol.MaxEffectsPerLayer);
        for (var k = 0; k < count; k++)
        {
            ref readonly var packet = ref layer.Effects[k];
            if (packet.Slot == GpuShaderSlot.TransitionWipe && packet.P5 > 0 && packet.P4 > 0.5f)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Prepares the Export Quality Preview Magnifier (<see cref="FrameSceneHeader.FlagLoopbackLoupe"/>) around an output point (the pointer,
    /// in output px): only the view and its placement (<see cref="LoupeAt"/>), no layer draw or edge scan; the compare pass draws into it
    /// (ExportLoopbackPreview.DrawLoupeView). False off the picture.
    /// </summary>
    internal bool PrepareLoupeAt(in FrameSceneHeader header, float outputX, float outputY, int outputWidth, int outputHeight)
    {
        _loupeReady = false;
        if (LoupeAt(header, outputX, outputY, outputWidth, outputHeight) is not { } plan)
            return false;

        Target(ref _loupeView, plan.Side, plan.Side);
        (_loupePlace, _loupePicture, _loupeFocus, _loupeReady) = ((plan.X, plan.Y, plan.Side), plan.Picture, (plan.PointX, plan.PointY, plan.Scale), true);
        return true;
    }

    /// <summary>
    /// Magnifier loupe around an output point: side and corner (same rules as the edge loupe: <see cref="LoupeSide"/>, <see cref="LoupeCorner"/>),
    /// the canvas point under it, view px per canvas px (player scale x LoupeZoom) and the picture rect. Null off the picture. Pure (goldens).
    /// </summary>
    internal static (int Side, float X, float Y, float PointX, float PointY, float Scale, (float X, float Y, float W, float H) Picture)? LoupeAt(
        in FrameSceneHeader header, float outputX, float outputY, int outputWidth, int outputHeight)
    {
        if (outputWidth <= 0 || outputHeight <= 0 || !float.IsFinite(outputX) || !float.IsFinite(outputY))
            return null;
        var (pictureX, pictureY, pictureScale) = Letterbox(header, outputWidth, outputHeight);
        if (!(pictureScale > 0))
            return null;
        var picture = (X: pictureX, Y: pictureY, W: Math.Max(header.CanvasWidth, 1) * pictureScale, H: Math.Max(header.CanvasHeight, 1) * pictureScale);
        if (outputX < Math.Max(picture.X, 0) || outputX >= Math.Min(picture.X + picture.W, outputWidth)
            || outputY < Math.Max(picture.Y, 0) || outputY >= Math.Min(picture.Y + picture.H, outputHeight))
            return null;

        var side = LoupeSide(picture);
        var zoom = header.LoupeZoom > 0 ? header.LoupeZoom : 4;
        var (x, y) = LoupeCorner(picture, outputWidth, outputHeight, side, outputX, outputY);
        return (side, x, y, (outputX - pictureX) / pictureScale, (outputY - pictureY) / pictureScale, pictureScale * zoom, picture);
    }

    /// <summary>Loupe side for a picture rect: 36 % of its shorter side, clamped to 120-640 px.</summary>
    internal static int LoupeSide((float X, float Y, float W, float H) picture) =>
        (int)Math.Clamp(MathF.Round(Math.Min(picture.W, picture.H) * 0.36f), 120, 640);

    /// <summary>
    /// Loupe position: the picture corner (within <see cref="LoupeBounds"/>) farthest from the output point it magnifies
    /// (<paramref name="atX"/>, <paramref name="atY"/>), so it never covers it.
    /// </summary>
    internal static (float X, float Y) LoupeCorner((float X, float Y, float W, float H) picture, int outputWidth, int outputHeight, int side, float atX, float atY)
    {
        var (left, right, top, bottom) = LoupeBounds(picture, outputWidth, outputHeight, side);
        return (atX < (left + right + side) / 2 ? right : left, atY < (top + bottom + side) / 2 ? bottom : top);
    }

    /// <summary>
    /// Allowed loupe positions on a picture: 12 px inside its edges and within the output (left/top = near corner, right/bottom = far
    /// corner). Shared by placement and goldens.
    /// </summary>
    internal static (float Left, float Right, float Top, float Bottom) LoupeBounds((float X, float Y, float W, float H) picture, int outputWidth, int outputHeight, int side) =>
        (Math.Max(picture.X, 0) + 12, Math.Min(picture.X + picture.W, outputWidth) - 12 - side,
         Math.Max(picture.Y, 0) + 12, Math.Min(picture.Y + picture.H, outputHeight) - 12 - side);

    /// <summary>
    /// Composites the prepared loupe onto <paramref name="targetRtv"/> (the output PrepareLoupe was given): view over a checkerboard, frame and
    /// cross hairs, clamped to the picture, in one quad (the Export Quality Preview calls it under the keyed mutex). Consumes the loupe.
    /// </summary>
    internal void CompositeLoupe(nint targetRtv, int outputWidth, int outputHeight)
    {
        if (!_loupeReady || _loupeView is not { } view)
            return;
        _loupeReady = false;

        var (x, y, side) = _loupePlace;
        var saved = (_outWidth, _outHeight, _letterbox, _frameRect);
        try
        {
            // Quad frame of reference: this output, clamped to the picture like a letterboxed layer.
            (_outWidth, _outHeight, _letterbox, _frameRect) = (outputWidth, outputHeight, true, _loupePicture);
            var constants = default(PassConstants);
            constants.Transform = Matrix4.Box(x, y, side, side, 0, 0.5f, 0.5f, _outWidth, _outHeight);
            constants.WholeUv();
            constants.Color = new Vector4(1f, 0.72f, 0.18f, 1);
            constants.Params2 = new Vector4(MathF.Max(MathF.Round(side / 32f), 6), 2, 0, 0.85f);
            constants.Params3 = new Vector4(side, side, 0, 0);
            constants.Params4 = new Vector4(1, 1, 0, (float)_time);
            Quad("PSLoupeView", view, targetRtv, ref constants, blend: false);
        }
        finally
        {
            (_outWidth, _outHeight, _letterbox, _frameRect) = saved;
        }
    }

    /// <summary>Sets the flat path's frame state to draw into <paramref name="target"/> at a scale and offset (canvas px to target px).</summary>
    private void Frame(float scale, float offsetX, float offsetY, int width, int height, int canvasWidth, GpuTexture target)
    {
        (_outWidth, _outHeight) = (width, height);
        (_scaleX, _scaleY, _offsetX, _offsetY) = (scale, scale, offsetX, offsetY);
        _letterbox = false;
        _frameRect = (0, 0, width, height);
        _pxScale1080 = canvasWidth * scale / 1080f;
        _pixelScale = scale;
        (_canvas, _canvasCopy) = (target, null);
        _gpu.Context.ClearRenderTargetView(target.Rtv, 0, 0, 0, 0);
    }

    /// <summary>
    /// Edge search: renders the isolated layer small, runs PSLoupeFind on the GPU and copies the one-pixel result to a staging texture that
    /// TakeLoupePoint reads a frame or two later.
    /// </summary>
    private void ScanLoupeEdge(in GpuLayerDescriptor layer, int canvasWidth)
    {
        var (width, height) = (MathF.Abs(layer.Width), MathF.Abs(layer.Height));
        var scale = LoupeScanSide / Math.Max(Math.Max(width, height), 1);
        var (scanWidth, scanHeight) = (Math.Max((int)MathF.Round(width * scale), 8), Math.Max((int)MathF.Round(height * scale), 8));
        var scan = Target(ref _loupeScan, scanWidth, scanHeight);
        Frame(scale, -Math.Min(layer.X, layer.X + layer.Width) * scale, -Math.Min(layer.Y, layer.Y + layer.Height) * scale, scanWidth, scanHeight, canvasWidth, scan);
        _historySalt = 0x5343414E; // "SCAN"
        DrawLayer(layer);
        _gpu.Textures.ReturnAll();

        var turn = _loupeTurn;
        if (_loupePending[turn])
            return; // previous result not read yet: skip this frame's search
        _loupeFound ??= _gpu.CreateTexture(1, 1, DxgiFormat.R32G32B32A32Float, renderTarget: true);
        var constants = default(PassConstants);
        constants.WholeUv();
        constants.Params2 = new Vector4(0.5f, 0, LoupeGrid, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Draw("PSLoupeFind", scan, _loupeFound.Rtv, 1, 1, ref constants, blend: false);

        if (_loupeStaging[turn] == 0)
            _loupeStaging[turn] = _gpu.CreateStaging(1, 1, DxgiFormat.R32G32B32A32Float);
        _gpu.Context.CopyResource(_loupeStaging[turn], _loupeFound.Texture);
        _loupePending[turn] = true;
        _loupeTurn = 1 - turn;
    }

    /// <summary>
    /// Reads a pending search result without waiting: the canvas edge point, smoothed (jumps when far away or first found).
    /// </summary>
    private void TakeLoupePoint(in GpuLayerDescriptor layer)
    {
        for (var k = 0; k < 2; k++)
        {
            var turn = (_loupeTurn + k) % 2;
            if (!_loupePending[turn] || _loupeStaging[turn] == 0)
                continue;
            var hr = _gpu.Context.Map(_loupeStaging[turn], 0, D3D11.MapRead, D3D11.MapFlagDoNotWait, out var mapped);
            if (hr == Com.WasStillDrawing)
                continue;
            _loupePending[turn] = false;
            if (hr < 0)
                continue;

            var found = *(Vector4*)mapped.Data;
            _gpu.Context.Unmap(_loupeStaging[turn], 0);
            if (found.Z < 0.5f || !float.IsFinite(found.X) || !float.IsFinite(found.Y))
                continue;

            var (left, top) = (Math.Min(layer.X, layer.X + layer.Width), Math.Min(layer.Y, layer.Y + layer.Height));
            var point = (X: left + found.X * MathF.Abs(layer.Width), Y: top + found.Y * MathF.Abs(layer.Height));
            var far = Math.Max(MathF.Abs(layer.Width), MathF.Abs(layer.Height)) * 0.15f;
            if (!_loupeHasPoint || MathF.Abs(point.X - _loupePoint.X) + MathF.Abs(point.Y - _loupePoint.Y) > far)
                _loupePoint = point;
            else
                _loupePoint = (_loupePoint.X + (point.X - _loupePoint.X) * 0.5f, _loupePoint.Y + (point.Y - _loupePoint.Y) * 0.5f);
            _loupeHasPoint = true;
        }
    }

    private GpuTexture Target(ref GpuTexture? texture, int width, int height)
    {
        if (texture is null || texture.Width != width || texture.Height != height)
        {
            texture?.Dispose();
            texture = _gpu.CreateTexture(width, height, DxgiFormat.R16G16B16A16Float, renderTarget: true);
        }

        return texture;
    }

    private void DisposeLoupe()
    {
        _loupeScan?.Dispose();
        _loupeView?.Dispose();
        _loupeFound?.Dispose();
        (_loupeScan, _loupeView, _loupeFound) = (null, null, null);
        for (var k = 0; k < 2; k++)
        {
            Com.Release(ref _loupeStaging[k]);
            _loupePending[k] = false;
        }
    }
}
