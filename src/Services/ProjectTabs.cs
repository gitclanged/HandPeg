using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Header of one title-bar project tab, persisted as <c>&lt;Id&gt;.tab.json</c> (under 4 KB including the window layout): name, file,
/// dirty flag, view state (playhead, Timeline zoom/scroll, 3D camera, dock layout). The Sequence lives in the tab's <c>.hptab</c> file;
/// view state is never written into the project file, so orbiting or resizing panes does not dirty the project.
/// </summary>
public sealed record ProjectTabHeader
{
    /// <summary>The tab's own id: the name of its files in the cache.</summary>
    public string Id { get; init; } = "";

    /// <summary>What the tab says: the project's name, else its first file's, else New Project.</summary>
    public string Title { get; set; } = "";

    /// <summary>The project file it was opened from or saved to; empty for work never saved.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Dirty flag captured on deactivation (the active tab is queried live).</summary>
    public bool Dirty { get; set; }

    /// <summary>Whether it holds nothing worth keeping (a fresh tab: MainViewModel.HoldsNothingForTab): an empty tab keeps no file.</summary>
    public bool Empty { get; set; } = true;

    public double PlayheadMs { get; set; }

    /// <summary>The Timeline's view: from where, how many seconds across, and whether it showed the whole Sequence.</summary>
    public double ViewStart { get; set; }

    public double ViewSpan { get; set; }

    public bool ViewFit { get; set; } = true;

    /// <summary>How far the Timeline's rows were scrolled down, in DIPs.</summary>
    public double VerticalOffset { get; set; }

    /// <summary>A cold resume's view restore is queued: the saved view stays this tab's until it runs (never saved).</summary>
    [JsonIgnore]
    public bool ViewPending { get; set; }

    /// <summary>Monotonic last-active counter; least recently used tabs drop their stills first.</summary>
    public long LastActive { get; set; }

    /// <summary>The name the New Project studio gave it, for its first save ("" for none): each tab's own, never the next tab's.</summary>
    public string NewProjectName { get; set; } = "";

    /// <summary>The folder the New Project studio chose for its first save (null: the Projects folder).</summary>
    public string? NewProjectFolder { get; set; }

    /// <summary>
    /// Project frame size captured on deactivation for the tab hover card (vertical projects reported tall); 0 while unknown.
    /// </summary>
    public int FrameWidth { get; set; }

    public int FrameHeight { get; set; }

    /// <summary>Project frame rate (fps) captured on deactivation; 0 while unknown.</summary>
    public double FrameRate { get; set; }

    /// <summary>
    /// True when the player was in 3D Exploded View on deactivation; reactivation restores the view and camera. False for older headers.
    /// </summary>
    public bool ExplodedOpen { get; set; }

    /// <summary>3D camera on deactivation (GpuPreviewEngine.CameraPose: degrees, fitted-distance multiple, plane spacing).</summary>
    public double ExplodedYaw { get; set; } = GpuPreviewEngine.DefaultYaw;

    public double ExplodedPitch { get; set; } = GpuPreviewEngine.DefaultPitch;

    public double ExplodedZoom { get; set; } = GpuPreviewEngine.DefaultZoom;

    public double ExplodedSpacing { get; set; } = GpuPreviewEngine.DefaultSpacing;

    /// <summary>
    /// Main-window dock layout on deactivation (AppSettings.PerTabWindowLayouts, DockManager.Save JSON, floating windows excluded),
    /// re-applied on activation. Null keeps the current layout.
    /// </summary>
    public string? LayoutJson { get; set; }

    /// <summary>
    /// MediaBrowserView name paired with <see cref="LayoutJson"/>, applied before the layout; null keeps the current view.
    /// </summary>
    public string? MediaView { get; set; }

    /// <summary>
    /// Undo steps the cached file lags the live project (MainViewModel.RecoveryLagSteps; the newest steps sit on Redo); 0 for an exact
    /// write. A restored lagging tab reports it once. Omitted from JSON while 0.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int StepsBack { get; set; }

    /// <summary>
    /// Project file stamp (last-write UTC ticks, length) of a clean tab, taken when project and file were known identical (open, reload,
    /// save; <see cref="ProjectTabs.Stamped"/>); 0 for none. A changed file on resume wins over the cache (<see cref="ProjectTabs.ReadForResume"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ProjectFileTicks { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ProjectFileLength { get; set; }

    /// <summary>
    /// In-memory stamp request counter: async stamps can complete out of order, so only the latest request may land (StampProjectFileAsync).
    /// </summary>
    [JsonIgnore]
    internal int StampsAsked { get; set; }

    /// <summary>Copies the view state (3D view, camera, layout, Media Browser view) from another header, for duplicates.</summary>
    public void TakeWindowStateOf(ProjectTabHeader other)
    {
        (ExplodedOpen, ExplodedYaw, ExplodedPitch, ExplodedZoom, ExplodedSpacing) =
            (other.ExplodedOpen, other.ExplodedYaw, other.ExplodedPitch, other.ExplodedZoom, other.ExplodedSpacing);
        (LayoutJson, MediaView) = (other.LayoutJson, other.MediaView);
    }

