namespace HandPegApp;

/// <summary>
/// Hold logic for a project tab hover card: open while the pointer is over the tab or the card, plus a short grace after leaving
/// both so the gap between tab and card can be crossed. Gliding between tabs never closes it (the next tab's enter follows the
/// previous leave). Pure, driven by a supplied ms clock: the strip polls <see cref="Due"/> from a timer, the tab-hover-scrub golden
/// from its own clock.
/// </summary>
internal sealed class HoverCardHold(double graceMs)
{
    // Pointer-over flags and the time it left both (NaN while over either, or before any leave).
    private bool _overTab, _overCard;
    private double _leftAt = double.NaN;

    /// <summary>Grace period after leaving both tab and card before closing (ms).</summary>
    public double GraceMs { get; } = graceMs;

    /// <summary>True while the pointer is over the tab or the card.</summary>
    public bool IsOver => _overTab || _overCard;

    /// <summary>Pointer entered a background tab (its own card, or the card retargeted to it).</summary>
    public void EnterTab() => (_overTab, _leftAt) = (true, double.NaN);

    /// <summary>Pointer left the tab at <paramref name="nowMs"/>.</summary>
    public void LeaveTab(double nowMs)
    {
        _overTab = false;
        Left(nowMs);
    }

    /// <summary>Pointer entered the card.</summary>
    public void EnterCard() => (_overCard, _leftAt) = (true, double.NaN);

    /// <summary>Pointer left the card at <paramref name="nowMs"/>.</summary>
    public void LeaveCard(double nowMs)
    {
        _overCard = false;
        Left(nowMs);
    }

    /// <summary>True when the card should close: the pointer has been off both for the full grace.</summary>
    public bool Due(double nowMs) => !IsOver && !double.IsNaN(_leftAt) && nowMs - _leftAt >= GraceMs;

    /// <summary>Time until <see cref="Due"/> in ms (0 when due; infinity while over the tab or card).</summary>
    public double Remaining(double nowMs) => IsOver || double.IsNaN(_leftAt) ? double.PositiveInfinity : Math.Max(GraceMs - (nowMs - _leftAt), 0);

    /// <summary>Clears all hold state when the card closes (including immediate closes: press, drag, menu).</summary>
    public void Reset() => (_overTab, _overCard, _leftAt) = (false, false, double.NaN);

    private void Left(double nowMs)
    {
        if (!IsOver)
            _leftAt = nowMs;
    }
}
