using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Services;

/// <summary>Who is waiting for a piece of work, from the most to the least: each lane has its own limit of work at once.</summary>
public enum WorkLane
{
    /// <summary>Exports, Render Preview, the queue and the Squisher.</summary>
    Foreground,

    /// <summary>What the user is waiting for: loading a file, probes, captions, Dead Air.</summary>
    Interactive,

    /// <summary>What makes the timeline nicer to work with: thumbnails, waveforms, I-frame lists, proxies.</summary>
    Background,

    /// <summary>What can wait for a quiet moment: folder indexing, cache purges, the encoder probe, update checks.</summary>
    Idle,
}

public enum WorkState { Queued, Running, Done, Failed, Cancelled }

/// <summary>What a piece of work is: its name in the progress flyout, its lane, and what tells two requests for the same work apart.</summary>
/// <param name="Title">Shown in the progress chip and flyout: "Thumbnails of clip.mp4".</param>
public sealed record WorkSpec(string Title, WorkLane Lane)
{
    /// <summary>Two requests with the same key while the first is waiting or running are one piece of work: the second gets the first's result.</summary>
    public string? Key { get; init; }

    /// <summary>Whether the flyout offers to cancel it.</summary>
    public bool Cancellable { get; init; } = true;

    /// <summary>Shown in the progress chip and flyout. Short chores (a probe) run through the lanes without being listed.</summary>
    public bool Visible { get; init; } = true;

    /// <summary>Its tools are stopped where they are while the player plays, and go on after (a proxy being made).</summary>
    public bool SuspendWhilePlaying { get; init; }
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
/// One piece of work, as the progress flyout shows it. Its bindable properties are touched on the UI thread only:
/// the work itself writes into plain fields, which the scheduler's timer copies across ten times a second.
/// </summary>
public sealed partial class WorkItem : ObservableObject
{
    internal WorkItem(WorkSpec spec, CancellationTokenSource cancellation) =>
        (Title, Lane, Key, Cancellable, Visible, SuspendWhilePlaying, Cancellation) = (spec.Title, spec.Lane, spec.Key, spec.Cancellable, spec.Visible, spec.SuspendWhilePlaying, cancellation);

    internal bool SuspendWhilePlaying { get; }

    // The tools started for it that are running, and whether they are stopped now.
    internal readonly List<int> Processes = [];
    internal bool Suspended;

    public string Title { get; }
    public WorkLane Lane { get; }
    public string? Key { get; }
    public bool Cancellable { get; }
    public bool Visible { get; }

    internal CancellationTokenSource Cancellation { get; }
    internal Task? Task { get; set; }
    internal Func<Task>? Start { get; set; }

    // Asked for by foreground or interactive work that may be waiting for it: it does not wait for a pause to end.
    internal bool BypassPause { get; init; }

    // Where it was asked for: it is started there (the UI thread, for work that works with the window), or on
    // the thread pool when it was asked for from there.
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

    public void Cancel()
    {
        if (!Cancellable)
            return;

        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
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

    /// <summary>Why background work is waiting, when it is: "Background work waits while the player plays." Empty otherwise.</summary>
    [ObservableProperty] private string _pausedNote = "";
}

/// <summary>
/// Every tool HandPeg runs, and every long piece of work it does, goes through here. Work is put in a lane by who
/// is waiting for it, and each lane has a limit of work at once:
///   - Foreground (exports, Render Preview, the queue, the Squisher): 2;
///   - Interactive (loading, probes, captions, Dead Air): 2;
///   - Background (thumbnails, waveforms, I-frame lists, proxies): Settings, Auto = one for every six processors not reserved;
///   - Idle (indexing, purges, the encoder probe, update checks): 1.
/// Background and Idle work waiting to start waits while the player plays and while foreground work runs (both
/// Settings, on by default), and while the user has paused its lane in the progress flyout; work already running
/// carries on. Work asked for twice under one key is done once. The tools started for a piece of work go into the
/// job for its lane (see <see cref="JobObjects"/>).
///
/// Work asked for from inside a running piece of work, at its own priority or above, is done right there, in that
/// piece's place: waiting for a place of its own in a lane its caller already fills could wait for ever.
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
    /// How many tools may decode on a graphics card at once for background work (a strip of thumbnails is one,
    /// however many inputs it opens): cards and drivers limit the decoding sessions they will open.
    /// </summary>
    public static SemaphoreSlim GpuDecodeGate { get; } = new(4, 4);

    /// <summary>Starts the scheduler's interface side (the summary and its timer), and applies the settings. On the UI thread, once.</summary>
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
        Pump();
    }

