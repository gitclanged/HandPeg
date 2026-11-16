using System.IO;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

/// <summary>
/// Off-Sequence file stills for the Media Browser preview and cutter via the thumbnail compositor (<see cref="GpuFilmstrips.TryStillAsync"/>:
/// key frame while dragging, exact frame at rest), decoded off the UI thread. Latest-wins: one in flight, newer requests replace the
/// waiting one (which completes null). Paused while drag-and-drop holds background work (<see cref="WorkScheduler.IsHeld"/>). No FFmpeg
/// fallback: the existing coarse strip remains when the compositor cannot draw.
/// </summary>
public sealed class SourceFrames
{
    /// <summary>How long a drag rests before its exact frame is asked for, ms (until then, key frames).</summary>
    public const int ExactAfterMs = 120;

    // How often a request waiting for a drag and drop to end looks again, ms.
    private const int HeldPollMs = 100;

    private readonly Func<string, double, int, bool, CancellationToken, Task<BitmapSource?>> _fetch;
    private readonly object _gate = new();
    private Request? _waiting;
    private bool _running;

    private sealed record Request(string Path, double Seconds, int Height, bool Fast, CancellationToken Token)
    {
        public TaskCompletionSource<BitmapSource?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <param name="fetch">Still producer (goldens inject one); defaults to the thumbnail compositor.</param>
    public SourceFrames(Func<string, double, int, bool, CancellationToken, Task<BitmapSource?>>? fetch = null) => _fetch = fetch ?? StillAsync;

    /// <summary>
    /// Frozen still of <paramref name="path"/> at <paramref name="mediaSeconds"/> (MediaOffset clock), <paramref name="pixelHeight"/> px tall;
    /// null on failure, cancel or supersession.
    /// </summary>
    /// <param name="fast">Nearest key frame suffices (pointer still moving).</param>
    public Task<BitmapSource?> FrameAsync(string path, double mediaSeconds, int pixelHeight, bool fast, CancellationToken token)
    {
        var request = new Request(path, Math.Max(double.IsFinite(mediaSeconds) ? mediaSeconds : 0, 0), Math.Clamp(pixelHeight, 16, 2160), fast, token);
        Request? replaced;
        var start = false;
        lock (_gate)
        {
            (replaced, _waiting) = (_waiting, request);
            if (!_running)
                (_running, start) = (true, true);
        }

        replaced?.Done.TrySetResult(null);
        if (start)
            _ = Task.Run(PumpAsync);
        return request.Done.Task;
    }

    /// <summary>Drops any waiting request (file changed or preview closed).</summary>
    public void Cancel()
    {
        Request? replaced;
        lock (_gate)
            (replaced, _waiting) = (_waiting, null);
        replaced?.Done.TrySetResult(null);
    }

    // One request at a time, the newest waiting next, until none waits.
    private async Task PumpAsync()
    {
        while (true)
        {
            Request? next;
            bool held;
            lock (_gate)
            {
                if (_waiting is null)
                {
                    _running = false;
                    return;
                }

                // Read the hold once per decision: either leave the request waiting or dequeue and serve it, never dequeue then skip.
                held = WorkScheduler.IsHeld;
                next = _waiting;
                if (!held)
                    _waiting = null;
            }

            // A drag and drop holds background work: the request waits (a newer one may take its place meanwhile).
            if (held)
            {
                await Task.Delay(HeldPollMs).ConfigureAwait(false);
                continue;
            }

            if (next.Token.IsCancellationRequested)
            {
                next.Done.TrySetResult(null);
                continue;
            }

            try
            {
                next.Done.TrySetResult(await _fetch(next.Path, next.Seconds, next.Height, next.Fast, next.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                next.Done.TrySetResult(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                AppLog.Write($"Media browser: a still of {Path.GetFileName(next.Path)} could not be made", ex);
                next.Done.TrySetResult(null);
            }
        }
    }

    /// <summary>
    /// Converts MediaOffset time to file seek time for <see cref="GpuFilmstrips.TryStillAsync"/>, adding the video-vs-container start offset
    /// (as MainViewModel.FileMomentAt).
    /// </summary>
    public static double StillMoment(double mediaSeconds, double? videoStart, double? containerStart) =>
        Math.Max(mediaSeconds + (videoStart is { } video && containerStart is { } container ? Math.Max(video - container, 0) : 0), 0);

    private static string Folder => Path.Combine(SessionPaths.Root, "browser-scrub");

    /// <summary>Renders a still via the thumbnail compositor to a session JPEG, decodes it and deletes the file.</summary>
    private static async Task<BitmapSource?> StillAsync(string path, double mediaSeconds, int height, bool fast, CancellationToken token)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"{Guid.NewGuid():N}.jpg");
        try
        {
            var at = StillMoment(mediaSeconds, MediaProbe.KnownVideoStart(path), MediaProbe.KnownContainerStart(path));
            if (!await GpuFilmstrips.TryStillAsync(path, file, at, height, onAKeyFrame: fast, token).ConfigureAwait(false) || !File.Exists(file))
                return null;

            return MediaBrowserFrames.Picture(file, height);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
