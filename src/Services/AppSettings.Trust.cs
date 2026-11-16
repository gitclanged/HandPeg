namespace HandPegApp.Services;

// ----- Project commands trusted on this PC -----

public sealed partial class AppSettings
{
    /// <summary>How many trusted project-command fingerprints are kept (the oldest go first).</summary>
    public const int TrustedProjectCommandsKept = 64;

    /// <summary>
    /// SHA-256 fingerprints of the FFmpeg commands and Extra options a project file carried that the user confirmed on this PC, or wrote
    /// here and saved: a project bringing one of them back runs it without asking again. Kept on this PC only (never in a project file),
    /// newest last; a project from elsewhere with a command never seen here still asks.
    /// </summary>
    public List<string> TrustedProjectCommands { get; set; } = [];
}
