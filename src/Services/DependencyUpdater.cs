using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
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

    /// <summary>Custom executable path from settings; used as-is and never replaced.</summary>
    CustomPath,
}

/// <summary>Install progress: fraction 0-1 (negative = indeterminate) and a status line.</summary>
public readonly record struct InstallProgress(double Fraction, string Text);

/// <summary>Tool release asset as described by GitHub.</summary>
/// <param name="Stamp">Build identity, compared with the stamp stored beside the installed copy.</param>
/// <param name="Sha256">Expected digest: pinned, else GitHub-published, when available.</param>
/// <param name="SumsUrl">Release checksum list used when no digest exists.</param>
public sealed record RemoteAsset(string Name, string DownloadUrl, string Stamp, string Tag, long Size, string? Sha256, string? SumsUrl)
{
    /// <summary>Pinned, substitute, or newest-release build.</summary>
    public PinState Pin { get; init; }

    /// <summary>Rank among the tool's <see cref="DependencyUpdater.PinnedBuilds"/> (best first); beyond the list for an off-list substitute.</summary>
    public int PinRank { get; init; } = DependencyUpdater.OutsideListRank;

    /// <summary>Which of the tool's pinned lists it was taken from (see <see cref="PinnedBuild.Variant"/>).</summary>
    public string Variant { get; init; } = "";

    /// <summary>How the dependency list names the build: the pinned build's label, or the release's tag.</summary>
    public string Label { get; init; } = "";

    /// <summary>Why a stand-in was taken, in a few words for the list and the log; empty otherwise.</summary>
    public string Note { get; init; } = "";
}

/// <summary>Where a build stands against the builds pinned for this version of HandPeg.</summary>
public enum PinState
{
    /// <summary>Newest upstream release: unpinned tool (yt-dlp), unpinned updates allowed, or a custom release URL.</summary>
    Unpinned,

    /// <summary>The build this version of HandPeg was tested with.</summary>
    Pinned,

    /// <summary>A kept build standing in for the pinned one, which upstream no longer publishes.</summary>
    StandIn,
}

/// <summary>
/// Pinned default build of a tool. The first entry per tool/variant is the tested build; later entries are ordered substitutes
/// used when upstream stops publishing it.
/// </summary>
/// <param name="Variant">
/// <see cref="DependencyUpdater.OlderNvidiaVariant"/> (FFmpeg for older NVIDIA drivers), <see cref="DependencyUpdater.Arm64Variant"/>
/// (FFmpeg ARM64), the asset name for whisper.cpp (per-hardware builds); empty otherwise.
/// </param>
/// <param name="Repository">GitHub owner/name.</param>
/// <param name="Tag">Immutable dated or numbered release tag.</param>
/// <param name="Asset">Release asset file name.</param>
/// <param name="Sha256">Pinned SHA-256, enforced regardless of what the release reports.</param>
/// <param name="Size">Size in bytes, shown before download.</param>
/// <param name="Version">Self-reported version (ffmpeg -version; tag for whisper.cpp), used to recognise pre-pin installs.</param>
/// <param name="Published">Release date; off-list substitutes are chosen nearest to it.</param>
/// <param name="Label">Display label.</param>
/// <param name="Mirror">Alternate URL for the identical file if the GitHub release disappears; verified against the same SHA-256.</param>
public sealed record PinnedBuild(
    string Tool, string Variant, string Repository, string Tag, string Asset, string Sha256, long Size, string Version, DateTime Published, string Label,
    string? Mirror = null)
{
    /// <summary>GitHub download URL; a HEAD request checks availability without consuming API rate limit.</summary>
    public string DownloadUrl => $"https://github.com/{Repository}/releases/download/{Tag}/{Asset}";
}

/// <summary>
/// Installs and updates FFmpeg/ffprobe, yt-dlp, whisper.cpp and speech models into per-tool folders under deps
/// (<see cref="AppPaths"/>); nothing is bundled.
/// FFmpeg and whisper.cpp use tested pinned builds (<see cref="PinnedBuilds"/>) unless
/// <see cref="AppSettings.AllowUnpinnedDependencyUpdates"/>; yt-dlp tracks its newest release.
/// Installs never touch the live copy: download to staging, verify SHA-256 (pinned or published), unpack, run once, then swap
/// folders. Any failure or cancellation leaves the existing install untouched.
/// </summary>
public static partial class DependencyUpdater
{
    public const string Ffmpeg = "ffmpeg";
    public const string YtDlp = "yt-dlp";
    public const string Whisper = "whisper";

    /// <summary>Managed tools in list/install order (retired libmpv cleanup: <see cref="DeleteRetiredPlayer"/>).</summary>
    public static IReadOnlyList<string> Tools { get; } = [Ffmpeg, YtDlp, Whisper];

    // ----- ARM64: a win-arm64 process installs native ARM64 tool builds -----
    // x64 tools would run under Prism emulation; each tool publishes ARM64 builds, installed into separate folders (ToolFolder)
    // so x64 and ARM64 builds never mix.

    /// <summary>The variant of FFmpeg built for ARM64 Windows (<see cref="PinnedBuild.Variant"/>).</summary>
    public const string Arm64Variant = "arm64";

    /// <summary>True for a win-arm64 process; tools use ARM64 builds.</summary>
    public static bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private static string YtDlpAssetName => IsArm64 ? "yt-dlp_arm64.exe" : "yt-dlp.exe";
    private static string FfmpegAssetName => IsArm64 ? "ffmpeg-master-latest-winarm64-gpl.zip" : "ffmpeg-master-latest-win64-gpl.zip";

    // whisper.cpp's plain build (on the processor alone): the one every computer can run.
    private const string WhisperX64AssetName = "whisper-bin-x64.zip";
    private const string WhisperArm64AssetName = "whisper-bin-win-cpu-arm64.zip";
    private const string WhisperArm64CudaAssetName = "whisper-bin-win-cuda-13.4-arm64.zip";
    private const string WhisperArm64AdrenoAssetName = "whisper-bin-win-opencl-adreno-arm64.zip";
    private static string WhisperAssetName => IsArm64 ? WhisperArm64AssetName : WhisperX64AssetName;

    // The builds of FFmpeg's numbered releases, published beside the newest one: "ffmpeg-n8.1-latest-win64-gpl-8.1.zip".
    [GeneratedRegex(@"^ffmpeg-n(?<version>\d+\.\d+)-latest-win64-gpl-\k<version>\.zip$")]
    private static partial Regex FfmpegReleaseAssetRegex();

    // The last FFmpeg whose NVENC runs on NVIDIA drivers before 610 (NVENC API 13.0), and so on the GTX 10 series.
    private static readonly Version LastFfmpegForOlderNvidiaDrivers = new(8, 1);

