using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <summary>Where a tool stands: as the dependency list shows it.</summary>
public enum DependencyState
{
    Missing,
    Installed,
    UpdateAvailable,

    /// <summary>The settings name a program of the user's own. It is used as it is and never replaced.</summary>
    CustomPath,
}

/// <summary>How far an install has come: a fraction from 0 to 1 (negative while it cannot be told) and a line of text.</summary>
public readonly record struct InstallProgress(double Fraction, string Text);

/// <summary>A release of a tool as GitHub describes it.</summary>
/// <param name="Stamp">What identifies this build: compared with the stamp kept beside the installed one.</param>
/// <param name="Sha256">The digest GitHub publishes for the file, when it does.</param>
/// <param name="SumsUrl">A checksum list attached to the same release, used when there is no digest.</param>
public sealed record RemoteAsset(string Name, string DownloadUrl, string Stamp, string Tag, long Size, string? Sha256, string? SumsUrl);

/// <summary>
/// Installs and updates FFmpeg (with ffprobe), yt-dlp and whisper.cpp, and the speech models, in the deps
/// folder (see <see cref="AppPaths"/>): a folder per tool. Nothing is shipped with HandPeg itself.
///
/// An install never works on the copy in use. The download goes into a staging folder, is checked against
/// the published SHA-256 where there is one, unpacked and run once; only then is the tool's folder swapped
/// for the new one. A download that fails, is cancelled or does not check out leaves what was there untouched.
/// </summary>
public static partial class DependencyUpdater
{
    public const string Ffmpeg = "ffmpeg";
    public const string YtDlp = "yt-dlp";
    public const string Whisper = "whisper";

    /// <summary>libmpv: the video player.</summary>
    public const string Mpv = "mpv";

    /// <summary>The tools, in the order they are listed and installed.</summary>
    public static IReadOnlyList<string> Tools { get; } = [Ffmpeg, Mpv, YtDlp, Whisper];

    // The library alone, from the builds the mpv project points Windows users to. Its name carries the date
    // and the commit, so it is recognised by its shape; the "v3" builds need a newer processor and are passed over.
    private const string MpvReleaseUrl = "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest";
    private const string MpvLibraryName = "libmpv-2.dll";

    [GeneratedRegex(@"^mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z$")]
    private static partial Regex MpvAssetRegex();

    private const string YtDlpAssetName = "yt-dlp.exe";
    private const string FfmpegAssetName = "ffmpeg-master-latest-win64-gpl.zip";
    private const string WhisperAssetName = "whisper-bin-x64.zip";

    // Checksum lists some projects attach to a release: yt-dlp's, and the FFmpeg builds'.
    private static readonly string[] SumsAssetNames = ["SHA2-256SUMS", "checksums.sha256"];

    /// <summary>The speech models offered for download: English-only ones first (smaller and better for English), then multilingual.</summary>
    public static IReadOnlyList<string> WhisperModels { get; } =
    [
        "ggml-tiny.en.bin", "ggml-base.en.bin", "ggml-small.en.bin", "ggml-medium.en.bin",
        "ggml-tiny.bin", "ggml-base.bin", "ggml-small.bin", "ggml-medium.bin",
    ];

    /// <summary>
    /// The voice activity detection model whisper.cpp uses to skip silence (Silero VAD, under 1 MB). Kept with
    /// the speech models; without it auto-captions still work, but may put words into quiet stretches.
    /// </summary>
    public const string VadModel = "ggml-silero-v5.1.2.bin";

    private const string VadModelUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/" + VadModel;

    private static readonly HttpClient Http = CreateClient(followRedirects: true);

    // For asking where a download redirects to, and what the answer says about the file, without fetching it.
    private static readonly HttpClient HttpNoRedirect = CreateClient(followRedirects: false);

    // ----- Where things are -----

    /// <summary>The folder this class keeps a tool in.</summary>
    public static string ToolFolder(string tool) => Path.Combine(AppPaths.Deps, tool);

