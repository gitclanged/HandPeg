using System.Diagnostics;
using System.Globalization;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.Native;

namespace HandPegApp.Compositor;

// ----- Playback decode pacing by a shared rate budget -----
// A 500 Hz on-screen trace showed background chases decoding >= 200 fps backing up the GPU, so the editor's Present waited 20-100 ms
// in most 100 ms windows (2 % at under 100 fps); per-draw slice pacing let chases run at 200-760 fps. Work ahead during playback now
// shares one frames-per-second budget across lanes (BudgetFps); a cut that cannot reach its time within budget gets the rate it needs
// (+25 %), or full speed when only that suffices. Discrete adapters open cuts earlier (CutReadAhead) so the budget covers most of them.
internal sealed partial class DecoderPool
{
    /// <summary>Shared work-ahead budget during playback, frames per second (below 100 fps presents kept their refresh in traces).</summary>
    internal const double PaceBudgetFps = 100;

    /// <summary>Seconds of slack a cut chase keeps before its time when its rate is computed.</summary>
    internal const double PaceMargin = 0.25;

    /// <summary>Maximum wait for a pacing slot before re-checking (the demand may have been cancelled meanwhile).</summary>
    private const double MostPaceWait = 0.1;

    /// <summary>Cut look-ahead in sequence seconds (times the play rate); matches GpuHintWriter.Horizon.</summary>
    internal const double CutReadAheadSeconds = 3.0;

    /// <summary>
    /// Effective budget: <see cref="PaceBudgetFps"/>, or HANDPEG_DECODE_BUDGET_FPS (read once; A/B knob inherited by the worker);
    /// a value &lt;= 0 restores per-draw slice pacing (Paced/WaitForNextDraw).
    /// </summary>
    internal static readonly double BudgetFps = ReadBudget();

    // Next shared slot (QPC ticks); _ownNext is a lane's own schedule when its cut is paced outside the budget.
    private long _paceNext;
    [ThreadStatic] private static long _ownNext;

    // Per-lane high-resolution waitable timer (Thread.Sleep and Monitor.Wait wake on the 15.6 ms tick); all closed with the pool.
    [ThreadStatic] private static nint _paceTimer;
    [ThreadStatic] private static DecoderPool? _paceTimerPool;
    private readonly List<nint> _paceTimers = [];

    private static double ReadBudget()
    {
        var text = Environment.GetEnvironmentVariable("HANDPEG_DECODE_BUDGET_FPS");
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && double.IsFinite(fps) ? fps : PaceBudgetFps;
    }

    /// <summary>
    /// Interval between a demand's frames during playback (pure): 1 / <paramref name="budgetFps"/> for work without a cut deadline; for a
    /// cut <paramref name="framesLeft"/> frames and <paramref name="secondsLeft"/> seconds away, 1 / (1.25 x needed rate, keeping
    /// <see cref="PaceMargin"/>) when that exceeds the budget; 0 (full speed) when it needs >= 80 % of <paramref name="fullSpeedFps"/>
    /// or the margin is gone.
    /// </summary>
    internal static double PaceInterval(double framesLeft, double secondsLeft, double budgetFps, double fullSpeedFps)
    {
        var budget = 1 / Math.Max(budgetFps, 1);
        if (!(framesLeft > 0) || double.IsNaN(secondsLeft))
            return budget;

        var usable = secondsLeft - PaceMargin;
        if (usable <= 1e-3)
            return 0;
        var needed = framesLeft / usable;
        if (needed >= 0.8 * fullSpeedFps)
            return 0;
        var rate = needed * 1.25;
        return rate > budgetFps ? 1 / rate : budget;
    }

    /// <summary>
    /// How close (sequence seconds) a cut may get before its decoder is opened or sought (pure): <see cref="CutSoon"/> on unified memory
    /// (single shared decoder lock on Adreno); on discrete adapters the hint horizon (<see cref="CutReadAheadSeconds"/> x play rate) so
    /// chases fit the budget.
    /// </summary>
    internal static double CutReadAhead(bool unifiedMemory, double rate) =>
        unifiedMemory ? CutSoon : Math.Max(CutSoon, CutReadAheadSeconds * Math.Max(rate, 1));

