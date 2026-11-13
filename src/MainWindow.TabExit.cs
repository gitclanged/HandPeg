using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Closing HandPeg with project tabs (Pass 5.5A) -----
// What closing does with the open project tabs is one plan, chosen as the close begins (MayCloseProjectTabs, from the settings):
//   SuspendAll (Keep open project tabs suspended between sessions, off at first since the 5.5A addenda): nothing is asked; every tab holding
//     work, the tab in front included, is kept in the session cache exactly as it is (its undo and redo history with it, never lagged),
//     tabs.json with their order, and the next start brings them back (RecoverProjectTabs: the one in front last time in front again).
//   KeepDirtyBehind (that setting off, Prompt to save before closing off): as before, nothing is asked, the tabs behind with unsaved changes
//     are kept and the tab in front's are not.
//   Forget (both settings as HandPeg comes: the 5.5A addenda, the user's call that closing asks): with two tabs or more holding work, one
//     dialog lists every one of them (SaveTabsOnExitDialog: the unsaved ones checked to be saved, the saved ones shown as saved): Save
//     Checked & Close, Keep Suspended for Next Launch (SuspendAll after all) or Discard & Close; Cancel stays. One tab holding work alone:
//     with work not saved as a project (changes, or never saved at all) the classic question when it is in front (MayClose,
//     SaveOnExitDialog), the dialog with its one row when it is behind; saved, nothing is asked.
// Once the tabs have answered for the tab in front too (ExitAnsweredByTabs), MayClose asks nothing more. ForgetProjectTabs carries the plan
// out as the window closes.
public partial class MainWindow
{
    /// <summary>What closing HandPeg does with the project tabs' cache files (Pass 5.5A; see the section above).</summary>
    internal enum TabExitPlan
    {
        /// <summary>Every tab's files go: nothing is offered back at the next start.</summary>
        Forget,

        /// <summary>The tabs behind with unsaved changes keep their files (the prompt off, the tabs not kept): offered back at the next start.</summary>
        KeepDirtyBehind,

        /// <summary>Every tab holding work keeps its file, the tab in front's written exactly as it is, and tabs.json their order.</summary>
        SuspendAll,
    }

    /// <summary>What closing HandPeg asks about the project tabs (Pass 5.5A addenda; see the section above).</summary>
    internal enum ExitQuestion
    {
        /// <summary>Nothing: closing goes on (or the tabs are kept, or the prompt is off).</summary>
        None,

        /// <summary>The one dialog listing every tab holding work (SaveTabsOnExitDialog).</summary>
        Tabs,

        /// <summary>The classic question for one project alone, in front, with work not saved (MayClose, SaveOnExitDialog).</summary>
        Classic,
    }

    // The plan the close under way carries out (MayCloseProjectTabs sets it, ForgetProjectTabs follows it), and what it asks.
    private TabExitPlan _tabExitPlan;
    private ExitQuestion _exitQuestion;

    /// <summary>Whether the close's answer covered the tab in front as well (Pass 5.5A): MayClose then asks nothing more.</summary>
    private bool ExitAnsweredByTabs { get; set; }

    /// <summary>
    /// The plan for closing HandPeg (pure: tabs-exit-plan), and what it asks: the tabs kept when <paramref name="keepOpen"/>
    /// (AppSettings.KeepOpenTabsOnClose), nothing asked; else today's way with no <paramref name="prompt"/> (AppSettings.PromptToSaveOnExit);
    /// else forgotten, and asked about only when closing would drop work not saved (<paramref name="unsavedTabs"/>: <see cref="UnsavedForExit"/>;
    /// 5.5.0, the user's Q2 (b): every tab saved closes with no question): with two tabs or more holding work (<paramref name="heldTabs"/>)
    /// the one dialog listing them all (the saved shown as saved); one alone the classic question when it is the project in front
    /// (<paramref name="unsavedInFront"/>), else the dialog with its one row.
    /// </summary>
    internal static (TabExitPlan Plan, ExitQuestion Ask) ExitPlanFor(bool keepOpen, bool prompt, int heldTabs, int unsavedTabs, bool unsavedInFront) =>
        keepOpen ? (TabExitPlan.SuspendAll, ExitQuestion.None)
        : !prompt ? (TabExitPlan.KeepDirtyBehind, ExitQuestion.None)
        : unsavedTabs == 0 ? (TabExitPlan.Forget, ExitQuestion.None)
        : heldTabs >= 2 ? (TabExitPlan.Forget, ExitQuestion.Tabs)
        : (TabExitPlan.Forget, unsavedInFront ? ExitQuestion.Classic : ExitQuestion.Tabs);

