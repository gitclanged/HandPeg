using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4D-TabPerf (implementer H): the managed heap kept lean: the recovery file written without huge strings, one
/// compacting collection when the editor is quiet after a tab is closed or put behind.
/// </summary>
public static class MemoryHygieneTests
{
    /// <summary>The window-free cases (and those on a window of their own), before the UI-scale tests.</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("memory-hygiene-project-write",
            "# A project written straight into its file (5.4D-TabPerf): the very bytes its text was, the undo steps' Base64 escaped as before, whole or not at all, without the text of all of it.",
            ProjectWrite);
        yield return Case("memory-hygiene-compact",
            "# One compacting collection once the editor is quiet (5.4D-TabPerf): asks close together are one, never while it is busy, asked again until it is quiet or 2 minutes have gone, on the thread that set IsQuiet.",
            Compact);
        yield return Case("memory-hygiene-write-whole-unique",
            "# Two whole writes of one file at once (5.4D-TabPerf review): each written beside it under a name of its own, so neither fails on the other's; the last moved in is the file, whole, and nothing is left beside it.",
            WriteWholeUnique);
    }

    /// <summary>The cases that need the WPF Application the UI-scale tests make: run last.</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
    }

    // ----- The project written as a stream -----

    private static Guid Fixed(int n) => new(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{n:D12}"));

    /// <summary>A Sequence of the main video in so many clips of two seconds each.</summary>
    private static TimelineDocument Cut(int clips) => new()
    {
        Tracks =
        [
            new TimelineTrackModel
            {
                Id = Fixed(1), TrackNumber = 1,
                Clips =
                [
                    .. Enumerable.Range(0, clips).Select(i => new TimelineClip
                    {
                        Id = Fixed(10 + i), ContentKind = LayerKind.MainVideo, SourcePath = "D:/media/main.mp4", AudioStreamIndex = 0, Start = i * 2, End = i * 2 + 2,
                        MediaOffset = i * 2, SourceDuration = 8, State = new LayerState { Name = "Main é ☕ 🎬 <&> \"quoted\"", Kind = LayerKind.MainVideo },
                    }),
                ],
            },
        ],
    };

    /// <summary>
    /// A project of 600 clips with a history of twenty undo steps and three redo steps: two real ones (packed Sequences), the rest
    /// bytes of a fixed seed in lengths around the writer's 3 KB pieces (and one of many pieces), their Base64 full of "+" and "/".
    /// </summary>
    private static ProjectState Project()
    {
        var random = new Random(5404);
        byte[] Bytes(int length)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }

        int[] lengths = [1, 2, 3, 4, 3071, 3072, 3073, 6144, 6145, 9215, 100_003, 250_000, 17, 512, 1024, 2048, 4096, 8192];
        var undo = new List<UndoStepState>
        {
            new("split Main", UndoSteps.Pack(new TimelineSnapshot(Cut(1), []))),
            new("split Main again", UndoSteps.Pack(new TimelineSnapshot(Cut(2), []))),
        };
        undo.AddRange(lengths.Select((length, i) => new UndoStepState(string.Create(CultureInfo.InvariantCulture, $"step {i} of {length} bytes \u00e9+/"), Bytes(length))));
        var redo = new List<UndoStepState> { new("redo one", Bytes(3)), new("redo two", Bytes(70_000)), new("", Bytes(5)) };
        return new ProjectState
        {
            Name = "Streamed é ☕", SourcePath = "D:/media/main.mp4", LocalMediaPath = "D:/media/main.mp4", Timeline = Cut(600),
            UndoHistory = undo, RedoHistory = redo, ThumbnailJpeg = Bytes(900),
        };
    }

    private static void ProjectWrite(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var folder = Path.Combine(AppPaths.Temp, "memory-hygiene-project-write");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        var state = Project();
        var path = Path.Combine(folder, "tab" + ProjectTabs.StateExtension);
        ProjectStore.SaveTo(state, path);
        var written = File.ReadAllBytes(path);
        var asText = ProjectStore.Serialize(state);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(asText);

        // The very bytes: what the stream wrote is the text, as File.WriteAllText wrote it before (UTF-8, no byte order mark).
        Expect(written.AsSpan().SequenceEqual(utf8), "the file written as a stream is the project's text, byte for byte");
        Expect(written.Length > 3 && !(written[0] == 0xEF && written[1] == 0xBB && written[2] == 0xBF), "no byte order mark");

        // Each step as 5.4C wrote it: its name and its Base64 as text, escaped as the serializer escapes any text (a "+" as \u002B).
        var steps = state.UndoHistory!.TakeLast(ProjectFile.MaximumUndoSteps).Concat(state.RedoHistory!).ToList();
        var missing = steps.Where(s => !asText.Contains($"{{\"what\":{JsonSerializer.Serialize(s.What)},\"state\":{JsonSerializer.Serialize(Convert.ToBase64String(s.State))}}}", StringComparison.Ordinal))
            .Select(s => s.What).ToList();
        Expect(missing.Count == 0, $"every step's Base64 is the text Convert.ToBase64String made, escaped as before ({steps.Count} steps{(missing.Count > 0 ? "; not: " + string.Join(", ", missing) : "")})");
        Expect(asText.Contains("\\u002B", StringComparison.Ordinal) && asText.Contains('/'), "the steps' \"+\" and \"/\" are there to be escaped (\"+\" as \\u002B)");

        // Read back: the same steps, byte for byte; written again, the same file.
        var opened = ProjectStore.Load(path, unpackMasks: true);
        static bool Same(List<UndoStepState>? read, IReadOnlyList<UndoStepState> wrote) =>
            read is not null && read.Count == wrote.Count && read.Zip(wrote).All(p => p.First.What == p.Second.What && p.First.State.AsSpan().SequenceEqual(p.Second.State));
        Expect(Same(opened.UndoHistory, state.UndoHistory!.TakeLast(ProjectFile.MaximumUndoSteps).ToList()) && Same(opened.RedoHistory, state.RedoHistory!),
            "read back, the history is the one written, step for step and byte for byte (the newest twenty undo steps)");
        Expect(opened.Timeline.Tracks is [{ Clips.Count: 600 }] && opened.Name == state.Name, "and the Sequence and the name are as they were");
        var again = Path.Combine(folder, "again" + ProjectTabs.StateExtension);
        ProjectStore.SaveTo(opened, again);
        Expect(File.ReadAllBytes(again).AsSpan().SequenceEqual(written), "read and written again as a stream, the same file");

        // The file as 5.4C wrote it (the version written in it set aside, so that a new version does not change the golden): its hash.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asText.Replace(ProjectStore.WriterName, ProjectTests.WriterPlaceholder, StringComparison.Ordinal))));
        text.Append(CultureInfo.InvariantCulture, $"   the file: {written.Length - Encoding.UTF8.GetByteCount(ProjectStore.WriterName) + Encoding.UTF8.GetByteCount(ProjectTests.WriterPlaceholder)} bytes, SHA-256 {hash}\n");

        // Whole or not at all: a write that fails part of the way leaves the old file as it was, and nothing beside it.
        var before = File.ReadAllBytes(path);
        var failed = false;
        try
        {
            ProjectStore.WriteWhole(path, stream =>
            {
                stream.Write(new byte[64 * 1024]);
                throw new IOException("the disk is full (test)");
            });
        }
        catch (IOException ex) when (ex.Message == "the disk is full (test)")
        {
            failed = true;
        }

        Expect(failed && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "a write that fails part of the way passes the failure on and leaves the old file as it was");
        Expect(!File.Exists(path + ".tmp") && Directory.EnumerateFiles(folder).Count() == 2, "and leaves nothing beside it (no half-written .tmp)");

        // A file of a newer format is still refused before anything is written.
        var newer = Path.Combine(folder, "newer.hproj");
        var newerText = asText.Replace($"\"version\":{ProjectFile.CurrentVersion},", $"\"version\":{ProjectFile.CurrentVersion + 1},", StringComparison.Ordinal);
        File.WriteAllText(newer, newerText);
        var refused = false;
        try
        {
            ProjectStore.SaveTo(state, newer);
        }
        catch (IOException)
        {
            refused = true;
        }

        Expect(refused && File.ReadAllText(newer) == newerText && !File.Exists(newer + ".tmp"), "a file of a newer format is refused, never written over");

        // The header read from the text's start, and from the whole when it is not all there (5.4D-TabPerf): every answer as before.
        Expect(Throws<NewerProjectFormatException>(() => ProjectStore.Deserialize(newerText)), "a newer format is refused from the header at the start of a big text");
        var late = "{\"padding\":\"" + new string('x', 9000) + "\"," + asText[1..];
        Expect(ProjectStore.Deserialize(late).Timeline.Tracks is [{ Clips.Count: 600 }], "a text whose header comes after 9,000 characters (by hand) is read as before");
        var noWriter = asText.Replace($"\"writtenBy\":{JsonSerializer.Serialize(ProjectStore.WriterName)},", "", StringComparison.Ordinal);
        Expect(noWriter.Length < asText.Length && ProjectStore.Deserialize(noWriter).Timeline.Tracks is [{ Clips.Count: 600 }], "and one without its writer's name");
        var oldFormat = noWriter.Replace($"\"version\":{ProjectFile.CurrentVersion},", "\"version\":5,", StringComparison.Ordinal);
        Expect(Throws<InvalidDataException>(() => ProjectStore.Deserialize(oldFormat), "format 5"), "a big text of format 5 is refused as format 5");
        Expect(Throws<InvalidDataException>(() => ProjectStore.Deserialize("{\"padding\":\"" + new string('x', 9000) + "\"}"), "not a HandPeg project"), "and one that is no project as none");

        // Without the text of all of it: what one write allocates, against the text made first and written (the 5.4C way), warmed up
        // both. The streamed write makes no string of the file (two bytes a character) and no buffer of all of it.
        var oldWay = Path.Combine(folder, "old-way.hproj");
        File.WriteAllText(oldWay, ProjectStore.Serialize(state));
        ProjectStore.SaveTo(state, path);
        var start = GC.GetAllocatedBytesForCurrentThread();
        File.WriteAllText(oldWay, ProjectStore.Serialize(state));
        var textFirst = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        ProjectStore.SaveTo(state, path);
        var streamed = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"  note  memory-hygiene-project-write: a {written.Length / 1024.0:0} KB file allocated {textFirst / 1048576.0:0.00} MB written from its text, {streamed / 1048576.0:0.00} MB streamed");
        Expect(textFirst - streamed >= written.Length * 3 / 2, "a streamed write allocates at least one and a half times the file's size less than the text made first (its string alone is twice it)");

        Directory.Delete(folder, recursive: true);
    }

    private static bool Throws<T>(Action action, string? saying = null) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T ex)
        {
            return saying is null || ex.Message.Contains(saying, StringComparison.Ordinal);
        }
    }

    // ----- The compacting collection -----

    /// <summary>A SynchronizationContext of one thread of its own, as a window's dispatcher is: what is posted runs there, in turn.</summary>
    private sealed class OneThread : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = [];
        private readonly Thread _thread;

        public OneThread()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                    callback(state);
            }) { IsBackground = true, Name = "memory-hygiene test thread" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Run(Action action)
        {
            using var done = new ManualResetEventSlim();
            Post(_ => { action(); done.Set(); }, null);
            done.Wait();
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    private static void Compact(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var (settle, recheck, giveUp, quietBefore) = (MemoryHygiene.Settle, MemoryHygiene.RecheckEvery, MemoryHygiene.GiveUpAfter, MemoryHygiene.IsQuiet);
        try
        {
            // An ask another test made is let go, and one running is let finish, so that what is counted here is this case's.
            MemoryHygiene.Forget();
            WaitFor(() => !MemoryHygiene.IsCollecting, 30_000);
            (MemoryHygiene.Settle, MemoryHygiene.RecheckEvery, MemoryHygiene.GiveUpAfter) = (TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30));

            // IsQuiet set from the pool (a thread without a SynchronizationContext): asked on the pool, as the timer runs.
            var quiet = false;
            var asked = 0;
            Task.Run(() => MemoryHygiene.IsQuiet = () => { Interlocked.Increment(ref asked); return Volatile.Read(ref quiet); }).Wait();

            // 1. Asks close together are one: five asks (two reasons twice), then the one collection after the last of them, not before.
            Volatile.Write(ref quiet, true);
            var count = MemoryHygiene.Compactions;
            var clock = Stopwatch.StartNew();
            var gaps = new List<double>();
            var last = 0.0;
            foreach (var reason in new[] { "tab closed", "tab let go", "tab closed", "tab let go", "tab closed" })
            {
                MemoryHygiene.RequestCompactOnce(reason);
                gaps.Add(clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
                Thread.Sleep(60);
            }

            var lastAsk = last;
            Expect(WaitFor(() => MemoryHygiene.Compactions > count, 10_000), "five asks close together: a collection follows");
            var collectedAt = clock.Elapsed.TotalMilliseconds;
            Thread.Sleep(800);
            var made = MemoryHygiene.Compactions - count;
            if (gaps.Skip(1).Max() < 300)
            {
                Expect(made == 1, "and only one");
            }
            else
            {
                // The machine was too busy to ask within the wait: as many as the gaps longer than it, plus one.
                Console.WriteLine($"  note  memory-hygiene-compact: asks {string.Join(", ", gaps.Select(g => g.ToString("0", CultureInfo.InvariantCulture)))} ms apart");
                Expect(made <= gaps.Count(g => g >= 300) + 1, "and only one");
            }

            Expect(collectedAt - lastAsk >= 290, "not before the wait after the last ask was over");
            Expect(MemoryHygiene.LastReason == "tab closed, tab let go", "its reasons, each once: tab closed, tab let go");

            // 2. Never while the editor is busy: asked every 200 ms meanwhile, collected at the first quiet moment.
            Volatile.Write(ref quiet, false);
            count = MemoryHygiene.Compactions;
            Interlocked.Exchange(ref asked, 0);
            clock.Restart();
            MemoryHygiene.RequestCompactOnce("busy");
            Thread.Sleep(1500);
            var askedBusy = Volatile.Read(ref asked);
            var busyFor = clock.Elapsed.TotalMilliseconds;
            Expect(MemoryHygiene.Compactions == count, "while the editor is busy, no collection");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  note  memory-hygiene-compact: a busy editor asked {askedBusy} times in {busyFor:0} ms"));
            Expect(askedBusy >= 2 && askedBusy <= (int)((busyFor - 300) / 200) + 1, "it is asked again, every 200 ms at most");
            Volatile.Write(ref quiet, true);
            Expect(WaitFor(() => MemoryHygiene.Compactions > count, 10_000), "once it is quiet, the collection follows");
            Expect(MemoryHygiene.LastReason == "busy", "the busy one's");

            // 3. Given up: a busy editor is not asked past the wait's end, and a quiet moment after it collects nothing.
            MemoryHygiene.GiveUpAfter = TimeSpan.FromMilliseconds(600);
            Volatile.Write(ref quiet, false);
            (count, var gaveUp) = (MemoryHygiene.Compactions, MemoryHygiene.GivenUp);
            MemoryHygiene.RequestCompactOnce("never quiet");
            Expect(WaitFor(() => MemoryHygiene.GivenUp > gaveUp, 10_000), "an editor busy all through the wait: the ask is let go");
            var askedThen = Volatile.Read(ref asked);
            Volatile.Write(ref quiet, true);
            Thread.Sleep(700);
            Expect(MemoryHygiene.Compactions == count && Volatile.Read(ref asked) == askedThen, "and no collection, nor another question, after it (quiet or not)");

            // 4. No window (IsQuiet null): never quiet.
            MemoryHygiene.IsQuiet = null;
            (count, gaveUp) = (MemoryHygiene.Compactions, MemoryHygiene.GivenUp);
            MemoryHygiene.RequestCompactOnce("no window");
            Expect(WaitFor(() => MemoryHygiene.GivenUp > gaveUp, 10_000) && MemoryHygiene.Compactions == count, "with no window to say it is quiet, no collection ever");

            // 5. Asked on the thread that set IsQuiet (the window's dispatcher): an ask from the pool is answered there.
            MemoryHygiene.GiveUpAfter = TimeSpan.FromSeconds(30);
            using (var window = new OneThread())
            {
                // What is posted to the window's thread as it answers (a Play pressed then: the key waits behind the answer) must run
                // after the collection, not before it (the 5.4D-TabPerf review's P16).
                var (askedOn, fullAtAnswer, fullAfterAnswer) = (-1, -1, -1);
                window.Run(() => MemoryHygiene.IsQuiet = () =>
                {
                    (askedOn, fullAtAnswer) = (Environment.CurrentManagedThreadId, GC.CollectionCount(2));
                    SynchronizationContext.Current?.Post(_ => Volatile.Write(ref fullAfterAnswer, GC.CollectionCount(2)), null);
                    return true;
                });
                count = MemoryHygiene.Compactions;
                var fullBefore = GC.CollectionCount(2);

                // Garbage in the large-object heap between objects that stay: the free space a background collection leaves committed.
                var kept = new List<byte[]>();
                for (var i = 0; i < 64; i++)
                {
                    var garbage = new byte[1 << 20];
                    garbage[0] = 1;
                    if (i % 2 == 0)
                        kept.Add(new byte[1 << 20]);
                }

                Task.Run(() => MemoryHygiene.RequestCompactOnce("from the pool")).Wait();
                Expect(WaitFor(() => MemoryHygiene.Compactions > count, 10_000), "an ask from the pool, the window quiet: collected");
                Expect(askedOn == window.ThreadId, "the window was asked on its own thread");
                Expect(MemoryHygiene.CollectedOn == window.ThreadId, "the collection ran there too, in the turn that said it was quiet");
                Expect(WaitFor(() => Volatile.Read(ref fullAfterAnswer) >= 0, 10_000) && fullAfterAnswer > fullAtAnswer,
                    "what was queued on that thread behind the answer (a Play pressed then) ran only after the collection");
                Expect(GC.CollectionCount(2) > fullBefore && GCSettings.LargeObjectHeapCompactionMode == GCLargeObjectHeapCompactionMode.Default,
                    "a full collection, the large-object heap compacted by it (its once-only mode used up)");
                GC.KeepAlive(kept);
            }
        }
        finally
        {
            MemoryHygiene.Forget();
            WaitFor(() => !MemoryHygiene.IsCollecting, 30_000);
            (MemoryHygiene.Settle, MemoryHygiene.RecheckEvery, MemoryHygiene.GiveUpAfter, MemoryHygiene.IsQuiet) = (settle, recheck, giveUp, quietBefore);
        }
    }

    private static bool WaitFor(Func<bool> holds, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!holds())
        {
            if (clock.ElapsedMilliseconds > milliseconds)
                return false;
            Thread.Sleep(10);
        }

        return true;
    }

    // ----- Whole writes of one file at once (5.4D-TabPerf review) -----

    /// <summary>
    /// A tab's cache file written on the thread pool while HandPeg falls over and writes it again: the first write holds its file beside open
    /// (shared for reading only) while a second, and a third from text, write the same file. Each has a name of its own, so all are whole;
    /// a write that fails (the file's place is a folder) deletes its own file beside and passes the failure on.
    /// </summary>
    private static void WriteWholeUnique(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var folder = Path.Combine(AppPaths.Temp, "memory-hygiene-write-whole-unique");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "tab" + ProjectTabs.StateExtension);
        File.WriteAllText(path, "old");

        using var inside = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var first = Task.Run(() => ProjectStore.WriteWhole(path, stream =>
        {
            stream.Write("first"u8);
            inside.Set();
            letGo.Wait(10_000);
        }));
        var started = inside.Wait(10_000);
        var halfWay = Directory.EnumerateFiles(folder, "*.tmp").ToList();
        Expect(started && halfWay.Count == 1 && Path.GetFileName(halfWay[0]).StartsWith("tab.hptab.", StringComparison.Ordinal) && Path.GetFileName(halfWay[0]).Length == "tab.hptab.".Length + 32 + ".tmp".Length,
            "a write under way writes beside the file under a name of its own (tab.hptab.<32 hex digits>.tmp)");

        string? second = null;
        try
        {
            ProjectStore.WriteWhole(path, stream => stream.Write("second"u8));
            ProjectStore.WriteWhole(path, "third");
        }
        catch (IOException ex)
        {
            second = ex.Message;
        }

        Expect(second is null && File.ReadAllText(path) == "third", "a second write of the same file meanwhile (streamed, then from text) never meets the first's file: both are whole");
        letGo.Set();
        var finished = first.Wait(10_000) && first.IsCompletedSuccessfully;
        Expect(finished && File.ReadAllText(path) == "first", "the first, let go, is whole too: the last moved in is the file");
        Expect(Directory.EnumerateFiles(folder).Count() == 1, "and nothing is left beside it");

        // A write that fails as it is moved in (the file's place is a folder): its own file beside goes, and the failure is passed on.
        var occupied = Path.Combine(folder, "occupied.json");
        Directory.CreateDirectory(occupied);
        var refused = false;
        try
        {
            ProjectStore.WriteWhole(occupied, "text");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refused = true;
        }

        Expect(refused && !Directory.EnumerateFiles(folder, "*.tmp").Any(), "a write from text that fails passes the failure on and deletes its own file beside");
        Directory.Delete(folder, recursive: true);
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }
}