    private static string ModelsFolder => Path.Combine(AppPaths.Deps, "models");
    private static string StagingRoot => Path.Combine(AppPaths.Deps, ".staging");

    // Where this class installs the tools. Downloads only ever go here, never over a path the user configured.
    private static string LocalYtDlpPath => Path.Combine(ToolFolder(YtDlp), "yt-dlp.exe");
    private static string LocalFfmpegPath => Path.Combine(ToolFolder(Ffmpeg), "ffmpeg.exe");
    private static string LocalFfprobePath => Path.Combine(ToolFolder(Ffmpeg), "ffprobe.exe");

    /// <summary>whisper.cpp's command line program as this class installs it: under this name, whatever the release calls it.</summary>
    private static string LocalWhisperPath => Path.Combine(ToolFolder(Whisper), "whisper.exe");

    // The tools the application actually runs: a path from the settings when one is set, otherwise the local copy.
    public static string YtDlpPath => OrDefault(AppSettings.Current.YtDlpPath, LocalYtDlpPath);
    public static string FfmpegPath => OrDefault(AppSettings.Current.FfmpegPath, LocalFfmpegPath);

    public static string FfprobePath
    {
        get
        {
            var settings = AppSettings.Current;
            if (!string.IsNullOrWhiteSpace(settings.FfprobePath))
                return settings.FfprobePath;

            // A custom FFmpeg normally ships with its own ffprobe; prefer that one so the two match.
            if (!string.IsNullOrWhiteSpace(settings.FfmpegPath)
                && Path.GetDirectoryName(settings.FfmpegPath) is { Length: > 0 } folder
                && Path.Combine(folder, "ffprobe.exe") is var sibling && File.Exists(sibling))
            {
                return sibling;
            }

            return LocalFfprobePath;
        }
    }

    /// <summary>The whisper program auto-captions run: the one named in the settings when there is one, otherwise the local copy.</summary>
    public static string WhisperPath => OrDefault(AppSettings.Current.WhisperPath, LocalWhisperPath);

    /// <summary>True when the settings name a specific whisper.exe instead of the local copy.</summary>
    public static bool IsWhisperOverridden => !string.IsNullOrWhiteSpace(AppSettings.Current.WhisperPath);

    /// <summary>True when the settings name a specific ffmpeg.exe instead of the local copy.</summary>
    public static bool IsFfmpegOverridden => !string.IsNullOrWhiteSpace(AppSettings.Current.FfmpegPath);

    /// <summary>The model chosen in the settings, in the models folder.</summary>
    public static string WhisperModelPath => GetWhisperModelPath(AppSettings.Current.WhisperModel);

    public static string GetWhisperModelPath(string model) => Path.Combine(ModelsFolder, Path.GetFileName(model));

    public static string VadModelPath => GetWhisperModelPath(VadModel);

    /// <summary>The libmpv library the player loads.</summary>
    public static string MpvPath => Path.Combine(ToolFolder(Mpv), MpvLibraryName);

