using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HandPegApp.Services;

/// <summary>
/// The crashes folder and what the two writers of reports share: HandPeg itself (<see cref="CrashReporter"/>) and its
/// sentinel (<see cref="CrashSentinel"/>), which knows nothing of the settings or of <see cref="AppPaths"/> and is
/// handed the folder. Nothing here loads WPF.
/// </summary>
public static class CrashReports
{
    /// <summary>How many reports are kept: the newest this many; older ones are deleted as each new one is written.</summary>
    public const int Kept = 50;

    /// <summary>
    /// Writes a report into the folder, whole: to a temporary name first, then moved into place, so the folder never
    /// holds half a report (HandPeg.CrashDesk watches it). Named by when and what: 20261024-153012-1a2b3c4d5e6f7a8b.hpcrash.
    /// Returns the report's path.
    /// </summary>
    public static string Write(CrashReport report, string folder)
    {
        Directory.CreateDirectory(folder);
        var stamp = report.CrashedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{stamp}-{report.Signature}{CrashReport.Extension}");

        // Two reports of one fault in the same second (a dispatcher fault and the task it ended) keep both.
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stamp}-{report.Signature}-{n}{CrashReport.Extension}"));

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, WriteOptions));
        File.Move(temporary, path, overwrite: true);
        Trim(folder);
        return path;
    }

    // The format's own options, with the text written as it is: a report is meant to be opened and read by whoever
    // has it, and "<file>.mkv" reads better than "<file>.mkv". (Any JSON reader reads both.)
    private static readonly JsonSerializerOptions WriteOptions = new(CrashReportJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Deletes all but the newest <see cref="Kept"/> reports, and temporary files a writer that died left behind.</summary>
    public static void Trim(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            foreach (var leftover in directory.EnumerateFiles("*" + CrashReport.Extension + ".tmp"))
                if (leftover.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-5))
                    leftover.Delete();

            foreach (var old in directory.GetFiles("*" + CrashReport.Extension).OrderByDescending(f => f.LastWriteTimeUtc).Skip(Kept))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for now; the next report tries again.
        }
    }

    /// <summary>How many reports the folder holds, and their size in bytes (for Settings).</summary>
    public static (int Count, long Bytes) Measure(string folder)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles("*" + CrashReport.Extension);
            return (files.Length, files.Sum(f => f.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    /// <summary>Deletes every report in the folder (Settings: Clear Crash Reports). Returns how many went.</summary>
    public static int Clear(string folder)
    {
        var deleted = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).GetFiles("*" + CrashReport.Extension))
            {
                file.Delete();
                deleted++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A report open elsewhere stays.
        }

        return deleted;
    }

    // ----- Sending them (Pass 4.1B) -----

    /// <summary>The file in the crashes folder that names, one per line, every report already sent: each is sent once.</summary>
    public const string SentManifest = "sent.txt";

    /// <summary>The most reports one pass sends: a folder that filled while offline is sent over a few starts, not in one burst.</summary>
    public const int MaxSentPerPass = 20;

    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(20);

    private static HttpClient? _http;

    /// <summary>
    /// Whether an endpoint may be sent to: an absolute https address, or http to this PC (a test server). Anything
    /// else is refused, so a mistyped setting never sends a report in the clear.
    /// </summary>
    public static bool IsUsableEndpoint(string? endpoint, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var parsed))
            return false;
        uri = parsed;
        return parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);
    }

    /// <summary>The reports in the folder not sent yet, oldest first (their file names begin with when they were written).</summary>
    public static List<string> Pending(string folder)
    {
        try
        {
            var sent = ReadSent(folder);
            return new DirectoryInfo(folder).GetFiles("*" + CrashReport.Extension)
                .Select(f => f.Name)
                .Where(name => !sent.Contains(name))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Sends the reports not sent yet, each once: read whole, checked to be a crash report (no other file in the folder
    /// is ever sent), compressed with GZip and POSTed as JSON (Content-Encoding: gzip). A report the service answered
    /// for (accepted, or refused for what it is) is written into <see cref="SentManifest"/>; when the service cannot be
    /// reached, is busy or fails, the pass stops and the rest wait for the next start. Never throws, shows nothing.
    /// Returns how many were sent.
    /// </summary>
    public static async Task<int> UploadPendingAsync(string folder, string endpoint, CancellationToken token)
    {
        if (!IsUsableEndpoint(endpoint, out var uri))
            return 0;

        var sent = 0;
        foreach (var name in Pending(folder).Take(MaxSentPerPass))
        {
            token.ThrowIfCancellationRequested();
            byte[] json;
            try
            {
                json = await File.ReadAllBytesAsync(Path.Combine(folder, name), token);
                _ = CrashReportJson.Deserialize(System.Text.Encoding.UTF8.GetString(json));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                // Not a crash report: never sent, and not looked at again.
                MarkSent(folder, name);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open elsewhere just now: tried again at the next start.
                continue;
            }

            try
            {
                using var content = new ByteArrayContent(Compress(json));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                content.Headers.ContentEncoding.Add("gzip");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(UploadTimeout);
                using var response = await Http.PostAsync(uri, content, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is 408 or 429 or >= 500)
                    break;

                // Accepted, or refused for what it is (too large, not wanted): either way it is not sent again.
                MarkSent(folder, name);
                if (response.IsSuccessStatusCode)
                    sent++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                break;
            }
        }

        return sent;
    }

    private static HttpClient Http => _http ??= MakeClient();

    private static HttpClient MakeClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HandPeg/" + AppVersion);
        return client;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data);
        return output.ToArray();
    }

    private static HashSet<string> ReadSent(string folder)
    {
        var file = Path.Combine(folder, SentManifest);
        return File.Exists(file)
            ? new HashSet<string>(File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Adds a report to the manifest, keeping only the names of reports still in the folder.</summary>
    private static void MarkSent(string folder, string name)
    {
        try
        {
            var present = new HashSet<string>(new DirectoryInfo(folder).GetFiles("*" + CrashReport.Extension).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var sent = ReadSent(folder);
            sent.Add(name);
            var file = Path.Combine(folder, SentManifest);
            File.WriteAllLines(file + ".tmp", sent.Where(present.Contains).Order(StringComparer.Ordinal));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not written: the report may be sent again at the next start, which the service takes as the same report.
        }
    }

    // ----- What the computer and the program are -----

    /// <summary>HandPeg's version as its releases are numbered ("4.1.0"): the informational version, without the "+commit" a build may add.</summary>
    public static string AppVersion { get; } = (typeof(CrashReports).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashReports).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0].Trim();

    /// <summary>"Windows 11 (10.0.26300)": Windows numbers both as 10, so the 11 is told by the build.</summary>
    public static string OsDescription()
    {
        var version = Environment.OSVersion.Version;
        var name = version is { Major: 10, Build: >= 22000 } ? "Windows 11" : version.Major == 10 ? "Windows 10" : "Windows";
        return string.Create(CultureInfo.InvariantCulture, $"{name} ({version.Major}.{version.Minor}.{version.Build})");
    }

    /// <summary>".NET 8.0.11".</summary>
    public static string RuntimeDescription() => RuntimeInformation.FrameworkDescription;

    /// <summary>
    /// What an exception code is, for a report: "0xC0000005 EXCEPTION_ACCESS_VIOLATION". Codes not known here are the
    /// number alone; an exit code that is not an NTSTATUS (a process ended from outside) says so.
    /// </summary>
    public static string DescribeCode(uint code)
    {
        var name = code switch
        {
            0xC0000005 => "EXCEPTION_ACCESS_VIOLATION",
            0xC0000006 => "EXCEPTION_IN_PAGE_ERROR",
            0xC000001D => "EXCEPTION_ILLEGAL_INSTRUCTION",
            0xC0000094 => "EXCEPTION_INT_DIVIDE_BY_ZERO",
            0xC0000096 => "EXCEPTION_PRIV_INSTRUCTION",
            0xC00000FD => "EXCEPTION_STACK_OVERFLOW",
            0xC0000135 => "STATUS_DLL_NOT_FOUND",
            0xC0000139 => "STATUS_ENTRYPOINT_NOT_FOUND",
            0xC0000142 => "STATUS_DLL_INIT_FAILED",
            0xC0000374 => "STATUS_HEAP_CORRUPTION",
            0xC0000409 => "STATUS_STACK_BUFFER_OVERRUN",
            0xC0000417 => "STATUS_INVALID_CRUNTIME_PARAMETER",
            0xC0000420 => "STATUS_ASSERTION_FAILURE",
            0xC000041D => "STATUS_FATAL_USER_CALLBACK_EXCEPTION",
            0xC0000602 => "STATUS_FAIL_FAST_EXCEPTION",
            0xC000013A => "STATUS_CONTROL_C_EXIT",
            0x80000003 => "EXCEPTION_BREAKPOINT",
            0xE0434352 => "CLR_EXCEPTION",
            0x80131623 => "COR_E_FAILFAST",
            0x40010004 => "DBG_TERMINATE_PROCESS",
            _ => null,
        };

        var number = string.Create(CultureInfo.InvariantCulture, $"0x{code:X8}");
        return name is not null ? $"{number} {name}"
            : code < 0x10000 ? $"{number} exit code {code.ToString(CultureInfo.InvariantCulture)}"
            : number;
    }
}
