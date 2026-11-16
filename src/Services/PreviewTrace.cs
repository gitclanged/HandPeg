using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Preview-build tracing: on when <see cref="FlagFile"/> sits beside HandPeg.exe (preview zips only, never official releases) or
/// HANDPEG_PREVIEW_TRACE=1 (inherited by child processes). When on, GpuNativeBreadcrumbs and DevFlightRecorder run as if their own flags
/// were set; otherwise both stay dormant unless individually enabled.
/// </summary>
public static class PreviewTrace
{
    /// <summary>The flag file's name, beside HandPeg.exe.</summary>
    public const string FlagFile = "preview-trace.flag";

    /// <summary>The environment variable that turns it on ("1").</summary>
    public const string Variable = "HANDPEG_PREVIEW_TRACE";

    private static bool? _on;

    /// <summary>Whether tracing is on for this process (decided once).</summary>
    public static bool IsOn => _on ??= Asked(Environment.GetEnvironmentVariable(Variable), AppContext.BaseDirectory);

    /// <summary>True when the env value or the folder flag file enables tracing (pure except one file check; golden-tested).</summary>
    internal static bool Asked(string? variable, string? folder)
    {
        if (variable is "1" or "true" or "TRUE" or "True")
            return true;
        try
        {
            return !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, FlagFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