    private static string OrDefault(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// Which folder a file of the old, single "bin" folder belongs in now, or null when it is not one of ours.
    /// </summary>
    public static string? FolderForLegacyFile(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        return name switch
        {
            "ffmpeg.exe" or "ffprobe.exe" or "ffmpeg.version" => ToolFolder(Ffmpeg),
            "yt-dlp.exe" or "yt-dlp.version" => ToolFolder(YtDlp),
            "whisper.exe" or "whisper.dll" or "whisper.version" => ToolFolder(Whisper),
            _ when name.StartsWith("ggml", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal) => ToolFolder(Whisper),
            _ when name.StartsWith("ggml", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal) => ModelsFolder,
            _ => null,
        };
    }

    // ----- What is installed -----

    public static string DisplayName(string tool) => tool switch
    {
        Ffmpeg => "FFmpeg and ffprobe",
        YtDlp => "yt-dlp",
        Mpv => "Video player (libmpv)",
        _ => "whisper.cpp",
    };

    /// <summary>The program of the user's own that stands in for a tool, or an empty string.</summary>
    public static string CustomPath(string tool) => (tool switch
    {
        Ffmpeg => AppSettings.Current.FfmpegPath,
        YtDlp => AppSettings.Current.YtDlpPath,
        Mpv => "",
        _ => AppSettings.Current.WhisperPath,
    }).Trim();

    private static string[] LocalPrograms(string tool) => tool switch
    {
        Ffmpeg => [LocalFfmpegPath, LocalFfprobePath],
        YtDlp => [LocalYtDlpPath],
        Mpv => [MpvPath],
        _ => [LocalWhisperPath],
    };

    /// <summary>What is on disk, without asking anyone: a custom path, installed, or missing.</summary>
    public static DependencyState GetLocalState(string tool) =>
        CustomPath(tool).Length > 0 ? DependencyState.CustomPath
        : LocalPrograms(tool).All(File.Exists) ? DependencyState.Installed
        : DependencyState.Missing;

    /// <summary>True when the installed copy is not the build <paramref name="latest"/> describes.</summary>
    public static bool IsOutdated(string tool, RemoteAsset latest) => ReadText(StampPath(ToolFolder(tool), tool)) != latest.Stamp;

    /// <summary>File names of the tools that are not installed yet.</summary>
    public static IReadOnlyList<string> GetMissing() =>
        new[] { YtDlpPath, FfmpegPath, FfprobePath }.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList()!;

    private static string StampPath(string folder, string tool) => Path.Combine(folder, tool + ".version");
    private static string VersionPath(string folder) => Path.Combine(folder, "version.txt");

    /// <summary>
    /// The version of the installed copy, for display. It is read from a small file written when the tool was
    /// installed; only a copy that has none (one brought over from an older build) is asked, by running it once.
    /// </summary>
    public static async Task<string> GetInstalledVersionAsync(string tool, CancellationToken cancellationToken)
    {
        var folder = ToolFolder(tool);
        if (ReadText(VersionPath(folder)) is { Length: > 0 } known)
            return known;

        try
        {
            var version = await ProbeVersionAsync(tool, folder, cancellationToken);
            if (version.Length > 0)
                File.WriteAllText(VersionPath(folder), version);
            return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Runs the copy in <paramref name="folder"/> briefly and returns what it says its version is. Also the
    /// proof that a freshly downloaded tool starts at all. whisper.cpp does not report a version, so its
    /// release tag stands in.
    /// </summary>
    private static async Task<string> ProbeVersionAsync(string tool, string folder, CancellationToken cancellationToken)
    {
        var (program, argument) = tool switch
        {
            Ffmpeg => ("ffmpeg.exe", "-version"),
            YtDlp => ("yt-dlp.exe", "--version"),
            _ => ("whisper.exe", "--help"),
        };

        // Looked at before it is run: Windows answers an attempt to start a file that is not a program for
        // this machine with a dialog of its own, which nobody asked for.
        if (!IsProgramForThisMachine(Path.Combine(folder, program)))
            throw new InvalidOperationException($"{program} is not a 64-bit Windows program.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(Path.Combine(folder, program))
                .WithArguments(argument)
                .WithWorkingDirectory(folder)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{program} did not answer.");
        }

        // A program that could not load (a missing library, the wrong architecture) ends with a Windows error code.
        if (result.ExitCode is < 0 or > 255)
            throw new InvalidOperationException($"{program} could not start (code 0x{result.ExitCode:X8}).");

        return tool switch
        {
            Ffmpeg => Regex.Match(result.StandardOutput, @"ffmpeg version (\S+)") is { Success: true } match
                ? match.Groups[1].Value
                : throw new InvalidOperationException("ffmpeg.exe did not report a version."),
            YtDlp => result.StandardOutput.Trim() is { Length: > 0 } version
                ? version
                : throw new InvalidOperationException("yt-dlp.exe did not report a version."),
            _ => (ReadText(StampPath(folder, tool)) ?? "").Split('|')[0],
        };
    }

    /// <summary>True when the file is a Windows executable built for 64-bit Intel/AMD processors, going by its header.</summary>
    private static bool IsProgramForThisMachine(string path)
    {
        const ushort Amd64 = 0x8664;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.BaseStream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
                return false;

            // The old DOS header says where the real one starts.
            reader.BaseStream.Position = 0x3C;
            var header = reader.ReadUInt32();
            if (header + 6 > reader.BaseStream.Length)
                return false;

            reader.BaseStream.Position = header;
            return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == Amd64;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ----- What is available -----

    // GitHub allows a machine sixty of these questions an hour, so an answer is kept for a few minutes:
    // the check at startup and the list in the settings then share one.
    private static readonly Dictionary<string, (DateTime At, RemoteAsset Asset)> Answers = [];
    private static readonly TimeSpan AnswerLifetime = TimeSpan.FromMinutes(10);

    private static string ReleaseUrl(string tool) => tool switch
    {
        Ffmpeg => OrDefault(AppSettings.Current.FfmpegReleaseUrl, AppSettings.DefaultFfmpegReleaseUrl),
        YtDlp => OrDefault(AppSettings.Current.YtDlpReleaseUrl, AppSettings.DefaultYtDlpReleaseUrl),
        Mpv => MpvReleaseUrl,
        _ => OrDefault(AppSettings.Current.WhisperReleaseUrl, AppSettings.DefaultWhisperReleaseUrl),
    };

    /// <summary>Whether a file attached to a release is the one this tool is installed from.</summary>
    private static bool IsAssetFor(string tool, string? name) => name is not null && tool switch
    {
        Ffmpeg => name == FfmpegAssetName,
        YtDlp => name == YtDlpAssetName,
        Mpv => MpvAssetRegex().IsMatch(name),
        _ => name == WhisperAssetName,
    };

    /// <summary>Asks GitHub for the newest build of a tool. Throws when it cannot be reached or has none.</summary>
    public static async Task<RemoteAsset> GetLatestAsync(string tool, CancellationToken cancellationToken)
    {
        var releaseUrl = ReleaseUrl(tool);
        lock (Answers)
        {
            if (Answers.TryGetValue(releaseUrl, out var kept) && DateTime.UtcNow - kept.At < AnswerLifetime)
                return kept.Asset;
        }

        using var response = await Http.GetAsync(releaseUrl, cancellationToken);
        if ((int)response.StatusCode is 403 or 429)
            throw new InvalidOperationException("GitHub's hourly limit for update checks has been reached; try again in a while");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {releaseUrl}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // One release, or a list of them newest first: the first one that has the asset is the one to use.
        var root = json.RootElement;
        var releases = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        foreach (var release in releases)
        {
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var assets = release.GetProperty("assets").EnumerateArray().ToList();
            foreach (var asset in assets)
            {
                var assetName = asset.GetProperty("name").GetString();
                if (!IsAssetFor(tool, assetName))
                    continue;

                // Rolling releases keep the same tag, so the asset timestamp is part of the version stamp.
                var url = asset.GetProperty("browser_download_url").GetString()!;
                var updatedAt = asset.GetProperty("updated_at").GetString();
                var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;

                var digest = asset.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind == JsonValueKind.String
                    ? digestValue.GetString()
                    : null;
                var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;

                var sums = assets.FirstOrDefault(a => SumsAssetNames.Contains(a.GetProperty("name").GetString()));
                var sumsUrl = sums.ValueKind == JsonValueKind.Object ? sums.GetProperty("browser_download_url").GetString() : null;

                var found = new RemoteAsset(assetName!, url, $"{tag}|{updatedAt}", tag, size, sha256, sumsUrl);
                lock (Answers)
                    Answers[releaseUrl] = (DateTime.UtcNow, found);
                return found;
            }
        }

        throw new InvalidOperationException($"No release at {releaseUrl} has the download for {DisplayName(tool)}.");
    }

    /// <summary>
    /// Quietly checks whether Install/Update would have anything to do for the tools everything depends on:
    /// yt-dlp or FFmpeg is missing, or its release is newer than the installed one. Tools replaced by a path
    /// from the settings are not checked. A failed check (offline, rate limited) reports false rather than
    /// raising an alarm.
    /// </summary>
    public static async Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var tool in new[] { YtDlp, Ffmpeg, Mpv })
            {
                switch (GetLocalState(tool))
                {
                    case DependencyState.CustomPath:
                        continue;
                    case DependencyState.Missing:
                        return true;
                }

                if (IsOutdated(tool, await GetLatestAsync(tool, cancellationToken)))
                    return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException or IOException)
        {
            AppLog.Write("Dependency update check failed", ex);
        }

        return false;
    }

    // ----- Installing a tool -----

    /// <summary>
    /// Downloads <paramref name="asset"/>, checks it, and puts it in place of the installed copy of the tool.
    /// Throws, having changed nothing, when any step fails or the operation is cancelled.
    /// </summary>
    public static async Task InstallToolAsync(string tool, RemoteAsset asset, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (GetLocalState(tool) == DependencyState.CustomPath)
            return;

        var name = DisplayName(tool);
        var staging = Path.Combine(StagingRoot, $"{tool}_{Guid.NewGuid():N}");
        var (download, fresh, parked) = (Path.Combine(staging, "download"), Path.Combine(staging, "new"), Path.Combine(staging, "old"));
        try
        {
            Directory.CreateDirectory(fresh);

            var hash = await DownloadAsync(asset.DownloadUrl, download, name, progress, cancellationToken);
            var expected = asset.Sha256 ?? await FindInSumsAsync(asset, cancellationToken);
            Verify(name, hash, expected);

            progress.Report(new InstallProgress(1, $"Unpacking {name}..."));
            await Task.Run(() => Unpack(tool, download, fresh, cancellationToken), cancellationToken);

            // The stamp travels with the files, so the two can never disagree about what is installed.
            File.WriteAllText(StampPath(fresh, tool), asset.Stamp);

            // A library is not something to run: it is looked at instead, and its release date is its version.
            progress.Report(new InstallProgress(1, $"Testing {name}..."));
            if (tool == Mpv && !IsProgramForThisMachine(Path.Combine(fresh, MpvLibraryName)))
                throw new InvalidOperationException($"{MpvLibraryName} is not a 64-bit Windows library.");
            File.WriteAllText(VersionPath(fresh), tool == Mpv ? asset.Tag : await ProbeVersionAsync(tool, fresh, cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            if (tool == Mpv)
                SwapLibrary(fresh, ToolFolder(tool));
            else
                Swap(fresh, ToolFolder(tool), parked, name);
            AppLog.Write($"Installed {name} {asset.Tag} ({(expected is null ? "no checksum published" : "SHA-256 verified")}).");
        }
        finally
        {
            DeleteFolder(staging);
        }
    }

    /// <summary>
    /// Puts the new folder where the old one is. The old one is set aside first and put back if the new one
    /// cannot take its place; when the old one is in use it cannot be set aside, and nothing changes at all.
    /// </summary>
    private static void Swap(string fresh, string live, string parked, string name)
    {
        var hadOld = Directory.Exists(live);
        try
        {
            if (hadOld)
                Directory.Move(live, parked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{name} is in use and was left as it is. Try again when nothing is running.", ex);
        }

        try
        {
            Directory.Move(fresh, live);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (hadOld)
                Directory.Move(parked, live);
            throw;
        }
    }

    /// <summary>
    /// The player's library is in use for as long as HandPeg runs, so its folder cannot be set aside like a
    /// tool's. Windows does let a library in use be renamed: the old one steps aside under another name,
    /// the new one takes its place, and is the one loaded the next time HandPeg starts. What stepped aside
    /// before is cleared away first, now that nothing uses it any more.
    /// </summary>
    private static void SwapLibrary(string fresh, string live)
    {
        Directory.CreateDirectory(live);
        foreach (var stale in Directory.EnumerateFiles(live, "*.old"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still the one in use, from an update earlier in this same session.
            }
        }

        var library = Path.Combine(live, MpvLibraryName);
        if (File.Exists(library))
            File.Move(library, Path.Combine(live, $"{MpvLibraryName}.{Guid.NewGuid():N}.old"));
        foreach (var file in Directory.EnumerateFiles(fresh))
            File.Move(file, Path.Combine(live, Path.GetFileName(file)), overwrite: true);
    }

    private static void Unpack(string tool, string download, string folder, CancellationToken cancellationToken)
    {
        if (tool == YtDlp)
        {
            // Not an archive: the download is the program.
            File.Move(download, Path.Combine(folder, "yt-dlp.exe"));
            return;
        }

        if (tool == Mpv)
        {
            // A 7z archive holding the library and the headers for programmers; only the library is wanted.
            using var packed = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(download);
            var library = packed.Entries.FirstOrDefault(e => !e.IsDirectory && Path.GetFileName(e.Key ?? "").Equals(MpvLibraryName, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidDataException($"The libmpv archive did not contain {MpvLibraryName}.");
            cancellationToken.ThrowIfCancellationRequested();
            using var source = library.OpenEntryStream();
            using var target = File.Create(Path.Combine(folder, MpvLibraryName));
            source.CopyTo(target);
            return;
        }

        using var archive = ZipFile.OpenRead(download);
        if (tool == Ffmpeg)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ffmpeg.exe", "ffprobe.exe" };
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (wanted.Remove(entry.Name))
                    entry.ExtractToFile(Path.Combine(folder, entry.Name.ToLowerInvariant()));
            }

            if (wanted.Count > 0)
                throw new InvalidDataException($"The FFmpeg archive did not contain {string.Join(", ", wanted)}.");
            return;
        }

        // whisper.cpp: the command line program, as whisper.exe, with the libraries it loads: whisper.dll and
        // the ggml ones. The archive's other programs (server, benchmarks, tests) are left behind.
        // Called whisper-cli.exe now; older builds called it main.exe, which newer ones keep only as a stub.
        var program = archive.Entries.FirstOrDefault(e => e.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
                      ?? archive.Entries.FirstOrDefault(e => e.Name.Equals("main.exe", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException("The whisper.cpp archive did not contain whisper-cli.exe.");
        program.ExtractToFile(Path.Combine(folder, "whisper.exe"));

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isLibrary = entry.Name.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase)
                            || (entry.Name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)
                                && entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (isLibrary)
                entry.ExtractToFile(Path.Combine(folder, entry.Name), overwrite: true);
        }
    }

    // ----- Speech models -----

    /// <summary>Roughly what a model weighs, for showing before it is downloaded.</summary>
    public static long ApproximateModelBytes(string model)
    {
        if (model == VadModel)
            return 885098;

        var megabytes = model switch
        {
            _ when model.Contains("tiny", StringComparison.Ordinal) => 75,
            _ when model.Contains("base", StringComparison.Ordinal) => 142,
            _ when model.Contains("small", StringComparison.Ordinal) => 466,
            _ when model.Contains("medium", StringComparison.Ordinal) => 1463,
            _ => 0,
        };
        return megabytes * 1048576L;
    }

    /// <summary>
    /// Downloads a speech model into the models folder, checked against the SHA-256 its host publishes.
    /// A model already there is only replaced once the new file is complete and has checked out.
    /// </summary>
    public static async Task InstallModelAsync(string model, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!WhisperModels.Contains(model) && model != VadModel)
            throw new InvalidOperationException($"Unknown model: {model}");

        var url = model == VadModel ? VadModelUrl : AppSettings.WhisperModelBaseUrl + model;
        var staging = Path.Combine(StagingRoot, $"model_{Guid.NewGuid():N}");
        var download = Path.Combine(staging, model);
        try
        {
            Directory.CreateDirectory(staging);
            var expected = await FindLinkedHashAsync(url, cancellationToken);
            var hash = await DownloadAsync(url, download, model, progress, cancellationToken);
            Verify(model, hash, expected);

            Directory.CreateDirectory(ModelsFolder);
            File.Move(download, GetWhisperModelPath(model), overwrite: true);
            AppLog.Write($"Installed the model {model} ({(expected is null ? "no checksum published" : "SHA-256 verified")}).");
        }
        finally
        {
            DeleteFolder(staging);
        }
    }

    /// <summary>Downloads a speech model. Returns a line for the status bar.</summary>
    public static async Task<string> DownloadWhisperModelAsync(string model, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!WhisperModels.Contains(model))
            return $"Unknown model: {model}";

        try
        {
            await InstallModelAsync(model, new Progress<InstallProgress>(p => progress.Report(p.Text)), cancellationToken);
        }
        catch (Exception ex) when (IsInstallFailure(ex))
        {
            return $"The model {model} could not be downloaded: {ex.Message}";
        }

        return $"Model {model} downloaded ({new FileInfo(GetWhisperModelPath(model)).Length / 1048576.0:0} MB).";
    }

    /// <summary>What an install can fail with, other than being cancelled: the network, the disk, or a file that is not what it should be.</summary>
    public static bool IsInstallFailure(Exception ex) =>
        ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException
            or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    // ----- Downloading and checking -----

    /// <summary>Downloads to a file and returns its SHA-256, worked out as the bytes arrive.</summary>
    private static async Task<string> DownloadAsync(
        string url, string destination, string label, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new InstallProgress(-1, $"Downloading {label}..."));

        // A file, whatever the server calls its type: the client's default asks for GitHub's API format.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("*/*");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long received = 0;
        var lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sha.AppendData(buffer, 0, read);
            received += read;

            if (total is > 0)
            {
                var percent = (int)(received * 100 / total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report(new InstallProgress(
                        (double)received / total.Value, $"Downloading {label}... {percent}% of {total.Value / 1048576.0:0.0} MB"));
                }
            }
        }

        if (total is > 0 && received != total.Value)
            throw new InvalidDataException($"The download of {label} ended early ({received} of {total.Value} bytes).");

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void Verify(string label, string actual, string? expected)
    {
        if (expected is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The download of {label} does not match its published checksum, and was thrown away.");
    }

    /// <summary>Looks the file up in the checksum list attached to its release, when there is one.</summary>
    private static async Task<string?> FindInSumsAsync(RemoteAsset asset, CancellationToken cancellationToken)
    {
        if (asset.SumsUrl is null)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.SumsUrl);
        request.Headers.Accept.ParseAdd("*/*");
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Lines of "<hash>  <file name>", the name sometimes marked with a star.
        foreach (var line in (await response.Content.ReadAsStringAsync(cancellationToken)).Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == asset.Name && IsSha256(parts[0]))
                return parts[0];
        }

        return null;
    }

    /// <summary>
    /// The SHA-256 of a large file on Hugging Face, which it names in the answer that redirects to the file itself.
    /// Null when it does not, or cannot be asked: the download then goes ahead unchecked.
    /// </summary>
    private static async Task<string?> FindLinkedHashAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.Accept.ParseAdd("*/*");
            using var response = await HttpNoRedirect.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("X-Linked-ETag", out var values)
                && values.FirstOrDefault()?.Trim('"', ' ') is { } hash && IsSha256(hash))
            {
                return hash;
            }
        }
        catch (HttpRequestException)
        {
        }

        return null;
    }

    private static bool IsSha256(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);

            // The staging root too, once nothing is left in it.
            if (Directory.Exists(StagingRoot) && !Directory.EnumerateFileSystemEntries(StagingRoot).Any())
                Directory.Delete(StagingRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Could not remove {folder}", ex);
        }
    }

    private static HttpClient CreateClient(bool followRedirects)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects });
        // GitHub's API rejects requests without a User-Agent with 403.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HandPeg/1.0");
        if (followRedirects)
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
