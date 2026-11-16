using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HandPegApp.Services;

// The reports HandPeg writes itself, for managed exceptions, and the developer's way to try every kind of crash.
public static partial class CrashReporter
{
    /// <summary>How many frames a report keeps (innermost first, across the exception and those inside it).</summary>
    private const int KeptFrames = 64;

    // Signatures reported this session: survivable faults (unobserved tasks, handled dispatcher exceptions) are written once.
    private static readonly HashSet<string> WrittenSignatures = [];

    // The exception the dispatcher last passed on, so the AppDomain handler can tell a dispatcher fault from another.
    private static Exception? _dispatcherException;

    // ----- The handlers -----

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception)
            return;

        // Filter-recorded native faults are the sentinel's to report (it knows the module; this handler sees an empty stack).
        if (Volatile.Read(ref _faulted) != 0 && IsWatching && exception is AccessViolationException or SEHException)
            return;

        var kind = ReferenceEquals(exception, _dispatcherException) ? CrashKind.Dispatcher : CrashKind.Managed;
        WriteReport(exception, kind, fatal: e.IsTerminating);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Note("fault", "dispatcher: " + e.Exception.GetType().Name);
        _dispatcherException = e.Exception;

        // Survival is known only after all handlers run: if one marks it handled, this writes a survived-fault report; otherwise the
        // process dies and the AppDomain handler reports it as a dispatcher fault.
        var args = e;
        ((Dispatcher)sender).BeginInvoke(() =>
        {
            if (args.Handled)
                WriteReport(args.Exception, CrashKind.Dispatcher, fatal: false);
        }, DispatcherPriority.Send);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Left unobserved (non-fatal). A single inner exception is reported unwrapped from its AggregateException for a precise signature.
        var exception = e.Exception.InnerExceptions is [var only] ? only : e.Exception;
        Note("fault", "unobserved task: " + exception.GetType().Name);
        WriteReport(exception, CrashKind.UnobservedTask, fatal: false);
    }

    /// <summary>
    /// Writes a managed exception report. Must never throw: runs during process failure on any thread.
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
                DevVerbose = DevFlightRecorder.ForReport(exception),
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
    /// Normalized frames (<see cref="CrashSignature.NormalizeFrame"/>) of the exception and inner exceptions, outermost first. Parsed from
    /// the stack trace text, which retains pre-await/rethrow frames a StackTrace object loses.
    /// </summary>
    private static List<string> Frames(Exception exception)
    {
        var frames = new List<string>();
        for (Exception? at = exception; at is not null && frames.Count < KeptFrames; at = at.InnerException)
        {
            foreach (var line in (at.StackTrace ?? "").Split('\n'))
            {
                var text = line.Trim();

                // Skip separators ("--- End of stack trace ..."); a frame is a localized "at" word followed by a method call.
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

    /// <summary>Inner exceptions (including all AggregateException members), outermost first, as type and sanitized message.</summary>
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
        // Single UI mode now; the format retains the legacy field.
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
    /// HANDPEG_TEST_CRASH=managed|dispatcher|task|native|native-null triggers one crash after Frame 1 to exercise the full path (handler
    /// or sentinel, file, HandPeg.CrashDesk): managed (worker thread exception), dispatcher (UI thread), task (unobserved, non-fatal),
    /// native (access violation in ntdll on a non-runtime thread; sentinel resolves module and offset), native-null (thread at address 0,
    /// CFG fail-fast 0xC0000409 invisible to filters; sentinel reports from the exit code).
    /// </summary>
    public static void TriggerTestCrashIfRequested(Dispatcher dispatcher)
    {
        var how = Environment.GetEnvironmentVariable(TestCrashVariable)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(how))
            return;

        Note("test", TestCrashVariable + "=" + how);

        // The sentinel starts concurrently on the pool; with crash reports on, wait (max 10 s) until it is watching so the right writer exists.
        _ = Task.Run(async () =>
        {
            for (var waited = 0; IsEnabled && !IsWatching && waited < 100; waited++)
                await Task.Delay(100);
            TriggerTestCrash(how, dispatcher);
        });
    }

    /// <summary>
    /// Performs a <see cref="TriggerTestCrashIfRequested"/> crash kind from any thread; each kind uses its own method for a distinct signature.
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
                // RtlEnterCriticalSection on address 0x10 writes into the never-mapped first page.
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
