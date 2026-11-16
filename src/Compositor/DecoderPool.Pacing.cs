using System.Diagnostics;
using System.Globalization;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.Native;

namespace HandPegApp.Compositor;

// ----- Pacing while playing, by a rate budget -----
// The Desktop's 500 Hz on-screen trace (AvSync session --onscreen, 2026-11-13): while playing, a background chase decoding 200 frames a
// second or more backed the card up, and the editor's swap chain waited 20-100 ms for its turn (Present to vblank) in 54-100 % of 100 ms
// windows; under 100 frames a second, in 2 %. The 5.0 rule (decode for a slice after each frame drawn, then wait for the next) let a
// chase run at 200-760 fps. So work ahead while playing now shares one budget of frames a second across the lanes (BudgetFps), and a
// cut that cannot make its moment within the budget is given the rate it needs (a quarter over it), or the card's full speed when only
// that makes it. Its cuts are also read earlier on a card of its own (CutReadAhead), so the budget is enough for most of them.
internal sealed partial class DecoderPool
{
    /// <summary>Work ahead's shared budget while playing, frames a second (the trace: under 100 the turns kept to their refresh).</summary>
    internal const double PaceBudgetFps = 100;

    /// <summary>The seconds a cut's chase keeps in hand before its moment, as its rate is worked out.</summary>
    internal const double PaceMargin = 0.25;

    /// <summary>How long a lane waits for its slot at most before it looks again (a lane's demand may have been cancelled meanwhile).</summary>
    private const double MostPaceWait = 0.1;

    /// <summary>How far ahead (sequence seconds, times the play rate) the hints name cuts: GpuHintWriter.Horizon.</summary>
    internal const double CutReadAheadSeconds = 3.0;

    /// <summary>
    /// The budget in effect: <see cref="PaceBudgetFps"/>, or HANDPEG_DECODE_BUDGET_FPS (read once: the A/B runs' knob, inherited by the
    /// worker from the editor); 0 or less brings back the 5.0 slices.
    /// </summary>
    internal static readonly double BudgetFps = ReadBudget();

    // The shared budget's next slot (QPC ticks); a lane's own last frame when its cut is paced outside the budget.
    private long _paceNext;
    [ThreadStatic] private static long _ownNext;

    // Each lane's high-resolution waitable timer (Thread.Sleep and Monitor.Wait wake on the 15.6 ms tick here), closed with the pool.
    [ThreadStatic] private static nint _paceTimer;
    [ThreadStatic] private static DecoderPool? _paceTimerPool;
    private readonly List<nint> _paceTimers = [];

    private static double ReadBudget()
    {
        var text = Environment.GetEnvironmentVariable("HANDPEG_DECODE_BUDGET_FPS");
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && double.IsFinite(fps) ? fps : PaceBudgetFps;
    }

    /// <summary>
    /// The seconds between a demand's frames while playing (pure): the budget's (1 / <paramref name="budgetFps"/>) for work with no cut to
    /// make; for a cut <paramref name="framesLeft"/> frames from its first, <paramref name="secondsLeft"/> seconds away, the rate it needs
    /// (keeping <see cref="PaceMargin"/> in hand) times 1.25 when that is more than the budget; 0 (the full speed) when it needs at least
    /// 80 % of <paramref name="fullSpeedFps"/>, or its margin is gone.
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
    /// How near (sequence seconds) a cut into a file the playback reads may be before a decoder is opened or sought for it (pure): on
    /// unified memory <see cref="CutSoon"/> (the Adreno's one decoder lock); on a card of its own as far as the hints reach
    /// (<see cref="CutReadAheadSeconds"/> times the play rate), so a chase has the time the budget needs.
    /// </summary>
    internal static double CutReadAhead(bool unifiedMemory, double rate) =>
        unifiedMemory ? CutSoon : Math.Max(CutSoon, CutReadAheadSeconds * Math.Max(rate, 1));

    /// <summary>
    /// Waits, if it must, before a lane decodes its next frame (a lane's own thread, after each frame): only while playing, for work ahead
    /// (never what is shown or read ahead of it, nor a read such a frame waits on, never an export's exact frames). Work with the budget's
    /// interval takes the shared budget's next slot; a cut paced faster keeps its own; the full speed waits for nothing.
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
    /// Whether a frame shown or read ahead waits on this lane's read: MostUrgent passed it over for this reader
    /// (ReadByAnotherLane: a pre-roll that was on a lane when play began, reading up through the playhead). The read then runs unpaced,
    /// as the playback's own would; once the reader is past what the playback waits for, the budget applies again (a lane's thread).
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
    /// Takes the shared budget's next slot (any thread): the later of now and the slot after the last one taken, the one after it moved on by
    /// <paramref name="step"/>. Two lanes taking slots in turn decode at the budget's rate together, not each.
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

    /// <summary>A cut's frames still to decode to the end of its stretch, and the seconds until its moment at the play rate (a lane's thread).</summary>
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

    /// <summary>Sleeps a lane for so many seconds on its high-resolution timer (Thread.Sleep without one), waking early when the pool stops.</summary>
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

    /// <summary>The lanes' timers closed (Dispose, after the lanes stopped).</summary>
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
