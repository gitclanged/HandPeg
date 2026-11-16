using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Smooth Slow-Mo, an in-between of a frame and the next by optical flow (GpuLayerDescriptor.FlagInterpolate) -----
// A clip slower than 1x with Smooth Slow-Mo on (the plan sets the flag: GpuScenePlan.Create's Timing) shows, between two of its frames, the
// picture the motion puts there instead of the first frame held. The next frame is the layer's companion (DecoderPool.ResolveNext,
// under a salted clip key, keeps no layer of its own, so no second play rate, PlayAhead or pre-roll stretch, nor a second decoder for the file):
// while it is not decoded yet the frame is drawn as before and the decoders' demand brings it (an export waits for it, as for any frame not exact). The flow of the next frame from this one is the Motion Blur's own measure of it (FlowOf,
// one solve kept for both), and PSFlowInterpolate (Motion.hlsl) reads each frame where the motion puts the point, guarding against what one
// of them cannot show. Never for a blur fill's twin (cover) nor in the 3D view or under the loupe (their canvases are their own: a 3D plane is
// kept by its frame's time).
internal sealed unsafe partial class SceneRenderer
{
    // The second frame's clip key: the layer's own with this salt, so its demand never moves the layer's own (DecoderPool.ResolveNext).
    private const long InterpolateSalt = 0x5A3C_9E17_6B2D_41F3;

    // Closer than this to a frame (a share of the way to the next) is the frame itself.
    private const double OnTheFrame = 1e-3;

    /// <summary>Draws the in-between for the layer's moment into <paramref name="target"/>; false when it drew nothing (the frame is then drawn as before).</summary>
    private bool Interpolate(in GpuLayerDescriptor layer, CachedFrame frame, GpuTexture target, Vector4 uv)
    {
        if (!ReferenceEquals(_canvas, _canvasWhole) && !ReferenceEquals(_canvas, _canvasScaled) || !_shaders.Has("PSFlowInterpolate"))
            return false;

        // How far between this frame and the next the moment is: only for the frame that covers it (not a nearest one shown while it decodes).
        var span = frame.Until - frame.Time;
        if (!double.IsFinite(span) || span <= 1e-5)
            return false;
        var phi = (layer.MediaSeconds - frame.Time) / span;
        if (!(phi > OnTheFrame && phi < 1))
            return false;

        // The next frame: missing, the same (the file's last), or not the one right after (a nearest one) draws this frame alone.
        if (_decoders.ResolveNext(layer.SourceId, frame.Until + 1e-4, layer.SourcePathHash, layer.ClipKey ^ InterpolateSalt) is not { LumaSrv: not 0 } next
            || ReferenceEquals(next, frame) || !(next.Time > frame.Time) || Math.Abs(next.Time - frame.Until) > span * 0.5
            || FlowOf(next, frame, out var gap) is not { } field || !(gap > 1e-5))
        {
            return false;
        }

        var (from, to) = (_gpu.Textures.Rent(target.Width, target.Height), _gpu.Textures.Rent(target.Width, target.Height));
        DrawFrame(frame, from, uv, layer.Flags);
        DrawFrame(next, to, uv, layer.Flags);

        // The flow (texels of the whole frame's flow, over the gap it was solved across) in this picture's uv, for the two frames' own gap.
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
