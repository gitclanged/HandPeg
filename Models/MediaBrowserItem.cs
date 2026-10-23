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
    [ObservableProperty] private string _info = "";

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