    /// <summary>Version from a numbered FFmpeg build name usable with older NVIDIA drivers, or null.</summary>
    private static Version? OlderNvidiaFfmpegVersion(string? name) =>
        name is not null && FfmpegReleaseAssetRegex().Match(name) is { Success: true } match
        && Version.Parse(match.Groups["version"].Value) is var version && version <= LastFfmpegForOlderNvidiaDrivers
            ? version
            : null;

    // Checksum lists some projects attach to a release: yt-dlp's, and the FFmpeg builds'.
    private static readonly string[] SumsAssetNames = ["SHA2-256SUMS", "checksums.sha256"];

    // ----- Pinned (tested) builds -----
    // FFmpeg and whisper.cpp publish daily and can change CLI options, so both install tested builds verified by SHA-256; yt-dlp
    // tracks upstream (sites change and old versions stop working). Upstream retention (2026-11-06): BtbN/FFmpeg-Builds keeps 14
    // daily builds plus each month's last build for two years; ggml-org/whisper.cpp keeps all releases.
    // Hence FFmpeg pins a month-end build with the previous month as substitute; beyond that, the dated build nearest the tested one
    // is used, verified against GitHub's digest. Substitutes are logged and labelled, never silent; nightly builds require opt-in.
    // A verified build that fails to start is skipped for the next candidate (InstallToolAsync).

    /// <summary>FFmpeg 8.1 line for NVIDIA drivers older than 610 (<see cref="AppSettings.UseFfmpegForOlderNvidiaDrivers"/>).</summary>
    public const string OlderNvidiaVariant = "8.1";

    /// <summary>The place of a stand-in from outside <see cref="PinnedBuilds"/>: after every listed build.</summary>
    public const int OutsideListRank = int.MaxValue;

    // What a stand-in from outside the list says in its stamp, so that it is recognised as one when it is installed.
    private const string StandInMark = "stand-in";

    private static readonly DateTime TestedFfmpegDate = new(2026, 10, 31, 12, 59, 47, DateTimeKind.Utc);
    private static readonly DateTime MonthEndFfmpegDate = new(2026, 9, 30, 13, 9, 24, DateTimeKind.Utc);
    private static readonly DateTime TestedWhisperDate = new(2026, 10, 6, 16, 51, 27, DateTimeKind.Utc);

    // ARM64 assets of the same releases, with GitHub-reported SHA-256 per file (matching the x64 pin source).
    private static readonly DateTime TestedFfmpegArm64Date = new(2026, 10, 31, 12, 59, 47, DateTimeKind.Utc);

    /// <summary>
    /// Pinned builds per tool and variant: tested build first, then substitutes in preference order. Updated per release after testing.
    /// </summary>
    public static IReadOnlyList<PinnedBuild> PinnedBuilds { get; } =
    [
        // FFmpeg: October's last build, then September's, which BtbN keeps until 2028.
        new(Ffmpeg, "", "BtbN/FFmpeg-Builds", "autobuild-2026-10-31-12-59", "ffmpeg-N-128227-g4811625b5f-win64-gpl.zip",
            "3a5cdb3b83dd1b62b29d946d3655a14d787e5e81d947ad68202a1a400095d2f8", 201662768, "N-128227-g4811625b5f-20261031", TestedFfmpegDate,
            "N-128227 (2026-10-31)"),
        new(Ffmpeg, "", "BtbN/FFmpeg-Builds", "autobuild-2026-09-30-13-08", "ffmpeg-N-127021-ge0c94b2d1c-win64-gpl.zip",
            "85c4a4636b93d681a07588ffa6f1d8f5c064d9c3fb0622066662906b6fd718a1", 198589424, "N-127021-ge0c94b2d1c-20260930", MonthEndFfmpegDate,
            "N-127021 (2026-09-30)"),

        // FFmpeg for older NVIDIA drivers: the 8.1 line, built the same days.
        new(Ffmpeg, OlderNvidiaVariant, "BtbN/FFmpeg-Builds", "autobuild-2026-10-31-12-59", "ffmpeg-n8.1.3-27-g8dca1586f4-win64-gpl-8.1.zip",
            "401dffcf14c301082e1fc03b0bc876b92ea676bfc521fc56e80a1537d137034f", 194182300, "n8.1.3-27-g8dca1586f4-20261031", TestedFfmpegDate,
            "8.1.3-27 (2026-10-31)"),
        new(Ffmpeg, OlderNvidiaVariant, "BtbN/FFmpeg-Builds", "autobuild-2026-09-30-13-08", "ffmpeg-n8.1.3-9-g29e619e767-win64-gpl-8.1.zip",
            "7b801cdd3a1a0bb54ae6f572187e68b4ed54f52086cfee133fab2180bfb429fa", 193109593, "n8.1.3-9-g29e619e767-20260930", MonthEndFfmpegDate,
            "8.1.3-9 (2026-09-30)"),

        // whisper.cpp: build b5454, in each kind HandPeg chooses between (see WhisperAssetNames).
        new(Whisper, "whisper-bin-win-cuda-12.4.0-x64.zip", "ggml-org/whisper.cpp", "b5454", "whisper-bin-win-cuda-12.4.0-x64.zip",
            "afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a", 684913404, "b5454", TestedWhisperDate, "b5454 (CUDA)"),
        new(Whisper, "whisper-blas-bin-x64.zip", "ggml-org/whisper.cpp", "b5454", "whisper-blas-bin-x64.zip",
            "b1158bd10edf92c9799149a6fc5d3e58da3f495241d6fb3423088130e88ffbf3", 21715607, "b5454", TestedWhisperDate, "b5454 (OpenBLAS)"),
        new(Whisper, WhisperX64AssetName, "ggml-org/whisper.cpp", "b5454", WhisperX64AssetName,
            "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb", 8928640, "b5454", TestedWhisperDate, "b5454"),

        // ARM64: FFmpeg master build from the last day of October. September's (N-127021) is excluded: it crashes immediately with
        // 0xC0000005 on Snapdragon X (BrokenAssets).
        new(Ffmpeg, Arm64Variant, "BtbN/FFmpeg-Builds", "autobuild-2026-10-31-12-59", "ffmpeg-N-128227-g4811625b5f-winarm64-gpl.zip",
            "7d2bc069f7eda052fac7d4d9ff40eabda5d9fd8b6105d479129547ad3b822919", 142303693, "N-128227-g4811625b5f-20261031", TestedFfmpegArm64Date,
            "N-128227 (2026-10-31, ARM64)"),

        // whisper.cpp b5454 for ARM64: CUDA (an NVIDIA card), OpenCL on Qualcomm's Adreno graphics, and the processor alone.
        new(Whisper, WhisperArm64CudaAssetName, "ggml-org/whisper.cpp", "b5454", WhisperArm64CudaAssetName,
            "6184ae8887d38ea9add68e883b9c846e7bf562ba1bf619ffc7ee9f8ec7e1699c", 291157544, "b5454", TestedWhisperDate, "b5454 (CUDA, ARM64)"),
        new(Whisper, WhisperArm64AdrenoAssetName, "ggml-org/whisper.cpp", "b5454", WhisperArm64AdrenoAssetName,
            "915b48289f362c5f4cf33f3ffdd30f1f43de68f6103a3088858e346635719007", 4944716, "b5454", TestedWhisperDate, "b5454 (Adreno OpenCL, ARM64)"),
        new(Whisper, WhisperArm64AssetName, "ggml-org/whisper.cpp", "b5454", WhisperArm64AssetName,
            "28c37e7b598c3d9bbfef94f3bd67f2ee6f12b7c86f3da5edcf5e4308d615ff6d", 4371193, "b5454", TestedWhisperDate, "b5454 (ARM64)"),
    ];

