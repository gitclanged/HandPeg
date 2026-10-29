using System.Globalization;
using System.IO;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// The hidden developer flight recorder (4.4.0): on only on a PC that has the flag file <see cref="FlagPath"/>
/// (%AppData%\HandPeg\dev-verbose.flag) or "devVerboseLogging": true in appsettings.json, never shown in Settings. While on,
/// it keeps the last <see cref="Capacity"/> events (every breadcrumb, log line, key and click, bound action, timeline edit,
/// refusal, undo step, project and media path, each with the playhead and the selection) in a lock-free ring, written to
/// <see cref="LogPath"/> about once a second off the window's thread, and a snapshot of the open project (its path, its
/// media, its TimelineDocument as JSON) beside it. A crash report then carries all of it unredacted
/// (<see cref="CrashDevVerbose"/>), the sentinel's too (read back from the disk), so a bug can be replayed exactly. Off,
/// none of this runs, and crash reports stay as sanitized as ever.
/// </summary>
public static class DevFlightRecorder
{
    public const int Capacity = 500;

    /// <summary>How many of the newest lines a crash report carries.</summary>
    public const int ReportLines = 200;

    public static string FlagPath { get; } = Path.Combine(AppPaths.Settings, "dev-verbose.flag");

    /// <summary>
    /// The ring as text, newest last. In HandPeg's logs folder (%LocalAppData%\HandPeg-Data\logs), not the installer's
    /// %LocalAppData%\HandPeg, which an update or an uninstall clears.
    /// </summary>
    public static string LogPath { get; } = Path.Combine(AppPaths.Logs, "flight-recorder.log");

    /// <summary>The last snapshot of the open project, for the sentinel's report of a native crash.</summary>
    public static string StatePath { get; } = Path.Combine(AppPaths.Logs, "flight-state.json");

    private static volatile bool _on;

    /// <summary>Whether the recorder runs (decided once, after the first frame: <see cref="Start"/>).</summary>
    public static bool IsEnabled => _on;

    /// <summary>
    /// What is selected and where the playhead is, asked on the window's thread for each input and action recorded
    /// (set by the main window).
    /// </summary>
    public static Func<string>? Where { get; set; }

    // ----- The ring -----
    //
    // As the crash reporter's breadcrumbs: parallel arrays, a count that only goes up, a slot's number cleared before it is
    // written and set after, so a reader on another thread skips a slot being written. Recording stores references and
    // numbers and allocates nothing of its own.

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
        Volatile.Write(ref Sequences[slot], 0);
        Stamps[slot] = DateTime.Now.Ticks;
        Categories[slot] = category;
        Details[slot] = detail;
        Volatile.Write(ref Sequences[slot], sequence);
    }

    /// <summary>Records an event of the user's (a key, a click, an action), with the playhead and the selection (<see cref="Where"/>).</summary>
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
            var (stamp, category, detail) = (Stamps[slot], Categories[slot], Details[slot]);
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

    /// <summary>The open project as it is now (taken on the window's thread after an edit, an undo or a load; written out by the timer).</summary>
    public static void TakeSnapshot(string projectPath, IReadOnlyList<string> media, string? timelineJson)
    {
        if (!_on)
            return;

        _snapshot = new Snapshot(projectPath, media, timelineJson, DateTime.Now);
        Interlocked.Increment(ref _snapshots);
    }

    // ----- On and off, and the disk -----

    private static Timer? _timer;

    /// <summary>
    /// Decides whether the recorder runs: the flag file, or the setting. Called off the window's thread after the first
    /// frame (the disk is not touched before it); turns on the writer while on.
    /// </summary>
    public static void Start()
    {
        bool on;
        try
        {
            on = File.Exists(FlagPath) || AppSettings.Current.DevVerboseLogging;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            on = false;
        }

        if (!on || _on)
            return;

        // The last session's log is kept beside this one's (a crash and a restart would otherwise write over it).
        try
        {
            if (File.Exists(LogPath))
                File.Move(LogPath, LogPath + ".previous", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _on = true;
        Record("dev", $"flight recorder on (HandPeg {CrashReports.AppVersion}, flag {(File.Exists(FlagPath) ? "file" : "setting")})");
        _timer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static readonly object FlushGate = new();

    /// <summary>Writes the ring and the snapshot when they changed. Never throws; called by the timer and by a crash report.</summary>
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

    /// <summary>The unredacted part of a report the app writes itself: the snapshot, the ring's newest lines, the whole exception. Null while off.</summary>
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
    /// The same for the sentinel's report of a process that died natively: read back from what the recorder wrote (the
    /// last snapshot and the log's newest lines). Null when the flag is not set on this PC or nothing was written.
    /// </summary>
    public static CrashDevVerbose? FromDisk()
    {
        try
        {
            if (!File.Exists(FlagPath) && !SettingOnDisk())
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
