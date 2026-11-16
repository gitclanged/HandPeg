using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HandPegApp.Services;

// ----- Crash report file (.hpcrash), format 1 -----
// Written by CrashReporter (in-process, or by the sentinel after a native fault) to the crashes folder (AppPaths) and read by
// HandPeg.CrashDesk, which links this file so the layout is shared. Reports must be anonymous: no paths, file/user/machine
// names, URLs or media text. All strings are sanitized; only categories (container, codec, size, rate, length), code
// identifiers and numbers remain. Exception: DevVerbose, written only when the developer flight recorder is enabled.

/// <summary>A crash report as it is written to disk: JSON, camelCase, enums by name.</summary>
public sealed class CrashReport
{
    public const string FormatName = "handpeg-crash";
    public const int CurrentVersion = 1;
    public const string Extension = ".hpcrash";

    // No defaults here: a file that does not say what it is must not pass for one that does.
    public string? Format { get; set; }
    public int Version { get; set; }

    /// <summary>16-hex-digit fault signature, stable across machines and builds (<see cref="CrashSignature"/>).</summary>
    public string Signature { get; set; } = "";

    /// <summary>HandPeg version from the project file (major.minor.patch).</summary>
    public string AppVersion { get; set; } = "";

    public DateTime CrashedAtUtc { get; set; }

    public CrashKind Kind { get; set; }

    /// <summary>True when the process terminated (false for survived dispatcher exceptions or unobserved tasks).</summary>
    public bool Fatal { get; set; }

    /// <summary>Exception full type name, or native code and name ("0xC0000005 EXCEPTION_ACCESS_VIOLATION").</summary>
    public string ExceptionType { get; set; } = "";

    /// <summary>The exception's message, sanitized.</summary>
    public string Message { get; set; } = "";

    /// <summary>Managed frames, innermost first, normalized by <see cref="CrashSignature.NormalizeFrame"/>.</summary>
    public List<string> Stack { get; set; } = [];

    /// <summary>Inner exceptions (type and message), outermost first.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CrashInnerException>? Inner { get; set; }

    /// <summary>Native fault module file name (never a path), e.g. "d3d11.dll", when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeModule { get; set; }

    /// <summary>For a native fault: the offset of the faulting address in that module, hexadecimal ("0x1a2b3c").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeOffset { get; set; }

    /// <summary>The process's exit code, as the sentinel saw it (a report written by the sentinel only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ExitCode { get; set; }

    public CrashEnvironment Environment { get; set; } = new();

    /// <summary>The last moments before the crash (the breadcrumb ring, oldest first).</summary>
    public List<CrashBreadcrumb> Breadcrumbs { get; set; } = [];

    /// <summary>
    /// Named context entries ("mode", "media", "graph", "export", ...) holding sanitized summaries, never paths or text.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Context { get; set; }

    /// <summary>
    /// Developer-only (requires <see cref="DevFlightRecorder"/>): unredacted project/media paths, timeline JSON, full exception and recent
    /// recorder lines. Never present in end-user reports.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CrashDevVerbose? DevVerbose { get; set; }
}

/// <summary>The developer part of a report (<see cref="CrashReport.DevVerbose"/>), unredacted.</summary>
public sealed class CrashDevVerbose
{
    public string ProjectPath { get; set; } = "";

    public List<string> MediaPaths { get; set; } = [];

    /// <summary>When the timeline below was taken (after the last edit, undo step or load).</summary>
    public DateTime? SnapshotTakenAt { get; set; }

    /// <summary>The TimelineDocument as a project file holds it (JSON text).</summary>
    public string? Timeline { get; set; }

    /// <summary>The exception in full (type, message, stack, inner exceptions), as .NET writes it.</summary>
    public string? Exception { get; set; }

    /// <summary>The flight recorder's newest lines, oldest first.</summary>
    public List<string> FlightRecorder { get; set; } = [];
}

public enum CrashKind
{
    /// <summary>An exception no code caught (AppDomain.UnhandledException).</summary>
    Managed,

    /// <summary>An exception thrown on the window's thread (Dispatcher.UnhandledException).</summary>
    Dispatcher,

    /// <summary>A task's exception nobody looked at (TaskScheduler.UnobservedTaskException).</summary>
    UnobservedTask,

    /// <summary>Native fault (e.g. access violation in a driver) captured by the unhandled-exception filter.</summary>
    Native,

    /// <summary>Written by the sentinel process: the process ended abnormally and wrote nothing itself.</summary>
    Sentinel,
}

public sealed class CrashInnerException
{
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>The computer and the program, as far as a crash needs them: no names.</summary>
public sealed class CrashEnvironment
{
    /// <summary>"Windows 11 (10.0.26300)".</summary>
    public string Os { get; set; } = "";

    /// <summary>".NET 8.0.11".</summary>
    public string Runtime { get; set; } = "";

    public int ProcessorCount { get; set; }

    /// <summary>GPU model and driver version per adapter.</summary>
    public List<string> Gpus { get; set; } = [];

    public long WorkingSetMb { get; set; }
    public long ManagedHeapMb { get; set; }
    public double UptimeSeconds { get; set; }

    /// <summary>Encoder or Editor.</summary>
    public string Mode { get; set; } = "";

