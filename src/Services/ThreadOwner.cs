using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace HandPegApp.Services;

/// <summary>
/// A thread-affinity rule checked in code instead of stated only in a comment: the first thread that calls <see cref="Check"/>
/// becomes the owner (the UI thread for a broker made there, a server thread for a strip worker's own broker, the golden runner's
/// thread in tests). A call from any other thread is logged once per member with its stack and counted in
/// <see cref="ViolationCount"/>, which the golden runner asserts is 0 at the end of a run. It never throws (a Debug.Fail would
/// end a golden run and is compiled out of release builds anyway) and costs one integer compare when the rule holds.
/// </summary>
internal sealed class ThreadOwner
{
    private static readonly ConcurrentDictionary<string, byte> Logged = new(StringComparer.Ordinal);
    private static int _violations;
    private int _owner;

    /// <summary>How many calls broke a thread rule in this process.</summary>
    public static int ViolationCount => Volatile.Read(ref _violations);

    /// <summary>Asserts the caller is on the owning thread (taking ownership on the first call).</summary>
    public void Check([CallerMemberName] string member = "")
    {
        var id = Environment.CurrentManagedThreadId;
        var owner = Volatile.Read(ref _owner);
        if (owner == id || (owner == 0 && Interlocked.CompareExchange(ref _owner, id, 0) == 0))
            return;

        Interlocked.Increment(ref _violations);
        if (Logged.TryAdd(member, 0))
            AppLog.Write($"Thread rule broken: {member} was called on thread {id}; its owner is thread {owner}.{Environment.NewLine}{Environment.StackTrace}");
    }
}
