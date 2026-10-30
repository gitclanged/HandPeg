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

    private void DrawLoupe(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers)
    {
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
            return;
        ref readonly var layer = ref layers[index];
        if (_loupeKey != header.LoupeClipKey)
            (_loupeKey, _loupeHasPoint) = (header.LoupeClipKey, false);

        // The flat picture's frame of reference, kept to be put back.
        var saved = (_canvas, _canvasCopy, _outWidth, _outHeight, _scaleX, _scaleY, _offsetX, _offsetY, _letterbox, _frameRect, _pxScale1080, _pixelScale);
        var canvasWidth = Math.Max(header.CanvasWidth, 1);
        var (pictureScale, pictureX, pictureY) = (_scaleX, _offsetX, _offsetY);
        var pictureRect = _frameRect;
        GpuTexture? view = null;
        var side = 0;
        try
        {
            _alone = true;
            TakeLoupePoint(layer);
            ScanLoupeEdge(layer, canvasWidth);

            // The point looked at: the edge found, else the box's top left corner (a feathered rectangle's softest place).
            var (pointX, pointY) = _loupeHasPoint ? _loupePoint : (layer.X + layer.Width * 0.04f, layer.Y + layer.Height * 0.04f);

            // The loupe: about a third of the picture's shorter side, the layer drawn at 400 % of the player's scale.
            side = (int)Math.Clamp(MathF.Round(Math.Min(pictureRect.W, pictureRect.H) * 0.36f), 120, 640);
            var zoom = header.LoupeZoom > 0 ? header.LoupeZoom : 4;
            var scale = pictureScale * zoom;
            scale = Math.Min(scale, LoupeMostSide / Math.Max(Math.Max(MathF.Abs(layer.Width), MathF.Abs(layer.Height)), 1));
            view = Target(ref _loupeView, side, side);
            Frame(scale, side / 2f - pointX * scale, side / 2f - pointY * scale, side, side, canvasWidth, view);
            _historySalt = 0x4C4F555045; // "LOUPE"
            DrawLayer(layer);
            _gpu.Textures.ReturnAll();
        }
        finally
        {
            _alone = false;
            _historySalt = 0;
            (_canvas, _canvasCopy, _outWidth, _outHeight, _scaleX, _scaleY, _offsetX, _offsetY, _letterbox, _frameRect, _pxScale1080, _pixelScale) = saved;
        }

        if (view is null || _canvas is null)
            return;

        // Where it goes: the corner of the picture (as shown) farthest from the point, inside the output.
        var (shownX, shownY) = (_loupeHasPoint ? _loupePoint.X : layer.X, _loupeHasPoint ? _loupePoint.Y : layer.Y);
        var (atX, atY) = (shownX * pictureScale + pictureX, shownY * pictureScale + pictureY);
        var left = Math.Max(pictureRect.X, 0) + 12;
        var top = Math.Max(pictureRect.Y, 0) + 12;
        var right = Math.Min(pictureRect.X + pictureRect.W, _outWidth) - 12 - side;
        var bottom = Math.Min(pictureRect.Y + pictureRect.H, _outHeight) - 12 - side;
        var x = atX < (left + right + side) / 2 ? right : left;
        var y = atY < (top + bottom + side) / 2 ? bottom : top;

        var constants = default(PassConstants);
        constants.Transform = Matrix4.Box(x, y, side, side, 0, 0.5f, 0.5f, _outWidth, _outHeight);
        constants.WholeUv();
        constants.Color = new Vector4(1f, 0.72f, 0.18f, 1);
        constants.Params2 = new Vector4(MathF.Max(MathF.Round(side / 32f), 6), 2, 0, 0.85f);
        constants.Params3 = new Vector4(side, side, 0, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        Quad("PSLoupeView", view, _canvas.Rtv, ref constants, blend: false);
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
            Com.Release(ref _loupeStaging[k]);
    }
}
