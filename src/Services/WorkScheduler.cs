using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Services;

/// <summary>Work lanes in priority order; each lane has its own concurrency limit.</summary>
public enum WorkLane
{
    /// <summary>Exports, the queue and the Squisher.</summary>
    Foreground,

    /// <summary>What the user is waiting for: loading a file, probes, captions, Dead Air.</summary>
    Interactive,

    /// <summary>What makes the timeline nicer to work with: thumbnails, waveforms, I-frame lists, proxies.</summary>
    Background,

    /// <summary>What can wait for a quiet moment: folder indexing, cache purges, the encoder probe, update checks.</summary>
    Idle,
}

public enum WorkState { Queued, Running, Done, Failed, Cancelled }

/// <summary>Work descriptor: flyout title, lane, and dedup key.</summary>
/// <param name="Title">Progress chip/flyout text, e.g. "Thumbnails of clip.mp4".</param>
public sealed record WorkSpec(string Title, WorkLane Lane)
{
    /// <summary>Dedup key: a request matching a queued or running item joins it and shares its result.</summary>
    public string? Key { get; init; }

    /// <summary>Whether the flyout offers to cancel it.</summary>
    public bool Cancellable { get; init; } = true;

    /// <summary>Listed in the progress UI; short chores (probes) run unlisted.</summary>
    public bool Visible { get; init; } = true;

    /// <summary>Its tools are stopped where they are while the player plays, and go on after (a proxy being made).</summary>
    public bool SuspendWhilePlaying { get; init; }

    /// <summary>
    /// Urgent work a user action is blocked on (e.g. probing a dropped file): starts immediately, bypassing the queue and lane limit,
    /// never paused or held. Use for short work, once per action.
    /// </summary>
    public bool Urgent { get; init; }
}

/// <summary>What a piece of work is given: its cancellation, and where to say how far it has got.</summary>
public sealed class WorkContext
{
    private readonly WorkItem? _item;

    internal WorkContext(WorkItem? item, CancellationToken token) => (_item, Token) = (item, token);

    public CancellationToken Token { get; }

    /// <summary>How far it has got, 0 to 1; a negative number when that cannot be said.</summary>
    public void Report(double fraction) => _item?.SetProgress(fraction);

    /// <summary>A word on what it is doing now: "12 of 48 files".</summary>
    public void Report(string detail) => _item?.SetDetail(detail);

    public void Report(double fraction, string detail)
    {
        Report(fraction);
        Report(detail);
    }
}

/// <summary>
/// Work item shown in the progress flyout. Bindable properties are UI-thread only; workers write plain fields that the
/// scheduler timer copies at 10 Hz.
/// </summary>
public sealed partial class WorkItem : ObservableObject
{
    internal WorkItem(WorkSpec spec, CancellationTokenSource cancellation) =>
        (Title, Lane, Key, Cancellable, Visible, SuspendWhilePlaying, Urgent, Cancellation) = (spec.Title, spec.Lane, spec.Key, spec.Cancellable, spec.Visible, spec.SuspendWhilePlaying, spec.Urgent, cancellation);

    internal bool SuspendWhilePlaying { get; }

    // Owning tab at request time; while another tab is active, this item queues behind that tab's work in its lane.
    internal string? Tab { get; init; }

    // Urgent (WorkSpec.Urgent): starts immediately past the lane limit; also set when an urgent request joins existing work.
    internal bool Urgent { get; set; }

    // The tools started for it that are running, and whether they are stopped now.
    internal readonly List<int> Processes = [];
    internal bool Suspended;

    // Lane slot released while paused between pieces (WhenBackgroundMayRunAsync); reacquired via Pump within the lane limit. Guarded by the gate.
    internal bool SlotGivenBack;

    public string Title { get; }
    public WorkLane Lane { get; }
    public string? Key { get; }
    public bool Cancellable { get; }
    public bool Visible { get; }

    internal CancellationTokenSource Cancellation { get; }
    internal Task? Task { get; set; }
    internal Func<Task>? Start { get; set; }

    // Requested by foreground/interactive work (or urgent): ignores pauses and its tools are never held (HoldBackground).
    internal bool BypassPause { get; set; }

    // Captured context: UI-thread requests start on the UI thread; pool requests start on the pool.
    internal SynchronizationContext? Context { get; init; }

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress = -1;
    [ObservableProperty] private WorkState _state = WorkState.Queued;
    [ObservableProperty] private string _elapsed = "";

