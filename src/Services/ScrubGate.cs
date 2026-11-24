namespace HandPegApp.Services;

/// <summary>
/// Pass 5.6.4A: UI-thread gate for a held playhead scrub. PositionMs listeners that rebuild heavy UI (player and Canvas boxes with their
/// hit masks, the Layout pane, the key timeline, the Effects pane's animated values) skip the intermediate 16 ms scrub commits while
/// <see cref="IsHeld"/> and catch up once on <see cref="Released"/>. Held and released by MainWindow's scrub (MainWindow.Scrub.cs).
/// </summary>
public static class ScrubGate
{
    // Volatile: also read by GpuPreviewEngine's pump for FrameSceneHeader.FlagScrubbing.
    private static volatile bool _held;

    /// <summary>True while a playhead drag is held (Overview Scrubber thumb or a ruler). Any thread may read it.</summary>
    public static bool IsHeld
    {
        get => _held;
        private set => _held = value;
    }

    /// <summary>Raised once as a held scrub ends (after its last PositionMs commit); listeners refresh what they skipped.</summary>
    public static event Action? Released;

    /// <summary>A scrub move: holds the gate (idempotent).</summary>
    public static void Hold() => IsHeld = true;

    /// <summary>The scrub ended: opens the gate and raises <see cref="Released"/> once; false when it was not held.</summary>
    public static bool Release()
    {
        if (!IsHeld)
            return false;
        IsHeld = false;
        Released?.Invoke();
        return true;
    }
}
