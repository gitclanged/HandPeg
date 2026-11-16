using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandPegApp.Controls;

/// <summary>
/// Shared pacing primitives for the interface thread, so a component never hand-rolls a DispatcherTimer plus a "queued" or "owed" flag:
/// <list type="bullet">
/// <item><see cref="TimerScope"/>: a component's timers, made on first use (nothing before Frame 1) and stopped together.</item>
/// <item><see cref="Debounce"/>: one run after a quiet period (trailing edge).</item>
/// <item><see cref="RunGate"/>: one queued run at a dispatcher priority however often it is requested before it runs.</item>
/// <item><see cref="WriteThrottle{TKey}"/>: writes at most once a period, the first at once, the newest per key kept.</item>
/// <item><see cref="CaptureThrottle"/>: a pointer drag on a slider or number box as an editing gesture of a <see cref="GestureWriter{TKey}"/>.</item>
/// </list>
/// </summary>
internal static class Pacing
{
    /// <summary>
    /// The most writes a second while an editing gesture is under way: each write is a new scene plan for the preview and a new command,
    /// and a pointer reports far more often than that.
    /// </summary>
    public const double GestureWritesPerSecond = 30;

    /// <summary>The period between two paced gesture writes.</summary>
    public static readonly TimeSpan GesturePeriod = TimeSpan.FromSeconds(1 / GestureWritesPerSecond);
}

/// <summary>A component's DispatcherTimers: each made on first use (Law 1), all stopped together when the component detaches.</summary>
internal sealed class TimerScope : IDisposable
{
    private readonly List<DispatcherTimer> _timers = [];

    /// <summary>A stopped timer owned by this scope.</summary>
    public DispatcherTimer Make(TimeSpan interval, DispatcherPriority priority, EventHandler tick)
    {
        var timer = new DispatcherTimer(priority) { Interval = interval };
        timer.Tick += tick;
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Stops every timer of the scope (each can start again).</summary>
    public void StopAll()
    {
        foreach (var timer in _timers)
            timer.Stop();
    }

    public void Dispose() => StopAll();
}

/// <summary>Runs an action once, <c>delay</c> after the last <see cref="Poke"/> (a burst of pokes is one run at its end).</summary>
internal sealed class Debounce(TimeSpan delay, Action action, DispatcherPriority priority = DispatcherPriority.Background, TimerScope? scope = null)
{
    private DispatcherTimer? _timer;

    /// <summary>Whether a run is waiting for the quiet period to end.</summary>
    public bool IsPending => _timer?.IsEnabled == true;

    /// <summary>Starts (or restarts) the quiet period.</summary>
    public void Poke()
    {
        var timer = _timer ??= Make();
        timer.Stop();
        timer.Start();
    }

    /// <summary>Runs a waiting run now.</summary>
    public void Flush()
    {
        if (!IsPending)
            return;
        _timer!.Stop();
        action();
    }

    /// <summary>Drops a waiting run.</summary>
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

/// <summary>One queued run of an action on a dispatcher, however many times it is requested before it runs (a redraw or rebuild owed).</summary>
internal sealed class RunGate(Dispatcher dispatcher, Action action, DispatcherPriority priority)
{
    /// <summary>Whether a run is queued and has not started yet.</summary>
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
/// Paces writes: a write queued when none was made within the period runs at once; later ones wait for the next tick, the newest per
/// key replacing an older one of that key (written in the order the keys were first queued). Every batch runs through
/// <c>around</c>, which wraps it (an undo step, a look batch) and calls the batch.
/// </summary>
internal sealed class WriteThrottle<TKey>(Action<Action> around, TimeSpan period, DispatcherPriority priority = DispatcherPriority.Input, TimerScope? scope = null)
    where TKey : notnull
{
    private readonly Dictionary<TKey, Action> _owed = [];
    private DispatcherTimer? _timer;
    private long _lastBatch = long.MinValue / 2;

    /// <summary>Whether a write of <paramref name="key"/> is waiting.</summary>
    public bool IsOwed(TKey key) => _owed.ContainsKey(key);

    /// <summary>Whether any write is waiting.</summary>
    public bool AnyOwed => _owed.Count > 0;

    /// <summary>Queues a write: now when nothing was written within the period, else at the next tick (the newest of a key wins).</summary>
    public void Queue(TKey key, Action write)
    {
        _owed[key] = write;
        if (_timer is { IsEnabled: true })
            return;
        if (Environment.TickCount64 - _lastBatch >= period.TotalMilliseconds)
            Flush();
        (_timer ??= MakeTimer()).Start();
    }

    /// <summary>Runs every waiting write now, as one batch.</summary>
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

    /// <summary>Drops every waiting write and stops the timer.</summary>
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
/// A pointer drag on a slider, its thumb, a repeat button or a number box, as an editing gesture of a <see cref="GestureWriter{TKey}"/>:
/// <see cref="Follow"/> (called as a value arrives) opens the writer's gesture while the left button holds such an element's mouse
/// capture, and the element losing the capture ends it (its last value written as part of the gesture).
/// </summary>
internal sealed class CaptureThrottle(IGestureWriter writer)
{
    private UIElement? _captured;

    /// <summary>Opens a gesture when the pointer is dragging a value control and none is open for it.</summary>
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
