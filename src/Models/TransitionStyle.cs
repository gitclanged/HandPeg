using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

/// <summary>
/// Visual style of a clip's fade in/out (audio always fades linearly). Fades composite over whatever is below, so a cross-track
/// transition is the upper clip's fade. Direction is the travel of a wipe edge or slide.
/// </summary>
/// <remarks>Serialized by name: append new styles, never rename.</remarks>
public enum TransitionStyle
{
    /// <summary>Opacity ramp: plain fade, or cross dissolve over a clip below.</summary>
    Dissolve,

    WipeLeft,
    WipeRight,
    WipeUp,
    WipeDown,

    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,

    /// <summary>Grows from nothing about its anchor point with a little overshoot (or shrinks away to nothing).</summary>
    Zoom,

    /// <summary>A dissolve that comes in out of a blur (or goes out into one).</summary>
    BlurDissolve,

    /// <summary>The Thanos Snap: the picture breaks into embers on a wind (or gathers itself out of them).</summary>
    ParticleSnap,

    /// <summary>Camera whip: enters from the right (exits left) with directional motion blur.</summary>
    WhipPan,

    /// <summary>Punches in from three times its size about its middle, streaked outward (or away into it).</summary>
    ZoomWarp,

    /// <summary>Torn rows and split colours that settle as it comes on, blinking between it and what is under it.</summary>
    RgbGlitch,

    /// <summary>Full rotation about the anchor with rotational blur, appearing at the start (or vanishing at the end).</summary>
    Spin,
}

/// <summary>
/// Gain law of an audio crossfade across a cut seam or an overlap (Pass 5.7.1, the Seam menu; Layer.CrossfadeInCurve and
/// CrossfadeOutCurve). Equal power, the default, keeps the loudness of two different sounds through the fade; linear keeps the level
/// of one sound cut in two (a through-edit).
/// </summary>
/// <remarks>Saved by value: append new curves, never reorder or rename.</remarks>
public enum CrossfadeCurve
{
    /// <summary>Quarter sine and cosine: out^2 + in^2 = 1 (no dip in loudness for uncorrelated sounds).</summary>
    EqualPower,

    /// <summary>Straight ramps: out + in = 1 (no bump in level for correlated sounds).</summary>
    Linear,
}

/// <summary>What each <see cref="CrossfadeCurve"/> is called and the two gains it gives (one formula for the mixer, menus and drawing).</summary>
public static class CrossfadeCurves
{
    public static IReadOnlyList<CrossfadeCurve> All { get; } = Enum.GetValues<CrossfadeCurve>();

    public static string Label(CrossfadeCurve curve) => curve == CrossfadeCurve.Linear ? "Linear" : "Equal Power";

    /// <summary>A saved curve, held to the known ones (an unknown value is equal power).</summary>
    public static CrossfadeCurve Known(CrossfadeCurve? saved) => saved is { } curve && Enum.IsDefined(curve) ? curve : CrossfadeCurve.EqualPower;

    /// <summary>
    /// Gains of the outgoing and incoming sounds at <paramref name="progress"/> (0 at the crossfade's start, 1 at its end; clamped,
    /// NaN as 0). Exact at both ends: (1, 0) and (0, 1).
    /// </summary>
    public static (double Out, double In) Gains(CrossfadeCurve curve, double progress)
    {
        var t = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        if (t <= 0)
            return (1, 0);
        if (t >= 1)
            return (0, 1);
        return curve == CrossfadeCurve.Linear ? (1 - t, t) : (Math.Cos(t * Math.PI / 2), Math.Sin(t * Math.PI / 2));
    }
}

/// <summary>What each <see cref="TransitionStyle"/> is called, which way it goes, and the curve its fade follows.</summary>
public static class TransitionStyles
{
    public static IReadOnlyList<TransitionStyle> All { get; } = Enum.GetValues<TransitionStyle>();