    /// <summary>
    /// Optional wait before a lane decodes its next frame (lane thread): only during playback and only for work ahead (never visible or
    /// play-ahead frames, reads they wait on, or exact export frames). Budget-rate work takes the shared slot; faster cuts keep their own
    /// schedule; full speed never waits.
    /// </summary>
    private void Pace(Demand demand, MfVideoDecoder decoder)
    {
        if (!_lastWasPlaying || _exactOnly || demand.Kind is DemandKind.Visible or DemandKind.PlayAhead || ServesPlayback(demand, decoder))
            return;
        if (BudgetFps <= 0)
        {
            if (Paced(demand, decoder))
                WaitForNextDraw();
            return;
        }

        var (framesLeft, secondsLeft) = demand.HintKind == GpuSeekHintKind.CutEntry ? CutLeft(demand, decoder) : (0, double.NaN);
        var interval = PaceInterval(framesLeft, secondsLeft, BudgetFps, FullSpeedFps);
        if (interval <= 0)
            return;

        var now = Stopwatch.GetTimestamp();
        var step = (long)(interval * Stopwatch.Frequency);
        long slot;
        if (interval < 1 / BudgetFps - 1e-9)
        {
            slot = Math.Max(now, _ownNext);
            _ownNext = slot + step;
        }
        else
        {
            slot = TakeSlot(ref _paceNext, now, step);
        }

        var wait = (slot - now) / (double)Stopwatch.Frequency;
        if (wait > 0.0002)
            PaceWait(Math.Min(wait, MostPaceWait));
    }

    /// <summary>
    /// True when a visible or play-ahead frame waits on this lane's read (MostUrgent deferred it to this reader via ReadByAnotherLane,
    /// e.g. a pre-roll reading through the playhead when play began). Such reads run unpaced until past what playback needs (lane thread).
    /// </summary>
    private bool ServesPlayback(Demand demand, MfVideoDecoder decoder)
    {
        lock (_gate)
        {
            foreach (var d in _demands)
            {
                if (d.Kind is DemandKind.Visible or DemandKind.PlayAhead && d.Lane < 0 && !d.Done && !d.Cancelled && d.Source == demand.Source
                    && !ReferenceEquals(d, demand) && Delivers(demand, decoder, d.Target))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the next shared slot (any thread, lock-free): max(now, next), advancing next by <paramref name="step"/>. Lanes taking turns
    /// decode at the budget rate combined, not each.
    /// </summary>
    internal static long TakeSlot(ref long next, long now, long step)
    {
        long seen, slot;
        do
        {
            seen = Volatile.Read(ref next);
            slot = Math.Max(now, seen);
        }
        while (Interlocked.CompareExchange(ref next, slot + step, seen) != seen);
        return slot;
    }

    /// <summary>Frames left to decode up to a cut's target and seconds until its time at the play rate (lane thread).</summary>
    private (double Frames, double Seconds) CutLeft(Demand demand, MfVideoDecoder decoder)
    {
        var read = decoder.ReadTime;
        var from = double.IsNaN(read) ? decoder.KeyAtOrBefore(demand.KeepFrom) : read;
        if (double.IsNaN(from))
            from = demand.KeepFrom - 4;
        var frames = Math.Max(demand.Target - from, 0) / Math.Max(decoder.FrameDuration, 1e-3);
        var seconds = (demand.At - _lastPlayhead) / Math.Max(Volatile.Read(ref _playRate), 0.25);
        return (frames, seconds);
    }

    /// <summary>Sleeps the lane on its high-resolution timer (Thread.Sleep fallback), waking early when the pool stops.</summary>
    private unsafe void PaceWait(double seconds)
    {
        if (!ReferenceEquals(_paceTimerPool, this) || _paceTimer == 0)
        {
            _paceTimerPool = this;
            _paceTimer = Kernel32.CreateWaitableTimerExW(0, 0, Kernel32.HighResolutionTimer, Kernel32.TimerAllAccess);
            if (_paceTimer != 0)
            {
                lock (_paceTimers)
                    _paceTimers.Add(_paceTimer);
            }
        }

        if (_paceTimer == 0)
        {
            Thread.Sleep(Math.Max((int)Math.Round(seconds * 1000), 1));
            return;
        }

        var due = -(long)(seconds * 10_000_000);
        if (Kernel32.SetWaitableTimer(_paceTimer, &due, 0, 0, 0, 0) == 0)
        {
            Thread.Sleep(Math.Max((int)Math.Round(seconds * 1000), 1));
            return;
        }

        var handles = stackalloc nint[2];
        handles[0] = _paceTimer;
        handles[1] = _stopping.SafeWaitHandle.DangerousGetHandle();
        Kernel32.WaitForMultipleObjects(2, handles, 0, (uint)Math.Ceiling(seconds * 1000) + 20);
    }

    /// <summary>Closes the lanes' timers (Dispose, after the lanes have stopped).</summary>
    private void ClosePaceTimers()
    {
        lock (_paceTimers)
        {
            foreach (var timer in _paceTimers)
                Kernel32.CloseHandle(timer);
            _paceTimers.Clear();
        }
    }
}
