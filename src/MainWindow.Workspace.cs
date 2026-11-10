using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

// ----- A project's saved window (Pass 5.4.1-M5) -----
// A save writes the window as it is into the project's "workspace" section (ProjectWorkspace): the dock layout, the Media Browser's
// view and the 3D Exploded View's camera, the same three a tab put behind keeps in its header (MainWindow.ProjectTabs.cs). Never part of
// the unsaved-changes fingerprint (ProjectState.Workspace is JsonIgnore), so rearranging the window never makes a project dirty. Opening
// a project takes it up again when AppSettings.RestoreProjectWorkspaceOnOpen is on (the Layout selector's footer and Settings).
public partial class MainWindow
{
    private void InitializeProjectWorkspace()
    {
        _viewModel.WorkspaceForSave = WorkspaceForSave;
        _viewModel.WorkspaceOpened += RestoreProjectWorkspace;
    }

    /// <summary>The window as a save writes it; null off the interface's thread or while the tab in front is not brought back yet.</summary>
    private ProjectWorkspace? WorkspaceForSave()
    {
        if (!Dispatcher.CheckAccess() || _unresumed.Contains(_tabs.Active.Id))
            return null;

        // A layout the Layout Selector is only previewing is never written: the arrangement in use before it is.
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
    /// A project's saved window taken up as it opens, into the tab in front (its header takes it as its own, as a switch would lay it
    /// out): the Media Browser's view before the layout's build, the layout, then the 3D view at its camera (or flat, its camera kept).
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
            // A layout that could not be laid out is let go (written down): the project is open all the same.
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
