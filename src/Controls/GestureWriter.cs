using System.Windows.Threading;
using HandPegApp.ViewModels;

namespace HandPegApp.Controls;

/// <summary>Gesture sink opened and closed by <see cref="CaptureThrottle"/> or a control's own press/release.</summary>
internal interface IGestureWriter
{
    void Begin();

    void End();
}

/// <summary>
/// Per-pane edit writer (Effects pane rows, audio filters and pitch, Color Studio grade): the single implementation of gesture pacing
/// and undo grouping:
/// <list type="bullet">
/// <item>Writes are queued per key and paced by a <see cref="WriteThrottle{TKey}"/> (<see cref="Pacing.GestureWritesPerSecond"/>).</item>
/// <item>Each batch runs as one look batch (<see cref="MainViewModel.BatchLookWrites"/>: one TimelineChanged and one command) owned by
/// the pane (<see cref="MainViewModel.OwnLookScope"/>: the view model records no look step of its own for the batch's writes).</item>
/// <item>Writes record undo via <see cref="MainViewModel.StepForLook"/> with <see cref="Gesture"/>: one step per gesture
/// (<see cref="Begin"/> to <see cref="End"/>, nestable), or the view model's run-coalescing rule for a lone write.</item>
/// </list>
/// </summary>
internal sealed class GestureWriter<TKey> : IGestureWriter where TKey : notnull
{
    private readonly Func<MainViewModel?> _viewModel;
    private readonly Action<Action>? _around;
    private readonly WriteThrottle<TKey> _throttle;
    private int _depth;

    /// <param name="viewModel">Target view model (null: writes run without a batch or undo step).</param>
    /// <param name="scope">Owner's timer scope (stopped together on detach).</param>
    /// <param name="around">Owner wrapper per batch, inside the look batch (e.g. a "writing" flag and a post-refresh).</param>
    public GestureWriter(Func<MainViewModel?> viewModel, TimerScope? scope = null, Action<Action>? around = null)
    {
        (_viewModel, _around) = (viewModel, around);
        _throttle = new WriteThrottle<TKey>(RunBatch, Pacing.GesturePeriod, DispatcherPriority.Input, scope);
    }

    /// <summary>True while a gesture is open.</summary>
    public bool InGesture => _depth > 0;

    /// <summary>Open gesture identity (one undo step per identity); null between gestures.</summary>
    public MainViewModel.EditGesture? Gesture { get; private set; }

    /// <summary>Raised when the outermost gesture ends, after its final writes are flushed.</summary>
    public event Action? Ended;

    /// <summary>Opens a gesture (nestable); all writes until the outermost <see cref="End"/> form one undo step.</summary>
    public void Begin()
    {
        if (_depth++ == 0)
            Gesture = _viewModel()?.NewGesture();
    }

    /// <summary>Ends a gesture; the outermost end flushes pending writes first so they join the gesture's step.</summary>
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

    /// <summary>True when a write for <paramref name="key"/> is pending.</summary>
    public bool IsOwed(TKey key) => _throttle.IsOwed(key);

    /// <summary>True when any write is pending.</summary>
    public bool AnyOwed => _throttle.AnyOwed;

    /// <summary>Queues a write (see <see cref="WriteThrottle{TKey}.Queue"/>).</summary>
    public void Queue(TKey key, Action write) => _throttle.Queue(key, write);

    /// <summary>Flushes pending writes now (e.g. before a target change or a dialog opens).</summary>
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
