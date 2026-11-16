namespace HandPegApp.Models.Effects;

/// <summary>
/// 5.6.0 (lane C): Motion Blur's stronger top end. Its Amount (0..100 %) acts as before up to 50 %; above it the shutter reaches
/// further, so 100 % is a dramatic smear (product decision). Shared by the scene plan (a layer's own
/// moves: the shutter's half-width, GpuScenePlan.Shutter) and the compositor (motion in the picture: the flow's share and its longest
/// streak, SceneRenderer.Fx.cs MotionBlur); neither takes more samples or passes for it. Every card saved at Amount 100 gets the
/// stronger blur.
/// </summary>
public static class MotionBoost
{
    /// <summary>The Amount (percent) up to which nothing is widened.</summary>
    public const double Plain = 50;

    /// <summary>How many times as far the shutter reaches at an Amount of 100 %.</summary>
    public const double Strongest = 2.5;

    /// <summary>The longest a widened streak in the picture may be: half the picture (uv), as the card's own Longest Streak tops out at.</summary>
    public const float LongestStreak = 0.5f;

    /// <summary>
    /// How many times the shutter's reach is widened at an Amount in percent: exactly 1 up to 50 % (and for anything not a number), then a
    /// smooth ramp (level where it starts, so the slider has no knee) up to <see cref="Strongest"/> at 100 % and beyond.
    /// </summary>
    public static double Span(double amountPercent)
    {
        if (!(amountPercent > Plain))
            return 1;

        var t = Math.Min((amountPercent - Plain) / (100 - Plain), 1);
        return 1 + (Strongest - 1) * t * t;
    }
}
