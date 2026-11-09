using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>What an entry of the media browser is.</summary>
public enum MediaBrowserKind
{
    Drive,
    Folder,
    Video,
    Audio,
    Image,
}

/// <summary>
/// One entry of the media browser pane: a drive, a folder, or a media file. A video's tile shows one of its frames,
/// and, while the pointer moves across it, the frame at that point of the video (from a strip of a few frames,
/// made once in the background and kept in the browser's cache).
/// </summary>
public sealed partial class MediaBrowserItem : ObservableObject
{
    public static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".m2ts", ".mts", ".wmv", ".flv", ".mpg", ".mpeg"];
    public static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma"];
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    public MediaBrowserItem(string path, MediaBrowserKind kind, string name)
    {
        (Path, Kind, Name) = (path, kind, name);
    }

    /// <summary>A file's kind by its extension; null for one the browser does not list.</summary>
    public static MediaBrowserKind? KindOf(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return VideoExtensions.Contains(extension) ? MediaBrowserKind.Video
            : AudioExtensions.Contains(extension) ? MediaBrowserKind.Audio
            : ImageExtensions.Contains(extension) ? MediaBrowserKind.Image
            : null;
    }

    public string Path { get; }
    public MediaBrowserKind Kind { get; }
    public string Name { get; }

    /// <summary>The file's size in bytes (0 for a folder), and when it was last changed: what the list can be sorted by.</summary>
    public long Bytes { get; init; }

    public DateTime Modified { get; init; }

    public bool IsFile => Kind is MediaBrowserKind.Video or MediaBrowserKind.Audio or MediaBrowserKind.Image;
    public bool IsVideo => Kind == MediaBrowserKind.Video;

    /// <summary>The icon shown while there is no picture (Segoe MDL2): a drive, a folder, a video, a note, a picture.</summary>
    public string Glyph => Kind switch
    {
        MediaBrowserKind.Drive => "",
        MediaBrowserKind.Folder => "",
        MediaBrowserKind.Audio => "",
        MediaBrowserKind.Image => "",
        _ => "",
    };

    /// <summary>The second line of the entry: a video's length and size, a file's size, a folder's nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaLine), nameof(DurationText))]
    private string _info = "";

    /// <summary>How long the file plays, in seconds; 0 while not known.</summary>
    public double Seconds { get; set; }

    /// <summary>A video's width over its height, which tells its frames apart in a strip; 0 while not known.</summary>
    public double Shape { get; set; }

    /// <summary>The picture of the entry when the pointer is not over it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shown))]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private ImageSource? _poster;

    /// <summary>The frame shown while the pointer is over the tile: the one at that point of the video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shown))]
    private ImageSource? _hoverFrame;

    /// <summary>What the tile shows: the frame under the pointer, or the poster.</summary>
    public ImageSource? Shown => HoverFrame ?? Poster;

    public bool HasPicture => Poster is not null;

    /// <summary>How far across the tile the pointer is, 0 to 1, while it is over a video; NaN otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScrubbing))]
    private double _hoverFraction = double.NaN;

    public bool IsScrubbing => !double.IsNaN(HoverFraction);

    /// <summary>The moment under the pointer, written out: "0:12 / 0:58".</summary>
    [ObservableProperty] private string _hoverText = "";

    /// <summary>The strip of frames has been asked for (made or found), so it is not asked for again this listing.</summary>
    public bool PreviewRequested { get; set; }

    /// <summary>Where the strip of frames is on disk, once it has been made; and how many frames it holds.</summary>
    public string? StripPath { get; set; }

    public int StripFrames { get; set; }

    // ----- Its details (5.4C): the List view's columns and the Filmstrip view's second line -----
    // Read once the entry is in sight in either view (MediaBrowserFrames.DetailsAsync, kept in the browser's cache), never before.

    /// <summary>The details have been asked for this listing (made or found), so they are not asked for again.</summary>
    public bool DetailsRequested { get; set; }

    /// <summary>Whether the details are known (read, or found unreadable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolutionText), nameof(FrameRateText), nameof(DurationText), nameof(CodecText), nameof(AudioText), nameof(MetaLine))]
    private bool _detailsKnown;

    /// <summary>The picture's width and height in pixels; 0 for a sound, or while not known.</summary>
    public int VideoWidth { get; private set; }

    public int VideoHeight { get; private set; }

    /// <summary>Frames a second; 0 while not known.</summary>
    public double FrameRate { get; private set; }

    /// <summary>The video's codec as FFmpeg names it (h264, hevc, av1...), or a sound's when it has no picture; "" while not known.</summary>
    public string Codec { get; private set; } = "";

    /// <summary>How many sound streams the file has; -1 while not known.</summary>
    public int AudioStreams { get; private set; } = -1;

    /// <summary>Takes the details read for the file (its length too, when the entry did not have it yet).</summary>
    public void TakeDetails(double seconds, int width, int height, double frameRate, string codec, int audioStreams)
    {
        (VideoWidth, VideoHeight, FrameRate, Codec, AudioStreams) = (Math.Max(width, 0), Math.Max(height, 0), double.IsFinite(frameRate) ? Math.Max(frameRate, 0) : 0, codec, audioStreams);
        if (Seconds <= 0 && seconds > 0)
            Seconds = seconds;
        if (Shape <= 0 && width > 0 && height > 0)
            Shape = (double)width / height;
        if (DetailsKnown)
            OnPropertyChanged(nameof(MetaLine));
        DetailsKnown = true;
    }

    /// <summary>"1920x1080"; "" for none.</summary>
    public string ResolutionText => FormatResolution(VideoWidth, VideoHeight);

    /// <summary>"59.94 fps", "360 fps"; "" for none.</summary>
    public string FrameRateText => FormatFrameRate(FrameRate);

    /// <summary>"2:03"; "" while not known.</summary>
    public string DurationText => Seconds > 0 ? FormatSeconds(Seconds) : "";

    /// <summary>"HEVC", "H.264"; "" while not known.</summary>
    public string CodecText => FormatCodec(Codec);

    /// <summary>"2" sound streams, "None"; "" while not known.</summary>
    public string AudioText => FormatAudio(AudioStreams);

    /// <summary>The file's size: "12.4 MB"; "" for a folder.</summary>
    public string SizeText => IsFile && Bytes > 0 ? FormatSize(Bytes) : "";

    /// <summary>The Filmstrip view's second line: "1920x1080 • 60 fps • 2:03" (what is known of it), else what <see cref="Info"/> says.</summary>
    public string MetaLine => JoinMeta(ResolutionText, FrameRateText, DurationText) is { Length: > 0 } line ? line : Info;

    /// <summary>The parts of a second line that are known, with a bullet between each two.</summary>
    public static string JoinMeta(params string[] parts) => string.Join(" • ", parts.Where(p => p.Length > 0));

    public static string FormatResolution(int width, int height) =>
        width > 0 && height > 0 ? string.Create(CultureInfo.InvariantCulture, $"{width}x{height}") : "";

    /// <summary>A rate to two places at most, whole rates without them: "60 fps", "59.94 fps", "23.98 fps".</summary>
    public static string FormatFrameRate(double fps) =>
        fps > 0 && double.IsFinite(fps)
            ? Math.Abs(fps - Math.Round(fps)) < 0.005
                ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(fps):0} fps")
                : string.Create(CultureInfo.InvariantCulture, $"{fps:0.##} fps")
            : "";

    /// <summary>A codec as people write it: H.264, HEVC, AV1, VP9, ProRes; any other in capitals.</summary>
    public static string FormatCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "" => "",
        "h264" or "avc" or "avc1" => "H.264",
        "hevc" or "h265" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "prores" => "ProRes",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4",
        "mjpeg" => "MJPEG",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>How many sound streams: "None", "1", "2"...; "" while not known.</summary>
    public static string FormatAudio(int streams) => streams < 0 ? "" : streams == 0 ? "None" : streams.ToString(CultureInfo.InvariantCulture);

    /// <summary>"1:02:03" or "2:03".</summary>
    public static string FormatSeconds(double seconds)
    {
        var whole = (long)Math.Max(Math.Floor(seconds), 0);
        return whole >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }

    /// <summary>"12.4 MB".</summary>
    public static string FormatSize(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB")
        : bytes >= 1L << 20
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(bytes / 1024, 1)} KB");

    /// <summary>The file's size, for its second line until its length is known.</summary>
    public void DescribeFile()
    {
        try
        {
            Info = FormatSize(new FileInfo(Path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
