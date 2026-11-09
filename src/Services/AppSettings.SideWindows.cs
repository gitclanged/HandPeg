namespace HandPegApp.Services;

// The side windows (Pass 5.4C): as many as wanted, each a Live Video Preview or a Pane Docker, saved apart from the main window's
// arrangement (DockLayout) so a layout taken up never touches them.
public sealed partial class AppSettings
{
    /// <summary>
    /// The side windows open as HandPeg was last left (a JSON list of Docking.SideWindowState: each one's mode, its panes and its
    /// place); null before 5.4C first saved them (the single side window of an older DockLayout then becomes the first Pane Docker),
    /// "[]" for none. A string, as DockLayout is, so Clone copies it as it is.
    /// </summary>
    public string? SideWindows { get; set; }
}
