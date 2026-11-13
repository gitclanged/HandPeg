using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// The always-on tracing of a preview build (5.4.4-Final, CLAUDE.md 5a): on when <see cref="FlagFile"/> lies beside HandPeg.exe (every zip
/// local\PreviewBuild.ps1 makes has it; an official release's has not) or the environment says HANDPEG_PREVIEW_TRACE=1 (LaunchLatestPreview
/// sets it; the compositor worker and other child processes inherit it). On, the native call breadcrumbs (GpuNativeBreadcrumbs) and the
/// developer flight recorder (DevFlightRecorder) run as if their own flags were set; off, both stay dormant unless their own flags ask.
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

    /// <summary>Whether the variable's value or the folder's flag file asks for it. Pure but for the one file check, for the golden test.</summary>
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
