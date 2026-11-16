using System.IO;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Whole-file writes: after a write the file holds either its old content or its new content, never a mix and never a
/// truncated copy. The content goes to a temporary file in the same folder, which then replaces the target in one rename.
/// </summary>
/// <remarks>
/// <para><b>Durable writes</b> flush the temporary file to the disk (<c>FlushFileBuffers</c>) before the rename. Without
/// the flush NTFS can commit the rename before the data, and a bugcheck or power loss within the lazy writer's window then
/// leaves a zero-filled file in place of the old one. A process crash alone never needs it (the data is already in the OS
/// cache), so caches rewritten on every tab switch skip it; user settings, presets and project saves use it. The flush
/// costs about 1-10 ms on an idle NVMe drive and can stall longer while an export writes to the same drive.</para>
/// <para><b>Previous copies</b> (<c>keepPrevious</c>) keep the replaced file as <c>&lt;path&gt;.prev</c> through
/// <see cref="File.Replace(string, string, string?, bool)"/>, so a load that finds the file damaged can fall back one save.
/// The suffix is not <c>.bak</c>: <see cref="BackupImporter"/> reserves <c>appsettings.json.bak</c> for the copy it
/// promises the user from before an import.</para>
/// </remarks>
internal static class AtomicFile
{
    /// <summary>The suffix of the copy a <c>keepPrevious</c> write leaves of the file it replaced.</summary>
    public const string PreviousSuffix = ".prev";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="text"/> as UTF-8 without a byte order mark (the bytes <see cref="File.WriteAllText(string, string?)"/> writes).</summary>
    public static void WriteAllText(string path, string text, bool durable, bool keepPrevious = false)
        => Write(path, stream =>
        {
            var bytes = Utf8NoBom.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }, durable, keepPrevious);

    /// <summary>
    /// Writes the content <paramref name="write"/> produces into a temporary file beside <paramref name="path"/> and then moves it
    /// over the target. If anything fails part of the way (the delegate throws, the disk fills), the target is left as it was, the
    /// temporary file is deleted and the exception is rethrown.
    /// </summary>
    public static void Write(string path, Action<Stream> write, bool durable, bool keepPrevious = false)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);

        var partial = PartialPath(path);
        try
        {
            // Opened as File.WriteAllText opens a file (shared for reading). Unbuffered: the project serializer hands over
            // pieces of 16 KB or more, and the string overload writes one block.
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
    /// The temporary file a whole write goes into: <c>&lt;path&gt;.&lt;32 hex digits&gt;.tmp</c>, a new name for every write, so two
    /// writes of the same file at once (a tab's cache written on the thread pool and again during a crash shutdown) never share
    /// one; the last rename wins with complete content. The ".tmp" suffix is what the project-tab clean-up looks for.
    /// </summary>
    public static string PartialPath(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>
    /// Moves a file that could not be parsed out of the way as <c>&lt;name&gt;.damaged-&lt;yyyyMMdd-HHmmss&gt;&lt;ext&gt;</c> beside it (copies
    /// it when it cannot be moved), so no later save can overwrite the only copy and a <c>keepPrevious</c> write cannot rotate the
    /// damage into the previous copy.
    /// </summary>
    /// <returns>The set-aside copy's path, or null when it could neither be moved nor copied.</returns>
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
                // ReplaceFile fails on some file systems (FAT/exFAT, several network shares) and while another process holds the
                // target open without FILE_SHARE_DELETE. The temporary file still has its own name in every failure case
                // (ERROR_UNABLE_TO_MOVE_REPLACEMENT included), so the plain rename below still commits it.
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
