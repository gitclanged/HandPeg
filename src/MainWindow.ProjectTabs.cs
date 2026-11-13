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
        ProjectTabStrip.HoverScrubbed += ScrubTabCard;

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
            TakeProjectPath(tab, _viewModel.CurrentProjectPath);
            tab.Empty = _viewModel.HoldsNothingForTab;
            tab.Dirty = _viewModel.MayHaveUnsavedChanges && (fingerprint ? _viewModel.HasUnsavedChanges : tab.Dirty);

            // Pass 5.5A: saved, or undone back to the save: its crash-recovery file goes at once (MainWindow.TabCache.cs).
            if (!tab.Dirty)
                DropFrontRecovery(tab);
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

        // The tab Ctrl+Tab brings next made ready now (5.4.2A): its playhead's frames decoded ahead.
        PrewarmLikelyTabs(null);
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
        _explodedLastPose = null; // the next tab's own, from its header (RestoreTabView); a new one starts at the camera's start
        StopPlayingForTabs();
        if (_viewModel.HoldsNothingForTab)
        {
            // Nothing worth keeping (a fresh tab, or one whose file could not be brought back: that keeps its file and tries again later).
            // Pass 5.5A: a crash-recovery file of what it held before (emptied in front) goes with it.
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

        // Pass 5.5A final review: a tab brought forward at start without its own layout (AD-1, Law 1) keeps the one it was kept with until that
        // is laid out or the user changes the window's by hand (LayoutNotTaken): the start-up layout is never taken for its own.
        var perTab = AppSettings.Current.PerTabWindowLayouts;
        if (!LayoutNotTaken(tab))
        {
            tab.LayoutJson = TabLayoutOf(_dock, perTab);
            tab.MediaView = TabMediaViewOf(MediaBrowser.ViewMode, perTab);
        }

        TakeExplodedState(tab, _explodedUi && _gpu is { } engine ? engine.TargetPose : null, _explodedWaitingTab, _explodedLastPose);
    }

    /// <summary>
    /// The 3D view's part of a tab's header (5.4D, window-free: the goldens' project-tabs-exploded-pose): in it at <paramref name="shown"/>
    /// (the engine's target pose) while the view is up; else, while a tab brought back in it still waits for its player or the engine
    /// (<paramref name="waiting"/>), in it at the camera that tab came back with (its own header kept as it is: a tab put behind again
    /// before its view was restored keeps it, 5.4D review); else flat, with the camera it last had in 3D (<paramref name="remembered"/>,
    /// 5.4.1-M1) kept for the next time the view is turned on in it.
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
            ForgetLayoutNotTaken(tab.Id);
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
        (tab.Title, tab.Empty, tab.Dirty) = (ActiveTabTitle(), false, TabDirtyNow(tab));
        TakeProjectPath(tab, _viewModel.CurrentProjectPath);
        (tab.NewProjectName, tab.NewProjectFolder, tab.WaitingTemplate) = (_viewModel.NewProjectName, _viewModel.NewProjectFolder, _viewModel.TemplateWaitingForTab);
        (tab.PlayheadMs, tab.ViewStart, tab.ViewSpan, tab.ViewFit, tab.VerticalOffset) =
            (_viewModel.PositionMs, _viewStart, _viewSpan, _viewFit, UnifiedTimeline.Surface.VerticalOffset);
        TakeFrameForTab(tab);

        // Pass 5.5A review: written from the view model, the tab's file is the project exactly as it is (a lagged crash-recovery copy says otherwise itself).
        tab.StepsBack = 0;
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
        var read = default(TabResume);
        try
        {
            // Pass 5.5A review: a tab kept with nothing unsaved whose project file changed on the disk since (another PC, a backup, another
            // HandPeg) opens from that file instead (ProjectTabs.ReadForResume, on its queue: the disk is never looked at on this thread).
            // Unsaved work wins: a tab with unsaved changes is read from its cache as before.
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

        // Opened as a project is: a new Sequence put in place when it worked. One that did not (its file not found) leaves the last
        // tab's Sequence where it was, which must never pass for this tab's. Pass 5.5A final review (C-4b): a cache whose project file changed
        // but could not be read is tied to no file (Save asks for a name: that file is never written over) and comes back unsaved (its work
        // is in no file now: closing asks, and its cache file stays its crash-recovery copy).
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

        // The tab is the view model's again: its files go (written anew when it is next put behind), and its waiting Game Preset is the
        // view model's. After its own disk work and before any asked later (5.4D-TabPerf): a delete that came after the next put-behind's
        // write would lose the tab. Pass 5.5A final review (AD-1): one with unsaved changes keeps them as the tab in front's crash-recovery
        // copy (its work as it came back: the 20 s tick writes it anew once the history moves, and lets it go once the tab is clean); deleted,
        // that work would be in memory alone until the tick, and a crash on the same project would lose it for good.
        _unresumed.Remove(id);
        tab.WaitingTemplate = null;
        if (unreadable)
            (tab.ProjectPath, tab.Dirty) = ("", true);
        if (tab.Dirty)
            _frontRecovery = new FrontLook(id, _viewModel.HistoryPosition, Written: true);
        else
            DeleteTabFiles(id);

        // Pass 5.5A final review (C-4a): the stamp of its project file as the project and that file were last known to be the same: as it was
        // read from it now (none for one tied to no file), else (read from the cache) the one its header carried. Pass 5.5A fix round 3 (S-R1):
        // a stamp still on its way (asked before the tab went behind) never lands: the one taken here, or carried, is of the project as it came back.
        tab.StampsAsked++;
        if (read.From != TabResumeSource.Cache)
            (tab.ProjectFileTicks, tab.ProjectFileLength) = (read.FileTicks, read.FileLength);

        // Pass 5.5A review: said once, as it comes back: opened from its changed file, or from a crash-recovery copy written actions back.
        if (read.From == TabResumeSource.ChangedFile)
            _viewModel.StatusText = ChangedOnDiskLine(tab.Title);
        else if (unreadable)
            _viewModel.StatusText = UnreadableOnDiskLine(tab.Title, read.Why ?? "");
        else if (tab.StepsBack > 0)
            _viewModel.StatusText = StepsBackLine(tab.StepsBack);
        tab.StepsBack = 0;

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
        // Its last 3D camera (5.4.1-M1), for the view turned on by hand: the header's (the camera's start for a tab never in 3D).
        if (ReferenceEquals(tab, _tabs.Active))
            _explodedLastPose = new(tab.ExplodedYaw, tab.ExplodedPitch, tab.ExplodedZoom, tab.ExplodedSpacing);

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

        // Its own playhead put back is no move of the user's: the still stays over it (FollowPlayheadInPlayer).
        _restoringTabView = true;
        try
        {
            _viewModel.PositionMs = at;

            // In the 3D view if it was left in it (5.4D), before the player opens: its first frame is the 3D one its still shows.
            RestoreExplodedForTab(tab);
            OpenInPlayer((long)at, paused: true);
        }
        finally
        {
            _restoringTabView = false;
        }

        WatchForFirstFrame();
    }

    // A tab's view being put back (RestoreTabView): its playhead's move keeps the still.
    private bool _restoringTabView;

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
            // Pass 5.5A fix round 3 (S-R1): the file's stamp taken before the open reads it, away from this thread (StampOpenedProject carries it
            // once the open worked); first, so every look below is made as things are after it.
            var stamp = await Task.Run(() => ProjectTabs.FileStamp(path));

            // Pass 5.5A review: nothing marks the open as under way during that wait (a file on a sleeping share can take seconds), so a close
            // can start in it; one asking (its prompt's loop runs this continuation) or under way is never opened into.
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

            // Nothing in front worth keeping: opened there, as before (queued behind a running encode, as LoadProjectAsync does).
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

                var asked = ++made.StampsAsked;
                await _viewModel.LoadProjectAsync(path);
                StampOpenedProject(made, "", path, stamp, asked);
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
    /// × on a tab, a middle click on it, Ctrl+W (null: the tab in front): Save / Discard / Cancel only when it holds work not saved (5.5.0:
    /// <see cref="UnsavedForExit(ProjectTabHeader)"/>, as closing HandPeg asks: unsaved changes, or content never saved as a project; a tab
    /// behind with a project file is saved from its cache file, never brought back for it; one never saved comes to the front for its first
    /// save), then its files deleted. The tab after it (or before, at the end) comes to the front; closing the last tab leaves one empty tab.
    /// With <paramref name="asked"/> (Pass 5.5A: Close Other Tabs asked about every unsaved tab in one dialog), nothing is asked again.
    /// </summary>
    private async Task CloseProjectTabAsync(ProjectTabHeader? which, bool asked = false)
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
        // 5.5.0 (the user's Q3 b): asked by the same rule as closing HandPeg: changes not saved, or work never saved as a project at all.
        if (!asked && UnsavedForExit(tab) && !await AskToSaveTabAsync(tab))
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
            _ = StampProjectFileAsync(tab, path);
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
    /// As HandPeg closes (MayClose, before the tab in front is asked about), Pass 5.5A (MainWindow.TabExit.cs): the plan for the tabs, from the
    /// settings (<see cref="ExitPlanFor"/>). Kept open between sessions: the tab in front written exactly as it is (every other tab has its
    /// file), nothing asked. The prompt to save off: nothing asked, the tabs behind with unsaved changes kept (as before). Otherwise (the
    /// 5.5A addenda, 5.5.0 Q2 b: asked only when closing would drop work not saved), with two tabs or more holding work, every one of them in
    /// one dialog (<see cref="AskAboutTabsOnExit"/>); one alone with work not saved as a project, MayClose's classic question when it is in front
    /// (the dialog with its one row when it is behind); every tab saved, nothing. False when the user chose to stay.
    /// </summary>
    private bool MayCloseProjectTabs()
    {
        SplitFromUnresumed();

        // 5.4D-TabPerf: the cache files still being written after a switch first (each tab's dot is then its exact one).
        FinishTabWrites();
        ExitAnsweredByTabs = false;
        var settings = AppSettings.Current;

        // The tab in front's dot made exact whenever the plan may ask (the 5.5A addenda: one project alone too), once: MayClose's classic
        // question then takes it as it is (a fingerprint never made twice).
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

            // One project alone, in front, with work not saved: MayClose's classic question.
            case ExitQuestion.Classic:
                return true;

            // Nothing holding work that is not saved (one saved project, or none): nothing to ask.
            default:
                ExitAnsweredByTabs = true;
                return true;
        }
    }

    /// <summary>
    /// The close's plan carried out (Pass 5.5A; MainWindow.TabExit.cs): forgotten, this session's tab files go, so nothing is offered back at
    /// the next start; with the prompt to save on exit off (and the tabs not kept), the files of the tabs behind with unsaved changes stay (as
    /// before); kept between sessions, every tab holding work keeps its file (the tab in front's written as the close began) and tabs.json
    /// their order. The next start offers what stays. Never another HandPeg's.
    /// </summary>
    private void ForgetProjectTabs()
    {
        // 5.4D-TabPerf: nothing still being written may bring a file back after the session's files went (the review: none is made after
        // ForgetSession, and none still to come is made at all), and a tab kept for the next start has its file (its write under way waited
        // for to its end; written now, here, when it failed or never ran).
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

        // Kept between sessions: their order and the tab in front as well, so the next start lists them as they were (Pass 5.5A final review:
        // marked kept on purpose, so it brings the tab in front forward; a crash's leftovers stay behind the start-up tab).
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
    /// After the first frame, at idle: the + tooltip with its keys, and the tabs other sessions left behind (those whose HandPeg is gone)
    /// taken into this session's folder and offered as tabs (cold, unopened: one comes back when clicked), behind this session's own in
    /// the most recently used. Pass 5.5A addenda (AD-1, MainWindow.TabExit.cs): with the start-up tab still blank and untouched, the tab
    /// that was in front last time comes to the front instead, and the blank tab goes.
    /// </summary>
    private async void RecoverProjectTabs()
    {
        try
        {
            // (The right-click menu reads its keys as it opens: KeybindManager.WithGesture in ProjectTabStrip.MenuFor.) Once, should this run again (below).
            if (!_tabGesturesSet)
            {
                _tabGesturesSet = true;
                ProjectTabStrip.SetGestures(KeybindManager.WithGesture("New Project Tab", "tab.new"));
                KeybindManager.Changed += () => ProjectTabStrip.SetGestures(KeybindManager.WithGesture("New Project Tab", "tab.new"));
            }

            // Pass 5.5A final review: never while a close is asking (this idle callback also runs in its dialog's loop): tabs taken in then
            // would not be on the list it asks about, and its Forget plan would delete them unasked. They stay where they are, taken in once
            // the close is called off (MainWindow_Closing runs this again), else at the next start. Nor beside a run under way.
            if (_closeAsked || _recoveringTabs)
            {
                _leftTabsPutOff |= _closeAsked;
                return;
            }

            // Closing HandPeg waits while they are moved in and put on the strip (RefuseCloseMidSwitch): ForgetSession deletes only the
            // files of the tabs it knows, so none moved in is lost either way, but one not on the strip yet would not be asked about.
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
                // A close asking by now (none can begin while they are moved in: a second guard): no tab for them, so ForgetSession keeps
                // their moved files (others' work) for the next start.
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

            // Pass 5.5A: they come out from behind the tab in front (ProjectTabStrip's slide, 180 ms; none with the extra animations off),
            // once: AD-1's tab brought forward is that tab, shown as soon as it is in front (while it is read).
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

            // Which project is open and how many came back (AD-1); a word the resume had of its own (opened actions back after a crash, or
            // from its project file changed on the disk) or why it could not come back, kept after it.
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
