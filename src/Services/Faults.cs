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
    public static async void Forget(Task work, string what)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            // A cancelled operation (a window closed, a newer request) is not a failure.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write($"{what} failed", ex);
            CrashReporter.Note("fault", $"{what}: {ex.GetType().Name}");
        }
    }
}
