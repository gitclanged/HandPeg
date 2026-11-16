using System.Diagnostics;
using System.IO;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// Threads and what they own: the sound feeders stop at once, a thread that did not stop never has its resources freed under it, shared
/// work cancelled only when no caller wants it, the transport serialized, seqlock rings read whole, thread rules checked in code, the
/// file cache healing itself without pinning drives, and every child process in HandPeg's kill-on-close job.
/// </summary>
public static class ThreadLifetimeTests
{
    /// <summary>Thread-rule breaks the goldens make on purpose; the end-of-run check allows exactly these.</summary>
    public static int DeliberateViolations { get; private set; }

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("audio-feeders-stop", "# An idle mixer with its two feeder threads disposes at once (both woken by one stop event, one shared deadline).", FeedersStop);
        yield return Case("thread-joins-checked", "# Every timed join goes through ThreadStop; an owner whose thread did not stop keeps what that thread uses.", Joins);
        yield return Case("work-shared-cancellation", "# Shared (keyed) work: one caller's cancel ends only its own wait; the work stops when no caller wants it.", SharedWork);
        yield return Case("transport-serialized", "# Play, Pause, Seek, the priming hand-off and the end of the sequence change the transport under one lock.", Transport);
        yield return Case("seqlock-rings", "# The flight recorder's and crash reporter's rings: full-fence writers, readers that check before and after with a barrier.", Seqlocks);
        yield return Case("thread-owner-rule", "# ThreadOwner: the first caller owns; a call from another thread is logged once and counted, never thrown.", Owner);
        yield return Case("file-state-heals", "# FileState: a folder deleted, renamed away and recreated heals; a failed watcher is retired; pluggable drives get none.", FileStateHeals);
        yield return Case("child-processes-in-job", "# Every child process HandPeg starts is in its kill-on-close job (ProcessPipes, WorkerProcess, Adopt), or opened by the shell.", ChildProcesses);
        yield return Case("native-interop-named", "# Win32 timers and events come from src/Native's Kernel32; Media Foundation's vtable slots are named (ComSlots), never bare numbers.", Interop);
    }

    // ----- native-interop-named -----

    private static void Interop(Action<bool, string> expect)
    {
        var compositor = Directory.EnumerateFiles(Path.Combine(SourceProbe.Root, "src", "Compositor"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("AudioNative.", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        expect(compositor.Count == 0, $"the compositor borrows nothing from the sound engine's interop (its timers and events are Kernel32's){(compositor.Count == 0 ? "" : ": " + string.Join(", ", compositor))}");
        var bare = System.Text.RegularExpressions.Regex.Matches(SourceProbe.Src("Compositor", "Native", "MediaFoundation.cs"), @"Com\.Table\(\w+\)\[\d+\]").Count;
        expect(bare == 0, $"Media Foundation calls name their vtable slots (IMFSampleSlot.AddBuffer, not [42]); bare slot numbers left: {bare}");
        expect(Slot<int>("IMFSampleSlot", "AddBuffer") == 42 && Slot<int>("IMFAttributesSlot", "End") == 33 && Slot<int>("IMFTransformSlot", "ProcessOutput") == 25,
            "the derived slots are the SDK's (IMFSample::AddBuffer 42 after IMFAttributes' 33, IMFTransform::ProcessOutput 25)");
    }

    private static T Slot<T>(string type, string name) =>
        (T)typeof(HandPegApp.Services.AppLog).Assembly.GetType("HandPegApp.Compositor.Native." + type)!.GetField(name)!.GetValue(null)!;

    /// <summary>The run's last case: no thread rule was broken by anything but the deliberate cases.</summary>
    public static IEnumerable<GraphResult> Final()
    {
        yield return Case("thread-owner-violations", "# At the end of the run: no thread rule was broken outside the cases that break one on purpose.", expect =>
            expect(ThreadOwner.ViolationCount == DeliberateViolations, ThreadOwner.ViolationCount == DeliberateViolations
                ? "no thread rule was broken outside the deliberate case"
                : $"thread-rule breaks: {ThreadOwner.ViolationCount}, deliberate {DeliberateViolations} (the others are in HandPeg.log as \"Thread rule broken\")"));
    }

    // ----- audio-feeders-stop -----

    private static void FeedersStop(Action<bool, string> expect)
    {
        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var mixer = new AudioMixer(48_000, 480, threaded: true);
            Thread.Sleep(30);
            var clock = Stopwatch.StartNew();
            mixer.Dispose();
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        expect(times[2] < 150, times[2] < 150 ? "disposed in under 150 ms (median of 5; was up to 1 s while the second feeder slept out its timeout)" : $"disposed in {times[2]:0} ms (median of 5): over 150 ms");
        var feeders = SourceProbe.Member(SourceProbe.Src("Services", "Audio", "AudioVoices.cs"), "public bool Stop(");
        expect(SourceProbe.InOrder(feeders, "_stop.Set();", "ThreadStop.JoinAll(_threads, 2000, ", "return false;", "Interlocked.CompareExchange(ref state.Busy, 1, 0) == 0"),
            "both feeders against one 2 s deadline; when one did not stop, nothing it can reach is released");
        expect(SourceProbe.Member(SourceProbe.Src("Services", "Audio", "AudioMixer.cs"), "public void Dispose(").Contains("Interlocked.CompareExchange(ref state.Busy, 1, 0) == 0", StringComparison.Ordinal),
            "the mixer releases a voice only when no feeder holds it (no decoder freed under a read)");
    }

    // ----- thread-joins-checked -----

    private static void Joins(Action<bool, string> expect)
    {
        var timed = Directory.EnumerateFiles(Path.Combine(SourceProbe.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("ThreadStop.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"\.Join\(\s*\d") && !l.Text.Contains("ThreadStop.Join", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}").ToList();
        expect(timed.Count == 0, $"no timed Thread.Join outside ThreadStop (whose result decides what may be freed){(timed.Count == 0 ? "" : ": " + string.Join(", ", timed))}");

        var client = SourceProbe.Src("Services", "CompositorClient.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(client, "private void Close(bool keepMemory)"), "ThreadStop.Join(_supervisor, 2000,", "keepMemory |= !supervisorStopped;", "if (keepMemory)", "_shared.Dispose();"),
            "the compositor client keeps its events and control plane when its supervisor did not stop");
        expect(SourceProbe.InOrder(SourceProbe.Member(client, "private void Supervise("), "AppLog.Write(\"Compositor supervisor\", ex);", "if (_disposed)", "_stopping.WaitOne(1000);"),
            "the supervisor's catch touches nothing of a closed client");
        var wasapi = SourceProbe.Src("Services", "Audio", "WasapiAudioEngine.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(wasapi, "public void Dispose("), "ThreadStop.Join(_thread, 3000,", "return;", "Kernel32.CloseHandle(_commands);")
               && SourceProbe.InOrder(SourceProbe.Member(wasapi, "private void Run("), "_watch?.Dispose(_enumerator);", "Close();"),
            "the sound engine closes its wake event only after its mixer stopped, and unregisters the device watch before the slow teardown");
    }

    // ----- work-shared-cancellation -----

    private static void SharedWork(Action<bool, string> expect)
    {
        var key = "golden-shared|" + Guid.NewGuid();
        var started = 0;
        async Task<int> Work(WorkContext context)
        {
            Interlocked.Increment(ref started);
            await Task.Delay(400, context.Token);
            return 42;
        }

        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();
        var first = WorkScheduler.Run(new WorkSpec("Golden shared work", WorkLane.Interactive) { Key = key, Visible = false }, Work, a.Token);
        var second = WorkScheduler.Run(new WorkSpec("Golden shared work", WorkLane.Interactive) { Key = key, Visible = false }, Work, b.Token);
        a.Cancel();
        var firstCancelled = false;
        try
        {
            first.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            firstCancelled = true;
        }

        var secondResult = second.GetAwaiter().GetResult();
        expect(firstCancelled && secondResult == 42 && started == 1, "caller A cancelled: its wait ends; caller B still gets the shared work's result (the work started once)");

        var lastKey = key + "|last";
        using var c = new CancellationTokenSource();
        using var d = new CancellationTokenSource();
        var third = WorkScheduler.Run(new WorkSpec("Golden shared work", WorkLane.Interactive) { Key = lastKey, Visible = false }, Work, c.Token);
        var fourth = WorkScheduler.Run(new WorkSpec("Golden shared work", WorkLane.Interactive) { Key = lastKey, Visible = false }, Work, d.Token);
        c.Cancel();
        d.Cancel();
        var allCancelled = 0;
        foreach (var task in new[] { third, fourth })
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                allCancelled++;
            }
        }

        expect(allCancelled == 2, "both callers cancelled: both waits end, and the work itself is cancelled (no one wants it)");
        var frames = SourceProbe.Member(SourceProbe.Src("Services", "SourceFrames.cs"), "private async Task PumpAsync(");
        expect(SourceProbe.InOrder(frames, "held = WorkScheduler.IsHeld;", "if (!held)", "_waiting = null;", "if (held)") && frames.Split("WorkScheduler.IsHeld").Length == 2,
            "the media browser's still pump reads the hold once per decision (a request is never taken out and then skipped)");
    }

    // ----- transport-serialized -----

    private static void Transport(Action<bool, string> expect)
    {
        var engine = SourceProbe.Src("Services", "GpuPreviewEngine.cs");
        expect(new[] { "public void Seek(double sequenceSeconds)", "public void Play()", "public void Pause()", "private void PauseAtEnd(", "private void EndPriming(" }
                   .All(m => SourceProbe.Member(engine, m).Contains("lock (_transport)", StringComparison.Ordinal)),
            "Seek, Play, Pause, the end of the sequence and the priming hand-off each change the transport under _transport");
        expect(!SourceProbe.Member(engine, "private string? PauseCore(").Contains("AppLog", StringComparison.Ordinal)
               && SourceProbe.Member(SourceProbe.Src("Services", "GpuPreviewEngine.Tabs.cs"), "private string? EndPlaySession(").Contains("return string.Create(", StringComparison.Ordinal),
            "no file I/O under the lock: the play session's log line is written after it");
        expect(SourceProbe.InOrder(engine, "var serial = Volatile.Read(ref _transportSerial);", "var at = _audio.Position", "PauseAtEnd(serial,")
               && SourceProbe.InOrder(SourceProbe.Member(engine, "private void PauseAtEnd("), "if (!_playing || _transportSerial != serial)", "Ended?.Invoke();"),
            "the pump's end of sequence yields to a Seek, Play or Pause made after it measured the end (no stale Ended)");
        var wasapi = SourceProbe.Src("Services", "Audio", "WasapiAudioEngine.cs");
        expect(!wasapi.Contains("_seekLock", StringComparison.Ordinal) && wasapi.Contains("private double ReadSeek() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _pausedBits));", StringComparison.Ordinal),
            "the sound engine's seek is one 8-byte value published before the serial's fence (the two-writer seqlock is gone)");
        expect(SourceProbe.Src("MainWindow.GpuPreview.cs").Contains("|| !_viewModel.HasSource || engine.IsPlaying)", StringComparison.Ordinal),
            "a queued Ended does nothing when Play was pressed again before it ran");
    }

    // ----- seqlock-rings -----

    private static void Seqlocks(Action<bool, string> expect)
    {
        var recorder = SourceProbe.Src("Services", "DevFlightRecorder.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(recorder, "public static List<string> Lines("), "if (Volatile.Read(ref Sequences[slot]) != sequence)", "(Stamps[slot], Categories[slot], Details[slot])",
                   "Interlocked.MemoryBarrier();", "if (Volatile.Read(ref Sequences[slot]) != sequence)"),
            "the flight recorder's reader checks the slot's sequence before and after the copy, with a barrier between");
        expect(SourceProbe.Member(recorder, "public static void Record(").Contains("Interlocked.Exchange(ref Sequences[slot], 0);", StringComparison.Ordinal)
               && SourceProbe.Member(SourceProbe.Src("Services", "CrashReporter.cs"), "public static void Note(").Contains("Interlocked.Exchange(ref Sequences[slot], 0);", StringComparison.Ordinal),
            "both ring writers open a slot with a full fence (ARM64 cannot make the data visible before it)");
        expect(SourceProbe.InOrder(SourceProbe.Member(SourceProbe.Src("Compositor", "ExportLoopbackPreview.cs"), "private string SoftwareArguments("), "GetString(", "Interlocked.MemoryBarrier();", "!= version"),
            "the Live Encoded Preview's software arguments: a barrier before the re-check");
    }

    // ----- thread-owner-rule -----

    private static void Owner(Action<bool, string> expect)
    {
        var owner = new ThreadOwner();
        owner.Check("GoldenMember");
        var before = ThreadOwner.ViolationCount;
        var other = new Thread(() => owner.Check("GoldenMember"));
        other.Start();
        other.Join();
        DeliberateViolations++;
        expect(ThreadOwner.ViolationCount == before + 1, "a call from another thread is counted (and logged once, with its stack), not thrown");
        owner.Check("GoldenMember");
        expect(ThreadOwner.ViolationCount == before + 1, "the owner's own calls cost a compare and count nothing");
        expect(SourceProbe.Src("Services", "Gpu", "GpuResourceBroker.cs").Split("_owner.Check();").Length == 6
               && SourceProbe.Src("Services", "GpuPreviewEngine.cs").Split("_uiOwner.Check();").Length == 5,
            "applied: the broker's five mutating members and the engine's four UI-thread members");
    }

    // ----- file-state-heals -----

    private static bool Until(Func<bool> condition, int milliseconds = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds)
                return false;
            Thread.Sleep(50);
        }

        return true;
    }

    private static void FileStateHeals(Action<bool, string> expect)
    {
        var saved = FileState.WatchedFreshFor;
        var root = Path.Combine(Path.GetTempPath(), "handpeg-filestate-" + Guid.NewGuid().ToString("N")[..8]);
        var folder = Path.Combine(root, "media");
        var file = Path.Combine(folder, "clip.txt");
        try
        {
            FileState.WatchedFreshFor = TimeSpan.FromMilliseconds(200);
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, "x");
            DriveKind.Learn(file).GetAwaiter().GetResult();
            expect(DriveKind.IsHotPluggable(file) == false, "the system drive is learnt as internal (a watcher may be kept on it)");
            expect(FileState.Get(file).Exists, "a file there: seen");

            Directory.Delete(folder, recursive: true);
            expect(Until(() => !FileState.Get(file).Exists), "its folder deleted: seen as missing");
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, "x");
            expect(Until(() => FileState.Get(file).Exists), "the folder and file back: seen again, without a restart");

            Directory.Move(folder, folder + "-away");
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, "yy");
            expect(Until(() => FileState.Get(file) is { Exists: true, Length: 2 }), "the folder renamed away and a new one made with the file: the new one is seen (the watcher followed the old folder; the age check heals it)");

            File.WriteAllText(Path.Combine(folder, "other.txt"), "z");
            FileState.Get(Path.Combine(folder, "other.txt"));
            Until(() => FileState.IsFolderWatched(folder), 3000);
            var watched = FileState.IsFolderWatched(folder);
            FileState.FailWatcherForTests(folder);
            expect(watched && !FileState.IsFolderWatched(folder), "a watcher that fails (not a buffer overflow) is removed and disposed, so the folder can be watched again");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            expect(false, Threw(ex));
        }
        finally
        {
            FileState.WatchedFreshFor = saved;
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var watch = SourceProbe.Member(SourceProbe.Src("Services", "FileState.cs"), "private static void Watch(");
        expect(SourceProbe.InOrder(watch, "DriveKind.IsHotPluggable(folder)", "if (pluggable ||", "Watchers.TryAdd(folder, null);"), "a drive that can be unplugged (USB, 1394, SD, external SSDs) gets no watcher, so it can always be ejected");
    }

    // ----- child-processes-in-job -----

    private static void ChildProcesses(Action<bool, string> expect)
    {
        // Process.Start only where the process is put in a job (Assign, Adopt, Track) or is the shell's (ShellOpen, ShellExecute).
        string[] allowed = ["FfmpegLoopbackEncoder.cs", "FfmpegVideoDecoder.cs", "HardwareBenchmark.cs", "ShellOpen.cs", "VodTrim.cs", "ClipLibrary.cs", "ProcessPipes.cs"];
        var starts = Directory.EnumerateFiles(Path.Combine(SourceProbe.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}ShellThumbnails{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"(?<!Worker)Process\.Start\(") && !allowed.Contains(Path.GetFileName(f)))
            .Select(Path.GetFileName).ToList();
        expect(starts.Count == 0, $"Process.Start only in the files that put the process in a job or hand it to the shell{(starts.Count == 0 ? "" : ": " + string.Join(", ", starts))}");
        expect(SourceProbe.Src("Services", "VodTrim.cs").Contains("using var adopted = ProcessPipes.Adopt(process.Id);", StringComparison.Ordinal)
               && SourceProbe.Src("Services", "ClipLibrary.cs").Contains("using (ProcessPipes.Adopt(started.Id))", StringComparison.Ordinal),
            "the bundle's VOD trims and the Clip Library's audio cut are adopted into the job (they no longer outlive a crash)");
        expect(SourceProbe.Src("Services", "WorkerProcess.cs").Contains("if (!JobObjects.Assign(started.ProcessId, jobClass, efficiencyMode: false))", StringComparison.Ordinal),
            "a compositor worker that cannot be put in the job is ended before it runs");
    }
}
