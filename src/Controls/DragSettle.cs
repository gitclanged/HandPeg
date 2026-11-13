using System.Windows;

namespace HandPegApp;

/// <summary>
/// The Timeline's drags held back while they pass over (Pass 5.5A, velocity-gated dragging): the pointer's speed over its last moves,
/// so a zone that changes what a let-go does (a "+ New Track" seam, a clip's tether, a header's nest) wakes only once the pointer has
/// settled on it, never while it sweeps across. Fast (over <see cref="TransitSpeed"/>) is Transit Mode: every zone asleep. Slow (under
/// <see cref="SettleSpeed"/>) for <see cref="SettleMs"/>, or still for as long, is settled: a zone under it may wake. A zone already
/// awake stays awake until Transit, so a slow drift across it (between the two speeds) never flickers it (the hysteresis).
/// <para>Pure: the caller passes the time (ms: MomentumScroller.Now, a test its own clock) and the points in the control's own
/// coordinates, so a pan or an auto-scroll under a still pointer never counts as speed. Made at the first drag, one per control.</para>
/// </summary>
internal sealed class DragSettle
{
    /// <summary>DIP/s: over it, Transit Mode (every zone asleep).</summary>
    public const double TransitSpeed = 160;

    /// <summary>DIP/s: under it for <see cref="SettleMs"/>, settled (a zone may wake).</summary>
    public const double SettleSpeed = 60;

    /// <summary>How long the pointer stays slow (or still) before it counts as settled, ms.</summary>
    public const double SettleMs = 75;

    /// <summary>The speed is the moves' endpoints over this much of the newest ones, ms.</summary>
    public const double WindowMs = 50;

    /// <summary>A move shorter than this (DIPs) from where the pointer last moved is a hand's jitter: the pointer counts as still (MomentumScroller's).</summary>
    public const double StillDip = 0.5;

    private const int Capacity = 32;

    // The newest moves, a ring: where and when (ms).
    private readonly (double X, double Y, double T)[] _samples = new (double, double, double)[Capacity];
    private int _count, _next;

    // Since when the pointer has been slow (NaN while it is not), when it last moved by StillDip or more, and where it was then.
    private double _slowSince = double.NaN, _lastMove = double.NaN;
    private Point _movedFrom;

    /// <summary>The pointer's speed at its newest sample, DIP/s.</summary>
    public double Speed { get; private set; }

    /// <summary>When the newest sample was taken (ms); NaN before the first press.</summary>
    public double LastSampleMs { get; private set; } = double.NaN;

    /// <summary>Whether the drag has looked again at rest since the pointer last moved (5.5A review: the dwell's timer looks once a rest, then only when the keys change); a press or a move clears it.</summary>
    public bool RestLooked { get; set; }

    /// <summary>A press: the pointer at rest there, settling from now.</summary>
    public void Begin(Point at, double nowMs)
    {
        (_count, _next) = (0, 0);
        Add(at, nowMs);
        Speed = 0;
        (_slowSince, _lastMove, _movedFrom) = (nowMs, nowMs, at);
        LastSampleMs = nowMs;
        RestLooked = false;
    }

    /// <summary>A move of the pointer to <paramref name="at"/> at <paramref name="nowMs"/> (never earlier than the last sample).</summary>
    public void Sample(Point at, double nowMs)
    {
        if (double.IsNaN(LastSampleMs) || !double.IsFinite(nowMs))
        {
            Begin(at, double.IsFinite(nowMs) ? nowMs : 0);
            return;
        }

        nowMs = Math.Max(nowMs, LastSampleMs);
        RestLooked = false;
        Add(at, nowMs);

        // The endpoints over the window: the oldest sample within it; with none older there, the one just before this.
        var (oldest, found) = ((X: at.X, Y: at.Y, T: nowMs), false);
        for (var k = 1; k < _count; k++)
        {
            var sample = _samples[(_next - 1 - k + 2 * Capacity) % Capacity];
            var inWindow = nowMs - sample.T <= WindowMs;
            if (found && !inWindow)
                break;
            (oldest, found) = (sample, true);
            if (!inWindow)
                break;
        }

        Speed = found ? Math.Sqrt((at.X - oldest.X) * (at.X - oldest.X) + (at.Y - oldest.Y) * (at.Y - oldest.Y)) / Math.Max(nowMs - oldest.T, 1) * 1000 : 0;
        if (Math.Abs(at.X - _movedFrom.X) >= StillDip || Math.Abs(at.Y - _movedFrom.Y) >= StillDip)
            (_lastMove, _movedFrom) = (nowMs, at);
        if (Speed >= SettleSpeed)
            _slowSince = double.NaN;
        else if (double.IsNaN(_slowSince))
            _slowSince = nowMs;
        LastSampleMs = nowMs;
    }

    /// <summary>Transit Mode: faster than <see cref="TransitSpeed"/>, and still moving (within the last <see cref="WindowMs"/>).</summary>
    public bool InTransit(double nowMs) => Speed > TransitSpeed && nowMs - _lastMove < WindowMs;

    /// <summary>Settled: slow for <see cref="SettleMs"/>, or still (no move of <see cref="StillDip"/>) for as long.</summary>
    public bool IsSettled(double nowMs) => (!double.IsNaN(_slowSince) && nowMs - _slowSince >= SettleMs) || nowMs - _lastMove >= SettleMs;

    /// <summary>Whether a zone under the pointer is awake: one that already is (<paramref name="sameZoneAsActive"/>) stays so until Transit; any other wakes once settled.</summary>
    public bool Awake(bool sameZoneAsActive, double nowMs) => sameZoneAsActive ? !InTransit(nowMs) : IsSettled(nowMs);

    private void Add(Point at, double nowMs)
    {
        _samples[_next] = (at.X, at.Y, nowMs);
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }
}
