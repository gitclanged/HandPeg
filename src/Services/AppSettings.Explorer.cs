namespace HandPegApp.Services;

// ----- Explorer previews (Pass 5.2D) -----
//
// Settings > General > Projects and saving: the project previews in Windows Explorer (ExplorerThumbnails).

public sealed partial class AppSettings
{
    /// <summary>
    /// .hproj and .hpb files show their baked composite frame as their preview in Windows Explorer (on at first): a thumbnail handler of
    /// HandPeg's own, registered for this user only (HKCU\Software\Classes, no administrator), taken out again when switched off
    /// (ExplorerThumbnails). Not in a self-contained (portable) copy, which leaves nothing on the PC.
    /// </summary>
    public bool ShowExplorerThumbnails { get; set; } = true;
}
