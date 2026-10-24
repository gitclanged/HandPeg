namespace HandPegApp.Models;

/// <summary>
/// How a clip's picture comes on over its fade in, or goes over its fade out (its sound fades the same way whatever this
/// is). Every clip can fade on its own, over whatever is under it, so a transition between two clips on separate tracks
/// is the upper one's fade over the lower. A direction is the way the edge of a wipe, or the picture of a slide, travels.
/// </summary>
/// <remarks>Saved by name in projects: new styles are added at the end, and none is renamed.</remarks>
public enum TransitionStyle
{
    /// <summary>Its opacity goes from nothing to all (or back): the plain fade, and a cross dissolve over a clip under it.</summary>
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
    /// The curve a fade of this style follows, as how much of the clip is there (0 to 1) against how far through the fade
    /// the clock is: a dissolve evenly; a wipe easing in and out; a slide arriving gently (and leaving as gently as it
    /// starts); a zoom with a little overshoot. The same curve on the timeline and in the graph (EasingCurves).
    /// </summary>
    public static EasingType Easing(TransitionStyle style) => style switch
    {
        TransitionStyle.Dissolve or TransitionStyle.BlurDissolve => EasingType.Linear,
        TransitionStyle.Zoom => EasingType.Back,
        _ when IsSlide(style) => EasingType.EaseOut,
        _ => EasingType.EaseInOut,
    };
}
