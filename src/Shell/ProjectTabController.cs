using System.Diagnostics.CodeAnalysis;
using HandPegApp.Services;

namespace HandPegApp.Shell;

/// <summary>
/// Owns the project-tab session state behind the title-bar strip (BAD-01, Pass 5.6.5): the tab order and the active tab
/// (<see cref="ProjectTabs"/>), the live contexts of the tabs behind it, the tabs whose resume failed, the activation generation and the
/// one tab operation in flight (prepare / commit / abort / finish). The window (MainWindow.ProjectTabs.cs and its tab partials) does the
/// UI and engine work of a switch and asks this controller for every state change, so no activation bypasses the generation.
/// <para>UI thread only. Nothing here touches disk or WPF, so it is built before Frame 1 at no cost (Law 1: the strip stays collapsed with
/// its one blank tab; tabs left by a previous session are offered only after ContentRendered).</para>
/// </summary>
/// <typeparam name="TContext">A live inactive tab (the window's TabViewContext).</typeparam>
internal sealed class ProjectTabController<TContext>(ProjectTabs tabs) where TContext : class
{
    private readonly Dictionary<string, TContext> _behind = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresumed = new(StringComparer.Ordinal);
    private TabOperation? _running;

    /// <summary>Tab order, the active tab, stills and the per-tab disk queue.</summary>
    public ProjectTabs Tabs { get; } = tabs;

    /// <summary>The tab in front (its project lives in the view model).</summary>
    public ProjectTabHeader Active => Tabs.Active;

    /// <summary>Live inactive tabs by id (the active tab is never here). Changed only through this controller.</summary>
    public IReadOnlyDictionary<string, TContext> Behind => _behind;

    /// <summary>
    /// Tabs whose resume failed (missing media drive, unreadable cache): their files are kept and activation retries. Changed only through
    /// this controller.
    /// </summary>
    public IReadOnlySet<string> Unresumed => _unresumed;

    /// <summary>
    /// Activation generation: bumped by every change of the active tab made here. Work queued for one activation (a deferred view restore,
    /// a still capture) compares it with <see cref="IsCurrent"/> and drops itself when another activation came between, even one that
    /// brought the same tab back (A, B, A).
    /// </summary>
    public long Generation { get; private set; }

    /// <summary>True while a switch, new tab, open, close, duplicate or recovery runs: other tab operations and project edits wait.</summary>
    public bool IsSwitching => _running is not null;

    /// <summary>The operation in flight, or null.</summary>
    public TabOperation? Running => _running;

    /// <summary>Raised when <see cref="IsSwitching"/> changes (true on prepare, false on finish).</summary>
    public event Action<bool>? SwitchingChanged;

    /// <summary>True when no activation happened since <paramref name="generation"/> was read.</summary>
    public bool IsCurrent(long generation) => generation == Generation;

    // ----- Live contexts behind the active tab -----

    /// <summary>
    /// Keeps the outgoing tab live in memory (it is no longer a failed resume); called while it is still active, just before the operation
    /// commits the next tab.
    /// </summary>
    public void PutBehind(ProjectTabHeader tab, TContext live)
    {
        _behind[tab.Id] = live;
        _unresumed.Remove(tab.Id);
    }

    /// <summary>A tab's live context, if kept.</summary>
    public bool TryGetBehind(string id, [MaybeNullWhen(false)] out TContext live) => _behind.TryGetValue(id, out live);

    /// <summary>Removes and returns a tab's live context (closed, or let go to its cache file).</summary>
    public bool TryTakeBehind(string id, [MaybeNullWhen(false)] out TContext live) => _behind.Remove(id, out live);

    /// <summary>
    /// The active tab took its project back into the view model (from memory or its file): no live context behind, no failed resume.
    /// </summary>
    public void TakeFront(ProjectTabHeader tab)
    {
        _behind.Remove(tab.Id);
        _unresumed.Remove(tab.Id);
    }

    // ----- Failed resumes -----

    /// <summary>True when <paramref name="tab"/>'s resume failed and its files wait for a retry.</summary>
    public bool IsUnresumed(ProjectTabHeader tab) => _unresumed.Contains(tab.Id);

    /// <summary>Marks a tab's resume as failed (it keeps its files; activation retries).</summary>
    public void MarkUnresumed(string id) => _unresumed.Add(id);

    /// <summary>Clears a tab's failed-resume mark (resumed, emptied, closed or split).</summary>
    public void ClearUnresumed(string id) => _unresumed.Remove(id);