    public bool IsIndeterminate => Progress < 0;
    public double Percent => Math.Clamp(Progress, 0, 1) * 100;
    public bool IsActive => State is WorkState.Queued or WorkState.Running;
    public bool CanCancel => Cancellable && IsActive;

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Percent));
    }

    partial void OnStateChanged(WorkState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanCancel));
    }

    // Written by the work, read by the timer.
    private long _reportedProgress = BitConverter.DoubleToInt64Bits(-1);
    private string? _reportedDetail;
    private int _phase = (int)WorkState.Queued;
    internal long StartedAt, FinishedAt;

    internal void SetProgress(double fraction) =>
        Interlocked.Exchange(ref _reportedProgress, BitConverter.DoubleToInt64Bits(double.IsFinite(fraction) && fraction >= 0 ? Math.Min(fraction, 1) : -1));

    internal void SetDetail(string detail) => Volatile.Write(ref _reportedDetail, detail);

    internal void SetPhase(WorkState state)
    {
        if (state == WorkState.Running)
            StartedAt = Environment.TickCount64;
        else if (state is WorkState.Done or WorkState.Failed or WorkState.Cancelled)
            FinishedAt = Environment.TickCount64;
        Volatile.Write(ref _phase, (int)state);
    }

    internal WorkState Phase => (WorkState)Volatile.Read(ref _phase);

    /// <summary>Copies what the work has written into what the flyout shows. UI thread only.</summary>
    internal void Sync()
    {
        State = Phase;
        Progress = State == WorkState.Done ? 1 : BitConverter.Int64BitsToDouble(Interlocked.Read(ref _reportedProgress));
        if (Volatile.Read(ref _reportedDetail) is { } detail && detail != Detail)
            Detail = detail;

        var seconds = StartedAt == 0 ? 0 : ((FinishedAt == 0 ? Environment.TickCount64 : FinishedAt) - StartedAt) / 1000;
        Elapsed = State == WorkState.Queued ? "waiting" : seconds < 60 ? $"{seconds} s" : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>The flyout's Cancel (only for cancellable work).</summary>
    public void Cancel() => Cancel(evenIfNotCancellable: false);

    /// <summary>Cancels the work; <paramref name="evenIfNotCancellable"/> when no caller waits any longer.</summary>
    internal void Cancel(bool evenIfNotCancellable)
    {
        if (!Cancellable && !evenIfNotCancellable)
            return;

        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Joined caller count, reserved and released under the scheduler's Gate; the work is cancelled when the last cancellable caller cancels.
    internal int Waiters;

    // Set under the Gate when the last caller left: the work is about to be cancelled and is never handed to a new caller (Codex UGLY-08).
    internal bool Closing;
}

/// <summary>What the progress chip at the bottom left shows: the lead piece of work, and how many more there are.</summary>
public sealed partial class WorkSummary : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private string _moreText = "";

    /// <summary>Only background work: the chip is drawn quieter.</summary>
    [ObservableProperty] private bool _isBackground;

    /// <summary>One line per piece of work, for the chip's tooltip.</summary>
    [ObservableProperty] private string _toolTip = "";

    /// <summary>Reason background work is waiting (e.g. playback), or empty.</summary>
    [ObservableProperty] private string _pausedNote = "";
}

/// <summary>
/// Central scheduler for every external tool and long-running task. Lanes and limits:
///   - Foreground (exports, queue, Squisher): 2;
///   - Interactive (loading, probes, captions, Dead Air): 2;
///   - Background (thumbnails, waveforms, I-frame lists, proxies): Settings; Auto = one per three unreserved cores, max 8;
///   - Idle (indexing, purges, encoder probe, update checks): 1.
/// Queued Background/Idle work waits during playback and foreground work (Settings, default on) and while paused in the flyout;
/// running work continues except piecewise work, which stops between pieces (<see cref="PausePoint"/>, also during scrub holds).
/// During drag-and-drop (<see cref="HoldBackground()"/>) it also waits and running background tools are suspended until the drop
/// completes. Urgent work bypasses queue and limits. Keyed requests dedupe. Spawned tools join the lane's job (<see cref="JobObjects"/>).
/// Nested requests at the caller's priority or higher run inline to avoid deadlocking on the caller's own lane slot.
/// </summary>
public static class WorkScheduler
{
    private static readonly object Gate = new();
    private static readonly List<WorkItem> Waiting = [];
    private static readonly List<WorkItem> Live = [];
    private static readonly int[] Running = new int[4];
    private static readonly bool[] UserPaused = new bool[4];
    private static readonly AsyncLocal<WorkItem?> Current = new();
    private static Dispatcher? _ui;
    private static DispatcherTimer? _timer;
    private static volatile bool _playing;

