using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// One project tab of the title bar (Pass 5.4A), as small as it is kept in memory and beside its file (<c>&lt;Id&gt;.tab.json</c>, a
/// few kilobytes at most: under 4 KB with its window layout, which is most of it, 5.4D): its name and file, whether it has unsaved
/// changes, where it was looked at (the playhead, the Timeline's zoom and scroll), and the window it was looked at in (5.4D: the 3D
/// Exploded View and its camera, the main window's layout). Its Sequence is not here: a tab behind the one in front is its <c>.hptab</c>
/// file alone (<see cref="ProjectTabs"/>). The window's part is never in the project file (ProjectState): orbiting the camera or
/// dragging a splitter is no change to the project.
/// </summary>
public sealed record ProjectTabHeader
{
    /// <summary>The tab's own id: the name of its files in the cache.</summary>
    public string Id { get; init; } = "";

    /// <summary>What the tab says: the project's name, else its first file's, else New Project.</summary>
    public string Title { get; set; } = "";

    /// <summary>The project file it was opened from or saved to; empty for work never saved.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Whether it had unsaved changes as it was put behind (the tab in front is asked as it is).</summary>
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

    /// <summary>When it was last in front, as a count that only grows (the least recently used tabs lose their stills first).</summary>
    public long LastActive { get; set; }

    /// <summary>The name the New Project studio gave it, for its first save ("" for none): each tab's own, never the next tab's.</summary>
    public string NewProjectName { get; set; } = "";

    /// <summary>The folder the New Project studio chose for its first save (null: the Projects folder).</summary>
    public string? NewProjectFolder { get; set; }

    /// <summary>
    /// The project's frame as it was put behind (Pass 5.4B: the hover card of a tab behind), as Project Settings draws it (a vertical
    /// project tall); 0 while unknown (a tab never put behind, or one left by an older session).
    /// </summary>
    public int FrameWidth { get; set; }

    public int FrameHeight { get; set; }

    /// <summary>The project's frame rate as it was put behind, frames a second; 0 while unknown.</summary>
    public double FrameRate { get; set; }

    /// <summary>
    /// Whether the player was in the 3D Exploded View as the tab was put behind (5.4D): it comes back in it, at once and where its camera
    /// was. False for a header from before 5.4D (the flat picture).
    /// </summary>
    public bool ExplodedOpen { get; set; }

    /// <summary>The 3D view's camera as the tab was put behind (5.4D, GpuPreviewEngine.CameraPose: degrees, times the fitted distance, the planes' spacing); the view's start until then.</summary>
    public double ExplodedYaw { get; set; } = GpuPreviewEngine.DefaultYaw;

    public double ExplodedPitch { get; set; } = GpuPreviewEngine.DefaultPitch;

    public double ExplodedZoom { get; set; } = GpuPreviewEngine.DefaultZoom;

    public double ExplodedSpacing { get; set; } = GpuPreviewEngine.DefaultSpacing;

    /// <summary>
    /// The main window's layout as the tab was put behind (5.4D, AppSettings.PerTabWindowLayouts: DockManager.Save's JSON, the side windows
    /// never part of it), laid out again as it comes back to the front; null for none (the setting off, a tab never put behind, or a
    /// header from before 5.4D): the tab takes the window as it is.
    /// </summary>
    public string? LayoutJson { get; set; }

    /// <summary>
    /// The Media Browser's view with that layout (5.4D review: its MediaBrowserView's name, the Filmstrip a Vertical layout chose), set
    /// again before the layout is laid out; null for none (as <see cref="LayoutJson"/>): the view stays as it is.
    /// </summary>
    public string? MediaView { get; set; }

    /// <summary>The window's part of another tab's header (5.4D: a duplicate): its 3D view, camera and layout (with its Media Browser view).</summary>
    public void TakeWindowStateOf(ProjectTabHeader other)
    {
        (ExplodedOpen, ExplodedYaw, ExplodedPitch, ExplodedZoom, ExplodedSpacing) =
            (other.ExplodedOpen, other.ExplodedYaw, other.ExplodedPitch, other.ExplodedZoom, other.ExplodedSpacing);
        (LayoutJson, MediaView) = (other.LayoutJson, other.MediaView);
    }

