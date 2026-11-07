using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace HandPegBootstrap;

/// <summary>One of Microsoft's zips of a .NET release: its name, where it is, and the SHA-512 Microsoft publishes for it (hex).</summary>
internal sealed record DotNetZip(string Name, string Url, string Sha512);

/// <summary>The .NET runtime and Windows Desktop runtime zips of one .NET release for one processor.</summary>
internal sealed record DotNetRelease(string Version, DotNetZip Runtime, DotNetZip Desktop);

/// <summary>
/// A .NET 8 Desktop Runtime of this copy's own (Pass 5.2.1), for a PC with none: Microsoft's release list for .NET 8 (releases.json, from
/// its download site or the site's older address), the newest release with both zips for this processor, each downloaded over HTTPS
/// (WinHTTP, the PC's proxy), its SHA-512 checked against Microsoft's as it arrives, and both unpacked into portable\dotnet under
/// HandPeg.exe (the whole of a .NET root: dotnet.exe, host\fxr, shared\). Nothing is installed, no admin is asked for, no script runs,
/// no environment variable is set; the folder can be carried anywhere. Unpacked beside, then put in place, so a cut-off download
/// never leaves half a .NET behind.
/// </summary>
internal static class PortableDotNet
{
    private static readonly string[] Sites =
    [
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json",
    ];

    /// <summary>What it is doing, for the window: a line to show and how far along it all is (0 to 1).</summary>
    public delegate void Progress(string line, double done);

    /// <summary>
    /// The newest release in the list with a runtime zip and a Windows Desktop runtime zip for <paramref name="rid"/>; null when none
    /// has them. Shared with the golden tests (portable-bootstrap).
    /// </summary>
    public static DotNetRelease? Pick(JsonDocument list, string rid)
    {
        if (!list.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var release in releases.EnumerateArray())
        {
            if (Zip(release, "runtime", "dotnet-runtime", rid) is { } runtime && Zip(release, "windowsdesktop", "windowsdesktop-runtime", rid) is { } desktop
                && release.TryGetProperty("release-version", out var version) && version.GetString() is { Length: > 0 } name)
                return new DotNetRelease(name, runtime, desktop);
        }

        return null;
    }

    private static DotNetZip? Zip(JsonElement release, string part, string prefix, string rid)
    {
        if (!release.TryGetProperty(part, out var section) || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var file in files.EnumerateArray())
        {
            string? Text(string name) => file.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Text("rid") == rid && Text("name") is { } name && name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal)
                && Text("url") is { } url && url.StartsWith("https://", StringComparison.Ordinal) && Text("hash") is { Length: 128 } hash)
                return new DotNetZip(name, url, hash);
        }

        return null;
    }

    /// <summary>Downloads and unpacks it into <paramref name="app"/>'s portable\dotnet; the release put there. Throws with a message for the user on any failure.</summary>
    public static DotNetRelease Install(string app, string arch, Progress progress, CancellationToken cancel)
    {
        var rid = $"win-{arch}";
        progress("Asking Microsoft for the newest .NET 8...", 0);
        DotNetRelease? release = null;
        Exception? last = null;
        foreach (var site in Sites)
        {
            try
            {
                using var list = JsonDocument.Parse(Web.Get(site, cancel));
                release = Pick(list, rid);
                break;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                last = ex;
            }
        }

        if (release is null)
            throw new IOException(last is not null ? $"Microsoft's .NET download site could not be reached: {last.Message}" : $"Microsoft lists no .NET 8 for {rid}.");

        var portable = Path.Combine(app, "portable");
        var work = Path.Combine(portable, "dotnet-download");
        var target = Path.Combine(app, DotNetRoots.PortableFolder);
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        try
        {
            var parts = new[] { (Zip: release.Runtime, Name: ".NET Runtime"), (Zip: release.Desktop, Name: "Windows Desktop Runtime") };
            for (var i = 0; i < parts.Length; i++)
            {
                var (zip, name) = parts[i];
                var file = Path.Combine(work, zip.Name);
                var shown = i;
                Web.Download(zip.Url, file, zip.Sha512, cancel, (received, total) =>
                    progress($"Downloading {name} {release.Version} ({arch}): {received / 1048576.0:0.0} of {(total > 0 ? $"{total / 1048576.0:0.0}" : "?")} MB",
                        (shown + (total > 0 ? Math.Min((double)received / total, 1) : 0)) * 0.45));
            }

            progress($"Unpacking .NET {release.Version} into this folder...", 0.92);
            var unpacked = Path.Combine(work, "dotnet");
            foreach (var (zip, _) in parts)
                ZipFile.ExtractToDirectory(Path.Combine(work, zip.Name), unpacked, overwriteFiles: true);
            if (DotNetRoots.Usable(unpacked, "the download") is null)
                throw new IOException("The downloaded .NET is not complete: no hostfxr.dll or no .NET 8 frameworks in it.");

            cancel.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(unpacked, target);
            progress($".NET {release.Version} is ready. Starting HandPeg...", 1);
            return release;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next attempt, which clears it first.
            }
        }
    }
}
