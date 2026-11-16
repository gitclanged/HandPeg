namespace HandPegApp.Services;

// Side windows (any number, each a Live Video Preview or Pane Docker), stored separately from DockLayout so applying a layout leaves
// them untouched.
public sealed partial class AppSettings
{
    /// <summary>
    /// Last-session side windows as JSON (Docking.SideWindowState list: mode, panes, placement); null when never saved (a legacy
    /// DockLayout side window becomes the first Pane Docker), "[]" for none. Stored as a string like DockLayout so Clone copies it verbatim.
    /// </summary>
    public string? SideWindows { get; set; }
}
