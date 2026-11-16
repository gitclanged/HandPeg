using System.Diagnostics;

namespace HandPegApp.Services;

/// <summary>
/// Timed joins whose result decides what may be freed. A thread that did not stop in time may still be inside a native call
/// (a Media Foundation read, a WASAPI or driver call) that uses its owner's handles, buffers, shared memory or COM objects;
/// freeing them under it is an access violation .NET cannot catch. So an owner whose join fails keeps (leaks) what the thread
/// can touch, logs it, and lets process exit or the thread's own exit path reclaim it. Every timed join in the app goes through
/// here (the <c>thread-joins-checked</c> golden).
/// </summary>
internal static class ThreadStop
{
    /// <summary>Joins <paramref name="thread"/> for up to <paramref name="milliseconds"/>; logs once when it did not stop.</summary>
    /// <returns>Whether the thread stopped; when false the caller must not free anything the thread uses.</returns>
    public static bool Join(Thread thread, int milliseconds, string owner) => JoinAll([thread], milliseconds, owner);

    /// <summary>
    /// Joins every thread against one shared deadline (not one timeout each, which would stack), so the caller's own budget holds.
    /// </summary>
    /// <returns>Whether every thread stopped.</returns>
    public static bool JoinAll(IReadOnlyList<Thread> threads, int totalMilliseconds, string owner)
    {
        var clock = Stopwatch.StartNew();
        var stopped = true;
        foreach (var thread in threads)
        {
            var left = Math.Max(0, totalMilliseconds - (int)clock.ElapsedMilliseconds);
            if (thread.Join(left))
                continue;

            stopped = false;
            AppLog.Write($"{owner}: the thread \"{thread.Name}\" did not stop within {totalMilliseconds} ms; what it uses is kept, not freed");
        }

        return stopped;
    }
}