    /// <summary>Every piece of work being shown: waiting, running, or finished a moment ago. UI thread only.</summary>
    public static ObservableCollection<WorkItem> Items { get; } = [];

    /// <summary>The progress chip's summary of <see cref="Items"/>. UI thread only.</summary>
    public static WorkSummary Summary { get; } = new();

    /// <summary>
    /// Concurrent GPU-decoding background tools (one per thumbnail strip regardless of inputs); drivers cap decode sessions.
    /// </summary>
    public static SemaphoreSlim GpuDecodeGate { get; } = new(4, 4);

    /// <summary>
    /// Global cap (2) on background decoding FFmpeg runs (strips, posters, hover previews, waveforms) regardless of lane limits: a strip
    /// opens one decoder per frame (15 HEVC decoders, hundreds of MB), and many tabs could otherwise run a dozen FFmpegs. Acquired inside
    /// <see cref="GpuDecodeGate"/>, never around it (ProcessPipes.RunGatedAsync).
    /// </summary>
    public static SemaphoreSlim ToolGate { get; } = new(2, 2);

    /// <summary>
    /// Active tab id (ProjectTabs.Active); other tabs' work queues behind it in each lane. Null before any tab (no distinction).
    /// </summary>
    public static string? FrontTab
    {
        get => Volatile.Read(ref _frontTab);
        set
        {
            if (Interlocked.Exchange(ref _frontTab, value) != value)
                Pump();
        }
    }

    private static string? _frontTab;

    /// <summary>Starts the UI side (summary and timer) and applies settings. UI thread, once.</summary>
    public static void Attach(Dispatcher dispatcher)
    {
        _ui = dispatcher;
        ApplySettings();
        JobObjects.FavourInterfaceThread();
    }

    /// <summary>The Performance settings changed: the tool jobs take them, and work that may start now does.</summary>
    public static void ApplySettings()
    {
        var settings = AppSettings.Current;
        JobObjects.Configure(settings.EffectiveReservedCores, settings.BackgroundPriority == AppSettings.PriorityIdle);
        TakeSettings();
    }

    /// <summary>
    /// Starts eligible work, then re-evaluates paused piecewise work (e.g. disabling "Pause background work while playing" mid-play
    /// resumes strips). Runs outside the gate; resumptions complete tasks whose continuations run on the pool, never inline.
    /// </summary>
    internal static void TakeSettings()
    {
        Pump();
        RaisePauseChanged();
    }

    /// <summary>How much of a lane's work may run at once.</summary>
    public static int LaneLimit(WorkLane lane) => lane switch
    {
        WorkLane.Foreground or WorkLane.Interactive => 2,
        WorkLane.Background => AppSettings.Current.BackgroundJobs > 0
            ? Math.Min(AppSettings.Current.BackgroundJobs, 16)
            : AutoBackgroundJobs(Environment.ProcessorCount, AppSettings.Current.EffectiveReservedCores),
        _ => 1,
    };

    /// <summary>
    /// Auto background limit: one per three unreserved logical cores, clamped to 1..<see cref="MostAutoBackgroundJobs"/> (6-8 on 24-32 threads).
    /// </summary>
    public static int AutoBackgroundJobs(int processors, int reserved) => Math.Clamp((processors - reserved) / 3, 1, MostAutoBackgroundJobs);

    public const int MostAutoBackgroundJobs = 8;

    /// <summary>
    /// Canonical path for work keys (full, case-normalized) so differently spelled requests for one file dedupe.
    /// </summary>
    public static string PathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"')).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }

