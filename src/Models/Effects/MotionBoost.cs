namespace HandPegApp.Models.Effects;

/// <summary>
/// Motion Blur amount boost: linear up to 50 %, then the shutter extends so 100 % yields a dramatic smear. Shared by the scene plan
/// (layer motion shutter half-width, GpuScenePlan.Shutter) and the compositor (optical-flow share and longest streak,
/// SceneRenderer.Fx.cs MotionBlur) without extra samples or passes.
/// </summary>
public static class MotionBoost
{
    /// <summary>The Amount (percent) up to which nothing is widened.</summary>
    public const double Plain = 50;

    /// <summary>How many times as far the shutter reaches at an Amount of 100 %.</summary>
    public const double Strongest = 2.5;

    /// <summary>Max boosted streak length: half the picture (uv), matching the card's Longest Streak maximum.</summary>
    public const float LongestStreak = 0.5f;

    /// <summary>
    /// Shutter multiplier for an Amount (%): 1 up to 50 % (and NaN), then a C1-smooth ramp to <see cref="Strongest"/> at 100 % and above.
    /// </summary>
    public static double Span(double amountPercent)
    {
        if (!(amountPercent > Plain))
            return 1;

        var t = Math.Min((amountPercent - Plain) / (100 - Plain), 1);
        return 1 + (Strongest - 1) * t * t;
    }
}
