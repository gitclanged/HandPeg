using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using HandPegApp.Compositor;

namespace HandPegApp.Services;

/// <summary>
/// Reclaims memory after a tab closes or is backgrounded: one blocking, compacting full GC (including LOH), deferred until the editor
/// is quiet (no play, scrub or tab switch) because it suspends all managed threads.
///
/// Requests within 2 s coalesce (the last restarts the wait). Quietness is checked on the thread that set <see cref="IsQuiet"/> (the UI
/// thread), and the GC runs there in the same turn: running it later on the pool let a queued Play start first and then stalled its
/// audio feeder; a blocking GC suspends every thread anyway, and no Play can start meanwhile. Finalizers and the log line go to the
/// pool. When busy, rechecks every 2 s for up to 2 minutes after the last request, then gives up. Lazily created (Law 1); idle between
/// requests.
///
/// Measured gain is modest (0-80 MB of a 1.2-1.4 GB process, mostly native memory); it exists to release a closed tab's Sequence, undo
/// history and finalizer-held native bitmaps, nothing more.
/// </summary>
public static class MemoryHygiene
{
    private static Func<bool>? _isQuiet;
    private static SynchronizationContext? _quietThread;

    /// <summary>
    /// Set by MainWindow: true when nothing plays, scrubs, records or switches tabs. Invoked via the setter thread's
    /// SynchronizationContext (the UI dispatcher), else on the pool. Null (no window) is never quiet.
    /// </summary>
    public static Func<bool>? IsQuiet
    {
        get => _isQuiet;
        set
        {
            lock (Gate)
                (_isQuiet, _quietThread) = (value, value is null ? null : SynchronizationContext.Current);
        }
    }

    // Timings (shortened by goldens): requests within Settle coalesce; busy rechecks every RecheckEvery until GiveUpAfter.
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(2);
    internal static TimeSpan RecheckEvery { get; set; } = TimeSpan.FromSeconds(2);
    internal static TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How many collections were made, and why the last one was (for the goldens and the log).</summary>
    internal static int Compactions { get; private set; }

    internal static string LastReason { get; private set; } = "";

    /// <summary>How many collections also compacted the process heap (an ask with compactProcessHeap).</summary>
    internal static int HeapCompactions { get; private set; }

    /// <summary>How many waits were released after <see cref="GiveUpAfter"/> without a quiet moment.</summary>
    internal static int GivenUp { get; private set; }

    /// <summary>Managed thread id of the last collection (goldens verify it is the quiet-check thread).</summary>
    internal static int CollectedOn { get; private set; }

    /// <summary>Whether a collection is running now (its finalizers and log line included).</summary>
    internal static bool IsCollecting
    {
        get
        {
            lock (Gate)
                return _collecting;
        }
    }

    private static readonly object Gate = new();
    private static readonly List<string> Reasons = [];
    private const int MaximumReasons = 8;
    private static Timer? _timer;
    private static long _dueAt, _giveUpAt;
    private static bool _collecting, _compactHeap;

    /// <summary>
    /// Requests one compacting GC when quiet (coalesced). With <paramref name="compactProcessHeap"/> (from any merged request), also runs
    /// HeapCompact after finalizers to decommit freed native blocks.
    /// </summary>
    public static void RequestCompactOnce(string reason, bool compactProcessHeap = false)
    {
        lock (Gate)
        {
            _compactHeap |= compactProcessHeap;
            // A few reasons are enough for the log line; the asks themselves are one however many there are.
            if (Reasons.Count < MaximumReasons && !Reasons.Contains(reason, StringComparer.Ordinal))
                Reasons.Add(reason);

            var now = Environment.TickCount64;
            _dueAt = now + (long)Settle.TotalMilliseconds;
            _giveUpAt = _dueAt + (long)GiveUpAfter.TotalMilliseconds;
            Arm(Settle);
        }
    }

    /// <summary>Lets go of an ask still waiting (the goldens, between cases).</summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            Reasons.Clear();
            _compactHeap = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    // Under Gate.
    private static void Arm(TimeSpan after) =>
        (_timer ??= new Timer(static _ => Due(), null, Timeout.Infinite, Timeout.Infinite)).Change(after, Timeout.InfiniteTimeSpan);

