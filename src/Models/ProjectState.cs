namespace HandPegApp.Models;

/// <summary>
/// Everything needed to bring the main window back to a given state: the source, the timeline and all
/// settings. Saved as a project file (format 5), and kept with each queued job so the job can be edited later.
/// </summary>
public sealed class ProjectState
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }

    // Source and destination
    public string SourcePath { get; set; } = "";

    /// <summary>The file actually read. Differs from the source for a URL, where it is the temporary download.</summary>
    public string LocalMediaPath { get; set; } = "";

    public string DestinationPath { get; set; } = "";
    public string DownloadResolution { get; set; } = "Best";
    public bool DownloadSubtitles { get; set; }

    /// <summary>
    /// The timeline (4.3C): every track and clip, the main video's included, its markers and the Sequence Output's
    /// effects. Always a copy of its own (<see cref="Timeline.TimelineDocument.Clone"/>): what holds a state (a queued
    /// job, an autosave being written) is never reached by later edits.
    /// </summary>
    public Timeline.TimelineDocument Timeline { get; set; } = new();

    public bool SnapToIFrames { get; set; }

    // Settings
    public string Container { get; set; } = "mp4";
    public bool WebOptimized { get; set; }
    public bool ChapterMarkers { get; set; } = true;
    public bool ChaptersAtCuts { get; set; }
    public bool ChaptersAtMarkers { get; set; }

    /// <summary>The imported or hand-made subtitle track; null when there is none.</summary>
    public SubtitleTrackData? Subtitles { get; set; }

    /// <summary>Kept here as well as in the settings, because a preset only carries it when the user opted in.</summary>
    public string TargetFileSize { get; set; } = "";

    /// <summary>The Dimensions, Filters, Video and Audio tabs, in the same shape as a preset.</summary>
    public EncodingPreset Settings { get; set; } = new();

    public List<AudioTrackState> AudioTracks { get; set; } = [];
    public List<SubtitleTrackState> SubtitleTracks { get; set; } = [];

    /// <summary>The audio file auto-captions listen to instead of the video's own track; blank for the video.</summary>
    public string CaptionAudioPath { get; set; } = "";

    /// <summary>
    /// Whether captions listen to that file rather than to one of the video's tracks. Null in projects saved
    /// before there was a choice: those used the file whenever one was named.
    /// </summary>
    public bool? CaptionUseExternalAudio { get; set; }

    /// <summary>Which of the video's audio tracks captions listen to.</summary>
    public int CaptionAudioTrackIndex { get; set; }

    /// <summary>The recycle bin: clips deleted from the timeline, kept so that they can be put back.</summary>
    public List<DeletedClip> Deleted { get; set; } = [];

    /// <summary>The command text when it had been edited by hand; null when it was the generated one.</summary>
    public string? ManualCommand { get; set; }
}

public sealed record AudioTrackState(
    int Index, string Action, string Codec, string Bitrate, string? Title = null, double GainDb = 0, TrackAudioFilters? Filters = null,
    double Offset = 0, List<AudioPiece>? Pieces = null, bool AutoDuck = false, bool IsVoice = false);

public sealed record SubtitleTrackState(int Index, string Action);
