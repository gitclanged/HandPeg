using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Application exit with project tabs -----
// MayCloseProjectTabs picks one TabExitPlan from settings when the close starts:
//   SuspendAll (KeepOpenTabsOnClose, default off): no prompt; every non-empty tab, active included, is written to the session cache
//     verbatim (full undo/redo history) with tabs.json order; RecoverProjectTabs restores them next start with the same active tab.
//   KeepDirtyBehind (both settings off): no prompt; dirty background tabs keep their cache files, the active tab's do not.
//   Forget (default: PromptToSaveOnExit on): two or more tabs holding work get one SaveTabsOnExitDialog (unsaved rows checked, saved rows
//     shown as saved) with Save Checked & Close, Keep Suspended for Next Launch (falls back to SuspendAll) or Discard & Close; Cancel aborts.
//     A single unsaved tab gets the classic SaveOnExitDialog when active (MayClose), else the one-row dialog; nothing asked if all saved.
// ExitAnsweredByTabs suppresses MayClose's own prompt once the dialog covered the active tab; ForgetProjectTabs applies the plan on close.
public partial class MainWindow
{
    /// <summary>Exit-time handling of project tab cache files (see the section header).</summary>
    internal enum TabExitPlan
    {
        /// <summary>Delete all tab files; nothing is restored next start.</summary>
        Forget,

        /// <summary>Dirty background tabs keep their files (prompt and keep-open both off) and are offered next start.</summary>
        KeepDirtyBehind,

        /// <summary>Every non-empty tab keeps its file (active tab written verbatim) plus tabs.json order.</summary>
        SuspendAll,
    }

    /// <summary>Exit-time prompt for project tabs (see the section header).</summary>
    internal enum ExitQuestion
    {
        /// <summary>No prompt (tabs kept, prompt disabled, or nothing unsaved).</summary>
        None,

        /// <summary>SaveTabsOnExitDialog listing every non-empty tab.</summary>
        Tabs,

        /// <summary>Single unsaved active project: classic SaveOnExitDialog via MayClose.</summary>
        Classic,
    }

    // Current close's plan and prompt (set by MayCloseProjectTabs, applied by ForgetProjectTabs).
    private TabExitPlan _tabExitPlan;
    private ExitQuestion _exitQuestion;

    /// <summary>True when the tab dialog already covered the active tab, so MayClose skips its prompt.</summary>
    private bool ExitAnsweredByTabs { get; set; }

    /// <summary>
    /// Pure exit decision (golden: tabs-exit-plan). <paramref name="keepOpen"/> (KeepOpenTabsOnClose) = SuspendAll, no prompt; no
    /// <paramref name="prompt"/> (PromptToSaveOnExit) = KeepDirtyBehind; else Forget, prompting only if <paramref name="unsavedTabs"/> > 0
    /// (<see cref="UnsavedForExit"/>): the tab dialog for <paramref name="heldTabs"/> >= 2, else Classic when <paramref name="unsavedInFront"/>.
    /// </summary>
    internal static (TabExitPlan Plan, ExitQuestion Ask) ExitPlanFor(bool keepOpen, bool prompt, int heldTabs, int unsavedTabs, bool unsavedInFront) =>
        keepOpen ? (TabExitPlan.SuspendAll, ExitQuestion.None)
        : !prompt ? (TabExitPlan.KeepDirtyBehind, ExitQuestion.None)
        : unsavedTabs == 0 ? (TabExitPlan.Forget, ExitQuestion.None)
        : heldTabs >= 2 ? (TabExitPlan.Forget, ExitQuestion.Tabs)
        : (TabExitPlan.Forget, unsavedInFront ? ExitQuestion.Classic : ExitQuestion.Tabs);

    /// <summary>
    /// Pure: a tab would lose work on exit if not <paramref name="empty"/> and either <paramref name="dirty"/> or never saved (empty
    /// <paramref name="projectPath"/>; an opened-but-unedited video still counts).
    /// </summary>
    internal static bool UnsavedForExit(bool dirty, bool empty, string projectPath) => !empty && (dirty || projectPath.Length == 0);

    /// <summary><see cref="UnsavedForExit(bool, bool, string)"/> for a tab; the active tab's path comes from the view model.</summary>
    private bool UnsavedForExit(ProjectTabHeader tab) =>
        UnsavedForExit(tab.Dirty, tab.Empty, KeptInFile(tab) ? tab.ProjectPath : _viewModel.CurrentProjectPath);

    /// <summary>
    /// SuspendAll: writes the active tab for next start (<see cref="TrySuspendActiveTab"/>). On failure with unsaved changes, asks whether to
    /// close anyway (default: stay). Returns whether to close.
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

