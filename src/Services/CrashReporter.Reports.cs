using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HandPegApp.Services;

// The reports HandPeg writes itself, for managed exceptions, and the developer's way to try every kind of crash.
public static partial class CrashReporter
{
    /// <summary>How many frames a report keeps (innermost first, across the exception and those inside it).</summary>
    private const int KeptFrames = 64;

    // Signatures written this session: a fault the process survives (a task nobody looked at, a dispatcher exception
    // something marked handled) is written once, however often it happens.
    private static readonly HashSet<string> WrittenSignatures = [];

    // The exception the dispatcher last passed on, so the AppDomain handler can tell a dispatcher fault from another.
    private static Exception? _dispatcherException;

    // ----- The handlers -----

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception)
            return;

        // A native fault the filter has recorded, which the runtime may pass on here as well, is the sentinel's to
        // report: it knows the module the fault was in, and this handler would only have an empty stack.
        if (Volatile.Read(ref _faulted) != 0 && IsWatching && exception is AccessViolationException or SEHException)
            return;

        var kind = ReferenceEquals(exception, _dispatcherException) ? CrashKind.Dispatcher : CrashKind.Managed;
        WriteReport(exception, kind, fatal: e.IsTerminating);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Note("fault", "dispatcher: " + e.Exception.GetType().Name);
        _dispatcherException = e.Exception;

        // Whether the window survives it is known only once every handler has run. If one marks it handled, the
        // dispatcher carries on and this runs, writing a report of a fault survived; if none does, the exception ends
        // the process on its way out and the AppDomain handler writes it, as the dispatcher fault it was.
        var args = e;
        ((Dispatcher)sender).BeginInvoke(() =>
        {
            if (args.Handled)
                WriteReport(args.Exception, CrashKind.Dispatcher, fatal: false);
        }, DispatcherPriority.Send);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Left unobserved, as before (it no longer ends the process). A task's one exception is reported as itself,
        // not as the AggregateException around it, so that its signature says what failed.
        var exception = e.Exception.InnerExceptions is [var only] ? only : e.Exception;
        Note("fault", "unobserved task: " + exception.GetType().Name);
        WriteReport(exception, CrashKind.UnobservedTask, fatal: false);
    }

    /// <summary>
    /// Writes a report of a managed exception. Nothing may escape from here: it runs while the process is falling over,
    /// on whatever thread that happens on.
    /// </summary>
    private static void WriteReport(Exception exception, CrashKind kind, bool fatal)
    {
        try
        {
            if (!IsEnabled)
                return;

            var type = exception.GetType().FullName ?? exception.GetType().Name;
            var frames = Frames(exception);
            var signature = CrashSignature.Compute(type, frames);
            lock (WrittenSignatures)
            {
                if (!WrittenSignatures.Add(signature) && !fatal)
                    return;
            }

            var report = new CrashReport
            {
                Format = CrashReport.FormatName,
                Version = CrashReport.CurrentVersion,
                Signature = signature,
                AppVersion = CrashReports.AppVersion,
                CrashedAtUtc = DateTime.UtcNow,
                Kind = kind,
                Fatal = fatal,
                ExceptionType = type,
                Message = CrashSanitizer.Sanitize(exception.Message),
                Stack = frames,
                Inner = InnerExceptions(exception),
                Environment = DescribeEnvironment(),
                Breadcrumbs = SnapshotBreadcrumbs(),
                Context = SnapshotContext(),
            };

            CrashReports.Write(report, AppPaths.Crashes);

            // The sentinel must not write a second report of the death this one describes.
            if (fatal)
            {
                unsafe
                {
                    var shared = (byte*)Volatile.Read(ref _shared);
                    if (shared != null)
                        CrashSharedMemory.SetFlag(shared, CrashSharedMemory.FlagManagedReportWritten);
                }
            }
        }
        catch (Exception failure)
        {
            // Nothing escapes a crash handler; the log is tried, and a log that cannot be written is let be.
            try
            {
                AppLog.Write("A crash report could not be written", failure);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
    }

    // ----- What a report holds -----

    /// <summary>
    /// The frames of the exception and of those inside it, outermost exception first, each normalized to its type and
    /// method (<see cref="CrashSignature.NormalizeFrame"/>): no parameters, files, lines or offsets. Read from the stack
    /// trace's text, which keeps the frames from before an await or a rethrow that a StackTrace object would lose.
    /// </summary>
    private static List<string> Frames(Exception exception)
    {
        var frames = new List<string>();
        for (Exception? at = exception; at is not null && frames.Count < KeptFrames; at = at.InnerException)
        {
            foreach (var line in (at.StackTrace ?? "").Split('\n'))
            {
                var text = line.Trim();

                // "--- End of stack trace from previous location ---" and the like are not frames. A frame is "at"
                // and a method (in whatever language the runtime speaks: the first word, then a name with a call).
                var space = text.IndexOf(' ');
                if (text.StartsWith("---", StringComparison.Ordinal) || space <= 0 || text.IndexOf('(') < space)
                    continue;

                var frame = CrashSignature.NormalizeFrame(text[(space + 1)..]);
                if (frame.Length > 0 && frames.Count < KeptFrames)
                    frames.Add(frame);
            }
        }

        return frames;
    }

    /// <summary>The exceptions inside this one, outermost first (and each of an AggregateException's), as type and sanitized message.</summary>
    private static List<CrashInnerException>? InnerExceptions(Exception exception)
    {
        var inner = new List<CrashInnerException>();
        var inside = exception is AggregateException aggregate ? aggregate.InnerExceptions.AsEnumerable() : [];
        for (var at = exception.InnerException; at is not null; at = at.InnerException)
            inside = inside.Append(at);

        foreach (var one in inside.Distinct().Take(8))
            inner.Add(new CrashInnerException { Type = one.GetType().FullName ?? one.GetType().Name, Message = CrashSanitizer.Sanitize(one.Message) });

        return inner.Count > 0 ? inner : null;
    }

    /// <summary>The breadcrumbs, oldest first, sanitized. A slot caught mid-write is skipped.</summary>
    private static List<CrashBreadcrumb> SnapshotBreadcrumbs()
    {
        var taken = new List<(long Sequence, double At, string Category, string? Detail)>(Ring);
        for (var slot = 0; slot < Ring; slot++)
        {
            var sequence = Volatile.Read(ref Sequences[slot]);
            var (at, category, detail) = (Ats[slot], Categories[slot], Details[slot]);
            Interlocked.MemoryBarrier();
            if (sequence > 0 && category is not null && Volatile.Read(ref Sequences[slot]) == sequence)
                taken.Add((sequence, at, category, detail));
        }

        taken.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        var offset = StartOffset();
        return taken.Select(c => new CrashBreadcrumb
        {
            At = Math.Round(c.At + offset, 3),
            Category = CrashSanitizer.Sanitize(c.Category, 24),
            Detail = CrashSanitizer.Sanitize(c.Detail, 300),
        }).ToList();
    }

    /// <summary>The context slots (not the "env." facts), sanitized; null when none is set.</summary>
    private static Dictionary<string, string>? SnapshotContext()
    {
        var context = new Dictionary<string, string>();
        for (var slot = 0; slot < ContextKeys.Length; slot++)
        {
            if (Volatile.Read(ref ContextKeys[slot]) is { } key && !key.StartsWith("env.", StringComparison.Ordinal)
                && Volatile.Read(ref ContextValues[slot]) is { Length: > 0 } value)
            {
                context[CrashSanitizer.Sanitize(key, 24)] = CrashSanitizer.Sanitize(value, 300);
            }
        }

        return context.Count > 0 ? context : null;
    }

    /// <summary>The computer and the program, from what is already known: nothing here enumerates or probes.</summary>
    private static CrashEnvironment DescribeEnvironment()
    {
        // The one interface HandPeg has (before 4.2 Editor or Encoder Mode); the format keeps the field.
        const string mode = AppSettings.EditorMode;

        long workingSet;
        try
        {
            workingSet = Environment.WorkingSet;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            workingSet = 0;
        }

        var environment = new CrashEnvironment
        {
            Os = CrashReports.OsDescription(),
            Runtime = CrashReports.RuntimeDescription(),
            ProcessorCount = Environment.ProcessorCount,
            Gpus = (_gpus ?? []).Select(g => CrashSanitizer.Sanitize(g, 160)).ToList(),
            WorkingSetMb = workingSet / (1024 * 1024),
            ManagedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024),
            UptimeSeconds = Math.Round(Now + StartOffset(), 1),
            Mode = mode,
            SelfContained = AppPaths.IsSelfContained,
            Ffmpeg = _ffmpegVersion is null ? null : CrashSanitizer.Sanitize(_ffmpegVersion, 100),
            Mpv = _mpvVersion is null ? null : CrashSanitizer.Sanitize(_mpvVersion, 100),
        };

        // What was learnt after the first frame: before it, a report says only the above.
        if (_gpus is not null)
        {
            var (theme, efficiency) = CurrentTheme();
            CrashHardware.Fill(environment, _cards, theme, efficiency);
        }

        return environment;
    }

    // ----- A way to try it out, for whoever is working on HandPeg -----

    /// <summary>The environment variable that asks for a test crash after the first frame. Not a setting.</summary>
    public const string TestCrashVariable = "HANDPEG_TEST_CRASH";

    /// <summary>
    /// HANDPEG_TEST_CRASH=managed|dispatcher|task|native|native-null makes HandPeg fall over in that way, once, after
    /// the first frame, so that the whole path (the handler or the sentinel, the file, HandPeg.CrashDesk) can be tried:
    /// managed, an exception on a thread of its own; dispatcher, one on the window's thread; task, a task's exception
    /// nobody looks at (HandPeg carries on); native, an access violation inside ntdll.dll on a thread the runtime knows
    /// nothing of, as a fault in libmpv or a driver arrives (the sentinel names the module and the offset);
    /// native-null, a thread started at address 0, which Windows' control-flow guard ends at once with a fail-fast
    /// (0xC0000409) that no filter sees: the sentinel writes it from the exit code alone.
    /// </summary>
    public static void TriggerTestCrashIfRequested(Dispatcher dispatcher)
    {
        var how = Environment.GetEnvironmentVariable(TestCrashVariable)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(how))
            return;

        Note("test", TestCrashVariable + "=" + how);

        // The sentinel is being started on a pool thread at this moment: with crash reports on, the test waits until it
        // is watching (ten seconds at most), so that whichever of the two should write the report is there to write it.
        _ = Task.Run(async () =>
        {
            for (var waited = 0; IsEnabled && !IsWatching && waited < 100; waited++)
                await Task.Delay(100);
            TriggerTestCrash(how, dispatcher);
        });
    }

    /// <summary>
    /// Falls over in one of the ways <see cref="TriggerTestCrashIfRequested"/> lists, from any thread. Each throws from a
    /// method of its own, so that each kind has a signature of its own in HandPeg.CrashDesk.
    /// </summary>
    public static unsafe void TriggerTestCrash(string how, Dispatcher? dispatcher)
    {
        switch (how)
        {
            case "managed":
                new Thread(TestCrashOnThreadOfItsOwn) { IsBackground = true, Name = "test crash" }.Start();
                break;

            case "dispatcher":
                dispatcher?.BeginInvoke(new Action(TestCrashOnWindowThread));
                break;

            case "task":
                _ = Task.Run(TestCrashInTask);

                // Seen only when the task is collected: a collection and its finalizers are asked for a moment later.
                _ = Task.Delay(500).ContinueWith(_ =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }, TaskScheduler.Default);
                break;

            case "native":
                // RtlEnterCriticalSection given a "critical section" at 0x10 writes into the first page, which is never mapped.
                var enter = NativeLibrary.GetExport(NativeLibrary.Load("ntdll.dll"), "RtlEnterCriticalSection");
                _ = CreateThread(0, 0, enter, 0x10, 0, null);
                break;

            case "native-null":
                _ = CreateThread(0, 0, 0, 0, 0, null);
                break;
        }
    }

    private static void TestCrashOnThreadOfItsOwn() =>
        throw new InvalidOperationException("HANDPEG_TEST_CRASH: an exception nobody handles, on a thread of its own.");

    private static void TestCrashOnWindowThread() =>
        throw new InvalidOperationException("HANDPEG_TEST_CRASH: an exception on the window's thread.");

    private static void TestCrashInTask() =>
        throw new InvalidOperationException("HANDPEG_TEST_CRASH: a task's exception nobody looks at.");

    [LibraryImport("kernel32.dll")]
    private static unsafe partial nint CreateThread(nint attributes, nuint stackSize, nint startAddress, nint parameter, uint flags, uint* threadId);
}
