namespace HandPegApp;

/// <summary>
/// Control that scrolls in time on a horizontal swipe or tilted wheel. The window's WM_MOUSEHWHEEL hook
/// (MainWindow.TimelineSidewaysWheel, also installed on side windows) forwards the delta to the first visible implementer under the
/// pointer when the Timeline is not there. Positive = right (later).
/// </summary>
internal interface ISidewaysWheel
{
    /// <summary>Handles one WM_MOUSEHWHEEL delta (120 per notch; positive = later) at <paramref name="nowMs"/> (MomentumScroller.Now clock).</summary>
    void SidewaysWheel(double delta, double nowMs);
}
