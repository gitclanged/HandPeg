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

// ----- Project tabs in the title bar -----
// Title-bar tab strip ([ • Name × ] per project, then [ + ]). One view model and one GPU engine serve the active tab; an inactive tab is
// either a live TabViewContext (Sequence, history, still, selection, views: switches swap references with no await) or only its cache
// file (%Cache%\project-tabs\<session>\<id>.hptab, written by ProjectStore on the thread pool after each switch, serialized by
// ProjectTabs.Enqueue). Deactivation stops playback, captures the tab's 3D pose and layout (AppSettings.PerTabWindowLayouts), and calls
// GpuPreviewEngine.Suspend. Switching never prompts; closing prompts only for unsaved changes. Each process owns its own cache folder.
// The strip stays collapsed (StripHidden) while the only tab is blank, so Frame 1 measures none of it; orphaned tabs from a previous
// session are offered at idle after Frame 1, and a tab layout is applied only on switch/close/undo-close (never at start-up).
public partial class MainWindow
{
    // Tab session state (order and active tab, live contexts behind it, failed resumes, activation generation, the operation in flight):
    // Shell.ProjectTabController. Every change goes through it; _tabs, _hydrated, _unresumed and _switchingTab are its views for the partials.
    private readonly Shell.ProjectTabController<TabViewContext> _tabControl = new(new ProjectTabs(ProjectTabs.DefaultRoot));

    private ProjectTabs _tabs => _tabControl.Tabs;

    // Set by Ctrl+T or + while the strip is hidden: shows the blank tab ([ New Project × ] [ + ]) until a close leaves one tab, which
    // clears it so a lone blank tab collapses the strip again.
    private bool _tabsRevealed;

    // True while a switch, new tab or close runs (ProjectTabController.Prepare .. Finish); later tab operations and app shutdown await it.
    // Meanwhile the Timeline ignores the pointer (SwitchingChanged) and project-changing keys/drops are refused (RefusedWhileDragging,
    // Window_PreviewDrop): such an edit would land in neither tab.
    private bool _switchingTab => _tabControl.IsSwitching;

    // RecoverProjectTabs moving a dead session's tabs into this session's folder; shutdown awaits it so no moved file is orphaned when
    // the session folder is released.
    private bool _recoveringTabs;

    // Tabs whose resume failed (missing media drive, unreadable cache): files are kept and activation (or a click while active) retries.
    // New work never lands in such a tab; it opens a new one.
    private IReadOnlySet<string> _unresumed => _tabControl.Unresumed;

    // Debounced refresh of the active tab's dirty dot and name (the fingerprint hashes the whole state, so never per frame).
    private readonly DispatcherTimer _tabDirty = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Short side of a tab's paused still in pixels (~2 MB at 16:9; at most three tabs keep one).</summary>
    private const int StillShortSide = 540;

    /// <summary>Title-bar width reserved right of +, in DIPs: the caption area used to drag and maximize the window.</summary>
    private const double CaptionGrip = 96;

