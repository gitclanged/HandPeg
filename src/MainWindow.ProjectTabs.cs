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

// ----- Project tabs in the title bar (Pass 5.4A) -----
// Browser-style tabs after HandPeg's name: [ • Name × ] for each open project, then [ + ]. One view model and one GPU engine hold the
// tab in front; a tab put behind is a file in the cache (Services/ProjectTabs.cs, %Cache%\project-tabs\<session>\<id>.hptab: a project file
// of its own with its undo history, written by ProjectStore) and a header of a few kilobytes, so ten tabs take the memory of one. Switching
// never asks to save: the tab in front is written to the cache, the engine lets go of everything of it (its sources and decoders, the
// frame cache, the sound's decoders: GpuPreviewEngine.Suspend), and the other tab is opened from its file as a project is
// (MainViewModel.ResumeTabAsync), its unsaved changes still unsaved, its playhead, zoom and scroll where they were, its New Project name,
// folder and waiting Game Preset its own. The three tabs most recently in front keep a frozen still of their paused picture, shown over the
// player at once as one comes back, until the engine has drawn its first frame. Closing a tab asks Save / Discard / Cancel only when it has
// unsaved changes. Copied clips survive a switch (the clipboard is the view model's): Ctrl+C in one tab, Ctrl+V in the next. Ctrl+T, Ctrl+W,
// Ctrl+Tab and Ctrl+Shift+Tab (KeybindManager). Before the first frame there is one tab, made in memory; tabs another session left behind
// (it ended without closing, or closed keeping unsaved tabs) are offered after it, at idle, never opened by themselves. Each HandPeg running
// has a cache folder of its own, so a second window never offers or deletes the first one's tabs.
// Pass 5.4B: the strip is not drawn at all until a project is started or opened (a file added or dropped, a New Project made, a project
// opened, Ctrl+T or +): the start-up's single blank tab is no tab to show (StripHidden; the strip collapsed in MainWindow.xaml, so nothing of
// it is measured or drawn on the first frame), and closing back down to one blank tab collapses it again. Tabs can be dragged along the strip
// (the order written to tabs.json once let go), have a right-click menu (Save, Save As, Rename, Duplicate in a new tab, Open Containing
// Folder, Close, Close Others), and a tab behind shows a hover card (its still or thumbnail, frame and frame rate, saved or not).
// Pass 5.4D: each tab keeps the window it was looked at in. The 3D Exploded View and its camera: a tab put behind in 3D keeps its still
// as it was shown (the 3D picture, in the player's shape; the project's own thumbnail stays flat), leaves the view at once, and comes back
// in it at once with its camera where it was (the Live Encoded Preview turned off for it: the two never run together); turned on by hand
// the camera still starts where it always does. Its window layout (AppSettings.PerTabWindowLayouts, on as HandPeg comes): the main
// window's arrangement as the tab was put behind (its header's LayoutJson, under 4 KB with it: the side windows are the window's, never a
// tab's) is laid out again as it comes back, before its still shows; a layout chosen in a tab is that tab's from then on. New and opened
// tabs take the window as it is, and nothing lays a tab's layout out but a switch, a close or a switch taken back (never at start-up,
// nor as tabs left behind are taken in): the first frame's single dock build is the only one until the user changes tabs.
// Pass 5.4D-TabPerf (the user's U1, an instant switch): a tab brought to the front this session stays live in memory as it goes behind
// (a TabViewContext: the view model's part, MainViewModel.TabProjectContext, with its live Sequence and history; its still, overview
// sheet, selection, player zoom and the Timeline's open stacks), and a switch between such tabs swaps references at once, with no await
// between putting one behind and bringing the other back: no cache file read, no open, no fingerprint. The still is kept fresh while
// paused (MainWindow.TabStill.cs), so a switch never waits for a capture. The tab put behind is still written to its cache file (the
// crash recovery), on the thread pool after the switch, from a frozen copy, one tab's disk work after another (ProjectTabs.Enqueue). A
// tab only in its file (one a session left behind, or let go to it by the memory valve: too many tabs, too much memory) comes back as
// before. The engine still lets go of the tab put behind (GpuPreviewEngine.Suspend).
public partial class MainWindow
{
    private readonly ProjectTabs _tabs = new(ProjectTabs.DefaultRoot);

    // Ctrl+T or + pressed while the strip was hidden: the blank tab is shown as a tab of its own ([ New Project × ] [ + ]) until closing
    // leaves one tab again (5.4B, the orchestrator's decision: then it is cleared, and a blank tab collapses the strip).
    private bool _tabsRevealed;

    // A switch, a new tab or a close under way: another waits for it, and so does closing HandPeg. While it lasts the Timeline takes no
    // pointer, and keys and drops that change the project are refused (RefusedWhileDragging, Window_PreviewDrop): an edit made between
    // the outgoing tab's last write and the incoming tab's Sequence would land in neither.
    private bool _switchingTab
    {
        get => _switching;
        set
        {
            _switching = value;
            UnifiedTimeline.IsHitTestVisible = !value;
        }
    }

    private bool _switching;

    // The tabs a dead session left being moved into this session's folder (RecoverProjectTabs): closing HandPeg waits for it, so no
    // file moved in is left unknown to the strip as the session's files are let go (5.4A review).
    private bool _recoveringTabs;

    // Tabs whose file could not be brought back (a media drive not there, the cache unreadable): their files are kept, and coming to
    // the front (or a click on the one in front) tries again. Such a tab never takes on new work: that gets a tab of its own.
    private readonly HashSet<string> _unresumed = new(StringComparer.Ordinal);

    // The tab in front's dot and name, looked at again once its changes have settled (never every frame: the fingerprint is the whole state).
    private readonly DispatcherTimer _tabDirty = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>The short side of a tab's paused still, in pixels (about 2 MB at 16:9; three tabs keep one at most).</summary>
    private const int StillShortSide = 540;

    /// <summary>The title bar kept free right of +, in DIPs: the caption the window is dragged and maximized by.</summary>
    private const double CaptionGrip = 96;

