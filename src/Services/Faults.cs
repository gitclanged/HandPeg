namespace HandPegApp.Services;

/// <summary>
/// The one place fire-and-forget work is started from: an event handler or a timer callback that starts asynchronous work hands
/// its task to <see cref="Forget"/> instead of being <c>async void</c>. An exception escaping an <c>async void</c> method is
/// rethrown on the dispatcher and ends the process; through <see cref="Forget"/> it is logged with its stack and the editor
/// keeps running. Cancellation is not a fault and is dropped silently.
/// </summary>
/// <remarks>The <c>async-void-guarded</c> golden keeps <c>async void</c> out of every source file but this one.</remarks>
public static class Faults
{
    /// <summary>Observes <paramref name="work"/>: a fault is logged under <paramref name="what"/> (the caller's member name).</summary>
    public static void Forget(Task work, string what)
    {
        // A continuation (no void-returning async method): only a fault is observed (cancellation, a window closed or a newer request, is not a
        // failure). What it does not handle (OutOfMemoryException, a failing log write) goes where the old observer sent it: rethrown on the
        // caller's context (the dispatcher for UI callers), else on the thread pool, so the unhandled-exception path still sees it.
        var context = SynchronizationContext.Current;
        work.ContinueWith(static (done, state) =>
        {
            var (name, context) = ((string, SynchronizationContext?))state!;
            try
            {
                // Rethrown as await would (its stack kept), so the log entry is the same.
                done.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                try
                {
                    AppLog.Write($"{name} failed", ex);
                    CrashReporter.Note("fault", $"{name}: {ex.GetType().Name}");
                }
                catch (Exception logging)
                {
                    Escalate(logging, context);
                }
            }
            catch (Exception ex)
            {
                Escalate(ex, context);
            }
        }, (what, context), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Rethrows <paramref name="ex"/> (stack kept) on <paramref name="context"/>, else on the thread pool: an unhandled exception.</summary>
    private static void Escalate(Exception ex, SynchronizationContext? context)
    {
        var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        if (context is not null)
            context.Post(static held => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)held!).Throw(), captured);
        else
            ThreadPool.QueueUserWorkItem(static held => held.Throw(), captured, preferLocal: false);
    }
}
