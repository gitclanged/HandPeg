using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace HandPegApp.Services;

/// <summary>
/// Installs and updates yt-dlp, ffmpeg, ffprobe and whisper.cpp in a "bin" folder next to the application.
/// Nothing is shipped with Handpeg itself: every tool arrives through this class, on request.
/// </summary>
public sealed class DependencyUpdater
{
    private const string YtDlpAssetName = "yt-dlp.exe";
    private const string FfmpegAssetName = "ffmpeg-master-latest-win64-gpl.zip";
    private const string WhisperAssetName = "whisper-bin-x64.zip";

    /// <summary>The speech models offered for download: English-only ones first (smaller and better for English), then multilingual.</summary>
    public static IReadOnlyList<string> WhisperModels { get; } =
    [
        "ggml-tiny.en.bin", "ggml-base.en.bin", "ggml-small.en.bin", "ggml-medium.en.bin",
        "ggml-tiny.bin", "ggml-base.bin", "ggml-small.bin", "ggml-medium.bin",
    ];

    private static readonly HttpClient Http = CreateClient();

    public static string BinDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "bin");

    // Where this class installs the tools. Downloads only ever go here, never over a path the user configured.
    private static string LocalYtDlpPath { get; } = Path.Combine(BinDirectory, "yt-dlp.exe");
    private static string LocalFfmpegPath { get; } = Path.Combine(BinDirectory, "ffmpeg.exe");
    private static string LocalFfprobePath { get; } = Path.Combine(BinDirectory, "ffprobe.exe");

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

    /// <summary>whisper.cpp's command line program as this class installs it: under this name, whatever the release calls it.</summary>
    private static string LocalWhisperPath { get; } = Path.Combine(BinDirectory, "whisper.exe");

    /// <summary>The whisper program auto-captions run: the one named in the settings when there is one, otherwise the local copy.</summary>
    public static string WhisperPath => OrDefault(AppSettings.Current.WhisperPath, LocalWhisperPath);

    /// <summary>True when the settings name a specific whisper.exe instead of the local copy.</summary>
    public static bool IsWhisperOverridden => !string.IsNullOrWhiteSpace(AppSettings.Current.WhisperPath);

    /// <summary>The model chosen in the settings, in the tools folder.</summary>
    public static string WhisperModelPath => GetWhisperModelPath(AppSettings.Current.WhisperModel);

    public static string GetWhisperModelPath(string model) => Path.Combine(BinDirectory, Path.GetFileName(model));

    private static string WhisperReleaseUrl => OrDefault(AppSettings.Current.WhisperReleaseUrl, AppSettings.DefaultWhisperReleaseUrl);
    private static string WhisperStampPath => Path.Combine(BinDirectory, "whisper.version");

    /// <summary>True when the settings name a specific ffmpeg.exe instead of the local copy.</summary>
    public static bool IsFfmpegOverridden => !string.IsNullOrWhiteSpace(AppSettings.Current.FfmpegPath);

    private static string YtDlpReleaseUrl => OrDefault(AppSettings.Current.YtDlpReleaseUrl, AppSettings.DefaultYtDlpReleaseUrl);
    private static string FfmpegReleaseUrl => OrDefault(AppSettings.Current.FfmpegReleaseUrl, AppSettings.DefaultFfmpegReleaseUrl);

    private static string OrDefault(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string YtDlpStampPath => Path.Combine(BinDirectory, "yt-dlp.version");
    private static string FfmpegStampPath => Path.Combine(BinDirectory, "ffmpeg.version");

    private sealed record ReleaseAsset(string DownloadUrl, string Stamp);

    /// <summary>File names of the tools that are not installed yet.</summary>
    public static IReadOnlyList<string> GetMissing() =>
        new[] { YtDlpPath, FfmpegPath, FfprobePath }.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList()!;

    /// <summary>
    /// Downloads whatever is missing, and replaces tools that have a newer release available.
    /// </summary>
    public async Task<string> InstallOrUpdateAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(BinDirectory);

        var updated = new List<string>();
        if (await UpdateYtDlpAsync(progress, cancellationToken))
            updated.Add("yt-dlp");
        if (await UpdateFfmpegAsync(progress, cancellationToken))
            updated.Add("FFmpeg");

        // whisper.cpp is only needed for auto-captions, so a problem with it does not undo the rest.
        var whisperProblem = "";
        try
        {
            if (await UpdateWhisperAsync(progress, cancellationToken))
                updated.Add("whisper.cpp");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException or IOException or InvalidDataException)
        {
            whisperProblem = $" whisper.cpp could not be installed: {ex.Message}";
        }

        return (updated.Count > 0 ? $"Installed or updated: {string.Join(", ", updated)}." : "Dependencies are already up to date.") + whisperProblem;
    }

    /// <summary>Downloads a speech model into the tools folder. Returns a line for the status bar.</summary>
    public static async Task<string> DownloadWhisperModelAsync(string model, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!WhisperModels.Contains(model))
            return $"Unknown model: {model}";

        Directory.CreateDirectory(BinDirectory);
        var target = GetWhisperModelPath(model);
        var temp = target + ".download";
        try
        {
            await DownloadAsync(AppSettings.WhisperModelBaseUrl + model, temp, model, progress, cancellationToken);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        return $"Model {model} downloaded ({new FileInfo(target).Length / 1048576.0:0} MB).";
    }

    private static async Task<bool> UpdateWhisperAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Checking the latest whisper.cpp build...");
        var asset = await GetAssetAsync(WhisperReleaseUrl, WhisperAssetName, cancellationToken);
        if (File.Exists(LocalWhisperPath) && ReadStamp(WhisperStampPath) == asset.Stamp)
            return false;

        var zipPath = Path.Combine(BinDirectory, "whisper.zip.download");
        try
        {
            await DownloadAsync(asset.DownloadUrl, zipPath, "whisper.cpp", progress, cancellationToken);

            progress.Report("Extracting whisper.cpp...");
            await Task.Run(() => ExtractWhisper(zipPath, cancellationToken), cancellationToken);
        }
        finally
        {
            File.Delete(zipPath);
        }

        File.WriteAllText(WhisperStampPath, asset.Stamp);
        return true;
    }

    /// <summary>
    /// Takes the command line program out of the archive, as whisper.exe, with the libraries it loads:
    /// whisper.dll and the ggml ones. The archive's other programs (server, benchmarks, tests) are left behind.
    /// </summary>
    private static void ExtractWhisper(string zipPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        // Called whisper-cli.exe now; older builds called it main.exe, which newer ones keep only as a stub.
        var program = archive.Entries.FirstOrDefault(e => e.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
                      ?? archive.Entries.FirstOrDefault(e => e.Name.Equals("main.exe", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("The whisper.cpp archive did not contain whisper-cli.exe.");
        program.ExtractToFile(LocalWhisperPath, overwrite: true);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isLibrary = entry.Name.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase)
                            || (entry.Name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)
                                && entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (isLibrary)
                entry.ExtractToFile(Path.Combine(BinDirectory, entry.Name), overwrite: true);
        }
    }

    /// <summary>
    /// Quietly checks whether Install/Update would have anything to do: a local tool is missing, or its
    /// release is newer than the installed one. Tools replaced by a path from the settings are not checked.
    /// A failed check (offline, rate limited) reports false rather than raising an alarm.
    /// </summary>
    public static async Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        var settings = AppSettings.Current;
        try
        {
            if (string.IsNullOrWhiteSpace(settings.YtDlpPath))
            {
                if (!File.Exists(LocalYtDlpPath))
                    return true;

                var asset = await GetAssetAsync(YtDlpReleaseUrl, YtDlpAssetName, cancellationToken);
                if (ReadStamp(YtDlpStampPath) != asset.Stamp)
                    return true;
            }

            if (string.IsNullOrWhiteSpace(settings.FfmpegPath))
            {
                if (!File.Exists(LocalFfmpegPath) || !File.Exists(LocalFfprobePath))
                    return true;

                var asset = await GetAssetAsync(FfmpegReleaseUrl, FfmpegAssetName, cancellationToken);
                if (ReadStamp(FfmpegStampPath) != asset.Stamp)
                    return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException or IOException)
        {
        }

        return false;
    }

    private static async Task<bool> UpdateYtDlpAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Checking the latest yt-dlp release...");
        var asset = await GetAssetAsync(YtDlpReleaseUrl, YtDlpAssetName, cancellationToken);
        if (File.Exists(LocalYtDlpPath) && ReadStamp(YtDlpStampPath) == asset.Stamp)
            return false;

        var temp = LocalYtDlpPath + ".download";
        try
        {
            await DownloadAsync(asset.DownloadUrl, temp, "yt-dlp", progress, cancellationToken);
            File.Move(temp, LocalYtDlpPath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        File.WriteAllText(YtDlpStampPath, asset.Stamp);
        return true;
    }

    private static async Task<bool> UpdateFfmpegAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Checking the latest FFmpeg build...");
        var asset = await GetAssetAsync(FfmpegReleaseUrl, FfmpegAssetName, cancellationToken);
        if (File.Exists(LocalFfmpegPath) && File.Exists(LocalFfprobePath) && ReadStamp(FfmpegStampPath) == asset.Stamp)
            return false;

        var zipPath = Path.Combine(BinDirectory, "ffmpeg.zip.download");
        try
        {
            await DownloadAsync(asset.DownloadUrl, zipPath, "FFmpeg", progress, cancellationToken);

            progress.Report("Extracting FFmpeg...");
            await Task.Run(() => ExtractExecutables(zipPath, cancellationToken), cancellationToken);
        }
        finally
        {
            File.Delete(zipPath);
        }

        File.WriteAllText(FfmpegStampPath, asset.Stamp);
        return true;
    }

    private static void ExtractExecutables(string zipPath, CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ffmpeg.exe"] = LocalFfmpegPath,
            ["ffprobe.exe"] = LocalFfprobePath,
        };

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (wanted.Remove(entry.Name, out var destination))
                entry.ExtractToFile(destination, overwrite: true);
        }

        if (wanted.Count > 0)
            throw new InvalidOperationException($"The FFmpeg archive did not contain {string.Join(", ", wanted.Keys)}.");
    }

    private static async Task<ReleaseAsset> GetAssetAsync(string releaseUrl, string assetName, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(releaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {releaseUrl}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // One release, or a list of them newest first: the first one that has the asset is the one to use.
        var root = json.RootElement;
        var releases = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        foreach (var release in releases)
        {
            var tag = release.GetProperty("tag_name").GetString();
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != assetName)
                    continue;

                // Rolling releases keep the same tag, so the asset timestamp is part of the version stamp.
                var url = asset.GetProperty("browser_download_url").GetString()!;
                var updatedAt = asset.GetProperty("updated_at").GetString();
                return new ReleaseAsset(url, $"{tag}|{updatedAt}");
            }
        }

        throw new InvalidOperationException($"No release at {releaseUrl} has an asset named {assetName}.");
    }

    private static async Task DownloadAsync(
        string url, string destination, string label, IProgress<string> progress, CancellationToken cancellationToken)
    {
        // A file, whatever the server calls its type: the client's default asks for GitHub's API format.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("*/*");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        var lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            if (total is > 0)
            {
                var percent = (int)(received * 100 / total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report($"Downloading {label}... {percent}% of {total.Value / 1048576.0:0.0} MB");
                }
            }
        }
    }

    private static string? ReadStamp(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        // GitHub's API rejects requests without a User-Agent with 403.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Handpeg/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
