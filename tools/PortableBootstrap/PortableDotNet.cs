using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace HandPegBootstrap;

/// <summary>One of Microsoft's files of a .NET release: its name, where it is, and the SHA-512 Microsoft publishes for it (hex).</summary>
internal sealed record DotNetFile(string Name, string Url, string Sha512);

/// <summary>The .NET runtime and Windows Desktop runtime zips of one .NET release for one processor.</summary>
internal sealed record DotNetRelease(string Version, DotNetFile Runtime, DotNetFile Desktop);

/// <summary>
/// A .NET 10 Desktop Runtime of this copy's own (Pass 5.2.1; for every kind of copy, Pass 5.3-Stage1), for a PC with none: Microsoft's
/// release list for .NET 10 (releases.json, from its download site or the site's older address), the newest released (not preview) version
/// with both zips for this processor, each downloaded over HTTPS (WinHTTP, the PC's proxy), its SHA-512 checked against Microsoft's as it
/// arrives, and both unpacked into the copy's own root (<see cref="DotNetRoots.OwnRoot"/>: the whole of a .NET root, dotnet.exe, host\fxr,
/// shared\). Nothing is installed, no admin is asked for, no script runs, no environment variable is set. Unpacked in a folder of the
/// attempt's own beside the root, then put in place, so a cut-off download never leaves half a .NET behind; a .NET already there is moved
/// aside, never deleted where it is. One HandPeg at a time downloads into a root (a named lock per root), and the next takes its download.
/// </summary>
internal static class PortableDotNet
{
    /// <summary>Microsoft's list of the .NET 10 releases: its download site, then the site's older address.</summary>
    public static readonly string[] Sites =
    [
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json",
    ];

    /// <summary>What it is doing, for the window: a line to show and how far along it all is (0 to 1).</summary>
    public delegate void Progress(string line, double done);

    /// <summary>
    /// The newest released version in the list (a preview or release candidate is passed over) with a runtime zip and a Windows Desktop
    /// runtime zip for <paramref name="rid"/>; null when none has them. Shared with the golden tests (portable-bootstrap).
    /// </summary>
    public static DotNetRelease? Pick(JsonDocument list, string rid)
    {
        foreach (var (release, name) in Released(list))
        {
            if (FileOf(release, "runtime", rid, n => n.StartsWith("dotnet-runtime", StringComparison.Ordinal) && n.EndsWith(".zip", StringComparison.Ordinal)) is { } runtime
                && FileOf(release, "windowsdesktop", rid, n => n.StartsWith("windowsdesktop-runtime", StringComparison.Ordinal) && n.EndsWith(".zip", StringComparison.Ordinal)) is { } desktop)
                return new DotNetRelease(name, runtime, desktop);
        }

        return null;
    }

