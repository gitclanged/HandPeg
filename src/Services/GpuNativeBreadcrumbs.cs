using System.Diagnostics;
using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Native GPU breadcrumbs for diagnosing hard hangs: ENTER/LEAVE lines around hang-prone D3D11, DXGI and Media Foundation calls (keyed
/// mutexes, Present, ResizeBuffers, Source Reader read/seek, MFTs, overlay window region). Lines bypass the OS cache so after a freeze the
/// file names the in-flight call, process, thread and adapter.
/// <para>Two write disciplines (<see cref="BreadcrumbFile"/>):</para>
/// <list type="bullet">
/// <item><see cref="Enter(string, long)"/>, <see cref="Enter(string, string)"/> and <see cref="Mark"/> write through synchronously; used
/// for infrequent calls (open, seek, resize, transform setup).</item>
/// <item><see cref="EnterHot"/> wraps per-frame calls (Present, AcquireSync/ReleaseSync, ReadSample, CopyHardwareFrame,
/// ProcessInput/ProcessOutput): formatted allocation-free into a per-thread slot, flushed by a background thread every
/// <see cref="BreadcrumbFile.FlushMilliseconds"/> ms, so any call blocked longer than one period is on disk well within the 2 s TDR
/// window.</item>
/// </list>
/// <para>Enabled by <see cref="FlagFile"/> in <see cref="Folder"/>, HANDPEG_GPU_BREADCRUMBS=1 or preview tracing
/// (<see cref="PreviewTrace.IsOn"/>), read once at start. Disabled cost: one static null check.</para>
/// </summary>
internal static class GpuNativeBreadcrumbs
{
    /// <summary>Where the breadcrumbs and the sentinel's log are kept: %LOCALAPPDATA%\HandPeg\CrashReports.</summary>
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandPeg", "CrashReports");

    /// <summary>Flag file in <see cref="Folder"/> enabling breadcrumbs for subsequently started processes.</summary>
    public const string FlagFile = "gpu-breadcrumbs.on";

    /// <summary>Whether this process leaves breadcrumbs (decided once, at its start).</summary>
    public static readonly bool On = IsAsked();

    /// <summary>The card this process draws on (its LUID), named on every line; set when its device is made.</summary>
    public static long Adapter;

    private static readonly string Role = Environment.GetCommandLineArgs().Any(a => a.Equals("--compositor-worker", StringComparison.OrdinalIgnoreCase)) ? "worker" : "editor";
    private static readonly BreadcrumbFile? Log = On ? BreadcrumbFile.OpenInFolder(Folder, Role) : null;

    /// <summary>In-flight call token; <see cref="Leave"/> records LEAVE (no-op when disabled).</summary>
    public readonly struct Crumb
    {
        private readonly BreadcrumbFile? _file;
        private readonly string? _op;
        private readonly long _started;
        private readonly bool _hot;

        internal Crumb(BreadcrumbFile file, string op, long started, bool hot) => (_file, _op, _started, _hot) = (file, op, started, hot);

        public void Leave(int hresult = 0) => _file?.Leave(_op!, _started, hresult, _hot);
    }

    /// <summary>Before a native call: its ENTER line is on disk before this returns.</summary>
    public static Crumb Enter(string op, long detail = 0) => Log is { } log ? log.Enter(op, detail, hot: false) : default;

    /// <summary>Before a native call, with a short description of its arguments (written through).</summary>
    public static Crumb Enter(string op, string detail) => Log is { } log ? log.Enter(op, detail) : default;

    /// <summary>Per-frame ENTER recorded in memory and flushed by the background writer.</summary>
    public static Crumb EnterHot(string op, long detail = 0) => Log is { } log ? log.Enter(op, detail, hot: true) : default;

    /// <summary>A standalone line (a state reached, a choice made), written through.</summary>
    public static void Mark(string op, string detail) => Log?.Mark(op, detail);

    private static bool IsAsked()
    {
        try
        {
            return Environment.GetEnvironmentVariable("HANDPEG_GPU_BREADCRUMBS") == "1" || File.Exists(Path.Combine(Folder, FlagFile)) || PreviewTrace.IsOn;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
