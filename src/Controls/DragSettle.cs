using System.Windows;

namespace HandPegApp;

/// <summary>
/// Velocity-gated drop zones for Timeline drags: tracks recent pointer speed so zones that change the drop result ("+ New Track" seam,
/// clip tether, header nest) activate only once the pointer settles, never while sweeping across. Above <see cref="TransitSpeed"/> is
/// Transit Mode (all zones inactive). Below <see cref="SettleSpeed"/> for <see cref="SettleMs"/>, or stationary that long, is settled
/// (the zone under the pointer may activate). An active zone stays active until Transit, so slow drift between the two thresholds
/// never flickers it (hysteresis).
/// <para>Pure: callers supply time in ms (MomentumScroller.Now, or a test clock) and points in control coordinates, so panning or
/// auto-scroll under a stationary pointer never counts as speed. One instance per control, created on the first drag.</para>
/// </summary>
internal sealed class DragSettle
{
    /// <summary>Speed (DIP/s) above which Transit Mode deactivates all zones.</summary>
    public const double TransitSpeed = 160;

    /// <summary>Speed (DIP/s) below which, sustained for <see cref="SettleMs"/>, the pointer is settled.</summary>
    public const double SettleSpeed = 60;

    /// <summary>Slow or stationary duration required to count as settled, ms.</summary>
    public const double SettleMs = 75;

    /// <summary>Velocity window: speed is measured between endpoints spanning the newest samples within this many ms.</summary>
    public const double WindowMs = 50;

    /// <summary>Jitter threshold in DIPs: smaller moves from the last significant position count as stationary (as in MomentumScroller).</summary>
    public const double StillDip = 0.5;

    private const int Capacity = 32;

    // Ring buffer of recent samples: position and time (ms).
    private readonly (double X, double Y, double T)[] _samples = new (double, double, double)[Capacity];
    private int _count, _next;

    // Slow-since time (NaN while fast), time of the last move of at least StillDip, and that move's position.
    private double _slowSince = double.NaN, _lastMove = double.NaN;
    private Point _movedFrom;

    /// <summary>Pointer speed at the newest sample, DIP/s.</summary>
    public double Speed { get; private set; }

    /// <summary>Timestamp of the newest sample (ms); NaN before the first press.</summary>
    public double LastSampleMs { get; private set; } = double.NaN;

    /// <summary>True once the dwell timer has re-evaluated during the current rest (it does so once per rest, then only on modifier-key changes); cleared by a press or move.</summary>
    public bool RestLooked { get; set; }

    /// <summary>Starts tracking on press: pointer at rest at <paramref name="at"/>, settling from <paramref name="nowMs"/>.</summary>
    public void Begin(Point at, double nowMs)
    {
        (_count, _next) = (0, 0);
        Add(at, nowMs);
        Speed = 0;
        (_slowSince, _lastMove, _movedFrom) = (nowMs, nowMs, at);
        LastSampleMs = nowMs;
        RestLooked = false;
    }

    /// <summary>Records a pointer move to <paramref name="at"/> at <paramref name="nowMs"/> (clamped to be monotonic).</summary>
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

        // Velocity endpoints: the oldest sample within the window, or the immediately preceding sample if none is in it.
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

    /// <summary>Transit Mode: speed above <see cref="TransitSpeed"/> with a significant move within the last <see cref="WindowMs"/>.</summary>
    public bool InTransit(double nowMs) => Speed > TransitSpeed && nowMs - _lastMove < WindowMs;

    /// <summary>Settled: slow for <see cref="SettleMs"/>, or stationary (no move of <see cref="StillDip"/>) for as long.</summary>
    public bool IsSettled(double nowMs) => (!double.IsNaN(_slowSince) && nowMs - _slowSince >= SettleMs) || nowMs - _lastMove >= SettleMs;

    /// <summary>Zone activation: the already active zone (<paramref name="sameZoneAsActive"/>) stays active until Transit; others activate once settled.</summary>
    public bool Awake(bool sameZoneAsActive, double nowMs) => sameZoneAsActive ? !InTransit(nowMs) : IsSettled(nowMs);

    private void Add(Point at, double nowMs)
    {
        _samples[_next] = (at.X, at.Y, nowMs);
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }
}
