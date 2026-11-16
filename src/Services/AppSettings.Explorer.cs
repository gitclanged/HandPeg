namespace HandPegApp.Services;

// ----- Explorer previews -----
//
// Settings > General > Projects and saving: the project previews in Windows Explorer (ExplorerThumbnails).

public sealed partial class AppSettings
{
    /// <summary>
    /// Show baked thumbnails for .hproj/.hpb in Explorer (default on) via a per-user handler (HKCU\Software\Classes, no admin), removed
    /// when disabled (ExplorerThumbnails). Not registered by portable copies.
    /// </summary>
    public bool ShowExplorerThumbnails { get; set; } = true;
}