    /// <summary>
    /// A Game Preset chosen before any clip, still waiting for the tab's first file (the view model's own, opaque here): kept with the
    /// header in memory only, never written (it is small, and a tab left by a session that ended without closing starts without it).
    /// </summary>
    [JsonIgnore]
    public object? WaitingTemplate { get; set; }
}

/// <summary>The order of the tabs and the one in front, as tabs.json keeps them (for tabs left behind by a session that did not close cleanly).</summary>
public sealed record ProjectTabManifest(IReadOnlyList<string> Order, string? Active);

/// <summary>
/// The title bar's project tabs (Pass 5.4A): their order, the one in front, and their files in <c>%Cache%\project-tabs\&lt;session&gt;</c>.
/// Headless and pure but for the disk and a frozen picture per tab: the window drives it (MainWindow.ProjectTabs.cs). One view model holds
/// the tab in front; every other tab is <c>&lt;Id&gt;.hptab</c> (a project file of its own, written by ProjectStore with its undo history),
/// its header <c>&lt;Id&gt;.tab.json</c>, and for one with unsaved changes <c>&lt;Id&gt;.saved</c> (what its saved state looked like). Two
/// tiers: the <see cref="MaxHotTabs"/> most recently in front keep a frozen still of their paused picture in memory (shown at once as one
/// comes back); the rest keep only their header. Nothing here touches the disk until asked (the window's first frame makes one tab in memory).
/// Every HandPeg running has a folder of its own under the root, held by <c>session.lock</c> (open, shared with nobody, for as long as the
/// session lives): another HandPeg's tabs are never offered here nor deleted, and a folder whose lock can be taken is one a session left
/// behind as it ended without closing (or closed keeping unsaved tabs), whose tabs the next session takes in (<see cref="TakeLeftBehind"/>).
/// Pass 5.4D-TabPerf: the window keeps the tabs brought to the front this session live in memory as well (MainWindow's tab contexts), and
/// their files are then the crash recovery, written after a switch; each tab's work on its files runs in order on the thread pool
/// (<see cref="Enqueue(string, Action)"/>), so a write, a delete and a read of one tab never pass each other.
/// </summary>
public sealed class ProjectTabs
{
    /// <summary>
    /// How many tabs, the most recently in front, keep their paused picture in memory here. Pass 5.4D-TabPerf: a tab kept in memory whole
    /// (MainWindow's live tab contexts) keeps its still with it, whatever this says; these are the stills of tabs only in their files.
    /// </summary>
    public const int MaxHotTabs = 3;

    public const string StateExtension = ".hptab", HeaderExtension = ".tab.json", SavedExtension = ".saved", ManifestName = "tabs.json", LockName = "session.lock";

    /// <summary>What a tab left behind as a project file without a header that can be read is called as it is taken in (<see cref="LeftBehind"/>).</summary>
    public const string RecoveredTitle = "Recovered Project";

    // The end of a file being written whole (ProjectStore.WriteWhole): half of one at most, never the only copy of a tab.
    private const string PartialExtension = ".tmp";

    private static readonly string[] TabFileEndings = [StateExtension, HeaderExtension, SavedExtension];

    private static readonly JsonSerializerOptions HeaderOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly List<ProjectTabHeader> _tabs = [];
    private readonly Dictionary<string, BitmapSource> _stills = new(StringComparer.Ordinal);
    private long _clock;

    // Each tab's work on the disk, one after another (Pass 5.4D-TabPerf): its cache file written after a switch, deleted as it comes back
    // or closes, its header written again, its file read or copied. The last of each tab's chain, under _gate.
    private readonly Dictionary<string, Task> _io = new(StringComparer.Ordinal);

