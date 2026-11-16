using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Whether files are there, and how large and how new they are, answered from memory: for code on the UI thread
/// that asks about the same files over and over (every mask, LUT and caption file each time a slider moves, the
/// main video each time Live Preview is rebuilt). A drive that is slow, asleep or across the network would
/// otherwise stall the window at every one of those questions.
///
/// The first question about a file looks at the disk once. After that the answer comes from memory: on a fixed
/// drive it is kept fresh by a watcher on the file's folder, which forgets it the moment anything there changes;
/// elsewhere (removable and network drives get no watcher: its open folder would keep a USB drive from being
/// removed safely) an answer older than <see cref="FreshFor"/> is given as it is and looked at again in the
/// background, so the next question has it.
/// </summary>
public static class FileState
{
    /// <summary>What is known of one file.</summary>
    /// <param name="Exists">Whether it was there.</param>
    /// <param name="Length">Its size in bytes; 0 when it was not there.</param>
    /// <param name="WrittenUtc">When it was last written.</param>
    public readonly record struct Entry(bool Exists, long Length, DateTime WrittenUtc, long CheckedAt);

    /// <summary>How long an answer about a file in a folder with no watcher is taken as it is.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);

    /// <summary>How long an answer about a file in a watched folder is taken as it is before it is looked at again anyway (settable for goldens).</summary>
    internal static TimeSpan WatchedFreshFor { get; set; } = TimeSpan.FromSeconds(30);

    // Live watchers kept at most: past this, folders are looked at again by age only. Counted apart from the table, whose null
    // entries (folders not watched) must not use up the room.
    private const int MostWatchers = 64;
    private static int _liveWatchers;

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FileSystemWatcher?> Watchers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Refreshing = new(StringComparer.OrdinalIgnoreCase);

    public static bool Exists(string? path) => Get(path).Exists;

    /// <summary>What is known of a file: from memory when it was asked about before, from the disk the first time.</summary>
    public static Entry Get(string? path)
    {
        if (Normalize(path) is not { } full)
            return default;

        if (Entries.TryGetValue(full, out var known))
        {
            // Old: given as it is, and looked at again in the background (a watched folder's change makes it forget at once as well).
            var freshFor = IsWatched(full) ? WatchedFreshFor : FreshFor;
            if (Environment.TickCount64 - known.CheckedAt > freshFor.TotalMilliseconds && Refreshing.TryAdd(full, 0))
            {
                _ = Task.Run(() =>
                {
                    Look(full);
                    Refreshing.TryRemove(full, out _);
                });
            }

            return known;
        }

        Watch(full);
        return Look(full);
    }

    /// <summary>Forgets what is known of a file: one HandPeg has just written, moved or deleted itself.</summary>
    public static void Forget(string? path)
    {
        if (Normalize(path) is { } full)
            Entries.TryRemove(full, out _);
    }

    private static Entry Look(string full)
    {
        Entry entry;
        try
        {
            var info = new FileInfo(full);
            entry = info.Exists
                ? new Entry(true, info.Length, info.LastWriteTimeUtc, Environment.TickCount64)
                : new Entry(false, 0, default, Environment.TickCount64);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            entry = new Entry(false, 0, default, Environment.TickCount64);
        }

        Entries[full] = entry;
        return entry;
    }

    private static string? Normalize(string? path)
    {
        var trimmed = (path ?? "").Trim().Trim('"');
        if (trimmed.Length == 0 || trimmed.Contains("://", StringComparison.Ordinal))
            return null;

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsWatched(string full) => Path.GetDirectoryName(full) is { } folder && Watchers.TryGetValue(folder, out var watcher) && watcher is not null;

    /// <summary>Starts watching the file's folder, when it is on a fixed drive and there is room for another watcher.</summary>
    private static void Watch(string full)
    {
        if (Path.GetDirectoryName(full) is not { } folder || Watchers.ContainsKey(folder))
            return;

        // Whether the drive can be unplugged is learnt once per volume off this thread: until then the folder is not watched (and not
        // marked, so a later question about a file in it tries again).
        if (DriveKind.IsHotPluggable(folder) is not { } pluggable)
        {
            _ = DriveKind.Learn(folder);
            return;
        }

        FileSystemWatcher? watcher = null;
        try
        {
            // Never watched (a drive that can be unplugged, a network or non-fixed drive): marked, so it is not asked again.
            if (pluggable || Path.GetPathRoot(folder) is not { Length: > 0 } root || root.StartsWith(@"\\", StringComparison.Ordinal)
                || new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                Watchers.TryAdd(folder, null);
                return;
            }

            // Not now (the folder is not there yet, or every watcher is in use): not marked, so a later question tries again.
            if (Volatile.Read(ref _liveWatchers) >= MostWatchers || !Directory.Exists(folder))
                return;

            watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                InternalBufferSize = 16384,
            };
            watcher.Changed += (_, e) => Forget(e.FullPath);
            watcher.Created += (_, e) => Forget(e.FullPath);
            watcher.Deleted += (_, e) => Forget(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Forget(e.OldFullPath);
                Forget(e.FullPath);
            };

            // Too much happened at once to say what (a buffer overflow; the watcher goes on): everything in the folder is looked at
            // again. Any other error stops the watcher (its folder deleted, its volume gone): it is removed and disposed as well.
            watcher.Error += (sender, e) =>
            {
                if (e.GetException() is not InternalBufferOverflowException && sender is FileSystemWatcher failed)
                    Retire(folder, failed);
                ForgetFolder(folder);
            };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            watcher = null;
        }

        if (!Watchers.TryAdd(folder, watcher))
            watcher?.Dispose();
        else if (watcher is not null)
            Interlocked.Increment(ref _liveWatchers);
    }

    /// <summary>A watcher that stopped: out of the table (so the folder can be watched again) and disposed.</summary>
    private static void Retire(string folder, FileSystemWatcher watcher)
    {
        if (!Watchers.TryRemove(KeyValuePair.Create(folder, (FileSystemWatcher?)watcher)))
            return;
        Interlocked.Decrement(ref _liveWatchers);
        watcher.Dispose();
    }

    /// <summary>Whether a folder has a live watcher (for goldens).</summary>
    internal static bool IsFolderWatched(string folder) => Watchers.TryGetValue(Path.GetFullPath(folder), out var watcher) && watcher is not null;

    /// <summary>Raises a watcher's failure as a dead folder would (for goldens): the watcher is retired and the folder forgotten.</summary>
    internal static void FailWatcherForTests(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (Watchers.TryGetValue(full, out var watcher) && watcher is not null)
        {
            Retire(full, watcher);
            ForgetFolder(full);
        }
    }

    private static void ForgetFolder(string folder)
    {
        foreach (var path in Entries.Keys)
        {
            if (string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase))
                Entries.TryRemove(path, out _);
        }
    }
}
