namespace HandPegApp;

/// <summary>
/// Whether a project tab's hover card stays open (5.4.4): while the pointer is over its tab or over the card itself, and for a short
/// grace after it left both, so the gap between the tab and the card under it is crossed without the card closing. Gliding from one
/// tab to the next never closes it (the next tab's enter follows the last one's leave). Pure, on the clock it is given (milliseconds):
/// the strip asks <see cref="Due"/> from a timer, the golden tab-hover-scrub from a clock of its own.
/// </summary>
internal sealed class HoverCardHold(double graceMs)
{
    // Where the pointer is, and when it last left the tab and the card both (NaN while it is over one of them, or before any leave).
    private bool _overTab, _overCard;
    private double _leftAt = double.NaN;

    /// <summary>How long the card waits after the pointer left the tab and the card (ms).</summary>
    public double GraceMs { get; } = graceMs;

    /// <summary>Whether the pointer is over the tab or the card now.</summary>
    public bool IsOver => _overTab || _overCard;

    /// <summary>The pointer came onto a tab behind (its own card, or the card moved to it).</summary>
    public void EnterTab() => (_overTab, _leftAt) = (true, double.NaN);

    /// <summary>The pointer left the tab at <paramref name="nowMs"/>.</summary>
    public void LeaveTab(double nowMs)
    {
        _overTab = false;
        Left(nowMs);
    }

    /// <summary>The pointer came onto the card.</summary>
    public void EnterCard() => (_overCard, _leftAt) = (true, double.NaN);

    /// <summary>The pointer left the card at <paramref name="nowMs"/>.</summary>
    public void LeaveCard(double nowMs)
    {
        _overCard = false;
        Left(nowMs);
    }

    /// <summary>Whether the card closes now: the pointer has been off the tab and the card for the whole grace.</summary>
    public bool Due(double nowMs) => !IsOver && !double.IsNaN(_leftAt) && nowMs - _leftAt >= GraceMs;

    /// <summary>How long until <see cref="Due"/> (ms, 0 when it is; infinite while the pointer is over the tab or the card).</summary>
    public double Remaining(double nowMs) => IsOver || double.IsNaN(_leftAt) ? double.PositiveInfinity : Math.Max(GraceMs - (nowMs - _leftAt), 0);

    /// <summary>The card closed (or moved by a close that is immediate: a press, a drag, a menu): nothing held.</summary>
    public void Reset() => (_overTab, _overCard, _leftAt) = (false, false, double.NaN);

    private void Left(double nowMs)
    {
        if (!IsOver)
            _leftAt = nowMs;
    }
}
