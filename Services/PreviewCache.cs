using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Pictures made from media files, kept on disk between sessions so that a file opened again does not have them
/// made again: a folder of files, each named after what it was made from (the file's path, size and time it was
/// last changed, so an edited file is a different one) and what it is ("sprite", "strip16", "wave0"...). When the
/// folder grows past its limit, the files used longest ago go first.
///
/// Two of them: the timeline's (sheets of hover previews, the strips of frames on the blocks, waveform peaks,
/// I-frame lists) and the media browser's (the frames a file's tile shows as the pointer moves over it). Each is
/// switched on and limited in Settings. Switched off, nothing is read or written, and callers make what they
/// need in the session's own folder as before.
/// </summary>
public sealed class PreviewCache
{
    private readonly Func<bool> _enabled;
    private readonly Func<int> _megabytes;
    private int _trimQueued;

    private PreviewCache(string folder, Func<bool> enabled, Func<int> megabytes) => (Folder, _enabled, _megabytes) = (folder, enabled, megabytes);

    /// <summary>The timeline's previews of the videos loaded: hover sheets, strips of frames, waveforms, I-frames.</summary>
    public static PreviewCache Timeline { get; } = new(
        Path.Combine(AppPaths.Cache, "timeline"), () => AppSettings.Current.CacheTimelinePreviews, () => AppSettings.Current.TimelineCacheMegabytes);

    /// <summary>The media browser's tiles: a few frames of each video, for the picture that follows the pointer.</summary>
    public static PreviewCache Browser { get; } = new(
        Path.Combine(AppPaths.Cache, "browser"), () => AppSettings.Current.CacheBrowserPreviews, () => AppSettings.Current.BrowserCacheMegabytes);

    public string Folder { get; }

    public bool IsEnabled => _enabled() && _megabytes() > 0;

    /// <summary>
    /// The name a thing made from a file is kept under: the same for the same file (unchanged) and the same
    /// <paramref name="kind"/>; null when the cache is off or the file cannot be looked at.
    /// </summary>
    public string? PathFor(string mediaPath, string kind, string extension)
    {
        if (!IsEnabled)
            return null;

        try
        {
            var info = new FileInfo(mediaPath);
            if (!info.Exists)
                return null;

            var key = string.Create(CultureInfo.InvariantCulture, $"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{kind}");
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
        if (PathFor(mediaPath, kind, extension) is not { } path)
            return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
                return null;

            info.LastAccessTimeUtc = DateTime.UtcNow;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        if (PrepareWrite(mediaPath, kind, extension) is not { } path)
            return;

        var part = path + ".part";
        try
        {
            File.WriteAllBytes(part, bytes.ToArray());
            File.Move(part, path, overwrite: true);
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

    /// <summary>How much is kept, in bytes, and in how many files.</summary>
    public (long Bytes, int Files) Measure()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return (0, 0);

            var files = new DirectoryInfo(Folder).GetFiles();
            return (files.Sum(f => f.Length), files.Length);
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
            TrimTo(IsEnabled ? _megabytes() * 1024L * 1024 : 0);
        });
    }

    /// <summary>Deletes the files used longest ago until what is left is no more than <paramref name="bytes"/>.</summary>
    public void TrimTo(long bytes)
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;

            var files = new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc).ToList();
            var kept = 0L;
            foreach (var file in files)
            {
                // A part left by a write that never finished is never kept.
                if (kept + file.Length <= bytes && file.Extension != ".part")
                {
                    kept += file.Length;
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use: it goes next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Formats a size in bytes for Settings: "under 1 MB", "312 MB", "1.4 GB".</summary>
    public static string Describe(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB")
        : bytes < 1024 * 1024
            ? "under 1 MB"
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / (1024.0 * 1024))} MB");
}
