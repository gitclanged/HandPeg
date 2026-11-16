using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// Memoized file existence, size and write time for UI-thread code that queries the same files repeatedly (masks, LUTs, captions on
/// every slider move), so slow, sleeping or network drives never stall the window.
///
/// The first query hits disk; afterwards answers come from memory. Fixed drives are kept fresh by a folder watcher that invalidates on
/// any change; removable and network drives get no watcher (it would block safe removal), so entries older than <see cref="FreshFor"/>
/// are returned stale and refreshed in the background.
/// </summary>
public static class FileState
{
    /// <summary>Cached file facts.</summary>
    /// <param name="Exists">True if present.</param>
    /// <param name="Length">Size in bytes; 0 when absent.</param>
    /// <param name="WrittenUtc">Last write time (UTC).</param>
    public readonly record struct Entry(bool Exists, long Length, DateTime WrittenUtc, long CheckedAt);

    /// <summary>How long an answer about a file in a folder with no watcher is taken as it is.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);

    /// <summary>Max age before a background refresh, even in watched folders (settable for goldens).</summary>
    internal static TimeSpan WatchedFreshFor { get; set; } = TimeSpan.FromSeconds(30);

    // Max live watchers (beyond it, folders refresh by age only); counted separately since null table entries mark unwatched folders.
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
            // Stale: return as-is and refresh in the background (watched folders also invalidate immediately on change).
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

    /// <summary>Watches the file's folder if it is on a fixed drive and watcher capacity remains.</summary>
    private static void Watch(string full)
    {
        if (Path.GetDirectoryName(full) is not { } folder || Watchers.ContainsKey(folder))
            return;

        // Drive removability is resolved once per volume off-thread; until then the folder stays unwatched and unmarked (retried later).
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

            // Folder missing or no capacity: leave unmarked so a later query retries.
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

            // Buffer overflow (watcher continues): invalidate the whole folder. Other errors (folder deleted, volume gone): retire and dispose.
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

    /// <summary>Test hook: simulates a fatal watcher error (watcher retired, folder forgotten).</summary>
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
