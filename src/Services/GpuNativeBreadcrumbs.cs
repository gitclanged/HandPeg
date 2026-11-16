using System.Diagnostics;
using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Native GPU breadcrumbs: a crash tracer for hard GPU hangs. Before a native Direct3D 11, DXGI or Media Foundation call that can hang
/// the driver (keyed mutexes, Present, ResizeBuffers, Source Reader reads and seeks, encoder and decoder transforms, the overlay's window
/// region) an ENTER line is recorded, and a LEAVE line after it returns. A buffered log is lost when Windows freezes or resets; these
/// lines bypass the disk cache, so after a hard freeze the file names the call in flight, its process, thread and adapter.
/// <para>Two write disciplines (<see cref="BreadcrumbFile"/>):</para>
/// <list type="bullet">
/// <item><see cref="Enter(string, long)"/>, <see cref="Enter(string, string)"/> and <see cref="Mark"/> write their line through to the
/// disk before returning. They wrap calls made a few times per session (open, seek, resize, transform set-up).</item>
/// <item><see cref="EnterHot"/> wraps the per-frame calls (Present, AcquireSync/ReleaseSync, Source Reader ReadSample, CopyHardwareFrame,
/// transform ProcessInput/ProcessOutput). Its lines are formatted into the thread's slot without allocating and without I/O; a
/// background thread writes each changed slot every <see cref="BreadcrumbFile.FlushMilliseconds"/> ms. A call blocked for longer than
/// one flush period is therefore on disk with its ENTER line, well inside the 2 s TDR delay. Only a whole-system freeze within one
/// flush period of a hot call can lose that call's line.</item>
/// </list>
/// <para>Enabled by the flag file <see cref="FlagFile"/> in <see cref="Folder"/>, HANDPEG_GPU_BREADCRUMBS=1, or a preview build's
/// tracing (<see cref="PreviewTrace.IsOn"/>), read once at process start. Disabled, a call costs one null test of a static field.</para>
/// </summary>
internal static class GpuNativeBreadcrumbs
{
    /// <summary>Where the breadcrumbs and the sentinel's log are kept: %LOCALAPPDATA%\HandPeg\CrashReports.</summary>
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandPeg", "CrashReports");

    /// <summary>The file whose presence in <see cref="Folder"/> turns the breadcrumbs on for processes started after it.</summary>
    public const string FlagFile = "gpu-breadcrumbs.on";

    /// <summary>Whether this process leaves breadcrumbs (decided once, at its start).</summary>
    public static readonly bool On = IsAsked();

    /// <summary>The card this process draws on (its LUID), named on every line; set when its device is made.</summary>
    public static long Adapter;

    private static readonly string Role = Environment.GetCommandLineArgs().Any(a => a.Equals("--compositor-worker", StringComparison.OrdinalIgnoreCase)) ? "worker" : "editor";
    private static readonly BreadcrumbFile? Log = On ? BreadcrumbFile.OpenInFolder(Folder, Role) : null;

    /// <summary>A native call in flight: <see cref="Leave"/> records its LEAVE line (a no-op when breadcrumbs are disabled).</summary>
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

    /// <summary>Before a per-frame native call: the ENTER line is recorded in memory and flushed by the background writer.</summary>
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
