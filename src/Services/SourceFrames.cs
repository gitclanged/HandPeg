using System.IO;
using System.Windows.Media.Imaging;

namespace HandPegApp.Services;

/// <summary>
/// The frames of one file that is not on the Sequence, for the Media Browser's preview and cutter (5.4C): the still at a moment, drawn by
/// the thumbnail compositor (<see cref="GpuFilmstrips.TryStillAsync"/>: a key frame while the pointer drags, the exact frame once it
/// rests), decoded off the interface's thread. Latest wins: one still is made at a time, and a request that comes while one is being
/// made takes the place of any that waited (its task ends with null), so a scrub never queues a frame for every move. Nothing is asked
/// for while a drag and drop holds background work (<see cref="WorkScheduler.IsHeld"/>). No FFmpeg: where the compositor draws nothing
/// (a file it cannot decode, the GPU off for background work), the coarse strip the browser already has stays the picture.
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

    /// <param name="fetch">What makes one still (the golden tests give their own); by default the thumbnail compositor's.</param>
    public SourceFrames(Func<string, double, int, bool, CancellationToken, Task<BitmapSource?>>? fetch = null) => _fetch = fetch ?? StillAsync;

    /// <summary>
    /// The frame of <paramref name="path"/> at <paramref name="mediaSeconds"/> (a clip's MediaOffset clock), <paramref name="pixelHeight"/>
    /// pixels tall, frozen; null when it could not be made, was cancelled, or a newer request took its place.
    /// </summary>
    /// <param name="fast">A key frame near the moment is enough (the pointer is still moving).</param>
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

    /// <summary>Lets go of the request waiting, if any (the file shown changed, or the preview closed).</summary>
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
            lock (_gate)
            {
                if (_waiting is null)
                {
                    _running = false;
                    return;
                }

                next = _waiting;
                if (!WorkScheduler.IsHeld)
                    _waiting = null;
            }

            // A drag and drop holds background work: the request waits (a newer one may take its place meanwhile).
            if (WorkScheduler.IsHeld)
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
                AppLog.Write($"Media browser: a still of {Path.GetFileName(next.Path)} could not be made: {ex.Message}");
                next.Done.TrySetResult(null);
            }
        }
    }

    /// <summary>
    /// The moment to ask the compositor for, on the file's own clock as it seeks (<see cref="GpuFilmstrips.TryStillAsync"/>), for a moment
    /// of a clip's MediaOffset clock: later by how far the video starts after the container does (as MainViewModel.FileMomentAt shifts it).
    /// </summary>
    public static double StillMoment(double mediaSeconds, double? videoStart, double? containerStart) =>
        Math.Max(mediaSeconds + (videoStart is { } video && containerStart is { } container ? Math.Max(video - container, 0) : 0), 0);

    private static string Folder => Path.Combine(SessionPaths.Root, "browser-scrub");

    /// <summary>One still through the thumbnail compositor into a JPEG of the session's, decoded and the file deleted.</summary>
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
