using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HandPegApp.Services;

/// <summary>
/// Crash folder logic shared by <see cref="CrashReporter"/> and <see cref="CrashSentinel"/> (which receives the folder and never uses
/// settings or <see cref="AppPaths"/>). WPF-free.
/// </summary>
public static class CrashReports
{
    /// <summary>Number of newest reports retained; older ones are pruned on each write.</summary>
    public const int Kept = 50;

    /// <summary>
    /// Writes a report atomically (temp name, then move; HandPeg.CrashDesk watches the folder), named timestamp-signature
    /// (yyyyMMdd-HHmmss-&lt;signature&gt;.hpcrash). Returns the path.
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

    // Serializer options with relaxed escaping so reports stay human-readable (any JSON reader accepts both forms).
    private static readonly JsonSerializerOptions WriteOptions = new(CrashReportJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Keeps the newest <see cref="Kept"/> reports and deletes orphaned temp files.</summary>
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

    // ----- Upload -----

    /// <summary>Manifest of sent report names (one per line) ensuring each is sent once.</summary>
    public const string SentManifest = "sent.txt";

    /// <summary>Per-pass upload cap, spreading an offline backlog over several starts.</summary>
    public const int MaxSentPerPass = 20;

    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(20);

    private static HttpClient? _http;

    /// <summary>
    /// True for an absolute https endpoint or http loopback (test server); anything else is refused so reports never go out in clear text.
    /// </summary>
    public static bool IsUsableEndpoint(string? endpoint, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var parsed))
            return false;
        uri = parsed;
        return parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);
    }

    /// <summary>Unsent reports, oldest first (names start with the timestamp).</summary>
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
    /// Uploads unsent reports once each: validated as crash reports (no other file is ever sent), gzip-compressed JSON POST. Reports the
    /// service answered (accepted or rejected) go into <see cref="SentManifest"/>; unreachable, busy or failing service stops the pass
    /// until next start. Never throws or shows UI. Returns the sent count.
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

    /// <summary>Release version (informational version without any "+commit" suffix).</summary>
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
    /// Describes an exception code ("0xC0000005 EXCEPTION_ACCESS_VIOLATION"); unknown codes are numeric only, and non-NTSTATUS exit codes
    /// (external termination) are labelled as such.
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
