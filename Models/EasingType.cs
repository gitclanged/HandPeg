namespace HandPegApp.Models;

/// <summary>
/// How a clip gets from one keyframe to the next: the shape of the stretch that leaves the keyframe it is
/// set on. Each is a closed-form curve of how far through the stretch the clip is (0 to 1), which is what
/// lets FFmpeg work it out for every frame from one expression.
/// </summary>
public enum EasingType
{
    /// <summary>At one speed all the way.</summary>
    Linear,

    /// <summary>Starts slowly, ends slowly: half a cosine wave.</summary>
    EaseInOut,

    /// <summary>Starts slowly and arrives at full speed.</summary>
    EaseIn,

    /// <summary>Leaves at full speed and settles slowly.</summary>
    EaseOut,

    /// <summary>Starts and ends slowly, a little more gently than Ease In-Out: 3u² - 2u³.</summary>
    Smoothstep,
}

/// <summary>An easing as the Keyframes pane's drop-down offers it.</summary>
public sealed record EasingChoice(EasingType Type, string Name)
{
    public static IReadOnlyList<EasingChoice> All { get; } =
    [
        new(EasingType.Linear, "Linear"),
        new(EasingType.EaseInOut, "Ease In-Out"),
        new(EasingType.EaseIn, "Ease In"),
        new(EasingType.EaseOut, "Ease Out"),
        new(EasingType.Smoothstep, "Smoothstep"),
    ];

    public override string ToString() => Name;
}