    /// <summary>
    /// Game Preset picked before the first clip, pending the tab's first file (opaque view-model object). In memory only, never serialized.
    /// </summary>
    [JsonIgnore]
    public object? WaitingTemplate { get; set; }
}

/// <summary>
/// tabs.json: tab order and active tab, read when a session ended without a clean close. <paramref name="Kept"/> is set only by a close
/// that suspended the tabs deliberately (<see cref="ProjectTabs.WriteKeptManifest"/>); only then is the active tab restored. Omitted while false.
/// </summary>
public sealed record ProjectTabManifest(IReadOnlyList<string> Order, string? Active,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Kept = false);

/// <summary>Source a suspended tab's project was resumed from (<see cref="ProjectTabs.ReadForResume"/>).</summary>
public enum TabResumeSource
{
    /// <summary>Its cache file: unsaved work, its project file unchanged since it was kept, or no project file there.</summary>
    Cache,

    /// <summary>Its project file, which changed on the disk since the tab was kept (said on the status line).</summary>
    ChangedFile,

    /// <summary>Its project file; the tab had no stamp to detect a change (nothing reported).</summary>
    File,

    /// <summary>Its cache file, its project file having changed on the disk and not being readable (a newer format, locked, damaged).</summary>
    CacheFileUnreadable,
}

/// <summary>
/// Result of <see cref="ProjectTabs.ReadForResume"/>: state, saved-state fingerprint (null resumes as unsaved), source, the project file
/// stamp when read from it (0 for the cache), and the read error of a changed but unreadable project file.
/// </summary>
public readonly record struct TabResume(ProjectState State, string? Saved, TabResumeSource From, long FileTicks, long FileLength, string? Why);

/// <summary>
/// Title-bar project tabs: order, active tab, and per-tab files in <c>%Cache%\project-tabs\&lt;session&gt;</c> (<c>.hptab</c> project with
/// undo history, <c>.tab.json</c> header, <c>.saved</c> fingerprint when dirty). Headless; driven by MainWindow.ProjectTabs.cs. The
/// <see cref="MaxHotTabs"/> most recent tabs keep a frozen still in memory. No disk access before Frame 1.
/// Each process owns its session folder through an exclusive <c>session.lock</c>; an unlockable folder belongs to a live process, a lockable
/// one was abandoned and is adopted by <see cref="TakeLeftBehind"/>. Per-tab file I/O is serialized on the thread pool
/// (<see cref="Enqueue(string, Action)"/>) so writes, deletes and reads of one tab never reorder.
/// </summary>
public sealed class ProjectTabs
{
    /// <summary>
    /// Number of most recently active file-only tabs that keep their paused still in memory (live tab contexts keep theirs regardless).
    /// </summary>
    public const int MaxHotTabs = 3;

    public const string StateExtension = ".hptab", HeaderExtension = ".tab.json", SavedExtension = ".saved", ManifestName = "tabs.json", LockName = "session.lock";

    /// <summary>Title given to an orphaned project file with no readable header (<see cref="LeftBehind"/>).</summary>
    public const string RecoveredTitle = "Recovered Project";

    // Temp-file suffix of ProjectStore.WriteWhole: a partial write, never the only copy of a tab.
    private const string PartialExtension = ".tmp";

    private static readonly string[] TabFileEndings = [StateExtension, HeaderExtension, SavedExtension];

    private static readonly JsonSerializerOptions HeaderOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly List<ProjectTabHeader> _tabs = [];
    private readonly Dictionary<string, BitmapSource> _stills = new(StringComparer.Ordinal);
    private long _clock;

    // Tail task of each tab's serialized disk-work chain (cache write, delete, header rewrite, read, copy). Guarded by _gate.
    private readonly Dictionary<string, Task> _io = new(StringComparer.Ordinal);

    // Session folder lock, acquired on the first write (never before Frame 1), and its gate: writers run on the pool and the UI thread.
    private readonly Lock _gate = new();
    private FileStream? _lock;

    // Set by ForgetSession under _gate: late pool writes are refused so they cannot recreate the folder, its lock or a tab file.
    private bool _forgotten;

    /// <param name="root">The cache's project-tabs folder: this session's files go in a folder of its own under it, made when the first is written.</param>
    /// <param name="firstId">The first tab's id (tests); a new one otherwise.</param>
    /// <param name="session">The session's folder name (tests); a new one otherwise.</param>
    public ProjectTabs(string root, string? firstId = null, string? session = null)
    {
        RootPath = root;
        FolderPath = Path.Combine(root, session ?? Guid.NewGuid().ToString("N"));
        Active = Add("New Project", firstId);
        Touch(Active);
    }

    /// <summary>The cache folder of HandPeg's tabs: a folder per session under it.</summary>
    public static string DefaultRoot => Path.Combine(AppPaths.Cache, "project-tabs");

    /// <summary>The folder every session's folder is in.</summary>
    public string RootPath { get; }

    /// <summary>This session's folder; the only one written to (adopted folders excepted).</summary>
    public string FolderPath { get; }

    /// <summary>The tabs in their order along the title bar.</summary>
    public IReadOnlyList<ProjectTabHeader> Tabs => _tabs;