    // The session's lock on its folder, taken as the first file is written (never before the window's first frame), and the gate of it:
    // tabs are written from the thread pool and the window's thread alike.
    private readonly Lock _gate = new();
    private FileStream? _lock;

    // The session's files let go as HandPeg closed (ForgetSession), under _gate (5.4D-TabPerf review): nothing is written into its folder
    // after that, so a write still running on the thread pool can never make the folder, its lock or a tab's file again.
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

    /// <summary>This session's own folder: the only one it writes in or deletes from (but for the folders it takes tabs in from).</summary>
    public string FolderPath { get; }

    /// <summary>The tabs in their order along the title bar.</summary>
    public IReadOnlyList<ProjectTabHeader> Tabs => _tabs;

    public int Count => _tabs.Count;

    // How many tabs the strip holds, for the player's frame pump (5.4.4): written on the window's thread as a tab is added or closed.
    private static int _openCount;

    /// <summary>
    /// How many project tabs are open (5.4.4): Dynamic Preview Scaling starts smaller on a card that shares the PC's memory while more than one
    /// is open (GpuPreviewEngine.StartStep). Written wherever the strip gains or loses a tab; read on any thread.
    /// </summary>
    public static int OpenCount
    {
        get => Volatile.Read(ref _openCount);
        private set => Volatile.Write(ref _openCount, value);
    }

    /// <summary>The tab in front: the one the view model holds. The scheduler is told (5.4.1-M3): the work of the tabs behind waits behind its.</summary>
    public ProjectTabHeader Active
    {
        get;
        private set
        {
            field = value;
            WorkScheduler.FrontTab = value.Id;
        }
    }

    /// <summary>A new tab at the end of the strip, not in front yet.</summary>
    public ProjectTabHeader Add(string title, string? id = null)
    {
        var tab = new ProjectTabHeader { Id = id ?? NewId(), Title = title };
        _tabs.Add(tab);
        OpenCount = _tabs.Count;
        return tab;
    }

    /// <summary>A new tab at <paramref name="index"/> along the strip (Pass 5.4B: a duplicate, right after its original), not in front yet.</summary>
    public ProjectTabHeader Insert(int index, string title, string? id = null)
    {
        var tab = new ProjectTabHeader { Id = id ?? NewId(), Title = title };
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        OpenCount = _tabs.Count;
        return tab;
    }

    /// <summary>
    /// A tab dragged along the strip (Pass 5.4B): taken out and put back at <paramref name="index"/> (clamped to the strip). The tab in
    /// front, the most recently used and the stills are as they were; Ctrl+Tab, the neighbour a close brings forward and tabs.json all
    /// follow the new order.
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
    /// A tab from a session that did not close cleanly, put at the end (cold: no still), unless one of that id is here already. It ranks
    /// below every tab of this session in the most recently used (its last session's count means nothing here), so it never takes the
    /// still of a tab that was in front in this one.
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
    /// New work that came in front of a tab that could not be brought back (MainWindow.SplitFromUnresumed): a new tab for it, put in
    /// front as it is (the view model holds it already); the tab that was in front stays on the strip, behind, its files untouched.
    /// </summary>
    public ProjectTabHeader TakeWorkOff(string title)
    {
        var fresh = Add(title);
        Activate(fresh);
        return fresh;
    }

    /// <summary>Puts a tab in front (the window has put the last one behind and brings this one back).</summary>
    public void Activate(ProjectTabHeader tab)
    {
        if (!_tabs.Contains(tab))
            throw new ArgumentException("The tab is not one of these.", nameof(tab));
        Active = tab;
        Touch(tab);
    }

    /// <summary>
    /// Marks a tab as the most recently used, and lets go of the stills of the tabs beyond the <see cref="MaxHotTabs"/> most recently
    /// used: a tab that has not been in front for a while is its header alone in memory.
    /// </summary>
    public void Touch(ProjectTabHeader tab)
    {
        tab.LastActive = ++_clock;
        foreach (var cold in _tabs.OrderByDescending(t => t.LastActive).Skip(MaxHotTabs))
            _stills.Remove(cold.Id);
    }

