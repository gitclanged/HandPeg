namespace HandPegBootstrap;

/// <summary>What HandPeg.exe does once it has looked for .NET.</summary>
internal enum StartAction
{
    /// <summary>Run HandPeg.dll on the .NET found.</summary>
    Host,

    /// <summary>End at once with no window, no message and no download (an installer's hook, or one of HandPeg's own background processes).</summary>
    QuietExit,

    /// <summary>Ask the user how to get .NET 10 (the choice dialog).</summary>
    Ask,
}

/// <summary>What Microsoft's .NET installer's exit code means for HandPeg.</summary>
internal enum InstallerOutcome
{
    /// <summary>Installed: look for it and start.</summary>
    Installed,

    /// <summary>Installed, and Windows would like a restart (3010), or has begun one (1641): look for it and start all the same.</summary>
    RestartSuggested,

    /// <summary>The user said no (the UAC prompt declined, or the installer cancelled): back to the choice dialog.</summary>
    Cancelled,

    /// <summary>Another installation is running (Windows Installer is busy): try again once it has finished.</summary>
    Busy,

    /// <summary>Anything else: the installer failed, with its code.</summary>
    Failed,
}

/// <summary>
/// HandPeg.exe's decisions on its command line (Pass 5.3-Stage1), pure and shared with the golden tests (portable-bootstrap). Velopack's
/// Setup and Update.exe start HandPeg.exe with a --veloapp-* argument (install, updated, obsolete, uninstall) and wait for it: with no
/// .NET 10 there is nothing of HandPeg's to run for such a hook, so it ends at once with exit code 0 (a non-zero one makes Setup say the
/// install "Partially Succeeded"), never with a window that would hold the installer up. HandPeg's own background processes (the GPU
/// compositor, the hardware benchmark, the crash sentinel: their argument first, as src\Program.cs reads it) never show anything either:
/// with no .NET they end with exit code 1, which their parent reports. Any other start with no .NET 10 asks the user.
/// </summary>
internal static class Launch
{
    /// <summary>Windows' ERROR_CANCELLED: what ShellExecuteEx says when the UAC prompt is declined.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>The first arguments of HandPeg's own background processes (CrashSentinel, CompositorWorker, HardwareBenchmark .Argument).</summary>
    public static readonly string[] BackgroundArguments = ["--compositor-worker", "--hw-benchmark", "--crash-sentinel"];

    /// <summary>Whether Velopack started this process for one of its hooks: any argument starting --veloapp- (as src\Program.cs tests it).</summary>
    public static bool IsInstallerHook(IReadOnlyList<string> arguments) =>
        arguments.Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this is one of HandPeg's own background processes: its argument first, exactly.</summary>
    public static bool IsBackground(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && Array.IndexOf(BackgroundArguments, arguments[0]) >= 0;

    /// <summary>Whether this start may never show anything: an installer's hook or a background process.</summary>
    public static bool IsQuiet(IReadOnlyList<string> arguments) => IsInstallerHook(arguments) || IsBackground(arguments);

    /// <summary>What to do once .NET has been looked for (<paramref name="found"/>: a usable .NET 10).</summary>
    public static StartAction Decide(bool found, IReadOnlyList<string> arguments) =>
        found ? StartAction.Host : IsQuiet(arguments) ? StartAction.QuietExit : StartAction.Ask;

    /// <summary>The exit code of a quiet start that cannot run HandPeg: 0 for an installer's hook (nothing of it failed), 1 for a background process.</summary>
    public static int QuietExitCode(IReadOnlyList<string> arguments) => IsInstallerHook(arguments) ? 0 : 1;

    /// <summary>
    /// What Microsoft's installer's exit code (or <see cref="ErrorCancelled"/> from a declined UAC prompt) means: 0 installed; 3010
    /// (ERROR_SUCCESS_REBOOT_REQUIRED) and 1641 (ERROR_SUCCESS_REBOOT_INITIATED) installed with a restart wanted; 1223 and 1602
    /// (ERROR_INSTALL_USEREXIT) cancelled; 1618 (ERROR_INSTALL_ALREADY_RUNNING) busy; anything else failed.
    /// </summary>
    public static InstallerOutcome OutcomeOf(int exitCode) => exitCode switch
    {
        0 => InstallerOutcome.Installed,
        3010 or 1641 => InstallerOutcome.RestartSuggested,
        ErrorCancelled or 1602 => InstallerOutcome.Cancelled,
        1618 => InstallerOutcome.Busy,
        _ => InstallerOutcome.Failed,
    };
}