    /// <summary>The strip's one tab and its handlers (before the first frame, from InitializeTitleBar: nothing read from the disk).</summary>
    private void InitializeProjectTabs()
    {
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
            // Made at once (the strip moved its own item already), so the strip and the tabs never disagree about the order mid-drag.
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

        _tabDirty.Tick += (_, _) =>
        {
            // Not under a switch (it ends with its own look), a drag (its commit is a change of its own) or playback (looked at as it stops).
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

            // What the tab is called follows its file, project and studio name at once (5.4D-TabPerf), playing or not: the name only, with
            // no fingerprint (the dot waits for the settling look).
            if (e.PropertyName is nameof(MainViewModel.HasSource) or nameof(MainViewModel.LocalMediaPath) or nameof(MainViewModel.CurrentProjectPath)
                or nameof(MainViewModel.NewProjectName))
            {
                RefreshTabTitle();
            }

            // The first file of a blank project shows the strip at once (5.4B), not after the settling look (skipped while playing).
            if (e.PropertyName == nameof(MainViewModel.HasSource) && ProjectTabStrip.Visibility != Visibility.Visible && !TabsHidden)
            {
                if (!_unresumed.Contains(_tabs.Active.Id))
                    _tabs.Active.Title = ActiveTabTitle();
                ShowTabs();
            }
        };

        // After the first frame, at idle: the keys in the + tooltip, and the tabs other sessions left behind.
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(RecoverProjectTabs, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Something that can change the tab in front's name or dot happened (an edit, an undo, a save, a status): looked at once it settles.</summary>
    private void NoteTabChanged()
    {
        _tabDirty.Stop();
        _tabDirty.Start();
    }

    /// <summary>The share of the title bar the tabs may take (<paramref name="room"/>: between the name and the buttons), less the caption kept free.</summary>
    private void FitProjectTabs(double room)
    {
        if (TitleBar.ActualWidth <= 0)
            return;
        ProjectTabStrip.Fit(room - CaptionGrip);
    }

    /// <summary>
    /// Whether the title bar shows no project tabs at all (pure: project-tabs-lazy-strip): one tab only, holding nothing (no file, clip,
    /// history, chosen frame, studio name or waiting Game Preset: MainViewModel.HoldsNothingForTab) and opened from no project file, not
    /// one whose file could not be brought back (its work is in the cache), and not shown on purpose by Ctrl+T or + (<paramref name="revealed"/>).
    /// </summary>
    internal static bool StripHidden(int count, bool unresumed, bool holdsNothing, string projectPath, bool revealed) =>
        count == 1 && !unresumed && holdsNothing && projectPath.Length == 0 && !revealed;

    /// <summary>Whether the strip is hidden now (<see cref="StripHidden"/> of the live view model: cheap, the tracks looked over once).</summary>
    private bool TabsHidden =>
        StripHidden(_tabs.Count, _unresumed.Contains(_tabs.Active.Id), _viewModel.HoldsNothingForTab, _viewModel.CurrentProjectPath, _tabsRevealed);

    /// <summary>
    /// The strip as the tabs are: their order, names, dots and the one in front (and which of them it has room to show). Hidden
    /// (<see cref="TabsHidden"/>) it is collapsed and holds no items, so nothing of it is measured or drawn; shown again, it is filled and
    /// laid out in the room the title bar last gave it before it is drawn. While a tab is dragged along it, its order and layout are the
    /// drag's: looked at again as the drag is let go.
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
            // A tab added or closed from the keyboard mid-drag ends the drag (its ReorderEnded shows the tabs as they are), so the strip's
            // indices never stop matching the tabs the drag moves.
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

        // A card open on a tab that came to the front (Ctrl+Tab under the pointer) or was closed (a middle click) closes: neither has one.
        if (ProjectTabStrip.HoverItem is { } hovered && (hovered.IsActive || !items.Contains(hovered)))
            ProjectTabStrip.CloseHoverCard();

        // Laid out (which tabs it has room for) before it is drawn, then shown.
        ProjectTabStrip.Refit();
        if (ProjectTabStrip.Visibility != Visibility.Visible)
        {
            ProjectTabStrip.Visibility = Visibility.Visible;

            // There are tabs to switch between from now on (never before the first frame: the strip is hidden there): the paused still
            // is kept fresh, so a switch never waits for one (5.4D-TabPerf).
            KeepTabStillFresh();
        }
    }

    /// <summary>
    /// The tab in front's header as the project is now: its name, file, dot (the fingerprint only when anything may have changed; 5.4D-TabPerf:
    /// none at all without <paramref name="fingerprint"/>, as a tab kept live comes back: its dot is what it was, looked at again once it settles).
    /// </summary>
    private void RefreshActiveTab(bool fingerprint = true)
    {
        // New work in front of a tab that could not be brought back is a tab of its own first.
        SplitFromUnresumed();

        // A tab that could not be brought back is still what its file holds (its name, file and dot), not the empty project in front.
        var tab = _tabs.Active;
        if (!_unresumed.Contains(tab.Id))
        {
            tab.Title = ActiveTabTitle();
            tab.ProjectPath = _viewModel.CurrentProjectPath;
            tab.Empty = _viewModel.HoldsNothingForTab;
            tab.Dirty = _viewModel.MayHaveUnsavedChanges && (fingerprint ? _viewModel.HasUnsavedChanges : tab.Dirty);
        }

        ShowTabs();
    }

    /// <summary>
    /// New work in front of a tab that could not be brought back (a file opened, dropped or typed in the Source box, a New Project, a frame
    /// chosen) never takes that tab over: the work gets a new tab of its own, in front, and the tab not brought back stays behind as a cold
    /// tab with its files (switching to it tries again). True when it did.
    /// </summary>
    private bool SplitFromUnresumed()
    {
        var stuck = _tabs.Active;
        if (!_unresumed.Contains(stuck.Id) || _viewModel.HoldsNothingForTab)
            return false;

        _unresumed.Remove(stuck.Id);
        _tabs.TakeWorkOff(ActiveTabTitle());
        AppLog.Write("Project tabs: new work in front of a tab that could not be brought back went into a tab of its own.");
        ShowTabs();
        WriteTabManifest();
        return true;
    }

    /// <summary>Whether a tab's work is its cache file, not the view model: one behind the tab in front, or the one in front that could not be brought back.</summary>
    private bool KeptInFile(ProjectTabHeader tab) => !ReferenceEquals(tab, _tabs.Active) || _unresumed.Contains(tab.Id);

    /// <summary>
    /// What the tab in front says (<see cref="TabTitleOf"/>): its project's name, else the name the New Project studio gave it, else its first
    /// file's (5.4D-TabPerf: while that file is still being opened into it, the name it was given as the open began), else New Project.
    /// </summary>
    private string ActiveTabTitle() =>
        TabTitleOf(_viewModel.CurrentProjectPath, _viewModel.NewProjectName, _viewModel.HasSource,
            _openingName is { } opening && opening.Id == _tabs.Active.Id ? opening.Name : null, FirstFileInFront);

    /// <summary>The file the tab in front is named for once it has one: the file last opened, else the first clip's.</summary>
    private string? FirstFileInFront() => _viewModel.LocalMediaPath is { Length: > 0 } local
        ? local
        : _viewModel.Document.Tracks.SelectMany(t => t.Clips).Select(c => c.SourcePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

    /// <summary>
    /// A project tab's name (pure, 5.4D-TabPerf: the goldens' tab-perf-instant-name): its project file's name, else the New Project studio's
    /// name, else (with a video or clip open) its first file's (<paramref name="file"/>, asked only then), else the file being opened into it
    /// (<paramref name="opening"/>: <see cref="OpeningTabName"/>), else New Project. The name a file gives as its open begins is the very
    /// name it gives once it is open, so a tab is named at once and never renamed by the open's end; an open that fails leaves New Project.
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

    // ----- A tab named at once by the file opened into it (5.4D-TabPerf) -----

    // The file being opened into the tab in front with nothing open (its tab's id, and the name it gives): the tab's name until the open ends.
    private (string Id, string Name)? _openingName;

    /// <summary>
    /// The name a file opened (dropped, or chosen from the Media pane or File &gt; Open) into a tab gives it as the open begins (pure): the
    /// name the open will give it (<see cref="TabTitleOf"/>, its file's), for a tab with no video or clip open (<paramref name="hasSource"/>) and
    /// no project file, that was not left unresumed (new work in front of one gets a tab of its own); null otherwise, and for a link (its
    /// file's name is known only once it is downloaded).
    /// </summary>
    internal static string? OpeningTabName(string file, bool hasSource, string projectPath, bool unresumed) =>
        hasSource || projectPath.Length > 0 || unresumed || MainViewModel.IsWebAddress(file)
            || Path.GetFileNameWithoutExtension(file.Trim().Trim('"')) is not { Length: > 0 } name
            ? null
            : name;

    /// <summary>
    /// A file is about to be opened into the tab in front (5.4D-TabPerf): with nothing open in it, the tab takes the file's name now, not
    /// once the file is probed and open (<see cref="OpeningTabName"/>). True when it did; the caller ends it (<see cref="EndTabOpening"/>)
    /// as the open ends, whichever way.
    /// </summary>
    private bool NameTabForOpen(string file)
    {
        if (_switchingTab || OpeningTabName(file, _viewModel.HasSource, _viewModel.CurrentProjectPath, _unresumed.Contains(_tabs.Active.Id)) is not { } name)
            return false;

        _openingName = (_tabs.Active.Id, name);
        RefreshTabTitle();
        return true;
    }

    /// <summary>The open <see cref="NameTabForOpen"/> named the tab for is over: its name is what the tab holds now (its file's; New Project again when it failed).</summary>
    private void EndTabOpening()
    {
        _openingName = null;
        RefreshTabTitle();
    }

    /// <summary>
    /// The tab in front's name only, at once (5.4D-TabPerf): no fingerprint, no dot, playing or not. Never while tabs switch (the view model
    /// is between two tabs then; the switch ends with its own look) nor for a tab in front that could not be brought back (it is its file's).
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
    /// Whether the tabs may change now; otherwise why not, in the status bar (the same rules Swap Project had). 5.4D-TabPerf: a tab is put
    /// behind at once, so nothing can start between this and the switch (it was asked again after a write before). The 5.4D-TabPerf review:
    /// never while clips dropped or recorded are still being added (MainViewModel.HasPendingAdds), so none lands in the next tab.
    /// </summary>
    private bool MayChangeTabs(string doing)
    {
        var why = _switchingTab ? $"Wait for the project tab to finish switching before {doing}."
            : _viewModel.HasPendingAdds ? "Adding clips: switch tabs when they are in."
            : _viewModel.IsRecording ? $"Stop the voiceover recording before {doing}."
            : _viewModel.IsBusy ? $"Wait for the running operation to finish, or cancel it, before {doing}."
            : TimelineDragHeld ? $"Let go of the drag on the timeline before {doing}."
            : null;
        if (why is null)
            return true;

        _viewModel.StatusText = why;
        return false;
    }

    // ----- Switching -----

    /// <summary>A tab clicked, or Ctrl+Tab: the tab in front put behind (never a prompt) and this one brought back. A click on the tab in front that could not be brought back tries again.</summary>
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

        _switchingTab = true;
        TabWrite? write = null;
        try
        {
            // 5.4D-TabPerf: put behind at once (kept live in memory), and a tab kept live brought back at once: nothing awaited between.
            var behind = PutActiveBehind(out write);
            _tabs.Activate(tab);
            ShowTabs();

            // Its own window layout first (5.4D), so its still shows where its player will be.
            ApplyTabLayout(tab);
            var hot = _hydrated.TryGetValue(tab.Id, out var live);
            if (hot)
            {
                if (!AttachHydrated(tab, live!))
                    TakeBack(tab, behind);
            }
            else
            {
                // Only in its file: read and opened as before, the tab put behind written meanwhile.
                QueueTabWrite(write);
                write = null;
                await ResumeTabAsync(tab, behind);
            }

            RefreshActiveTab(fingerprint: !hot);
            WriteTabManifest();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("switch project tabs", ex);
        }
        finally
        {
            _switchingTab = false;

            // The tab put behind written to its cache file after the switch (5.4D-TabPerf), on the thread pool, from its frozen copy.
            QueueTabWrite(write);
        }

        LetGoForMemory();
    }

    /// <summary>The tab in front that could not be brought back, clicked: its file (or what it keeps in memory) tried once more (nothing in front to put behind: it is empty).</summary>
    private async Task RetryResumeAsync(ProjectTabHeader tab)
    {
        _switchingTab = true;
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
            _switchingTab = false;
        }
    }

    /// <summary>Ctrl+Tab (1) and Ctrl+Shift+Tab (-1): the next tab or the one before, round the strip.</summary>
    private async Task CycleProjectTabAsync(int step)
    {
        if (_tabs.Next(_tabs.Active, step) is { } next)
            await SwitchToTabAsync(next);
        else
            _viewModel.StatusText = "Only one project tab is open: + (or Ctrl+T) opens another.";
    }

    /// <summary>
    /// Puts the tab in front behind (5.4D-TabPerf: at once, never refused, never awaited): playback stopped, its window's part taken (5.4D:
    /// its 3D view, camera and layout), and its project kept live in memory with its still, overview sheet, selection and views
    /// (<see cref="DetachActive"/>), then the 3D view left and everything the engine held of it let go. Its cache file is written after the
    /// switch: <paramref name="write"/> (null when nothing is to be written), which the caller hands to <see cref="QueueTabWrite"/>. A tab
    /// holding nothing worth keeping (a fresh one) keeps nothing but its window's part.
    /// </summary>
    private ProjectTabHeader PutActiveBehind(out TabWrite? write)
    {
        write = null;
        SplitFromUnresumed();
        var tab = _tabs.Active;

        // A file still to be opened (5.4D-TabPerf) names no tab put behind: its name is what the tab holds.
        if (_openingName is { } opening)
        {
            _openingName = null;
            if (opening.Id == tab.Id)
                tab.Title = ActiveTabTitle();
        }

        // A still being taken now is of the tab as it was before this switch: dropped as it comes in (5.4D-TabPerf review), whichever tab
        // is in front by then.
        _stillAsked++;

        // Its window first (5.4D), as it is now: the 3D view still up for its still, a layout previewed by the selector put back.
        CaptureWindowStateForTab(tab);
        StopPlayingForTabs();
        if (_viewModel.HoldsNothingForTab)
        {
            // Nothing worth keeping (a fresh tab, or one whose file could not be brought back: that keeps its file and tries again later).
            if (!_unresumed.Contains(tab.Id))
                (tab.Empty, tab.Dirty) = (true, false);
            _tabs.SetStill(tab, null);
            (_liveStill, _liveStillMark) = (null, default);
            _explodedWaitingTab = null;
            LeaveExplodedAtOnce();
            return tab;
        }

        var live = DetachActive(tab, out write);
        _hydrated[tab.Id] = live;
        _unresumed.Remove(tab.Id);
        _tabs.SetStill(tab, null);
        OnTimelinePointer(null);

        // The 3D view left at once (5.4D): its state is the tab's header's now, and the engine lets go of the tab.
        _explodedWaitingTab = null;
        LeaveExplodedAtOnce();
        _gpu?.Suspend();
        ClearProgramStrip();
        KeptTabsChanged();
        RetryFailedTabWrites();
        return tab;
    }

    // ----- Each tab's own window (Pass 5.4D) -----

    // A tab brought back in the 3D view whose player was not shown yet: it goes into it once the player shows, while it is in front.
    // The 5.4D review: also one whose view is not restored yet (ResumeTabAsync to RestoreTabView), or whose engine has not started.
    private ProjectTabHeader? _explodedWaitingTab;
    private bool _explodedWaitHooked;

