using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Hot paths and memory hygiene: per-frame breadcrumbs that never touch the disk on the calling thread and are never torn on disk, and
/// blocking collections only behind the quiet gate.
/// </summary>
public static partial class HotPathTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("gpu-breadcrumbs-hot", "# Per-frame breadcrumbs (EnterHot): no disk write on the calling thread; the flusher puts the last ENTER line on disk within one period.", BreadcrumbsHot);
        yield return Case("gpu-breadcrumbs-idle", "# The breadcrumb flusher sleeps when no hot line comes (no timer wake-ups while paused) and wakes for the next one; none at all with breadcrumbs off.", BreadcrumbsIdle);
        yield return Case("gpu-breadcrumbs-seqlock", "# A slot copied while its thread keeps writing is never torn: the header's event count always matches its newest ring line.", BreadcrumbsSeqlock);
        yield return Case("gc-collect-sites", "# Blocking collections only behind the quiet gate (MemoryHygiene), the worker's idle trim and the crash path; no working-set trim anywhere.", CollectSites);
    }

    // ----- gpu-breadcrumbs-hot -----

    private static void BreadcrumbsHot(Action<bool, string> expect)
    {
        var path = Path.Combine(Path.GetTempPath(), $"handpeg-breadcrumbs-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        try
        {
            using (var file = BreadcrumbFile.Open(path, "golden"))
            {
                expect(file is not null, "the breadcrumb file opens (write-through, unbuffered, overlapped)");
                if (file is null)
                    return;

                var (hotWrites, coldWrites, allocated) = (-1, -1, -1L);
                var thread = new Thread(() =>
                {
                    // Warm up (slot, formatting paths), then measure 10,000 hot pairs.
                    file.Enter("Warm", 1, hot: true).Leave();
                    var writesBefore = BreadcrumbFile.DiskWritesOnThisThread;
                    var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10_000; i++)
                        file.Enter("Present", i + 1, hot: true).Leave(i == 9_999 ? unchecked((int)0x887A0005) : 0);
                    allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
                    hotWrites = BreadcrumbFile.DiskWritesOnThisThread - writesBefore;

                    writesBefore = BreadcrumbFile.DiskWritesOnThisThread;
                    file.Enter("ResizeBuffers", "1920x1080").Leave();
                    coldWrites = BreadcrumbFile.DiskWritesOnThisThread - writesBefore;
                    file.Enter("Present", 424242, hot: true);
                }) { Name = "golden hot crumbs" };
                thread.Start();
                thread.Join();

                expect(hotWrites == 0, $"10,000 hot ENTER/LEAVE pairs: 0 disk writes on the calling thread (made {hotWrites})");
                expect(allocated < 1024, $"and no allocations on the calling thread ({allocated} bytes for 20,000 lines)");
                expect(coldWrites == 2, $"a set-up call (ResizeBuffers) still writes its ENTER and LEAVE through at once (made {coldWrites})");

                var deadline = Environment.TickCount64 + 2_000;
                string slot;
                do
                {
                    Thread.Sleep(BreadcrumbFile.FlushMilliseconds);
                    slot = ReadShared(path);
                }
                while (!slot.Contains("Present luid=0x0 424242", StringComparison.Ordinal) && Environment.TickCount64 < deadline);

                expect(slot.Contains("ENTER ", StringComparison.Ordinal) && slot.Contains("Present luid=0x0 424242", StringComparison.Ordinal),
                    "the flusher wrote the thread's last hot ENTER line to disk within its period (no LEAVE: the call it names is in flight)");
                expect(slot.Contains("STATE ENTER Present", StringComparison.Ordinal), "the slot's header names the call in flight");
                expect(slot.Contains("LEAVE", StringComparison.Ordinal) && slot.Contains("hr=0x887A0005", StringComparison.Ordinal),
                    "LEAVE lines carry the duration and the HRESULT as 8 hex digits (hr=0x887A0005)");
                expect(Regex.IsMatch(slot, @"events=20005 "), "the header counts every line of the thread: warm-up 2 + 20,000 hot + 2 written through + 1 in flight");
                expect(slot.Length % BreadcrumbFile.SlotBytes == 0 && slot.Split('\n').All(l => l.Length is 0 or BreadcrumbFile.LineBytes - 1),
                    "fixed 4 KB slots of 128-byte lines (127 characters and a line break)");
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    // ----- gpu-breadcrumbs-idle -----

    private static void BreadcrumbsIdle(Action<bool, string> expect)
    {
        var path = Path.Combine(Path.GetTempPath(), $"handpeg-breadcrumbs-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        try
        {
            using var file = BreadcrumbFile.Open(path, "golden");
            expect(file is not null, "the breadcrumb file opens");
            if (file is null)
                return;

            void Hot(long detail)
            {
                var thread = new Thread(() => file.Enter("Present", detail, hot: true).Leave()) { Name = "golden idle crumbs" };
                thread.Start();
                thread.Join();
            }

            bool WaitFor(Func<bool> done, int milliseconds)
            {
                var deadline = Environment.TickCount64 + milliseconds;
                while (!done() && Environment.TickCount64 < deadline)
                    Thread.Sleep(BreadcrumbFile.FlushMilliseconds);
                return done();
            }

            expect(!file.FlusherIdle && file.FlushPasses == 0, "no flusher before the first hot line (a process with only set-up calls never starts one)");
            Hot(1);
            expect(WaitFor(() => file.FlusherIdle, 2_000), $"after {BreadcrumbFile.IdleAfterQuietPeriods} quiet periods the flusher stops its timer and sleeps");
            var passes = file.FlushPasses;
            Thread.Sleep(300);
            expect(file.FlusherIdle && file.FlushPasses == passes, "asleep, it wakes for nothing: no flush period passes in 300 ms with no hot line");

            Hot(777_777);
            expect(WaitFor(() => ReadShared(path).Contains("Present luid=0x0 777777", StringComparison.Ordinal), 2_000),
                "the next hot line wakes it, and its slot is on disk within a period or two");
            expect(WaitFor(() => file.FlusherIdle, 2_000), "and it goes back to sleep once the lines stop");

            var crumbs = SourceProbe.Src("Services", "GpuNativeBreadcrumbs.cs");
            var enter = SourceProbe.Member(SourceProbe.Src("Services", "BreadcrumbFile.cs"), "internal GpuNativeBreadcrumbs.Crumb Enter(string op, long detail, bool hot)");
            expect(crumbs.Contains("Log = On ? BreadcrumbFile.OpenInFolder(Folder, Role) : null;", StringComparison.Ordinal)
                   && enter.Contains("if (hot && _flusherStarted == 0)", StringComparison.Ordinal),
                "with breadcrumbs off there is no file and so no flusher; with them on, the flusher starts at the first hot line");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    // ----- gpu-breadcrumbs-seqlock -----

    private static void BreadcrumbsSeqlock(Action<bool, string> expect)
    {
        var path = Path.Combine(Path.GetTempPath(), $"handpeg-breadcrumbs-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        try
        {
            using var file = BreadcrumbFile.Open(path, "golden");
            if (file is null)
            {
                expect(false, "the breadcrumb file opens");
                return;
            }

            var stop = 0;
            var writer = new Thread(() =>
            {
                // detail = the event index the line is (events counts from 1), so a whole copy always agrees with itself.
                for (long i = 0; Volatile.Read(ref stop) == 0; i++)
                    file.Enter("ReadSample", i + 1, hot: true);
            }) { Name = "golden seqlock writer" };
            writer.Start();

            var copy = new byte[BreadcrumbFile.SlotBytes];
            var (copies, torn, skipped) = (0, 0, 0);
            // A writer that never pauses makes most copies retry (a real per-frame thread is idle between calls): keep copying until
            // 200 whole copies were checked, for 10 s at most.
            var (until, deadline) = (Environment.TickCount64 + 600, Environment.TickCount64 + 10_000);
            while ((Environment.TickCount64 < until || copies < 200) && Environment.TickCount64 < deadline)
            {
                if (!file.TryCopyOwnSlot(0, copy))
                {
                    skipped++;
                    Thread.Yield();
                    continue;
                }

                copies++;
                var text = Encoding.ASCII.GetString(copy);
                var events = Regex.Match(text, @"events=(\d+) ");
                if (!events.Success)
                    continue; // the slot exists before its first line is complete only for an instant
                var n = long.Parse(events.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var line = text.Substring((1 + (int)((n - 1) % BreadcrumbFile.RingLines)) * BreadcrumbFile.LineBytes, BreadcrumbFile.LineBytes);
                if (!line.Contains($"ReadSample luid=0x0 {n} ", StringComparison.Ordinal))
                    torn++;
            }

            Volatile.Write(ref stop, 1);
            writer.Join();
            expect(copies >= 200, $"copies taken while the thread kept writing: {(copies >= 200 ? "200 or more" : copies.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            expect(torn == 0, $"none torn: each copy's header (events=N) matches its newest ring line (detail N){(torn == 0 ? "" : $"; torn {torn}")}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    // ----- gc-collect-sites -----

    /// <summary>C# text with its line and block comments removed (string literals holding // are cut too: these probes only lose hits).</summary>
    internal static string WithoutComments(string text) => CommentPattern().Replace(text, "");

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    private static void CollectSites(Action<bool, string> expect)
    {
        var sources = Directory.GetFiles(Path.Combine(SourceProbe.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Name: Path.GetRelativePath(Path.Combine(SourceProbe.Root, "src"), f).Replace('\\', '/'), Text: WithoutComments(File.ReadAllText(f)))).ToList();
        string[] Using(string token) => sources.Where(s => s.Text.Contains(token, StringComparison.Ordinal)).Select(s => s.Name).Order(StringComparer.Ordinal).ToArray();

        var collects = Using("GC.Collect(");
        expect(collects.SequenceEqual(["Compositor/CompositorWorker.cs", "Services/CrashReporter.Reports.cs", "Services/MemoryHygiene.cs"]),
            $"GC.Collect only in MemoryHygiene (the quiet gate), the worker's idle trim and the crash path: {string.Join(", ", collects)}");
        expect(Using("EmptyWorkingSet").Length == 0, "no working-set trim (K32EmptyWorkingSet): Law 2 reads private bytes, not a trimmed working set");
        var heaps = Using("HeapCompact(");
        expect(heaps.SequenceEqual(["Native/Kernel32.cs", "Services/MemoryHygiene.cs"]), $"the process heap is compacted only after a quiet-gated collection: {string.Join(", ", heaps)}");
        expect(Using("GCCollectionMode.Aggressive").SequenceEqual(["Compositor/CompositorWorker.cs"]),
            "Aggressive collections only in the worker (no frame pump or audio there); the editor's are Forced (keeps the 64 MB gen-0 budget)");
    }
}
