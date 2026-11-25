namespace HandPegApp.Shell;

/// <summary>
/// HandPeg.exe --dpi-matrix (Pass 5.6.5 seam, dispatched first by Program.Main on its STA thread): builds MainWindow, the player overlay
/// (VideoSurface.OverlayWindow), PlayerFullscreenWindow, MaskDrawWindow and FirstRunWindow at simulated per-window DPI scales (100-250 %)
/// and aspect ratios (16:9, 16:10, 3:2, 21:9), visible on the primary monitor, and checks their geometry with GetWindowRect and UIA
/// ElementFromPoint. Exit code: <see cref="Passed"/>, <see cref="Failed"/> (a geometry check failed) or <see cref="NotRun"/> (could not run).
/// </summary>
internal static class DpiMatrixRunner
{
    /// <summary>The command-line switch, as the first argument.</summary>
    public const string Argument = "--dpi-matrix";

    /// <summary>Process exit codes.</summary>
    public const int Passed = 0, Failed = 1, NotRun = 2;

    /// <summary>Runs the matrix and returns its exit code (the matrix itself lands with the runner; until then nothing runs).</summary>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return NotRun;
    }
}