    /// <summary>Whether a tab is among the <see cref="MaxHotTabs"/> most recently used.</summary>
    public bool IsHot(ProjectTabHeader tab) => _tabs.OrderByDescending(t => t.LastActive).Take(MaxHotTabs).Contains(tab);

    /// <summary>Keeps a tab's paused picture (frozen here if it was not), while the tab is hot; null lets it go.</summary>
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

    /// <summary>The tab that comes to the front when <paramref name="tab"/> is closed: the one after it, or the one before at the end.</summary>
    public ProjectTabHeader? NeighbourOf(ProjectTabHeader tab)
    {
        var at = _tabs.IndexOf(tab);
        if (at < 0 || _tabs.Count < 2)
            return null;
        return at + 1 < _tabs.Count ? _tabs[at + 1] : _tabs[at - 1];
    }

    /// <summary>
    /// Takes a tab off the strip and deletes its files. The tab in front is never left without one: close it only after another is in
    /// front (<see cref="NeighbourOf"/>); with a single tab the window empties it instead.
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

    // ----- Each tab's disk work, in order (Pass 5.4D-TabPerf) -----

    /// <summary>
    /// Work on a tab's files, on the thread pool after everything asked before for the same tab (any thread): a cache file written after
    /// a switch is never deleted before it is written, nor written again after a delete asked later. What <paramref name="io"/> throws is
    /// the task's; the chain goes on after it.
    /// </summary>
    public Task Enqueue(string id, Action io) => Enqueue<bool>(id, () =>
    {
        io();
        return true;
    });