    /// <summary>Creates the strip's single in-memory tab and wires its handlers (pre-Frame 1 from InitializeTitleBar; no disk I/O).</summary>
    private void InitializeProjectTabs()
    {
        _tabControl.SwitchingChanged += switching => UnifiedTimeline.IsHitTestVisible = !switching;
        ShowTabs();
        ProjectTabStrip.TabChosen += item =>
        {
            if (_tabs.Find(item.Id) is { } tab)
                _ = SwitchToTabAsync(tab);
        };
        ProjectTabStrip.TabClosing += item =>
        {
            if (_tabs.Find(item.Id) is { } tab)
                _ = CloseProjectTabAsync(tab);
        };
        ProjectTabStrip.NewTabAsked += () => _ = NewProjectTabAsync();
        ProjectTabStrip.TabMoved += (item, index) =>
        {
            // Applied immediately (the strip already moved its item) so strip and tab order never diverge mid-drag.
            if (_tabs.Find(item.Id) is { } tab)
                _tabs.Move(tab, index);
        };
        ProjectTabStrip.ReorderEnded += () =>
        {
            ShowTabs();
            WriteTabManifest();
        };
        ProjectTabStrip.TabCommand += (item, command) =>
        {
            if (_tabs.Find(item.Id) is { } tab)
                _ = DoTabCommandAsync(tab, command);
        };
        ProjectTabStrip.TabRenamed += (item, name) =>
        {
            if (_tabs.Find(item.Id) is { } tab)
                RenameProjectTab(tab, name);
        };
        ProjectTabStrip.PreviewAsked += FillTabPreview;
        ProjectTabStrip.HoverScrubbed += ScrubTabCard;

        _tabDirty.Tick += (_, _) =>
        {
            // Skipped during a switch (it refreshes on completion), a drag (its commit raises its own change) and playback (refreshed on stop).
            _tabDirty.Stop();
            if (!_switchingTab && !TimelineDragHeld && !_viewModel.IsPlaying)
                RefreshActiveTab();
        };
        _viewModel.TimelineChanged += NoteTabChanged;
        _viewModel.TimelineRestored += NoteTabChanged;
        _viewModel.PreviewStateInvalidated += NoteTabChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.StatusText) or nameof(MainViewModel.HasSource) or nameof(MainViewModel.IsPlaying))
                NoteTabChanged();

            // The name tracks file/project/studio name immediately, even while playing; no fingerprint (the dot waits for the debounce).
            if (e.PropertyName is nameof(MainViewModel.HasSource) or nameof(MainViewModel.LocalMediaPath) or nameof(MainViewModel.CurrentProjectPath)
                or nameof(MainViewModel.NewProjectName))
            {
                RefreshTabTitle();
            }

            // A blank project's first file reveals the strip immediately rather than after the debounce (skipped while playing).
            if (e.PropertyName == nameof(MainViewModel.HasSource) && ProjectTabStrip.Visibility != Visibility.Visible && !TabsHidden)
            {
                if (!_unresumed.Contains(_tabs.Active.Id))
                    _tabs.Active.Title = ActiveTabTitle();
                ShowTabs();
            }
        };

        // Post-Frame 1 idle: + tooltip keys and import of orphaned session tabs.
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(RecoverProjectTabs, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>An edit, undo, save or status change may alter the active tab's name or dot: restarts the debounced refresh.</summary>
    private void NoteTabChanged()
    {
        _tabDirty.Stop();
        _tabDirty.Start();
    }

    /// <summary>Width available to tabs: <paramref name="room"/> (between the app name and caption buttons) minus the reserved drag caption.</summary>
    private void FitProjectTabs(double room)
    {
        if (TitleBar.ActualWidth <= 0)
            return;
        ProjectTabStrip.Fit(room - CaptionGrip);
    }

    /// <summary>
    /// Pure: true when the strip is hidden: a single tab that holds nothing (MainViewModel.HoldsNothingForTab), has no project file, did
    /// not fail to resume, and was not revealed by Ctrl+T or + (<paramref name="revealed"/>).
    /// </summary>
    internal static bool StripHidden(int count, bool unresumed, bool holdsNothing, string projectPath, bool revealed) =>
        count == 1 && !unresumed && holdsNothing && projectPath.Length == 0 && !revealed;

    /// <summary><see cref="StripHidden"/> for the live view model (cheap: one pass over the tracks).</summary>
    private bool TabsHidden =>
        StripHidden(_tabs.Count, _unresumed.Contains(_tabs.Active.Id), _viewModel.HoldsNothingForTab, _viewModel.CurrentProjectPath, _tabsRevealed);

    /// <summary>
    /// Syncs the strip to the tab list (order, names, dots, active tab, overflow). Hidden (<see cref="TabsHidden"/>) it is collapsed with
    /// zero items; re-shown, it is filled and laid out in the last title-bar width before rendering. Skipped mid-drag (synced on drop).
    /// </summary>
    private void ShowTabs()
    {
        if (TabsHidden)
        {
            if (ProjectTabStrip.Visibility != Visibility.Collapsed)
            {
                ProjectTabStrip.CloseFlyouts();
                ProjectTabStrip.Visibility = Visibility.Collapsed;
            }

            if (ProjectTabStrip.Tabs.Count > 0)
                ProjectTabStrip.Tabs.Clear();
            return;
        }

        if (ProjectTabStrip.IsReordering)
        {
            // A keyboard add/close mid-drag cancels the drag (ReorderEnded resyncs), keeping strip indices aligned with the dragged tabs.
            if (!ProjectTabStrip.Tabs.Select(i => i.Id).SequenceEqual(_tabs.Tabs.Select(t => t.Id)))
                ProjectTabStrip.EndReorder();
            return;
        }

        var items = ProjectTabStrip.Tabs;
        if (!items.Select(i => i.Id).SequenceEqual(_tabs.Tabs.Select(t => t.Id)))
        {
            var kept = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
            items.Clear();
            foreach (var tab in _tabs.Tabs)
                items.Add(kept.GetValueOrDefault(tab.Id) ?? new ProjectTabItem { Id = tab.Id });
        }

        for (var i = 0; i < items.Count; i++)
        {
            var (item, tab) = (items[i], _tabs.Tabs[i]);
            (item.Title, item.ProjectPath, item.IsDirty, item.IsActive) = (tab.Title, tab.ProjectPath, tab.Dirty, ReferenceEquals(tab, _tabs.Active));
        }

        // Close a hover card whose tab became active (Ctrl+Tab under the pointer) or was closed (middle click): neither shows one.
        if (ProjectTabStrip.HoverItem is { } hovered && (hovered.IsActive || !items.Contains(hovered)))
            ProjectTabStrip.CloseHoverCard();

        // Measure overflow (which tabs fit) before making it visible.
        ProjectTabStrip.Refit();
        if (ProjectTabStrip.Visibility != Visibility.Visible)
        {
            ProjectTabStrip.Visibility = Visibility.Visible;

            // Multiple tabs exist (never before Frame 1: the strip is hidden then): keep the paused still fresh so switches never wait on a capture.
            KeepTabStillFresh();
        }
    }

    /// <summary>
    /// Refreshes the active tab's header (name, file, dot) from the project. Without <paramref name="fingerprint"/> the dot is left as-is
    /// (a live tab resuming) until the debounced refresh recomputes it.
    /// </summary>
    private void RefreshActiveTab(bool fingerprint = true)
    {
        // New work while a failed-resume tab is active moves into a new tab first.
        SplitFromUnresumed();

        // A failed-resume tab keeps its file's name, path and dot, not those of the empty project shown.
        var tab = _tabs.Active;
        if (!_unresumed.Contains(tab.Id))
        {
            tab.Title = ActiveTabTitle();
            TakeProjectPath(tab, _viewModel.CurrentProjectPath);
            tab.Empty = _viewModel.HoldsNothingForTab;
            tab.Dirty = _viewModel.MayHaveUnsavedChanges && (fingerprint ? _viewModel.HasUnsavedChanges : tab.Dirty);

            // Clean (saved, or undone to the save point): delete its crash-recovery file now (MainWindow.TabCache.cs).
            if (!tab.Dirty)
                DropFrontRecovery(tab);
        }

        ShowTabs();
    }

    /// <summary>
    /// New work (open, drop, Source box, New Project, chosen frame) while a failed-resume tab is active opens a new active tab instead; the
    /// failed tab stays cold with its files and retries on activation. Returns true when a tab was added.
    /// </summary>
    private bool SplitFromUnresumed()
    {
        var stuck = _tabs.Active;
        if (!_unresumed.Contains(stuck.Id) || _viewModel.HoldsNothingForTab)
            return false;

        _tabControl.TakeWorkOff(stuck, ActiveTabTitle());
        AppLog.Write("Project tabs: new work in front of a tab that could not be brought back went into a tab of its own.");
        ShowTabs();
        WriteTabManifest();
        return true;
    }

    /// <summary>True when a tab's state lives in its cache file rather than the view model: any inactive tab, or the active failed-resume tab.</summary>
    private bool KeptInFile(ProjectTabHeader tab) => !ReferenceEquals(tab, _tabs.Active) || _unresumed.Contains(tab.Id);

    /// <summary>
    /// Active tab title (<see cref="TabTitleOf"/>): project name, else New Project studio name, else first file's name (the pending open's
    /// name while it runs), else New Project.
    /// </summary>
    private string ActiveTabTitle() =>
        TabTitleOf(_viewModel.CurrentProjectPath, _viewModel.NewProjectName, _viewModel.HasSource,
            _openingName is { } opening && opening.Id == _tabs.Active.Id ? opening.Name : null, FirstFileInFront);

    /// <summary>File the active tab is named after: the last opened file, else the first clip's source.</summary>
    private string? FirstFileInFront() => _viewModel.LocalMediaPath is { Length: > 0 } local
        ? local
        : _viewModel.Document.Tracks.SelectMany(t => t.Clips).Select(c => c.SourcePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

    /// <summary>
    /// Pure tab title (golden tab-perf-instant-name): project file name, else studio name, else <paramref name="file"/> (evaluated only
    /// with a source open), else <paramref name="opening"/> (<see cref="OpeningTabName"/>), else New Project. The pending-open name equals
    /// the post-open name, so a tab is never renamed when the open completes; a failed open reverts to New Project.
    /// </summary>
    internal static string TabTitleOf(string projectPath, string newProjectName, bool hasSource, string? opening, Func<string?> file)
    {
        if (projectPath.Length > 0)
            return Path.GetFileNameWithoutExtension(projectPath);
        if (newProjectName.Length > 0)
            return newProjectName;
        if (!hasSource)
            return opening ?? "New Project";
        return file() is { Length: > 0 } first && Path.GetFileNameWithoutExtension(first) is { Length: > 0 } name ? name : "Untitled Project";
    }

    // ----- Immediate tab naming from a pending open -----

    // Pending open into an empty active tab (tab id, resulting name): used as the tab's title until the open completes.
    private (string Id, string Name)? _openingName;

    /// <summary>
    /// Pure: the title an open (drop, Media pane, File &gt; Open) will give a tab (<see cref="TabTitleOf"/>), for a tab with no source
    /// (<paramref name="hasSource"/>), no project file and a successful resume; null otherwise and for URLs (name known only after download).
    /// </summary>
    internal static string? OpeningTabName(string file, bool hasSource, string projectPath, bool unresumed) =>
        hasSource || projectPath.Length > 0 || unresumed || MainViewModel.IsWebAddress(file)
            || Path.GetFileNameWithoutExtension(file.Trim().Trim('"')) is not { Length: > 0 } name
            ? null
            : name;

    /// <summary>
    /// Names an empty active tab after <paramref name="file"/> before the probe/open runs (<see cref="OpeningTabName"/>). Returns true when
    /// applied; the caller must call <see cref="EndTabOpening"/> when the open finishes, success or failure.
    /// </summary>
    private bool NameTabForOpen(string file)
    {
        if (_switchingTab || OpeningTabName(file, _viewModel.HasSource, _viewModel.CurrentProjectPath, _unresumed.Contains(_tabs.Active.Id)) is not { } name)
            return false;

        _openingName = (_tabs.Active.Id, name);
        RefreshTabTitle();
        return true;
    }

    /// <summary>Ends a <see cref="NameTabForOpen"/> naming: the title reverts to the tab's content (the file's name, or New Project after a failure).</summary>
    private void EndTabOpening()
    {
        _openingName = null;
        RefreshTabTitle();
    }

    /// <summary>
    /// Updates only the active tab's name, immediately (no fingerprint or dot), even while playing. Skipped during a switch (the view model is
    /// between tabs; the switch refreshes on completion) and for a failed-resume tab (it shows its file's name).
    /// </summary>
    private void RefreshTabTitle()
    {
        var tab = _tabs.Active;
        if (_switchingTab || _unresumed.Contains(tab.Id))
            return;

        var title = ActiveTabTitle();
        if (string.Equals(title, tab.Title, StringComparison.Ordinal))
            return;
        tab.Title = title;
        ShowTabs();
    }

    /// <summary>
    /// True when a tab change may start now; otherwise posts the reason to the status bar. Deactivation is synchronous, so nothing can start
    /// between this check and the switch. Refused while dropped/recorded clips are pending (MainViewModel.HasPendingAdds) so none lands in the next tab.
    /// </summary>
    private bool MayChangeTabs(string doing)
    {
        var why = _switchingTab ? $"Wait for the project tab to finish switching before {doing}."
            : _viewModel.HasPendingAdds ? "Adding clips: switch tabs when they are in."
            : _viewModel.HoldsRecording ? $"Stop the voiceover recording before {doing}."
            : _viewModel.IsBusy ? $"Wait for the running operation to finish, or cancel it, before {doing}."
            : TimelineDragHeld ? $"Let go of the drag on the timeline before {doing}."
            : null;
        if (why is null)
            return true;

        _viewModel.StatusText = why;
        return false;
    }

    // ----- Switching -----

    /// <summary>Tab click or Ctrl+Tab: deactivates the active tab (no prompt) and activates <paramref name="tab"/>; clicking an active failed-resume tab retries it.</summary>
    private async Task SwitchToTabAsync(ProjectTabHeader tab)
    {
        if (!_tabs.Tabs.Contains(tab))
            return;
        if (!_switchingTab)
            SplitFromUnresumed();

        if (ReferenceEquals(tab, _tabs.Active))
        {
            if (_unresumed.Contains(tab.Id) && MayChangeTabs("bringing the project tab back"))
                await RetryResumeAsync(tab);
            return;
        }

        if (!MayChangeTabs("switching project tabs"))
            return;

        var switching = _tabControl.Prepare("switch project tabs");
        TabWrite? write = null;
        ProjectTabHeader? behind = null;
        object? parked = null;
        var settled = false;
        try
        {
            // Deactivate synchronously (kept live in memory) and reactivate a live tab synchronously: no await between the two.
            behind = PutActiveBehind(out write);
            parked = _viewModel.Document;
            _tabControl.Commit(switching, tab);
            ShowTabs();

            // Apply the tab's layout first so its still is placed where its player will be.
            ApplyTabLayout(tab);
            var hot = _hydrated.TryGetValue(tab.Id, out var live);
            if (hot)
            {
                if (!AttachHydrated(tab, live!))
                    TakeBack(tab, behind);
            }
            else
            {
                // File-only tab: read and opened from the cache while the outgoing tab's write proceeds.
                QueueTabWrite(write);
                write = null;
                await ResumeTabAsync(tab, behind);
            }

            // The front is settled: the incoming project attached, or its failure handled (TakeBack / failed resume).
            settled = true;
            RefreshActiveTab(fingerprint: !hot);
            WriteTabManifest();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("switch project tabs", ex);
            if (!settled && behind is not null)
                SettleFailedSwitch(tab, behind, parked);
        }
        finally
        {
            _tabControl.Finish(switching);

            // Write the outgoing tab's cache file after the switch, on the thread pool, from its frozen copy.
            QueueTabWrite(write);
        }

        // Pre-roll the Ctrl+Tab target: decode its playhead frames ahead.
        PrewarmLikelyTabs(null);
        LetGoForMemory();
    }

    /// <summary>
    /// A switch faulted after the outgoing tab was put behind but before the front settled. While the incoming project has not replaced the
    /// detached one (<paramref name="parked"/> still in the view model) the outgoing tab is taken back from memory (<see cref="TakeBack"/>);
    /// if that is impossible or faults too, the incoming tab stays active as a failed resume, so the active header never claims a Sequence
    /// it does not hold. Runs before the edit guard is released (ProjectTabController.Finish).
    /// </summary>
    private void SettleFailedSwitch(ProjectTabHeader tab, ProjectTabHeader behind, object? parked)
    {
        if (!ReferenceEquals(tab, _tabs.Active) || !ReferenceEquals(_viewModel.Document, parked))
            return;
        try
        {
            TakeBack(tab, behind);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the tab put behind could not be taken back", ex);
        }

        if (ReferenceEquals(tab, _tabs.Active))
            _tabControl.MarkUnresumed(tab.Id);
        ShowTabs();
    }

    /// <summary>Retries resuming the active failed-resume tab from its file or memory (nothing to deactivate: the shown project is empty).</summary>
    private async Task RetryResumeAsync(ProjectTabHeader tab)
    {
        var retry = _tabControl.Prepare("bring the project tab back");
        try
        {
            StopPlayingForTabs();
            if (_hydrated.TryGetValue(tab.Id, out var live))
            {
                if (!AttachHydrated(tab, live))
                    _viewModel.StatusText = $"\"{tab.Title}\" could not be brought back yet: click it again to try once more.";
            }
            else
            {
                await ResumeTabAsync(tab, null);
            }

            RefreshActiveTab();
            WriteTabManifest();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("bring the project tab back", ex);
        }
        finally
        {
            _tabControl.Finish(retry);
        }
    }

    /// <summary>Ctrl+Tab (1) / Ctrl+Shift+Tab (-1): activates the next or previous tab, wrapping around.</summary>
    private async Task CycleProjectTabAsync(int step)
    {
        if (_tabs.Next(_tabs.Active, step) is { } next)
            await SwitchToTabAsync(next);
        else
            _viewModel.StatusText = "Only one project tab is open: + (or Ctrl+T) opens another.";
    }

    /// <summary>
    /// Synchronously deactivates the active tab (never refused or awaited): stops playback, captures its 3D pose and layout, detaches the
    /// live project (<see cref="DetachActive"/>), leaves 3D and suspends the engine. <paramref name="write"/> is the deferred cache write
    /// (null if none) the caller passes to <see cref="QueueTabWrite"/>; a fresh tab keeps only its window state.
    /// </summary>
    private ProjectTabHeader PutActiveBehind(out TabWrite? write)
    {
        write = null;
        SplitFromUnresumed();
        var tab = _tabs.Active;

        // A pending open no longer names the outgoing tab: its title reverts to its content.
        if (_openingName is { } opening)
        {
            _openingName = null;
            if (opening.Id == tab.Id)
                tab.Title = ActiveTabTitle();
        }

        // An in-flight still capture predates this switch: discard it on arrival regardless of which tab is active then.
        _stillAsked++;

        // Capture window state first: 3D view still up (for the still), any Layout Selector preview reverted.
        CaptureWindowStateForTab(tab);
        _explodedLastPose = null; // next tab's pose comes from its header (RestoreTabView); a new tab uses the default start pose
        StopPlayingForTabs();
        if (_viewModel.HoldsNothingForTab)
        {
            // Nothing to keep (fresh tab, or failed-resume tab, which keeps its file for a retry); its stale crash-recovery file is deleted.
            if (!_unresumed.Contains(tab.Id))
            {
                (tab.Empty, tab.Dirty) = (true, false);
                DropFrontRecovery(tab);
            }
            _tabs.SetStill(tab, null);
            (_liveStill, _liveStillMark) = (null, default);
            _explodedWaitingTab = null;
            LeaveExplodedAtOnce();
            return tab;
        }

        TakeStillOnScreen();
        var live = DetachActive(tab, out write);
        _tabControl.PutBehind(tab, live);
        _tabs.SetStill(tab, null);
        OnTimelinePointer(null);

        // Leave 3D immediately (its state is now in the header) and suspend the engine's resources for this tab.
        _explodedWaitingTab = null;
        LeaveExplodedAtOnce();
        _gpu?.Suspend();
        ClearProgramStrip();
        KeptTabsChanged();
        RetryFailedTabWrites();
        return tab;
    }

    // ----- Per-tab window state -----

    // Tab resumed in 3D whose player, view restore (ResumeTabAsync to RestoreTabView) or engine is not ready: enters 3D once ready, if still active.
    private ProjectTabHeader? _explodedWaitingTab;
    private bool _explodedWaitHooked;

    /// <summary>
    /// Captures the window state into the active tab's (or its duplicate's) header: 3D flag and camera, main layout and Media Browser view
    /// (only with AppSettings.PerTabWindowLayouts). Reverts a Layout Selector preview first. No-op for a failed-resume tab (its header keeps
    /// its last window state); never called from TakeHeaderForTab.
    /// </summary>
    private void CaptureWindowStateForTab(ProjectTabHeader tab)
    {
        EndLayoutPreview();
        if (_unresumed.Contains(tab.Id))
            return;

        // A tab resumed at start-up without applying its layout (Law 1) keeps its stored layout until applied or the window is changed by
        // hand (LayoutNotTaken): the start-up layout never overwrites it.
        var perTab = AppSettings.Current.PerTabWindowLayouts;
        if (!LayoutNotTaken(tab))
        {
            tab.LayoutJson = TabLayoutOf(_dock, perTab);
            tab.MediaView = TabMediaViewOf(MediaBrowser.ViewMode, perTab);
        }

        TakeExplodedState(tab, _explodedUi && _gpu is { } engine ? engine.TargetPose : null, _explodedWaitingTab, _explodedLastPose);
    }

    /// <summary>
    /// Window-free (golden project-tabs-exploded-pose): writes the header's 3D state: in 3D at <paramref name="shown"/> (engine target pose)
    /// while the view is up; else unchanged while it is <paramref name="waiting"/> (resumed in 3D, player/engine not ready); else flat,
    /// keeping <paramref name="remembered"/> as the camera for the next manual 3D toggle.
    /// </summary>
    internal static void TakeExplodedState(ProjectTabHeader tab, GpuPreviewEngine.CameraPose? shown, ProjectTabHeader? waiting,
        GpuPreviewEngine.CameraPose? remembered = null)
    {
        if (shown is { } pose)
        {
            tab.ExplodedOpen = true;
            (tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing) = pose;
        }
        else if (waiting is not null)
        {
            if (!ReferenceEquals(waiting, tab))
                (tab.ExplodedOpen, tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing) =
                    (true, waiting.ExplodedYaw, waiting.ExplodedPitch, waiting.ExplodedZoom, waiting.ExplodedSpacing);
        }
        else
        {
            tab.ExplodedOpen = false;
            if (remembered is { } last)
                (tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing) = last;
        }
    }

    /// <summary>
    /// Pure (golden project-tabs-window-layouts): the Media Browser view name stored with a tab's layout (e.g. Filmstrip, set by a layout's
    /// Then, never in its JSON); null when per-tab layouts are off.
    /// </summary>
    internal static string? TabMediaViewOf(Views.MediaBrowserView view, bool perTab) => perTab ? view.ToString() : null;

    /// <summary>Parses a header's Media Browser view name; null when absent or unknown.</summary>
    internal static Views.MediaBrowserView? MediaViewOf(string? name) =>
        Enum.TryParse<Views.MediaBrowserView>(name, ignoreCase: true, out var view) && Enum.IsDefined(view) ? view : null;

    /// <summary>
    /// Closes the Layout Selector and reverts its preview synchronously; its Closed handler runs later (async popup close) and would
    /// otherwise capture the preview as a tab layout or restore a stale arrangement over the next tab's.
    /// </summary>
    private void EndLayoutPreview()
    {
        _layoutHover.Close();
        RestoreLayoutBeforePreview();
    }

    /// <summary>
    /// Window-free (golden project-tabs-window-layouts): the main layout stored for a deactivated tab (DockManager.Save output); null when per-tab
    /// layouts are off.
    /// </summary>
    internal static string? TabLayoutOf(DockManager dock, bool perTab) => perTab ? dock.Save() : null;

    /// <summary>
    /// Window-free (golden project-tabs-window-layouts): applies a tab's stored main layout via DockManager.Load (never Apply, so a layout
    /// differing only in active tabs is rebound, not lost); side windows untouched. No-op when per-tab layouts are off, the tab has none, or
    /// it is already current. Returns true when applied. DockManager builds nothing mid-drag, and switches never start during one.
    /// </summary>
    internal static bool LoadTabLayout(DockManager dock, ProjectTabHeader tab, bool perTab)
    {
        if (!perTab || tab.LayoutJson is not { Length: > 0 } json || string.Equals(json, dock.Save(), StringComparison.Ordinal))
            return false;

        dock.Load(json);
        return true;
    }

    /// <summary>
    /// Applies the activated tab's layout and persists it as the window arrangement. Closes the Layout Selector first (one opened during the
    /// switch's awaits would restore the previous tab's arrangement on close) and sets the Media Browser view before the build so the Media
    /// pane's minimum size matches the layout.
    /// </summary>
    private void ApplyTabLayout(ProjectTabHeader tab)
    {
        try
        {
            EndLayoutPreview();
            var perTab = AppSettings.Current.PerTabWindowLayouts;
            if (perTab && MediaViewOf(tab.MediaView) is { } view && view != MediaBrowser.ViewMode)
                MediaBrowser.ViewMode = view;
            if (LoadTabLayout(_dock, tab, perTab))
                _viewModel.SavePaneLayout(SaveDockInto);
            ForgetLayoutNotTaken(tab.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A layout that fails to apply is logged and dropped; activation continues.
            AppLog.Write("Project tabs: a tab's own layout could not be laid out", ex);
        }
    }

    /// <summary>
    /// Re-enters 3D at the tab's camera if it was deactivated in 3D, turning off Live Encoded Preview first (mutually exclusive). Defers once
    /// for an unshown player or an unstarted engine (lazy per Law 2, or released by a fault/Reload Player): RestoreExplodedOnEngine resumes
    /// it. Silent: an empty tab simply stays flat.
    /// </summary>
    private void RestoreExplodedForTab(ProjectTabHeader tab)
    {
        _explodedWaitingTab = null;
        if (!tab.ExplodedOpen || _explodedUi || !_viewModel.HasSource)
            return;

        if (_gpu is null)
        {
            _explodedWaitingTab = tab;
            return;
        }

        if (!VideoView.IsVisible)
        {
            _explodedWaitingTab = tab;
            if (!_explodedWaitHooked)
            {
                _explodedWaitHooked = true;
                VideoView.IsVisibleChanged += ExplodedWait_IsVisibleChanged;
            }

            return;
        }

        if (_viewModel.IsExportQualityPreview)
        {
            _viewModel.IsExportQualityPreview = false;
            _viewModel.StatusText = $"Live Encoded Preview is off: \"{tab.Title}\" was left in the 3D Exploded View.";
        }

        SetExplodedView(true, instant: true, new(tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing));
    }

    private void ExplodedWait_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
            return;

        VideoView.IsVisibleChanged -= ExplodedWait_IsVisibleChanged;
        _explodedWaitHooked = false;
        if (_explodedWaitingTab is { } tab && ReferenceEquals(tab, _tabs.Active))
            RestoreExplodedForTab(tab);
        else
            _explodedWaitingTab = null;
    }

    /// <summary>
    /// Called from StartGpuAsync before the player opens: an active tab waiting to re-enter 3D does so now at its camera, so its first
    /// frame is already 3D.
    /// </summary>
    private void RestoreExplodedOnEngine()
    {
        if (_explodedWaitingTab is { } tab && ReferenceEquals(tab, _tabs.Active))
            RestoreExplodedForTab(tab);
    }

    /// <summary>
    /// Fills the outgoing tab's header from the view model (still holding it): name, file, playhead, zoom, scroll, studio name/folder,
    /// pending Game Preset, frame. The dot comes from <see cref="TabDirtyNow"/> (no fingerprint); the exact value is recomputed on the
    /// thread pool during the cache write.
    /// </summary>
    private void TakeHeaderForTab(ProjectTabHeader tab)
    {
        (tab.Title, tab.Empty, tab.Dirty) = (ActiveTabTitle(), false, TabDirtyNow(tab));
        TakeProjectPath(tab, _viewModel.CurrentProjectPath);
        (tab.NewProjectName, tab.NewProjectFolder, tab.WaitingTemplate) = (_viewModel.NewProjectName, _viewModel.NewProjectFolder, _viewModel.TemplateWaitingForTab);
        (tab.PlayheadMs, tab.ViewStart, tab.ViewSpan, tab.ViewFit, tab.VerticalOffset) =
            (_viewModel.PositionMs, _viewStart, _viewSpan, _viewFit, UnifiedTimeline.Surface.VerticalOffset);
        TakeFrameForTab(tab);

        // Written from the view model, the cache file is the exact current project (a lagged crash-recovery copy marks itself as such).
        tab.StepsBack = 0;
    }

    /// <summary>
    /// Fingerprint-free dirty flag for the outgoing tab: clean when nothing changed since save/open; else the last debounced result
    /// (RefreshActiveTab), or dirty while a refresh is still pending (edit, setting, playback).
    /// </summary>
    private bool TabDirtyNow(ProjectTabHeader tab) => _viewModel.MayHaveUnsavedChanges && (tab.Dirty || _tabDirty.IsEnabled || _viewModel.IsPlaying);

    /// <summary>Copies Project Settings' frame size and rate into a tab's header (shown on its hover card while inactive).</summary>
    private void TakeFrameForTab(ProjectTabHeader tab)
    {
        var settings = _viewModel.CurrentProjectSettings;
        (tab.FrameWidth, tab.FrameHeight) = ProjectTabs.FrameOf(settings.NaturalWidth, settings.NaturalHeight, settings.Vertical);
        tab.FrameRate = settings.FrameRate.Fps;
    }

    /// <summary>
    /// Resumes a file-only (already active) tab into the view model; live tabs use <see cref="AttachHydrated"/>. Shows its still at once,
    /// reads the cache after any pending write, opens it as a project (unsaved state preserved), then restores playhead/zoom/scroll once
    /// settled. An empty tab becomes a new project. Returns false on failure: the tab stays active and empty, its file kept for a retry.
    /// </summary>
    /// <param name="behind">The tab deactivated for this switch, reactivated if the view model cannot be cleared; null for a close or retry.</param>
    private async Task<bool> ResumeTabAsync(ProjectTabHeader tab, ProjectTabHeader? behind)
    {
        var id = tab.Id;
        if (tab.Empty)
        {
            HideTabStill();
            if (_viewModel.NewProject())
            {
                // A name set while inactive (Rename Project...) is kept for its first save, like a studio name.
                if (tab.NewProjectName.Length > 0)
                    _viewModel.KeepNewProjectName(tab.NewProjectName, tab.NewProjectFolder);
                return true;
            }

            TakeBack(tab, behind);
            return false;
        }

        ShowTabStill(_tabs.StillOf(tab));
        ProjectState state;
        string? saved;
        var read = default(TabResume);
        try
        {
            // A clean tab whose project file changed on disk since (another PC, a backup, another instance) opens from that file instead
            // (ProjectTabs.ReadForResume, on its queue: no disk I/O on this thread). A dirty tab always resumes from its cache.
            if (!tab.Dirty && tab.ProjectPath is { Length: > 0 } clean)
            {
                read = await _tabs.Enqueue(id, () => _tabs.ReadForResume(id, clean));
                (state, saved) = (read.State, read.Saved);
            }
            else
            {
                (state, saved) = await _tabs.Enqueue(id, () => (_tabs.ReadState(id), _tabs.ReadSaved(id)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            AppLog.Write("Project tabs: a tab could not be read back from the cache", ex);
            return NotResumed(tab, behind, $"\"{tab.Title}\" could not be read back from the cache ({ex.Message}).");
        }

        // On success a new Sequence is installed; on failure the previous tab's Sequence remains and must never be attributed to this tab.
        // A cache whose changed project file was unreadable is detached from that file (Save prompts for a name, so it is never
        // overwritten) and marked unsaved (closing prompts; the cache file stays its crash-recovery copy).
        var unreadable = read.From == TabResumeSource.CacheFileUnreadable;
        var (projectPath, dirty) = unreadable ? ("", true) : (tab.ProjectPath, tab.Dirty);
        var before = _viewModel.Document;
        try
        {
            await _viewModel.ResumeTabAsync(state, projectPath, dirty, saved, tab.NewProjectName, tab.NewProjectFolder, tab.WaitingTemplate);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: a tab could not be opened again", ex);
            return NotResumed(tab, behind, $"\"{tab.Title}\" could not be brought back ({ex.Message}). Its tab keeps it: click it again to try once more.");
        }

        if (ReferenceEquals(before, _viewModel.Document))
        {
            var said = _viewModel.StatusText;
            return NotResumed(tab, behind, $"\"{tab.Title}\" could not be brought back ({said}). Its tab keeps it: click it again to try once more.");
        }

        // The view model owns the tab again: delete its cache files (rewritten on next deactivation) and hand over its pending Game Preset.
        // Queued after its own disk work and before any later request, so the delete can never follow the next deactivation's write. A dirty
        // tab keeps its file as the active crash-recovery copy (the 20 s tick rewrites it on history change and deletes it once clean).
        _tabControl.TakeFront(tab);
        tab.WaitingTemplate = null;
        if (unreadable)
            (tab.ProjectPath, tab.Dirty) = ("", true);
        if (tab.Dirty)
            _frontRecovery = new FrontLook(id, _viewModel.HistoryPosition, Written: true);
        else
            DeleteTabFiles(id);

        // Project-file stamp at the last known match: the one just read (none when detached), else the header's (cache resume). A stamp
        // requested before deactivation is discarded on arrival; only this one describes the resumed project.
        tab.StampsAsked++;
        if (read.From != TabResumeSource.Cache)
            (tab.ProjectFileTicks, tab.ProjectFileLength) = (read.FileTicks, read.FileLength);

        // One-time status on resume: opened from a changed project file, or from a lagged crash-recovery copy.
        if (read.From == TabResumeSource.ChangedFile)
            _viewModel.StatusText = ChangedOnDiskLine(tab.Title);
        else if (unreadable)
            _viewModel.StatusText = UnreadableOnDiskLine(tab.Title, read.Why ?? "");
        else if (tab.StepsBack > 0)
            _viewModel.StatusText = StepsBackLine(tab.StepsBack);
        tab.StepsBack = 0;

        // Deactivated in 3D: mark it waiting until its view is restored, so a quick re-deactivation (held Ctrl+Tab) keeps the header's 3D
        // state and camera instead of recording it as flat.
        if (tab.ExplodedOpen)
            _explodedWaitingTab = tab;
        var activation = _tabControl.Generation;
        _ = Dispatcher.BeginInvoke(() => RestoreTabView(tab, activation), DispatcherPriority.Background);
        return true;
    }

    /// <summary>Marks a resume failure: the tab stays active and empty, its file kept, and the reason goes to the status bar.</summary>
    private bool NotResumed(ProjectTabHeader tab, ProjectTabHeader? behind, string why)
    {
        HideTabStill();
        if (!_viewModel.NewProject())
        {
            TakeBack(tab, behind);
            return false;
        }

        _tabControl.MarkUnresumed(tab.Id);
        _viewModel.StatusText = why;
        return false;
    }

    /// <summary>
    /// The view model refused <paramref name="tab"/> (an operation or recording started; the status line says which): the deactivated tab
    /// is reactivated from memory unchanged and <paramref name="tab"/> stays inactive. With no tab to return to (a close) or a failed return,
    /// <paramref name="tab"/> stays active as failed-resume and the view model's content moves to its own tab (<see cref="SplitFromUnresumed"/>).
    /// </summary>
    private void TakeBack(ProjectTabHeader tab, ProjectTabHeader? behind)
    {
        // Abort: back to the tab put behind (false: none to return to, so tab stays active as a failed resume).
        if (!_tabControl.Abort(tab, behind))
            return;
        ShowTabs();

        // Restore its layout, then (from memory) its project, still, view and 3D state.
        ApplyTabLayout(behind!);
        if (_hydrated.TryGetValue(behind!.Id, out var live) && !AttachHydrated(behind, live))
        {
            _tabControl.Reinstate(tab);
            ShowTabs();
        }
    }

    /// <summary>
    /// Restores a resumed tab's playhead, zoom, scroll and 3D state once the player and time bars have bound the new Sequence. Queued at
    /// <paramref name="activation"/> (ProjectTabController.Generation): a later activation (even of this same tab: A, B, A) restores its own
    /// view, so this stale one only does the camera and wait bookkeeping below when its tab is no longer in front.
    /// </summary>
    private void RestoreTabView(ProjectTabHeader tab, long activation)
    {
        if (ReferenceEquals(tab, _tabs.Active) && !_tabControl.IsCurrent(activation))
            return;

        // Last 3D camera for a manual toggle: the header's (the default start pose for a tab never in 3D).
        if (ReferenceEquals(tab, _tabs.Active))
            _explodedLastPose = new(tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing);

        // Deactivated meanwhile: the now-active tab owns the still (and already cleared the wait); leave both alone.
        if (!ReferenceEquals(tab, _tabs.Active))
            return;

        if (!_viewModel.HasSource)
        {
            // Active but empty: stay flat and clear the wait.
            if (ReferenceEquals(_explodedWaitingTab, tab))
                _explodedWaitingTab = null;
            HideTabStill();
            return;
        }

        if (tab.ViewFit || tab.ViewSpan <= 0)
            FitTimeline(announce: false);
        else
            SetView(tab.ViewStart, tab.ViewSpan);
        UnifiedTimeline.Surface.VerticalOffset = tab.VerticalOffset;
        var at = Math.Max(0, Math.Min(tab.PlayheadMs, Math.Max(_viewModel.DurationMs, 0)));

        // A restored playhead is not a user seek: the still stays up (FollowPlayheadInPlayer).
        _restoringTabView = true;
        try
        {
            _viewModel.PositionMs = at;

            // Re-enter 3D before the player opens so the first frame matches the 3D still.
            RestoreExplodedForTab(tab);
            OpenInPlayer((long)at, paused: true);
        }
        finally
        {
            _restoringTabView = false;
        }

        WatchForFirstFrame();
    }

    // True during RestoreTabView: the restored playhead move keeps the still visible.
    private bool _restoringTabView;

    /// <summary>
    /// Stops playback, shuttle, a Color Match pick, and any 3D drag or lift before a tab is deactivated or closed. The 3D view stays up so the still captures it;
    /// PutActiveBehind then leaves it (LeaveExplodedAtOnce).
    /// </summary>
    private void StopPlayingForTabs()
    {
        EndColorMatchPicks();
        StopShuttle();
        StopTimelineMomentum();
        if (_gpu is { IsPlaying: true })
            PlayerSetPause(true);
        _viewModel.IsPlaying = false;
        FollowPlayerByFrame(false);
        if (_explodedUi)
        {
            EndExplodedDrag(cancelled: true);
            _gpu?.LiftLayer(0, 0);
        }
    }

    // ----- New, open, close -----

    /// <summary>
    /// + / Ctrl+T / Project Manager New Project: deactivates the active tab (no prompt) and opens an empty project in a new tab. With
    /// <paramref name="reuseEmpty"/> a fresh active tab is reused. Returns false (reason in the status bar) with the active tab unchanged.
    /// </summary>
    private Task<bool> NewProjectTabAsync(bool reuseEmpty = false) => Task.FromResult(NewProjectTab(reuseEmpty));

    /// <summary>Synchronous <see cref="NewProjectTabAsync"/> (deactivation never awaits).</summary>
    private bool NewProjectTab(bool reuseEmpty)
    {
        // Strip hidden: Ctrl+T reveals the blank tab ([ New Project × ] [ + ]) rather than adding a second blank one.
        if (!reuseEmpty && TabsHidden)
        {
            _tabsRevealed = true;
            ShowTabs();
            return true;
        }

        if (reuseEmpty && !_switchingTab && _viewModel.HoldsNothingForTab && !_unresumed.Contains(_tabs.Active.Id))
        {
            if (!_viewModel.NewProject())
                return false;
            RefreshActiveTab();
            return true;
        }

        if (!MayChangeTabs("opening a new project tab"))
            return false;

        var opening = _tabControl.Prepare("open a new project tab");
        TabWrite? write = null;
        try
        {
            var behind = PutActiveBehind(out write);
            var made = _tabs.Add("New Project");
            _tabControl.Commit(opening, made);
            HideTabStill();
            if (!_viewModel.NewProject())
            {
                BackOutOf(made, behind);
                return false;
            }

            RefreshActiveTab();
            WriteTabManifest();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("open a new project tab", ex);
            return false;
        }
        finally
        {
            _tabControl.Finish(opening);
            QueueTabWrite(write);
            LetGoForMemory();
        }
    }

    /// <summary>Rolls back a new tab whose view model could not be cleared (see <see cref="TakeBack"/>): removes it and reactivates the previous tab.</summary>
    private void BackOutOf(ProjectTabHeader made, ProjectTabHeader behind)
    {
        TakeBack(made, behind);
        _tabControl.ClearUnresumed(made.Id);
        if (!ReferenceEquals(made, _tabs.Active))
            _tabs.Close(made);
        ShowTabs();
    }

    /// <summary>
    /// Single entry point for opening a .hproj/.hpb (Project Manager, drop, Explorer, Source box); never replaces work and never prompts.
    /// Activates the tab already holding it; else opens into an empty active tab; else into a new tab.
    /// </summary>
    internal async Task OpenProjectInTabAsync(string path)
    {
        try
        {
            // Stamp the file off-thread before the open reads it (StampOpenedProject applies it on success); done first so every check
            // below sees post-await state.
            var stamp = await Task.Run(() => ProjectTabs.FileStamp(path));

            // The await is not guarded (a sleeping share can take seconds), so a close may start meanwhile; never open into a tab that is
            // prompting (its dialog loop runs this continuation) or closing.
            if (_closeAsked || _windowClosing is { IsCancellationRequested: true })
            {
                AppLog.Write($"Project tabs: {Path.GetFileName(path)} was not opened: HandPeg was closing");
                _viewModel.StatusText = $"{Path.GetFileNameWithoutExtension(path)} was not opened while HandPeg was closing: open it again.";
                return;
            }

            if (SameProjectFile(_viewModel.CurrentProjectPath, path))
            {
                _viewModel.StatusText = $"{Path.GetFileNameWithoutExtension(path)} is open already, in the tab in front.";
                return;
            }

            if (_tabs.Tabs.FirstOrDefault(t => !ReferenceEquals(t, _tabs.Active) && SameProjectFile(t.ProjectPath, path)) is { } open)
            {
                await SwitchToTabAsync(open);
                return;
            }

            // Active tab is empty: open into it (queued behind a running encode, as LoadProjectAsync does).
            if (_viewModel.HoldsNothingForTab && !_unresumed.Contains(_tabs.Active.Id))
            {
                if (_switchingTab)
                {
                    _viewModel.StatusText = "Wait for the project tab to finish switching before opening a project.";
                    return;
                }

                var (into, opening) = (_tabs.Active, _viewModel.CurrentProjectPath);
                var asked = ++into.StampsAsked;
                await _viewModel.LoadProjectAsync(path);
                StampOpenedProject(into, opening, path, stamp, asked);
                RefreshActiveTab();
                return;
            }

            if (!MayChangeTabs("opening a project in a new tab"))
                return;

            var openInNew = _tabControl.Prepare("open a project in a new tab");
            TabWrite? write = null;
            try
            {
                var behind = PutActiveBehind(out write);
                QueueTabWrite(write);
                write = null;
                var made = _tabs.Add(Path.GetFileNameWithoutExtension(path));
                _tabControl.Commit(openInNew, made);
                HideTabStill();
                ShowTabs();

                // Reset the session first so a failed open leaves the tab empty, never holding the previous tab's work.
                if (!_viewModel.NewProject())
                {
                    BackOutOf(made, behind);
                    return;
                }

                var asked = ++made.StampsAsked;
                await _viewModel.LoadProjectAsync(path);
                StampOpenedProject(made, "", path, stamp, asked);
                RefreshActiveTab();
                WriteTabManifest();
            }
            finally
            {
                _tabControl.Finish(openInNew);
                QueueTabWrite(write);
            }

            LetGoForMemory();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("open the project in a tab", ex);
        }
    }

    private static bool SameProjectFile(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// ×, middle click or Ctrl+W (<paramref name="which"/> null = active tab). Prompts Save/Discard/Cancel only per
    /// <see cref="UnsavedForExit(ProjectTabHeader)"/>, then deletes its files and activates the next (or previous) tab; the last tab is
    /// emptied instead. <paramref name="asked"/>: Close Other Tabs already prompted for every tab in one dialog.
    /// </summary>
    private async Task CloseProjectTabAsync(ProjectTabHeader? which, bool asked = false)
    {
        // Ctrl+W with the strip hidden: nothing to close; the blank project is not recreated.
        if (which is null && TabsHidden)
            return;
        if (!_switchingTab)
            SplitFromUnresumed();
        var tab = which ?? _tabs.Active;
        if (!_tabs.Tabs.Contains(tab))
            return;

        var active = ReferenceEquals(tab, _tabs.Active);
        if (active ? !MayChangeTabs("closing a project tab") : _switchingTab)
        {
            if (_switchingTab)
                _viewModel.StatusText = "Wait for the project tab to finish switching before closing one.";
            return;
        }

        if (active)
            RefreshActiveTab();
        // Same rule as app exit: unsaved changes, or content never saved as a project.
        if (!asked && UnsavedForExit(tab) && !await AskToSaveTabAsync(tab))
            return;

        // The prompt may have activated the tab (first save) and held a dialog open: re-read state.
        if (!_tabs.Tabs.Contains(tab) || _switchingTab)
            return;
        active = ReferenceEquals(tab, _tabs.Active);
        if (active && !MayChangeTabs("closing a project tab"))
            return;

        try
        {
            if (!active)
            {
                _tabControl.ClearUnresumed(tab.Id);
                ForgetKeptTab(tab.Id);
                _tabs.Close(tab);
                ForgetRevealIfOne();
                ShowTabs();
                WriteTabManifest();
                CompactWhenQuiet("a project tab closed");
                return;
            }

            if (_tabs.Count == 1)
            {
                // Last tab: emptied, not closed (left unchanged if the view model cannot be cleared; status says why); leaves 3D immediately.
                StopPlayingForTabs();
                HideTabStill();
                if (!_viewModel.NewProject())
                    return;
                _explodedWaitingTab = null;
                LeaveExplodedAtOnce();
                _tabControl.ClearUnresumed(tab.Id);
                ForgetKeptTab(tab.Id);
                DeleteTabFiles(tab.Id);
                ForgetLiveStill();
                ForgetRevealIfOne();
                RefreshActiveTab();
                WriteTabManifest();
                CompactWhenQuiet("a project tab closed");
                return;
            }

            if (_tabs.NeighbourOf(tab) is not { } next)
                return;

            var closing = _tabControl.Prepare("close a project tab");
            try
            {
                // Discard the closed project (saved or discarded above; no cache write), leave 3D, and apply the next tab's layout before its still shows.
                StopPlayingForTabs();
                OnTimelinePointer(null);
                EndLayoutPreview();
                _explodedWaitingTab = null;
                LeaveExplodedAtOnce();
                _gpu?.Suspend();
                ClearProgramStrip();

                // Drop its still; an in-flight capture is discarded on arrival.
                ForgetLiveStill();
                _tabControl.Commit(closing, next);
                _tabControl.ClearUnresumed(tab.Id);
                ForgetKeptTab(tab.Id);
                _tabs.Close(tab);
                ForgetRevealIfOne();
                ShowTabs();
                ApplyTabLayout(next);

                // Next tab from memory when live (replacing the released Sequence), else from its cache file.
                if (!_hydrated.TryGetValue(next.Id, out var live) || !AttachHydrated(next, live))
                    await ResumeTabAsync(next, null);
                RefreshActiveTab();
                WriteTabManifest();
            }
            finally
            {
                _tabControl.Finish(closing);
            }

            CompactWhenQuiet("a project tab closed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("close the project tab", ex);
        }
    }

    /// <summary>
    /// After a close leaves one tab: clears the Ctrl+T/+ reveal so a lone blank tab collapses the strip; a tab holding work keeps it shown.
    /// </summary>
    private void ForgetRevealIfOne()
    {
        if (_tabs.Count == 1)
            _tabsRevealed = false;
    }

    /// <summary>
    /// Save/Discard/Cancel prompt on close; true to proceed (saved or discarded). A tab with a project file saves to it (inactive tabs from
    /// their cache file); a never-saved tab is activated for a first save in the Project Manager and closes only if that save succeeds.
    /// </summary>
    private async Task<bool> AskToSaveTabAsync(ProjectTabHeader tab)
    {
        var never = !tab.Dirty && (KeptInFile(tab) ? tab.ProjectPath : _viewModel.CurrentProjectPath).Length == 0;
        var ask = new ConfirmDialog("Close Project Tab", never
            ? $"\"{tab.Title}\" was never saved as a project.\n\nSave it before its tab closes, or let it go?"
            : $"\"{tab.Title}\" has changes that were not saved as a project.\n\nSave them before its tab closes, or let them go?",
            "Save", "Discard") { Owner = this };
        if (ask.ShowDialog() != true)
            return false;
        if (ask.ChoseOther)
            return true;

        if (KeptInFile(tab) && tab.ProjectPath.Length > 0)
            return SaveTabInPlace(tab, tab.Title).IsSaved;
        if (!KeptInFile(tab) && _viewModel.CurrentProjectPath.Length > 0)
            return _viewModel.SaveProjectTo(_viewModel.CurrentProjectPath).IsSaved;

        // Never saved: activate it first (switch, or retry a failed resume); the status line reports a refusal.
        if (KeptInFile(tab))
        {
            await SwitchToTabAsync(tab);
            if (KeptInFile(tab))
                return false;
        }

        return FirstSaveInFront();
    }

    /// <summary>First save of the active tab, as Ctrl+S on a new project (Project Manager with the name box focused); true when saved.</summary>
    private bool FirstSaveInFront()
    {
        var dialog = new ProjectManagerWindow(_viewModel, focusName: true) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            // The Project Manager returned another action (open, New Project, bundle): run it; the tab stays open.
            _ = ActOnProjectManagerAsync(dialog);
            return false;
        }

        return _viewModel.CurrentProjectPath.Length > 0 && !_viewModel.HasUnsavedChanges;
    }

    /// <summary>
    /// Saves an inactive tab without activating it (from its cache file, or from memory after its cache write when live) to
    /// <see cref="TabSavePath"/> (project file, portable folder, studio folder, else Projects\<paramref name="name"/>). Never overwrites
    /// another project's file without confirmation. Returns the outcome and its status-bar line.
    /// </summary>
    private SaveOutcome SaveTabInPlace(ProjectTabHeader tab, string name)
    {
        var path = tab.ProjectPath;
        try
        {
            var title = name.Trim().Length > 0 ? name.Trim() : tab.Title;
            (path, var portable) = TabSavePath(tab.ProjectPath, tab.NewProjectFolder, title, AppSettings.Current.FullyPortableProjectSave);
            if (!SameProjectFile(path, tab.ProjectPath) && File.Exists(path) && !MayReplaceProject(path))
                return new(SaveResult.Declined, _viewModel.StatusText = $"Not saved: {Path.GetFileNameWithoutExtension(path)} is another project, and it was left as it is.");

            // Live tab: wait for its cache write (still running after 5 s: not saved, reported), then save a clone of the frozen state with
            // the save's name and time; the frozen copy is never mutated (the pool or a crash handler may be serializing it).
            var live = _hydrated.GetValueOrDefault(tab.Id);
            if (live is not null && !_tabs.WaitFor(tab.Id, IoWaitMs))
                return new(SaveResult.Declined, _viewModel.StatusText = "Still writing this tab's recovery copy: try again in a moment.");
            var state = live is not null ? live.Project.Frozen.ShallowCopy() : _tabs.ReadState(tab.Id);
            if (live is not null)
                state.ThumbnailJpeg ??= live.Project.Thumbnail;
            (state.Name, state.SavedAt) = (Path.GetFileNameWithoutExtension(path), DateTime.Now);
            if (portable)
                _ = NoteCopiesAsync(PortableProject.Save(state, path).Copies);
            else
                ProjectStore.SaveTo(state, path);
            CrashReporter.KeepOut(CrashSummaries.FilesIn(state).Append(path));
            (tab.ProjectPath, tab.Dirty, tab.NewProjectName, tab.NewProjectFolder) = (path, false, "", null);
            _ = StampProjectFileAsync(tab, path);
            if (live is not null)
            {
                // Saved as read back: fingerprint once (the view model's own save also takes one).
                live.Project.MarkSavedOnAttach = true;
                live.Write?.Superseded();
                WriteTabHeader(tab);
            }

            ProjectStore.NotifyChanged();
            ShowTabs();
            return new(SaveResult.Saved, _viewModel.StatusText = $"Project saved: {Path.GetFileNameWithoutExtension(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return new(SaveResult.Failed, _viewModel.Fail($"Could not save the project: {ex.Message}", $"Save tab \"{tab.Title}\" to {path}", ex));
        }
    }

    /// <summary>
    /// Pure (golden project-tabs-save-path): an inactive tab's save target, matching the view model's save. Saved tab: its project file
    /// (with portable saves, its portable folder or a new one, as Ctrl+S; never for autosave). Unsaved: a portable folder named
    /// <paramref name="name"/>, else the studio folder, else Projects (sanitized name). Also returns whether the save is portable.
    /// </summary>
    internal static (string Path, bool Portable) TabSavePath(string projectPath, string? newProjectFolder, string name, bool portableSaves)
    {
        if (projectPath.Length > 0)
        {
            if (!portableSaves || Path.GetFileNameWithoutExtension(projectPath).EndsWith(ProjectStore.AutosaveSuffix, StringComparison.OrdinalIgnoreCase))
                return (projectPath, false);
            var own = PortableProject.IsPortableFolder(projectPath) && File.Exists(projectPath);
            return (own ? projectPath : PortableProject.PathFor(Path.GetFileNameWithoutExtension(projectPath)), true);
        }

        if (portableSaves)
            return (PortableProject.PathFor(name), true);
        var fileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return (Path.Combine(newProjectFolder ?? ProjectStore.Folder, (fileName.Length == 0 ? "Project" : fileName) + ProjectStore.Extension), false);
    }

    /// <summary>Confirms overwriting another project's file of the same name (default: no).</summary>
    private bool MayReplaceProject(string path) =>
        new ConfirmDialog("Replace Project", $"A project is already saved as {path}.\n\nReplace it with this tab's project? What it holds now is lost.", "Replace")
        {
            Owner = this, DefaultsToCancel = true,
        }.ShowDialog() == true;

    /// <summary>Logs failures of a portable save's background media copies (the project save itself has completed).</summary>
    private static async Task NoteCopiesAsync(Task copies)
    {
        try
        {
            await copies;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Project tabs: a portable save's background copies failed", ex);
        }
    }

    /// <summary>Routes Project Manager results (New Project, + New Project, bundle saved, project opened) through the project tabs.</summary>
    private async Task ActOnProjectManagerAsync(ProjectManagerWindow dialog)
    {
        try
        {
            if (dialog.NewProjectToCreate is { } request)
            {
                if (await NewProjectTabAsync(reuseEmpty: true))
                    await CreateProjectAsync(request);
                RefreshActiveTab();
            }
            else if (dialog.NewProjectRequested)
            {
                await NewProjectTabAsync(reuseEmpty: true);
            }
            else if (dialog.BundleRequested)
            {
                SaveProjectAs(bundle: true);
            }
            else if (dialog.ProjectToLoad is { } project)
            {
                await OpenProjectInTabAsync(project);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("do what the Project Manager asked", ex);
        }
    }

    // ----- App exit and orphaned-session tab recovery -----

    /// <summary>
    /// First check in MainWindow_Closing: refuses (status bar says wait) while a tab is mid-activation (split between view model and cache,
    /// so neither can be prompted for or kept) or while RecoverProjectTabs is importing a dead session's tabs.
    /// </summary>
    private bool RefuseCloseMidSwitch()
    {
        // Recovery in progress: closing now would release session files with some moved in but not yet on the strip.
        if (_recoveringTabs)
        {
            _viewModel.StatusText = "Wait for the project tabs left by the last session to come back, then close HandPeg.";
            return true;
        }

        if (!_switchingTab)
            return false;
        _viewModel.StatusText = "Wait for the project tab to finish switching, then close HandPeg.";
        return true;
    }

    /// <summary>
    /// Exit plan from settings (<see cref="ExitPlanFor"/>, MainWindow.TabExit.cs), run by MayClose before the active-tab prompt. Keep-tabs:
    /// write the active tab, no prompt. Save prompt off: no prompt, dirty inactive tabs kept. Otherwise prompt only if work would be lost:
    /// 2+ tabs in one dialog (<see cref="AskAboutTabsOnExit"/>), one active tab via MayClose's prompt. Returns false when the user cancels.
    /// </summary>
    private bool MayCloseProjectTabs()
    {
        SplitFromUnresumed();

        // Drain pending post-switch cache writes first so every tab's dot is exact.
        FinishTabWrites();
        ExitAnsweredByTabs = false;
        var settings = AppSettings.Current;

        // Compute the active tab's exact dot once whenever the plan may prompt (single project included); MayClose reuses it (one fingerprint).
        if (settings.PromptToSaveOnExit && !settings.KeepOpenTabsOnClose)
            RefreshActiveTab();
        var held = _tabs.Tabs.Where(t => !t.Empty).ToList();
        var unsaved = held.Where(t => UnsavedForExit(t)).ToList();
        var (plan, ask) = ExitPlanFor(settings.KeepOpenTabsOnClose, settings.PromptToSaveOnExit, held.Count, unsaved.Count,
            unsaved.Count == 1 && !KeptInFile(unsaved[0]));
        (_tabExitPlan, _exitQuestion) = (plan, ask);
        switch (plan)
        {
            case TabExitPlan.SuspendAll:
                return ExitAnsweredByTabs = SuspendFrontForExit();

            case TabExitPlan.KeepDirtyBehind:
                return true;
        }

        switch (ask)
        {
            case ExitQuestion.Tabs:
                return AskAboutTabsOnExit(held);

            // Single active project with unsaved work: defer to MayClose's prompt.
            case ExitQuestion.Classic:
                return true;

            // No unsaved work (one saved project, or none): no prompt.
            default:
                ExitAnsweredByTabs = true;
                return true;
        }
    }

    /// <summary>
    /// Executes the exit plan (MainWindow.TabExit.cs). Forget: delete this session's tab files. Save prompt off: keep dirty inactive tabs'
    /// files. Keep: every tab with work keeps its file plus tabs.json order. The next start offers what remains; other instances' files are never touched.
    /// </summary>
    private void ForgetProjectTabs()
    {
        // No pending write may recreate a file after ForgetSession (none is queued after it, and queued ones are dropped); a kept tab
        // must have its file (await its pending write, or write it here if it failed or never ran).
        FinishTabWrites();
        if (_tabExitPlan == TabExitPlan.Forget)
        {
            CancelTabWrites(_ => false);
            _tabs.ForgetSession();
            return;
        }

        WaitForKeptTabWrites();
        Func<ProjectTabHeader, bool> keep = _tabExitPlan == TabExitPlan.SuspendAll ? t => !t.Empty : t => KeptInFile(t) && t.Dirty && !t.Empty;
        WriteKeptTabsNow(keep);
        CancelTabWrites(keep);

        // Keep: also write order and active tab, marked intentional so the next start activates it (crash leftovers stay behind the start-up tab).
        if (_tabExitPlan == TabExitPlan.SuspendAll && _tabs.Tabs.Any(keep))
        {
            try
            {
                _tabs.WriteKeptManifest();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Project tabs: tabs.json could not be written as HandPeg closed (the kept tabs come back in the order they were last written)", ex);
            }
        }

        _tabs.ForgetSession(keep);
    }

    /// <summary>
    /// Post-Frame 1 idle work: sets the + tooltip, imports dead sessions' tabs into this session's folder as cold tabs (resumed on click),
    /// ordered most recent first after this session's own. If the start-up tab is still blank, the previously active tab replaces it
    /// (MainWindow.TabExit.cs).
    /// </summary>
    private void RecoverProjectTabs() => Faults.Forget(RecoverProjectTabsAsync(), nameof(RecoverProjectTabs));

    private async Task RecoverProjectTabsAsync()
    {
        try
        {
            // Set once (this method can rerun); the context menu reads its keys on open (KeybindManager.WithGesture in ProjectTabStrip.MenuFor).
            if (!_tabGesturesSet)
            {
                _tabGesturesSet = true;
                ProjectTabStrip.SetGestures(KeybindManager.WithGesture("New Project Tab", "tab.new"));
                KeybindManager.Changed += () => ProjectTabStrip.SetGestures(KeybindManager.WithGesture("New Project Tab", "tab.new"));
            }

            // Skip while a close is prompting (this idle callback also runs in its dialog loop): imported tabs would miss the prompt and a
            // Forget plan would delete them. A cancelled close reruns this (MainWindow_Closing); else the next start imports them. Not reentrant.
            if (_closeAsked || _recoveringTabs)
            {
                _leftTabsPutOff |= _closeAsked;
                return;
            }

            // Closing waits for the import (RefuseCloseMidSwitch): ForgetSession only deletes known tabs' files, but a tab not yet on the
            // strip would miss the exit prompt.
            var known = _tabs.Tabs.Select(t => t.Id).ToList();
            var adopted = new List<string>();
            string? leftFront;
            _recoveringTabs = true;
            try
            {
                (var left, leftFront) = await Task.Run(() =>
                {
                    var taken = _tabs.TakeLeftBehind(known, out var front);
                    return (taken, front);
                });
                // Second guard: a close is prompting, so add no tabs; ForgetSession then keeps the moved files for the next start.
                foreach (var tab in left)
                {
                    if (!_closeAsked && _tabs.Adopt(tab))
                        adopted.Add(tab.Id);
                }
            }
            finally
            {
                _recoveringTabs = false;
            }

            if (adopted.Count == 0)
                return;

            _tabs.Touch(_tabs.Active);

            // One-shot 180 ms slide-out from the active tab (ProjectTabStrip; skipped with extra animations off); a restored previously
            // active tab is shown as soon as it activates, while still loading.
            var shown = false;
            void Show()
            {
                if (shown)
                    return;
                shown = true;
                ShowTabs();
                ProjectTabStrip.SlideOutFromActive(adopted);
            }

            var said = _viewModel.StatusText;
            var forward = RestoredFrontToBring(leftFront, adopted, known);
            var stepsBack = forward?.StepsBack ?? 0;
            var broughtForward = forward is not null && await BringRestoredTabForwardAsync(forward, Show);
            Show();
            WriteTabManifest();
            AppLog.Write($"Project tabs: {adopted.Count} left in the cache by a session that ended were offered again"
                         + (forward is null ? "." : broughtForward ? ", the one in front last time in front again." : "; the one in front last time could not be brought back."));

            // Status: open project and recovered tab count, followed by the resume's own note (lagged crash copy, changed project file) or failure reason.
            var line = RestoredLine(broughtForward ? forward!.Title : null, adopted.Count);
            var resumeSaid = forward is not null && _viewModel.StatusText is { Length: > 0 } now && !string.Equals(now, said, StringComparison.Ordinal)
                && (!broughtForward || ResumeSaidOfItsOwn(now, stepsBack)) ? now : null;
            _viewModel.StatusText = resumeSaid is null ? line : $"{line} {resumeSaid}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the tabs left by the last session could not be offered", ex);
        }
    }

    /// <summary>Rewrites tabs.json (order and active tab) in this session's folder, off the UI thread.</summary>
    private void WriteTabManifest()
    {
        var json = _tabs.ManifestJson();
        _ = Task.Run(() =>
        {
            try
            {
                _tabs.WriteManifest(json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Project tabs: tabs.json could not be written", ex);
            }
        });
    }

    private void NoteTabFault(string what, Exception ex)
    {
        AppLog.Write($"Project tabs: could not {what}", ex);
        _viewModel.StatusText = $"Could not {what}: {ex.Message}";
    }
}