    /// <summary>
    /// Whether a tab holds work closing would lose unasked (pure, the 5.5A addenda): it holds something (not <paramref name="empty"/>) and
    /// either has changes not saved (<paramref name="dirty"/>) or was never saved as a project at all (no <paramref name="projectPath"/>:
    /// a video opened and left as it was is work too, though nothing changed since it was opened).
    /// </summary>
    internal static bool UnsavedForExit(bool dirty, bool empty, string projectPath) => !empty && (dirty || projectPath.Length == 0);

    /// <summary><see cref="UnsavedForExit(bool, bool, string)"/> for a tab: the tab in front's file is the view model's (its header follows it).</summary>
    private bool UnsavedForExit(ProjectTabHeader tab) =>
        UnsavedForExit(tab.Dirty, tab.Empty, KeptInFile(tab) ? tab.ProjectPath : _viewModel.CurrentProjectPath);

    /// <summary>
    /// The tab in front kept for the next start as HandPeg closes (SuspendAll): written exactly as it is (<see cref="TrySuspendActiveTab"/>).
    /// When it could not be and it has unsaved changes, closing anyway is the user's call (staying is the default). Whether to close.
    /// </summary>
    private bool SuspendFrontForExit()
    {
        if (TrySuspendActiveTab(out var why))
            return true;

        var front = _tabs.Active;
        if (!front.Dirty)
            return true;

        var close = new ConfirmDialog("Project Tab Not Kept",
            $"\"{front.Title}\" could not be kept for the next start ({why}).\n\nClose HandPeg anyway? Its changes that were not saved are lost.", "Close Without Keeping It")
        {
            Owner = this, DefaultsToCancel = true, CancelText = "Keep HandPeg Open",
        }.ShowDialog() == true;

        // Staying: the tab in front keeps no cache file while it is in front (one half written goes, after any write of it).
        if (!close)
            DeleteTabFiles(front.Id);
        return close;
    }

    /// <summary>
    /// The tab in front written to its cache file exactly as it is (Pass 5.5A, SuspendAll; the window's thread, waited for): its project
    /// with its undo and redo history (never lagged), its saved state's fingerprint when it has unsaved changes, its header with its window,
    /// view and exact dot. On the tab's own queue, so it lands after its crash-recovery file's write, never beside it. A tab in front that
    /// could not be brought back is its file already (kept as it is), and one holding nothing keeps none. False, and why, when it failed.
    /// </summary>
    private bool TrySuspendActiveTab(out string? why)
    {
        why = null;
        var tab = _tabs.Active;
        if (_unresumed.Contains(tab.Id))
            return true;
        if (_viewModel.HoldsNothingForTab)
        {
            (tab.Empty, tab.Dirty) = (true, false);
            return true;
        }

        try
        {
            // Its window first, as a tab put behind takes it (PutActiveBehind), then playback stopped.
            _viewModel.StatusText = "Keeping tabs for the next start...";
            CaptureWindowStateForTab(tab);
            StopPlayingForTabs();
            TakeHeaderForTab(tab);
            tab.Dirty = _viewModel.HasUnsavedChanges;
            var (id, state, saved, header) = (tab.Id, _viewModel.CaptureStateForTab(tab.Title), tab.Dirty ? _viewModel.SavedFingerprintForTab : null, ProjectTabs.HeaderJson(tab));
            _tabs.Enqueue(id, () => _tabs.WriteTab(id, state, saved, header)).Wait();
            AppLog.Write($"Project tabs: the tab in front was kept for the next start ({(tab.Dirty ? "with unsaved changes" : "saved")}).");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
            AppLog.Write("Project tabs: the tab in front could not be kept for the next start", cause);
            why = cause.Message;
            return false;
        }
    }