    public int Count => _tabs.Count;

    // Tab count for the frame pump; written on the UI thread when a tab is added or closed.
    private static int _openCount;

    /// <summary>
    /// Open tab count, read on any thread: with several tabs on unified memory, Dynamic Preview Scaling starts smaller (GpuPreviewEngine.StartStep).
    /// </summary>
    public static int OpenCount
    {
        get => Volatile.Read(ref _openCount);
        private set => Volatile.Write(ref _openCount, value);
    }

    /// <summary>Active tab (held by the view model); setting it reprioritizes WorkScheduler so background tabs queue behind it.</summary>
    public ProjectTabHeader Active
    {
        get;
        private set
        {
            field = value;
            WorkScheduler.FrontTab = value.Id;
        }
    }

    /// <summary>Appends a new inactive tab.</summary>
    public ProjectTabHeader Add(string title, string? id = null)
    {
        var tab = new ProjectTabHeader { Id = id ?? NewId(), Title = title };
        _tabs.Add(tab);
        OpenCount = _tabs.Count;
        return tab;
    }

    /// <summary>Inserts a new inactive tab at <paramref name="index"/> (duplicates go right after the original).</summary>
    public ProjectTabHeader Insert(int index, string title, string? id = null)
    {
        var tab = new ProjectTabHeader { Id = id ?? NewId(), Title = title };
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        OpenCount = _tabs.Count;
        return tab;
    }

    /// <summary>
    /// Moves a tab to <paramref name="index"/> (clamped). Active tab, MRU order and stills are unchanged; Ctrl+Tab, close-neighbour and
    /// tabs.json follow the new order.
    /// </summary>
    public void Move(ProjectTabHeader tab, int index)
    {
        var from = _tabs.IndexOf(tab);
        if (from < 0)
            return;
        var to = Math.Clamp(index, 0, _tabs.Count - 1);
        if (to == from)
            return;
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
    }

    /// <summary>
    /// Appends an adopted tab from an abandoned session (cold, no still) unless the id exists. It ranks below every tab of this session
    /// in MRU order, so it never evicts a current tab's still.
    /// </summary>
    public bool Adopt(ProjectTabHeader tab)
    {
        if (tab.Id.Length == 0 || Find(tab.Id) is not null)
            return false;
        tab.LastActive = 0;
        _tabs.Add(tab);
        OpenCount = _tabs.Count;
        return true;
    }

    /// <summary>
    /// Creates an active tab for new work that replaced a tab that failed to resume (MainWindow.SplitFromUnresumed); the failed tab stays
    /// on the strip, inactive, its files untouched.
    /// </summary>
    public ProjectTabHeader TakeWorkOff(string title)
    {
        var fresh = Add(title);
        Activate(fresh);
        return fresh;
    }

    /// <summary>Makes a tab active (the window has already deactivated the previous one).</summary>
    public void Activate(ProjectTabHeader tab)
    {
        if (!_tabs.Contains(tab))
            throw new ArgumentException("The tab is not one of these.", nameof(tab));
        Active = tab;
        Touch(tab);
    }

    /// <summary>
    /// Marks a tab most recently used and releases the stills of tabs outside the <see cref="MaxHotTabs"/> most recent.
    /// </summary>
    public void Touch(ProjectTabHeader tab)
    {
        tab.LastActive = ++_clock;
        foreach (var cold in _tabs.OrderByDescending(t => t.LastActive).Skip(MaxHotTabs))
            _stills.Remove(cold.Id);
    }

    /// <summary>Whether a tab is among the <see cref="MaxHotTabs"/> most recently used.</summary>
    public bool IsHot(ProjectTabHeader tab) => _tabs.OrderByDescending(t => t.LastActive).Take(MaxHotTabs).Contains(tab);

    /// <summary>Stores a hot tab's paused still (frozen if needed); null releases it.</summary>
    public void SetStill(ProjectTabHeader tab, BitmapSource? still)
    {
        if (still is null || !IsHot(tab))
        {
            _stills.Remove(tab.Id);
            return;
        }

        if (!still.IsFrozen && still.CanFreeze)
            still.Freeze();
        _stills[tab.Id] = still;
    }

    /// <summary>A hot tab's paused picture; null for a cold one, or one that never had any.</summary>
    public BitmapSource? StillOf(ProjectTabHeader tab) => _stills.GetValueOrDefault(tab.Id);

    /// <summary>How many tabs keep a still now (tests).</summary>
    public int StillCount => _stills.Count;

    /// <summary>Where a tab is along the strip (-1 for none of these).</summary>
    public int IndexOf(ProjectTabHeader tab) => _tabs.IndexOf(tab);

