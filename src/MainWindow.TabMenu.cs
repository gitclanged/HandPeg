using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Project tabs: a tab's right-click menu and hover card (split from MainWindow.ProjectTabs.cs, 5.4.1-M5) -----
public partial class MainWindow
{
    // ----- A tab's right-click menu and hover card (Pass 5.4B) -----

    /// <summary>
    /// The height a cold tab's thumbnail is decoded at for its hover card, in pixels (5.4C): the thumbnail's own (its short side is
    /// <see cref="ProjectThumbnail.ShortSide"/>), which is about what the card draws at 200 %: a wide frame's height is its short side; a
    /// tall frame's (its frame known from its header) is longer by its shape, never past twice the card's tallest.
    /// </summary>
    private static int TabCardPictureHeight(int frameWidth, int frameHeight) =>
        frameWidth > 0 && frameHeight > frameWidth
            ? (int)Math.Min(Math.Round(ProjectThumbnail.ShortSide * (double)frameHeight / frameWidth), 2 * ProjectTabItem.CardMaxHeight)
            : ProjectThumbnail.ShortSide;

    /// <summary>A line of a tab's right-click menu chosen (ProjectTabStrip.MenuFor; Rename Project... is <see cref="RenameProjectTab"/>).</summary>
    private async Task DoTabCommandAsync(ProjectTabHeader tab, ProjectTabCommand command)
    {
        try
        {
            switch (command)
            {
                case ProjectTabCommand.Save:
                    await SaveProjectTabAsync(tab, saveAs: false);
                    break;
                case ProjectTabCommand.SaveAs:
                    await SaveProjectTabAsync(tab, saveAs: true);
                    break;
                case ProjectTabCommand.Duplicate:
                    await DuplicateProjectTabAsync(tab);
                    break;
                case ProjectTabCommand.OpenFolder:
                    OpenTabFolder(tab);
                    break;
                case ProjectTabCommand.Close:
                    await CloseProjectTabAsync(tab);
                    break;
                case ProjectTabCommand.CloseOthers:
                    await CloseOtherProjectTabsAsync(tab);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("do what the project tab's menu asked", ex);
        }
    }

    /// <summary>
    /// Save Project and Save Project As... on a tab: the tab in front saved as Ctrl+S and Ctrl+Alt+S save it; a tab behind with a project
    /// file saved to it from its cache file, never brought back for it (as closing it saves it); any other tab behind brought to the front
    /// first, then saved there (a first save asks for its name in the Project Manager).
    /// </summary>
    private async Task SaveProjectTabAsync(ProjectTabHeader tab, bool saveAs)
    {
        if (!_tabs.Tabs.Contains(tab))
            return;

        // Refused mid-switch for the tab in front too: until the switch ends the view model holds part of one tab and part of the other,
        // and its project path is still the tab put behind's (as Ctrl+S is refused then).
        if (_switchingTab)
        {
            _viewModel.StatusText = "Wait for the project tab to finish switching before saving one.";
            return;
        }

        if (KeptInFile(tab) && tab.Empty)
        {
            _viewModel.StatusText = $"\"{tab.Title}\" holds nothing to save yet.";
            return;
        }

        if (KeptInFile(tab) && !saveAs && tab.ProjectPath.Length > 0)
        {
            SaveTabInPlace(tab, tab.Title);
            return;
        }

        if (KeptInFile(tab))
        {
            await SwitchToTabAsync(tab);
            if (!_tabs.Tabs.Contains(tab) || KeptInFile(tab))
                return;
        }

        if (saveAs)
            SaveProjectAs();
        else
            SaveProject();
        RefreshActiveTab();
    }

    /// <summary>
    /// What a name typed for Rename Project... is refused for (pure: project-tabs-menu): nothing typed, a character no file name can hold,
    /// or a name of dots alone; null for a name that will do.
    /// </summary>
    internal static string? RenameRefusal(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Not renamed: type a name for the project.";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Not renamed: a project's name cannot hold \\ / : * ? \" < > |.";
        if (trimmed.Trim('.').Length == 0 || trimmed.EndsWith('.'))
            return "Not renamed: a project's name cannot end with a dot.";
        return null;
    }

    /// <summary>Where a saved project's file goes as it is renamed (pure: project-tabs-menu): beside where it is, under the new name, its extension kept.</summary>
    internal static string RenamedPath(string projectPath, string name) =>
        Path.Combine(Path.GetDirectoryName(projectPath) ?? "", name.Trim() + Path.GetExtension(projectPath));

    /// <summary>
    /// Rename Project... on a tab, taken once with Enter: a project never saved takes the name for its first save (the New Project
    /// studio's name); a saved one has its file renamed beside where it is (its autosave with it), and Save writes there from then on.
    /// Refused (the status line says why) for a fully portable project (its folder is named after it: Save Project As makes a copy under
    /// another name), a file no longer there, or a name another file already has; never written over anything.
    /// </summary>
    private void RenameProjectTab(ProjectTabHeader tab, string name)
    {
        if (!_tabs.Tabs.Contains(tab))
            return;
        if (RenameRefusal(name) is { } why)
        {
            _viewModel.StatusText = why;
            return;
        }

        name = name.Trim();
        var front = !KeptInFile(tab);
        if (front ? !MayChangeTabs("renaming the project") : _switchingTab)
        {
            if (!front)
                _viewModel.StatusText = "Wait for the project tab to finish switching before renaming one.";
            return;
        }

        var path = front ? _viewModel.CurrentProjectPath : tab.ProjectPath;
        if (path.Length == 0)
        {
            if (front)
            {
                _viewModel.KeepNewProjectName(name, _viewModel.NewProjectFolder);
                RefreshActiveTab();
            }
            else
            {
                (tab.Title, tab.NewProjectName) = (name, name);
                WriteTabHeader(tab);
                ShowTabs();
            }

            _viewModel.StatusText = $"Renamed to {name}: its first save is named so.";
            return;
        }

        if (PortableProject.IsPortableFolder(path))
        {
            _viewModel.StatusText = $"Not renamed: {Path.GetFileNameWithoutExtension(path)} is a fully portable project, in a folder named after it. Save Project As makes a copy under a new name.";
            return;
        }

        var target = RenamedPath(path, name);
        if (string.Equals(target, path, StringComparison.Ordinal))
            return;
        try
        {
            if (!File.Exists(path))
            {
                _viewModel.StatusText = $"Not renamed: {path} is not there any more.";
                return;
            }

            // A change of case alone is the same file: moved as it is. Any other name already there is never written over.
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
            {
                _viewModel.StatusText = $"Not renamed: a project is already saved as {target}.";
                return;
            }

            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _viewModel.StatusText = $"Could not rename the project: {ex.Message}";
            return;
        }

        MoveAutosave(path, target);
        CrashReporter.KeepOut(target);
        if (front)
        {
            // Pass 5.5A final review: the file moved as it was (its last write and length with it), so the stamp the tab carries goes with it.
            var stamp = (tab.ProjectFileTicks, tab.ProjectFileLength);
            _viewModel.RenameProjectFile(target);
            RefreshActiveTab();
            (tab.ProjectFileTicks, tab.ProjectFileLength) = stamp;
        }
        else
        {
            (tab.ProjectPath, tab.Title) = (target, name);
            WriteTabHeader(tab);
            ShowTabs();
            _viewModel.StatusText = $"Project renamed: {name}";
        }

        ProjectStore.NotifyChanged();
    }

    /// <summary>A renamed project's autosave beside it renamed with it, when there is one and nothing has the new name (best effort: the log says why not).</summary>
    private static void MoveAutosave(string path, string target)
    {
        var (from, to) = (ProjectStore.AutosavePath(path, ""), ProjectStore.AutosavePath(target, ""));
        try
        {
            if (File.Exists(from) && !File.Exists(to))
                File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Write("Project tabs: a renamed project's autosave could not be renamed with it", ex);
        }
    }

    /// <summary>
    /// A tab behind's header written again after it changed in memory (renamed, saved), away from this thread (only when it has a project
    /// file), after the tab's other disk work (5.4D-TabPerf: a cache file still being written after a switch is written first).
    /// </summary>
    private void WriteTabHeader(ProjectTabHeader tab)
    {
        var (id, json) = (tab.Id, ProjectTabs.HeaderJson(tab));
        _ = _tabs.Enqueue(id, () =>
        {
            try
            {
                _tabs.WriteHeader(id, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Project tabs: a tab's header could not be written again", ex);
            }
        });
    }

    /// <summary>
    /// Duplicate Project in New Tab: a copy of the tab's project, its undo history with it, in a new tab right after it, never saved (its
    /// first save asks for its name, "<c>Name (Copy)</c>" offered, beside the original's file) and brought to the front. A tab behind is
    /// copied from its cache file as it is (5.4D-TabPerf: from its frozen copy when it is kept live), after its own disk work; the tab in
    /// front is captured here and written on the thread pool. A tab holding nothing duplicates as a new blank tab.
    /// </summary>
    private async Task DuplicateProjectTabAsync(ProjectTabHeader tab)
    {
        if (!_tabs.Tabs.Contains(tab))
            return;
        var inFile = KeptInFile(tab);
        if (inFile ? tab.Empty : _viewModel.HoldsNothingForTab)
        {
            await NewProjectTabAsync();
            return;
        }

        if (!MayChangeTabs("duplicating a project tab"))
            return;

        if (!inFile)
            tab.Title = ActiveTabTitle();
        var source = inFile ? tab.ProjectPath : _viewModel.CurrentProjectPath;
        var title = $"{tab.Title} (Copy)";
        var copy = _tabs.Insert(_tabs.IndexOf(tab) + 1, title);
        (copy.Empty, copy.Dirty, copy.ProjectPath, copy.NewProjectName) = (false, true, "", title);
        copy.NewProjectFolder = source.Length > 0
            ? PortableProject.IsPortableFolder(source) ? null : Path.GetDirectoryName(source)
            : inFile ? tab.NewProjectFolder : _viewModel.NewProjectFolder;
        if (inFile)
        {
            (copy.PlayheadMs, copy.ViewStart, copy.ViewSpan, copy.ViewFit, copy.VerticalOffset) = (tab.PlayheadMs, tab.ViewStart, tab.ViewSpan, tab.ViewFit, tab.VerticalOffset);
            (copy.FrameWidth, copy.FrameHeight, copy.FrameRate, copy.WaitingTemplate) = (tab.FrameWidth, tab.FrameHeight, tab.FrameRate, tab.WaitingTemplate);
            copy.TakeWindowStateOf(tab);
        }
        else
        {
            (copy.PlayheadMs, copy.ViewStart, copy.ViewSpan, copy.ViewFit, copy.VerticalOffset) =
                (_viewModel.PositionMs, _viewStart, _viewSpan, _viewFit, UnifiedTimeline.Surface.VerticalOffset);
            copy.WaitingTemplate = _viewModel.TemplateWaitingForTab;
            TakeFrameForTab(copy);
            CaptureWindowStateForTab(copy);
        }

        _switchingTab = true;
        try
        {
            var header = ProjectTabs.HeaderJson(copy);
            if (inFile && _hydrated.GetValueOrDefault(tab.Id) is { } live)
            {
                var (to, frozen) = (copy.Id, live.Project.Frozen);
                await _tabs.Enqueue(tab.Id, () => _tabs.WriteTab(to, frozen, null, header));
            }
            else if (inFile)
            {
                var from = tab.Id;
                await _tabs.Enqueue(from, () => _tabs.CopyState(from, copy.Id, header));
            }
            else
            {
                // Captured here (the view model is this thread's), written away from it.
                var state = _viewModel.CaptureStateForTab(title);
                await _tabs.Enqueue(copy.Id, () => _tabs.WriteTab(copy.Id, state, null, header));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Write("Project tabs: a tab could not be duplicated", ex);
            if (_tabs.Tabs.Contains(copy) && !ReferenceEquals(copy, _tabs.Active))
                _tabs.Close(copy);
            ShowTabs();
            _viewModel.StatusText = $"Could not duplicate \"{tab.Title}\": {ex.Message}";
            return;
        }
        finally
        {
            _switchingTab = false;
        }

        ShowTabs();
        WriteTabManifest();
        if (!_tabs.Tabs.Contains(copy))
            return;
        await SwitchToTabAsync(copy);
        if (ReferenceEquals(copy, _tabs.Active))
            _viewModel.StatusText = $"\"{tab.Title}\" duplicated into a new tab, \"{title}\": not saved yet.";
    }

    /// <summary>Open Containing Folder: the tab's project file shown selected in Explorer (or its folder, should the file be gone).</summary>
    private void OpenTabFolder(ProjectTabHeader tab)
    {
        var path = KeptInFile(tab) ? tab.ProjectPath : _viewModel.CurrentProjectPath;
        if (path.Length == 0)
        {
            _viewModel.StatusText = $"\"{tab.Title}\" is not saved as a project yet, so it has no folder.";
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (File.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                else if (Path.GetDirectoryName(path) is { Length: > 0 } folder && Directory.Exists(folder))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                }
                else
                {
                    Dispatcher.BeginInvoke(() => _viewModel.StatusText = $"The folder of \"{tab.Title}\" is not there any more.");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                AppLog.Write("Project tabs: the project's folder could not be shown", ex);
            }
        });
    }

    /// <summary>
    /// Close Other Tabs: <paramref name="keep"/> brought to the front, then every other tab closed in turn as its × closes it (asked about
    /// when it has unsaved changes). Stops at the first one the user keeps (Cancel), and looks again after every wait: a first save can
    /// bring another tab to the front, and the Project Manager can open another project meanwhile. Pass 5.5A: with two or more of the
    /// others unsaved, they are asked about in one dialog instead (<see cref="CloseOthersAskedOnceAsync"/>).
    /// </summary>
    private async Task CloseOtherProjectTabsAsync(ProjectTabHeader keep)
    {
        if (!_tabs.Tabs.Contains(keep) || _tabs.Count < 2)
            return;
        if (!ReferenceEquals(keep, _tabs.Active))
        {
            await SwitchToTabAsync(keep);
            if (!ReferenceEquals(keep, _tabs.Active))
                return;
        }

        // The others' dots made the exact ones their writes found (a tab put behind just now may not have told its own yet).
        TakeWrittenDots();
        if (_tabs.Tabs.Count(t => !ReferenceEquals(t, keep) && t.Dirty && !t.Empty) >= 2)
        {
            await CloseOthersAskedOnceAsync(keep);
            return;
        }

        foreach (var other in _tabs.Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            if (!_tabs.Tabs.Contains(keep))
                return;
            if (!_tabs.Tabs.Contains(other))
                continue;
            await CloseProjectTabAsync(other);
            if (_tabs.Tabs.Contains(other))
                break;
        }

        // A first save brought another tab to the front on the way: the one kept comes back.
        if (_tabs.Tabs.Contains(keep) && !ReferenceEquals(keep, _tabs.Active) && !_switchingTab)
            await SwitchToTabAsync(keep);
    }

    /// <summary>
    /// Close Other Tabs with two or more of the others unsaved (Pass 5.5A): one dialog for all of them (SaveTabsOnExitDialog, its Close Other
    /// Tabs words). Save Checked &amp; Close Tabs saves the checked ones from their cache files (never brought to the front: a tab never saved
    /// under the name typed for it) and closes every other tab but one whose save failed (unchecked ones unsaved); Keep Unsaved Tabs Open
    /// closes only the others with nothing unsaved;
    /// Discard &amp; Close Tabs closes them all. Cancel closes none. Each is then closed as its × closes it, with no second question.
    /// </summary>
    private async Task CloseOthersAskedOnceAsync(ProjectTabHeader keep)
    {
        var others = _tabs.Tabs.Where(t => !ReferenceEquals(t, keep)).ToList();
        var rows = RowsOf(others.Where(t => t.Dirty && !t.Empty));
        var dialog = new SaveTabsOnExitDialog(rows, SaveTabsOnExitDialog.Purpose.CloseOthers) { Owner = this };
        dialog.ShowDialog();

        var staying = new HashSet<string>(StringComparer.Ordinal);
        var failed = new List<string>();
        switch (dialog.Choice)
        {
            case TabsExitChoice.SaveChecked:
                foreach (var row in rows.Where(r => r.IsChecked))
                {
                    if (_tabs.Find(row.Id) is not { } tab || ReferenceEquals(tab, _tabs.Active))
                        continue;
                    var saved = SaveTabInPlace(tab, row.Name.Trim().Length > 0 ? row.Name.Trim() : tab.Title);
                    if (!saved.StartsWith("Project saved", StringComparison.Ordinal))
                    {
                        staying.Add(tab.Id);
                        failed.Add(tab.Title);
                    }
                }

                break;

            case TabsExitChoice.KeepSuspended:
                staying.UnionWith(rows.Select(r => r.Id));
                break;

            case TabsExitChoice.Discard:
                break;

            default:
                return;
        }

        foreach (var other in others)
        {
            if (!_tabs.Tabs.Contains(keep))
                return;
            if (staying.Contains(other.Id) || !_tabs.Tabs.Contains(other) || ReferenceEquals(other, _tabs.Active))
                continue;
            await CloseProjectTabAsync(other, asked: true);
        }

        if (failed.Count > 0)
            _viewModel.StatusText = failed.Count == 1
                ? $"\"{failed[0]}\" could not be saved: its tab stays open."
                : $"{failed.Count} tabs could not be saved: their tabs stay open.";
    }

    /// <summary>
    /// A tab behind's hover card opening: its frame and frame rate, and its picture: the paused still a hot tab keeps (shown as it is,
    /// never copied), else the thumbnail its cache file or its project file carries, read and decoded small on the thread pool, and given
    /// only if its card is still open. The card lets it go as it closes.
    /// </summary>
    private async void FillTabPreview(ProjectTabItem item)
    {
        try
        {
            if (_tabs.Find(item.Id) is not { } tab || !KeptInFile(tab))
                return;

            // The tab under the pointer is likely next (5.4.2A): its playhead's frames decoded ahead, with the next in Ctrl+Tab's order.
            PrewarmLikelyTabs(tab);
            item.Details = ProjectTabs.DescribeFrame(tab.FrameWidth, tab.FrameHeight, tab.FrameRate);
            if (StillOfTab(tab) is { } still)
            {
                item.Preview = still;
                return;
            }

            // A tab kept live without a still has its thumbnail in memory (5.4D-TabPerf: its cache file may not be written yet).
            var kept = _hydrated.GetValueOrDefault(tab.Id)?.Project is { } held ? held.Frozen.ThumbnailJpeg ?? held.Thumbnail : null;
            var (stateFile, projectPath, decodeHeight) = (_tabs.StateFile(tab.Id), tab.ProjectPath, TabCardPictureHeight(tab.FrameWidth, tab.FrameHeight));
            var picture = await Task.Run(() =>
            {
                var jpeg = kept ?? ProjectThumbnail.ReadFromProject(stateFile) ?? (projectPath.Length > 0 ? ProjectThumbnail.ReadFromProject(projectPath) : null);
                return jpeg is null ? null : ProjectThumbnail.Decode(jpeg, decodeHeight);
            });
            // 5.4.4: never over a picture the pointer has scrubbed to meanwhile.
            if (ReferenceEquals(ProjectTabStrip.HoverItem, item) && !item.IsScrubbing)
                item.Preview = picture;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: a tab's hover card could not be filled", ex);
        }
    }
}