    /// <summary>
    /// The window's part of a tab's header, as it is now (5.4D): whether the player is in the 3D view and where its camera is, and the main
    /// window's layout (AppSettings.PerTabWindowLayouts; none while it is off). A layout the Layout Selector is previewing is never taken:
    /// the selector closes and the arrangement in use comes back first. Only ever the tab in front's (or its duplicate's): never in
    /// TakeHeaderForTab, which a duplicate's header does not take its window from. The 5.4D review: with the
    /// layout, the Media Browser's view (a Vertical layout's Filmstrip); and nothing of a tab in front that could not be brought back (it
    /// shows an empty project, not its own window: its header keeps the window it was put behind in).
    /// </summary>
    private void CaptureWindowStateForTab(ProjectTabHeader tab)
    {
        EndLayoutPreview();
        if (_unresumed.Contains(tab.Id))
            return;

        var perTab = AppSettings.Current.PerTabWindowLayouts;
        tab.LayoutJson = TabLayoutOf(_dock, perTab);
        tab.MediaView = TabMediaViewOf(MediaBrowser.ViewMode, perTab);
        TakeExplodedState(tab, _explodedUi && _gpu is { } engine ? engine.TargetPose : null, _explodedWaitingTab);
    }

    /// <summary>
    /// The 3D view's part of a tab's header (5.4D, window-free: the goldens' project-tabs-exploded-pose): in it at <paramref name="shown"/>
    /// (the engine's target pose) while the view is up; else, while a tab brought back in it still waits for its player or the engine
    /// (<paramref name="waiting"/>), in it at the camera that tab came back with (its own header kept as it is: a tab put behind again
    /// before its view was restored keeps it, 5.4D review); else flat.
    /// </summary>
    internal static void TakeExplodedState(ProjectTabHeader tab, GpuPreviewEngine.CameraPose? shown, ProjectTabHeader? waiting)
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
        }
    }

    /// <summary>
    /// The Media Browser's view a tab put behind keeps with its layout (5.4D review, window-free: the goldens' project-tabs-window-layouts):
    /// its name (a Vertical layout's Filmstrip, set by the layout's Then and never in the layout's JSON), or none while each tab does not
    /// remember its own layout.
    /// </summary>
    internal static string? TabMediaViewOf(Views.MediaBrowserView view, bool perTab) => perTab ? view.ToString() : null;

    /// <summary>The Media Browser's view a tab's header names (5.4D review); null for none, or a name no view has.</summary>
    internal static Views.MediaBrowserView? MediaViewOf(string? name) =>
        Enum.TryParse<Views.MediaBrowserView>(name, ignoreCase: true, out var view) && Enum.IsDefined(view) ? view : null;

    /// <summary>
    /// The Layout Selector closed, and a layout it was previewing put back at once (5.4D): its own Closed does so only later (a popup
    /// closes asynchronously), which would take a preview for a tab's layout, or lay the arrangement before it over the next tab's.
    /// </summary>
    private void EndLayoutPreview()
    {
        _layoutHover.Close();
        RestoreLayoutBeforePreview();
    }

    /// <summary>
    /// The main window's layout a tab put behind keeps (5.4D, window-free: the goldens' project-tabs-window-layouts): the arrangement as
    /// DockManager.Save writes it, or none while each tab does not remember its own.
    /// </summary>
    internal static string? TabLayoutOf(DockManager dock, bool perTab) => perTab ? dock.Save() : null;

    /// <summary>
    /// A tab's own layout laid out in the main window as it comes to the front (5.4D, window-free: the goldens' project-tabs-window-layouts):
    /// DockManager.Load, never Apply (a layout that differs only in the tabs in front is bound again, not lost), the side windows as they
    /// are. Nothing while each tab does not remember its own, for a tab with none, or when it is the window's already. True when laid out.
    /// (A pane being dragged keeps the window as it is: DockManager builds nothing mid-drag, and a switch never starts during one.)
    /// </summary>
    internal static bool LoadTabLayout(DockManager dock, ProjectTabHeader tab, bool perTab)
    {
        if (!perTab || tab.LayoutJson is not { Length: > 0 } json || string.Equals(json, dock.Save(), StringComparison.Ordinal))
            return false;

        dock.Load(json);
        return true;
    }

    /// <summary>
    /// A tab's own layout in the main window as it comes to the front (5.4D), kept as the window's arrangement as any change of it is.
    /// The 5.4D review: the Layout Selector closed and a layout it previews put back first (one opened during the switch's awaits would
    /// otherwise lay the last tab's arrangement over this one as it closes), and the tab's Media Browser view set before the build (as a
    /// layout's Then sets it), so the Media pane's floor is the one its layout was made with.
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
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A layout that could not be laid out is let go (written down): the tab still comes to the front.
            AppLog.Write("Project tabs: a tab's own layout could not be laid out", ex);
        }
    }

    /// <summary>
    /// A tab brought back (5.4D) in the 3D view if it was left in it: at once, its camera where it was. The Live Encoded Preview is turned
    /// off for it first (the two never run together; the tab's own 3D view wins). A player not shown yet is waited for once, and so is an
    /// engine not started yet (5.4D review: Law 2 starts it with the first video, and a fault or Reload Player lets it go): the engine's
    /// start brings it in (RestoreExplodedOnEngine). Never a status line of refusal: a tab with nothing open simply stays flat.
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
    /// The engine started (5.4D review: MainWindow.GpuPreview's StartGpuAsync, before the player opens): a tab in front brought back in the
    /// 3D view while there was none goes into it now, its camera where it was, so its first frame is the 3D one.
    /// </summary>
    private void RestoreExplodedOnEngine()
    {
        if (_explodedWaitingTab is { } tab && ReferenceEquals(tab, _tabs.Active))
            RestoreExplodedForTab(tab);
    }

    /// <summary>
    /// A tab going behind's header brought up to date from the view model as it is now (it still holds the tab): its name, file, dot,
    /// playhead, zoom and scroll, its New Project name, folder and waiting Game Preset, its frame. 5.4D-TabPerf: the dot without a
    /// fingerprint (the last settled look, or "may have changed" while one is due: <see cref="TabDirtyNow"/>); the exact one is worked out
    /// on the thread pool as its cache file is written, and put right then.
    /// </summary>
    private void TakeHeaderForTab(ProjectTabHeader tab)
    {
        (tab.Title, tab.ProjectPath, tab.Empty, tab.Dirty) = (ActiveTabTitle(), _viewModel.CurrentProjectPath, false, TabDirtyNow(tab));
        (tab.NewProjectName, tab.NewProjectFolder, tab.WaitingTemplate) = (_viewModel.NewProjectName, _viewModel.NewProjectFolder, _viewModel.TemplateWaitingForTab);
        (tab.PlayheadMs, tab.ViewStart, tab.ViewSpan, tab.ViewFit, tab.VerticalOffset) =
            (_viewModel.PositionMs, _viewStart, _viewSpan, _viewFit, UnifiedTimeline.Surface.VerticalOffset);
        TakeFrameForTab(tab);
    }

    /// <summary>
    /// The tab in front's dot as it goes behind, without a fingerprint (5.4D-TabPerf): none when nothing may have changed since the last
    /// save or open; else the last settled look's (RefreshActiveTab), or "changed" while a look is still due (an edit, a setting, playback).
    /// </summary>
    private bool TabDirtyNow(ProjectTabHeader tab) => _viewModel.MayHaveUnsavedChanges && (tab.Dirty || _tabDirty.IsEnabled || _viewModel.IsPlaying);

    /// <summary>The project's frame and frame rate in a tab's header, as Project Settings has them now (5.4B: its hover card once it is behind).</summary>
    private void TakeFrameForTab(ProjectTabHeader tab)
    {
        var settings = _viewModel.CurrentProjectSettings;
        (tab.FrameWidth, tab.FrameHeight) = ProjectTabs.FrameOf(settings.NaturalWidth, settings.NaturalHeight, settings.Vertical);
        tab.FrameRate = settings.FrameRate.Fps;
    }

    /// <summary>
    /// Brings a tab only in its cache file back into the view model (it is in front already; 5.4D-TabPerf: one kept live comes back by
    /// <see cref="AttachHydrated"/> instead): its paused picture over the player at once (a hot tab), its project read from the cache (after
    /// any write of it still under way) and opened as a project is, its unsaved changes unsaved, its New Project name, folder and waiting
    /// Game Preset its own, then its playhead, zoom and scroll once the open has settled. A tab that kept nothing (an empty one) is a new
    /// project. False when its file could not be brought back: the tab is then empty in front, its file kept to try again.
    /// </summary>
    /// <param name="behind">The tab put behind for it (a switch), gone back to when the view model cannot be emptied; null when there is none (a close, a retry).</param>
    private async Task<bool> ResumeTabAsync(ProjectTabHeader tab, ProjectTabHeader? behind)
    {
        var id = tab.Id;
        if (tab.Empty)
        {
            HideTabStill();
            if (_viewModel.NewProject())
            {
                // A name given to it behind (Rename Project...) is kept for its first save, as its studio name is.
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
        try
        {
            (state, saved) = await _tabs.Enqueue(id, () => (_tabs.ReadState(id), _tabs.ReadSaved(id)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            AppLog.Write("Project tabs: a tab could not be read back from the cache", ex);
            return NotResumed(tab, behind, $"\"{tab.Title}\" could not be read back from the cache ({ex.Message}).");
        }

        // Opened as a project is: a new Sequence put in place when it worked. One that did not (its file not found) leaves the last
        // tab's Sequence where it was, which must never pass for this tab's.
        var before = _viewModel.Document;
        try
        {
            await _viewModel.ResumeTabAsync(state, tab.ProjectPath, tab.Dirty, saved, tab.NewProjectName, tab.NewProjectFolder, tab.WaitingTemplate);
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

        // The tab is the view model's again: its files go (written anew when it is next put behind), and its waiting Game Preset is the
        // view model's. After its own disk work and before any asked later (5.4D-TabPerf): a delete that came after the next put-behind's
        // write would lose the tab.
        _unresumed.Remove(id);
        tab.WaitingTemplate = null;
        DeleteTabFiles(id);

        // Left in the 3D view: waiting for its view to be restored (5.4D review), so a tab put behind again before then (a held
        // Ctrl+Tab) keeps its header's 3D view and camera rather than being taken as flat.
        if (tab.ExplodedOpen)
            _explodedWaitingTab = tab;
        _ = Dispatcher.BeginInvoke(() => RestoreTabView(tab), DispatcherPriority.Background);
        return true;
    }

    /// <summary>A tab that could not be brought back: empty in front, its file kept, and why in the status bar.</summary>
    private bool NotResumed(ProjectTabHeader tab, ProjectTabHeader? behind, string why)
    {
        HideTabStill();
        if (!_viewModel.NewProject())
        {
            TakeBack(tab, behind);
            return false;
        }

        _unresumed.Add(tab.Id);
        _viewModel.StatusText = why;
        return false;
    }

    /// <summary>
    /// The view model could not take the tab coming to the front (an operation or a recording started meanwhile: the status line says
    /// which): the tab put behind for it comes back to the front instead, from memory (5.4D-TabPerf: it was kept live), as it was, and
    /// <paramref name="tab"/> stays behind as it was. With no tab to go back to (the one in front was closed), or one that cannot come
    /// back either, <paramref name="tab"/> stays in front as one not brought back (its file, or what it keeps in memory, kept to try
    /// again), and what the view model still holds gets a tab of its own (<see cref="SplitFromUnresumed"/>): never passed off as this tab's.
    /// </summary>
    private void TakeBack(ProjectTabHeader tab, ProjectTabHeader? behind)
    {
        if (behind is null || ReferenceEquals(behind, tab) || !_tabs.Tabs.Contains(behind))
        {
            _unresumed.Add(tab.Id);
            return;
        }

        _tabs.Activate(behind);
        ShowTabs();

        // Its own window again (5.4D): its layout, and (from memory) its project, still, view and 3D view.
        ApplyTabLayout(behind);
        if (_hydrated.TryGetValue(behind.Id, out var live) && !AttachHydrated(behind, live))
        {
            _tabs.Activate(tab);
            ShowTabs();
            _unresumed.Add(tab.Id);
        }
    }

    /// <summary>A tab brought back, after its open has settled (the player and the time bars have taken the new Sequence): where it was looked at.</summary>
    private void RestoreTabView(ProjectTabHeader tab)
    {
        if (!ReferenceEquals(tab, _tabs.Active) || !_viewModel.HasSource)
        {
            // In front with nothing to show: flat (its wait let go); one put behind meanwhile has had its wait let go already.
            if (ReferenceEquals(tab, _tabs.Active) && ReferenceEquals(_explodedWaitingTab, tab))
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
        _viewModel.PositionMs = at;

        // In the 3D view if it was left in it (5.4D), before the player opens: its first frame is the 3D one its still shows.
        RestoreExplodedForTab(tab);
        OpenInPlayer((long)at, paused: true);
        WatchForFirstFrame();
    }

    /// <summary>
    /// Playback, a shuttle, and a drag or a lift in the 3D view stopped: what a tab is put behind (or closed) in. The 3D view itself stays
    /// up (5.4D): a tab put behind keeps its still as it was shown, then leaves it at once (PutActiveBehind, LeaveExplodedAtOnce).
    /// </summary>
    private void StopPlayingForTabs()
    {
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

    // ----- The paused still over the player -----
    // The player is a native window, which nothing of WPF's draws over but the window kept over it (PlayerOverlay): the still lies there,
    // under everything else of it, until the engine has presented a scene drawn since the tab came back (its exact frames, or after a
    // moment whatever it has), or two and a half seconds at most. A bounded watch of a few frames, only after a switch.

    private Image? _tabStill;
    private DispatcherTimer? _stillTimer;
    private long _stillSince, _stillScene;
    private int _stillSeen;

    /// <summary>How long the still waits for the engine's exact frames before any presented frame will do, and how long it stays at most, ms.</summary>
    private const long StillExactWaitMs = 700, StillLongestMs = 2500;

    /// <summary>A BGRA8 picture as a frozen bitmap (its alpha meaningless: Bgr32).</summary>
    private static BitmapSource StillOf(byte[] bgra, int width, int height)
    {
        var still = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        still.Freeze();
        return still;
    }

    private void ShowTabStill(BitmapSource? still)
    {
        if (still is null)
        {
            HideTabStill();
            return;
        }

        if (_tabStill is null)
        {
            _tabStill = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(_tabStill, BitmapScalingMode.HighQuality);
            PlayerOverlay.Children.Insert(0, _tabStill);
        }

        _tabStill.Source = still;
        _tabStill.Visibility = Visibility.Visible;
    }

    private void HideTabStill()
    {
        _stillTimer?.Stop();
        if (_tabStill is null)
            return;
        _tabStill.Source = null;
        _tabStill.Visibility = Visibility.Collapsed;
    }

    /// <summary>The still stays until the engine has presented a scene since now (see the section above).</summary>
    private void WatchForFirstFrame()
    {
        if (_tabStill is not { Visibility: Visibility.Visible })
            return;

        (_stillScene, _stillSince, _stillSeen) = (_gpu?.PresentedScene ?? 0, Environment.TickCount64, 0);
        if (_stillTimer is null)
        {
            _stillTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            _stillTimer.Tick += (_, _) =>
            {
                var waited = Environment.TickCount64 - _stillSince;
                var drawn = _gpu is { } engine && engine.PresentedScene > _stillScene && (engine.PresentedExact || waited > StillExactWaitMs);

                // Seen on two turns running, so the viewport has presented it too.
                _stillSeen = drawn ? _stillSeen + 1 : 0;
                if (_stillSeen >= 2 || waited > StillLongestMs || _viewModel.IsPlaying)
                    HideTabStill();
            };
        }

        _stillTimer.Start();
    }

    // ----- Tabs kept live in memory (Pass 5.4D-TabPerf) -----
    // A tab put behind keeps everything of it in memory (a TabViewContext, by its id): the view model's part (its live Sequence, history,
    // fingerprints, what was made of its file) and the window's (its still, overview sheet, selection, player zoom and pan, the Timeline's
    // open stacks). It comes back by a swap of references (AttachHydrated). Its cache file is written after the switch from a frozen copy
    // (QueueTabWrite), each tab's disk work after the last (ProjectTabs.Enqueue), so a crash, a close or the memory valve finds it there.
    // The valve (LetGoForMemory) lets the least recently used go back to its file when too many are kept or memory runs short.

    /// <summary>How long closing HandPeg, a save from memory or a close waits for a tab's cache file still being written, ms.</summary>
    private const int IoWaitMs = 5000;

    /// <summary>The most tabs kept live in memory (beyond it the least recently used goes back to its file).</summary>
    internal const int MostLiveTabs = 16;

    /// <summary>How often the memory valve looks again while tabs are kept live, ms.</summary>
    private const int ValveTickMs = 30_000;

    /// <summary>The tabs behind the one in front kept live in memory, by id (the one in front is the view model's).</summary>
    private readonly Dictionary<string, TabViewContext> _hydrated = new(StringComparer.Ordinal);

    // The cache files queued and not written yet: written as they are should HandPeg fall over first (WriteUnwrittenTabs). Any thread.
    private readonly List<TabWrite> _unwritten = [];
    private bool _crashWritesHooked, _letGoing, _memoryHooked;
    private DispatcherTimer? _valveTimer;

    /// <summary>A still's moment: the undo mark, the playhead and the 3D view it was taken at (a stale one only covers the player until its first frame).</summary>
    private readonly record struct StillMark(object? UndoMark, double PositionMs, bool Shown3D);

    /// <summary>A tab put behind, kept live in memory (see the section above). Made on the window's thread and used only there.</summary>
    private sealed class TabViewContext(MainViewModel.TabProjectContext project)
    {
        /// <summary>The view model's part: its live Sequence, history and fingerprints, its frozen copy.</summary>
        public MainViewModel.TabProjectContext Project { get; } = project;

        /// <summary>Its paused picture as last kept fresh (MainWindow.TabStill.cs), and when it was taken; null for none yet.</summary>
        public BitmapSource? Still { get; init; }

        public StillMark StillMark { get; init; }

        /// <summary>The overview sheet it showed (whole), and the undo mark its Sequence was at.</summary>
        public ThumbnailSheet? ProgramSheet { get; init; }

        public object? ProgramSheetMark { get; init; }

        /// <summary>What was selected on its Timeline: its views of before, followed by their clips' ids as it comes back.</summary>
        public object? SelectedClip { get; init; }

        public Layer? RangeAnchor { get; init; }

        public bool PlayheadVideoSelected { get; init; }

        /// <summary>The player's zoom and pan (Keybinds' navigation).</summary>
        public (double Zoom, double PanX, double PanY) PlayerView { get; init; }

        /// <summary>The Timeline's mini-stacks open (their rows' keys are any project's: "captions", a track's number).</summary>
        public IReadOnlyList<(object Key, ChildStack Stack)> Expansion { get; init; } = [];

        /// <summary>Its cache file's write after it went behind.</summary>
        public TabWrite? Write { get; set; }

        /// <summary>About how much memory it holds, in bytes (the memory valve's measure).</summary>
        public long ApproxBytes { get; set; }

        /// <summary>Its cache file could not be written: never let go to it (its work is only here).</summary>
        public bool MustStay { get; set; }
    }

    /// <summary>
    /// A tab's cache file to write after a switch (5.4D-TabPerf): from its frozen copy, with its header as it went behind; the exact dot
    /// worked out on the thread pool. Its state moves on the thread pool (and on a thread falling over): read under its own lock.
    /// </summary>
    private sealed class TabWrite(ProjectTabHeader tab, TabViewContext live, string header, BitmapSource? thumbnailFrom)
    {
        private readonly Lock _gate = new();
        private bool _done, _failed, _cancelled, _superseded, _dirty, _running;

        public ProjectTabHeader Tab { get; } = tab;

        public string Id { get; } = tab.Id;

        public TabViewContext Live { get; } = live;

        /// <summary>The header as the tab went behind (its dot the cheap one).</summary>
        public string Header { get; } = header;

        /// <summary>The flat still a project never saved takes its thumbnail from (5.4B), or null.</summary>
        public BitmapSource? ThumbnailFrom { get; } = thumbnailFrom;

        /// <summary>The thumbnail made of it on the thread pool (null for none), and the file's length once written.</summary>
        public byte[]? Jpeg { get; set; }

        public long Bytes { get; set; }

        /// <summary>Its last try is over: written, or failed (<see cref="Failed"/>); false again while it is tried once more.</summary>
        public bool IsDone
        {
            get
            {
                lock (_gate)
                    return _done;
            }
        }

        /// <summary>Its last try failed or faulted: the tab has no recovery copy yet (tried again: 5.4D-TabPerf review).</summary>
        public bool Failed
        {
            get
            {
                lock (_gate)
                    return _failed;
            }
        }

        /// <summary>Not to be written any more (its tab closed): nothing of it may come back at the next start.</summary>
        public bool IsCancelled
        {
            get
            {
                lock (_gate)
                    return _cancelled;
            }
        }

        /// <summary>Whether it is still to be written: not written whole (a failed one still is), not cancelled.</summary>
        public bool IsWanted
        {
            get
            {
                lock (_gate)
                    return !(_done && !_failed) && !_cancelled;
            }
        }

        /// <summary>The exact dot (whether it has unsaved changes), once written; null before, or once a save made it out of date.</summary>
        public bool? Dirty
        {
            get
            {
                lock (_gate)
                    return _done && !_failed && !_superseded ? _dirty : null;
            }
        }

        /// <summary>
        /// The write claimed by one writer (any thread, 5.4D-TabPerf review: the thread pool, the crash writer, the clean close): true when it is
        /// still wanted and nobody is writing it, and it is that writer's to <see cref="Finish"/> or <see cref="Fail"/> now (a failed one is
        /// tried once more). False when it is written, cancelled or being written: two copies of one tab's file are never written at once.
        /// </summary>
        public bool TryStart()
        {
            lock (_gate)
            {
                if (_running || _cancelled || (_done && !_failed))
                    return false;
                (_running, _done, _failed) = (true, false, false);
                return true;
            }
        }

        public void Finish(bool dirty)
        {
            lock (_gate)
                (_running, _done, _failed, _dirty) = (false, true, false, dirty);
        }

        /// <summary>The claimed try failed (nothing once it finished: a writer calls it on its way out whatever happened).</summary>
        public void Fail()
        {
            lock (_gate)
            {
                if (_running)
                    (_running, _done, _failed) = (false, true, true);
            }
        }

        public void Cancel()
        {
            lock (_gate)
                _cancelled = true;
        }

        /// <summary>A save while it was behind (SaveTabInPlace): its dot is the save's now, whatever this write found.</summary>
        public void Superseded()
        {
            lock (_gate)
                _superseded = true;
        }
    }

    /// <summary>
    /// The tab in front taken into a context of its own, kept live (5.4D-TabPerf; its header brought up to date first): the view model
    /// lets go of it (MainViewModel.DetachForTab), and the window's part goes with it. <paramref name="write"/> is its cache file to write.
    /// </summary>
    private TabViewContext DetachActive(ProjectTabHeader tab, out TabWrite write)
    {
        TakeHeaderForTab(tab);
        var sheetMark = _viewModel.UndoMark;
        var (still, stillMark) = (_liveStill, _liveStillMark);
        var live = new TabViewContext(_viewModel.DetachForTab(tab.Title))
        {
            Still = still,
            StillMark = stillMark,
            ProgramSheet = SheetToKeep(),
            ProgramSheetMark = sheetMark,
            SelectedClip = _selectedClip,
            RangeAnchor = _rangeAnchor,
            PlayheadVideoSelected = _playheadVideoSelected,
            PlayerView = (_playerZoom, _playerPanX, _playerPanY),
            Expansion = UnifiedTimeline.Surface.SaveExpansion(),
        };

        (_liveStill, _liveStillMark) = (null, default);

        // A project with no thumbnail yet (never saved) keeps its paused still as one (5.4B), so its hover card has a picture once it is
        // cold too: made a JPEG on the thread pool, with the write. Never a 3D still (5.4D): a project's picture is flat.
        write = new TabWrite(tab, live, ProjectTabs.HeaderJson(tab), still is not null && !stillMark.Shown3D ? still : null);
        live.Write = write;
        live.ApproxBytes = ApproxBytesOf(live, 0);
        return live;
    }

    /// <summary>
    /// A tab kept live brought back to the front (5.4D-TabPerf; it is in front already, its layout laid out), at once: its still over the
    /// player, its project swapped into the view model (MainViewModel.AttachTab), its overview sheet, selection, open stacks and player view
    /// as it left them, its view and playhead restored now (not after an open), the engine opened on it. Its cache file goes, after its
    /// write (the tab in front keeps none). False when the view model is held by an operation or a recording (nothing changed: the caller
    /// takes the tab put behind back). A fault leaves the tab in front as one not brought back (what it holds kept in memory to try again).
    /// </summary>
    private bool AttachHydrated(ProjectTabHeader tab, TabViewContext live)
    {
        // What its header says of its file and first save is the latest (a save or a rename while it was behind).
        var project = live.Project;
        (project.ProjectPath, project.NewProjectName, project.NewProjectFolder) = (tab.ProjectPath, tab.NewProjectName, tab.NewProjectFolder);

        // Its still only when it is the picture it comes back to (5.4D-TabPerf review): taken at its playhead (within a frame), flat or in
        // 3D as it is now; a still from before playback or from the other view covers nothing.
        var stillFits = StillFits(live.StillMark.PositionMs, live.StillMark.Shown3D, tab.PlayheadMs, tab.ExplodedOpen, tab.FrameRate);
        if (stillFits)
            ShowTabStill(live.Still);
        else
            HideTabStill();

        // Its selection as its views of before: the restore follows them by their clips' ids (Timeline_Restored).
        (_selectedClip, _rangeAnchor, _playheadVideoSelected) = (live.SelectedClip, live.RangeAnchor, live.PlayheadVideoSelected);
        try
        {
            if (!_viewModel.AttachTab(project))
            {
                HideTabStill();
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Part of it may be the view model's now: an empty project instead (its own Sequence never touched again), and the tab keeps
            // what it holds in memory to try once more.
            AppLog.Write("Project tabs: a tab kept in memory could not be brought back", ex);
            HideTabStill();
            _viewModel.ParkDocument();
            _viewModel.NewProject();
            _unresumed.Add(tab.Id);
            _viewModel.StatusText = $"\"{tab.Title}\" could not be brought back ({ex.Message}). Its tab keeps it: click it again to try once more.";
            return true;
        }

        // In front again: its cache file is not written now (a write still to come is let go) and goes after any under way.
        _hydrated.Remove(tab.Id);
        _unresumed.Remove(tab.Id);
        tab.WaitingTemplate = null;
        live.Write?.Cancel();
        ForgetUnwritten(live.Write);
        DeleteTabFiles(tab.Id);

        if (live.ProgramSheet is { } sheet)
            AdoptProgramSheet(sheet, live.ProgramSheetMark);
        UnifiedTimeline.Surface.RestoreExpansion(live.Expansion);
        RestorePlayerView(live.PlayerView);
        (_liveStill, _liveStillMark) = stillFits ? (live.Still, live.StillMark) : (null, default);

        // Left in the 3D view: waiting for its view to be restored (5.4D review), as a tab read from its file does.
        if (tab.ExplodedOpen)
            _explodedWaitingTab = tab;
        RestoreTabView(tab);
        _viewModel.BackgroundSelected = _selectedClip is Layer { IsBackground: true };
        UpdateTimelineInspector();
        UpdatePlayerSelection();
        KeptTabsChanged();

        // Its rows are laid out at the next layout: the scroll is looked at again then (it is held to the rows there are).
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(tab, _tabs.Active) && Math.Abs(UnifiedTimeline.Surface.VerticalOffset - tab.VerticalOffset) > 0.5)
                UnifiedTimeline.Surface.VerticalOffset = tab.VerticalOffset;
        }, DispatcherPriority.Background);
        return true;
    }

    /// <summary>
    /// Whether a tab's kept still is the picture it comes back to (pure, 5.4D-TabPerf review: the goldens' project-tabs-tabperf-review): taken
    /// within a frame of its playhead (<paramref name="frameRate"/> its Project Settings', 30 fps while it is not known), in the 3D view
    /// exactly when it comes back in it.
    /// </summary>
    internal static bool StillFits(double stillMs, bool still3D, double playheadMs, bool exploded, double frameRate) =>
        still3D == exploded && Math.Abs(stillMs - playheadMs) <= 1000.0 / (frameRate > 0 ? frameRate : 30);

    /// <summary>The player's zoom and pan a tab kept, put back (none: fitted, as a project opened is; the Canvas's view fitted with it).</summary>
    private void RestorePlayerView((double Zoom, double PanX, double PanY) view)
    {
        ResetNavigationViews();
        if (view.Zoom <= 0)
            return;

        (_playerZoom, _playerPanX, _playerPanY) = view;
        _navPicture = PictureSize();
        ApplyPlayerView();
    }

    /// <summary>
    /// A tab put behind's cache file written on the thread pool (5.4D-TabPerf), after the switch and after the tab's other disk work: its
    /// frozen copy (a thumbnail made of its still first when it has none), its saved state's fingerprint when it has unsaved changes, and
    /// its header with the exact dot (worked out there: MainViewModel.FrozenStateDiffers), which the strip then shows. A failure is written
    /// down, and the tab stays in memory (never let go to a file it has not got) and listed as one with no recovery copy yet: the crash
    /// writer writes it too, and the valve's tick and the next tab put behind try it again (5.4D-TabPerf review). Nothing for null.
    /// </summary>
    private void QueueTabWrite(TabWrite? write)
    {
        if (write is not { IsWanted: true })
            return;

        HookTabCrashWrites();
        lock (_unwritten)
        {
            if (!_unwritten.Contains(write))
                _unwritten.Add(write);
        }

        _ = _tabs.Enqueue(write.Id, () => WriteTabNow(write));
    }

    /// <summary>The tabs kept live whose cache file could not be written, tried again (5.4D-TabPerf review: the valve's tick, a tab put behind).</summary>
    private void RetryFailedTabWrites()
    {
        foreach (var live in _hydrated.Values)
        {
            if (live.Write is { Failed: true, IsWanted: true } write)
                QueueTabWrite(write);
        }
    }

    /// <summary>
    /// One tab's cache file written (the thread pool; see <see cref="QueueTabWrite"/>), then told to the window's thread. Claimed first
    /// (5.4D-TabPerf review): one the crash writer took, or a retry under way, is never written beside it.
    /// </summary>
    private void WriteTabNow(TabWrite write)
    {
        if (!write.TryStart())
            return;

        try
        {
            var project = write.Live.Project;

            // The frozen copy itself is never written to here (5.4D-TabPerf review: a save or the crash writer may be reading it this
            // moment): the thumbnail made of the still goes into a copy of it.
            var frozen = project.Frozen;
            if (write.ThumbnailFrom is { } still && frozen.ThumbnailJpeg is null)
            {
                if (write.Jpeg is null)
                {
                    var (width, height) = (still.PixelWidth, still.PixelHeight);
                    var pixels = new byte[width * height * 4];
                    still.CopyPixels(pixels, width * 4, 0);
                    write.Jpeg = ProjectThumbnail.EncodeJpeg(pixels, width, height);
                }

                frozen = frozen.ShallowCopy();
                frozen.ThumbnailJpeg = write.Jpeg;
            }

            var dirty = project.MayBeDirty && MainViewModel.FrozenStateDiffers(frozen, project.SavedFingerprint);
            var header = ProjectTabs.ReadHeader(write.Header) is { } read && read.Dirty != dirty ? ProjectTabs.HeaderJson(read with { Dirty = dirty }) : write.Header;
            _tabs.WriteTab(write.Id, frozen, dirty ? project.SavedFingerprint : null, header);
            write.Bytes = new FileInfo(_tabs.StateFile(write.Id)).Length;
            write.Finish(dirty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: a tab put behind could not be written to the cache (it stays in memory, with no recovery copy yet)", ex);
        }
        finally
        {
            // Not written whole (failed, or faulted with anything at all): still listed, to be written as HandPeg falls over and tried again.
            write.Fail();
            if (!write.IsWanted)
            {
                lock (_unwritten)
                    _unwritten.Remove(write);
            }
        }

        if (write.IsDone)
            _ = Dispatcher.BeginInvoke(() => TabWritten(write), DispatcherPriority.Background);
    }

    /// <summary>A tab's cache file written (or not), on the window's thread: its exact dot, its thumbnail, its size for the memory valve.</summary>
    private void TabWritten(TabWrite write)
    {
        var (tab, live) = (write.Tab, write.Live);
        if (!_tabs.Tabs.Contains(tab) || !ReferenceEquals(_hydrated.GetValueOrDefault(tab.Id), live))
            return;

        if (write.Failed)
        {
            live.MustStay = true;
            _viewModel.StatusText = $"\"{tab.Title}\" has no recovery copy yet: its cache file could not be written. It stays in memory, and HandPeg tries again.";
            return;
        }

        live.MustStay = false;
        if (write.Jpeg is { } jpeg)
            live.Project.Thumbnail ??= jpeg;
        live.ApproxBytes = ApproxBytesOf(live, write.Bytes);
        if (write.Dirty is { } dirty && dirty != tab.Dirty)
        {
            tab.Dirty = dirty;
            ShowTabs();
        }
    }

    /// <summary>
    /// About how much memory a tab kept live holds, in bytes (the valve's measure, 5.4D-TabPerf): its project's objects about three times
    /// its cache file (its live Sequence, its frozen copy, its history; 4 MB until the file is written), and the pictures it keeps: its
    /// still and overview sheet, and (the 5.4D-TabPerf review) its clips' views' filmstrips and waveforms, its tracks' waveforms and the
    /// timeline's, each picture once (<see cref="PictureBytes"/>).
    /// </summary>
    private static long ApproxBytesOf(TabViewContext live, long fileBytes)
    {
        var project = live.Project;
        IEnumerable<Layer> views = project.Views is { } kept ? kept.Views.Values.Concat(kept.EmptyTrackViews.Values) : [];
        var pictures = PictureBytes(views.SelectMany(view => new[] { view.Filmstrip, view.Waveform })
            .Concat(project.TrackWaveforms.Values).Append(project.TimelineWaveform).Append(live.Still).Append(live.ProgramSheet?.Picture));
        return (fileBytes > 0 ? 3 * fileBytes : 4L << 20) + pictures;
    }

    /// <summary>
    /// What pictures hold in memory (pure, 5.4D-TabPerf review: the goldens' project-tabs-tabperf-review): each bitmap's width x height x 4
    /// bytes, each object once however many hold it (one file's filmstrip on all its clips' views); nulls and drawings count nothing.
    /// </summary>
    internal static long PictureBytes(IEnumerable<ImageSource?> pictures)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var picture in pictures)
        {
            if (picture is BitmapSource bitmap && seen.Add(bitmap))
                bytes += 4L * bitmap.PixelWidth * bitmap.PixelHeight;
        }

        return bytes;
    }

    /// <summary>A tab's three files deleted after its other disk work (5.4D-TabPerf: a write still queued is never undone by it, nor done after it).</summary>
    private void DeleteTabFiles(string id) => _ = _tabs.Enqueue(id, () => _tabs.DeleteFiles(id));

    /// <summary>A tab closed: what it kept in memory let go, and its cache file's write, if it was still to come, never made.</summary>
    private void ForgetKeptTab(string id)
    {
        if (!_hydrated.Remove(id, out var live))
            return;
        live.Write?.Cancel();
        ForgetUnwritten(live.Write);
        KeptTabsChanged();
    }

    /// <summary>
    /// A write cancelled (its tab came back or closed) taken off the list of those not written yet (5.4D-TabPerf review): one that failed
    /// stays listed until written, and would otherwise hold its tab's frozen copy for the rest of the session. Nothing for null.
    /// </summary>
    private void ForgetUnwritten(TabWrite? write)
    {
        if (write is null)
            return;
        lock (_unwritten)
            _unwritten.Remove(write);
    }

    /// <summary>A tab behind's paused picture: the one it keeps in memory, else the one the tabs keep for the most recent of those in their files.</summary>
    private BitmapSource? StillOfTab(ProjectTabHeader tab) =>
        _hydrated.TryGetValue(tab.Id, out var live) && live.Still is { } still ? still : _tabs.StillOf(tab);

    /// <summary>
    /// The tabs kept live changed (5.4D-TabPerf): the timeline keeps waveform tiles for them too (256, and 128 more for each, at most
    /// 1024), and the memory valve looks again every 30 s while any is kept.
    /// </summary>
    private void KeptTabsChanged()
    {
        UnifiedTimelineControl.WaveTileCapacity = 256 + 128 * _hydrated.Count;
        HookTabMemory();
        if (_hydrated.Count == 0)
        {
            _valveTimer?.Stop();
            return;
        }

        if (_valveTimer is null)
        {
            _valveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(ValveTickMs) };
            _valveTimer.Tick += (_, _) =>
            {
                RetryFailedTabWrites();
                if (!_switchingTab && !_viewModel.IsPlaying)
                    LetGoForMemory();
            };
        }

        if (!_valveTimer.IsEnabled)
            _valveTimer.Start();
    }

    /// <summary>
    /// What the memory hygiene asks before a compacting collection (5.4D-TabPerf, set once, after the first frame): nothing playing,
    /// recording, being dragged on the Timeline or switching tabs. Asked from any thread: looked at on the window's.
    /// </summary>
    private void HookTabMemory()
    {
        if (_memoryHooked)
            return;
        _memoryHooked = true;
        MemoryHygiene.IsQuiet = () => Dispatcher.CheckAccess()
            ? QuietForMemory()
            : Dispatcher.Invoke(QuietForMemory, DispatcherPriority.Background, CancellationToken.None, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>One compacting collection of the managed heap asked for, once the editor is quiet (a tab closed or let go to its file).</summary>
    private void CompactWhenQuiet(string reason)
    {
        HookTabMemory();
        MemoryHygiene.RequestCompactOnce(reason);
    }

    private bool QuietForMemory() =>
        !_viewModel.IsPlaying && _gpu is not { IsPlaying: true } && !_switchingTab && !TimelineDragHeld && !_viewModel.IsRecording;

    /// <summary>
    /// Which tab kept live the memory valve lets go back to its cache file now (pure, 5.4D-TabPerf: the goldens' tab-perf-valve), or null:
    /// when more than <see cref="MostLiveTabs"/> are kept, when what they hold passes <paramref name="budgetBytes"/>
    /// (AppSettings.HydratedTabBudgetMb), or when Windows says memory is short (<paramref name="memoryShort"/>), the least recently in front
    /// of them, never one of the two used last (the tab in front and the one before it) nor one that must stay (its file not written).
    /// </summary>
    internal static string? TabToLetGo(IReadOnlyList<ProjectTabHeader> tabs, IReadOnlyDictionary<string, long> keptBytes, IReadOnlySet<string> mustStay,
        long budgetBytes, bool memoryShort)
    {
        if (keptBytes.Count == 0 || !(keptBytes.Count > MostLiveTabs || keptBytes.Values.Sum() > budgetBytes || memoryShort))
            return null;

        var recent = tabs.OrderByDescending(t => t.LastActive).Take(2).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return tabs.Where(t => keptBytes.ContainsKey(t.Id) && !recent.Contains(t.Id) && !mustStay.Contains(t.Id))
            .OrderBy(t => t.LastActive).Select(t => t.Id).FirstOrDefault();
    }

    /// <summary>
    /// The memory valve (5.4D-TabPerf), after a tab went behind and every 30 s while tabs are kept live: the tab <see cref="TabToLetGo"/>
    /// picks goes back to its cache file once that is written (its still kept while it is among the most recent), and the heap is compacted
    /// once the editor is quiet (MemoryHygiene). Then it looks again.
    /// </summary>
    private async void LetGoForMemory()
    {
        if (_letGoing || _hydrated.Count == 0)
            return;

        try
        {
            var info = GC.GetGCMemoryInfo();
            var memoryShort = info.HighMemoryLoadThresholdBytes > 0 && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes;
            var budget = (long)Math.Max(AppSettings.Current.HydratedTabBudgetMb, 0) << 20;
            var kept = _hydrated.ToDictionary(p => p.Key, p => p.Value.ApproxBytes, StringComparer.Ordinal);
            var staying = _hydrated.Where(p => p.Value.MustStay).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            if (TabToLetGo(_tabs.Tabs, kept, staying, budget, memoryShort) is not { } id || _tabs.Find(id) is not { } tab)
                return;

            _letGoing = true;
            var live = _hydrated[id];
            try
            {
                await _tabs.IoOf(id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("Project tabs: a tab's disk work failed before it was let go to its file", ex);
            }

            // Brought back, closed or written badly meanwhile: kept as it is.
            if (!ReferenceEquals(_hydrated.GetValueOrDefault(id), live) || ReferenceEquals(tab, _tabs.Active))
                return;
            if (live.Write is not { IsDone: true, Failed: false })
            {
                live.MustStay = true;
                return;
            }

            _hydrated.Remove(id);
            _tabs.SetStill(tab, live.Still);
            KeptTabsChanged();
            AppLog.Write(memoryShort ? "Project tabs: a tab kept in memory was let go to its cache file (memory is short)."
                : "Project tabs: a tab kept in memory was let go to its cache file (too many kept).");
            CompactWhenQuiet("a project tab let go to its cache file");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the memory valve failed", ex);
            return;
        }
        finally
        {
            _letGoing = false;
        }

        LetGoForMemory();
    }

    /// <summary>
    /// As HandPeg closes (5.4D-TabPerf): every tab's disk work done first (5 s at most), and the dots of the tabs kept live made the exact
    /// ones their writes found (their tell to the window's thread may not have come yet).
    /// </summary>
    private void FinishTabWrites()
    {
        if (!_tabs.WaitFor(null, IoWaitMs))
            AppLog.Write("Project tabs: a tab's cache file was still being written as HandPeg closed (waited 5 s)");
        TakeWrittenDots();
    }

    /// <summary>The dots of the tabs kept live made the exact ones their writes found (their tell to the window's thread may not have come yet).</summary>
    private void TakeWrittenDots()
    {
        foreach (var live in _hydrated.Values)
        {
            if (live.Write is { Dirty: { } dirty } write && _tabs.Tabs.Contains(write.Tab))
                write.Tab.Dirty = dirty;
        }
    }

    /// <summary>
    /// As HandPeg closes keeping the tabs behind with unsaved changes (5.4D-TabPerf review): the disk work still under way for any tab that
    /// may be kept waited for to its end, however long (the title says "Saving tabs..."), so its file is never written a second time beside
    /// it nor cut off as the process ends; their dots the exact ones then.
    /// </summary>
    private void WaitForKeptTabWrites()
    {
        var busy = _tabs.Tabs.Where(t => KeptInFile(t) && !t.Empty && _tabs.IsBusyWith(t.Id)).ToList();
        if (busy.Count == 0)
            return;

        _viewModel.StatusText = "Saving tabs...";
        Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        foreach (var tab in busy)
            _tabs.WaitFor(tab.Id, Timeout.Infinite);
        TakeWrittenDots();
    }

    /// <summary>
    /// The tabs kept live that HandPeg keeps for the next start as it closes (<paramref name="keep"/>), written now on this thread when their
    /// write after the switch failed or never ran (5.4D-TabPerf): their work is only in memory otherwise. Claimed first (the review): one
    /// written, or being written, is never written again beside it.
    /// </summary>
    private void WriteKeptTabsNow(Func<ProjectTabHeader, bool> keep)
    {
        foreach (var tab in _tabs.Tabs.Where(keep))
        {
            if (!_hydrated.TryGetValue(tab.Id, out var live) || live.Write is { } claimed && !claimed.TryStart())
                continue;
            try
            {
                var project = live.Project;
                _tabs.WriteTab(tab.Id, project.Frozen, project.MayBeDirty ? project.SavedFingerprint : null, ProjectTabs.HeaderJson(tab));
                live.Write?.Finish(project.MayBeDirty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("Project tabs: a tab kept for the next start could not be written as HandPeg closed", ex);
            }
            finally
            {
                live.Write?.Fail();
            }
        }
    }

    /// <summary>The writes of the tabs kept live that HandPeg does not keep for the next start let go as it closes (none is made after now).</summary>
    private void CancelTabWrites(Func<ProjectTabHeader, bool> keep)
    {
        foreach (var (id, live) in _hydrated)
        {
            if (_tabs.Find(id) is not { } tab || !keep(tab))
                live.Write?.Cancel();
        }
    }

    /// <summary>The cache files not written yet are written should HandPeg fall over (hooked with the first one queued, never before the first frame).</summary>
    private void HookTabCrashWrites()
    {
        if (_crashWritesHooked)
            return;
        _crashWritesHooked = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.IsTerminating)
                WriteUnwrittenTabs();
        };
    }

    /// <summary>How long HandPeg falling over waits, in all, for tabs' cache files being written on the thread pool, ms.</summary>
    private const int CrashIoWaitMs = 3000;

    /// <summary>
    /// HandPeg falling over (any thread, 5.4D-TabPerf): the tabs put behind whose cache file was queued and not written yet (or failed) are
    /// written now from their frozen copies (nothing of the window's thread is touched), so the next start offers them. The review: one
    /// being written on the thread pool is waited for (3 s at most in all), never written a second time beside it; a write is claimed
    /// first (<see cref="TabWrite.TryStart"/>). Nothing escapes from here.
    /// </summary>
    private void WriteUnwrittenTabs()
    {
        TabWrite[] left;
        lock (_unwritten)
            left = [.. _unwritten];
        var until = Environment.TickCount64 + CrashIoWaitMs;
        foreach (var write in left)
        {
            try
            {
                if (!write.IsWanted)
                    continue;

                // Its disk work under way (this write, or work asked before it): waited for, then written here only if it still is not.
                if (_tabs.IsBusyWith(write.Id))
                    WaitForTabIo(write.Id, until);
                if (!write.TryStart())
                    continue;

                try
                {
                    var project = write.Live.Project;
                    _tabs.WriteTab(write.Id, project.Frozen, project.MayBeDirty ? project.SavedFingerprint : null, write.Header);
                    write.Finish(project.MayBeDirty);
                }
                finally
                {
                    write.Fail();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("Project tabs: a tab could not be written to the cache as HandPeg fell over", ex);
            }
        }
    }

    /// <summary>A tab's disk work waited for until <paramref name="until"/> (Environment.TickCount64) at most; a failure is its writer's to note.</summary>
    private void WaitForTabIo(string id, long until)
    {
        try
        {
            _tabs.IoOf(id).Wait((int)Math.Max(0, Math.Min(until - Environment.TickCount64, int.MaxValue)));
        }
        catch (AggregateException)
        {
        }
    }

    // ----- New, open, close -----

    /// <summary>
    /// + and Ctrl+T (and the Project Manager's New Project): the tab in front put behind, never a prompt, and a new empty project in a
    /// tab of its own. With <paramref name="reuseEmpty"/> (the New Project studio, the Project Manager), a fresh tab in front is used as it is.
    /// False when it could not be made (the reason in the status bar): the tab in front stays as it was.
    /// </summary>
    private Task<bool> NewProjectTabAsync(bool reuseEmpty = false) => Task.FromResult(NewProjectTab(reuseEmpty));

    /// <summary><see cref="NewProjectTabAsync"/>, at once (5.4D-TabPerf: the tab in front is put behind without a wait).</summary>
    private bool NewProjectTab(bool reuseEmpty)
    {
        // The strip hidden (5.4B): Ctrl+T shows the blank tab as a tab, [ New Project × ] [ + ], rather than making a second blank one.
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

        _switchingTab = true;
        TabWrite? write = null;
        try
        {
            var behind = PutActiveBehind(out write);
            var made = _tabs.Add("New Project");
            _tabs.Activate(made);
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
            _switchingTab = false;
            QueueTabWrite(write);
            LetGoForMemory();
        }
    }

    /// <summary>A tab just made for new work whose view model could not be emptied (see <see cref="TakeBack"/>): the tab put behind back in front, the new one gone.</summary>
    private void BackOutOf(ProjectTabHeader made, ProjectTabHeader behind)
    {
        TakeBack(made, behind);
        _unresumed.Remove(made.Id);
        if (!ReferenceEquals(made, _tabs.Active))
            _tabs.Close(made);
        ShowTabs();
    }

    /// <summary>
    /// Opens a project (.hproj or .hpb) in a project tab, never in place of the open work and never with a prompt: one open in a tab
    /// already comes to the front; with nothing worth keeping in the tab in front it opens there; otherwise in a new tab. Every way a project
    /// is opened goes through here (the Project Manager, a project dropped on the window, one double-clicked in Explorer, the Source box).
    /// </summary>
    internal async Task OpenProjectInTabAsync(string path)
    {
        try
        {
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

            // Nothing in front worth keeping: opened there, as before (queued behind a running encode, as LoadProjectAsync does).
            if (_viewModel.HoldsNothingForTab && !_unresumed.Contains(_tabs.Active.Id))
            {
                if (_switchingTab)
                {
                    _viewModel.StatusText = "Wait for the project tab to finish switching before opening a project.";
                    return;
                }

                await _viewModel.LoadProjectAsync(path);
                RefreshActiveTab();
                return;
            }

            if (!MayChangeTabs("opening a project in a new tab"))
                return;

            _switchingTab = true;
            TabWrite? write = null;
            try
            {
                var behind = PutActiveBehind(out write);
                QueueTabWrite(write);
                write = null;
                var made = _tabs.Add(Path.GetFileNameWithoutExtension(path));
                _tabs.Activate(made);
                HideTabStill();
                ShowTabs();

                // A fresh session first: a project that cannot be opened leaves its tab empty, never holding the last tab's work.
                if (!_viewModel.NewProject())
                {
                    BackOutOf(made, behind);
                    return;
                }

                await _viewModel.LoadProjectAsync(path);
                RefreshActiveTab();
                WriteTabManifest();
            }
            finally
            {
                _switchingTab = false;
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
    /// × on a tab, a middle click on it, Ctrl+W (null: the tab in front): Save / Discard / Cancel only when it has unsaved changes (a tab
    /// behind with a project file is saved from its cache file, never brought back for it; one never saved comes to the front for its first
    /// save), then its files deleted. The tab after it (or before, at the end) comes to the front; closing the last tab leaves one empty tab.
    /// </summary>
    private async Task CloseProjectTabAsync(ProjectTabHeader? which)
    {
        // Ctrl+W with no tab shown (5.4B): nothing to close, and the blank project is not made anew.
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
        if (tab.Dirty && !tab.Empty && !await AskToSaveTabAsync(tab))
            return;

        // The question may have brought the tab to the front (its first save) and kept a dialog up a while: looked at again.
        if (!_tabs.Tabs.Contains(tab) || _switchingTab)
            return;
        active = ReferenceEquals(tab, _tabs.Active);
        if (active && !MayChangeTabs("closing a project tab"))
            return;

        try
        {
            if (!active)
            {
                _unresumed.Remove(tab.Id);
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
                // The last tab: emptied, not closed (kept as it is when the view model cannot be emptied: the status line says why), its
                // 3D view left at once (5.4D).
                StopPlayingForTabs();
                HideTabStill();
                if (!_viewModel.NewProject())
                    return;
                _explodedWaitingTab = null;
                LeaveExplodedAtOnce();
                _unresumed.Remove(tab.Id);
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

            _switchingTab = true;
            try
            {
                // What was open is let go (saved or discarded above), not written to the cache; its 3D view with it, at once (5.4D),
                // and the next tab's own layout laid out (before its still shows).
                StopPlayingForTabs();
                OnTimelinePointer(null);
                EndLayoutPreview();
                _explodedWaitingTab = null;
                LeaveExplodedAtOnce();
                _gpu?.Suspend();
                ClearProgramStrip();

                // Its still goes, and one being taken is dropped as it comes in (5.4D-TabPerf review).
                ForgetLiveStill();
                _tabs.Activate(next);
                _unresumed.Remove(tab.Id);
                ForgetKeptTab(tab.Id);
                _tabs.Close(tab);
                ForgetRevealIfOne();
                ShowTabs();
                ApplyTabLayout(next);

                // 5.4D-TabPerf: the next tab from memory when it is kept live (the view model's Sequence, let go, is replaced), else from its file.
                if (!_hydrated.TryGetValue(next.Id, out var live) || !AttachHydrated(next, live))
                    await ResumeTabAsync(next, null);
                RefreshActiveTab();
                WriteTabManifest();
            }
            finally
            {
                _switchingTab = false;
            }

            CompactWhenQuiet("a project tab closed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteTabFault("close the project tab", ex);
        }
    }

    /// <summary>
    /// Closing left one tab (5.4B, the orchestrator's decision): a Ctrl+T or + that showed the strip is forgotten, so a blank tab left
    /// alone collapses it again (also from two tabs down to one); one that holds work keeps it shown.
    /// </summary>
    private void ForgetRevealIfOne()
    {
        if (_tabs.Count == 1)
            _tabsRevealed = false;
    }

    /// <summary>
    /// Save / Discard / Cancel for a tab with unsaved changes as it closes: true to go on closing it (saved, or let go). A tab with a project
    /// file is saved to it (one behind from its cache file, as the view model's own save would write it); one never saved comes to the front
    /// and is saved as any first save is, in the Project Manager with its name box in hand, and closes only once that save is made.
    /// </summary>
    private async Task<bool> AskToSaveTabAsync(ProjectTabHeader tab)
    {
        var ask = new ConfirmDialog("Close Project Tab", $"\"{tab.Title}\" has changes that were not saved as a project.\n\nSave them before its tab closes, or let them go?",
            "Save", "Discard") { Owner = this };
        if (ask.ShowDialog() != true)
            return false;
        if (ask.ChoseOther)
            return true;

        if (KeptInFile(tab) && tab.ProjectPath.Length > 0)
            return SaveTabInPlace(tab, tab.Title).StartsWith("Project saved", StringComparison.Ordinal);
        if (!KeptInFile(tab) && _viewModel.CurrentProjectPath.Length > 0)
            return _viewModel.SaveProjectTo(_viewModel.CurrentProjectPath).StartsWith("Project saved", StringComparison.Ordinal);

        // Never saved: in front first (a click on it, or a retry of one that could not be brought back); the status line says why not.
        if (KeptInFile(tab))
        {
            await SwitchToTabAsync(tab);
            if (KeptInFile(tab))
                return false;
        }

        return FirstSaveInFront();
    }

    /// <summary>The tab in front saved for the first time, as Ctrl+S on a new project does (the Project Manager, its name box in hand): whether it was.</summary>
    private bool FirstSaveInFront()
    {
        var dialog = new ProjectManagerWindow(_viewModel, focusName: true) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            // Something else asked for there (a project opened, a New Project, a bundle): done, and the tab stays open.
            _ = ActOnProjectManagerAsync(dialog);
            return false;
        }

        return _viewModel.CurrentProjectPath.Length > 0 && !_viewModel.HasUnsavedChanges;
    }

    /// <summary>
    /// Saves a tab behind the one in front from its cache file (5.4D-TabPerf: from memory when it is kept live, after its cache file's
    /// write: the same frozen copy), without bringing it back, where the view model's own save would put it
    /// (<see cref="TabSavePath"/>: its project file, a fully portable folder when Settings say so, the New Project studio's folder, else the
    /// Projects folder under <paramref name="name"/>). Another project's file is never written over without the user's word. A line for the
    /// status bar ("Project saved: ..." when it was).
    /// </summary>
    private string SaveTabInPlace(ProjectTabHeader tab, string name)
    {
        try
        {
            var title = name.Trim().Length > 0 ? name.Trim() : tab.Title;
            var (path, portable) = TabSavePath(tab.ProjectPath, tab.NewProjectFolder, title, AppSettings.Current.FullyPortableProjectSave);
            if (!SameProjectFile(path, tab.ProjectPath) && File.Exists(path) && !MayReplaceProject(path))
                return _viewModel.StatusText = $"Not saved: {Path.GetFileNameWithoutExtension(path)} is another project, and it was left as it is.";

            // Kept live (5.4D-TabPerf review): after its cache file's write, never beside it (still under way after 5 s: not saved, said so),
            // and from a copy of its frozen state with the save's name and time: the frozen copy itself is never written to (the thread pool,
            // or HandPeg falling over, may be writing the cache file from it).
            var live = _hydrated.GetValueOrDefault(tab.Id);
            if (live is not null && !_tabs.WaitFor(tab.Id, IoWaitMs))
                return _viewModel.StatusText = "Still writing this tab's recovery copy: try again in a moment.";
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
            if (live is not null)
            {
                // Saved as it comes back: one fingerprint then (the view model's own save takes one as well).
                live.Project.MarkSavedOnAttach = true;
                live.Write?.Superseded();
                WriteTabHeader(tab);
            }

            ProjectStore.NotifyChanged();
            ShowTabs();
            return _viewModel.StatusText = $"Project saved: {Path.GetFileNameWithoutExtension(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return _viewModel.StatusText = $"Could not save the project: {ex.Message}";
        }
    }

    /// <summary>
    /// Where a tab behind the one in front is saved, as the view model's save puts the tab in front (pure: project-tabs-save-path): a saved
    /// tab to its project file (with fully portable saves, its portable folder's file, or a new portable folder of its name, as Ctrl+S does;
    /// never for an autosave); a tab never saved to a portable folder of <paramref name="name"/>, else into the New Project studio's folder,
    /// else the Projects folder, the name made fit for a file. Whether it is a portable save.
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

    /// <summary>Whether the user lets a tab's save write over another project's file of the same name (never by default).</summary>
    private bool MayReplaceProject(string path) =>
        new ConfirmDialog("Replace Project", $"A project is already saved as {path}.\n\nReplace it with this tab's project? What it holds now is lost.", "Replace")
        {
            Owner = this, DefaultsToCancel = true,
        }.ShowDialog() == true;

    /// <summary>A portable save's background copies, written to the log should they fail (the save itself is done).</summary>
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

    /// <summary>What the Project Manager asked for, done in the project tabs: a New Project made, + New Project, a bundle saved, a project opened.</summary>
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
            _viewModel.RenameProjectFile(target);
            RefreshActiveTab();
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
    /// bring another tab to the front, and the Project Manager can open another project meanwhile.
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
            if (ReferenceEquals(ProjectTabStrip.HoverItem, item))
                item.Preview = picture;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: a tab's hover card could not be filled", ex);
        }
    }

    // ----- Closing HandPeg, and the tabs other sessions left behind -----

    /// <summary>
    /// Whether HandPeg must not close now (MainWindow_Closing, before anything else): a project tab is coming to the front, half in the
    /// view model and half in its cache file, so neither could be asked about or kept as it is; or the tabs a dead session left are being
    /// taken in (RecoverProjectTabs). The status bar says to wait.
    /// </summary>
    private bool RefuseCloseMidSwitch()
    {
        // The tabs a dead session left, being moved into this session's folder (5.4A review): closing then would let the session's files
        // go with some of them moved in but not on the strip yet.
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
    /// As HandPeg closes (MayClose, before the tab in front is asked about): every tab behind it with unsaved changes asked about in turn,
    /// as the tab in front is (Save and Close, saved from its file without bringing it back; Close Without Saving; Cancel). False when the
    /// user chose to stay. Nothing is asked when the settings say not to ask on exit: those tabs' files are kept then (ForgetProjectTabs),
    /// and the next start offers them again.
    /// </summary>
    private bool MayCloseProjectTabs()
    {
        SplitFromUnresumed();

        // 5.4D-TabPerf: the cache files still being written after a switch first (each tab's dot is then its exact one).
        FinishTabWrites();
        if (!AppSettings.Current.PromptToSaveOnExit)
            return true;

        foreach (var tab in _tabs.Tabs.Where(t => KeptInFile(t) && t.Dirty && !t.Empty).ToList())
        {
            AppLog.Write("Closing with unsaved changes in a project tab behind the one in front.");
            var dialog = new SaveOnExitDialog(tab.Title) { Owner = this, Title = $"Unsaved Changes: {tab.Title}" };
            dialog.ShowDialog();
            switch (dialog.Choice)
            {
                case ExitChoice.Cancel:
                    return false;

                case ExitChoice.SaveAndClose:
                    var saved = SaveTabInPlace(tab, dialog.ProjectName);
                    if (!saved.StartsWith("Project saved", StringComparison.Ordinal)
                        && new ConfirmDialog("Project Not Saved", $"{saved}\n\nClose HandPeg anyway? The changes to \"{tab.Title}\" that were not saved are lost.", "Close Without Saving")
                        {
                            Owner = this, DefaultsToCancel = true, CancelText = "Keep HandPeg Open",
                        }.ShowDialog() != true)
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// A clean close: this session's tab files go, so nothing is offered back at the next start; with the prompt to save on exit off, the
    /// files of the tabs behind with unsaved changes stay (nothing asked about them, and autosave covers only the tab in front), and the next
    /// start offers them again. Never another HandPeg's.
    /// </summary>
    private void ForgetProjectTabs()
    {
        // 5.4D-TabPerf: nothing still being written may bring a file back after the session's files went (the review: none is made after
        // ForgetSession, and none still to come is made at all), and a tab kept for the next start has its file (its write under way waited
        // for to its end; written now, here, when it failed or never ran).
        FinishTabWrites();
        if (AppSettings.Current.PromptToSaveOnExit)
        {
            CancelTabWrites(_ => false);
            _tabs.ForgetSession();
            return;
        }

        WaitForKeptTabWrites();
        Func<ProjectTabHeader, bool> keep = t => KeptInFile(t) && t.Dirty && !t.Empty;
        WriteKeptTabsNow(keep);
        CancelTabWrites(keep);
        _tabs.ForgetSession(keep);
    }

    /// <summary>
    /// After the first frame, at idle: the + tooltip with its keys, and the tabs other sessions left behind (those whose HandPeg is gone)
    /// taken into this session's folder and offered as tabs (cold, unopened: one comes back when clicked), behind this session's own in
    /// the most recently used.
    /// </summary>
    private async void RecoverProjectTabs()
    {
        try
        {
            // (The right-click menu reads its keys as it opens: KeybindManager.WithGesture in ProjectTabStrip.MenuFor.)
            ProjectTabStrip.SetGestures(KeybindManager.WithGesture("New Project Tab", "tab.new"));
            KeybindManager.Changed += () => ProjectTabStrip.SetGestures(KeybindManager.WithGesture("New Project Tab", "tab.new"));

            // Closing HandPeg waits while they are moved in and put on the strip (RefuseCloseMidSwitch): ForgetSession deletes only the
            // files of the tabs it knows, so none moved in is lost either way, but one not on the strip yet would not be asked about.
            var known = _tabs.Tabs.Select(t => t.Id).ToList();
            var adopted = 0;
            _recoveringTabs = true;
            try
            {
                var left = await Task.Run(() => _tabs.TakeLeftBehind(known));
                foreach (var tab in left)
                {
                    if (_tabs.Adopt(tab))
                        adopted++;
                }
            }
            finally
            {
                _recoveringTabs = false;
            }

            if (adopted == 0)
                return;

            _tabs.Touch(_tabs.Active);
            ShowTabs();
            WriteTabManifest();
            AppLog.Write($"Project tabs: {adopted} left in the cache by a session that ended were offered again.");
            _viewModel.StatusText = adopted == 1
                ? "A project tab left open when HandPeg last closed is back in the title bar: click it to open it."
                : $"{adopted} project tabs left open when HandPeg last closed are back in the title bar: click one to open it.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the tabs left by the last session could not be offered", ex);
        }
    }

    /// <summary>tabs.json written anew (the order and the tab in front), in this session's folder, away from this thread.</summary>
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