    /// <summary>Playback state: queued background work waits (Settings) and opted-in running work is suspended.</summary>
    public static bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value)
                return;

            _playing = value;
            Pump();
            ApplySuspensions();
            RaisePauseChanged();
        }
    }

    // ----- Cooperative pausing of piecewise background work -----
    // Piecewise background/idle work (GpuProgramStrip, GpuFilmstrips) checks between pieces whether to pause: during playback ("Pause
    // background work while playing", default on) and while background is held (scrub, drag-and-drop). Paused work releases its lane
    // slot and decode permit and waits for PauseChanged; work a user is waiting on never pauses.

    /// <summary>True when piecewise background work should pause (playback per Settings, or a background hold).</summary>
    public static bool BackgroundPaused => IsHeld || (_playing && AppSettings.Current.PauseBackgroundWhilePlaying);

    /// <summary>Raised on any thread when <see cref="BackgroundPaused"/> may have changed.</summary>
    public static event Action? PauseChanged;

    private static void RaisePauseChanged()
    {
        try
        {
            PauseChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Background work's pause", ex);
        }
    }

    /// <summary>
    /// Between-piece pause check bound to the current work item: true for background/idle work nobody waits on while
    /// <see cref="BackgroundPaused"/>. Always false outside scheduled work (goldens, tools).
    /// </summary>
    internal static Func<bool> PausePoint()
    {
        if (Current.Value is not { } item || item.Lane < WorkLane.Background || item.BypassPause)
            return static () => false;
        return () => !item.BypassPause && BackgroundPaused;
    }

    /// <summary>
    /// Awaits the end of <see cref="BackgroundPaused"/> from paused piecewise work, releasing its lane slot meanwhile. The slot is reacquired
    /// only when the lane has room (Pump serves resumptions before unstarted work, which never takes reserved room), so the limit holds;
    /// concurrent pieces share one slot. On cancellation it throws with the slot released (never released twice). Returns immediately when not paused.
    /// </summary>
    public static async Task WhenBackgroundMayRunAsync(CancellationToken token)
    {
        if (!BackgroundPaused)
            return;

        var item = Current.Value is { Lane: >= WorkLane.Background } running ? running : null;
        if (item is not null)
        {
            lock (Gate)
            {
                if (!item.SlotGivenBack)
                {
                    item.SlotGivenBack = true;
                    Running[(int)item.Lane]--;
                    Stopped[(int)item.Lane]++;
                }
            }

            Pump();
        }

        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Look()
        {
            if (!BackgroundPaused)
                resumed.TrySetResult();
        }

        PauseChanged += Look;
        try
        {
            using (token.Register(() => resumed.TrySetCanceled(token)))
            {
                Look();
                await resumed.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            PauseChanged -= Look;
        }

        if (item is null)
            return;

        // Reacquire the lane slot unless another piece of the same work already did.
        var room = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (!item.SlotGivenBack)
                return;
            Resuming.Add((item, room));
        }

        Pump();
        using (token.Register(() =>
        {
            lock (Gate)
                Resuming.RemoveAll(r => ReferenceEquals(r.Room, room));
            room.TrySetCanceled(token);
        }))
        {
            await room.Task.ConfigureAwait(false);
        }
    }

    // Resumable paused work awaiting lane room; Pump serves these first. Guarded by the gate.
    private static readonly List<(WorkItem Item, TaskCompletionSource Room)> Resuming = [];

    // Per lane: paused work whose slot is released; room is reserved for it when the pause ends.
    private static readonly int[] Stopped = new int[4];

    /// <summary>Golden-test probe: slot-holding work and paused work per lane. Not used by the app.</summary>
    internal static (int Running, int Stopped) LaneForTests(WorkLane lane)
    {
        lock (Gate)
            return (Running[(int)lane], Stopped[(int)lane]);
    }

    /// <summary>
    /// Reassigns the lane slot to paused work (<paramref name="counted"/>; not when it ended while paused) and resumes its waiting pieces.
    /// Under the gate.
    /// </summary>
    private static void GoOnLocked(WorkItem item, bool counted)
    {
        if (!item.SlotGivenBack)
            return;
        item.SlotGivenBack = false;
        Stopped[(int)item.Lane]--;
        if (counted)
            Running[(int)item.Lane]++;
        for (var i = Resuming.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Resuming[i].Item, item))
            {
                Resuming[i].Room.TrySetResult();
                Resuming.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Suspends or resumes running tools per current state: opted-in work during playback, background/idle work during a drop hold
    /// (never work foreground/interactive work waits on). Each item recomputes under its own lock, so the last call on any thread wins.
    /// </summary>
    private static void ApplySuspensions()
    {
        List<WorkItem> running;
        lock (Gate)
            running = Live.Where(i => i.Phase == WorkState.Running && (i.SuspendWhilePlaying || i.Lane >= WorkLane.Background)).ToList();

        foreach (var item in running)
        {
            bool changed, suspend, playing;
            lock (item.Processes)
            {
                playing = item.SuspendWhilePlaying && _playing;
                suspend = playing || (IsHeld && item.Lane >= WorkLane.Background && !item.BypassPause);
                changed = item.Suspended != suspend;
                if (changed)
                {
                    item.Suspended = suspend;
                    foreach (var process in item.Processes)
                        JobObjects.Suspend(process, suspend);
                }
            }

            // Only the player's pause is told in the flyout: a hold lasts as long as a drag and drop.
            if (changed && item.SuspendWhilePlaying && (playing || !suspend))
                item.SetDetail(playing ? "Paused while the player plays" : "");
        }
    }

    /// <summary>
    /// Registers (or unregisters) a tool process with the current work so it is suspended with it during playback or background holds.
    /// </summary>
    internal static void NoteProcess(int processId, bool running)
    {
        if (Current.Value is not { } item || !(item.SuspendWhilePlaying || item.Lane >= WorkLane.Background))
            return;

        lock (item.Processes)
        {
            if (!running)
            {
                item.Processes.Remove(processId);
                return;
            }

            item.Processes.Add(processId);
            if (item.Suspended && item.Phase == WorkState.Running)
                JobObjects.Suspend(processId, true);
        }
    }

    /// <summary>The job a tool started now goes into: the one for the lane of the work it is started for.</summary>
    public static ProcessClass CurrentProcessClass => Current.Value?.Lane is WorkLane.Background or WorkLane.Idle ? ProcessClass.Background : ProcessClass.Foreground;

    /// <summary>True inside background/idle work (tools get fewer threads and the background adapter).</summary>
    public static bool InBackground => CurrentProcessClass == ProcessClass.Background;

    public static bool IsPausedByUser(WorkLane lane) => UserPaused[(int)lane];

    private static bool PausedByRule(WorkLane lane)
    {
        if (lane < WorkLane.Background)
            return false;

        var settings = AppSettings.Current;
        return IsHeld || (_playing && settings.PauseBackgroundWhilePlaying) || (Running[(int)WorkLane.Foreground] > 0 && settings.PauseBackgroundWhileExporting);
    }

    // ----- Background hold during drag-and-drop -----
    // From drag start until the drop's work (placement, probe) completes, queued background/idle work waits and running tools are
    // suspended, so the drop's probe never competes for disk I/O (e.g. an HDD the indexer is seeking 25 times per strip).

    // Explicit holds (browser drag, drop work) and a timed hold (external drag, renewed on move; drop-to-work gap). Each explicit hold
    // expires HoldsAtMost after acquisition, so a hung probe (unresponsive share) cannot suspend background work indefinitely.
    private const long HoldsAtMost = 10_000;
    private static readonly List<Hold> Holds = [];
    private static long _heldUntil;

    // The timer that looks again when a hold ends by itself, and when it is set to (long.MaxValue: not set).
    private static readonly object HoldGate = new();
    private static readonly System.Threading.Timer HoldTimer = new(_ => HoldTimer_Tick());
    private static long _wakeAt = long.MaxValue;

    /// <summary>True while a drag or drop hold is active.</summary>
    public static bool IsHeld
    {
        get
        {
            var now = Environment.TickCount64;
            if (now < Interlocked.Read(ref _heldUntil))
                return true;

            lock (HoldGate)
                return Holds.Exists(h => now < h.Until);
        }
    }

    /// <summary>
    /// Holds background/idle work until the returned token is disposed (any thread, once), max 10 s: queued work waits and running tools
    /// are suspended. Work needed by foreground, interactive or urgent work is exempt.
    /// </summary>
    public static IDisposable HoldBackground()
    {
        var hold = new Hold(Environment.TickCount64 + HoldsAtMost);
        lock (HoldGate)
            Holds.Add(hold);
        WakeAt(hold.Until);
        HoldChanged();
        return hold;
    }

    /// <summary>Extends the timed hold (never shortens it); cheap enough to call on every drag move.</summary>
    public static void HoldBackground(TimeSpan span)
    {
        var until = Environment.TickCount64 + (long)Math.Max(span.TotalMilliseconds, 0);
        var held = IsHeld;
        for (var seen = Interlocked.Read(ref _heldUntil); seen < until; seen = Interlocked.Read(ref _heldUntil))
        {
            if (Interlocked.CompareExchange(ref _heldUntil, until, seen) == seen)
            {
                WakeAt(until);
                break;
            }
        }

        if (!held)
            HoldChanged();
    }

    // Has the timer look again at a moment, unless it is already set to look before it (it then sets itself again).
    private static void WakeAt(long at)
    {
        lock (HoldGate)
        {
            var now = Environment.TickCount64;
            if (_wakeAt > now && _wakeAt <= at)
                return;

            _wakeAt = at;
            HoldTimer.Change(Math.Max(at - now, 0), Timeout.Infinite);
        }
    }

    // A hold may have expired: rearm the timer for the next expiry and reapply the current state.
    private static void HoldTimer_Tick()
    {
        lock (HoldGate)
            _wakeAt = long.MaxValue;

        var now = Environment.TickCount64;
        var next = long.MaxValue;
        lock (HoldGate)
        {
            // A hold past its time holds nothing more, given back or not.
            Holds.RemoveAll(h => h.Until <= now);
            foreach (var hold in Holds)
                next = Math.Min(next, hold.Until);
        }

        if (Interlocked.Read(ref _heldUntil) is var held && held > now)
            next = Math.Min(next, held);
        if (next != long.MaxValue)
            WakeAt(next);

        HoldChanged();
    }

    // Hold changed: suspend/resume running tools and start eligible work.
    private static void HoldChanged()
    {
        ApplySuspensions();
        Pump();
        RaisePauseChanged();
    }

    private sealed class Hold(long until) : IDisposable
    {
        private int _released;

        /// <summary>When it holds nothing more, given back or not.</summary>
        public long Until { get; } = until;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            lock (HoldGate)
                Holds.Remove(this);
            HoldChanged();
        }
    }

    /// <summary>Pauses a lane (its waiting work does not start) or lets it go on: the flyout's toggles.</summary>
    public static void SetPaused(WorkLane lane, bool paused)
    {
        UserPaused[(int)lane] = paused;
        Pump();
    }

    public static Task Run(WorkSpec spec, Func<WorkContext, Task> work, CancellationToken token = default) =>
        Run(spec, async context =>
        {
            await work(context);
            return true;
        }, token);

    /// <summary>
    /// Runs work in its lane when it has room and is not paused. The task completes with the work (cancelled if cancelled here or in the flyout).
    /// </summary>
    public static Task<T> Run<T>(WorkSpec spec, Func<WorkContext, Task<T>> work, CancellationToken token = default)
    {
        // Asked for by work that is running, at its priority or above: done in its place.
        if (Current.Value is { } caller && spec.Lane <= caller.Lane)
            return work(new WorkContext(null, token));

        WorkItem item;
        TaskCompletionSource<T> done;
        lock (Gate)
        {
            if (spec.Key is { } key && Live.FirstOrDefault(i => i.Key == key && i.Task is Task<T> && !i.Closing && !i.Cancellation.IsCancellationRequested) is { Task: Task<T> same } twin)
            {
                // The subscription is reserved before the gate opens, so a leaving caller can never see zero and cancel it.
                twin.Waiters++;
                // Urgent re-request: promote the existing item (start now, resume its tools). Re-enters the gate on this thread.
                if (spec.Urgent && !twin.Urgent)
                {
                    (twin.Urgent, twin.BypassPause) = (true, true);
                    ApplySuspensions();
                    Pump();
                }

                return Join(twin, same, token);
            }

            // Keyed work is shared, so it owns its cancellation: a caller token ends only that caller's wait (Join); the work cancels when
            // all cancellable callers have (or from the flyout). Unkeyed work uses its single caller's token.
            var cancellation = spec.Key is null ? CancellationTokenSource.CreateLinkedTokenSource(token) : new CancellationTokenSource();
            item = new WorkItem(spec, cancellation)
            {
                BypassPause = spec.Urgent || Current.Value is { Lane: WorkLane.Foreground or WorkLane.Interactive },
                Context = SynchronizationContext.Current,
                Tab = FrontTab,
            };
            done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            item.Task = done.Task;
            item.Start = async () =>
            {
                Current.Value = item;
                try
                {
                    var result = await work(new WorkContext(item, cancellation.Token));
                    item.SetPhase(WorkState.Done);
                    done.TrySetResult(result);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    item.SetPhase(WorkState.Cancelled);
                    done.TrySetCanceled(cancellation.Token);
                }
                catch (Exception ex)
                {
                    // Logged once per kind of failure (its type and stack), so a folder of broken files cannot fill the log.
                    AppLog.WriteOnce($"work|{ex.GetType().FullName}|{ex.StackTrace}", $"Work \"{item.Title}\" failed", ex);
                    item.SetPhase(WorkState.Failed);
                    item.SetDetail(ex.Message);
                    done.TrySetException(ex);
                }
            };

            // Keyed work is published with its creator's subscription already reserved (a twin found next joins a live caller count).
            if (spec.Key is not null)
                item.Waiters = 1;
            Waiting.Add(item);
            Live.Add(item);

            // Cancelled while it waits: it never starts.
            cancellation.Token.Register(() =>
            {
                bool removed;
                lock (Gate)
                {
                    removed = Waiting.Remove(item);
                    if (removed)
                        Live.Remove(item);
                }

                if (removed)
                {
                    item.SetPhase(WorkState.Cancelled);
                    done.TrySetCanceled(cancellation.Token);
                }
            });
        }

        Show(item);
        Pump();
        return spec.Key is null ? done.Task : Join(item, done.Task, token);
    }

    /// <summary>Golden seam: runs after the last caller marked shared work closing and before its cancellation (null in the app).</summary>
    internal static Action<WorkItem>? ClosingSeam;

    /// <summary>
    /// A caller's wait on keyed work whose subscription the caller already reserved under the Gate; <paramref name="token"/> cancels only
    /// this wait. The work cancels when the last cancellable caller cancels; a non-cancellable caller keeps its reservation (and the work).
    /// </summary>
    private static Task<T> Join<T>(WorkItem item, Task<T> work, CancellationToken token)
    {
        if (!token.CanBeCanceled)
            return work;

        var registration = token.Register(static state =>
        {
            var shared = (WorkItem)state!;
            var close = false;
            lock (Gate)
            {
                // Zero callers left: mark it closing in the same step, so no new caller joins work that is about to be cancelled.
                if (--shared.Waiters == 0)
                    (shared.Closing, close) = (true, true);
            }

            if (!close)
                return;
            ClosingSeam?.Invoke(shared);
            shared.Cancel(evenIfNotCancellable: true);
        }, item);
        _ = work.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(), registration,
                              CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work.WaitAsync(token);
    }

    /// <summary>Cancels the work asked for under a key (the thumbnails of a file that is no longer loaded, say).</summary>
    public static void Cancel(string key)
    {
        List<WorkItem> found;
        lock (Gate)
            found = Live.Where(i => i.Key == key).ToList();
        foreach (var item in found)
            item.Cancel();
    }

    /// <summary>Starts eligible work: urgent first (ignoring limits), then per lane with the active tab's work first.</summary>
    private static void Pump()
    {
        lock (Gate)
        {
            // What a user's action waits on this moment starts at once, past its lane's limit (it still counts in it).
            while (Waiting.Find(i => i.Urgent) is { } urgent)
                StartLocked(urgent);

            foreach (var lane in Enum.GetValues<WorkLane>())
            {
                var index = (int)lane;

                // Resume paused piecewise work first, within the lane limit (urgent work may exceed it).
                while (Resuming.Find(r => r.Item.Lane == lane && (r.Item.Urgent || Running[index] < LaneLimit(lane))).Item is { } resumed)
                    GoOnLocked(resumed, counted: true);

                while (Running[index] < LaneLimit(lane))
                {
                    // Active tab's work first, then others FIFO. Reserved room for paused work is only used by pause-exempt work.
                    var front = FrontTab;
                    var kept = Running[index] + Stopped[index] >= LaneLimit(lane);
                    var next = Waiting.FirstOrDefault(i => i.Lane == lane && (i.Tab is null || i.Tab == front) && !UserPaused[index] && (i.BypassPause || !kept && !PausedByRule(lane)))
                        ?? Waiting.FirstOrDefault(i => i.Lane == lane && !UserPaused[index] && (i.BypassPause || !kept && !PausedByRule(lane)));
                    if (next is null)
                        break;

                    StartLocked(next);
                }
            }
        }
    }

    /// <summary>Starts queued work on its captured context (UI-thread work stays on the UI thread). Under the gate.</summary>
    private static void StartLocked(WorkItem item)
    {
        var lane = (int)item.Lane;
        Waiting.Remove(item);
        Running[lane]++;
        item.SetPhase(WorkState.Running);
        if (item.Context is { } context)
            context.Post(_ => _ = RunItAsync(item, lane), null);
        else
            _ = Task.Run(() => RunItAsync(item, lane));
    }

    private static async Task RunItAsync(WorkItem item, int lane)
    {
        try
        {
            await item.Start!();
        }
        finally
        {
            lock (Gate)
            {
                // Slot already released if it ended while paused (cancelled during wait); never release twice.
                if (item.SlotGivenBack)
                    GoOnLocked(item, counted: false);
                else
                    Running[lane]--;
                Live.Remove(item);
            }

            // Ended: nothing started in its name from now on is stopped for it (no later call reaches it to let it go on).
            lock (item.Processes)
                item.Suspended = false;

            item.Cancellation.Dispose();
            Pump();
        }
    }

    // ----- The interface side -----

    private static void Show(WorkItem item)
    {
        if (!item.Visible || _ui is not { } ui)
            return;

        ui.BeginInvoke(() =>
        {
            Items.Add(item);
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(100) };
                _timer.Tick += (_, _) => Refresh();
            }

            _timer.Start();
            Refresh();
        });
    }

    /// <summary>Copies progress to bindable state, expires finished items, and updates the chip. UI thread.</summary>
    private static void Refresh()
    {
        var settings = AppSettings.Current;
        var keepFinished = Math.Clamp(settings.FinishedTaskSeconds, 0, 600) * 1000L;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var item = Items[i];
            item.Sync();
            if (!item.IsActive && Environment.TickCount64 - item.FinishedAt > keepFinished)
                Items.RemoveAt(i);
        }

        if (Items.Count == 0)
            _timer?.Stop();

        // Background work held back by a rule says so, in the flyout.
        var held = Items.Any(i => i.State == WorkState.Queued && i.Lane >= WorkLane.Background);
        Summary.PausedNote = !held ? ""
            : _playing && settings.PauseBackgroundWhilePlaying ? "Background work waits while the player plays."
            : Running[(int)WorkLane.Foreground] > 0 && settings.PauseBackgroundWhileExporting ? "Background work waits while the export runs."
            : IsHeld ? "Background work waits while a file is dragged and dropped."
            : "";

        // The lead: the first running piece of work by lane; queued work only when nothing runs.
        var shown = Items.Where(i => i.IsActive && (settings.ShowBackgroundTasks || i.Lane < WorkLane.Background)).ToList();
        var lead = shown.Where(i => i.State == WorkState.Running).OrderBy(i => i.Lane).FirstOrDefault() ?? shown.OrderBy(i => i.Lane).FirstOrDefault();
        Summary.IsVisible = lead is not null;
        if (lead is null)
        {
            Summary.ToolTip = "";
            return;
        }

        // Only background and idle work: their progress together, drawn quieter.
        var background = lead.Lane >= WorkLane.Background;
        if (background)
        {
            var measured = shown.Where(i => i.State == WorkState.Running && !i.IsIndeterminate).ToList();
            Summary.IsIndeterminate = measured.Count == 0;
            Summary.Percent = measured.Count == 0 ? 0 : measured.Average(i => i.Percent);
            Summary.Title = shown.Count == 1 ? lead.Title : $"{lead.Title}";
        }
        else
        {
            Summary.IsIndeterminate = lead.IsIndeterminate || lead.State == WorkState.Queued;
            Summary.Percent = lead.Percent;
            Summary.Title = lead.State == WorkState.Queued ? $"{lead.Title} (waiting)" : lead.Title;
        }

        Summary.IsBackground = background;
        Summary.PercentText = Summary.IsIndeterminate ? "" : $"{Summary.Percent:0}%";
        Summary.MoreText = shown.Count > 1 ? $"+{shown.Count - 1}" : "";
        Summary.ToolTip = string.Join(Environment.NewLine, shown.Select(i => $"{i.Title}: {(i.State == WorkState.Queued ? "waiting" : i.IsIndeterminate ? "working" : $"{i.Percent:0}%")}"));
    }
}
