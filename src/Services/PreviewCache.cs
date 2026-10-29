using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Pictures made from media files, kept on disk between sessions so that a file opened again does not have them
/// made again: a folder of files, each named after what it was made from (the file's path, size and time it was
/// last changed, so an edited file is a different one) and what it is ("sprite", "strip16", "peaks80-0"...). When the
/// folder grows past its limit, the files used longest ago go first.
///
/// Three of them: the timeline's (sheets of hover previews, the strips of frames on the blocks, waveform peaks,
/// I-frame lists), the media browser's (the frames a file's tile shows as the pointer moves over it) and the
/// proxies (small copies of heavy videos that Live Preview plays in their place; see ProxyCache). Each is switched
/// on and limited in Settings, Caches. Switched off, nothing is read or written, and callers make what they need in
/// the session's own folder as before.
///
/// Each folder keeps a manifest (manifest.tsv): one line per file kept, with the file it was made from, that file's
/// size and when it was written. That is what lets the entries of files that have since been deleted (or changed)
/// be found and removed (<see cref="PurgeMissing"/>): the names are one-way hashes and say nothing by themselves.
/// </summary>
public sealed class PreviewCache
{
    private const string ManifestName = "manifest.tsv";

    private readonly Func<bool> _enabled;
    private readonly Func<long> _limitBytes;
    private readonly object _manifestLock = new();
    private int _trimQueued;

    // Files used since the last trim, and when: their "last used" times are written in one go, by the trim, and
    // not as each is found (a write to the disk from whatever thread asked, the UI's included).
    private readonly ConcurrentDictionary<string, DateTime> _touched = new(StringComparer.OrdinalIgnoreCase);

    private PreviewCache(string name, string folder, Func<bool> enabled, Func<long> limitBytes) =>
        (Name, Folder, _enabled, _limitBytes) = (name, folder, enabled, limitBytes);

    /// <summary>The timeline's previews of the videos loaded: hover sheets, strips of frames, waveforms, I-frames.</summary>
    public static PreviewCache Timeline { get; } = new("Timeline previews",
        Path.Combine(AppPaths.Cache, "timeline"), () => AppSettings.Current.CacheTimelinePreviews, () => AppSettings.Current.TimelineCacheMegabytes * 1024L * 1024);

    /// <summary>The media browser's tiles: a few frames of each video, for the picture that follows the pointer.</summary>
    public static PreviewCache Browser { get; } = new("Media browser previews",
        Path.Combine(AppPaths.Cache, "browser"), () => AppSettings.Current.CacheBrowserPreviews, () => AppSettings.Current.BrowserCacheMegabytes * 1024L * 1024);

    /// <summary>The proxies: small copies of heavy videos, and the composed timeline proxy (see ProxyCache).</summary>
    public static PreviewCache Proxies { get; } = new("Proxies",
        Path.Combine(AppPaths.Cache, "proxies"), () => AppSettings.Current.UsesProxies, () => (long)(Math.Max(AppSettings.Current.ProxyCacheGigabytes, 0.25) * 1024 * 1024 * 1024));

    /// <summary>A proxy cache in a folder of its own: beside a project, when proxies are kept with it (Settings).</summary>
    public static PreviewCache ProjectProxies(string folder) => new("Proxies", folder, () => AppSettings.Current.UsesProxies,
        () => (long)(Math.Max(AppSettings.Current.ProxyCacheGigabytes, 0.25) * 1024 * 1024 * 1024));

    /// <summary>Every cache, for what treats them all alike: the startup purge, Settings.</summary>
    public static IReadOnlyList<PreviewCache> All { get; } = [Timeline, Browser, Proxies];

    /// <summary>What Settings calls it.</summary>
    public string Name { get; }

    public string Folder { get; }

    public bool IsEnabled => _enabled() && _limitBytes() > 0;