    /// <summary><see cref="Enqueue(string, Action)"/>, with what the work gives back (a file read after the writes asked before it).</summary>
    public Task<T> Enqueue<T>(string id, Func<T> io)
    {
        Task<T> next;
        lock (_gate)
        {
            var before = _io.GetValueOrDefault(id) ?? Task.CompletedTask;
            next = before.ContinueWith(_ => io(), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _io[id] = next;
        }

        // The chain forgotten once its last work is done (outside the gate: done already, this runs here at once).
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

    /// <summary>The last work asked for a tab's files (done at once when there is none): awaited before its files are relied on.</summary>
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
    /// Waits (blocking: as HandPeg closes, and for a save from memory) until the work asked for a tab's files, or every tab's, is done, or
    /// <paramref name="timeoutMs"/> has gone. Whether it is all done (a failed write counts as done: its tab keeps what it holds in memory).
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
    /// A tab's three files deleted after the work asked before for it (any thread): at once when there is none, so a tab with nothing
    /// waiting goes as it always did; a failure is left for the clean close (<see cref="DeleteFiles"/>).
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

    /// <summary>A header as its file holds it (made on the window's thread, written on any).</summary>
    public static string HeaderJson(ProjectTabHeader tab) => JsonSerializer.Serialize(tab, HeaderOptions);

    /// <summary>A header read back; null when the text is not one (damaged, or of something else).</summary>
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
    /// Writes a tab put behind (any thread: everything it reads is given): its project file (ProjectStore, atomic, with the undo
    /// history the state carries), its saved state's fingerprint when it has unsaved changes (none deletes an old one), and its header.
    /// </summary>
    public void WriteTab(string id, ProjectState state, string? savedFingerprint, string headerJson)
    {
        // The project file first, the header last (5.4A review): a session that ends between them leaves a project file without a
        // header, which the next start takes in under a made-up one (LeftBehind: "Recovered Project", with unsaved changes), never
        // deletes. The other way round a header would name a project file not there yet, and the work would be in neither.
        ClaimSession();
        ProjectStore.SaveTo(state, StateFile(id));

        // The session let go while it was being written (a write that outlived the close's wait): taken out again, never offered back.
        if (IsForgotten)
        {
            TryDelete(StateFile(id));
            throw new IOException(ForgottenMessage);
        }

        if (savedFingerprint is { Length: > 0 })
            ProjectStore.WriteWhole(SavedFile(id), savedFingerprint);
        else
            TryDelete(SavedFile(id));
        ProjectStore.WriteWhole(HeaderFile(id), headerJson);
    }

    /// <summary>
    /// A duplicate of a tab kept in its file (Pass 5.4B, any thread): <paramref name="fromId"/>'s project file copied whole (copied beside,
    /// then moved in, as a file written whole is) to <paramref name="toId"/>'s, no saved fingerprint (a duplicate was never saved), and
    /// its header last, as <see cref="WriteTab"/> writes them.
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
    /// A tab behind's header written again after it changed in memory (Pass 5.4B: renamed), when it has a project file (any thread: the
    /// text is made on the window's). A tab with none keeps nothing on the disk to bring up to date.
    /// </summary>
    public void WriteHeader(string id, string headerJson)
    {
        if (!HasState(id))
            return;
        ClaimSession();
        ProjectStore.WriteWhole(HeaderFile(id), headerJson);
    }

    /// <summary>A project's frame as Project Settings draws it (pure): a vertical project's natural frame turned tall.</summary>
    public static (int Width, int Height) FrameOf(int naturalWidth, int naturalHeight, bool vertical) =>
        vertical ? (naturalHeight, naturalWidth) : (naturalWidth, naturalHeight);

    /// <summary>A tab's frame in a line for its hover card (pure): "1920 x 1080 · 60 fps"; "" while it is not known.</summary>
    public static string DescribeFrame(int width, int height, double frameRate)
    {
        var size = width > 0 && height > 0 ? $"{width} x {height}" : "";
        var fps = frameRate > 0 ? frameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " fps" : "";
        return size.Length > 0 && fps.Length > 0 ? $"{size} · {fps}" : size + fps;
    }

    /// <summary>A tab's project as it was put behind (any thread).</summary>
    public ProjectState ReadState(string id) => ProjectStore.Load(StateFile(id));

    /// <summary>A tab's saved state's fingerprint; null when it has none (it had no unsaved changes) or it cannot be read.</summary>
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

    /// <summary>Deletes a tab's three files (any thread); one that cannot be deleted is left for the clean close.</summary>
    public void DeleteFiles(string id)
    {
        TryDelete(StateFile(id));
        TryDelete(HeaderFile(id));
        TryDelete(SavedFile(id));
    }

    /// <summary>The order and the tab in front, as tabs.json keeps them.</summary>
    public string ManifestJson() => JsonSerializer.Serialize(new ProjectTabManifest(_tabs.Select(t => t.Id).ToList(), Active.Id), HeaderOptions);

    /// <summary>tabs.json written (any thread: the text is made on the window's, <see cref="ManifestJson"/>), in this session's folder.</summary>
    public void WriteManifest(string json)
    {
        ClaimSession();
        ProjectStore.WriteWhole(ManifestFile, json);
    }

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

    /// <summary>Whether this session's files were let go as HandPeg closed (<see cref="ForgetSession"/>): nothing is written after that.</summary>
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
    /// This session's folder made and its lock taken, if they were not (any thread): open and shared with nobody for as long as the
    /// session lives, which is what tells another HandPeg the folder is in use. An IOException when it cannot be, and once the session
    /// was let go (<see cref="ForgetSession"/>, 5.4D-TabPerf review: a late write never makes the folder or its lock again).
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

    /// <summary>The lock let go, the files left as they are: the folder is then one the next session takes the tabs in from.</summary>
    public void ReleaseSession()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    /// <summary>
    /// As HandPeg closes: the files of this session's tabs deleted, but for those of the tabs <paramref name="keep"/> picks (with the
    /// prompt to save on exit off, the tabs behind with unsaved changes), and its lock let go. Only the files of the tabs it knows go (with
    /// tabs.json and half-written files): a project file of no tab of its own (one moved in from a dead session that never reached the
    /// strip) is never deleted (5.4A review). With nothing kept and no such file the folder goes too; otherwise it stays, with its lock
    /// file (and tabs.json when a tab was kept), so the next start offers what is left again. Never touches another session's folder.
    /// From then on nothing is written into the folder (<see cref="IsForgotten"/>: a write still running is refused, 5.4D-TabPerf review).
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

                    // A header or saved fingerprint of no tab of this session's is kept while a project file of no tab of its own is
                    // there (it may be that one's), and goes with the folder otherwise (without its project file it is nothing).
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
            // Not looked through: nothing says the folder holds no other work, so it stays, its lock let go.
            others = true;
        }

        ReleaseSession();
        if (kept.Count > 0 || others)
            return;
        TryDelete(Path.Combine(FolderPath, LockName));
        TryDeleteFolder(FolderPath);
    }

