namespace HandPegApp.Services;

// ----- Project commands trusted on this PC -----

public sealed partial class AppSettings
{
    /// <summary>Max trusted project-command fingerprints retained (oldest evicted first).</summary>
    public const int TrustedProjectCommandsKept = 64;

    /// <summary>
    /// SHA-256 fingerprints of project FFmpeg commands and Extra options confirmed or authored locally; matching projects run without a
    /// prompt. Machine-local (never in project files), newest last; unseen commands from elsewhere still prompt.
    /// </summary>
    public List<string> TrustedProjectCommands { get; set; } = [];
}