    public ProjectTabHeader? Find(string id) => _tabs.Find(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>The tab after (<paramref name="step"/> 1) or before (-1) another, round from the last to the first; null with only one tab.</summary>
    public ProjectTabHeader? Next(ProjectTabHeader from, int step)
    {
        var at = _tabs.IndexOf(from);
        if (_tabs.Count < 2 || at < 0)
            return null;
        var next = ((at + step) % _tabs.Count + _tabs.Count) % _tabs.Count;
        return _tabs[next];
    }

    /// <summary>Tab activated when <paramref name="tab"/> closes: the next one, or the previous at the end.</summary>
    public ProjectTabHeader? NeighbourOf(ProjectTabHeader tab)
    {
        var at = _tabs.IndexOf(tab);
        if (at < 0 || _tabs.Count < 2)
            return null;
        return at + 1 < _tabs.Count ? _tabs[at + 1] : _tabs[at - 1];
    }

    /// <summary>
    /// Removes a tab and deletes its files. Close the active tab only after activating another (<see cref="NeighbourOf"/>); a sole tab
    /// is emptied by the window instead.
    /// </summary>
    public void Close(ProjectTabHeader tab)
    {
        if (ReferenceEquals(tab, Active) && _tabs.Count > 1)
            throw new InvalidOperationException("Put another tab in front before closing the one in front.");
        if (_tabs.Count <= 1 || !_tabs.Remove(tab))
            return;
        OpenCount = _tabs.Count;
        _stills.Remove(tab.Id);
        DeleteFilesAfterIo(tab.Id);
    }

    // ----- Per-tab serialized disk work -----

    /// <summary>
    /// Queues file work for a tab on the thread pool after all earlier work for that tab (callable from any thread). Exceptions from
    /// <paramref name="io"/> fault the returned task; the chain continues.
    /// </summary>
    public Task Enqueue(string id, Action io) => Enqueue<bool>(id, () =>
    {
        io();
        return true;
    });

    /// <summary><see cref="Enqueue(string, Action)"/> with a result (e.g. a read ordered after earlier writes).</summary>
    public Task<T> Enqueue<T>(string id, Func<T> io)
    {
        Task<T> next;
        lock (_gate)
        {
            var before = _io.GetValueOrDefault(id) ?? Task.CompletedTask;
            next = before.ContinueWith(_ => io(), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _io[id] = next;
        }

        // Drop the chain entry once its tail completes (runs inline when already complete, so outside the gate).
        _ = next.ContinueWith(done =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_io.GetValueOrDefault(id), done))
                    _io.Remove(id);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return next;
    }

    /// <summary>Tail of a tab's work chain (completed when empty); await before relying on its files.</summary>
    public Task IoOf(string id)
    {
        lock (_gate)
            return _io.GetValueOrDefault(id) ?? Task.CompletedTask;
    }

    /// <summary>Whether work on a tab's files is still under way or waiting.</summary>
    public bool IsBusyWith(string id)
    {
        lock (_gate)
            return _io.GetValueOrDefault(id) is { IsCompleted: false };
    }

    /// <summary>
    /// Blocks (shutdown, save from memory) until one tab's or all tabs' queued work completes or <paramref name="timeoutMs"/> elapses.
    /// Returns true when drained; a failed write counts as completed.
    /// </summary>
    public bool WaitFor(string? id, int timeoutMs)
    {
        Task[] running;
        lock (_gate)
            running = id is null ? [.. _io.Values] : _io.GetValueOrDefault(id) is { } one ? [one] : [];
        if (running.Length == 0)
            return true;
        try
        {
            return Task.WaitAll(running, timeoutMs);
        }
        catch (AggregateException)
        {
            return running.All(t => t.IsCompleted);
        }
    }

    /// <summary>
    /// Deletes a tab's three files after its queued work (inline when idle); failures are retried by the clean close (<see cref="DeleteFiles"/>).
    /// </summary>
    public void DeleteFilesAfterIo(string id)
    {
        if (IsBusyWith(id))
            _ = Enqueue(id, () => DeleteFiles(id));
        else
            DeleteFiles(id);
    }

    // ----- Files -----

    public string StateFile(string id) => Path.Combine(FolderPath, id + StateExtension);

    public string HeaderFile(string id) => Path.Combine(FolderPath, id + HeaderExtension);

    public string SavedFile(string id) => Path.Combine(FolderPath, id + SavedExtension);

    public string ManifestFile => Path.Combine(FolderPath, ManifestName);

    /// <summary>Header JSON (serialized on the UI thread, written on any).</summary>
    public static string HeaderJson(ProjectTabHeader tab) => JsonSerializer.Serialize(tab, HeaderOptions);

    /// <summary>Parses a header; null for damaged or foreign text.</summary>
    public static ProjectTabHeader? ReadHeader(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectTabHeader>(json, HeaderOptions) is { Id.Length: > 0 } tab ? tab : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a deactivated tab from supplied data (any thread): project file (ProjectStore, atomic, with undo history), saved-state
    /// fingerprint when dirty (null deletes a stale one), then the header.
    /// </summary>
    public void WriteTab(string id, ProjectState state, string? savedFingerprint, string headerJson)
    {
        // Project file first, header last: a crash in between leaves an orphaned project file that the next start adopts as
        // "Recovered Project" (dirty). The reverse order could leave a header naming a missing file, losing the work.
        ClaimSession();
        ProjectStore.SaveTo(state, StateFile(id), durable: false);

        // Session was forgotten mid-write (the write outlived the shutdown wait): remove the file so it is never offered back.
        if (IsForgotten)
        {
            TryDelete(StateFile(id));
            throw new IOException(ForgottenMessage);
        }

        if (savedFingerprint is { Length: > 0 })
            ProjectStore.WriteWhole(SavedFile(id), savedFingerprint);
        else
            TryDelete(SavedFile(id));
        ProjectStore.WriteWhole(HeaderFile(id), Stamped(headerJson));
    }

    /// <summary>
    /// Header text with the project file stamp applied (any thread): the carried <see cref="ProjectTabHeader.ProjectFileTicks"/> for a clean
    /// tab with a project file, none otherwise; non-header text is returned unchanged. Never stats the disk: a fresh stat could postdate an
    /// external change (making a stale cache look current) and an unreachable share would stall the shutdown write.
    /// </summary>
    public static string Stamped(string headerJson)
    {
        if (ReadHeader(headerJson) is not { } header || (!header.Dirty && !header.Empty && header.ProjectPath.Length > 0)
            || header is { ProjectFileTicks: 0, ProjectFileLength: 0 })
            return headerJson;

        (header.ProjectFileTicks, header.ProjectFileLength) = (0L, 0L);
        return HeaderJson(header);
    }

    /// <summary>Header text with <see cref="ProjectTabHeader.StepsBack"/> applied (any thread).</summary>
    public static string WithStepsBack(string headerJson, int steps)
    {
        if (ReadHeader(headerJson) is not { } header || header.StepsBack == steps)
            return headerJson;

        header.StepsBack = steps;
        return HeaderJson(header);
    }

    /// <summary>A file's last write (UTC ticks) and length (any thread); (0, 0) when it is not there or cannot be looked at.</summary>
    public static (long Ticks, long Length) FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0L, 0L);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (0L, 0L);
        }
    }

    /// <summary>
    /// True when a clean tab's project file changed since suspension (pure): a stamp exists, the file exists, and last write or length
    /// differs. A missing file keeps the cache; a missing stamp is decided by <see cref="ReadForResume"/>.
    /// </summary>
    public static bool FileChangedSince(long keptTicks, long keptLength, long ticks, long length) =>
        keptTicks != 0 && ticks != 0 && (ticks != keptTicks || length != keptLength);

    /// <summary>
    /// Reads a suspended tab for activation (on its queue, after its writes): the cache file plus fingerprint, or, for a clean tab
    /// (<paramref name="cleanProjectPath"/>) whose project file changed externally (<see cref="FileChangedSince"/>), that file as saved.
    /// A clean tab without a stamp reads its project file (<see cref="TabResumeSource.File"/>); a missing file keeps the cache. A changed
    /// but unreadable file falls back to the cache (<see cref="TabResumeSource.CacheFileUnreadable"/>) so Save never overwrites it.
    /// An unreadable cache throws, as <see cref="ReadState"/> does.
    /// </summary>
    public TabResume ReadForResume(string id, string? cleanProjectPath)
    {
        if (cleanProjectPath is { Length: > 0 } path && KeptStampOf(id, path) is { } kept && FileStamp(path) is { Ticks: not 0 } now
            && (IsUnstamped(kept.Ticks, kept.Length) || FileChangedSince(kept.Ticks, kept.Length, now.Ticks, now.Length)))
        {
            var changed = !IsUnstamped(kept.Ticks, kept.Length);
            try
            {
                var state = ProjectStore.Load(path);
                AppLog.Write(changed
                    ? "Project tabs: a tab kept without unsaved changes found its project file changed on the disk since; it opens from that file, not from its cache"
                    : "Project tabs: a tab kept without unsaved changes carried no stamp of its project file; it opens from that file, as before 5.5A");
                return new TabResume(state, null, changed ? TabResumeSource.ChangedFile : TabResumeSource.File, now.Ticks, now.Length, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                AppLog.Write("Project tabs: a tab's project file changed on the disk since it was kept, but could not be read; it opens from its cache, tied to no file", ex);
                // No fingerprint: the resumed state matches no file on disk, so it is dirty regardless of any stale fingerprint.
                var why = ex is NewerProjectFormatException newer ? $"saved in a newer project format by {newer.WrittenBy ?? "a newer version of HandPeg"}" : ex.Message;
                return new TabResume(ReadState(id), null, TabResumeSource.CacheFileUnreadable, 0L, 0L, why);
            }
        }

        return new TabResume(ReadState(id), ReadSaved(id), TabResumeSource.Cache, 0L, 0L, null);
    }

    /// <summary>True for an empty stamp (pure): a change to the project file cannot be detected.</summary>
    private static bool IsUnstamped(long ticks, long length) => ticks == 0 && length == 0;

    /// <summary>
    /// Stamp a tab header recorded for <paramref name="path"/> (any thread); (0, 0) for none. Null when the header is missing, unreadable,
    /// dirty or names another file, so the cache is read (unsaved work wins).
    /// </summary>
    private (long Ticks, long Length)? KeptStampOf(string id, string path)
    {
        try
        {
            return File.Exists(HeaderFile(id)) && ReadHeader(File.ReadAllText(HeaderFile(id))) is { Dirty: false } header
                   && string.Equals(header.ProjectPath, path, StringComparison.OrdinalIgnoreCase)
                ? (header.ProjectFileTicks, header.ProjectFileLength)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Duplicates a file-backed tab (any thread): copies <paramref name="fromId"/>'s project file atomically to <paramref name="toId"/>,
    /// writes no fingerprint (never saved), then the header, in <see cref="WriteTab"/> order.
    /// </summary>
    public void CopyState(string fromId, string toId, string headerJson)
    {
        ClaimSession();
        var partial = ProjectStore.PartialPath(StateFile(toId));
        File.Copy(StateFile(fromId), partial, overwrite: true);
        File.Move(partial, StateFile(toId), overwrite: true);
        TryDelete(SavedFile(toId));
        ProjectStore.WriteWhole(HeaderFile(toId), headerJson);
    }

    /// <summary>
    /// Rewrites an inactive tab's header after an in-memory change such as a rename (any thread; text built on the UI thread). No-op
    /// for tabs without a project file.
    /// </summary>
    public void WriteHeader(string id, string headerJson)
    {
        if (!HasState(id))
            return;
        ClaimSession();
        ProjectStore.WriteWhole(HeaderFile(id), Stamped(headerJson));
    }

    /// <summary>A project's frame as Project Settings draws it (pure): a vertical project's natural frame turned tall.</summary>
    public static (int Width, int Height) FrameOf(int naturalWidth, int naturalHeight, bool vertical) =>
        vertical ? (naturalHeight, naturalWidth) : (naturalWidth, naturalHeight);

    /// <summary>Hover-card frame line (pure): size and fps joined by a middle dot; "" while unknown.</summary>
    public static string DescribeFrame(int width, int height, double frameRate)
    {
        var size = width > 0 && height > 0 ? $"{width} x {height}" : "";
        var fps = frameRate > 0 ? frameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " fps" : "";
        return size.Length > 0 && fps.Length > 0 ? $"{size} · {fps}" : size + fps;
    }

    /// <summary>Reads a deactivated tab's project (any thread).</summary>
    public ProjectState ReadState(string id) => ProjectStore.Load(StateFile(id));

    /// <summary>Reads a tab's saved-state fingerprint; null when absent (clean tab) or unreadable.</summary>
    public string? ReadSaved(string id)
    {
        try
        {
            return File.Exists(SavedFile(id)) ? File.ReadAllText(SavedFile(id)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a tab's project file is there.</summary>
    public bool HasState(string id) => File.Exists(StateFile(id));

    /// <summary>Deletes a tab's three files (any thread); undeletable files are left for the clean close.</summary>
    public void DeleteFiles(string id)
    {
        TryDelete(StateFile(id));
        TryDelete(HeaderFile(id));
        TryDelete(SavedFile(id));
    }

    /// <summary>tabs.json text: order and active tab (<paramref name="kept"/>: written by a suspending close, <see cref="WriteKeptManifest"/>).</summary>
    public string ManifestJson(bool kept = false) => JsonSerializer.Serialize(new ProjectTabManifest(_tabs.Select(t => t.Id).ToList(), Active.Id, kept), HeaderOptions);

    /// <summary>Writes tabs.json to this session's folder (any thread; text from <see cref="ManifestJson"/> on the UI thread).</summary>
    public void WriteManifest(string json) => WriteManifest(json, kept: false);

    /// <summary>
    /// Writes tabs.json marked kept on a deliberate suspend (Keep Suspended for Next Launch; UI thread, at shutdown) so the next start
    /// restores the active tab; any other ending leaves tabs behind the start-up tab. Final write: later-landing earlier writes are dropped.
    /// </summary>
    public void WriteKeptManifest() => WriteManifest(ManifestJson(kept: true), kept: true);

    private void WriteManifest(string json, bool kept)
    {
        ClaimSession();
        lock (_manifestGate)
        {
            if (_manifestKept && !kept)
                return;
            ProjectStore.WriteWhole(ManifestFile, json);
            _manifestKept = kept;
        }
    }

    // Set once WriteKeptManifest wrote tabs.json; guarded by its own lock so manifest writes never interleave.
    private readonly Lock _manifestGate = new();
    private bool _manifestKept;

    // ----- The session's folder -----

    /// <summary>Whether this session holds its folder's lock now (tests).</summary>
    public bool HoldsSession
    {
        get
        {
            lock (_gate)
                return _lock is not null;
        }
    }

    /// <summary>True after <see cref="ForgetSession"/>; all later writes are refused.</summary>
    public bool IsForgotten
    {
        get
        {
            lock (_gate)
                return _forgotten;
        }
    }

    private const string ForgottenMessage = "This session's project tab files were let go as HandPeg closed: nothing more is written to them.";

    /// <summary>
    /// Creates the session folder and takes its exclusive lock if needed (any thread); the open lock marks the folder in use to other
    /// processes. Throws IOException on failure or after <see cref="ForgetSession"/>.
    /// </summary>
    public void ClaimSession()
    {
        lock (_gate)
        {
            if (_forgotten)
                throw new IOException(ForgottenMessage);
            if (_lock is not null)
                return;
            Directory.CreateDirectory(FolderPath);
            _lock = new FileStream(Path.Combine(FolderPath, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }

    /// <summary>Releases the lock and leaves the files, so the next session adopts the folder's tabs.</summary>
    public void ReleaseSession()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    /// <summary>
    /// Shutdown: deletes this session's tab files except those <paramref name="keep"/> selects, plus tabs.json and partial writes, then
    /// releases the lock. Unknown project files (adopted but never shown) are never deleted. The folder is removed only when nothing
    /// remains; otherwise the next start offers it again. Later writes are refused (<see cref="IsForgotten"/>).
    /// </summary>
    public void ForgetSession(Func<ProjectTabHeader, bool>? keep = null)
    {
        lock (_gate)
            _forgotten = true;

        var mine = new HashSet<string>(_tabs.Select(t => t.Id), StringComparer.Ordinal);
        var kept = new HashSet<string>(keep is null ? Enumerable.Empty<string>() : _tabs.Where(keep).Select(t => t.Id), StringComparer.Ordinal);
        var others = false;
        try
        {
            if (Directory.Exists(FolderPath))
            {
                var names = Directory.EnumerateFiles(FolderPath).Select(f => Path.GetFileName(f)).ToList();
                others = names.Any(n => IsStateFile(n) && !mine.Contains(IdOf(n)));
                foreach (var name in names)
                {
                    if (string.Equals(name, LockName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Unknown headers/fingerprints may belong to an unknown project file: keep them while one exists.
                    var id = IdOf(name);
                    var go = name.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase) ? kept.Count == 0
                            : mine.Contains(id) ? !kept.Contains(id)
                            : !others);
                    if (go)
                        TryDelete(Path.Combine(FolderPath, name));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder not enumerated: it may hold other work, so keep it and only release the lock.
            others = true;
        }

        ReleaseSession();
        if (kept.Count > 0 || others)
            return;
        TryDelete(Path.Combine(FolderPath, LockName));
        TryDeleteFolder(FolderPath);
    }

    /// <summary>
    /// Adopts tabs from abandoned sessions (any thread, after Frame 1): each other folder whose lock can be taken (no lock file means one
    /// being created) has its tab files moved here in tabs.json order, skipping <paramref name="known"/> ids; headerless project files get
    /// a synthetic header (<see cref="LeftBehind"/>). Fully drained folders are deleted. Returns headers for <see cref="Adopt"/>.
    /// </summary>
    public List<ProjectTabHeader> TakeLeftBehind(IReadOnlyCollection<string> known) => TakeLeftBehind(known, out _);

    /// <summary>
    /// <see cref="TakeLeftBehind(IReadOnlyCollection{string})"/> plus <paramref name="front"/>: the active tab named by the newest kept
    /// manifest (<see cref="FrontOf"/>, <see cref="WriteKeptManifest"/>), or null. Crashed sessions are adopted without a front, so a
    /// project that crashed HandPeg is never reopened automatically.
    /// </summary>
    public List<ProjectTabHeader> TakeLeftBehind(IReadOnlyCollection<string> known, out string? front)
    {
        front = null;
        var newest = DateTime.MinValue;
        var keptSeen = false;
        var taken = new List<ProjectTabHeader>();
        List<string> folders;
        try
        {
            if (!Directory.Exists(RootPath))
                return taken;
            folders = [.. Directory.EnumerateDirectories(RootPath)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return taken;
        }

        var ids = new HashSet<string>(known, StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (string.Equals(Path.GetFileName(folder), Path.GetFileName(FolderPath), StringComparison.OrdinalIgnoreCase))
                continue;

            var lockPath = Path.Combine(folder, LockName);
            FileStream held;
            try
            {
                held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            bool whole;
            using (held)
            {
                // Read the manifest's active tab and kept flag before the sweep deletes tabs.json.
                var manifest = ReadManifest(folder);
                var named = manifest?.Active;
                var here = new HashSet<string>(StringComparer.Ordinal);
                var takenHere = new List<ProjectTabHeader>();
                foreach (var tab in LeftBehind(folder, []))
                {
                    if (!ids.Contains(tab.Id) && TryMoveIn(folder, tab))
                    {
                        taken.Add(tab);
                        takenHere.Add(tab);
                        ids.Add(tab.Id);
                        here.Add(tab.Id);
                    }
                }

                // Multiple abandoned sessions: the newest kept session (moves preserve last-write times) decides the front; if it names
                // no adopted tab (a blank tab was active), no tab is brought forward.
                if (manifest is { Kept: true } && takenHere.Count > 0 && LastWrittenOf(takenHere) is var written && (!keptSeen || written > newest))
                    (front, newest, keptSeen) = (FrontOf(takenHere, named), written, true);
                whole = SweepTaken(folder, here);
            }

            if (!whole)
                continue;
            TryDelete(lockPath);
            TryDeleteFolder(folder);
        }

        return taken;
    }

    /// <summary>
    /// Active tab of an adopted session (pure): <paramref name="named"/> when it was among the taken tabs, else null so the start-up tab
    /// stays active (never a substitute picked by MRU count).
    /// </summary>
    internal static string? FrontOf(IReadOnlyList<ProjectTabHeader> taken, string? named) =>
        named is not null && taken.Any(t => string.Equals(t.Id, named, StringComparison.Ordinal)) ? named : null;

    /// <summary>Newest last-write time among the adopted project files; MinValue when none is readable.</summary>
    private DateTime LastWrittenOf(IReadOnlyList<ProjectTabHeader> taken)
    {
        var newest = DateTime.MinValue;
        foreach (var tab in taken)
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(StateFile(tab.Id));
                if (written > newest)
                    newest = written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        return newest;
    }

    /// <summary>
    /// Moves an abandoned tab into this session: project file (required), fingerprint, then a rewritten header, in <see cref="WriteTab"/>
    /// order, so a crash midway leaves an adoptable orphan and the project file always exists in one folder.
    /// </summary>
    private bool TryMoveIn(string folder, ProjectTabHeader tab)
    {
        try
        {
            ClaimSession();
            File.Move(Path.Combine(folder, tab.Id + StateExtension), StateFile(tab.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // A dirty tab without its fingerprint still resumes as dirty (RestoreUnsavedState).
        try
        {
            var saved = Path.Combine(folder, tab.Id + SavedExtension);
            if (File.Exists(saved))
                File.Move(saved, SavedFile(tab.Id));
            ProjectStore.WriteWhole(HeaderFile(tab.Id), HeaderJson(tab));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return true;
    }

    /// <summary>
    /// Tab id owning a session-folder file: the name minus a known tab extension (and .tmp), else the name up to the first dot (covers
    /// partial writes named <c>&lt;id&gt;.hptab.&lt;32 hex digits&gt;.tmp</c>).
    /// </summary>
    private static string IdOf(string fileName)
    {
        var name = fileName.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase) ? fileName[..^PartialExtension.Length] : fileName;
        foreach (var ending in TabFileEndings)
        {
            if (name.Length > ending.Length && name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return name[..^ending.Length];
        }

        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>True for a complete tab project file (<c>&lt;Id&gt;.hptab</c>, not a partial write).</summary>
    private static bool IsStateFile(string fileName) =>
        fileName.Length > StateExtension.Length && fileName.EndsWith(StateExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cleans an abandoned folder after adopting <paramref name="taken"/>: deletes their old headers, tabs.json and partial writes. Returns
    /// false (folder kept intact) while any project file remains; otherwise deletes stray headers/fingerprints and returns true.
    /// </summary>
    private static bool SweepTaken(string folder, HashSet<string> taken)
    {
        try
        {
            var left = new List<string>();
            foreach (var name in Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)).ToList())
            {
                if (string.Equals(name, LockName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (taken.Contains(IdOf(name)) || name.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase))
                    TryDelete(Path.Combine(folder, name));
                else
                    left.Add(name);
            }

            if (left.Exists(IsStateFile))
                return false;
            foreach (var name in left)
                TryDelete(Path.Combine(folder, name));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Parses tabs.json; null when missing (single-tab session) or damaged.</summary>
    public static ProjectTabManifest? ReadManifest(string folder)
    {
        try
        {
            var path = Path.Combine(folder, ManifestName);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<ProjectTabManifest>(File.ReadAllText(path), HeaderOptions) is { Order: not null } manifest ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tabs in a session folder (any thread): every header with a project file, plus a dirty <see cref="RecoveredTitle"/> header for
    /// each headerless project file (so closing it prompts). Ordered by tabs.json, then oldest first; <paramref name="known"/> ids skipped.
    /// </summary>
    public static List<ProjectTabHeader> LeftBehind(string folder, IReadOnlyCollection<string> known)
    {
        var found = new List<ProjectTabHeader>();
        var projects = new List<string>();
        try
        {
            if (!Directory.Exists(folder))
                return found;
            foreach (var file in Directory.EnumerateFiles(folder).ToList())
            {
                var name = Path.GetFileName(file);
                if (IsStateFile(name))
                {
                    projects.Add(IdOf(name));
                    continue;
                }

                if (!name.EndsWith(HeaderExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                string json;
                try
                {
                    json = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (ReadHeader(json) is { } tab && !known.Contains(tab.Id) && !found.Exists(t => string.Equals(t.Id, tab.Id, StringComparison.Ordinal))
                    && File.Exists(Path.Combine(folder, tab.Id + StateExtension)))
                {
                    found.Add(tab);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (var id in projects)
        {
            if (!known.Contains(id) && !found.Exists(t => string.Equals(t.Id, id, StringComparison.Ordinal)))
                found.Add(new ProjectTabHeader { Id = id, Title = RecoveredTitle, Dirty = true, Empty = false });
        }

        var order = (ReadManifest(folder)?.Order ?? []).ToList();
        int Rank(ProjectTabHeader tab)
        {
            var at = order.IndexOf(tab.Id);
            return at >= 0 ? at : int.MaxValue;
        }

        return [.. found.OrderBy(Rank).ThenBy(t => t.LastActive)];
    }

    /// <summary>Deletes a folder if empty; leaves it when an undeletable file remains.</summary>
    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];
}