    /// <summary>
    /// The list's releases, newest first as Microsoft lists them, with their version, leaving out every preview and release candidate
    /// ("10.0.0-rc.2": a version with a dash) and any entry whose version is not a plain version number.
    /// </summary>
    public static IEnumerable<(JsonElement Release, string Version)> Released(JsonDocument list)
    {
        if (list.RootElement.ValueKind != JsonValueKind.Object || !list.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind == JsonValueKind.Object && release.TryGetProperty("release-version", out var version) && version.ValueKind == JsonValueKind.String
                && version.GetString() is { Length: > 0 } name && !name.Contains('-') && Version.TryParse(name, out _))
                yield return (release, name);
        }
    }

    /// <summary>
    /// The file of <paramref name="release"/>'s <paramref name="part"/> ("runtime", "windowsdesktop") for <paramref name="rid"/> whose name
    /// <paramref name="named"/> takes, from an https:// address with a SHA-512 to check (128 hex digits) and a plain file name; null for none.
    /// </summary>
    public static DotNetFile? FileOf(JsonElement release, string part, string rid, Func<string, bool> named)
    {
        if (!release.TryGetProperty(part, out var section) || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
                continue;

            string? Text(string name) => file.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Text("rid") == rid && Text("name") is { Length: > 0 } name && named(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && Text("url") is { } url && url.StartsWith("https://", StringComparison.Ordinal) && Text("hash") is { } hash && IsSha512(hash))
                return new DotNetFile(name, url, hash);
        }

        return null;
    }

    /// <summary>Whether <paramref name="hash"/> reads as a SHA-512: 128 hex digits.</summary>
    public static bool IsSha512(string hash) => hash.Length == 128 && hash.All(char.IsAsciiHexDigit);

    /// <summary>
    /// What <paramref name="pick"/> takes from Microsoft's release list, asked of each site in turn (one that cannot be reached or answers
    /// something unreadable is passed over). Throws with a message for the user when no site answers, or with <paramref name="none"/>
    /// when the list has nothing to take.
    /// </summary>
    public static T FromReleaseList<T>(Func<JsonDocument, T?> pick, string none, CancellationToken cancel) where T : class
    {
        Exception? last = null;
        foreach (var site in Sites)
        {
            T? found;
            try
            {
                using var list = JsonDocument.Parse(Web.Get(site, cancel));
                found = pick(list);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                last = ex;
                continue;
            }

            return found ?? throw new IOException(none);
        }

        throw new IOException($"Microsoft's .NET download site could not be reached: {last?.Message}");
    }

    /// <summary>
    /// The root a download for <paramref name="own"/> goes in, in full: what Program gives <see cref="Install"/>. A portable zip's is
    /// portable\dotnet beside HandPeg.exe and a portable.txt or --portable copy's dotnet beside it; an installed or any other copy's is
    /// never in the program's "portable" folder, which would turn it into a portable copy.
    /// </summary>
    public static string DownloadTarget(OwnDotNet own) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(own.Folder));

    /// <summary>
    /// Where one attempt (<paramref name="attempt"/>) at a download into <paramref name="target"/> works: the root itself; the folder it is
    /// downloaded and unpacked in first, "&lt;root&gt;-download-&lt;attempt&gt;" (each attempt its own, so two never share one); and where a
    /// .NET already there is moved aside, "&lt;root&gt;-old-&lt;attempt&gt;". Both are beside the root on its drive, so putting it in place
    /// is a rename, and never anywhere but the root's own parent: an installed copy's own .NET never makes a "portable" folder in "current".
    /// </summary>
    public static (string Target, string Work, string Aside) DownloadPlan(string target, Guid attempt)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var id = attempt.ToString("N");
        return (root, $"{root}-download-{id}", $"{root}-old-{id}");
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is something a download into <paramref name="root"/> left behind: beside it, named for it with
    /// "-download-" or "-old-" and an attempt's 32 hex digits (<see cref="DownloadPlan"/>), or 5.2.1's "&lt;root&gt;-download". Never the
    /// root itself, another root's, or anything else in that folder (an installed copy's dotnet sits beside "current" and Velopack's files).
    /// </summary>
    public static bool IsLeftOver(string root, string folder)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!string.Equals(Path.GetDirectoryName(folder), Path.GetDirectoryName(root), StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileName(folder);
        var own = Path.GetFileName(root);
        if (name.Equals(own + "-download", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var mark in new[] { "-download-", "-old-" })
        {
            var prefix = own + mark;
            if (name.Length == prefix.Length + 32 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name[prefix.Length..].All(char.IsAsciiHexDigit))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The name of the lock a HandPeg holds while it downloads into <paramref name="root"/>: one per root, the same whatever the path's
    /// letter case or a trailing separator (so two copies sharing %LOCALAPPDATA%\HandPeg\dotnet share it), for this Windows session, and a
    /// name Windows takes (no backslash past "Local\"): the 64-bit FNV-1a of the upper-cased full path, in hex.
    /// </summary>
    public static string MutexName(string root)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())
            hash = unchecked((hash ^ c) * 1099511628211UL);
        return "Local\\HandPeg.DotNet." + hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Downloads the newest .NET 10 runtime and Windows Desktop runtime for <paramref name="arch"/> and puts them in place as
    /// <paramref name="target"/> (this copy's own root, <see cref="DownloadTarget"/>); the release put there, or null when another HandPeg
    /// had put a usable .NET there by the time this one could begin. One HandPeg at a time downloads into a root, holding its lock
    /// (<see cref="MutexName"/>) throughout: a second waits, said in the window and stopped by <paramref name="cancel"/>, then takes the
    /// first one's download. A usable .NET found there once the lock is held is taken as it is, unless it is the very one hostfxr refused in
    /// this start (<paramref name="refused"/>: its <see cref="DotNetRoots.StampOf"/>, which looks whole but cannot start). A .NET already
    /// there (a cut-off download, the other processor's, one hostfxr refused) is moved aside and deleted after, never deleted where it is
    /// (<see cref="PutInPlace"/>). Throws with a message for the user on any failure, and OperationCanceledException when
    /// <paramref name="cancel"/> stops it.
    /// </summary>
    public static DotNetRelease? Install(string target, string arch, Progress progress, CancellationToken cancel, string? refused = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        using var gate = new Mutex(false, MutexName(root));
        Take(gate, progress, cancel);
        try
        {
            if (DotNetRoots.Usable(root, "the download") is { } there && DotNetRoots.StampOf(there) != refused)
            {
                progress($".NET {DotNetRoots.Major} is ready. Starting HandPeg...", 1);
                return null;
            }

            ClearLeftOvers(root);
            var rid = $"win-{arch}";
            progress($"Asking Microsoft for the newest .NET {DotNetRoots.Major}...", 0);
            var release = FromReleaseList<DotNetRelease>(list => Pick(list, rid), $"Microsoft lists no .NET {DotNetRoots.Major} for {rid}.", cancel);

            var (_, work, aside) = DownloadPlan(root, Guid.NewGuid());
            var placed = false;
            Directory.CreateDirectory(work);
            try
            {
                var parts = new[] { (Zip: release.Runtime, Name: ".NET Runtime"), (Zip: release.Desktop, Name: "Windows Desktop Runtime") };
                for (var i = 0; i < parts.Length; i++)
                {
                    var (zip, name) = parts[i];
                    var shown = i;
                    Web.Download(zip.Url, Path.Combine(work, zip.Name), zip.Sha512, cancel, (received, total) =>
                        progress($"Downloading {name} {release.Version} ({arch}): {received / 1048576.0:0.0} of {(total > 0 ? $"{total / 1048576.0:0.0}" : "?")} MB",
                            (shown + (total > 0 ? Math.Min((double)received / total, 1) : 0)) * 0.45));
                }

                progress($"Unpacking .NET {release.Version} for HandPeg...", 0.92);
                var unpacked = Path.Combine(work, "dotnet");
                foreach (var (zip, _) in parts)
                    ZipFile.ExtractToDirectory(Path.Combine(work, zip.Name), unpacked, overwriteFiles: true);
                if (DotNetRoots.Usable(unpacked, "the download") is null)
                    throw new IOException($"The downloaded .NET is not complete: no hostfxr.dll for this processor or no .NET {DotNetRoots.Major} frameworks in it.");

                cancel.ThrowIfCancellationRequested();
                try
                {
                    PutInPlace(unpacked, root, aside);
                }
                catch (IOException) when (refused is not null && Directory.Exists(root) && !Directory.Exists(aside))
                {
                    // The .NET hostfxr refused in this start: .NET's host keeps its hostpolicy.dll loaded in this process for good, so its
                    // folder cannot be moved aside here. The download, whole and checked, waits beside it for a new start of HandPeg.exe,
                    // which puts it in place before it loads anything (FinishPending).
                    Directory.Move(unpacked, PendingFolder(root, Guid.NewGuid()));
                    throw new PendingSwapException(root);
                }

                placed = true;
                progress($".NET {release.Version} is ready. Starting HandPeg...", 1);
                return release;
            }
            finally
            {
                // This attempt's work folder, and once the new .NET is in place the one it moved aside: best effort, as something may still
                // hold a file (a later install clears what is left, under the lock).
                foreach (var folder in placed ? new[] { work, aside } : new[] { work })
                {
                    try
                    {
                        if (Directory.Exists(folder))
                            Directory.Delete(folder, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Left for a later install.
                    }
                }
            }
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    /// <summary>A download waiting to be put in place as <paramref name="root"/> by a new start (&lt;root&gt;-pending-&lt;attempt&gt;).</summary>
    public static string PendingFolder(string root, Guid attempt) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + "-pending-" + attempt.ToString("N");

    /// <summary>Whether <paramref name="folder"/> is a download waiting beside <paramref name="root"/> (<see cref="PendingFolder"/>).</summary>
    public static bool IsPending(string root, string folder)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var prefix = Path.GetFileName(root) + "-pending-";
        var name = Path.GetFileName(folder);
        return string.Equals(Path.GetDirectoryName(folder), Path.GetDirectoryName(root), StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length == prefix.Length + 32
            && name[prefix.Length..].All(Uri.IsHexDigit);
    }

    /// <summary>
    /// At every start, before any .NET is looked at (5.3): a download that waits beside <paramref name="target"/> (a start whose own .NET
    /// hostfxr refused could not move it aside, Install) is put in place, under the root's lock, when no other HandPeg holds the lock and
    /// nothing runs on the old one; other waiting downloads, and what earlier attempts left (<see cref="IsLeftOver"/>), are deleted. Nothing
    /// to do costs one look at the root's folder. Never throws.
    /// </summary>
    public static void FinishPending(string target)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            var parent = Path.GetDirectoryName(root);
            if (parent is null || !Directory.Exists(parent))
                return;
            var folders = Directory.EnumerateDirectories(parent).ToList();
            var pending = folders.Where(folder => IsPending(root, folder)).ToList();
            var leftOvers = folders.Where(folder => IsLeftOver(root, folder)).ToList();
            if (pending.Count == 0 && leftOvers.Count == 0)
                return;

            using var gate = new Mutex(false, MutexName(root));
            try
            {
                if (!gate.WaitOne(0))
                    return;
            }
            catch (AbandonedMutexException)
            {
                // Taken: the mutex is this thread's now.
            }

            try
            {
                var ready = pending.Where(folder => DotNetRoots.Usable(folder, "the download") is not null)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
                if (ready is not null)
                {
                    var aside = DownloadPlan(root, Guid.NewGuid()).Aside;
                    PutInPlace(ready, root, aside);
                    pending.Remove(ready);
                    pending.Add(aside);
                }

                // And what earlier starts left (a .NET moved aside that the start which replaced it still had loaded, a work folder).
                foreach (var folder in pending.Concat(leftOvers))
                {
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Left for a later start.
                    }
                }
            }
            finally
            {
                gate.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old .NET is in use (another HandPeg runs on it) or a folder cannot be read: the download waits for a later start.
        }
    }

    /// <summary>
    /// <paramref name="unpacked"/> put in place as <paramref name="root"/>. A .NET already there is renamed to <paramref name="aside"/>
    /// first, never deleted where it is, and only when no program runs on it (<see cref="InUse"/>: an installed or any other copy's root is
    /// shared with the other processor's HandPeg); it is put back when the new one cannot be moved in. Throws with a message for the user
    /// when the old one is in use or cannot be moved aside, leaving it as it was.
    /// </summary>
    public static void PutInPlace(string unpacked, string root, string aside)
    {
        var movedAside = false;
        if (Directory.Exists(root))
        {
            if (InUse(root))
                throw new IOException($"The .NET in {root} is in use (another HandPeg runs on it, perhaps the other processor's), so it was left as it is. Close every HandPeg, then choose again.");

            try
            {
                Directory.Move(root, aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The .NET in {root} could not be moved aside to make way for the new one ({ex.Message}). Close every HandPeg, then choose again.", ex);
            }

            movedAside = true;
        }

        try
        {
            Directory.Move(unpacked, root);
        }
        catch (Exception ex) when (movedAside && ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                Directory.Move(aside, root);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                // Left aside: a later install clears it.
            }

            throw;
        }
    }

    /// <summary>
    /// Whether a program runs on the .NET in <paramref name="root"/>: one of its coreclr.dll files cannot be opened for writing (Windows lets
    /// nothing write to a loaded program file, and every running .NET has its coreclr.dll loaded; a start hostfxr refused never loads it).
    /// </summary>
    private static bool InUse(string root)
    {
        var frameworks = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(frameworks))
            return false;

        foreach (var coreclr in Directory.EnumerateDirectories(frameworks).Select(dir => Path.Combine(dir, "coreclr.dll")).Where(File.Exists))
        {
            try
            {
                new FileStream(coreclr, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1).Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes <paramref name="gate"/> for this thread, waiting while another HandPeg holds it (said in the window; <paramref name="cancel"/>
    /// stops the wait, and then it is not taken). One whose holder ended without letting go (a HandPeg closed mid-download) is taken at
    /// once: what that download left beside the root is cleared, and its root, if any, is checked like any other.
    /// </summary>
    private static void Take(Mutex gate, Progress progress, CancellationToken cancel)
    {
        try
        {
            if (gate.WaitOne(0))
                return;

            progress($"Waiting for another HandPeg's download of .NET {DotNetRoots.Major} to finish...", 0);
            if (WaitHandle.WaitAny(new WaitHandle[] { gate, cancel.WaitHandle }) != 0)
                throw new OperationCanceledException(cancel);
        }
        catch (AbandonedMutexException)
        {
            // Taken: the mutex is this thread's now.
        }
    }

    /// <summary>
    /// What earlier attempts left beside <paramref name="root"/> (<see cref="IsLeftOver"/>: their work folders, the .NETs they moved aside,
    /// 5.2.1's dotnet-download), deleted. Only while the lock is held, so never another HandPeg's download under way; one still in use is
    /// left for a later install.
    /// </summary>
    private static void ClearLeftOvers(string root)
    {
        try
        {
            if (Path.GetDirectoryName(root) is not { Length: > 0 } parent || !Directory.Exists(parent))
                return;

            foreach (var folder in Directory.GetDirectories(parent))
            {
                if (!IsLeftOver(root, folder))
                    continue;

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or locked: left for a later install.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder could not be listed: nothing is cleared this time.
        }
    }
}
