using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;

namespace HandPegApp.Views;

/// <summary>
/// Media browser entry: a drive, folder or media file. A video tile shows a poster frame and, on hover, the frame at the pointer's
/// horizontal position (from a short frame strip built once in the background and kept in the browser cache).
/// </summary>
public sealed partial class MediaBrowserItem : ObservableObject
{
    public MediaBrowserItem(string path, MediaBrowserKind kind, string name)
    {
        (Path, Kind, Name) = (path, kind, name);
    }

    public string Path { get; }
    public MediaBrowserKind Kind { get; }
    public string Name { get; }

    /// <summary>File size in bytes (0 for folders); with <see cref="Modified"/>, a list sort key.</summary>
    public long Bytes { get; init; }

    public DateTime Modified { get; init; }

    public bool IsFile => Kind is MediaBrowserKind.Video or MediaBrowserKind.Audio or MediaBrowserKind.Image;
    public bool IsVideo => Kind == MediaBrowserKind.Video;

    /// <summary>Placeholder glyph (Segoe MDL2) shown until a picture loads: drive, folder, audio, photo or video.</summary>
    public string Glyph => Kind switch
    {
        MediaBrowserKind.Drive => Glyphs.HardDrive,
        MediaBrowserKind.Folder => Glyphs.Folder,
        MediaBrowserKind.Audio => Glyphs.Audio,
        MediaBrowserKind.Image => Glyphs.Photo,
        _ => Glyphs.Video,
    };

    /// <summary>Entry subtitle: duration and size for video, size for other files, empty for folders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaLine), nameof(DurationText))]
    private string _info = "";

    /// <summary>Duration in seconds; 0 while unknown.</summary>
    public double Seconds { get; set; }

    /// <summary>Video aspect ratio (width / height), used to slice strip frames; 0 while unknown.</summary>
    public double Shape { get; set; }

    /// <summary>Resting picture of the entry (shown when not hovered).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shown))]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private ImageSource? _poster;

    /// <summary>Hover frame at the pointer's position across the tile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shown))]
    private ImageSource? _hoverFrame;

    /// <summary>Displayed image: the hover frame when present, else the poster.</summary>
    public ImageSource? Shown => HoverFrame ?? Poster;

    public bool HasPicture => Poster is not null;

    /// <summary>Pointer position across a hovered video tile (0..1); NaN when not hovering.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScrubbing))]
    private double _hoverFraction = double.NaN;

    public bool IsScrubbing => !double.IsNaN(HoverFraction);

    /// <summary>Hover time label, e.g. "0:12 / 0:58".</summary>
    [ObservableProperty] private string _hoverText = "";

    /// <summary>Frame strip already requested (built or found) for this listing; prevents re-requests.</summary>
    public bool PreviewRequested { get; set; }

    /// <summary>On-disk frame strip path once built; <see cref="StripFrames"/> holds its frame count.</summary>
    public string? StripPath { get; set; }

    public int StripFrames { get; set; }

    // ----- Media details (List view columns, Filmstrip view subtitle) -----
    // Read lazily once the entry is visible in either view (MediaBrowserFrames.DetailsAsync, cached by the browser).

    /// <summary>Details already requested for this listing; prevents re-requests.</summary>
    public bool DetailsRequested { get; set; }

    /// <summary>True once details are resolved (read, or found unreadable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolutionText), nameof(FrameRateText), nameof(DurationText), nameof(CodecText), nameof(AudioText), nameof(MetaLine))]
    private bool _detailsKnown;

    /// <summary>Picture width in pixels (with <see cref="VideoHeight"/>); 0 for audio or while unknown.</summary>
    public int VideoWidth { get; private set; }

    public int VideoHeight { get; private set; }

    /// <summary>Frames per second; 0 while unknown.</summary>
    public double FrameRate { get; private set; }

    /// <summary>FFmpeg codec name (h264, hevc, av1...) of the video, or of the audio when there is no picture; "" while unknown.</summary>
    public string Codec { get; private set; } = "";

    /// <summary>Audio stream count; -1 while unknown.</summary>
    public int AudioStreams { get; private set; } = -1;

    /// <summary>Applies read details; also fills duration and aspect ratio when not yet known.</summary>
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

    /// <summary>Audio stream count text ("2", "None"); "" while unknown.</summary>
    public string AudioText => FormatAudio(AudioStreams);

    /// <summary>File size text, e.g. "12.4 MB"; "" for folders.</summary>
    public string SizeText => IsFile && Bytes > 0 ? FormatSize(Bytes) : "";

    /// <summary>Filmstrip subtitle from known parts ("1920x1080 • 60 fps • 2:03"), else <see cref="Info"/>.</summary>
    public string MetaLine => JoinMeta(ResolutionText, FrameRateText, DurationText) is { Length: > 0 } line ? line : Info;

    /// <summary>Joins non-empty parts with " • ".</summary>
    public static string JoinMeta(params string[] parts) => string.Join(" • ", parts.Where(p => p.Length > 0));

    public static string FormatResolution(int width, int height) =>
        width > 0 && height > 0 ? string.Create(CultureInfo.InvariantCulture, $"{width}x{height}") : "";

    /// <summary>Frame rate with up to two decimals, integers without: "60 fps", "59.94 fps", "23.98 fps".</summary>
    public static string FormatFrameRate(double fps) =>
        fps > 0 && double.IsFinite(fps)
            ? Math.Abs(fps - Math.Round(fps)) < 0.005
                ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(fps):0} fps")
                : string.Create(CultureInfo.InvariantCulture, $"{fps:0.##} fps")
            : "";

    /// <summary>Display codec name (H.264, HEVC, AV1, VP9, ProRes...); unknown names upper-cased.</summary>
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

    /// <summary>Audio stream count text: "None", "1", "2"...; "" when negative (unknown).</summary>
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

}