    /// <summary>
    /// The name a thing made from a file is kept under: the same for the same file (unchanged) and the same
    /// <paramref name="kind"/>; null when the cache is off or the file cannot be looked at. What is known of the
    /// file comes from memory after the first time (see FileState): this is asked for every tile and every clip.
    /// </summary>
    public string? PathFor(string mediaPath, string kind, string extension)
    {
        if (!IsEnabled)
            return null;

        try
        {
            var file = FileState.Get(mediaPath);
            if (!file.Exists)
                return null;

            var fullName = Path.GetFullPath(mediaPath.Trim().Trim('"'));
            var key = string.Create(CultureInfo.InvariantCulture, $"{fullName.ToUpperInvariant()}|{file.Length}|{file.WrittenUtc.Ticks}|{kind}");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 12).ToLowerInvariant();
            return Path.Combine(Folder, hash + extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The kept file for a file and a kind, when there is one; it then counts as just used.</summary>
    public string? Find(string mediaPath, string kind, string extension)
    {
        if (PathFor(mediaPath, kind, extension) is not { } path || FileState.Get(path) is not { Exists: true, Length: > 0 })
            return null;

        _touched[path] = DateTime.UtcNow;
        return path;
    }

    /// <summary>Somewhere to write a new one: the folder is made, and kept to its size a moment after.</summary>
    public string? PrepareWrite(string mediaPath, string kind, string extension)
    {
        if (PathFor(mediaPath, kind, extension) is not { } path)
            return null;

        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        QueueTrim();
        return path;
    }

    /// <summary>
    /// Takes a file just made (whole: it is only ever given one that was finished) into the cache, and says where
    /// it is now; where it was, when the cache is off or the move fails.
    /// </summary>
    public string Keep(string mediaPath, string kind, string extension, string made)
    {
        if (PrepareWrite(mediaPath, kind, extension) is not { } path)
            return made;

        try
        {
            File.Move(made, path, overwrite: true);
            Remember(path, kind, mediaPath);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return made;
        }
    }

    /// <summary>Reads the bytes kept for a file and a kind, or null.</summary>
    public byte[]? ReadBytes(string mediaPath, string kind, string extension)
    {
        try
        {
            return Find(mediaPath, kind, extension) is { } path ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps bytes for a file and a kind (written whole, then put in place, so a reader never sees half of them).</summary>
    public void WriteBytes(string mediaPath, string kind, string extension, ReadOnlySpan<byte> bytes)
    {
        // Copied once into a buffer the writer can carry (a span cannot be captured), never into an array of its own size.
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(bytes.Length, 1));
        try
        {
            bytes.CopyTo(rented);
            var length = bytes.Length;
            Write(mediaPath, kind, extension, stream => stream.Write(rented, 0, length));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Keeps what <paramref name="write"/> writes for a file and a kind, written whole and then put in place.</summary>
    public void Write(string mediaPath, string kind, string extension, Action<Stream> write)
    {
        if (PrepareWrite(mediaPath, kind, extension) is not { } path)
            return;

        var part = path + ".part";
        try
        {
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                write(stream);
            File.Move(part, path, overwrite: true);
            Remember(path, kind, mediaPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ----- The manifest -----

    private string ManifestPath => Path.Combine(Folder, ManifestName);

    /// <summary>One line in the manifest for a file just kept: what it is, and the file (as it was) it was made from.</summary>
    private void Remember(string path, string kind, string mediaPath)
    {
        FileState.Forget(path);
        var source = FileState.Get(mediaPath);
        string full;
        try
        {
            full = Path.GetFullPath(mediaPath.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        // Tabs and line ends cannot be in a Windows path; the line is safe as it is.
        var line = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)}\t{kind}\t{full}\t{source.Length}\t{source.WrittenUtc.Ticks}{Environment.NewLine}");
        lock (_manifestLock)
        {
            try
            {
                File.AppendAllText(ManifestPath, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not listed: the file is left to the size limit, as one from before there was a manifest.
            }
        }
    }

    /// <summary>One line of the manifest.</summary>
    public readonly record struct Entry(string File, string Kind, string Source, long Length, long WrittenTicks);

    /// <summary>The manifest's lines, the last one for each file (a file made again is listed again).</summary>
    public List<Entry> ReadManifest()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        lock (_manifestLock)
        {
            try
            {
                if (!File.Exists(ManifestPath))
                    return [];

                foreach (var line in File.ReadLines(ManifestPath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 5
                        && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
                        && long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                    {
                        entries[parts[0]] = new Entry(parts[0], parts[1], parts[2], length, ticks);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. entries.Values];
    }

    /// <summary>Writes the manifest again with only the lines of files that are still there.</summary>
    private void RewriteManifest()
    {
        var kept = ReadManifest().Where(e => File.Exists(Path.Combine(Folder, e.File))).ToList();
        lock (_manifestLock)
        {
            try
            {
                if (kept.Count == 0)
                {
                    File.Delete(ManifestPath);
                    return;
                }

                var text = new StringBuilder();
                foreach (var e in kept)
                    text.Append(CultureInfo.InvariantCulture, $"{e.File}\t{e.Kind}\t{e.Source}\t{e.Length}\t{e.WrittenTicks}").AppendLine();
                File.WriteAllText(ManifestPath + ".part", text.ToString());
                File.Move(ManifestPath + ".part", ManifestPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ----- Size -----

    /// <summary>How much is kept, in bytes, and in how many files.</summary>
    public (long Bytes, int Files) Measure()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return (0, 0);

            var files = new DirectoryInfo(Folder).GetFiles().Where(f => f.Name != ManifestName).ToList();
            return (files.Sum(f => f.Length), files.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    /// <summary>Deletes everything kept. Files in use are left for the next time.</summary>
    public void Clear() => TrimTo(0);

    /// <summary>Trims the folder to its limit on the thread pool, once, however many files were just written.</summary>
    public void QueueTrim()
    {
        if (Interlocked.Exchange(ref _trimQueued, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref _trimQueued, 0);
            TrimTo(IsEnabled ? _limitBytes() : 0);
        });
    }

    /// <summary>
    /// Deletes the files used longest ago until what is left is no more than <paramref name="bytes"/>. The "last used"
    /// times of the files found since the last trim are written first, so that they count as the recent ones they are.
    /// </summary>
    /// <param name="spare">Files never deleted, whatever their age: a proxy that is playing.</param>
    public void TrimTo(long bytes, IReadOnlySet<string>? spare = null)
    {
        try
        {
            FlushTouches();
            if (!Directory.Exists(Folder))
                return;

            var files = new DirectoryInfo(Folder).GetFiles().Where(f => f.Name != ManifestName)
                .OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc).ToList();
            var (kept, deleted) = (0L, false);
            foreach (var file in files)
            {
                // A part left by a write that never finished is never kept (unless it is being written now: it is in use).
                if ((kept + file.Length <= bytes || spare?.Contains(file.FullName) == true) && file.Extension != ".part")
                {
                    kept += file.Length;
                    continue;
                }

                try
                {
                    file.Delete();
                    FileState.Forget(file.FullName);
                    deleted = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use: it goes next time.
                }
            }

            if (deleted || bytes == 0)
                RewriteManifest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void FlushTouches()
    {
        foreach (var (path, when) in _touched.ToArray())
        {
            _touched.TryRemove(path, out _);
            try
            {
                File.SetLastAccessTimeUtc(path, when);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    // ----- Entries of files that are gone -----

    /// <summary>
    /// Deletes what was kept for files that have been deleted since, or changed (that version can never be asked for
    /// again), as the manifest lists them. A file on a drive that is not there now (an unplugged USB drive, a share
    /// that is offline) is only unreachable: its entries are kept, unless <paramref name="missingDrivesCountAsDeleted"/>.
    /// Files kept before there was a manifest are left to the size limit. Returns how many files were deleted and their bytes.
    /// </summary>
    public (int Files, long Bytes) PurgeMissing(bool missingDrivesCountAsDeleted, CancellationToken token = default)
    {
        var (count, bytes) = (0, 0L);
        var drives = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ReadManifest())
        {
            token.ThrowIfCancellationRequested();
            if (!Gone(entry.Source, entry.Length, entry.WrittenTicks))
                continue;

            var path = Path.Combine(Folder, entry.File);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    continue;

                var length = info.Length;
                info.Delete();
                FileState.Forget(path);
                (count, bytes) = (count + 1, bytes + length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use: it goes next time.
            }
        }

        if (count > 0)
            RewriteManifest();
        return (count, bytes);

        bool Gone(string source, long length, long ticks)
        {
            try
            {
                var root = Path.GetPathRoot(source) ?? "";
                if (!drives.TryGetValue(root, out var ready))
                {
                    // A share is there when its folder can be looked at; a drive when Windows says it is ready.
                    ready = root.StartsWith(@"\\", StringComparison.Ordinal) ? Directory.Exists(root) : root.Length > 0 && new DriveInfo(root).IsReady;
                    drives[root] = ready;
                }

                if (!ready)
                    return missingDrivesCountAsDeleted;

                var file = new FileInfo(source);
                return !file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Cannot be looked at now: kept.
                return false;
            }
        }
    }

    /// <summary>Formats a size in bytes for Settings: "under 1 MB", "312 MB", "1.4 GB".</summary>
    public static string Describe(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB")
        : bytes < 1024 * 1024
            ? "under 1 MB"
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / (1024.0 * 1024))} MB");
}
