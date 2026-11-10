namespace HandPegApp;

/// <summary>
/// A control that scrolls in time on a sideways swipe or a tilted wheel (5.4.1A): the window's WM_MOUSEHWHEEL hook
/// (MainWindow.TimelineSidewaysWheel, also on every side window) hands the delta to the first visible one under the pointer when the
/// Timeline is not there. Positive is to the right: later.
/// </summary>
internal interface ISidewaysWheel
{
    /// <summary>One WM_MOUSEHWHEEL delta (120 a notch; positive to the right, later) at <paramref name="nowMs"/> (MomentumScroller.Now's clock).</summary>
    void SidewaysWheel(double delta, double nowMs);
}
