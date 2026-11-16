using System.Globalization;
using System.IO;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Developer flight recorder, enabled by <see cref="FlagPath"/> (%AppData%\HandPeg\dev-verbose.flag) or "devVerboseLogging": true
/// (Settings > General > Diagnostics > Verbose flight recorder, applied on save via <see cref="Follow"/>). When on, keeps the last
/// <see cref="Capacity"/> events (breadcrumbs, log lines, input, actions, edits, refusals, undo steps, paths, each with playhead and
/// selection) in a lock-free ring flushed to <see cref="LogPath"/> about once a second off the UI thread, plus a project snapshot
/// (path, media, TimelineDocument JSON). Crash reports (including the sentinel's, read from disk) then include it unredacted
/// (<see cref="CrashDevVerbose"/>) for exact replay. When off, nothing runs and reports stay sanitized.
/// </summary>
public static class DevFlightRecorder
{
    public const int Capacity = 500;

    /// <summary>How many of the newest lines a crash report carries.</summary>
    public const int ReportLines = 200;

    public static string FlagPath { get; } = Path.Combine(AppPaths.Settings, "dev-verbose.flag");

    /// <summary>
    /// Ring log (newest last) in %LocalAppData%\HandPeg-Data\logs, not the installer's %LocalAppData%\HandPeg (cleared by update/uninstall).
    /// </summary>
    public static string LogPath { get; } = Path.Combine(AppPaths.Logs, "flight-recorder.log");

    /// <summary>The last snapshot of the open project, for the sentinel's report of a native crash.</summary>
    public static string StatePath { get; } = Path.Combine(AppPaths.Logs, "flight-state.json");

    private static volatile bool _on;

    /// <summary>Whether the recorder runs (decided once, after the first frame: <see cref="Start"/>).</summary>
    public static bool IsEnabled => _on;

    /// <summary>
    /// Selection and playhead provider, queried on the UI thread per recorded event (set by MainWindow).
    /// </summary>
    public static Func<string>? Where { get; set; }

    // ----- Ring buffer -----
    //
    // Same design as crash breadcrumbs: parallel arrays, a monotonic counter, and a per-slot sequence cleared before and set after
    // writing so concurrent readers skip in-flight slots. Recording is allocation-free.

    private static readonly long[] Sequences = new long[Capacity];
    private static readonly long[] Stamps = new long[Capacity];
    private static readonly string?[] Categories = new string?[Capacity];
    private static readonly string?[] Details = new string?[Capacity];
    private static long _recorded, _written;

    /// <summary>Records an event (a no-op while the recorder is off). Lock-free, never throws.</summary>
    public static void Record(string category, string? detail)
    {
        if (!_on)
            return;

        var sequence = Interlocked.Increment(ref _recorded);
        var slot = (int)((sequence - 1) % Capacity);

        // Full fence (not a release store): subsequent plain stores must not become visible before the slot is marked in-flight
        // (ARM64 can reorder later stores ahead of a release).
        Interlocked.Exchange(ref Sequences[slot], 0);
        Stamps[slot] = DateTime.Now.Ticks;
        Categories[slot] = category;
        Details[slot] = detail;
        Volatile.Write(ref Sequences[slot], sequence);
    }

    /// <summary>Records an input event (key, click, action) with playhead and selection (<see cref="Where"/>).</summary>
    public static void RecordInput(string category, string detail)
    {
        if (!_on)
            return;

        string? where = null;
        try
        {
            where = Where?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        Record(category, where is { Length: > 0 } ? $"{detail}   | {where}" : detail);
    }

    /// <summary>The ring's lines, oldest first (at most <paramref name="newest"/> of them).</summary>
    public static List<string> Lines(int newest = Capacity)
    {
        var end = Volatile.Read(ref _recorded);
        var lines = new List<string>(Math.Min(newest, Capacity));
        for (var sequence = Math.Max(end - Math.Min(newest, Capacity), 0) + 1; sequence <= end; sequence++)
        {
            var slot = (int)((sequence - 1) % Capacity);

            // Seqlock read: sequence must match before and after the copy (full barrier before the re-check), else skip. The pre-check
            // prevents a torn read of an in-flight newest slot passing the post-check (on x64 too).
            if (Volatile.Read(ref Sequences[slot]) != sequence)
                continue;
            var (stamp, category, detail) = (Stamps[slot], Categories[slot], Details[slot]);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref Sequences[slot]) != sequence)
                continue;

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{new DateTime(stamp):yyyy-MM-dd HH:mm:ss.fff}  #{sequence}  [{category}] {detail}"));
        }