    /// <summary>How much of a lane's work may run at once.</summary>
    public static int LaneLimit(WorkLane lane) => lane switch
    {
        WorkLane.Foreground or WorkLane.Interactive => 2,
        WorkLane.Background => AppSettings.Current.BackgroundJobs > 0
            ? Math.Min(AppSettings.Current.BackgroundJobs, 16)
            : Math.Max(1, (Environment.ProcessorCount - AppSettings.Current.EffectiveReservedCores) / 6),
        _ => 1,
    };

    /// <summary>Whether the player is playing: background work waits meanwhile (Settings), and work that asked for it is stopped where it is.</summary>
    public static bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value)
                return;

            _playing = value;
            Pump();

            List<WorkItem> stoppable;
            lock (Gate)
                stoppable = Live.Where(i => i.SuspendWhilePlaying && i.Phase == WorkState.Running).ToList();
            foreach (var item in stoppable)
            {
                lock (item.Processes)
                {
                    if (item.Suspended == value)
                        continue;

                    item.Suspended = value;
                    foreach (var process in item.Processes)
                        JobObjects.Suspend(process, value);
                }

                item.SetDetail(value ? "Paused while the player plays" : "");
            }
        }
    }

    /// <summary>
    /// A tool was started (or has ended) for the work running now: it is the work's, to be stopped with it while the
    /// player plays when the work asked for that.
    /// </summary>
    internal static void NoteProcess(int processId, bool running)
    {
        if (Current.Value is not { SuspendWhilePlaying: true } item)
            return;

        lock (item.Processes)
        {
            if (!running)
            {
                item.Processes.Remove(processId);
                return;
            }

            item.Processes.Add(processId);
            if (item.Suspended)
                JobObjects.Suspend(processId, true);
        }
    }

    /// <summary>The job a tool started now goes into: the one for the lane of the work it is started for.</summary>
    public static ProcessClass CurrentProcessClass => Current.Value?.Lane is WorkLane.Background or WorkLane.Idle ? ProcessClass.Background : ProcessClass.Foreground;

    /// <summary>Whether the code running now is background or idle work (whose tools are given fewer threads and the background card).</summary>
    public static bool InBackground => CurrentProcessClass == ProcessClass.Background;

    /// <summary>Whether work in a lane waits to start: paused by the user, or by a rule (playing, exporting).</summary>
    public static bool IsPaused(WorkLane lane) => UserPaused[(int)lane] || PausedByRule(lane);

    public static bool IsPausedByUser(WorkLane lane) => UserPaused[(int)lane];

    private static bool PausedByRule(WorkLane lane)
    {
        if (lane < WorkLane.Background)
            return false;

        var settings = AppSettings.Current;
        return (_playing && settings.PauseBackgroundWhilePlaying) || (Running[(int)WorkLane.Foreground] > 0 && settings.PauseBackgroundWhileExporting);
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
    /// Does a piece of work in its lane: now, when the lane has room and is not paused; otherwise when it has. The
    /// task ends as the work does (cancelled when it was cancelled, here or in the flyout).
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
            if (spec.Key is { } key && Live.FirstOrDefault(i => i.Key == key && i.Task is Task<T>) is { Task: Task<T> same })
                return same;

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            item = new WorkItem(spec, cancellation)
            {
                BypassPause = Current.Value is { Lane: WorkLane.Foreground or WorkLane.Interactive },
                Context = SynchronizationContext.Current,
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
                    item.SetPhase(WorkState.Failed);
                    item.SetDetail(ex.Message);
                    done.TrySetException(ex);
                }
            };

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
        return done.Task;
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

    /// <summary>Starts whatever may start now, lane by lane.</summary>
    private static void Pump()
    {
        lock (Gate)
        {
            foreach (var lane in Enum.GetValues<WorkLane>())
            {
                var index = (int)lane;
                while (Running[index] < LaneLimit(lane))
                {
                    var next = Waiting.FirstOrDefault(i => i.Lane == lane && !UserPaused[index] && (i.BypassPause || !PausedByRule(lane)));
                    if (next is null)
                        break;

                    Waiting.Remove(next);
                    Running[index]++;
                    next.SetPhase(WorkState.Running);

                    async Task RunIt(WorkItem item, int lane)
                    {
                        try
                        {
                            await item.Start!();
                        }
                        finally
                        {
                            lock (Gate)
                            {
                                Running[lane]--;
                                Live.Remove(item);
                            }

                            item.Cancellation.Dispose();
                            Pump();
                        }
                    }

                    // Where it was asked for: the UI thread's work stays on the UI thread.
                    var (started, at) = (next, index);
                    if (next.Context is { } context)
                        context.Post(_ => _ = RunIt(started, at), null);
                    else
                        _ = Task.Run(() => RunIt(started, at));
                }
            }
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

    /// <summary>Copies the work's progress into what is shown, lets finished work go after a while, and works out the chip. UI thread.</summary>
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