    /// <summary>
    /// Every tab holding work asked about in one dialog as HandPeg closes (Pass 5.5A, the Forget plan; the 5.5A addenda: every one of them,
    /// <paramref name="held"/>, the saved ones shown as saved; the tab in front's dot made exact by MayCloseProjectTabs). Save Checked &amp;
    /// Close saves the checked tabs (the unchecked ones are let go; a save that fails asks whether to close anyway, staying by default), Keep
    /// Suspended for Next Launch keeps every tab (SuspendAll), Discard &amp; Close lets them all go. False when the user chose to stay
    /// (Cancel, Esc, the title bar's ×).
    /// </summary>
    private bool AskAboutTabsOnExit(IReadOnlyList<ProjectTabHeader> held)
    {
        var rows = RowsOf(held);
        AppLog.Write($"Closing with {held.Count} project tab(s) holding work ({rows.Count(r => !r.Saved)} not saved): asked about in one dialog.");
        var dialog = new SaveTabsOnExitDialog(rows, SaveTabsOnExitDialog.Purpose.Exit) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case TabsExitChoice.SaveChecked:
                var failed = new List<string>();
                SaveCheckedTabs(rows, failed);
                if (failed.Count > 0 && !CloseAnyway(failed))
                    return false;
                _tabExitPlan = TabExitPlan.Forget;
                break;

            case TabsExitChoice.KeepSuspended:
                _tabExitPlan = TabExitPlan.SuspendAll;
                if (!SuspendFrontForExit())
                    return false;
                break;

            case TabsExitChoice.Discard:
                _tabExitPlan = TabExitPlan.Forget;
                break;

            default:
                return false;
        }

