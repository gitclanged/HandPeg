using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Momentum for a drag or a wheel along one axis (5.4C; CLAUDE.md 10.2): a released flick coasts on with v(t) = v0 * d^t (t in
/// milliseconds) and slows to a stop. Two guards keep careful work still: a release slower than 250 DIP/s stops dead, and so does one
/// after the pointer rested 40 ms or more (or a wheel stream that paused that long). Any press, or a wheel tick against the coast,
/// stops it at once. Pure: the host passes the time (<see cref="Now"/>, the one clock for input and ticks) and applies what
/// <see cref="Wheel"/> and <see cref="Tick"/> return; nothing here hooks a frame loop.
/// </summary>
/// <remarks>
/// The release velocity is the drag's endpoints over the last <see cref="WindowMs"/> of samples (deterministic, and steady enough at
/// a mouse's rate). The coast is worked out in closed form from the time since it began, x(t) = v0 / k * (1 - d^t) with k = -ln d, so
/// it covers the same ground at 60 Hz as at 500 Hz; one <see cref="Tick"/> never advances it more than <see cref="MaxStepMs"/> (a stalled
/// frame never makes a jump), and it ends below <see cref="StopSpeed"/>. A drag's coast decays at <see cref="DragDecayPerMs"/>, a wheel
/// stream's at <see cref="WheelDecayPerMs"/> (both starting points, to tune live). Ticks and samples allocate nothing.
/// </remarks>
public sealed class MomentumScroller
{
    /// <summary>DIP/s: a release slower than this gives no momentum.</summary>
    public const double FlickThreshold = 250;

    /// <summary>A rest this long (ms) before letting go, or between wheel ticks, gives no momentum.</summary>
    public const double DetentionMs = 40;

    /// <summary>The span of input (ms) the release velocity is measured over.</summary>
    public const double WindowMs = 60;

    /// <summary>d for a drag's coast, per millisecond: the coast goes on for v0 * 499.5 ms's worth of ground.</summary>
    public const double DragDecayPerMs = 0.998;

    /// <summary>d for a wheel stream's coast, per millisecond: a short tail of v0 * 99.5 ms's worth.</summary>
    public const double WheelDecayPerMs = 0.99;

    /// <summary>DIP/s: no coast starts faster than this.</summary>
    public const double MaxSpeed = 8000;

    /// <summary>DIP/s: a coast slower than this is over.</summary>
    public const double StopSpeed = 20;

    /// <summary>The most one <see cref="Tick"/> advances the coast (ms), however long it has been since the last.</summary>
    public const double MaxStepMs = 50;

    /// <summary>A sample that moved less than this (DIPs) from the last move is a hand at rest: it does not count as moving.</summary>
    public const double StillDip = 0.5;

    // 64 ms of samples at a 1 kHz mouse; a faster stream measures over the 64 it keeps.
    private const int Capacity = 64;

    private readonly Func<bool> _enabled;

    // The drag's (or the wheel stream's) samples: a ring, oldest at _next once full, and the same in order for measuring.
    private readonly (double P, double T)[] _samples = new (double, double)[Capacity];
    private readonly (double P, double T)[] _ordered = new (double, double)[Capacity];
    private int _count, _next;

    // When and where the pointer last moved (NaN: not since the press).
    private double _lastMoveTime = double.NaN, _lastMovePosition;

    // The coast: its starting speed (DIP/ms), k = -ln d, how long it has run (ms), the time of the last tick, the ground covered.
    private double _v0, _k, _coastMs, _tickTime, _travelled;

    // The wheel stream: where it has got to (DIPs), when its last tick came (NaN: none), and what the coast covered since.
    private double _wheelPosition, _lastWheel = double.NaN, _coastedSinceWheel;

    /// <param name="enabled">Whether momentum is on; by default <see cref="AppSettings.EnableMomentumScrolling"/>, read at each release.</param>
    public MomentumScroller(Func<bool>? enabled = null) => _enabled = enabled ?? (() => AppSettings.Current.EnableMomentumScrolling);

    /// <summary>The clock for samples and ticks, in milliseconds (Stopwatch; never an input event's timestamp).</summary>
    public static double Now() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Whether a coast is under way.</summary>
    public bool IsCoasting { get; private set; }

    /// <summary>The coast's speed now, DIP/s along the axis (0 when not coasting).</summary>
    public double Velocity { get; private set; }

    /// <summary>The d of the coast under way (or the last one), per millisecond.</summary>
    internal double DecayPerMs => Math.Exp(-_k);

    /// <summary>How long the coast under way has run, in ms of its own time (a stalled tick counts at most <see cref="MaxStepMs"/>).</summary>
    internal double CoastMs => _coastMs;

    /// <summary>The ground the coast under way has covered, in DIPs.</summary>
    internal double Travelled => _travelled;

    /// <summary>A press: stops any coast and begins a new gesture.</summary>
    public void Press(double timeMs)
    {
        Stop();
        Clear();
        _lastWheel = double.NaN;
    }

    /// <summary>Where the drag is now along the axis, in DIPs (cumulative; the same sign as the offset it drives).</summary>
    public void Sample(double position, double timeMs)
    {
        if (!double.IsFinite(position) || !double.IsFinite(timeMs))
            return;

        // Time never runs back within a gesture.
        if (_count > 0)
            timeMs = Math.Max(timeMs, _samples[(_next + Capacity - 1) % Capacity].T);

        // A move of less than half a DIP (WPF's move as capture is taken, a hand's tremor) is not a move: the rest goes on ageing.
        if (double.IsNaN(_lastMoveTime) || Math.Abs(position - _lastMovePosition) >= StillDip)
            (_lastMoveTime, _lastMovePosition) = (timeMs, position);

        _samples[_next] = (position, timeMs);
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    /// <summary>The drag let go: the velocity the coast starts at (DIP/s), or 0 when it is too slow, rested, or momentum is off.</summary>
    public double Release(double timeMs)
    {
        var velocity = 0.0;
        if (_enabled() && _count >= 2 && timeMs - _lastMoveTime < DetentionMs)
        {
            velocity = VelocityOf(Ordered());
            if (Math.Abs(velocity) < FlickThreshold)
                velocity = 0;
        }

        Clear();
        if (velocity != 0)
            Begin(velocity, timeMs, DragDecayPerMs);
        else
            Stop();
        return velocity;
    }

    /// <summary>A wheel tick of <paramref name="delta"/> DIPs: returns what the host applies now (a tick against the coast stops it and
    /// returns 0); a fast stream starts a coast.</summary>
    /// <remarks>
    /// A tick 40 ms or more after the last begins a fresh stream (no momentum from what came before). While the stream's own coast runs
    /// the same way, a tick gives only what the coast has not already covered since the last tick: the coast pre-pays the notch, so it
    /// smooths between notches without adding a second helping to a fast spin. Two or more ticks under 40 ms apart at 250 DIP/s or more
    /// start (or carry on) the coast at the stream's speed. With momentum off a tick is applied as it is and never coasts.
    /// </remarks>
    public double Wheel(double delta, double timeMs)
    {
        if (delta == 0 || !double.IsFinite(delta) || !double.IsFinite(timeMs))
            return 0;

        if (!_enabled())
        {
            Stop();
            _lastWheel = double.NaN;
            return delta;
        }

        // Against the coast: it stops, and the tick that stopped it is spent doing so.
        if (IsCoasting && Math.Sign(delta) != Math.Sign(Velocity))
        {
            Stop();
            Clear();
            _lastWheel = double.NaN;
            return 0;
        }

        if (double.IsNaN(_lastWheel) || timeMs - _lastWheel >= DetentionMs)
            Clear();

        var apply = IsCoasting ? Math.Sign(delta) * Math.Max(0, Math.Abs(delta) - Math.Abs(_coastedSinceWheel)) : delta;
        _coastedSinceWheel = 0;
        _lastWheel = timeMs;
        _wheelPosition += delta;
        Sample(_wheelPosition, timeMs);
        if (_count >= 2)
        {
            var velocity = VelocityOf(Ordered());
            if (Math.Abs(velocity) >= FlickThreshold && Math.Sign(velocity) == Math.Sign(delta))
                Begin(velocity, timeMs, WheelDecayPerMs);
        }

        return apply;
    }

    /// <summary>The coast's travel since the previous tick, in DIPs (0 when not coasting).</summary>
    public double Tick(double timeMs)
    {
        if (!IsCoasting)
            return 0;

        var step = Math.Min(timeMs - _tickTime, MaxStepMs);
        if (!(step > 0))
            return 0;

        _tickTime = timeMs;
        _coastMs += step;
        var left = Math.Exp(-_k * _coastMs);
        var at = _v0 / _k * (1 - left);
        var moved = at - _travelled;
        _travelled = at;
        _coastedSinceWheel += moved;
        var speed = _v0 * left * 1000;
        if (Math.Abs(speed) < StopSpeed)
            Stop();
        else
            Velocity = speed;
        return moved;
    }

    /// <summary>Stops the coast.</summary>
    public void Stop() => (IsCoasting, Velocity) = (false, 0);

    /// <summary>
    /// The velocity (DIP/s, signed) of samples in time order: the newest against the oldest within <see cref="WindowMs"/> of it, held to
    /// <see cref="MaxSpeed"/>; 0 for fewer than two, or less than a millisecond between them. No threshold is applied here.
    /// </summary>
    internal static double VelocityOf(ReadOnlySpan<(double P, double T)> samples)
    {
        if (samples.Length < 2)
            return 0;

        var last = samples[^1];
        var first = last;
        for (var i = samples.Length - 2; i >= 0 && samples[i].T >= last.T - WindowMs; i--)
            first = samples[i];

        var span = last.T - first.T;
        if (span < 1)
            return 0;

        return Math.Clamp((last.P - first.P) / span * 1000, -MaxSpeed, MaxSpeed);
    }

    private ReadOnlySpan<(double P, double T)> Ordered()
    {
        var oldest = _count < Capacity ? 0 : _next;
        for (var i = 0; i < _count; i++)
            _ordered[i] = _samples[(oldest + i) % Capacity];
        return _ordered.AsSpan(0, _count);
    }

    private void Clear()
    {
        (_count, _next) = (0, 0);
        _lastMoveTime = double.NaN;
        _wheelPosition = 0;
    }

    private void Begin(double velocity, double timeMs, double decayPerMs)
    {
        velocity = Math.Clamp(velocity, -MaxSpeed, MaxSpeed);
        (_v0, _k, _coastMs, _tickTime, _travelled, _coastedSinceWheel) = (velocity / 1000, -Math.Log(decayPerMs), 0, timeMs, 0, 0);
        (IsCoasting, Velocity) = (true, velocity);
    }
}

/// <summary>
/// Momentum for a list's wheel (5.4C): attached to an ItemsControl or ScrollViewer, a fast wheel coasts its ScrollViewer on vertically
/// through a <see cref="MomentumScroller"/>; any press, any key, and the list unloaded or hidden stop it.
/// </summary>
/// <remarks>
/// Attaching only adds a few handlers (no tree walk, nothing allocated until the first wheel), so a pane may attach in its constructor
/// before Frame 1. The ScrollViewer is found at the first wheel (the host itself, or the first in its template). A notch moves
/// 16 DIPs per line of Windows' wheel setting (48 at the usual 3, as the tile panel's own wheel did), and the offset aimed at is kept
/// here, because the ScrollViewer's own catches up only at the next layout and quick notches would otherwise be lost. Only the plain
/// wheel coasts: with Ctrl, Shift or Alt held (or the wheel set to scroll by pages) the wheel passes on untouched and any coast stops.
/// CompositionTarget.Rendering is hooked only while a coast runs and the list is shown.
/// </remarks>
public static class ScrollMomentum
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollMomentum), new PropertyMetadata(false, OnIsEnabledChanged));

    // The host's state, made at its first wheel.
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ScrollMomentum), new PropertyMetadata(null));

    private static readonly MouseButtonEventHandler PressHandler = (sender, _) => StopSender(sender);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>Gives <paramref name="host"/> wheel momentum (the same as setting <see cref="IsEnabledProperty"/>).</summary>
    public static void Attach(Control host) => SetIsEnabled(host, true);

    /// <summary>Stops any coast on <paramref name="host"/> (a host's own scrolling, say the middle-click auto-scroll, takes over).</summary>
    public static void Stop(Control host)
    {
        if (host.GetValue(StateProperty) is not State state)
            return;

        state.Scroller.Stop();
        state.LastWheel = double.NaN;
        Unhook(state);
    }

    // One host's coast: its scroller, its ScrollViewer, the offset aimed at, when its last wheel came, and its frame hook.
    private sealed class State(Control host)
    {
        public Control Host { get; } = host;
        public MomentumScroller Scroller { get; } = new();
        public ScrollViewer? Viewer { get; set; }
        public double Target { get; set; }
        public double LastWheel { get; set; } = double.NaN;
        public EventHandler? Frame { get; set; }
        public bool Hooked { get; set; }
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control host)
            return;

        // Off first in either case, so setting it twice never adds the handlers twice.
        host.PreviewMouseWheel -= OnWheel;
        host.RemoveHandler(UIElement.PreviewMouseDownEvent, PressHandler);
        host.PreviewKeyDown -= OnKey;
        host.Unloaded -= OnUnloaded;
        host.IsVisibleChanged -= OnVisibleChanged;
        if (!(bool)e.NewValue)
        {
            Stop(host);
            host.ClearValue(StateProperty);
            return;
        }

        host.PreviewMouseWheel += OnWheel;
        host.AddHandler(UIElement.PreviewMouseDownEvent, PressHandler, handledEventsToo: true);
        host.PreviewKeyDown += OnKey;
        host.Unloaded += OnUnloaded;
        host.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Control host || e.Handled)
            return;

        if (Keyboard.Modifiers != ModifierKeys.None || SystemParameters.WheelScrollLines <= 0)
        {
            Stop(host);
            return;
        }

        if (!double.IsNaN(Wheel(host, e.Delta, MomentumScroller.Now())))
            e.Handled = true;
    }

    private static void OnKey(object sender, KeyEventArgs e) => StopSender(sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => StopSender(sender);

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
            StopSender(sender);
    }

    private static void StopSender(object sender)
    {
        if (sender is Control host)
            Stop(host);
    }

    /// <summary>
    /// A plain wheel of <paramref name="delta"/> (120 a notch, up positive) over <paramref name="host"/> at <paramref name="nowMs"/>: the
    /// DIPs its ScrollViewer was moved down by now, or NaN when the wheel is not taken (no ScrollViewer, or nothing to scroll), so it
    /// passes on. Test seam; the wheel handler calls it.
    /// </summary>
    internal static double Wheel(Control host, int delta, double nowMs)
    {
        if (host.GetValue(StateProperty) is not State state)
            host.SetValue(StateProperty, state = new State(host));

        // A template applied again makes a new ScrollViewer: the one kept is looked for again.
        if (state.Viewer is { } kept && !ReferenceEquals(kept, host) && !kept.IsDescendantOf(host))
            state.Viewer = null;
        var viewer = state.Viewer ??= host as ScrollViewer ?? FindViewer(host);
        if (viewer is null || viewer.ScrollableHeight <= 0)
            return double.NaN;

        // At rest, the aim starts from where the list is (the scroll bar or the keys may have moved it since).
        if (!state.Scroller.IsCoasting && !(nowMs - state.LastWheel < MomentumScroller.DetentionMs))
            state.Target = viewer.VerticalOffset;
        state.LastWheel = nowMs;

        var step = 16.0 * Math.Max(SystemParameters.WheelScrollLines, 1);
        var wanted = state.Scroller.Wheel(-delta / 120.0 * step, nowMs);
        var moved = MoveBy(state, viewer, wanted);
        if (wanted != 0 && moved == 0)
            state.Scroller.Stop();
        if (state.Scroller.IsCoasting)
            Hook(state);
        else
            Unhook(state);
        return moved;
    }

    /// <summary>One frame of <paramref name="host"/>'s coast at <paramref name="nowMs"/>: whether its list moved. An end of the list stops the
    /// coast, and so does the coast running out (the frame hook goes with it). Test seam; the frame hook calls it.</summary>
    internal static bool Step(Control host, double nowMs)
    {
        if (host.GetValue(StateProperty) is not State state)
            return false;

        var moved = false;
        if (state.Viewer is { } viewer)
        {
            var travel = state.Scroller.Tick(nowMs);
            moved = travel != 0 && MoveBy(state, viewer, travel) != 0;
            if (travel != 0 && !moved)
                state.Scroller.Stop();
        }
        else
        {
            state.Scroller.Stop();
        }

        if (!state.Scroller.IsCoasting)
            Unhook(state);
        return moved;
    }

    /// <summary>Whether <paramref name="host"/>'s list is coasting (test seam).</summary>
    internal static bool IsCoasting(Control host) => host.GetValue(StateProperty) is State { Scroller.IsCoasting: true };

    /// <summary>The offset <paramref name="host"/>'s list is aimed at (test seam; NaN before its first wheel).</summary>
    internal static double Target(Control host) => host.GetValue(StateProperty) is State state ? state.Target : double.NaN;

    /// <summary>Whether <paramref name="host"/>'s coast has the frame hooked (test seam).</summary>
    internal static bool IsHooked(Control host) => host.GetValue(StateProperty) is State { Hooked: true };

    // Moves the aim by so many DIPs, held within the list: how far it actually went.
    private static double MoveBy(State state, ScrollViewer viewer, double by)
    {
        var to = Math.Clamp(state.Target + by, 0, Math.Max(viewer.ScrollableHeight, 0));
        var moved = to - state.Target;
        state.Target = to;
        if (moved != 0)
            viewer.ScrollToVerticalOffset(to);
        return moved;
    }

    private static void Hook(State state)
    {
        if (state.Hooked || !state.Host.IsVisible)
            return;

        state.Frame ??= (_, _) => Step(state.Host, MomentumScroller.Now());
        CompositionTarget.Rendering += state.Frame;
        state.Hooked = true;
    }

    private static void Unhook(State state)
    {
        if (!state.Hooked)
            return;

        CompositionTarget.Rendering -= state.Frame;
        state.Hooked = false;
    }

    // The first ScrollViewer under the host, nearest first.
    private static ScrollViewer? FindViewer(DependencyObject host)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(host);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(next); i++)
            {
                var child = VisualTreeHelper.GetChild(next, i);
                if (child is ScrollViewer viewer)
                    return viewer;
                queue.Enqueue(child);
            }
        }

        return null;
    }
}
