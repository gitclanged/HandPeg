using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Smooth Slow-Mo: optical-flow in-between frames (GpuLayerDescriptor.FlagInterpolate) -----
// Clips slower than 1x draw the motion-compensated picture between a frame and the next instead of holding the first. The next frame
// resolves under a salted clip key (DecoderPool.ResolveNext: no extra layer, decoder or play rate); until it decodes the held frame is
// drawn (exports wait). Flow reuses the Motion Blur solve (FlowOf); PSFlowInterpolate (Motion.hlsl) warps both frames with occlusion
// guards. Not used for blur-fill twins, the 3D view or the loupe.
internal sealed unsafe partial class SceneRenderer
{
    // Salt for the next frame's clip key so its demand never moves the layer's own (DecoderPool.ResolveNext).
    private const long InterpolateSalt = 0x5A3C_9E17_6B2D_41F3;

    // Phase (fraction of the frame interval) below which the frame itself is drawn.
    private const double OnTheFrame = 1e-3;

    /// <summary>Draws the in-between frame into <paramref name="target"/>; false when nothing was drawn and the caller draws the frame.</summary>
    private bool Interpolate(in GpuLayerDescriptor layer, CachedFrame frame, GpuTexture target, Vector4 uv)
    {
        if (!ReferenceEquals(_canvas, _canvasWhole) && !ReferenceEquals(_canvas, _canvasScaled) || !_shaders.Has("PSFlowInterpolate"))
            return false;

        // Phase within the covering frame's interval (never a nearest-frame stand-in shown while decoding).
        var span = frame.Until - frame.Time;
        if (!double.IsFinite(span) || span <= 1e-5)
            return false;
        var phi = (layer.MediaSeconds - frame.Time) / span;
        if (!(phi > OnTheFrame && phi < 1))
            return false;

        // Fall back to the single frame when the next is missing, identical (last frame) or not adjacent.
        if (_decoders.ResolveNext(layer.SourceId, frame.Until + 1e-4, layer.SourcePathHash, layer.ClipKey ^ InterpolateSalt) is not { LumaSrv: not 0 } next
            || ReferenceEquals(next, frame) || !(next.Time > frame.Time) || Math.Abs(next.Time - frame.Until) > span * 0.5
            || FlowOf(next, frame, out var gap) is not { } field || !(gap > 1e-5))
        {
            return false;
        }

        var (from, to) = (_gpu.Textures.Rent(target.Width, target.Height), _gpu.Textures.Rent(target.Width, target.Height));
        DrawFrame(frame, from, uv, layer.Flags);
        DrawFrame(next, to, uv, layer.Flags);

        // Rescale the flow (full-frame texels over its solve gap) to this picture's uv and the two frames' actual gap.
        var share = (float)((next.Time - frame.Time) / gap);
        var constants = default(PassConstants);
        constants.Params2 = uv;
        constants.Params3 = new Vector4(share / field.Width / Math.Max(uv.Z - uv.X, 1e-4f), share / field.Height / Math.Max(uv.W - uv.Y, 1e-4f), (float)phi, 0);
        constants.Params4 = new Vector4(1, 1, 0, (float)_time);
        constants.WholeUv();
        var view = field.Srv;
        _gpu.Context.PSSetShaderResources(5, 1, &view);
        Step("PSFlowInterpolate", from, target, ref constants, 0, aux: to.Srv);
        view = 0;
        _gpu.Context.PSSetShaderResources(5, 1, &view);
        _gpu.Textures.Return(from);
        _gpu.Textures.Return(to);
        return true;
    }
}
