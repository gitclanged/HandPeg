using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using HandPegApp.Compositor;

namespace HandPegApp.Services;

/// <summary>
/// Giving the computer's memory back after a tab is closed or put behind (Pass 5.4D-TabPerf): one blocking, compacting full
/// collection of the managed heap (the large-object heap included), deferred until the editor is quiet (nothing playing,
/// scrubbing or switching tabs), since such a collection stops every managed thread for its length.
///
/// Asks made within 2 s of each other are one (the last of them starts the wait). The editor is then asked whether it is quiet,
/// on the thread that set <see cref="IsQuiet"/> (the window's: what it reads belongs to it); when it is, the collection runs there
/// and then, in the same turn (the 5.4D-TabPerf review: a collection started later on the pool let a Play queued behind the answer
/// start first, and the collection then stopped the audio feeder it had just started; a blocking collection stops every managed
/// thread anyway, so running it on the window's thread costs nothing more, and no Play can start while it runs). Only what follows
/// it, the finalizers it found and the log line, goes to the pool. When the editor is not quiet, it is asked again every 2 s, for
/// 2 minutes after the last ask at most, and then let go. Nothing of this exists until the first ask (Law 1), and nothing runs
/// between asks.
///
/// The collection's cost was measured on the XPS 13 (5.4D-TabPerf): a forced compacting collection there gave back 0 to 80 MB
/// of the main process's 1.2 to 1.4 GB, most of which is native memory, not the managed heap's. It is kept for what it does
/// give back after a tab's last references go (its Sequence, its undo history, its pictures' native memory, through their
/// finalizers), never for a promise of more.
/// </summary>
public static class MemoryHygiene
{
    private static Func<bool>? _isQuiet;
    private static SynchronizationContext? _quietThread;

    /// <summary>
    /// Set by the main window: true while nothing plays, scrubs, records or switches tabs. It is called on the thread that set it
    /// (through that thread's SynchronizationContext: the window's dispatcher), or, when that thread had none, on the pool.
    /// Null (no window) is never quiet.
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

    // The waits (the goldens run them shorter): asks closer than Settle are one; a busy editor is asked again every RecheckEvery, until
    // GiveUpAfter past the last ask.
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(2);
    internal static TimeSpan RecheckEvery { get; set; } = TimeSpan.FromSeconds(2);
    internal static TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How many collections were made, and why the last one was (for the goldens and the log).</summary>
    internal static int Compactions { get; private set; }

    internal static string LastReason { get; private set; } = "";

    /// <summary>How many waits were let go after <see cref="GiveUpAfter"/> without a quiet moment.</summary>
    internal static int GivenUp { get; private set; }

    /// <summary>The managed thread the last collection ran on (the goldens: the one that said the editor was quiet).</summary>
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
    private static bool _collecting;

    /// <summary>Asks for one compacting collection once the editor is quiet; asks made close together are one.</summary>
    public static void RequestCompactOnce(string reason)
    {
        lock (Gate)
        {
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

    // On the thread that set IsQuiet: collect now, right here, if the editor is quiet; ask again later if not, or give up. The
    // readings the log line compares are taken first: a few counters (no process list), so the collection follows the answer at once.
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
            }
            else
            {
                reason = string.Join(", ", Reasons);
                Reasons.Clear();
                _collecting = true;
            }
        }

        if (quiet)
            Compact(reason, before);
        else
            AppLog.Write(FormattableString.Invariant($"Memory hygiene ({reason}): no quiet moment within {GiveUpAfter.TotalSeconds:0} s, the compacting collection let go"));
    }

    /// <summary>
    /// The collection itself, on the thread that just said the editor is quiet (in the same turn): blocking (a background collection
    /// never compacts), compacting, the large-object heap too (CompactOnce: the next blocking full collection only). Forced, not
    /// Aggressive: Aggressive would hand back the youngest generation's budget too (64 MB, HandPegApp.csproj), and the next allocations
    /// would fault it back in. The finalizers it finds run on the pool (a finalizer that waits for the window's thread would wait for
    /// ever there), before the second reading, so the native memory they free (pictures, sections) counts in what it gave back.
    /// </summary>
    private static void Compact(string reason, (long Private, long Heap, TimeSpan Paused) before)
    {
        var handedOn = false;
        try
        {
            CollectedOn = Environment.CurrentManagedThreadId;
            var watch = Stopwatch.StartNew();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var paused = GC.GetTotalPauseDuration() - before.Paused;
            Task.Run(() => AfterCompact(reason, before, paused, watch));
            handedOn = true;
        }
        finally
        {
            if (!handedOn)
                Done(reason);
        }
    }

    // On the pool after the collection: its finalizers, the second reading and the log line.
    private static void AfterCompact(string reason, (long Private, long Heap, TimeSpan Paused) before, TimeSpan paused, Stopwatch watch)
    {
        try
        {
            GC.WaitForPendingFinalizers();
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
