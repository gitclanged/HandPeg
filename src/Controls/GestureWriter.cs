using System.Windows.Threading;
using HandPegApp.ViewModels;

namespace HandPegApp.Controls;

/// <summary>A gesture that <see cref="CaptureThrottle"/> (or a control's own press and release) opens and ends.</summary>
internal interface IGestureWriter
{
    void Begin();

    void End();
}

/// <summary>
/// One pane's editing writes (the Effects pane's rows, sound filters and pitch; the Color Studio's grade), the single implementation of
/// gesture pacing and undo grouping:
/// <list type="bullet">
/// <item>Writes are queued per key and paced by a <see cref="WriteThrottle{TKey}"/> (<see cref="Pacing.GestureWritesPerSecond"/>).</item>
/// <item>Each batch runs as one look batch (<see cref="MainViewModel.BatchLookWrites"/>: TimelineChanged and the command once) owned by
/// the pane (<see cref="MainViewModel.OwnLookScope"/>: the view model takes no look step of its own for what the batch writes).</item>
/// <item>A write takes its undo step through <see cref="MainViewModel.StepForLook"/> with <see cref="Gesture"/>: one step for a whole
/// gesture (<see cref="Begin"/> to <see cref="End"/>, nesting), or the view model's run rule for a lone write.</item>
/// </list>
/// </summary>
internal sealed class GestureWriter<TKey> : IGestureWriter where TKey : notnull
{
    private readonly Func<MainViewModel?> _viewModel;
    private readonly Action<Action>? _around;
    private readonly WriteThrottle<TKey> _throttle;
    private int _depth;

    /// <param name="viewModel">The view model written to (null: writes run without a batch or undo step).</param>
    /// <param name="scope">The owner's timers (stopped together at detach).</param>
    /// <param name="around">The owner's own wrap of each batch, inside the look batch (its "writing" flag, a refresh after it).</param>
    public GestureWriter(Func<MainViewModel?> viewModel, TimerScope? scope = null, Action<Action>? around = null)
    {
        (_viewModel, _around) = (viewModel, around);
        _throttle = new WriteThrottle<TKey>(RunBatch, Pacing.GesturePeriod, DispatcherPriority.Input, scope);
    }

    /// <summary>Whether a gesture is open.</summary>
    public bool InGesture => _depth > 0;

    /// <summary>The open gesture's identity (one undo step per identity), or null between gestures.</summary>
    public MainViewModel.EditGesture? Gesture { get; private set; }

    /// <summary>Raised when the outermost gesture ends, after its last values were written.</summary>
    public event Action? Ended;

    /// <summary>Opens a gesture (nests): everything written until the outermost <see cref="End"/> is one undo step.</summary>
    public void Begin()
    {
        if (_depth++ == 0)
            Gesture = _viewModel()?.NewGesture();
    }

    /// <summary>Ends a gesture; the outermost writes what is still waiting first, so it belongs to the gesture's step.</summary>
    public void End()
    {
        if (_depth == 0)
            return;
        if (_depth == 1)
            _throttle.Flush();
        if (--_depth > 0)
            return;
        Gesture = null;
        Ended?.Invoke();
    }

    /// <summary>Whether a write of <paramref name="key"/> is waiting.</summary>
    public bool IsOwed(TKey key) => _throttle.IsOwed(key);

    /// <summary>Whether any write is waiting.</summary>
    public bool AnyOwed => _throttle.AnyOwed;

    /// <summary>Queues a write (see <see cref="WriteThrottle{TKey}.Queue"/>).</summary>
    public void Queue(TKey key, Action write) => _throttle.Queue(key, write);

    /// <summary>Writes everything waiting now (the target is about to change, a dialog opens).</summary>
    public void Flush() => _throttle.Flush();

    private void RunBatch(Action batch)
    {
        var run = _around is { } around ? () => around(batch) : batch;
        if (_viewModel() is not { } viewModel)
        {
            run();
            return;
        }

        using (viewModel.BatchLookWrites())
        using (viewModel.OwnLookScope())
            run();
    }
}
