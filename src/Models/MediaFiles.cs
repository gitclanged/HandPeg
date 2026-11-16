using System.IO;

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

/// <summary>The media files HandPeg reads, by extension: what the media browser lists and what a drop or Project Media takes.</summary>
public static class MediaFiles
{
    public static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".m2ts", ".mts", ".wmv", ".flv", ".mpg", ".mpeg"];
    public static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma"];
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>A file's kind by its extension; null for one the browser does not list.</summary>
    public static MediaBrowserKind? KindOf(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return VideoExtensions.Contains(extension) ? MediaBrowserKind.Video
            : AudioExtensions.Contains(extension) ? MediaBrowserKind.Audio
            : ImageExtensions.Contains(extension) ? MediaBrowserKind.Image
            : null;
    }
}
