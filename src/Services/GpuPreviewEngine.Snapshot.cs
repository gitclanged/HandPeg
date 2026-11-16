using System.Runtime.CompilerServices;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services;

// ----- Immediate on-screen snapshot -----
// When a tab is backgrounded without a settled playhead still (playing, or not yet settled), the pump reads back the displayed frame
// on its device, cropped to the picture when fully visible and decimated to near the requested short side. The UI waits a few frames max.
public sealed partial class GpuPreviewEngine
{
    internal sealed class SnapshotRequest(int shortSide)
    {
        public readonly int ShortSide = shortSide;
        public readonly ManualResetEventSlim Done = new(false);
        public (byte[] Bgra, int Width, int Height)? Frame;

        // Soft (scaled) frame read once while awaiting the full one (pump thread), used if the full frame is late. Boxed for cross-thread
        // publication.
        public StrongBox<(byte[] Bgra, int Width, int Height)>? Soft;
        public bool SoftTried;
    }

    private SnapshotRequest? _snapshot;

    // Newest published scene drawn scaled (Dynamic Preview Scaling; pump thread); full stills wait while it may be on screen.
    private long _scaledSequence;

    /// <summary>
    /// True when a requested still may be read now: immediately while playing; when paused, only after a full-resolution frame newer than
    /// the last scaled one is on screen, so soft stills are kept only if the full one is late. Meanwhile the soft frame is read once as a
    /// fallback; the UI's timeout still applies (it withdraws and keeps the soft frame).
    /// </summary>
    internal static bool StillMayBeRead(bool playing, long presentedSequence, long scaledSequence) => playing || presentedSequence > scaledSequence;

    /// <summary>
    /// Current on-screen frame near <paramref name="shortSide"/> (never upscaled): full frame if read within <paramref name="timeoutMs"/>,
    /// else the soft frame, else null. UI thread; the pump performs the readback.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? SnapshotPresented(int shortSide, int timeoutMs) => SnapshotPresented(shortSide, timeoutMs, out _);

    /// <summary><see cref="SnapshotPresented(int, int)"/> that also reports whether the result is the soft frame (keep only until a full
    /// one is available).</summary>
    public (byte[] Bgra, int Width, int Height)? SnapshotPresented(int shortSide, int timeoutMs, out bool soft)
    {
        soft = false;
        if (_viewport.Presents == 0)
            return null;
        var request = new SnapshotRequest(shortSide);
        Volatile.Write(ref _snapshot, request);
        _dirty.Set();
        return AwaitStill(ref _snapshot, request, timeoutMs, out soft);
    }

    /// <summary>
    /// Waits at most <paramref name="timeoutMs"/> (UI thread) for <paramref name="request"/>: its frame, else the soft frame, else null. On
    /// timeout it is withdrawn from <paramref name="slot"/> if not yet taken (the event is not disposed: the pump may still set it).
    /// </summary>
    internal static (byte[] Bgra, int Width, int Height)? AwaitStill(ref SnapshotRequest? slot, SnapshotRequest request, int timeoutMs) =>
        AwaitStill(ref slot, request, timeoutMs, out _);

    /// <summary>As above, reporting <paramref name="soft"/> (false when nothing is returned).</summary>
    internal static (byte[] Bgra, int Width, int Height)? AwaitStill(ref SnapshotRequest? slot, SnapshotRequest request, int timeoutMs, out bool soft)
    {
        if (!request.Done.Wait(timeoutMs))
        {
            Interlocked.CompareExchange(ref slot, null, request);
            var late = Volatile.Read(ref request.Soft)?.Value;
            soft = late is not null;
            return late;
        }

        request.Done.Dispose();
        if (request.Frame is { } whole)
        {
            soft = false;
            return whole;
        }

        var fallback = Volatile.Read(ref request.Soft)?.Value;
        soft = fallback is not null;
        return fallback;
    }

    /// <summary>A snapshot asked for, taken (pump thread).</summary>
    private void TakeSnapshot()
    {
        // Read the request before player state so a preceding Pause is observed.
        if (Volatile.Read(ref _snapshot) is not { } asked)
            return;
        if (!StillMayBeRead(_playing, _viewport.PresentedSequence, _scaledSequence))
        {
            // Read the soft frame once and attach it: a slow Sequence may not render full-size within the UI wait, and the tab must never
            // be left without a still.
            if (!asked.SoftTried)
            {
                asked.SoftTried = true;
                if (StillOnScreen(asked.ShortSide) is { } soft)
                    Volatile.Write(ref asked.Soft, new StrongBox<(byte[] Bgra, int Width, int Height)>(soft));
            }

            return;
        }

        if (Interlocked.Exchange(ref _snapshot, null) is not { } request)
            return;
        try
        {
            request.Frame = StillOnScreen(request.ShortSide);
        }
        finally
        {
            request.Done.Set();
        }
    }

    /// <summary>Reads back the displayed frame near <paramref name="shortSide"/>; null when none or on failure (logged). Pump thread.</summary>
    private (byte[] Bgra, int Width, int Height)? StillOnScreen(int shortSide)
    {
        try
        {
            // Crop to the picture when fully visible (unzoomed) so the still matches a captured canvas still.
            var canvas = _shownCanvas;
            (int, int, int, int) Region(int width, int height)
            {
                var (left, top, w, h) = PictureRectOf(canvas.Width, canvas.Height, _viewScale, _viewPanX, _viewPanY, width, height);
                return left >= -0.5 && top >= -0.5 && left + w <= width + 0.5 && top + h <= height + 0.5
                    ? ((int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(left + w), (int)Math.Round(top + h))
                    : (0, 0, width, height);
            }

            int Step(int width, int height) => Math.Max(Math.Min(width, height) / Math.Max(shortSide, 1), 1);
            return _viewport.TryReadStill(Region, Step, out var bgra, out var stillWidth, out var stillHeight) ? (bgra, stillWidth, stillHeight) : null;
        }
        catch (Exception ex) when (ex is GpuException or InvalidOperationException)
        {
            AppLog.Write("GPU viewport: the picture on screen could not be read back", ex);
            return null;
        }
    }
}