    /// <summary>
    /// New work in front of a failed-resume tab goes into a fresh active tab (ProjectTabs.TakeWorkOff); the failed tab stays inactive with
    /// its files and is no longer marked (activation resumes it from its file).
    /// </summary>
    public ProjectTabHeader TakeWorkOff(ProjectTabHeader stuck, string title)
    {
        _unresumed.Remove(stuck.Id);
        var fresh = Tabs.TakeWorkOff(title);
        Generation++;
        return fresh;
    }

    // ----- Prepare / commit / abort / finish -----

    /// <summary>
    /// Starts a tab operation (the caller has checked that one may start: MainWindow.MayChangeTabs). One at a time; ended by
    /// <see cref="Finish"/> in the caller's finally.
    /// </summary>
    public TabOperation Prepare(string what)
    {
        if (_running is { } running)
            throw new InvalidOperationException($"A project tab operation ({running.What}) is still running; {what} must wait for it.");
        var operation = new TabOperation(what, Tabs.Active, Generation);
        _running = operation;
        SwitchingChanged?.Invoke(true);
        return operation;
    }

    /// <summary>
    /// Makes <paramref name="to"/> the active tab for <paramref name="operation"/> (the outgoing tab is already put behind, or kept nothing).
    /// A second commit in one operation (start-up swap, put back) is allowed; each bumps the generation.
    /// </summary>
    public void Commit(TabOperation operation, ProjectTabHeader to)
    {
        Check(operation);
        Tabs.Activate(to);
        Generation++;
        (operation.To, operation.Committed) = (to, Generation);
    }

    /// <summary>
    /// <paramref name="failed"/> could not be brought to the front: <paramref name="returnTo"/> (the tab put behind for this operation) is
    /// made active again and true returned, the caller then re-attaching it. With no tab to return to (null, the failed tab itself, or
    /// closed meanwhile) <paramref name="failed"/> stays active as a failed resume and false is returned.
    /// </summary>
    public bool Abort(ProjectTabHeader failed, ProjectTabHeader? returnTo)
    {
        if (returnTo is null || ReferenceEquals(returnTo, failed) || !Tabs.Tabs.Contains(returnTo))
        {
            _unresumed.Add(failed.Id);
            return false;
        }

        Tabs.Activate(returnTo);
        Generation++;
        if (_running is { } running)
            (running.To, running.Committed, running.Aborted) = (returnTo, Generation, true);
        return true;
    }

    /// <summary>
    /// The return of an <see cref="Abort"/> failed too: <paramref name="failed"/> is active again as a failed resume (its files kept), so the
    /// view model's content moves to a tab of its own on the next new work.
    /// </summary>
    public void Reinstate(ProjectTabHeader failed)
    {
        Tabs.Activate(failed);
        Generation++;
        _unresumed.Add(failed.Id);
        if (_running is { } running)
            (running.To, running.Committed) = (failed, Generation);
    }

    /// <summary>Ends <paramref name="operation"/> (idempotent; a stale or foreign operation is ignored).</summary>
    public void Finish(TabOperation operation)
    {
        if (!ReferenceEquals(_running, operation))
            return;
        _running = null;
        SwitchingChanged?.Invoke(false);
    }

    private void Check(TabOperation operation)
    {
        if (!ReferenceEquals(_running, operation))
            throw new InvalidOperationException($"The project tab operation ({operation.What}) is not the one running.");
    }
}

/// <summary>One project-tab operation in flight (<see cref="ProjectTabController{TContext}.Prepare"/>).</summary>
internal sealed class TabOperation(string what, ProjectTabHeader from, long prepared)
{
    /// <summary>What runs, for diagnostics ("switch project tabs", "close a project tab").</summary>
    public string What { get; } = what;

    /// <summary>The active tab when the operation was prepared.</summary>
    public ProjectTabHeader From { get; } = from;

    /// <summary>The generation when prepared.</summary>
    public long Prepared { get; } = prepared;

    /// <summary>The tab the operation last made active, or null before a commit.</summary>
    public ProjectTabHeader? To { get; internal set; }

    /// <summary>The generation of the last commit (0 before one).</summary>
    public long Committed { get; internal set; }

    /// <summary>True when the operation returned to an earlier tab (<see cref="ProjectTabController{TContext}.Abort"/>).</summary>
    public bool Aborted { get; internal set; }
}