    public bool SelfContained { get; set; }

    /// <summary>The FFmpeg build in use, by its version string only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ffmpeg { get; set; }

    // Optional hardware fields: omitted when unavailable (absent in older reports).

    /// <summary>CPU brand string.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cpu { get; set; }

    /// <summary>Physical cores (fewer than <see cref="ProcessorCount"/> logical processors means SMT is on).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PhysicalCores { get; set; }

    /// <summary>The memory installed, in GB.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RamGb { get; set; }

    /// <summary>Configured memory speed (MT/s) and module rated speed (higher when XMP/EXPO is off).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RamMts { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RamRatedMts { get; set; }

    /// <summary>Each graphics card: model, PCI vendor and device, driver and its own video memory.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CrashGpu>? Adapters { get; set; }

    /// <summary>The main display.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CrashDisplay? Display { get; set; }

    /// <summary>HandPeg's theme ("Windows System", "OLED Black"...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; set; }

    /// <summary>Power plan (well-known GUID name, else "custom plan"), power mode and source, e.g. "Balanced, Best performance, on AC".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PowerPlan { get; set; }

    /// <summary>Whether HandPeg's background tools run in Windows' Efficiency mode (EcoQoS).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EfficiencyMode { get; set; }

    /// <summary>The whisper.cpp and yt-dlp builds installed, by their version strings only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Whisper { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YtDlp { get; set; }
}

/// <summary>A graphics card: its model, PCI ids ("0x10DE"), driver and dedicated video memory.</summary>
public sealed class CrashGpu
{
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Driver { get; set; }

    public long VramMb { get; set; }
}

/// <summary>The main display: its mode in pixels, its refresh rate and the system's scale.</summary>
public sealed class CrashDisplay
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
    public int ScalePercent { get; set; }
}

/// <summary>Pre-crash breadcrumb: seconds since process start, category and sanitized detail.</summary>
public sealed class CrashBreadcrumb
{
    public double At { get; set; }
    public string Category { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>Reading and writing .hpcrash files.</summary>
public static class CrashReportJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static string Serialize(CrashReport report) => JsonSerializer.Serialize(report, Options);

    /// <summary>Parses a report; rejects anything not format 1 with a reason.</summary>
    public static CrashReport Deserialize(string json)
    {
        var report = JsonSerializer.Deserialize<CrashReport>(json, Options) ?? throw new InvalidDataException("The crash report is empty.");
        if (report.Format != CrashReport.FormatName)
            throw new InvalidDataException("This is not a HandPeg crash report.");
        if (report.Version < 1 || report.Version > CrashReport.CurrentVersion)
            throw new InvalidDataException($"This crash report is in format {report.Version}; this version reads format {CrashReport.CurrentVersion}.");
        return report;
    }
}

/// <summary>
/// Stable crash signature for grouping reports and spotting regressions: hash of the exception type and the first HandPeg frames (or
/// first frames overall), normalized against rebuilds, moved lines and renumbered compiler names; native faults use code and module.
/// </summary>
public static partial class CrashSignature
{
    /// <summary>How many frames go into a signature.</summary>
    public const int Frames = 6;

    /// <summary>The namespace HandPeg's own code is in.</summary>
    public const string OwnNamespace = "HandPegApp.";

    public static string Compute(string exceptionType, IEnumerable<string> frames, string? nativeModule = null)
    {
        var normalized = frames.Select(NormalizeFrame).Where(f => f.Length > 0).ToList();
        var own = normalized.Where(f => f.StartsWith(OwnNamespace, StringComparison.Ordinal)).Take(Frames).ToList();
        var chosen = own.Count > 0 ? own : normalized.Take(Frames).ToList();

        var text = new StringBuilder(exceptionType.Trim());
        foreach (var frame in chosen)
            text.Append('|').Append(frame);
        if (!string.IsNullOrWhiteSpace(nativeModule))
            text.Append("|module:").Append(nativeModule.Trim().ToLowerInvariant());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes a frame: full method name without parameters, file, line or offset, and with compiler-generated numbering removed.
    /// "   at HandPegApp.X.&lt;Run&gt;b__12_0(Object o) in C:\src\X.cs:line 40" becomes "HandPegApp.X.&lt;Run&gt;b__".
    /// </summary>
    public static string NormalizeFrame(string frame)
    {
        var text = frame.Trim();
        if (text.StartsWith("at ", StringComparison.Ordinal))
            text = text[3..];

        var cut = text.IndexOf('(');
        if (cut >= 0)
            text = text[..cut];
        cut = text.IndexOf(" in ", StringComparison.Ordinal);
        if (cut >= 0)
            text = text[..cut];
        cut = text.IndexOf(" +", StringComparison.Ordinal);
        if (cut >= 0)
            text = text[..cut];

        return CompilerNumbers().Replace(text.Trim(), "$1");
    }

    // "<Run>b__12_0" -> "<Run>b__", "<>c__DisplayClass5_0" -> "<>c__DisplayClass", "<Main>g__Local|3_1" -> "<Main>g__Local|", "d__14" -> "d__".
    [GeneratedRegex(@"((?:__|\|)[A-Za-z]*)\d+(?:_\d+)?")]
    private static partial Regex CompilerNumbers();
}
