using System.IO;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Atomic whole-file writes via a same-folder temp file and a single rename: readers see old or new content, never partial.
/// </summary>
/// <remarks>
/// <para><b>Durable writes</b> call <c>FlushFileBuffers</c> before the rename; otherwise NTFS may commit the rename before the data
/// and a bugcheck or power loss leaves a zero-filled file. Process crashes alone are safe (data is in the OS cache), so per-tab-switch
/// caches skip it; settings, presets and project saves use it. Costs ~1-10 ms on idle NVMe, more under concurrent export writes.</para>
/// <para><b>Previous copies</b> (<c>keepPrevious</c>) retain the replaced file as <c>&lt;path&gt;.prev</c> via
/// <see cref="File.Replace(string, string, string?, bool)"/> for one-step recovery. Not <c>.bak</c>, which <see cref="BackupImporter"/>
/// reserves for its pre-import copy.</para>
/// </remarks>
internal static class AtomicFile
{
    /// <summary>The suffix of the copy a <c>keepPrevious</c> write leaves of the file it replaced.</summary>
    public const string PreviousSuffix = ".prev";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="text"/> as BOM-less UTF-8 (same bytes as <see cref="File.WriteAllText(string, string?)"/>).</summary>
    public static void WriteAllText(string path, string text, bool durable, bool keepPrevious = false)
        => Write(path, stream =>
        {
            var bytes = Utf8NoBom.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }, durable, keepPrevious);

    /// <summary>
    /// Writes <paramref name="write"/>'s output to a temp file beside <paramref name="path"/> and replaces the target. On any failure the
    /// target is untouched, the temp file deleted and the exception rethrown.
    /// </summary>
    public static void Write(string path, Action<Stream> write, bool durable, bool keepPrevious = false)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);

        var partial = PartialPath(path);
        try
        {
            // Same sharing as File.WriteAllText (read-shared). Unbuffered: callers write chunks of 16 KB or more.
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0))
            {
                write(stream);
                if (durable)
                    stream.Flush(flushToDisk: true);
            }

            Commit(partial, path, keepPrevious);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// Unique temp path <c>&lt;path&gt;.&lt;32 hex&gt;.tmp</c> per write so concurrent writers never collide (last rename wins, complete).
    /// The ".tmp" suffix is what project-tab cleanup matches.
    /// </summary>
    public static string PartialPath(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>
    /// Moves (or copies) an unparseable file aside as <c>&lt;name&gt;.damaged-&lt;yyyyMMdd-HHmmss&gt;&lt;ext&gt;</c> so later saves cannot
    /// overwrite it and <c>keepPrevious</c> cannot rotate the damage into .prev.
    /// </summary>
    /// <returns>Set-aside path, or null if neither move nor copy succeeded.</returns>
    public static string? SetAside(string path)
    {
        var aside = Path.Combine(Path.GetDirectoryName(path) ?? "",
                                 $"{Path.GetFileNameWithoutExtension(path)}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
        try
        {
            File.Move(path, aside, overwrite: true);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Copy(path, aside, overwrite: true);
                return aside;
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static void Commit(string partial, string path, bool keepPrevious)
    {
        if (keepPrevious && File.Exists(path))
        {
            try
            {
                File.Replace(partial, path, path + PreviousSuffix, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // ReplaceFile fails on FAT/exFAT, some shares, or when the target is open without FILE_SHARE_DELETE. The temp file keeps
                // its name in every failure (including ERROR_UNABLE_TO_MOVE_REPLACEMENT), so the plain rename below still commits.
            }
        }

        File.Move(partial, path, overwrite: true);
    }

    private static void TryDelete(string partial)
    {
        try
        {
            File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the clean-ups that remove stale ".tmp" files (the project tabs' session folder, the cache clean-up).
        }
    }
}