    // The files a stand-in from outside the list may be: the same kind of build as the pinned one.
    [GeneratedRegex(@"^ffmpeg-N-\d+-g[0-9a-f]+-win64-gpl\.zip$")]
    private static partial Regex FfmpegMasterAssetRegex();

    [GeneratedRegex(@"^ffmpeg-n8\.1(?:\.\d+)*(?:-\d+-g[0-9a-f]+)?-win64-gpl-8\.1\.zip$")]
    private static partial Regex FfmpegOlderNvidiaAssetRegex();

    [GeneratedRegex(@"^ffmpeg-N-\d+-g[0-9a-f]+-winarm64-gpl\.zip$")]
    private static partial Regex FfmpegArm64AssetRegex();

    /// <summary>Upstream builds known not to start: never taken, listed or found as a stand-in.</summary>
    public static IReadOnlySet<string> BrokenAssets { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // BtbN's 2026-09-30 ARM64 build: ffmpeg.exe and ffprobe.exe end at once with 0xC0000005, even for -version.
        "ffmpeg-N-127021-ge0c94b2d1c-winarm64-gpl.zip",
    };

    // Verified builds that failed to start this session (InstallToolAsync); skipped like BrokenAssets.
    private static readonly HashSet<string> WontStartHere = new(StringComparer.Ordinal);

    // How many builds an install tries, one after another, when each in turn will not start.
    private const int MostBuildsTried = 3;

    /// <summary>Whether a build is never to be taken on this computer: known broken, or it would not start here.</summary>
    public static bool IsPassedOver(string assetName)
    {
        lock (WontStartHere)
            return BrokenAssets.Contains(assetName) || WontStartHere.Contains(assetName);
    }

    /// <summary>Downloadable speech models: English-only first (smaller, better for English), then multilingual.</summary>
    public static IReadOnlyList<string> WhisperModels { get; } =
    [
        "ggml-tiny.en.bin", "ggml-base.en.bin", "ggml-small.en.bin", "ggml-medium.en.bin",
        "ggml-tiny.bin", "ggml-base.bin", "ggml-small.bin", "ggml-medium.bin",
        "ggml-large-v3.bin", "ggml-large-v3-turbo.bin",
    ];

    /// <summary>
    /// Installable whisper.cpp builds, best first for this machine. x64: CUDA (NVIDIA), OpenBLAS (CPU), plain. No published Vulkan
    /// build for AMD/Intel, so OpenBLAS is used there (a custom Vulkan build can be set in Settings). ARM64: CUDA, OpenCL for Adreno
    /// (requires the driver's OpenCL loader), plain; the first runnable one is chosen.
    /// </summary>
    public static IReadOnlyList<string> WhisperAssetNames { get; } = IsArm64
        ? [.. (HasNvidiaCuda ? [WhisperArm64CudaAssetName] : Array.Empty<string>()), .. (HasSystemOpenCl ? [WhisperArm64AdrenoAssetName] : Array.Empty<string>()), WhisperArm64AssetName]
        : HasNvidiaCuda
            ? ["whisper-cublas-12.4.0-bin-x64.zip", "whisper-bin-win-cuda-12.4.0-x64.zip", "whisper-blas-bin-x64.zip", WhisperX64AssetName]
            : ["whisper-blas-bin-x64.zip", WhisperX64AssetName];

    /// <summary>True when the driver's OpenCL loader is in System32 (required by the Adreno whisper.cpp build).</summary>
    private static bool HasSystemOpenCl
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(Environment.SystemDirectory, "OpenCL.dll"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>An NVIDIA driver with CUDA is installed: its runtime library is in the system folder.</summary>
    private static bool HasNvidiaCuda
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    // How good a download is for a tool: lower is better. Only whisper has more than one to choose between.
    private static int AssetRank(string tool, string? name)
    {
        var rank = tool == Whisper && name is not null ? WhisperAssetNames.ToList().IndexOf(name) : 0;
        return rank < 0 ? int.MaxValue : rank;
    }

    /// <summary>
    /// Silero VAD model (under 1 MB) used by whisper.cpp to skip silence. Optional; without it captions may hallucinate in quiet parts.
    /// </summary>
    public const string VadModel = "ggml-silero-v5.1.2.bin";

    /// <summary>
    /// Pinned SHA-256 per offered model (Hugging Face LFS digests, 2026-11-15), enforced instead of the server's claim. Unlisted models
    /// fall back to the server's linked hash and are refused without one (<c>model-checksums-pinned</c> golden).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ModelSha256 { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ggml-tiny.en.bin"] = "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f",
        ["ggml-base.en.bin"] = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
        ["ggml-small.en.bin"] = "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d",
        ["ggml-medium.en.bin"] = "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356",
        ["ggml-tiny.bin"] = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
        ["ggml-base.bin"] = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        ["ggml-small.bin"] = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        ["ggml-medium.bin"] = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        ["ggml-large-v3.bin"] = "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
        ["ggml-large-v3-turbo.bin"] = "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
        [VadModel] = "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf",
    };

    private const string VadModelUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/" + VadModel;

    // Lazy: the class is touched before Frame 1, and constructing HttpClient costs a few ms nothing at startup needs.
    private static readonly Lazy<HttpClient> LazyHttp = new(() => CreateClient(followRedirects: true));
    private static readonly Lazy<HttpClient> LazyHttpNoRedirect = new(() => CreateClient(followRedirects: false));

    private static HttpClient Http => LazyHttp.Value;

    // For asking where a download redirects to, and what the answer says about the file, without fetching it.
    private static HttpClient HttpNoRedirect => LazyHttpNoRedirect.Value;

    // ----- Where things are -----

    /// <summary>The folder this class keeps a tool in.</summary>
    public static string ToolFolder(string tool) => Path.Combine(AppPaths.Deps, IsArm64 ? $"{tool}-{Arm64Variant}" : tool);

    private static string ModelsFolder => Path.Combine(AppPaths.Deps, "models");
    private static string StagingRoot => Path.Combine(AppPaths.Deps, ".staging");

    // Where this class installs the tools. Downloads only ever go here, never over a path the user configured.
    private static string LocalYtDlpPath => Path.Combine(ToolFolder(YtDlp), "yt-dlp.exe");
    private static string LocalFfmpegPath => Path.Combine(ToolFolder(Ffmpeg), "ffmpeg.exe");
    private static string LocalFfprobePath => Path.Combine(ToolFolder(Ffmpeg), "ffprobe.exe");

