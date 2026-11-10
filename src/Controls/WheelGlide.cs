using System.Diagnostics;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// The wheel's glide (5.4.1-M1): a notch sets a target (a pan in pixels, a zoom as a factor about a point) and the view chases it on the
/// frame clock, an exponential chase that settles in about 160 ms (tau 40 ms), so a mouse wheel's steps read as one smooth motion and a
/// zoom stays pinned to the pointer. Hooked on CompositionTarget.Rendering only while something is left to go; <see cref="Stop"/> (a
/// press, another pan taking over) ends it where it is. A control not shown (a hidden pane, a golden test's control) gets it at once.
/// </summary>
internal sealed class WheelGlide(Action<double> pan, Action<double, double> zoom, Func<bool> shown)
{
    private const double Tau = 0.040, PanDone = 0.5, ZoomDone = 1e-3;

    // What is left to go: pixels of pan, the natural log of the zoom (about _zoomAt, pixels from the time area's left).
    private double _panLeft, _zoomLeft, _zoomAt;
    private long _clock;
    private bool _hooked;
    private TimeSpan _lastRendering;

    /// <summary>Whether a pan or a zoom is still on its way.</summary>
    public bool IsGliding => _panLeft != 0 || _zoomLeft != 0;

    /// <summary>A pan of <paramref name="pixels"/> (positive: later) added to what is left to go.</summary>
    public void Pan(double pixels)
    {
        if (!double.IsFinite(pixels) || pixels == 0)
            return;
        if (!shown())
        {
            pan(pixels);
            return;
        }

        _panLeft += pixels;
        Hook();
    }

    /// <summary>A zoom by <paramref name="factor"/> about <paramref name="at"/> added to what is left to go (the newest point pins it).</summary>
    public void Zoom(double factor, double at)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor == 1)
            return;
        if (!shown())
        {
            zoom(factor, at);
            return;
        }

        _zoomLeft += Math.Log(factor);
        _zoomAt = at;
        Hook();
    }

    /// <summary>Ends the pan where it is (the zoom goes on).</summary>
    public void StopPan()
    {
        _panLeft = 0;
        if (!IsGliding)
            Unhook();
    }

    /// <summary>Ends both where they are.</summary>
    public void Stop()
    {
        (_panLeft, _zoomLeft) = (0, 0);
        Unhook();
    }

    private void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        _clock = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += Tick;
    }

    private void Unhook()
    {
        if (!_hooked)
            return;
        _hooked = false;
        CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, EventArgs e)
    {
        // Rendering can come more than once for one frame: the frame's own time tells.
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRendering)
                return;
            _lastRendering = rendering.RenderingTime;
        }

        if (!shown())
        {
            Stop();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0, Math.Min((now - _clock) / (double)Stopwatch.Frequency, 0.05));
        _clock = now;
        var share = 1 - Math.Exp(-seconds / Tau);
        if (_panLeft != 0)
        {
            var step = Math.Abs(_panLeft) < PanDone ? _panLeft : _panLeft * share;
            _panLeft -= step;
            pan(step);
        }

        if (_zoomLeft != 0)
        {
            var step = Math.Abs(_zoomLeft) < ZoomDone ? _zoomLeft : _zoomLeft * share;
            _zoomLeft -= step;
            zoom(Math.Exp(step), _zoomAt);
        }

        if (!IsGliding)
            Unhook();
    }
}
