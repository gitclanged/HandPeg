using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- The magnifier loupe (Pass 5.0-Verify, the user's bug 6) -----
// While a slider that shapes a layer's edge is held (a feather, a mask's grow or offset, a matte's choke or smooth, a chroma
// key), the client names the layer (FrameSceneHeader.LoupeClipKey) and the player draws, in a corner of the picture, the
// layer alone at LoupeZoom (400 %) times the player's own scale round a point of its edge, over a checkerboard:
// - Not the player's pixels enlarged: the layer is drawn again at that scale (its source resampled, its mask and effects
//   run there, a feather's radius as large as it then is), so every sub-pixel step of its edge shows. Up to 4096 pixels
//   across for the whole layer; a larger one is drawn at less than 400 %.
// - The point: on a small picture of the layer alone (its shadow and blurred background left out, so only its own edge
//   counts), the edge nearest its box's top middle (PSLoupeFind: a soft pixel, or alpha crossing one half). The answer is
//   read back a frame later without waiting for the card (a staging ring of two), and followed smoothly; until one
//   arrives (or with no edge: a feathered rectangle's soft edge is its corner), the box's top left corner is looked at.
// - The corner of the picture farthest from that point holds the loupe, so it never covers what it magnifies.
// - Over the Export Quality Preview (5.4C): PrepareLoupe finds the point outside the output's keyed mutex, the compare pass draws the
//   encoded picture (or its difference x10) into the view around that point with point sampling, so compression's blocks are what is
//   magnified, and CompositeLoupe lays it on under the mutex after the compare. The encoded frame itself never holds a loupe.
// - The preview's Magnifier (5.4D): with no edge tuned, PrepareLoupeAt puts the same loupe round the point under the pointer
//   (FrameSceneHeader.LoopbackLoupeX/Y), with the same side and corner rule (LoupeSide, LoupeCorner), and no layer drawn or scanned.
internal sealed unsafe partial class SceneRenderer
{
    private GpuTexture? _loupeScan, _loupeView, _loupeFound;
    private readonly nint[] _loupeStaging = new nint[2];
    private readonly bool[] _loupePending = new bool[2];
    private int _loupeTurn;
    private long _loupeKey;
    private bool _loupeHasPoint;
    private (float X, float Y) _loupePoint;

    // Drawing a layer alone (DrawLayer: no blurred background, shadow, opacity, blend or transition), and the alpha
    // history key it keeps then (TemporalMatte).
    private bool _alone;
    private long _historySalt;

    /// <summary>The scan's picture of the layer, its longest side in pixels; the grid it is searched on.</summary>
    private const int LoupeScanSide = 192, LoupeGrid = 64;

    /// <summary>The largest side the layer is drawn at for the loupe.</summary>
    private const int LoupeMostSide = 4096;

    // The loupe prepared for this frame (PrepareLoupe), waiting to be laid on a target (CompositeLoupe): its corner and side on the
    // output, the picture's rectangle there (what it is kept within), and the point it looks at with the scale it is drawn at.
    private bool _loupeReady;
    private (float X, float Y, int Side) _loupePlace;
    private (float X, float Y, float W, float H) _loupePicture;
    private (float X, float Y, float Scale) _loupeFocus;

    /// <summary>
    /// The loupe's view and point when one is prepared (5.4C, the Export Quality Preview's loupe): the view's render target and side,
    /// the point on the canvas it is centred on, and canvas pixels to view pixels. The compare pass draws the encoded picture into it
    /// (ExportLoopbackPreview.DrawLoupeView) in place of the layer.
    /// </summary>
    internal (nint Rtv, int Side, float X, float Y, float Scale)? LoupeFocus =>
        _loupeReady && _loupeView is { } view ? (view.Rtv, _loupePlace.Side, _loupeFocus.X, _loupeFocus.Y, _loupeFocus.Scale) : null;

    /// <summary>
    /// Prepares the loupe for a player picture of <paramref name="outputWidth"/> x <paramref name="outputHeight"/> placed as
    /// <see cref="Letterbox"/> places it (5.4C: split from the old DrawLoupe, so the Export Quality Preview can lay it over its compare):
    /// the layer the header names, its edge point (the scan), and where the loupe goes (the corner farthest from the point); with
    /// <paramref name="drawLayer"/>, the layer alone drawn at LoupeZoom into the loupe's view. False (nothing prepared) when the layer
    /// is not on the scene. The flat path's frame of reference is put back as it was.
    /// </summary>
    internal bool PrepareLoupe(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, int outputWidth, int outputHeight, bool drawLayer = true)
    {
        _loupeReady = false;

        // The layer: the clip's own picture (not its blurred background, not a pane of glass).
        var index = -1;
        for (var i = 0; i < layers.Length; i++)
        {
            ref readonly var candidate = ref layers[i];
            if (candidate.ClipKey == header.LoupeClipKey && (candidate.Flags & GpuLayerDescriptor.FlagVisible) != 0 && candidate.Width != 0 && candidate.Height != 0
                && (candidate.Flags & (GpuLayerDescriptor.FlagBlurFillOnly | GpuLayerDescriptor.FlagAdjustment)) == 0)
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

        // The player's picture on the output, as the flat path and the compare pass both place it.
        var canvasWidth = Math.Max(header.CanvasWidth, 1);
        var (pictureX, pictureY, pictureScale) = Letterbox(header, outputWidth, outputHeight);
        var pictureRect = (X: pictureX, Y: pictureY, W: canvasWidth * pictureScale, H: Math.Max(header.CanvasHeight, 1) * pictureScale);

        // The flat picture's frame of reference, kept to be put back.
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

            // The point looked at: the edge found, else the box's top left corner (a feathered rectangle's softest place).
            (pointX, pointY) = _loupeHasPoint ? _loupePoint : (layer.X + layer.Width * 0.04f, layer.Y + layer.Height * 0.04f);

            // The loupe: about a third of the picture's shorter side, the layer drawn at 400 % of the player's scale.
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

        // Where it goes: the corner of the picture (as shown) farthest from the point, inside the output.
        var (shownX, shownY) = (_loupeHasPoint ? _loupePoint.X : layer.X, _loupeHasPoint ? _loupePoint.Y : layer.Y);
        var (x, y) = LoupeCorner(pictureRect, outputWidth, outputHeight, side, shownX * pictureScale + pictureX, shownY * pictureScale + pictureY);
        (_loupePlace, _loupePicture, _loupeFocus, _loupeReady) = ((x, y, side), pictureRect, (pointX, pointY, scale), true);
        return true;
    }

    /// <summary>
    /// Prepares the Export Quality Preview's Magnifier (5.4D, <see cref="FrameSceneHeader.FlagLoopbackLoupe"/>) round a point of the
    /// output (<paramref name="outputX"/>, <paramref name="outputY"/>: the pointer, in output pixels): no layer and no edge scan, only
    /// the view and its place (<see cref="LoupeAt"/>), for the compare pass to draw into (ExportLoopbackPreview.DrawLoupeView). False
    /// (nothing prepared) off the picture.
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
    /// The Magnifier's loupe round a point of the output (5.4D): its side and corner (as an edge's loupe has them: <see cref="LoupeSide"/>,
    /// <see cref="LoupeCorner"/>), the canvas point under the output point, view pixels per canvas pixel (the player's scale x LoupeZoom),
    /// and the picture's rectangle on the output. Null off the picture. Pure, for the golden tests.
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

    /// <summary>A loupe's side on a picture of this rectangle: about a third of its shorter side, 120 to 640 pixels.</summary>
    internal static int LoupeSide((float X, float Y, float W, float H) picture) =>
        (int)Math.Clamp(MathF.Round(Math.Min(picture.W, picture.H) * 0.36f), 120, 640);

    /// <summary>
    /// Where a loupe of <paramref name="side"/> stands: the corner of the picture (within <see cref="LoupeBounds"/>) farthest from the
    /// output point it looks at (<paramref name="atX"/>, <paramref name="atY"/>), so it never covers what it magnifies.
    /// </summary>
    internal static (float X, float Y) LoupeCorner((float X, float Y, float W, float H) picture, int outputWidth, int outputHeight, int side, float atX, float atY)
    {
        var (left, right, top, bottom) = LoupeBounds(picture, outputWidth, outputHeight, side);
        return (atX < (left + right + side) / 2 ? right : left, atY < (top + bottom + side) / 2 ? bottom : top);
    }

    /// <summary>
    /// The corners a loupe of <paramref name="side"/> may stand in on a picture: 12 pixels in from the picture's edges, within the
    /// output (left and top the near corner's, right and bottom the far one's). Shared by the placement and the golden tests.
    /// </summary>
    internal static (float Left, float Right, float Top, float Bottom) LoupeBounds((float X, float Y, float W, float H) picture, int outputWidth, int outputHeight, int side) =>
        (Math.Max(picture.X, 0) + 12, Math.Min(picture.X + picture.W, outputWidth) - 12 - side,
         Math.Max(picture.Y, 0) + 12, Math.Min(picture.Y + picture.H, outputHeight) - 12 - side);

    /// <summary>
    /// The prepared loupe laid onto <paramref name="targetRtv"/> (an output of <paramref name="outputWidth"/> x
    /// <paramref name="outputHeight"/>, the one PrepareLoupe was given): its view over a checkerboard, its frame and cross hairs, kept
    /// within the picture. One quad: the Export Quality Preview lays it on under the output's keyed mutex (5.4C). Then it is used up.
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
            // Quad's frame of reference: this output, kept within the picture as a letterboxed layer is.
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

    /// <summary>The flat path's frame of reference set to draw into <paramref name="target"/> at a scale and an offset (canvas pixels to its pixels).</summary>
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
    /// The edge search: the layer alone on a small picture of its box, searched on the card (PSLoupeFind), the one-pixel
    /// answer copied to a staging texture to be read a frame or two later (TakeLoupePoint).
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
            return; // its last answer not read yet: this frame's search is skipped
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
    /// Reads an edge search's answer when the card has it (never waits): the point on the canvas, followed smoothly (a jump
    /// when it is far, or the first).
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