        return lines;
    }

    // ----- The open project -----

    private sealed record Snapshot(string ProjectPath, IReadOnlyList<string> Media, string? TimelineJson, DateTime TakenAt);

    private static volatile Snapshot? _snapshot;
    private static long _snapshotWritten = -1, _snapshots;

    /// <summary>Project snapshot taken on the UI thread after edit/undo/load; persisted by the timer.</summary>
    public static void TakeSnapshot(string projectPath, IReadOnlyList<string> media, string? timelineJson)
    {
        if (!_on)
            return;

        _snapshot = new Snapshot(projectPath, media, timelineJson, DateTime.Now);
        Interlocked.Increment(ref _snapshots);
    }

    // ----- On and off, and the disk -----

    private static Timer? _timer;

    // Set after the first start in this process so re-enabling does not rotate the log again.
    private static bool _startedBefore;

    // Start, Stop and Follow, one at a time (Settings saved twice in a row runs two of them on the pool).
    private static readonly object SwitchGate = new();

    /// <summary>
    /// True when enabled by the flag file or setting. Reads disk (never before Frame 1); never throws.
    /// </summary>
    public static bool IsWanted()
    {
        try
        {
            // Also enabled by preview tracing (PreviewTrace: preview-trace.flag beside HandPeg.exe, or HANDPEG_PREVIEW_TRACE=1).
            return File.Exists(FlagPath) || AppSettings.Current.DevVerboseLogging || PreviewTrace.IsOn;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the recorder if enabled (flag or setting). Called off the UI thread after Frame 1; starts the writer when on.
    /// </summary>
    public static void Start()
    {
        if (!IsWanted())
            return;

        lock (SwitchGate)
        {
            if (_on)
                return;

            // Rotate the previous session's log (a crash-restart would overwrite it); re-enabling within a session continues the current log.
            try
            {
                if (!_startedBefore && File.Exists(LogPath))
                    File.Move(LogPath, LogPath + ".previous", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            _startedBefore = true;
            _on = true;
            Record("dev", $"flight recorder on (HandPeg {CrashReports.AppVersion}, flag {(File.Exists(FlagPath) ? "file" : "setting")})");
            _timer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Disabled in Settings: flushes remaining lines and stops recording immediately. Never throws.
    /// </summary>
    public static void Stop()
    {
        lock (SwitchGate)
        {
            if (!_on)
                return;

            Record("dev", "flight recorder off (Settings)");
            Flush();
            _on = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// On Settings save: starts or stops per <see cref="IsWanted"/>. Off the UI thread; never throws.
    /// </summary>
    public static void Follow()
    {
        if (IsWanted())
            Start();
        else
            Stop();
    }

    private static readonly object FlushGate = new();

    /// <summary>Flushes the ring and snapshot if changed (timer and crash report). Never throws.</summary>
    public static void Flush()
    {
        if (!_on || !Monitor.TryEnter(FlushGate))
            return;

        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var recorded = Volatile.Read(ref _recorded);
            if (recorded != _written)
            {
                var temporary = LogPath + ".tmp";
                File.WriteAllLines(temporary, Lines(), Encoding.UTF8);
                File.Move(temporary, LogPath, overwrite: true);
                _written = recorded;
            }

            var taken = Volatile.Read(ref _snapshots);
            if (taken != _snapshotWritten && _snapshot is { } snapshot)
            {
                var temporary = StatePath + ".tmp";
                File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(ToReport(snapshot, [])), Encoding.UTF8);
                File.Move(temporary, StatePath, overwrite: true);
                _snapshotWritten = taken;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A recorder that cannot write is let be: it must never cost the app anything.
        }
        finally
        {
            Monitor.Exit(FlushGate);
        }
    }

    // ----- In a crash report -----

    // The setting as the settings file has it, read without loading the settings (the sentinel never does).
    private static bool SettingOnDisk()
    {
        if (!File.Exists(AppPaths.SettingsFile))
            return false;

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        return json.RootElement.TryGetProperty("devVerboseLogging", out var on) && on.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static CrashDevVerbose ToReport(Snapshot? snapshot, List<string> lines) => new()
    {
        ProjectPath = snapshot?.ProjectPath ?? "",
        MediaPaths = [.. snapshot?.Media ?? []],
        SnapshotTakenAt = snapshot?.TakenAt,
        Timeline = snapshot?.TimelineJson,
        FlightRecorder = lines,
    };

    /// <summary>Unredacted in-process crash section: snapshot, recent ring lines, full exception. Null when off.</summary>
    public static CrashDevVerbose? ForReport(Exception? exception)
    {
        if (!_on)
            return null;

        try
        {
            if (exception is not null)
                Record("crash", exception.ToString());
            var report = ToReport(_snapshot, Lines(ReportLines));
            report.Exception = exception?.ToString();
            Flush();
            return report;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sentinel equivalent for native crashes, read from the last persisted snapshot and log lines. Null when disabled or nothing written.
    /// </summary>
    public static CrashDevVerbose? FromDisk()
    {
        try
        {
            if (!File.Exists(FlagPath) && !SettingOnDisk() && !PreviewTrace.IsOn)
                return null;

            var report = File.Exists(StatePath) ? System.Text.Json.JsonSerializer.Deserialize<CrashDevVerbose>(File.ReadAllText(StatePath)) ?? new() : new();
            report.FlightRecorder = File.Exists(LogPath) ? [.. File.ReadAllLines(LogPath).TakeLast(ReportLines)] : [];
            return report.FlightRecorder.Count > 0 || report.ProjectPath.Length > 0 ? report : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