    /// <summary>Installed whisper.cpp CLI name, normalized regardless of the release's naming.</summary>
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

    /// <summary>whisper executable for auto-captions: custom path from settings, else the managed copy.</summary>
    public static string WhisperPath => OrDefault(AppSettings.Current.WhisperPath, LocalWhisperPath);

    /// <summary>True when the settings name a specific whisper.exe instead of the local copy.</summary>
    public static bool IsWhisperOverridden => !string.IsNullOrWhiteSpace(AppSettings.Current.WhisperPath);

    // ggml GPU backend libraries loaded from beside whisper.exe (b5454+), mapped to backend names.
    private static readonly (string Library, string Backend)[] WhisperGpuLibraries =
        [("ggml-cuda.dll", "CUDA"), ("ggml-opencl.dll", "OpenCL"), ("ggml-vulkan.dll", "Vulkan"), ("ggml-sycl.dll", "SYCL")];

    /// <summary>
    /// GPU backend of the active whisper.cpp from the adjacent ggml library: "CUDA", "OpenCL" (Adreno), "Vulkan" (custom); empty for
    /// CPU-only or statically linked builds. Performs file I/O; not for the UI thread before Frame 1.
    /// </summary>
    public static string WhisperGpuBackend
    {
        get
        {
            try
            {
                var folder = Path.GetDirectoryName(WhisperPath);
                if (string.IsNullOrEmpty(folder) || !File.Exists(WhisperPath))
                    return "";
                foreach (var (library, backend) in WhisperGpuLibraries)
                {
                    if (File.Exists(Path.Combine(folder, library)))
                        return backend;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Not readable: as a build on the processor alone.
            }

            return "";
        }
    }

    /// <summary>
    /// True when an NPU speech build (QNN or ONNX Runtime) is installed. Currently always false: pinned whisper.cpp builds target CPU,
    /// CUDA and Adreno OpenCL only.
    /// </summary>
    public static bool NpuSpeechBuildInstalled => false;

    /// <summary>True when the settings name a specific ffmpeg.exe instead of the local copy.</summary>
    public static bool IsFfmpegOverridden => !string.IsNullOrWhiteSpace(AppSettings.Current.FfmpegPath);

    /// <summary>The model chosen in the settings, in the models folder.</summary>
    public static string WhisperModelPath => GetWhisperModelPath(AppSettings.Current.WhisperModel);

    public static string GetWhisperModelPath(string model) => Path.Combine(ModelsFolder, Path.GetFileName(model));

    public static string VadModelPath => GetWhisperModelPath(VadModel);

    private static string OrDefault(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// Target tool folder for a file from the legacy single "bin" folder, or null if unrecognized.
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
        _ => "whisper.cpp",
    };

    /// <summary>Custom executable configured for a tool, or empty.</summary>
    public static string CustomPath(string tool) => (tool switch
    {
        Ffmpeg => AppSettings.Current.FfmpegPath,
        YtDlp => AppSettings.Current.YtDlpPath,
        _ => AppSettings.Current.WhisperPath,
    }).Trim();

    private static string[] LocalPrograms(string tool) => tool switch
    {
        Ffmpeg => [LocalFfmpegPath, LocalFfprobePath],
        YtDlp => [LocalYtDlpPath],
        _ => [LocalWhisperPath],
    };

    /// <summary>What is on disk, without asking anyone: a custom path, installed, or missing.</summary>
    public static DependencyState GetLocalState(string tool) =>
        CustomPath(tool).Length > 0 ? DependencyState.CustomPath
        : LocalPrograms(tool).All(File.Exists) ? DependencyState.Installed
        : DependencyState.Missing;

    /// <summary>
    /// True when the install should be replaced by <paramref name="latest"/>: for newest-release mode when it differs; for pinned
    /// mode when the install is not a pinned build or a better-ranked one is now available. Never downgrades an installed pin to a substitute.
    /// </summary>
    public static bool IsOutdated(string tool, RemoteAsset latest)
    {
        var stamp = ReadText(StampPath(ToolFolder(tool), tool));
        if (stamp == latest.Stamp)
            return false;
        if (latest.Pin == PinState.Unpinned)
            return true;

        return InstalledPinRank(tool, latest.Variant) is not { } rank || latest.PinRank < rank;
    }

    /// <summary>
    /// Rank of the install among a variant's pinned builds, <see cref="OutsideListRank"/> for an off-list substitute, or null (newest
    /// release, other variant, none). Pre-pin installs are matched by reported version.
    /// </summary>
    public static int? InstalledPinRank(string tool, string variant)
    {
        var folder = ToolFolder(tool);
        if (ReadText(StampPath(folder, tool)) is not { Length: > 0 } stamp)
            return null;

        var builds = PinnedFor(tool, variant);
        var index = builds.FindIndex(b => stamp == PinStamp(b));
        if (index >= 0)
            return index;

        // Pre-pin install: same build if the reported version matches; whisper.cpp stamps also encode the build kind, which must match.
        var version = ReadText(VersionPath(folder)) ?? "";
        var parts = stamp.Split('|');
        index = builds.FindIndex(b => b.Version == version
                                      && (tool != Whisper || (parts.Length > 2 ? parts[^1] : WhisperAssetName) == b.Asset));
        if (index >= 0)
            return index;

        return parts is [_, var asset, StandInMark] && IsVariantAsset(tool, variant, asset) ? OutsideListRank : null;
    }

    /// <summary>What the stamp of a pinned build says: its release and its file.</summary>
    private static string PinStamp(PinnedBuild build) => $"{build.Tag}|{build.Asset}";

    /// <summary>The pinned builds of a tool's variant, the tested one first.</summary>
    private static List<PinnedBuild> PinnedFor(string tool, string variant) => PinnedBuilds.Where(b => b.Tool == tool && b.Variant == variant).ToList();

    /// <summary>True when a release asset matches a variant's build kind (eligible as an off-list substitute).</summary>
    internal static bool IsVariantAsset(string tool, string variant, string name) => tool switch
    {
        Ffmpeg => variant switch
        {
            OlderNvidiaVariant => FfmpegOlderNvidiaAssetRegex().IsMatch(name),
            Arm64Variant => FfmpegArm64AssetRegex().IsMatch(name),
            _ => FfmpegMasterAssetRegex().IsMatch(name),
        },
        Whisper => name == variant,
        _ => false,
    };

    /// <summary>File names of the tools that are not installed yet.</summary>
    public static IReadOnlyList<string> GetMissing() =>
        new[] { YtDlpPath, FfmpegPath, FfprobePath }.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList()!;

    private static string StampPath(string folder, string tool) => Path.Combine(folder, tool + ".version");
    private static string VersionPath(string folder) => Path.Combine(folder, "version.txt");

    /// <summary>
    /// Installed version for display, from the stamp file written at install; legacy installs without one are queried by running once.
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
    /// Runs the tool in <paramref name="folder"/> to read its version, also proving a fresh download starts. whisper.cpp reports
    /// none, so its release tag is used.
    /// </summary>
    private static async Task<string> ProbeVersionAsync(string tool, string folder, CancellationToken cancellationToken)
    {
        var result = tool switch
        {
            Ffmpeg => await RunOnceAsync(folder, "ffmpeg.exe", "-version", cancellationToken),
            YtDlp => await RunOnceAsync(folder, "yt-dlp.exe", "--version", cancellationToken),
            _ => await RunOnceAsync(folder, "whisper.exe", "--help", cancellationToken),
        };

        // ffprobe comes in the same archive and is run as often (every file opened): it has to start too.
        if (tool == Ffmpeg)
            await RunOnceAsync(folder, "ffprobe.exe", "-version", cancellationToken);

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

    /// <summary>Runs a tool executable once and returns its output; throws <see cref="WontStartException"/> if it cannot start.</summary>
    private static async Task<BufferedCommandResult> RunOnceAsync(string folder, string program, string argument, CancellationToken cancellationToken)
    {
        // Check the PE machine type first: starting a wrong-architecture binary makes Windows show an unsolicited dialog.
        if (!IsBinaryForThisMachine(Path.Combine(folder, program)))
            throw new WontStartException($"{program} is not a {ExpectedMachine} Windows program.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        BufferedCommandResult result;
        try
        {
            result = await WorkScheduler.Run(new WorkSpec($"Checking {program}", WorkLane.Interactive) { Visible = false },
                work => ProcessPipes.RunBufferedAsync(Cli.Wrap(Path.Combine(folder, program))
                    .WithArguments(argument)
                    .WithWorkingDirectory(folder)
                    .WithValidation(CommandResultValidation.None), work.Token), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WontStartException($"{program} did not answer.");
        }

        // Loader failures (missing DLL, wrong architecture) exit with an NTSTATUS; the install is refused with its meaning
        // (IsInstallFailure). Program.Main's error mode suppresses the Windows error box.
        if (result.ExitCode is < 0 or > 255)
        {
            var why = unchecked((uint)result.ExitCode) switch
            {
                0xC0000135 => "a library it needs is missing",
                0xC000007B => "it or a library it loads is not a program for this processor",
                0xC0000139 => "a library it loads is the wrong version",
                0xC0000005 => "it crashed as it started (an access violation)",
                0xC000001D => "it uses an instruction this processor does not have",
                _ => "it ended with a Windows error",
            };
            throw new WontStartException($"{program} could not start: {why} (code 0x{result.ExitCode:X8}).");
        }

        return result;
    }

    /// <summary>The PE machine types HandPeg's tools are built for: 64-bit Intel/AMD and 64-bit ARM.</summary>
    public const ushort MachineAmd64 = 0x8664, MachineArm64 = 0xAA64;

    /// <summary>
    /// True when an executable of PE <paramref name="machine"/> can run as a child of a process on <paramref name="architecture"/>:
    /// x64 runs x64 only; ARM64 runs ARM64 natively or x64 emulated.
    /// </summary>
    public static bool IsMachineAccepted(ushort machine, Architecture architecture) => architecture switch
    {
        Architecture.X64 => machine == MachineAmd64,
        Architecture.Arm64 => machine is MachineArm64 or MachineAmd64,
        _ => false,
    };

    /// <summary>
    /// PE header machine type read from <paramref name="stream"/>, or null for non-PE files.
    /// </summary>
    public static ushort? PeMachineOf(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        stream.Position = 0;
        if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            return null;

        // The old DOS header says where the real one starts.
        stream.Position = 0x3C;
        var header = reader.ReadUInt32();
        if (header + 6L > stream.Length)
            return null;

        stream.Position = header;
        return reader.ReadUInt32() == 0x00004550 ? reader.ReadUInt16() : null;
    }

    /// <summary>True when the file is a Windows program this machine can run, going by its header.</summary>
    private static bool IsBinaryForThisMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return PeMachineOf(stream) is { } machine && IsMachineAccepted(machine, RuntimeInformation.ProcessArchitecture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What a refused program should have been, for the message.</summary>
    private static string ExpectedMachine => IsArm64 ? "64-bit ARM or x64" : "64-bit Intel/AMD (x64)";

    // ----- What is available -----

    // GitHub's unauthenticated limit is 60 requests/hour: cache responses briefly so startup and Settings share them.
    private static readonly Dictionary<string, (DateTime At, RemoteAsset Asset)> Answers = [];
    private static readonly TimeSpan AnswerLifetime = TimeSpan.FromMinutes(10);

    private static string ReleaseUrl(string tool) => tool switch
    {
        Ffmpeg => HttpsOrDefault(AppSettings.Current.FfmpegReleaseUrl, AppSettings.DefaultFfmpegReleaseUrl),
        YtDlp => HttpsOrDefault(AppSettings.Current.YtDlpReleaseUrl, AppSettings.DefaultYtDlpReleaseUrl),
        _ => HttpsOrDefault(AppSettings.Current.WhisperReleaseUrl, AppSettings.DefaultWhisperReleaseUrl),
    };

    /// <summary>
    /// Custom release URL if absolute https, else the default. Guards hand-edited or imported settings (not a security boundary:
    /// whoever writes that file controls tool paths anyway).
    /// </summary>
    internal static string HttpsOrDefault(string? value, string fallback) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.OriginalString : fallback;

    /// <summary>Whether a file attached to a release is the one this tool is installed from.</summary>
    private static bool IsAssetFor(string tool, string? name) => name is not null && tool switch
    {
        Ffmpeg => name == FfmpegAssetName,
        YtDlp => name == YtDlpAssetName,
        _ => name is not null && WhisperAssetNames.Contains(name),
    };

    /// <summary>
    /// True for pinned installs: FFmpeg and whisper.cpp unless unpinned updates or a custom release URL is set; never yt-dlp.
    /// </summary>
    public static bool IsPinned(string tool) => tool != YtDlp && !AppSettings.Current.AllowUnpinnedDependencyUpdates && !HasOwnReleaseUrl(tool);

    /// <summary>True when settings override the tool's release URL.</summary>
    private static bool HasOwnReleaseUrl(string tool) => tool switch
    {
        Ffmpeg => AppSettings.Current.FfmpegReleaseUrl.Trim() is { Length: > 0 } url && url != AppSettings.DefaultFfmpegReleaseUrl,
        Whisper => AppSettings.Current.WhisperReleaseUrl.Trim() is { Length: > 0 } url && url != AppSettings.DefaultWhisperReleaseUrl,
        _ => false,
    };

    /// <summary>Wanted variants for this machine, best first (whisper build kinds, FFmpeg driver variant, ARM64).</summary>
    private static IReadOnlyList<string> WantedVariants(string tool) => tool switch
    {
        Ffmpeg => [IsArm64 ? Arm64Variant : AppSettings.Current.UseFfmpegForOlderNvidiaDrivers ? OlderNvidiaVariant : ""],
        Whisper => WhisperAssetNames.Where(name => PinnedBuilds.Any(b => b.Tool == Whisper && b.Variant == name)).ToList(),
        _ => [""],
    };

    /// <summary>Tested build for this machine (dependency list); null for unpinned tools.</summary>
    public static PinnedBuild? TestedBuild(string tool) =>
        IsPinned(tool) ? WantedVariants(tool).Select(v => PinnedFor(tool, v).FirstOrDefault()).FirstOrDefault(b => b is not null) : null;

    /// <summary>Lets go of what GitHub was asked: for when the settings change which builds are wanted.</summary>
    public static void ForgetAnswers()
    {
        lock (Answers)
            Answers.Clear();
    }

    /// <summary>
    /// Target build for Install/Update: pinned build or substitute (<see cref="IsPinned"/>), else newest release. Throws when unreachable or absent.
    /// </summary>
    public static Task<RemoteAsset> GetLatestAsync(string tool, CancellationToken cancellationToken) =>
        IsPinned(tool) ? GetPinnedAsync(tool, cancellationToken) : GetNewestAsync(tool, cancellationToken);

    /// <summary>
    /// Pinned build to install: first still-published entry (GitHub or mirror) of the wanted variant, else the nearest dated release.
    /// Availability uses HEAD on download URLs (not rate-limited); only the off-list search calls the API.
    /// </summary>
    private static async Task<RemoteAsset> GetPinnedAsync(string tool, CancellationToken cancellationToken)
    {
        var variants = WantedVariants(tool);
        var answerKey = $"pinned|{tool}|{string.Join(",", variants)}";
        lock (Answers)
        {
            if (Answers.TryGetValue(answerKey, out var kept) && DateTime.UtcNow - kept.At < AnswerLifetime)
                return kept.Asset;
        }

        var found = await ResolvePinnedAsync(tool, PinnedBuilds, variants, cancellationToken);
        if (found.Pin == PinState.StandIn)
            AppLog.Write($"{DisplayName(tool)}: {found.Label} ({found.Tag}) stands in, because {found.Note}.");

        lock (Answers)
            Answers[answerKey] = (DateTime.UtcNow, found);
        return found;
    }

    /// <summary>First published build across the variants in <paramref name="table"/>, else nearest dated release; throws when neither exists.</summary>
    private static async Task<RemoteAsset> ResolvePinnedAsync(string tool, IReadOnlyList<PinnedBuild> table, IReadOnlyList<string> variants, CancellationToken cancellationToken)
    {
        RemoteAsset? found = null;
        foreach (var variant in variants)
        {
            var builds = table.Where(b => b.Tool == tool && b.Variant == variant).ToList();
            for (var rank = 0; rank < builds.Count && found is null; rank++)
            {
                var build = builds[rank];
                if (IsPassedOver(build.Asset))
                    continue;

                var url = await IsPublishedAsync(build.DownloadUrl, cancellationToken) ? build.DownloadUrl
                    : build.Mirror is { } mirror && await IsPublishedAsync(mirror, cancellationToken) ? mirror
                    : null;
                if (url is null)
                    continue;

                found = new RemoteAsset(build.Asset, url, PinStamp(build), build.Tag, build.Size, build.Sha256, null)
                {
                    Pin = rank == 0 ? PinState.Pinned : PinState.StandIn,
                    PinRank = rank,
                    Variant = variant,
                    Label = build.Label,
                    Note = rank == 0 ? "" : IsPassedOver(builds[0].Asset) ? $"the pinned {builds[0].Label} does not start on this computer"
                        : $"the pinned {builds[0].Label} is no longer published",
                };
            }

            if (found is not null)
                break;
        }

        // Nothing on the list is published any more: the dated build nearest the tested one, of the kind most wanted.
        foreach (var variant in variants)
        {
            if (found is not null)
                break;
            if (table.FirstOrDefault(b => b.Tool == tool && b.Variant == variant) is { } tested)
                found = await FindNearestAsync(tool, variant, tested, cancellationToken);
        }

        if (found is null)
        {
            var tested = table.FirstOrDefault(b => b.Tool == tool && variants.Contains(b.Variant));
            throw new InvalidOperationException($"The {DisplayName(tool)} build this version of HandPeg was tested with ({tested?.Label}) is no longer published, and no build "
                                                + "stands in for it. Settings, General: allowing untested dependency updates installs the newest release instead.");
        }

        return found;
    }

    /// <summary>
    /// Checks availability with a non-redirecting HEAD: 404/410 returns false; other failures throw so a transient error never
    /// triggers a substitute.
    /// </summary>
    private static async Task<bool> IsPublishedAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        request.Headers.Accept.ParseAdd("*/*");
        using var response = await HttpNoRedirect.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        return status switch
        {
            >= 200 and < 400 => true,
            404 or 410 => false,
            _ => throw new InvalidOperationException($"{new Uri(url).Host} answered {status} ({response.ReasonPhrase}) when asked for {Path.GetFileName(new Uri(url).AbsolutePath)}"),
        };
    }

    /// <summary>
    /// Off-list substitute: dated release nearest the tested one with a matching build kind, verified by GitHub's SHA-256; null if none.
    /// </summary>
    private static async Task<RemoteAsset?> FindNearestAsync(string tool, string variant, PinnedBuild tested, CancellationToken cancellationToken)
    {
        var listUrl = $"https://api.github.com/repos/{tested.Repository}/releases?per_page=100";
        using var response = await Http.GetAsync(listUrl, cancellationToken);
        if ((int)response.StatusCode is 403 or 429)
            throw new InvalidOperationException("GitHub's hourly limit for update checks has been reached; try again in a while");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {listUrl}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        RemoteAsset? nearest = null;
        var distance = TimeSpan.MaxValue;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            // The rolling release is the newest build under a name that never changes: not a dated one.
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (tag == "latest" || !release.TryGetProperty("published_at", out var publishedValue) || !publishedValue.TryGetDateTime(out var published))
                continue;

            var away = (published.ToUniversalTime() - tested.Published).Duration();
            if (away >= distance)
                continue;

            var assets = release.GetProperty("assets").EnumerateArray().ToList();
            var sums = assets.FirstOrDefault(a => SumsAssetNames.Contains(a.GetProperty("name").GetString()));
            foreach (var asset in assets)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!IsVariantAsset(tool, variant, name) || IsPassedOver(name))
                    continue;

                // Pre-digest releases use their checksum list; releases with neither are never eligible (unverifiable).
                var digest = asset.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind == JsonValueKind.String ? digestValue.GetString() : null;
                var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsSha256(digest[7..]) ? digest[7..] : null;
                var sumsUrl = sums.ValueKind == JsonValueKind.Object ? sums.GetProperty("browser_download_url").GetString() : null;
                if (sha256 is null && sumsUrl is null)
                    continue;

                nearest = new RemoteAsset(name, asset.GetProperty("browser_download_url").GetString()!, $"{tag}|{name}|{StandInMark}", tag,
                    asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0, sha256, sumsUrl)
                {
                    Pin = PinState.StandIn,
                    Variant = variant,
                    Label = tag,
                    Note = $"the pinned {tested.Label} and the builds listed after it are no longer published, and {tag} is the nearest to it",
                };
                distance = away;
                break;
            }
        }

        return nearest;
    }

    /// <summary>Asks GitHub for the newest build of a tool. Throws when it cannot be reached or has none.</summary>
    private static async Task<RemoteAsset> GetNewestAsync(string tool, CancellationToken cancellationToken)
    {
        var releaseUrl = ReleaseUrl(tool);

        // The same release answers differently when the build for older NVIDIA drivers is wanted from it.
        var olderFfmpeg = tool == Ffmpeg && AppSettings.Current.UseFfmpegForOlderNvidiaDrivers && !IsArm64;
        var answerKey = olderFfmpeg ? releaseUrl + "|older" : releaseUrl;
        lock (Answers)
        {
            if (Answers.TryGetValue(answerKey, out var kept) && DateTime.UtcNow - kept.At < AnswerLifetime)
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

            // Older NVIDIA drivers: newest compatible numbered build, else the standard build (everything but NVENC works).
            var older = olderFfmpeg
                ? assets.Where(a => OlderNvidiaFfmpegVersion(a.GetProperty("name").GetString()) is not null)
                    .OrderByDescending(a => OlderNvidiaFfmpegVersion(a.GetProperty("name").GetString()))
                    .FirstOrDefault()
                : default;
            var isOlder = older.ValueKind == JsonValueKind.Object;
            var asset = isOlder ? older : assets.Where(a => IsAssetFor(tool, a.GetProperty("name").GetString()))
                .OrderBy(a => AssetRank(tool, a.GetProperty("name").GetString())).FirstOrDefault();
            if (asset.ValueKind != JsonValueKind.Object)
                continue;

            var assetName = asset.GetProperty("name").GetString()!;

            // Rolling tag: include the asset timestamp in the stamp; two builds share the release, so the stamp also names the variant.
            var url = asset.GetProperty("browser_download_url").GetString()!;
            var updatedAt = asset.GetProperty("updated_at").GetString();
            // Non-plain whisper builds encode their kind so a plain install is offered the upgrade.
            var stamp = isOlder || (tool == Whisper && assetName != WhisperAssetName) ? $"{tag}|{updatedAt}|{assetName}" : $"{tag}|{updatedAt}";
            var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;

            var digest = asset.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind == JsonValueKind.String
                ? digestValue.GetString()
                : null;
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsSha256(digest[7..]) ? digest[7..] : null;

            var sums = assets.FirstOrDefault(a => SumsAssetNames.Contains(a.GetProperty("name").GetString()));
            var sumsUrl = sums.ValueKind == JsonValueKind.Object ? sums.GetProperty("browser_download_url").GetString() : null;

            var found = new RemoteAsset(assetName, url, stamp, tag, size, sha256, sumsUrl) { Label = tag };
            lock (Answers)
                Answers[answerKey] = (DateTime.UtcNow, found);
            return found;
        }

        throw new InvalidOperationException($"No release at {releaseUrl} has the download for {DisplayName(tool)}.");
    }

    /// <summary>
    /// Silent check whether yt-dlp or FFmpeg is missing or not the target build. Custom-path tools are skipped; failures
    /// (offline, rate-limited) return false.
    /// </summary>
    public static async Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var tool in new[] { YtDlp, Ffmpeg })
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
    /// Downloads, verifies and swaps in <paramref name="asset"/>. Throws with no change on any failure or cancellation.
    /// </summary>
    public static async Task InstallToolAsync(string tool, RemoteAsset asset, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (GetLocalState(tool) == DependencyState.CustomPath)
            return;

        // One install at a time, whoever asks (the dependency list, the background update): two would swap the same folders.
        await Installing.WaitAsync(cancellationToken);
        try
        {
            // Put in by the other one meanwhile: nothing left to do.
            if (GetLocalState(tool) != DependencyState.Missing && !IsOutdated(tool, asset))
                return;

            // A verified build that fails to start is skipped for the next candidate so one bad upstream build cannot block first run.
            for (var tried = 1; ; tried++)
            {
                try
                {
                    await InstallToolNowAsync(tool, asset, progress, cancellationToken);
                    return;
                }
                catch (WontStartException ex) when (asset.Pin != PinState.Unpinned && IsPinned(tool) && tried < MostBuildsTried)
                {
                    lock (WontStartHere)
                        WontStartHere.Add(asset.Name);
                    AppLog.Write($"{DisplayName(tool)} {asset.Label} ({asset.Name}) will not start on this computer; looking for another build", ex);
                    progress.Report(new InstallProgress(-1, $"{asset.Label} does not start on this computer; looking for another build..."));
                    ForgetAnswers();
                    asset = await GetPinnedAsync(tool, cancellationToken);
                }
            }
        }
        finally
        {
            Installing.Release();
        }
    }

