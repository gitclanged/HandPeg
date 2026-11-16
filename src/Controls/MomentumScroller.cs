using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Single-axis momentum for drags and wheels: a released flick coasts with v(t) = v0 * d^t (t in ms) to a stop. Precision guards: releases
/// under 250 DIP/s, or after a 40 ms rest (or a 40 ms wheel pause), do not coast. Any press, or a wheel tick against the coast, stops it.
/// Pure: the host supplies time (<see cref="Now"/>, the single clock for input and ticks) and applies the deltas returned by
/// <see cref="Wheel"/> and <see cref="Tick"/>; no frame loop is hooked here.
/// </summary>
/// <remarks>
/// Release velocity uses the endpoints of the last <see cref="WindowMs"/> of samples (deterministic and stable at mouse rates). The coast
/// is closed form, x(t) = v0 / k * (1 - d^t) with k = -ln d, so distance is frame-rate independent (60 Hz = 500 Hz). A <see cref="Tick"/>
/// advances at most <see cref="MaxStepMs"/> (no jump after a stalled frame) and the coast ends below <see cref="StopSpeed"/>. Drags decay
/// at <see cref="DragDecayPerMs"/>, wheel streams at <see cref="WheelDecayPerMs"/> (tunable defaults). Ticks and samples are allocation-free.
/// </remarks>
public sealed class MomentumScroller
{
    /// <summary>Minimum release speed (DIP/s) for momentum.</summary>
    public const double FlickThreshold = 250;

    /// <summary>Rest before release, or gap between wheel ticks (ms), that cancels momentum.</summary>
    public const double DetentionMs = 40;

    /// <summary>Sample window (ms) for release velocity.</summary>
    public const double WindowMs = 60;

    /// <summary>Drag decay d per ms; total coast distance = v0 * 499.5 ms.</summary>
    public const double DragDecayPerMs = 0.998;

    /// <summary>Wheel decay d per ms; short tail of v0 * 99.5 ms.</summary>
    public const double WheelDecayPerMs = 0.99;

    /// <summary>Maximum initial coast speed (DIP/s).</summary>
    public const double MaxSpeed = 8000;

    /// <summary>Speed (DIP/s) below which a coast ends.</summary>
    public const double StopSpeed = 20;

    /// <summary>Maximum coast advance per <see cref="Tick"/> (ms), regardless of elapsed time.</summary>
    public const double MaxStepMs = 50;

    /// <summary>Movement (DIPs) below which a sample counts as at rest.</summary>
    public const double StillDip = 0.5;

    // Ring capacity: 64 ms at 1 kHz; faster streams measure over the last 64 samples.
    private const int Capacity = 64;

    private readonly Func<bool> _enabled;

    // Sample ring (oldest at _next once full) plus a scratch buffer in time order for measuring.
    private readonly (double P, double T)[] _samples = new (double, double)[Capacity];
    private readonly (double P, double T)[] _ordered = new (double, double)[Capacity];
    private int _count, _next;

    // Time and position of the last real move (NaN: none since press).
    private double _lastMoveTime = double.NaN, _lastMovePosition;

    // Coast state: v0 (DIP/ms), k = -ln d, elapsed coast ms, last tick time, distance covered.
    private double _v0, _k, _coastMs, _tickTime, _travelled;

    // Wheel stream: cumulative position (DIPs), last tick time (NaN: none), coast distance since that tick.
    private double _wheelPosition, _lastWheel = double.NaN, _coastedSinceWheel;

    /// <param name="enabled">Momentum enabled predicate; defaults to <see cref="AppSettings.EnableMomentumScrolling"/>, read per release.</param>
    public MomentumScroller(Func<bool>? enabled = null) => _enabled = enabled ?? (() => AppSettings.Current.EnableMomentumScrolling);

    /// <summary>Stopwatch clock in ms for samples and ticks (never input event timestamps).</summary>
    public static double Now() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>True while coasting.</summary>
    public bool IsCoasting { get; private set; }

    /// <summary>Current signed coast speed (DIP/s); 0 when idle.</summary>
    public double Velocity { get; private set; }

    /// <summary>Decay d per ms of the current (or last) coast.</summary>
    internal double DecayPerMs => Math.Exp(-_k);

    /// <summary>Coast-local elapsed ms (each stalled tick contributes at most <see cref="MaxStepMs"/>).</summary>
    internal double CoastMs => _coastMs;

    /// <summary>Distance covered by the current coast (DIPs).</summary>
    internal double Travelled => _travelled;

    /// <summary>Press: stops any coast and starts a new gesture.</summary>
    public void Press(double timeMs)
    {
        Stop();
        Clear();
        _lastWheel = double.NaN;
    }