    // On the pool, when the wait is over: the window is asked on its own thread.
    private static void Due()
    {
        SynchronizationContext? thread;
        lock (Gate)
        {
            if (Reasons.Count == 0)
                return;

            // A later ask moved the wait on (the timer had fired already), or a collection is still running: wait again.
            var left = _dueAt - Environment.TickCount64;
            if (left > 0 || _collecting)
            {
                Arm(left > 0 ? TimeSpan.FromMilliseconds(left) : RecheckEvery);
                return;
            }

            thread = _quietThread;
        }

        if (thread is null)
            Decide();
        else
            thread.Post(static _ => Decide(), null);
    }

    // On the IsQuiet thread: collect now if quiet, else reschedule or give up. Cheap counter readings for the log are taken first so
    // the GC immediately follows the answer.
    private static void Decide()
    {
        var before = (Private: CompositorWorker.PrivateBytes(), Heap: GC.GetTotalMemory(forceFullCollection: false), Paused: GC.GetTotalPauseDuration());
        bool quiet;
        try
        {
            quiet = _isQuiet?.Invoke() == true;
        }
        catch (InvalidOperationException)
        {
            // What it reads is another thread's (set from a thread without a dispatcher): never quiet, never a crash.
            quiet = false;
        }

        string reason;
        var compactHeap = false;
        lock (Gate)
        {
            if (Reasons.Count == 0)
                return;

            if (!quiet || _collecting)
            {
                if (_collecting || Environment.TickCount64 < _giveUpAt)
                {
                    Arm(RecheckEvery);
                    return;
                }

                GivenUp++;
                reason = string.Join(", ", Reasons);
                Reasons.Clear();
                _compactHeap = false;
            }
            else
            {
                reason = string.Join(", ", Reasons);
                Reasons.Clear();
                (compactHeap, _compactHeap) = (_compactHeap, false);
                _collecting = true;
            }
        }

        if (quiet)
            Compact(reason, compactHeap, before);
        else
            AppLog.Write(FormattableString.Invariant($"Memory hygiene ({reason}): no quiet moment within {GiveUpAfter.TotalSeconds:0} s, the compacting collection let go"));
    }

    /// <summary>
    /// Runs the GC on the quiet-check thread in the same turn: blocking (background GCs never compact), compacting, LOH included
    /// (CompactOnce). Forced, not Aggressive (which would release the 64 MB gen0 budget from HandPegApp.csproj, only to fault it back).
    /// Finalizers run on the pool (avoiding UI-thread deadlock) before the second reading so freed native memory is counted.
    /// </summary>
    private static void Compact(string reason, bool compactHeap, (long Private, long Heap, TimeSpan Paused) before)
    {
        var handedOn = false;
        try
        {
            CollectedOn = Environment.CurrentManagedThreadId;
            var watch = Stopwatch.StartNew();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var paused = GC.GetTotalPauseDuration() - before.Paused;
            Task.Run(() => AfterCompact(reason, compactHeap, before, paused, watch));
            handedOn = true;
        }
        finally
        {
            if (!handedOn)
                Done(reason);
        }
    }

    // On the pool after the collection: its finalizers, the process heap when asked, the second reading and the log line.
    private static void AfterCompact(string reason, bool compactHeap, (long Private, long Heap, TimeSpan Paused) before, TimeSpan paused, Stopwatch watch)
    {
        try
        {
            GC.WaitForPendingFinalizers();
            if (compactHeap)
            {
                _ = Native.Kernel32.HeapCompact(Native.Kernel32.GetProcessHeap(), 0);
                HeapCompactions++;
            }

            watch.Stop();

            var (privateAfter, heapAfter, committed) = (CompositorWorker.PrivateBytes(), GC.GetTotalMemory(forceFullCollection: false), GC.GetGCMemoryInfo().TotalCommittedBytes);
            AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                $"Memory hygiene ({reason}): {(before.Private - privateAfter) / 1048576.0:0.0} MB given back, private {before.Private / 1048576.0:0} -> {privateAfter / 1048576.0:0} MB, managed heap {before.Heap / 1048576.0:0.0} -> {heapAfter / 1048576.0:0.0} MB ({committed / 1048576.0:0.0} MB committed), paused {paused.TotalMilliseconds:0.0} ms, {watch.Elapsed.TotalMilliseconds:0.0} ms with the finalizers"));
        }
        finally
        {
            Done(reason);
        }
    }

    private static void Done(string reason)
    {
        lock (Gate)
        {
            _collecting = false;
            Compactions++;
            LastReason = reason;
        }
    }
}
