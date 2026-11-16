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

// ----- Project tab context menu and hover card -----
public partial class MainWindow
{
    // ----- Tab context menu and hover card -----

    /// <summary>
    /// Decode height in pixels for a cold tab's hover-card thumbnail: the thumbnail's own (short side = <see cref="ProjectThumbnail.ShortSide"/>,
    /// roughly the card at 200 %); portrait frames (aspect from the header) scale up by aspect, capped at twice the card's max height.
    /// </summary>
    private static int TabCardPictureHeight(int frameWidth, int frameHeight) =>
        frameWidth > 0 && frameHeight > frameWidth
            ? (int)Math.Min(Math.Round(ProjectThumbnail.ShortSide * (double)frameHeight / frameWidth), 2 * ProjectTabItem.CardMaxHeight)
            : ProjectThumbnail.ShortSide;

    /// <summary>Dispatches a tab context-menu command (ProjectTabStrip.MenuFor; Rename Project... goes to <see cref="RenameProjectTab"/>).</summary>
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
    /// Save Project / Save Project As... from a tab menu: the active tab saves like Ctrl+S / Ctrl+Alt+S; a background tab with a project
    /// file saves in place from its cache file without activation (as on close); other background tabs are activated first, then saved
    /// (a first save opens the Project Manager for a name).
    /// </summary>
    private async Task SaveProjectTabAsync(ProjectTabHeader tab, bool saveAs)
    {
        if (!_tabs.Tabs.Contains(tab))
            return;

        // Refuse during a switch even for the active tab: the view model holds mixed state and still the outgoing tab's project path
        // (Ctrl+S is refused then too).
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
    /// Pure (golden: project-tabs-menu): refusal message for a Rename Project... name (empty, invalid file-name characters, or trailing
    /// dot); null when acceptable.
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

    /// <summary>Pure (golden: project-tabs-menu): renamed path in the same folder with the new name and original extension.</summary>
    internal static string RenamedPath(string projectPath, string name) =>
        Path.Combine(Path.GetDirectoryName(projectPath) ?? "", name.Trim() + Path.GetExtension(projectPath));

    /// <summary>
    /// Rename Project... (committed on Enter): unsaved projects adopt the name for their first save (New Project studio name); saved ones
    /// rename the file and its autosave in place, and later saves target it. Refused with a status message for fully portable projects
    /// (folder named after the project; use Save Project As), missing files, or name collisions; never overwrites.
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

            // Case-only rename is the same file (allowed); any other existing target is never overwritten.
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
            {
                _viewModel.StatusText = $"Not renamed: a project is already saved as {target}.";
                return;
            }

            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _viewModel.Fail($"Could not rename the project: {ex.Message}", $"Rename the project {path}", ex);
            return;
        }

        MoveAutosave(path, target);
        CrashReporter.KeepOut(target);
        if (front)
        {
            // File.Move keeps write time and length, so carry the tab's file stamp across the rename.
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

    /// <summary>Best-effort rename of the project's autosave alongside it (skipped if the target exists; failures logged).</summary>
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
    /// Rewrites a background tab's header after an in-memory change (rename, save) on the tab's I/O queue, so pending cache writes from a
    /// switch complete first.
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
    /// Duplicate Project in New Tab: copies the project with undo history into an unsaved tab right after it ("Name (Copy)" offered at
    /// first save, beside the original) and activates it. Background tabs copy from their cache file (or frozen live copy) after pending
    /// I/O; the active tab is captured here and written on the pool. Empty tabs duplicate as a new blank tab.
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
                // Capture on the UI thread (view model affinity), write off-thread.
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

    /// <summary>Open Containing Folder: reveals the project file in Explorer (or opens its folder if the file is gone).</summary>
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
                    ShellOpen.Reveal(path);
                }
                else if (Path.GetDirectoryName(path) is { Length: > 0 } folder && Directory.Exists(folder))
                {
                    ShellOpen.OpenFolder(folder);
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
    /// Close Other Tabs: activates <paramref name="keep"/>, then closes each other tab like its × (prompting if unsaved), stopping at the
    /// first Cancel and re-checking after each await (a first save or the Project Manager may change the active tab). With two or more
    /// unsaved others, a single dialog is used instead (<see cref="CloseOthersAskedOnceAsync"/>).
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

        // Sync dirty dots from completed writes (a just-backgrounded tab may not have reported yet).
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

        // A first save may have activated another tab: reactivate the kept one.
        if (_tabs.Tabs.Contains(keep) && !ReferenceEquals(keep, _tabs.Active) && !_switchingTab)
            await SwitchToTabAsync(keep);
    }

    /// <summary>
    /// Close Other Tabs with two or more unsaved: one SaveTabsOnExitDialog (CloseOthers wording). Save Checked &amp; Close Tabs saves checked
    /// tabs from cache without activation (unsaved ones under the typed name) and closes all except failed saves; Keep Unsaved Tabs Open
    /// closes only clean tabs; Discard &amp; Close Tabs closes all; Cancel closes none. Closing then proceeds without a second prompt.
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
                    if (!saved.IsSaved)
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
    /// Fills a background tab's hover card: frame size/rate and picture (a hot tab's paused still by reference, else the cache or project
    /// thumbnail decoded small on the pool, applied only if the card is still open). The card releases it on close.
    /// </summary>
    private void FillTabPreview(ProjectTabItem item) => Faults.Forget(FillTabPreviewAsync(item), nameof(FillTabPreview));

    private async Task FillTabPreviewAsync(ProjectTabItem item)
    {
        try
        {
            if (_tabs.Find(item.Id) is not { } tab || !KeptInFile(tab))
                return;

            // Hovered tab is a likely next switch: prewarm its playhead frames (and the next in Ctrl+Tab order).
            PrewarmLikelyTabs(tab);
            item.Details = ProjectTabs.DescribeFrame(tab.FrameWidth, tab.FrameHeight, tab.FrameRate);
            if (StillOfTab(tab) is { } still)
            {
                item.Preview = still;
                return;
            }

            // Live-kept tabs without a still use the in-memory thumbnail (the cache file may not be written yet).
            var kept = _hydrated.GetValueOrDefault(tab.Id)?.Project is { } held ? held.Frozen.ThumbnailJpeg ?? held.Thumbnail : null;
            var (stateFile, projectPath, decodeHeight) = (_tabs.StateFile(tab.Id), tab.ProjectPath, TabCardPictureHeight(tab.FrameWidth, tab.FrameHeight));
            var picture = await Task.Run(() =>
            {
                var jpeg = kept ?? ProjectThumbnail.ReadFromProject(stateFile) ?? (projectPath.Length > 0 ? ProjectThumbnail.ReadFromProject(projectPath) : null);
                return jpeg is null ? null : ProjectThumbnail.Decode(jpeg, decodeHeight);
            });
            // Do not overwrite a frame the pointer scrubbed to meanwhile.
            if (ReferenceEquals(ProjectTabStrip.HoverItem, item) && !item.IsScrubbing)
                item.Preview = picture;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: a tab's hover card could not be filled", ex);
        }
    }
}
