using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// HANDPEG_LOG_STARTUP=1 launch trace: milestones with ms since process start, working set, managed heap and GC committed memory,
/// buffered and written once after Frame 1 when idle (no pre-Frame-1 disk I/O). Dock rebuilds or tab creation after Frame 1 are
/// flagged as pop-in.
/// </summary>
public static class StartupTrace
{
    public static bool IsOn { get; } = Environment.GetEnvironmentVariable("HANDPEG_LOG_STARTUP") == "1";

    private static readonly object Gate = new();
    private static readonly List<string> Lines = [];
    private static Stopwatch? _clock;
    private static double _clockStartedAt;
    private static bool _flushed;

    /// <summary>Whether the first frame has been drawn: what changes the layout after it is a second stage.</summary>
    public static bool FirstFrameDrawn { get; private set; }

    /// <summary>Milliseconds since the process started.</summary>
    public static double Now
    {
        get
        {
            if (_clock is null)
            {
                using (var self = Process.GetCurrentProcess())
                    _clockStartedAt = (DateTime.Now - self.StartTime).TotalMilliseconds;
                _clock = Stopwatch.StartNew();
            }

            return _clockStartedAt + _clock.Elapsed.TotalMilliseconds;
        }
    }

    public static void Mark(string moment)
    {
        if (!IsOn)
            return;

        lock (Gate)
        {
            var at = Now;
            // Heap in use and GC committed; gen0/1/2 collection counts, GC pause time and lock contention (slow steps with none of
            // these moving were native or disk time); total allocated bytes, JIT-compiled method count and JIT time (ReadyToRun gaps).
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{at,8:0.0} ms  {Environment.WorkingSet / 1048576.0,6:0.0} MB ws  {GC.GetTotalMemory(false) / 1048576.0,6:0.0} MB heap  {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0,6:0.0} MB committed  gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} {GC.GetTotalPauseDuration().TotalMilliseconds,5:0.0} ms  locks {Monitor.LockContentionCount,3}  {GC.GetTotalAllocatedBytes(false) / 1048576.0,6:0.0} MB allocated  jit {System.Runtime.JitInfo.GetCompiledMethodCount(false),5} {System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds,6:0.0} ms  {moment}");
            if (_flushed)
                AppLog.Write("Startup trace: " + line);
            else
                Lines.Add(line);
        }
    }

    /// <summary>Records a visible layout change (pane rebuild, tab creation); after Frame 1 it is flagged as pop-in.</summary>
    public static void Layout(string what) => Mark(FirstFrameDrawn ? $"{what} (after the first frame)" : what);

    public static void FirstFrame()
    {
        Mark("first frame drawn (ContentRendered)");
        if (IsOn)
            Mark("assemblies loaded: " + string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)));
        FirstFrameDrawn = true;
    }

    /// <summary>Writes what was kept to the log; marks after this go to it one by one.</summary>
    public static void Flush()
    {
        if (!IsOn)
            return;

        string text;
        lock (Gate)
        {
            if (_flushed)
                return;
            _flushed = true;
            var all = new StringBuilder("Startup trace (HANDPEG_LOG_STARTUP=1):");
            foreach (var line in Lines)
                all.Append(Environment.NewLine).Append("    ").Append(line);
            text = all.ToString();
            Lines.Clear();
        }

        AppLog.Write(text);
    }
}