    public static string Label(TransitionStyle style) => style switch
    {
        TransitionStyle.WipeLeft => "Wipe Left",
        TransitionStyle.WipeRight => "Wipe Right",
        TransitionStyle.WipeUp => "Wipe Up",
        TransitionStyle.WipeDown => "Wipe Down",
        TransitionStyle.SlideLeft => "Slide Left",
        TransitionStyle.SlideRight => "Slide Right",
        TransitionStyle.SlideUp => "Slide Up",
        TransitionStyle.SlideDown => "Slide Down",
        TransitionStyle.Zoom => "Zoom (Scale Pop)",
        TransitionStyle.BlurDissolve => "Blur Dissolve",
        TransitionStyle.ParticleSnap => "Particle Disintegration (Thanos Snap)",
        TransitionStyle.WhipPan => "Whip Pan",
        TransitionStyle.ZoomWarp => "Zoom Warp",
        TransitionStyle.RgbGlitch => "RGB Glitch",
        TransitionStyle.Spin => "Spin",
        _ => "Dissolve",
    };

    /// <summary>The way it travels, as a step across (X) and down (Y); (0, 0) for a style that goes nowhere.</summary>
    public static (int X, int Y) Direction(TransitionStyle style) => style switch
    {
        TransitionStyle.WipeLeft or TransitionStyle.SlideLeft => (-1, 0),
        TransitionStyle.WipeRight or TransitionStyle.SlideRight => (1, 0),
        TransitionStyle.WipeUp or TransitionStyle.SlideUp => (0, -1),
        TransitionStyle.WipeDown or TransitionStyle.SlideDown => (0, 1),
        _ => (0, 0),
    };

    public static bool IsWipe(TransitionStyle style) => style is TransitionStyle.WipeLeft or TransitionStyle.WipeRight or TransitionStyle.WipeUp or TransitionStyle.WipeDown;

    public static bool IsSlide(TransitionStyle style) => style is TransitionStyle.SlideLeft or TransitionStyle.SlideRight or TransitionStyle.SlideUp or TransitionStyle.SlideDown;

    /// <summary>
    /// Progress curve (visibility 0-1 vs. fade time): dissolve, Thanos Snap and glitch linear; wipe, whip pan and spin ease in-out;
    /// slide and zoom warp ease out; zoom slight overshoot. Shared by Timeline and graph (EasingCurves).
    /// </summary>
    public static EasingType Easing(TransitionStyle style) => style switch
    {
        TransitionStyle.Dissolve or TransitionStyle.BlurDissolve or TransitionStyle.ParticleSnap or TransitionStyle.RgbGlitch => EasingType.Linear,
        TransitionStyle.Zoom => EasingType.Back,
        TransitionStyle.ZoomWarp => EasingType.EaseOut,
        _ when IsSlide(style) => EasingType.EaseOut,
        _ => EasingType.EaseInOut,
    };

    /// <summary>Compositor shader slot: one per family, with direction passed in the packet.</summary>
    public static GpuShaderSlot GpuSlot(TransitionStyle style) => style switch
    {
        TransitionStyle.Zoom => GpuShaderSlot.TransitionZoom,
        TransitionStyle.BlurDissolve => GpuShaderSlot.TransitionBlurDissolve,
        TransitionStyle.ParticleSnap => GpuShaderSlot.TransitionParticleSnap,
        TransitionStyle.WhipPan => GpuShaderSlot.TransitionWhipPan,
        TransitionStyle.ZoomWarp => GpuShaderSlot.TransitionZoomWarp,
        TransitionStyle.RgbGlitch => GpuShaderSlot.TransitionRgbGlitch,
        TransitionStyle.Spin => GpuShaderSlot.TransitionSpin,
        _ when IsWipe(style) => GpuShaderSlot.TransitionWipe,
        _ when IsSlide(style) => GpuShaderSlot.TransitionSlide,
        _ => GpuShaderSlot.TransitionDissolve,
    };

    /// <summary>
    /// Compositor packet: P0 eased progress (<see cref="Easing"/>), P1-P2 direction (<see cref="Direction"/>), P3 linear progress 0-1,
    /// P4 1 for fade in, 0 for fade out.
    /// </summary>
    public static GpuEffectPacket GpuPacket(TransitionStyle style, double progress, bool fadingIn)
    {
        var through = Math.Clamp(progress, 0, 1);
        var shown = EasingCurves.Evaluate(fadingIn ? through : 1 - through, new Keyframe(0, 0, Easing(style)));
        var (x, y) = Direction(style);
        return new(GpuSlot(style), (float)shown, x, y, (float)through, fadingIn ? 1 : 0, 0, 0, 0);
    }
}