        ExitAnsweredByTabs = true;
        return true;
    }

    /// <summary>
    /// The checked rows' tabs saved (Pass 5.5A): the tab in front as Ctrl+S saves it (its project file, else a first save under the row's
    /// name), a tab behind from its cache file without bringing it back (<see cref="SaveTabInPlace"/>). What failed goes into <paramref name="failed"/>.
    /// </summary>
    private void SaveCheckedTabs(IReadOnlyList<TabExitRow> rows, List<string> failed)
    {
        foreach (var row in rows.Where(r => r.IsChecked && !r.Saved))
        {
            if (_tabs.Find(row.Id) is not { } tab)
                continue;

            var name = row.Name.Trim().Length > 0 ? row.Name.Trim() : tab.Title;
            var inFront = !KeptInFile(tab);
            var saved = !inFront ? SaveTabInPlace(tab, name)
                : _viewModel.CurrentProjectPath.Length > 0 ? _viewModel.SaveProjectTo(_viewModel.CurrentProjectPath)
                : _viewModel.SaveProject(name);
            if (!saved.StartsWith("Project saved", StringComparison.Ordinal))
                failed.Add($"\"{tab.Title}\": {saved}");
            else if (inFront)
                tab.Dirty = false;
        }
    }

    /// <summary>Whether to close HandPeg although some tabs could not be saved (never by default: Keep HandPeg Open).</summary>
    private bool CloseAnyway(IReadOnlyList<string> failed) =>
        new ConfirmDialog("Projects Not Saved", $"{string.Join("\n", failed)}\n\nClose HandPeg anyway? The changes that were not saved are lost.", "Close Without Saving")
        {
            Owner = this, DefaultsToCancel = true, CancelText = "Keep HandPeg Open",
        }.ShowDialog() == true;

    /// <summary>
    /// The dialog's rows (Pass 5.5A): each its name, its project file (the tab in front's is the view model's) or "Never saved"
    /// (<see cref="RowDetail"/>), and for one never saved a name to save it under: its own, or a dated one for a tab still called New
    /// Project or Untitled Project, each name once in the list. The 5.5A addenda: a tab with nothing unsaved (<see cref="UnsavedForExit(ProjectTabHeader)"/>)
    /// is a row shown as saved, with nothing to tick.
    /// </summary>
    private List<TabExitRow> RowsOf(IEnumerable<ProjectTabHeader> tabs)
    {
        var rows = new List<TabExitRow>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in tabs)
        {
            var path = KeptInFile(tab) ? tab.ProjectPath : _viewModel.CurrentProjectPath;
            if (!UnsavedForExit(tab))
            {
                rows.Add(new TabExitRow(tab.Id, tab.Title, RowDetail(path, tab.StepsBack), false, "", saved: true));
                continue;
            }

            var name = tab.Title is { Length: > 0 } title and not ("New Project" or "Untitled Project" or ProjectTabs.RecoveredTitle)
                ? title
                : $"Project {DateTime.Now:yyyy-MM-dd HH.mm}";
            if (path.Length == 0)
            {
                var unique = name;
                for (var n = 2; !names.Add(unique); n++)
                    unique = $"{name} ({n})";
                name = unique;
            }

            rows.Add(new TabExitRow(tab.Id, tab.Title, RowDetail(path, tab.StepsBack), path.Length == 0, name));
        }

        return rows;
    }

    /// <summary>
    /// A row's detail (pure, Pass 5.5A review): its project file or "Never saved"; for a tab whose file is a crash-recovery copy written actions
    /// back (ProjectTabHeader.StepsBack), first that it was recovered so far back (saved or kept, it opens there with the last edits on Redo),
    /// so a long path never hides it.
    /// </summary>
    internal static string RowDetail(string path, int stepsBack) =>
        (stepsBack > 0 ? $"Recovered {stepsBack} action{(stepsBack == 1 ? "" : "s")} back · " : "") + (path.Length > 0 ? path : "Never saved");

    // ----- The tabs a session left, brought back with the one in front last time in front (Pass 5.5A addenda, AD-1) -----
    // The user: opening HandPeg with tabs left open must not also open a new blank tab. RecoverProjectTabs (at idle after the first frame:
    // the first frame is still the blank Timeline with the strip collapsed) takes the tabs in; when the start-up tab is still untouched,
    // the tab that was in front last time comes to the front through the switch's own steps (the blank tab put behind, the tab resumed
    // from its cache file) and the blank tab goes (it holds nothing: nothing is asked). One step of a switch is left out: the tab's own
    // window layout (Law 1: the first frame's single dock build is the only one until the user changes tabs), so the tab takes the window
    // as it is, as a tab opened does. Its resume failing keeps the blank tab in front, as before, and says so. Pass 5.5A final review: only the
    // tabs of a session that closed keeping them on purpose (ProjectTabs.WriteKeptManifest); a crash's stay behind the start-up tab, unopened.

    // Pass 5.5A final review: RecoverProjectTabs came while a close was asking, and left the tabs a dead session left where they are; it runs
    // again once that close is called off (MainWindow_Closing). Its keys for the strip's menu are set once (the first run).
    private bool _leftTabsPutOff, _tabGesturesSet;

    /// <summary>
    /// A close called off (MainWindow_Closing, once its <c>_closeAsked</c> is let go): the tabs a dead session left, put off while it was asking
    /// (<see cref="RecoverProjectTabs"/>), taken in now, at idle as at start. Pass 5.5A fix round 3 (S-R3): a method of its own, so the goldens
    /// drive it.
    /// </summary>
    private void TakeInLeftTabsPutOff()
    {
        if (!_leftTabsPutOff)
            return;

        _leftTabsPutOff = false;
        _ = Dispatcher.BeginInvoke(RecoverProjectTabs, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Whether the tabs a session left bring the one in front last time to the front (pure: project-tabs-restored-front): the start-up tab
    /// is the only tab but them and in front (<paramref name="startTabAlone"/>), still blank (it holds nothing and has no project file),
    /// not shown on purpose by Ctrl+T or +, with nothing under way in it (a switch, an open, an import, a drop, a recording, a drag, the
    /// close), and HandPeg was not started with a project to open. Otherwise the tabs stay behind it, as before.
    /// </summary>
    internal static bool BringsRestoredForward(bool startTabAlone, bool holdsNothing, string projectPath, bool revealed, bool underWay, bool launchedWithProject) =>
        startTabAlone && holdsNothing && projectPath.Length == 0 && !revealed && !underWay && !launchedWithProject;

    /// <summary>
    /// The tab the last session had in front, to bring forward now (<see cref="BringsRestoredForward"/>); null to leave the tabs behind the
    /// start-up tab: <paramref name="leftFront"/> (ProjectTabs.TakeLeftBehind's) when it was adopted (<paramref name="adopted"/>), and
    /// <paramref name="known"/> the tabs there were before them.
    /// </summary>
    private ProjectTabHeader? RestoredFrontToBring(string? leftFront, IReadOnlyList<string> adopted, IReadOnlyList<string> known)
    {
        if (leftFront is null || !adopted.Contains(leftFront, StringComparer.Ordinal))
            return null;

        var startTabAlone = known.Count == 1 && _tabs.Count == adopted.Count + 1 && string.Equals(_tabs.Active.Id, known[0], StringComparison.Ordinal);
        var underWay = _switchingTab || _closeAsked || _openingName is not null || _viewModel.IsBusy || _viewModel.HasPendingAdds || _viewModel.IsRecording
            || TimelineDragHeld;
        return BringsRestoredForward(startTabAlone, _viewModel.HoldsNothingForTab, _viewModel.CurrentProjectPath, _tabsRevealed, underWay,
            SingleInstance.LaunchFile() is not null)
            ? _tabs.Find(leftFront)
            : null;
    }

    /// <summary>
    /// The tabs' side of bringing <paramref name="front"/> forward (the goldens drive it on a ProjectTabs of their own): it is put in front
    /// and the blank start-up tab taken off the strip. Where that tab was, to put a blank tab back there (<see cref="PutStartTabBack"/>).
    /// </summary>
    internal static int SwapStartTabFor(ProjectTabs tabs, ProjectTabHeader blank, ProjectTabHeader front)
    {
        var at = tabs.IndexOf(blank);
        tabs.Activate(front);
        tabs.Close(blank);
        return at;
    }

    /// <summary>A blank tab put back in front where the start-up tab was (<see cref="SwapStartTabFor"/>), the tab brought forward behind it again.</summary>
    internal static ProjectTabHeader PutStartTabBack(ProjectTabs tabs, int at)
    {
        var blank = tabs.Insert(at, "New Project");
        tabs.Activate(blank);
        return blank;
    }

    /// <summary>
    /// The tab the last session had in front brought to the front at start (AD-1), as a switch brings a tab only in its file (its window
    /// layout left as it is: see the section above): the blank start-up tab put behind and taken off the strip, the strip shown with the
    /// tab in front (<paramref name="shown"/>: RecoverProjectTabs shows the strip and slides the others out from behind it, at once, while
    /// it is read), then the tab resumed from its cache file. True when it came back; false with a blank tab in front again (it could not
    /// be read or opened: the tab stays behind with its files, to try again with a click, and the status line says why).
    /// </summary>
    private async Task<bool> BringRestoredTabForwardAsync(ProjectTabHeader front, Action shown)
    {
        var blank = _tabs.Active;
        var at = -1;
        var resumed = false;
        _switchingTab = true;
        try
        {
            // The blank tab put behind as a switch puts it (it keeps nothing), then gone from the strip.
            PutActiveBehind(out var write);
            QueueTabWrite(write);
            _unresumed.Remove(blank.Id);
            ForgetKeptTab(blank.Id);
            at = SwapStartTabFor(_tabs, blank, front);
            shown();

            resumed = await ResumeTabAsync(front, null);
            if (resumed)
            {
                RefreshActiveTab();
                KeepLayoutNotTaken(front);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Project tabs: the tab in front when HandPeg last closed could not be brought to the front", ex);
        }
        finally
        {
            // Not brought back: a blank tab in front where the start-up tab was, as HandPeg starts without this (the tab keeps its files).
            if (!resumed && at >= 0 && _tabs.Tabs.Contains(front))
            {
                PutStartTabBack(_tabs, at);
                ShowTabs();
                AppLog.Write("Project tabs: the tab in front when HandPeg last closed could not be brought back; the blank start-up tab stays in front.");
            }

            _switchingTab = false;
        }

        return resumed;
    }

    // ----- The restored tab's own layout, kept until it is laid out (Pass 5.5A final review) -----
    // AD-1 brings the tab forward without laying its layout out (Law 1), so the window keeps the start-up layout: that must never be taken for
    // the tab's own as it goes behind (CaptureWindowStateForTab), or the arrangement it was kept with would be lost. Until its layout is laid
    // out (a switch back to it: ApplyTabLayout), the user changes the window's by hand (the Layout Selector, a pane moved or a border dragged:
    // DockManager.Changed, which a layout laid out for a tab never raises), or it closes, its header keeps its own.

    // The tab brought forward without its layout (null for none).
    private string? _layoutNotTaken;
    private bool _layoutWatchHooked;

    /// <summary>Whether a tab's header keeps the layout it was kept with as it goes behind (Pass 5.5A final review, see above).</summary>
    private bool LayoutNotTaken(ProjectTabHeader tab) => string.Equals(_layoutNotTaken, tab.Id, StringComparison.Ordinal);

    /// <summary>The tab brought forward (AD-1) keeps its own layout, when it has one, until it is laid out (see above); the dock watched from now on.</summary>
    private void KeepLayoutNotTaken(ProjectTabHeader tab)
    {
        if (!AppSettings.Current.PerTabWindowLayouts || tab.LayoutJson is not { Length: > 0 })
            return;

        _layoutNotTaken = tab.Id;
        if (_layoutWatchHooked)
            return;
        _layoutWatchHooked = true;
        _dock.Changed += () => _layoutNotTaken = null;
    }

    /// <summary>A tab's layout laid out, or the tab closed: nothing kept for it any more (see above).</summary>
    private void ForgetLayoutNotTaken(string id)
    {
        if (string.Equals(_layoutNotTaken, id, StringComparison.Ordinal))
            _layoutNotTaken = null;
    }

    // The end of what ResumeTabAsync says of a tab opened from its project file, changed on the disk since the tab was kept (5.5A review).
    private const string ChangedOnDiskTail = ": opened from its project file, which changed on the disk since its tab was kept.";

    /// <summary>What the status line says of a tab brought back from its project file, which changed on the disk since its tab was kept.</summary>
    internal static string ChangedOnDiskLine(string title) => title + ChangedOnDiskTail;

    // The end of what ResumeTabAsync says of a tab whose project file changed on the disk but could not be read (Pass 5.5A final review, C-4b).
    private const string UnreadableOnDiskTail = "; opened from the copy kept with its tab, unsaved and tied to no file: Save asks for a name, and that file is left as it is.";

    /// <summary>
    /// What the status line says of a tab brought back from its cache because its project file changed on the disk but could not be read (pure,
    /// Pass 5.5A final review): the project, and why (<paramref name="why"/>: a newer format named as such, or what the read said).
    /// </summary>
    internal static string UnreadableOnDiskLine(string title, string why) =>
        $"{title}: its project file changed on the disk but could not be read{(why.Length > 0 ? $" ({why})" : "")}" + UnreadableOnDiskTail;

    /// <summary>
    /// Whether the status line is a word of ResumeTabAsync's own about the tab it brought back (AD-1 keeps it after its own line): opened
    /// <paramref name="stepsBack"/> actions back from a crash-recovery copy, or from its project file changed on the disk (or, Pass 5.5A final
    /// review, from its cache because that file could not be read); not the open's own.
    /// </summary>
    private static bool ResumeSaidOfItsOwn(string said, int stepsBack) =>
        (stepsBack > 0 && string.Equals(said, StepsBackLine(stepsBack), StringComparison.Ordinal)) || said.EndsWith(ChangedOnDiskTail, StringComparison.Ordinal)
        || said.EndsWith(UnreadableOnDiskTail, StringComparison.Ordinal);

    /// <summary>
    /// What the status line says once the tabs a session left came back (pure, AD-1): with the one in front last time brought forward
    /// (<paramref name="front"/>), which project is open and how many came back; else (as before) that they are back in the title bar.
    /// </summary>
    internal static string RestoredLine(string? front, int count) =>
        front is not null
            ? count == 1
                ? $"{front} is open again: the project tab left open when HandPeg last closed came back."
                : $"{front} is open again: {count} project tabs left open when HandPeg last closed came back."
            : count == 1
                ? "A project tab left open when HandPeg last closed is back in the title bar: click it to open it."
                : $"{count} project tabs left open when HandPeg last closed are back in the title bar: click one to open it.";
}