    /// <summary>A downloaded tool that checked out but would not start on this computer: another build may.</summary>
    private sealed class WontStartException(string message) : InvalidOperationException(message);

    private static readonly SemaphoreSlim Installing = new(1, 1);

    private static async Task InstallToolNowAsync(string tool, RemoteAsset asset, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {

        var name = DisplayName(tool);
        var staging = Path.Combine(StagingRoot, $"{tool}_{Guid.NewGuid():N}");
        var (download, fresh, parked) = (Path.Combine(staging, "download"), Path.Combine(staging, "new"), Path.Combine(staging, "old"));
        try
        {
            Directory.CreateDirectory(fresh);

            var hash = await DownloadAsync(asset.DownloadUrl, download, name, progress, cancellationToken);
            // Fail closed: a download whose release publishes no SHA-256 (no GitHub digest, no checksum file) is not installed, whatever
            // its source (a custom release address, an unpinned build, a stand-in), because HandPeg runs what it installs.
            var expected = asset.Sha256 ?? await FindInSumsAsync(asset, cancellationToken)
                           ?? throw new InvalidDataException(asset.Pin == PinState.StandIn
                               ? $"No checksum is published for {asset.Name}, so it cannot stand in for the tested {name}."
                               : $"No checksum is published for {asset.Name}, so {name} was not installed from it.");
            Verify(name, hash, expected);

            progress.Report(new InstallProgress(1, $"Unpacking {name}..."));
            await Task.Run(() => Unpack(tool, download, fresh, cancellationToken), cancellationToken);

            // The stamp travels with the files, so the two can never disagree about what is installed.
            File.WriteAllText(StampPath(fresh, tool), asset.Stamp);

            progress.Report(new InstallProgress(1, $"Testing {name}..."));
            File.WriteAllText(VersionPath(fresh), await ProbeVersionAsync(tool, fresh, cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            Swap(fresh, ToolFolder(tool), parked, name);
            var kind = asset.Pin switch
            {
                PinState.Pinned => "the pinned build",
                PinState.StandIn => $"a stand-in: {asset.Note}",
                _ => "the newest release",
            };
            AppLog.Write($"Installed {name} {asset.Label} {asset.Name}, {kind} (SHA-256 verified).");
            RaiseInstalled(tool);
        }
        finally
        {
            DeleteFolder(staging);
        }
    }

    /// <summary>
    /// Raised with the tool name after any install or replacement (first run, Settings, feature prompts, background update), on the
    /// installing thread (subscribers marshal). Handler exceptions are logged and never fail the install.
    /// </summary>
    public static event Action<string>? Installed;

    private static void RaiseInstalled(string tool)
    {
        foreach (var handler in Installed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<string>)handler)(tool);
            }
            catch (Exception ex)
            {
                AppLog.Write($"A handler of the {tool} install failed", ex);
            }
        }
    }