    /// <summary>Records a drag sample: cumulative axis position in DIPs (same sign as the driven offset).</summary>
    public void Sample(double position, double timeMs)
    {
        if (!double.IsFinite(position) || !double.IsFinite(timeMs))
            return;

        // Enforce monotonic time within a gesture.
        if (_count > 0)
            timeMs = Math.Max(timeMs, _samples[(_next + Capacity - 1) % Capacity].T);

        // Sub-StillDip moves (capture's synthetic move, tremor) do not reset the rest timer.
        if (double.IsNaN(_lastMoveTime) || Math.Abs(position - _lastMovePosition) >= StillDip)
            (_lastMoveTime, _lastMovePosition) = (timeMs, position);

        _samples[_next] = (position, timeMs);
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    /// <summary>Release: returns the coast's start velocity (DIP/s), or 0 if too slow, rested, or momentum is disabled.</summary>
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

    /// <summary>Wheel tick of <paramref name="delta"/> DIPs: returns the amount for the host to apply now (0 when an opposing tick stops
    /// the coast); fast streams start a coast.</summary>
    /// <remarks>
    /// A tick >= 40 ms after the previous starts a fresh stream. While a same-direction coast runs, a tick applies only the part the coast
    /// has not already covered since the last tick (the coast pre-pays the notch), smoothing between notches without double-counting.
    /// Two or more ticks under 40 ms apart at >= 250 DIP/s start or continue the coast at stream speed. Momentum off: applied as-is, no coast.
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

        // Opposing tick: stop the coast and consume the tick.
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

    /// <summary>Advances the coast; returns DIPs travelled since the previous tick (0 when idle).</summary>
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
    /// Signed velocity (DIP/s) from time-ordered samples: newest vs oldest within <see cref="WindowMs"/>, clamped to <see cref="MaxSpeed"/>;
    /// 0 for fewer than two samples or a span under 1 ms. No flick threshold applied.
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
/// Attached vertical wheel momentum for an ItemsControl or ScrollViewer via <see cref="MomentumScroller"/>; any press, key, unload or
/// hide stops the coast.
/// </summary>
/// <remarks>
/// Attaching only adds handlers (no tree walk or allocation until the first wheel), so panes may attach in constructors before Frame 1.
/// The ScrollViewer is resolved at the first wheel (the host or the first in its template). A notch moves 16 DIPs per system wheel line
/// (48 at the default 3). The target offset is tracked here because ScrollViewer.VerticalOffset only updates at the next layout and rapid
/// notches would otherwise be lost. Only the plain wheel coasts: modifiers or page scrolling pass through and stop any coast.
/// CompositionTarget.Rendering is hooked only while coasting and visible.
/// </remarks>
public static class ScrollMomentum
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollMomentum), new PropertyMetadata(false, OnIsEnabledChanged));

    // Per-host state, created at the first wheel.
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ScrollMomentum), new PropertyMetadata(null));

    private static readonly MouseButtonEventHandler PressHandler = (sender, _) => StopSender(sender);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>Enables wheel momentum on <paramref name="host"/> (same as setting <see cref="IsEnabledProperty"/>).</summary>
    public static void Attach(Control host) => SetIsEnabled(host, true);

    /// <summary>Stops any coast on <paramref name="host"/> (e.g. when its own middle-click auto-scroll takes over).</summary>
    public static void Stop(Control host)
    {
        if (host.GetValue(StateProperty) is not State state)
            return;

        state.Scroller.Stop();
        state.LastWheel = double.NaN;
        Unhook(state);
    }

    // Per-host coast: scroller, ScrollViewer, target offset, last wheel time, and frame hook.
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

        // Always unsubscribe first so repeated sets never double-subscribe.
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
    /// Test seam (called by the wheel handler): plain wheel <paramref name="delta"/> (120/notch, up positive) on <paramref name="host"/> at
    /// <paramref name="nowMs"/>. Returns DIPs scrolled down now, or NaN when not handled (no ScrollViewer or nothing to scroll).
    /// </summary>
    internal static double Wheel(Control host, int delta, double nowMs)
    {
        if (host.GetValue(StateProperty) is not State state)
            host.SetValue(StateProperty, state = new State(host));

        // Re-templating creates a new ScrollViewer: re-resolve if the cached one is detached.
        if (state.Viewer is { } kept && !ReferenceEquals(kept, host) && !kept.IsDescendantOf(host))
            state.Viewer = null;
        var viewer = state.Viewer ??= host as ScrollViewer ?? FindViewer(host);
        if (viewer is null || viewer.ScrollableHeight <= 0)
            return double.NaN;

        // At rest, resync the target to the actual offset (scroll bar or keys may have moved it).
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

    /// <summary>Test seam (called per frame): advances <paramref name="host"/>'s coast at <paramref name="nowMs"/>; returns whether it moved.
    /// Hitting either end, or the coast expiring, stops it and unhooks the frame.</summary>
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

    /// <summary>Test seam: true while <paramref name="host"/> is coasting.</summary>
    internal static bool IsCoasting(Control host) => host.GetValue(StateProperty) is State { Scroller.IsCoasting: true };

    /// <summary>Test seam: target offset for <paramref name="host"/> (NaN before the first wheel).</summary>
    internal static double Target(Control host) => host.GetValue(StateProperty) is State state ? state.Target : double.NaN;

    /// <summary>Test seam: true while <paramref name="host"/>'s frame hook is attached.</summary>
    internal static bool IsHooked(Control host) => host.GetValue(StateProperty) is State { Hooked: true };

    // Moves the target by DIPs clamped to the scroll range; returns the actual movement.
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

    // Breadth-first search for the nearest descendant ScrollViewer.
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