    /// <summary>
    /// The tabs other sessions left behind (any thread; after the window's first frame), taken into this session's folder: every folder
    /// under the root but this one whose lock can be taken (its session is gone; a live one holds its own, and a folder with no lock file
    /// is one being made this moment) has its tabs' files moved here, in tabs.json's order, leaving out ids that are <paramref name="known"/>
    /// or already taken; a project file without a header that can be read comes across under a made-up one (<see cref="LeftBehind"/>). A
    /// folder whose every project file came across is deleted; one that kept any (never deleted: 5.4A review) stays, with its lock file,
    /// for the next start. The headers come back for the window to <see cref="Adopt"/>.
    /// </summary>
    public List<ProjectTabHeader> TakeLeftBehind(IReadOnlyCollection<string> known)
    {
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
                var here = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tab in LeftBehind(folder, []))
                {
                    if (!ids.Contains(tab.Id) && TryMoveIn(folder, tab))
                    {
                        taken.Add(tab);
                        ids.Add(tab.Id);
                        here.Add(tab.Id);
                    }
                }

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
    /// A left-behind tab's files moved into this session's folder: its project file (without which it is not taken), its saved
    /// fingerprint, its header written anew. The project file first and the header last, as <see cref="WriteTab"/>: a session that ends
    /// between them has the project file here without a header, which the next start takes in under a made-up one, and the old header
    /// left in the dead folder, which names no project file there and goes with it. Never a moment with the project file in neither.
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

        // Without its saved fingerprint a tab with unsaved changes still comes back unsaved (RestoreUnsavedState): never lost.
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
    /// The tab id a file of a session's folder belongs to: its name less a tab file's ending (and a half-written file's .tmp after it),
    /// else its name up to the first dot (a half-written file's own name, <c>&lt;id&gt;.hptab.&lt;32 hex digits&gt;.tmp</c>, among them).
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

    /// <summary>Whether a file of a session's folder is a tab's project file (<c>&lt;Id&gt;.hptab</c>, not a half-written one).</summary>
    private static bool IsStateFile(string fileName) =>
        fileName.Length > StateExtension.Length && fileName.EndsWith(StateExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What is left of a dead session's folder once its tabs were taken (<paramref name="taken"/>, their files moved here): the rest of
    /// their files (their old headers), tabs.json and half-written files deleted. A project file still there (a tab that could not come
    /// across) is never deleted (5.4A review): false then, and the folder stays with everything else in it. With none left, the headers
    /// and saved fingerprints of no project file go too (nothing without one), and true: the folder may go.
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

    /// <summary>tabs.json read back; null when it is not there or cannot be read (a session that never had a second tab, a damaged file).</summary>
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
    /// The tabs a session left in its folder (any thread; <see cref="TakeLeftBehind"/> reads each dead session's with it): every header
    /// whose project file is there, and for a project file without a header that can be read (its session ended between writing the two,
    /// or the header is damaged) a made-up one: <see cref="RecoveredTitle"/>, with unsaved changes, so closing it asks first (5.4A review).
    /// In tabs.json's order where it names them (the rest after, oldest first), leaving out <paramref name="known"/> ids.
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

    /// <summary>An empty folder deleted; one still holding a file that could not be deleted is left.</summary>
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