    /// <summary>
    /// True when FFmpeg is installed (preview decodes via Media Foundation; probing, waveforms, filmstrips and export need FFmpeg).
    /// </summary>
    public static bool EnginesInstalled => File.Exists(FfmpegPath);

    // ----- Retired libmpv player cleanup -----

    // Legacy libmpv install folders ("mpv" ToolFolder, x64 and ARM64) and the cache folder of its gap-filler clips.
    private static readonly string[] RetiredPlayerFolders = ["mpv", $"mpv-{Arm64Variant}"];
    private const string RetiredClockFolder = "clock";

    /// <summary>
    /// Deletes the three legacy libmpv folders directly under <see cref="AppPaths.Deps"/> and <see cref="AppPaths.Cache"/>, never
    /// through a reparse point, logging each. Idle work after Frame 1; folders in use are retried on a later start.
    /// </summary>
    public static void DeleteRetiredPlayer()
    {
        foreach (var name in RetiredPlayerFolders)
            DeleteRetired(new DirectoryInfo(Path.Combine(AppPaths.Deps, name)), "the libmpv player's old folder");
        DeleteRetired(new DirectoryInfo(Path.Combine(AppPaths.Cache, RetiredClockFolder)), "the libmpv player's old stand-in clocks");
    }

    private static void DeleteRetired(DirectoryInfo folder, string what)
    {
        var said = char.ToUpperInvariant(what[0]) + what[1..];
        try
        {
            if (!folder.Exists)
                return;
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                AppLog.Write($"{said} {folder.FullName} is a link: left alone.");
                return;
            }

            var (files, bytes) = (0, 0L);
            foreach (var file in folder.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                (files, bytes) = (files + 1, bytes + file.Length);
            folder.Delete(recursive: true);
            AppLog.Write($"Removed {what} {folder.FullName} ({files} files, {PreviewCache.Describe(bytes)}): HandPeg no longer uses it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"{said} {folder.FullName} could not be removed yet", ex);
        }
    }

    /// <summary>
    /// Swaps the new folder into place: renames the old one aside first and restores it on failure; an in-use old folder aborts with no change.
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

    private static void Unpack(string tool, string download, string folder, CancellationToken cancellationToken)
    {
        if (tool == YtDlp)
        {
            // Not an archive: the download is the program.
            File.Move(download, Path.Combine(folder, "yt-dlp.exe"));
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

        // whisper.cpp: install the CLI as whisper.exe plus whisper.dll, ggml DLLs and, on ARM64, libomp140.aarch64.dll (imported by
        // ggml-base/ggml-cpu and absent from Windows; without it startup fails with 0xC0000135). Other archive binaries are skipped.
        // The CLI is whisper-cli.exe in current builds; main.exe in older ones (now only a stub).
        var program = archive.Entries.FirstOrDefault(e => e.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
                      ?? archive.Entries.FirstOrDefault(e => e.Name.Equals("main.exe", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException("The whisper.cpp archive did not contain whisper-cli.exe.");
        program.ExtractToFile(Path.Combine(folder, "whisper.exe"));

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isLibrary = entry.Name.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase)
                            || ((entry.Name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith("libomp", StringComparison.OrdinalIgnoreCase))
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
            _ when model.Contains("large-v3-turbo", StringComparison.Ordinal) => 1549,
            _ when model.Contains("large", StringComparison.Ordinal) => 2952,
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

            // Pinned model SHA-256 (not from the download server), else Hugging Face's linked hash; with neither, nothing is installed.
            var expected = (ModelSha256.TryGetValue(model, out var pinned) ? pinned : await FindLinkedHashAsync(url, cancellationToken))
                           ?? throw new InvalidDataException($"No checksum is known for the model {model}, so it was not installed.");
            var hash = await DownloadAsync(url, download, model, progress, cancellationToken);
            Verify(model, hash, expected);

            Directory.CreateDirectory(ModelsFolder);
            File.Move(download, GetWhisperModelPath(model), overwrite: true);
            AppLog.Write($"Installed the model {model} (SHA-256 verified).");
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
            AppLog.Write($"Speech model {model}: the download failed", ex);
            return $"The model {model} could not be downloaded: {ex.Message}";
        }

        return $"Model {model} downloaded ({new FileInfo(GetWhisperModelPath(model)).Length / 1048576.0:0} MB).";
    }

    /// <summary>True for non-cancellation install failures: network, disk, or verification.</summary>
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

    /// <summary>Throws unless <paramref name="actual"/> matches the expected SHA-256; a missing checksum also fails (fail closed).</summary>
    internal static void Verify(string label, string actual, string? expected)
    {
        if (expected is null)
            throw new InvalidDataException($"No checksum is known for {label}, so it was not installed.");
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The download of {label} does not match its checksum, and was thrown away.");
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
    /// SHA-256 of a Hugging Face LFS file, read from the redirect response headers. Null when absent or unreachable (the caller
    /// then refuses to install).
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

    // Response-header timeout; bodies then stream without limit. HttpClient's own timeout is only a backstop for stalled buffered bodies.
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(60);

    private static HttpClient CreateClient(bool followRedirects)
    {
        var client = new HttpClient(new AnswerDeadline(AnswerWithin) { InnerHandler = new HttpClientHandler { AllowAutoRedirect = followRedirects } })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        // GitHub's API rejects requests without a User-Agent with 403.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HandPeg/1.0");
        if (followRedirects)
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Header timeout raises <see cref="HttpRequestException"/> (inner <see cref="TimeoutException"/>), treated as an install failure
    /// (<see cref="IsInstallFailure"/>), unlike HttpClient's timeout whose <see cref="TaskCanceledException"/> reads as user cancellation.
    /// </summary>
    internal sealed class AnswerDeadline(TimeSpan limit) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(limit);
            try
            {
                return await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException($"{request.RequestUri?.Host} did not answer within {limit.TotalSeconds:0} s.", new TimeoutException(ex.Message, ex));
            }
        }
    }
}
