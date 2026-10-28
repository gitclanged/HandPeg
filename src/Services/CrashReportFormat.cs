using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HandPegApp.Services;

// ----- The crash report file (.hpcrash), format 1 -----
// Written by HandPeg (CrashReporter in the process itself, or its sentinel process when the process died of a native
// fault) into the crashes folder (AppPaths), and read by HandPeg.CrashDesk, which links this file: the two never disagree
// on the layout. Nothing in a report may tell who made it or what they were working on: no paths, file names, user or
// machine names, URLs, or words from the media (subtitles, text layers). Every string is sanitized before it is put here;
// what is kept is the kind of thing (container, codec, size, rate, length), the code's own names, and numbers.

/// <summary>A crash report as it is written to disk: JSON, camelCase, enums by name.</summary>
public sealed class CrashReport
{
    public const string FormatName = "handpeg-crash";
    public const int CurrentVersion = 1;
    public const string Extension = ".hpcrash";

    // No defaults here: a file that does not say what it is must not pass for one that does.
    public string? Format { get; set; }
    public int Version { get; set; }

    /// <summary>What crashed, as 16 hex digits: the same fault gives the same signature on any machine and in any build (see <see cref="CrashSignature"/>).</summary>
    public string Signature { get; set; } = "";

    /// <summary>HandPeg's version, as its project file numbers it ("4.1.0").</summary>
    public string AppVersion { get; set; } = "";

    public DateTime CrashedAtUtc { get; set; }

    public CrashKind Kind { get; set; }

    /// <summary>Whether the process ended with it. A dispatcher exception the window survived, or an unobserved task, is not.</summary>
    public bool Fatal { get; set; }

    /// <summary>The exception's full type name ("System.NullReferenceException"), or for a native fault its code and name ("0xC0000005 EXCEPTION_ACCESS_VIOLATION").</summary>
    public string ExceptionType { get; set; } = "";

    /// <summary>The exception's message, sanitized.</summary>
    public string Message { get; set; } = "";

    /// <summary>The managed frames, innermost first, each normalized (<see cref="CrashSignature.NormalizeFrame"/>): "HandPegApp.Services.FilterGraphBuilder+Pass.AppendVideoFile".</summary>
    public List<string> Stack { get; set; } = [];

    /// <summary>The exceptions inside it (an AggregateException's, a TargetInvocationException's), outermost first, each as type and message.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CrashInnerException>? Inner { get; set; }

    /// <summary>For a native fault: the file name (never the path) of the module it happened in ("libmpv-2.dll"), when it is known.</summary>
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
    /// What HandPeg was doing, by name: "mode" (Editor / Encoder), "media" (the main video's container, codecs, size,
    /// rate, length, and how many layers), "graph" (the live graph's size and the filters in it), "export"... Values are
    /// sanitized summaries, never paths or text.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Context { get; set; }
}

public enum CrashKind
{
    /// <summary>An exception no code caught (AppDomain.UnhandledException).</summary>
    Managed,

    /// <summary>An exception thrown on the window's thread (Dispatcher.UnhandledException).</summary>
    Dispatcher,

    /// <summary>A task's exception nobody looked at (TaskScheduler.UnobservedTaskException).</summary>
    UnobservedTask,

    /// <summary>A native fault (an access violation in libmpv or a driver), recorded by the process's unhandled-exception filter.</summary>
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

    /// <summary>Each graphics card by its model and driver version ("NVIDIA GeForce RTX 3080 (driver 32.0.15.6094)").</summary>
    public List<string> Gpus { get; set; } = [];

    public long WorkingSetMb { get; set; }
    public long ManagedHeapMb { get; set; }
    public double UptimeSeconds { get; set; }

    /// <summary>Encoder or Editor.</summary>
    public string Mode { get; set; } = "";

    public bool SelfContained { get; set; }

    /// <summary>The FFmpeg and libmpv builds in use, by their version strings only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ffmpeg { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mpv { get; set; }

    // Since 4.1.0 (Pass 4.1B), each left out when it could not be learnt. Old reports have none of them.

    /// <summary>The processor's model as it names itself ("AMD Ryzen 9 7950X 16-Core Processor").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cpu { get; set; }

    /// <summary>Its cores, beside <see cref="ProcessorCount"/> (its logical processors): fewer cores than processors is SMT on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PhysicalCores { get; set; }

    /// <summary>The memory installed, in GB.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RamGb { get; set; }

    /// <summary>The speed the memory runs at, in MT/s, and the modules' rated speed (higher when XMP / EXPO is off).</summary>
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

    /// <summary>"Balanced, Best performance, on AC": Windows' power plan (by its well-known GUID, else "custom plan"), its power mode, the power source.</summary>
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

/// <summary>One moment before the crash: when (seconds after the process started), what kind of thing, and what (sanitized).</summary>
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

    /// <summary>Reads a report from its text. Anything that is not a format 1 crash report is refused, with the reason.</summary>
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
/// A crash's signature: the same fault, wherever and whenever it happens, hashes the same, so reports can be grouped and
/// a fault seen again after it was fixed can be told apart from a new one. Made from the exception's type and the first
/// frames of HandPeg's own code (or, with none, the first frames at all), normalized so that a rebuild, a line moved or
/// a compiler-made name renumbered does not change it; for a native fault, from its code and module.
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
    /// One frame as a signature and a report carry it: the method's full name without its parameters, file, line or
    /// offset, and with the numbers the compiler puts into the names it makes (lambdas, local functions, state machines)
    /// taken out. "   at HandPegApp.X.&lt;Run&gt;b__12_0(Object o) in C:\src\X.cs:line 40" is "HandPegApp.X.&lt;Run&gt;b__".
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
