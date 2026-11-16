using System.Diagnostics;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Wheel glide: each notch adds to a target (pan in pixels, zoom factor about a point) that the view chases on the frame clock with an
/// exponential approach settling in ~160 ms (tau 40 ms), so discrete wheel steps read as one smooth motion and zoom stays pinned to the
/// pointer. Hooks CompositionTarget.Rendering only while motion remains; <see cref="Stop"/> (a press, another pan taking over) halts in
/// place. Hidden controls (hidden panes, golden-test controls) apply changes immediately.
/// </summary>
internal sealed class WheelGlide(Action<double> pan, Action<double, double> zoom, Func<bool> shown)
{
    private const double Tau = 0.040, PanDone = 0.5, ZoomDone = 1e-3;

    // Remaining motion: pan in pixels and zoom as a natural log factor about _zoomAt (pixels from the time area's left edge).
    private double _panLeft, _zoomLeft, _zoomAt;
    private long _clock;
    private bool _hooked;
    private TimeSpan _lastRendering;

    /// <summary>True while pan or zoom motion remains.</summary>
    public bool IsGliding => _panLeft != 0 || _zoomLeft != 0;

    /// <summary>Adds a pan of <paramref name="pixels"/> (positive = later) to the remaining motion.</summary>
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

    /// <summary>Adds a zoom by <paramref name="factor"/> about <paramref name="at"/> (the latest anchor wins).</summary>
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

    /// <summary>Stops the pan in place (zoom continues).</summary>
    public void StopPan()
    {
        _panLeft = 0;
        if (!IsGliding)
            Unhook();
    }

    /// <summary>Stops pan and zoom in place.</summary>
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
        // Rendering can fire more than once per frame; dedupe on RenderingTime.
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