        // Staying: the active tab must not keep a cache file; delete any partial write (queued after pending writes).
        if (!close)
            DeleteTabFiles(front.Id);
        return close;
    }

    /// <summary>
    /// Synchronously writes the active tab's cache file (UI thread): project with full undo/redo, saved fingerprint if dirty, header with
    /// window/view state and exact dirty dot. Runs on the tab's queue after any crash-recovery write. Unresumed tabs already are their file;
    /// empty tabs write none. Returns false with the reason on failure.
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
            // Capture window state first (as PutActiveBehind does), then stop playback.
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
    /// Forget plan: one dialog for all <paramref name="held"/> tabs (saved rows shown as saved; active dirty state refreshed by
    /// MayCloseProjectTabs). Save Checked &amp; Close saves checked tabs and discards the rest (on save failure asks, default stay); Keep
    /// Suspended switches to SuspendAll; Discard &amp; Close drops all. Returns false on Cancel, Esc or the title bar ×.
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
    /// Saves checked rows: the active tab like Ctrl+S (its project file, else a first save under the row name); background tabs from their
    /// cache files without resuming (<see cref="SaveTabInPlace"/>). Failures are appended to <paramref name="failed"/>.
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
            if (!saved.IsSaved)
                failed.Add($"\"{tab.Title}\": {saved.Line}");
            else if (inFront)
                tab.Dirty = false;
        }
    }

    /// <summary>Asks whether to close despite save failures (default: Keep HandPeg Open).</summary>
    private bool CloseAnyway(IReadOnlyList<string> failed) =>
        new ConfirmDialog("Projects Not Saved", $"{string.Join("\n", failed)}\n\nClose HandPeg anyway? The changes that were not saved are lost.", "Close Without Saving")
        {
            Owner = this, DefaultsToCancel = true, CancelText = "Keep HandPeg Open",
        }.ShowDialog() == true;

    /// <summary>
    /// Builds dialog rows: title, detail (<see cref="RowDetail"/>; active tab path from the view model), and for never-saved tabs a unique
    /// save name (its title, or a dated name for default/recovered titles). Tabs not <see cref="UnsavedForExit(ProjectTabHeader)"/> are
    /// read-only "saved" rows.
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
    /// Pure row detail: project path or "Never saved", prefixed with "Recovered N actions back" for a lagging crash-recovery copy
    /// (ProjectTabHeader.StepsBack; the latest edits are on Redo) so a long path never hides it.
    /// </summary>
    internal static string RowDetail(string path, int stepsBack) =>
        (stepsBack > 0 ? $"Recovered {stepsBack} action{(stepsBack == 1 ? "" : "s")} back · " : "") + (path.Length > 0 ? path : "Never saved");

    // ----- Restoring kept tabs and reactivating the previous active tab -----
    // Restoring kept tabs must not leave an extra blank tab. RecoverProjectTabs runs at idle after Frame 1 (blank Timeline, strip collapsed);
    // if the start-up tab is untouched, the previously active tab is brought forward via the switch steps (blank tab backgrounded, target
    // resumed from cache) and the blank tab is removed without prompting. The tab's own window layout is skipped (Law 1: single dock build
    // until the user switches tabs). On resume failure the blank tab stays active with a status message. Only manifests from deliberate
    // keep-on-close (ProjectTabs.WriteKeptManifest) are restored this way; crash leftovers stay unopened in the background.

    // Set when RecoverProjectTabs ran during a pending close prompt and deferred the leftover tabs; it re-runs when that close is cancelled
    // (MainWindow_Closing). Strip menu gestures are registered once, on the first run.
    private bool _leftTabsPutOff, _tabGesturesSet;

    /// <summary>
    /// After a cancelled close (MainWindow_Closing, once <c>_closeAsked</c> clears): adopts the leftover tabs deferred during the prompt
    /// (<see cref="RecoverProjectTabs"/>) at idle, as at start. Separate method so goldens can drive it.
    /// </summary>
    private void TakeInLeftTabsPutOff()
    {
        if (!_leftTabsPutOff)
            return;

        _leftTabsPutOff = false;
        _ = Dispatcher.BeginInvoke(RecoverProjectTabs, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Pure (golden: project-tabs-restored-front): bring the previous active tab forward only if the start-up tab is the sole other tab and
    /// active (<paramref name="startTabAlone"/>), blank with no project path, not deliberately <paramref name="revealed"/> (Ctrl+T or +),
    /// nothing <paramref name="underWay"/> (switch, open, import, drop, recording, drag, close), and not <paramref name="launchedWithProject"/>.
    /// </summary>
    internal static bool BringsRestoredForward(bool startTabAlone, bool holdsNothing, string projectPath, bool revealed, bool underWay, bool launchedWithProject) =>
        startTabAlone && holdsNothing && projectPath.Length == 0 && !revealed && !underWay && !launchedWithProject;

    /// <summary>
    /// Previous active tab to bring forward per <see cref="BringsRestoredForward"/>, or null to leave restored tabs in the background.
    /// <paramref name="leftFront"/> comes from ProjectTabs.TakeLeftBehind and must be in <paramref name="adopted"/>; <paramref name="known"/>
    /// lists the tabs present before adoption.
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
    /// Strip-side swap (golden-testable on a standalone ProjectTabs): activates <paramref name="front"/> and removes the blank start-up tab.
    /// Returns the blank tab's index for <see cref="PutStartTabBack"/>.
    /// </summary>
    internal static int SwapStartTabFor(ProjectTabs tabs, ProjectTabHeader blank, ProjectTabHeader front)
    {
        var at = tabs.IndexOf(blank);
        tabs.Activate(front);
        tabs.Close(blank);
        return at;
    }

    /// <summary>Reverts <see cref="SwapStartTabFor"/>: inserts and activates a blank tab at the start-up tab's index.</summary>
    internal static ProjectTabHeader PutStartTabBack(ProjectTabs tabs, int at)
    {
        var blank = tabs.Insert(at, "New Project");
        tabs.Activate(blank);
        return blank;
    }

    /// <summary>
    /// Brings the previous active tab forward at start like a content-only switch (window layout untouched; see section header): blank
    /// start-up tab backgrounded and removed, <paramref name="shown"/> callback (RecoverProjectTabs reveals the strip while it loads), then
    /// resume from cache. Returns false on failure with a blank tab reactivated; the tab keeps its files for a retry and the status
    /// line reports why.
    /// </summary>
    private async Task<bool> BringRestoredTabForwardAsync(ProjectTabHeader front, Action shown)
    {
        var blank = _tabs.Active;
        var at = -1;
        var resumed = false;
        var restoring = _tabControl.Prepare("bring the restored tab forward");
        try
        {
            // Background the blank tab as a switch would (it keeps nothing), then remove it from the strip.
            PutActiveBehind(out var write);
            QueueTabWrite(write);
            _tabControl.ClearUnresumed(blank.Id);
            ForgetKeptTab(blank.Id);
            at = SwapStartTabFor(_tabs, blank, front);
            _tabControl.Commit(restoring, front);
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
            // Failed: restore a blank active tab at the start-up position (the restored tab keeps its files).
            if (!resumed && at >= 0 && _tabs.Tabs.Contains(front))
            {
                var blankAgain = PutStartTabBack(_tabs, at);
                _tabControl.Commit(restoring, blankAgain);
                ShowTabs();
                AppLog.Write("Project tabs: the tab in front when HandPeg last closed could not be brought back; the blank start-up tab stays in front.");
            }

            _tabControl.Finish(restoring);
        }

        return resumed;
    }

    // ----- Preserving a restored tab's saved layout -----
    // The restored tab is brought forward without applying its layout (Law 1), so the window shows the start-up layout; CaptureWindowStateForTab
    // must not overwrite the tab's saved layout with it when the tab is backgrounded. The header keeps its own layout until ApplyTabLayout
    // runs (switch back), the user edits the dock (Layout Selector, pane move or splitter: DockManager.Changed, never raised by ApplyTabLayout),
    // or the tab closes.

    // Id of the tab restored without its layout applied (null for none).
    private string? _layoutNotTaken;
    private bool _layoutWatchHooked;

    /// <summary>True when backgrounding this tab must keep its saved layout (see section header).</summary>
    private bool LayoutNotTaken(ProjectTabHeader tab) => string.Equals(_layoutNotTaken, tab.Id, StringComparison.Ordinal);

    /// <summary>Marks a restored tab's saved layout as preserved (if any) and hooks DockManager.Changed once to clear the mark.</summary>
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

    /// <summary>Clears the preserved-layout mark when the tab's layout is applied or the tab closes.</summary>
    private void ForgetLayoutNotTaken(string id)
    {
        if (string.Equals(_layoutNotTaken, id, StringComparison.Ordinal))
            _layoutNotTaken = null;
    }

    // ResumeTabAsync status suffix: tab reopened from a project file modified on disk since the tab was kept.
    private const string ChangedOnDiskTail = ": opened from its project file, which changed on the disk since its tab was kept.";

    /// <summary>Status line for a tab reopened from its project file after the file changed on disk.</summary>
    internal static string ChangedOnDiskLine(string title) => title + ChangedOnDiskTail;

    // ResumeTabAsync status suffix: project file changed on disk but unreadable, so the tab opened from its cache copy.
    private const string UnreadableOnDiskTail = "; opened from the copy kept with its tab, unsaved and tied to no file: Save asks for a name, and that file is left as it is.";

    /// <summary>
    /// Pure status line for a tab opened from cache because its changed project file was unreadable; <paramref name="why"/> names a newer
    /// format or the read error.
    /// </summary>
    internal static string UnreadableOnDiskLine(string title, string why) =>
        $"{title}: its project file changed on the disk but could not be read{(why.Length > 0 ? $" ({why})" : "")}" + UnreadableOnDiskTail;

    /// <summary>
    /// True when <paramref name="said"/> is a ResumeTabAsync-specific notice (kept instead of the restore line): recovered
    /// <paramref name="stepsBack"/> actions back, project file changed on disk, or changed-but-unreadable fallback to cache.
    /// </summary>
    private static bool ResumeSaidOfItsOwn(string said, int stepsBack) =>
        (stepsBack > 0 && string.Equals(said, StepsBackLine(stepsBack), StringComparison.Ordinal)) || said.EndsWith(ChangedOnDiskTail, StringComparison.Ordinal)
        || said.EndsWith(UnreadableOnDiskTail, StringComparison.Ordinal);

    /// <summary>
    /// Pure status line after restoring kept tabs: with <paramref name="front"/> brought forward, names it and the count; otherwise says
    /// the tabs are back in the title bar.
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
