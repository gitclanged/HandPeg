using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandPegApp.Controls;

/// <summary>
/// Shared UI-thread pacing primitives, replacing hand-rolled DispatcherTimer + "queued"/"owed" flag pairs:
/// <list type="bullet">
/// <item><see cref="TimerScope"/>: a component's timers, created lazily (nothing before Frame 1) and stopped together.</item>
/// <item><see cref="Debounce"/>: one trailing-edge run after a quiet period.</item>
/// <item><see cref="RunGate"/>: at most one queued run at a dispatcher priority, however often requested.</item>
/// <item><see cref="WriteThrottle{TKey}"/>: at most one batch per period, leading edge immediate, latest write per key kept.</item>
/// <item><see cref="CaptureThrottle"/>: maps a pointer drag on a value control to a <see cref="GestureWriter{TKey}"/> gesture.</item>
/// </list>
/// </summary>
internal static class Pacing
{
    /// <summary>
    /// Maximum writes per second during an editing gesture: each write builds a new preview scene plan and command, while pointer
    /// input arrives far more often.
    /// </summary>
    public const double GestureWritesPerSecond = 30;

    /// <summary>Minimum interval between paced gesture writes.</summary>
    public static readonly TimeSpan GesturePeriod = TimeSpan.FromSeconds(1 / GestureWritesPerSecond);
}

/// <summary>Owns a component's DispatcherTimers: created lazily (Law 1), stopped together when the component detaches.</summary>
internal sealed class TimerScope : IDisposable
{
    private readonly List<DispatcherTimer> _timers = [];

    /// <summary>Creates a stopped timer owned by this scope.</summary>
    public DispatcherTimer Make(TimeSpan interval, DispatcherPriority priority, EventHandler tick)
    {
        var timer = new DispatcherTimer(priority) { Interval = interval };
        timer.Tick += tick;
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Stops all scope timers (they remain restartable).</summary>
    public void StopAll()
    {
        foreach (var timer in _timers)
            timer.Stop();
    }

    public void Dispose() => StopAll();
}

/// <summary>Runs an action once, <c>delay</c> after the last <see cref="Poke"/> (a burst collapses to one trailing run).</summary>
internal sealed class Debounce(TimeSpan delay, Action action, DispatcherPriority priority = DispatcherPriority.Background, TimerScope? scope = null)
{
    private DispatcherTimer? _timer;

    /// <summary>True while a run is pending the end of the quiet period.</summary>
    public bool IsPending => _timer?.IsEnabled == true;

    /// <summary>Starts (or restarts) the quiet period.</summary>
    public void Poke()
    {
        var timer = _timer ??= Make();
        timer.Stop();
        timer.Start();
    }

    /// <summary>Runs the pending action immediately, if any.</summary>
    public void Flush()
    {
        if (!IsPending)
            return;
        _timer!.Stop();
        action();
    }

    /// <summary>Cancels the pending run.</summary>
    public void Cancel() => _timer?.Stop();

    private DispatcherTimer Make()
    {
        void Tick(object? sender, EventArgs e)
        {
            _timer!.Stop();
            action();
        }

        return scope?.Make(delay, priority, Tick) ?? MakeOwn(Tick);
    }

    private DispatcherTimer MakeOwn(EventHandler tick)
    {
        var timer = new DispatcherTimer(priority) { Interval = delay };
        timer.Tick += tick;
        return timer;
    }
}

/// <summary>Coalesces requests into at most one queued dispatcher run (e.g. a pending redraw or rebuild).</summary>
internal sealed class RunGate(Dispatcher dispatcher, Action action, DispatcherPriority priority)
{
    /// <summary>True while a run is queued and not yet started.</summary>
    public bool IsQueued { get; private set; }

    /// <summary>Queues the run unless one is queued already.</summary>
    public void Request()
    {
        if (IsQueued)
            return;
        IsQueued = true;
        dispatcher.BeginInvoke(() =>
        {
            IsQueued = false;
            action();
        }, priority);
    }
}

/// <summary>
/// Write throttle: a write queued after an idle period flushes immediately; later ones wait for the next tick, the latest per key
/// replacing older ones (flushed in first-queued key order). Each batch runs inside <c>around</c>, which wraps it (an undo step, a
/// look batch) and invokes it.
/// </summary>
internal sealed class WriteThrottle<TKey>(Action<Action> around, TimeSpan period, DispatcherPriority priority = DispatcherPriority.Input, TimerScope? scope = null)
    where TKey : notnull
{
    private readonly Dictionary<TKey, Action> _owed = [];
    private DispatcherTimer? _timer;
    private long _lastBatch = long.MinValue / 2;

    /// <summary>True when a write for <paramref name="key"/> is pending.</summary>
    public bool IsOwed(TKey key) => _owed.ContainsKey(key);

    /// <summary>True when any write is pending.</summary>
    public bool AnyOwed => _owed.Count > 0;

    /// <summary>Queues a write: flushed now if the last batch is older than the period, else on the next tick (latest per key wins).</summary>
    public void Queue(TKey key, Action write)
    {
        _owed[key] = write;
        if (_timer is { IsEnabled: true })
            return;
        if (Environment.TickCount64 - _lastBatch >= period.TotalMilliseconds)
            Flush();
        (_timer ??= MakeTimer()).Start();
    }

    /// <summary>Flushes all pending writes now as one batch.</summary>
    public void Flush()
    {
        if (_owed.Count == 0)
            return;
        var batch = _owed.Values.ToArray();
        _owed.Clear();
        _lastBatch = Environment.TickCount64;
        around(() =>
        {
            foreach (var write in batch)
                write();
        });
    }

    /// <summary>Discards all pending writes and stops the timer.</summary>
    public void Cancel()
    {
        _owed.Clear();
        _timer?.Stop();
    }

    private DispatcherTimer MakeTimer()
    {
        void Tick(object? sender, EventArgs e)
        {
            if (_owed.Count > 0)
                Flush();
            else
                _timer!.Stop();
        }

        if (scope is not null)
            return scope.Make(period, priority, Tick);
        var timer = new DispatcherTimer(priority) { Interval = period };
        timer.Tick += Tick;
        return timer;
    }
}

/// <summary>
/// Maps a pointer drag on a slider, thumb, repeat button or number box to a <see cref="GestureWriter{TKey}"/> gesture:
/// <see cref="Follow"/> (called per incoming value) opens the gesture while the left button holds such an element's mouse capture;
/// losing capture ends it, with the final value written inside the gesture.
/// </summary>
internal sealed class CaptureThrottle(IGestureWriter writer)
{
    private UIElement? _captured;

    /// <summary>Opens a gesture when the pointer is dragging a value control and none is open yet.</summary>
    public void Follow()
    {
        if (_captured is not null || Mouse.LeftButton != MouseButtonState.Pressed || Mouse.Captured is not UIElement captured
            || captured is not (Thumb or Slider or RepeatButton or TextBoxBase))
            return;

        _captured = captured;
        captured.LostMouseCapture += Lost;
        writer.Begin();
    }

    private void Lost(object sender, MouseEventArgs e)
    {
        if (_captured is null)
            return;
        _captured.LostMouseCapture -= Lost;
        _captured = null;
        writer.End();
    }
}
