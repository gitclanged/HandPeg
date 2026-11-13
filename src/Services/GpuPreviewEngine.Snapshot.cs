using System.Runtime.CompilerServices;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services;

// ----- The picture on the screen read back at once (Pass 5.4.3) -----
// A project tab put behind with no still of its playhead (it was playing, or the still had not settled yet) keeps the frame the player
// shows as it goes: the pump reads it back on its own device (the viewport's reader is the pump's), cropped to the picture when the whole
// picture is in view, every n-th pixel so its short side is near the size asked. The UI thread waits a few frames at most, never more.
public sealed partial class GpuPreviewEngine
{
    internal sealed class SnapshotRequest(int shortSide)
    {
        public readonly int ShortSide = shortSide;
        public readonly ManualResetEventSlim Done = new(false);
        public (byte[] Bgra, int Width, int Height)? Frame;

        // The frame drawn smaller on the screen while the whole one is awaited (5.4.4), read once (pump thread): what the UI keeps when the
        // whole frame is not read in time, never nothing instead. Boxed: the pump writes it while the UI thread may read it.
        public StrongBox<(byte[] Bgra, int Width, int Height)>? Soft;
        public bool SoftTried;
    }

    private SnapshotRequest? _snapshot;

    // The newest scene published drawn smaller (Dynamic Preview Scaling; pump thread): no still is read while it may be the one on screen.
    private long _scaledSequence;

    /// <summary>
    /// Whether the still asked for is read from the screen now (5.4.4): playing, as it is; paused, only once the frame on the screen is newer
    /// than the last one drawn smaller (the paused turn draws it whole first: a play stopped for a tab switch, a scrub just over), so a tab
    /// put behind keeps a soft still only when the whole one is late. Meanwhile the request waits for that turn with the soft frame read once
    /// (its fallback); the UI thread's own bound still holds (it withdraws the request and keeps the soft frame).
    /// </summary>
    internal static bool StillMayBeRead(bool playing, long presentedSequence, long scaledSequence) => playing || presentedSequence > scaledSequence;

    /// <summary>
    /// The frame on the screen now, its short side near <paramref name="shortSide"/> (never upscaled): the whole frame when it is read within
    /// <paramref name="timeoutMs"/>, else the one drawn smaller read meanwhile, else null (nothing presented yet, the pump busy). UI thread;
    /// the pump does the readback.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? SnapshotPresented(int shortSide, int timeoutMs) => SnapshotPresented(shortSide, timeoutMs, out _);

    /// <summary>As <see cref="SnapshotPresented(int, int)"/>, saying whether what it returns is the soft frame (drawn smaller: 5.4.4), which the
    /// caller keeps only until a whole one can be read.</summary>
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
    /// What <paramref name="request"/> gets within <paramref name="timeoutMs"/>, never longer (UI thread, 5.4.4): the frame read for it, else
    /// the soft one read while the whole one was awaited (not read in time, or not read), else null. Not taken in time, it is withdrawn from
    /// <paramref name="slot"/> if the pump has not taken it yet (the event is left to the collector: the pump may set it still).
    /// </summary>
    internal static (byte[] Bgra, int Width, int Height)? AwaitStill(ref SnapshotRequest? slot, SnapshotRequest request, int timeoutMs) =>
        AwaitStill(ref slot, request, timeoutMs, out _);

    /// <summary>As above, saying whether what it returns is the soft frame (<paramref name="soft"/>: never when nothing is returned).</summary>
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
        // The request read before the player's state: a Pause made before it was asked is seen (5.4.4).
        if (Volatile.Read(ref _snapshot) is not { } asked)
            return;
        if (!StillMayBeRead(_playing, _viewport.PresentedSequence, _scaledSequence))
        {
            // The frame drawn smaller read once meanwhile (5.4.4), left with the request: a Sequence slow enough to be drawn smaller may
            // take longer to draw whole than the UI waits, and the tab then keeps this still (as before 5.4.4), never an uncovered player.
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

    /// <summary>The frame on the screen now, its short side near <paramref name="shortSide"/>; null when there is none or it is not read (logged). Pump thread.</summary>
    private (byte[] Bgra, int Width, int Height)? StillOnScreen(int shortSide)
    {
        try
        {
            // The picture's own rectangle when all of it is in view (unzoomed): the still is then the canvas's, as a captured still is.
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
            AppLog.Write("GPU viewport: the picture on screen could not be read back: " + ex.Message);
            return null;
        }
    }
}
