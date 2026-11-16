using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Per-project saved workspace -----
// Saving writes the current window state to the project's "workspace" section (ProjectWorkspace): dock layout, Media Browser view and
// 3D Exploded View camera, the same three a backgrounded tab keeps in its header (MainWindow.ProjectTabs.cs). Excluded from the dirty
// fingerprint (ProjectState.Workspace is JsonIgnore), so rearranging never marks a project modified. Restored on open when
// AppSettings.RestoreProjectWorkspaceOnOpen is enabled (Layout selector footer and Settings).
public partial class MainWindow
{
    private void InitializeProjectWorkspace()
    {
        _viewModel.WorkspaceForSave = WorkspaceForSave;
        _viewModel.WorkspaceOpened += RestoreProjectWorkspace;
    }

    /// <summary>Workspace snapshot for saving; null off the UI thread or while the active tab has not been resumed yet.</summary>
    private ProjectWorkspace? WorkspaceForSave()
    {
        if (!Dispatcher.CheckAccess() || _unresumed.Contains(_tabs.Active.Id))
            return null;

        // Never save a Layout Selector preview; save the arrangement that preceded it.
        var pose = new ProjectTabHeader();
        TakeExplodedState(pose, _explodedUi && _gpu is { } engine ? engine.TargetPose : null, _explodedWaitingTab, _explodedLastPose);
        return new ProjectWorkspace
        {
            LayoutJson = _previewing >= 0 && !_layoutKept ? _layoutBeforePreview : _dock.Save(),
            MediaView = MediaBrowser.ViewMode.ToString(),
            ExplodedOpen = pose.ExplodedOpen,
            ExplodedYaw = pose.ExplodedYaw,
            ExplodedPitch = pose.ExplodedPitch,
            ExplodedZoom = pose.ExplodedZoom,
            ExplodedSpacing = pose.ExplodedSpacing,
        };
    }

    /// <summary>
    /// Restores a project's saved workspace on open into the active tab (its header adopts it, as a tab switch would): Media Browser view
    /// before the layout build, then the layout, then the 3D view at its camera (or flat with the camera retained).
    /// </summary>
    private void RestoreProjectWorkspace(ProjectWorkspace workspace)
    {
        if (!AppSettings.Current.RestoreProjectWorkspaceOnOpen)
            return;

        var tab = _tabs.Active;
        (tab.ExplodedOpen, tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing) =
            (workspace.ExplodedOpen, workspace.ExplodedYaw, workspace.ExplodedPitch, workspace.ExplodedZoom, workspace.ExplodedSpacing);
        try
        {
            EndLayoutPreview();
            if (MediaViewOf(workspace.MediaView) is { } view && view != MediaBrowser.ViewMode)
                MediaBrowser.ViewMode = view;
            if (workspace.LayoutJson is { Length: > 0 } json && !string.Equals(json, _dock.Save(), StringComparison.Ordinal))
            {
                _dock.Load(json);
                _viewModel.SavePaneLayout(SaveDockInto);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Invalid saved layout: log and continue; the project still opens.
            AppLog.Write("Project workspace: the saved layout could not be laid out", ex);
        }

        if (workspace.ExplodedOpen)
        {
            RestoreExplodedForTab(tab);
            return;
        }

        _explodedLastPose = new(workspace.ExplodedYaw, workspace.ExplodedPitch, workspace.ExplodedZoom, workspace.ExplodedSpacing);
        if (_explodedUi)
            SetExplodedView(false, instant: true);
    }
}
