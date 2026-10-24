using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// HANDPEG_LOG_STARTUP=1: the moments of a launch, each with the milliseconds since the process started, the working
/// set, the managed heap and the memory the collector holds (committed) at that moment. They are kept in memory and written to the log in one go once the first
/// frame is on screen and the window has gone idle: writing each as it happens would be disk work before the first frame.
/// A dock rebuild or a tab made after "first frame" in the list is a pop-in, and is marked as one.
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
                _clockStartedAt = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
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
            // The heap: what is in use; committed: what the collector holds from the system (in use, or kept for later).
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{at,8:0.0} ms  {Environment.WorkingSet / 1048576.0,6:0.0} MB ws  {GC.GetTotalMemory(false) / 1048576.0,6:0.0} MB heap  {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0,6:0.0} MB committed  {moment}");
            if (_flushed)
                AppLog.Write("Startup trace: " + line);
            else
                Lines.Add(line);
        }
    }

    /// <summary>Something that changes what is on screen (a rebuild of the panes, a tab made): after the first frame, a pop-in.</summary>
    public static void Layout(string what) => Mark(FirstFrameDrawn ? $"{what} (after the first frame)" : what);

    public static void FirstFrame()
    {
        Mark("first frame drawn (ContentRendered)");
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
