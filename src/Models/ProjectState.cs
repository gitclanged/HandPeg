namespace HandPegApp.Models;

/// <summary>
/// Complete restorable editor state (source, timeline, settings); serialized as a project file (format 6) and stored with queued jobs.
/// </summary>
public sealed class ProjectState
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }

    // Source and destination
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// Last opened local file (the download for a URL source), supplying Info, Streams and default output name; rendered only via its
    /// timeline clips.
    /// </summary>
    public string LocalMediaPath { get; set; } = "";

    public string DestinationPath { get; set; } = "";
    public string DownloadResolution { get; set; } = "Best";
    public bool DownloadSubtitles { get; set; }

    /// <summary>
    /// Timeline: all tracks, clips, markers and Sequence Output effects. Always an independent clone
    /// (<see cref="Timeline.TimelineDocument.Clone"/>) so queued jobs and in-flight autosaves are isolated from later edits.
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

    /// <summary>Output audio tracks by stream index (legacy files also carry main-video mix data, see <see cref="AudioTrackState"/>).</summary>
    public List<AudioTrackState> AudioTracks { get; set; } = [];

    public List<SubtitleTrackState> SubtitleTracks { get; set; } = [];

    /// <summary>The audio file auto-captions listen to instead of the video's own track; blank for the video.</summary>
    public string CaptionAudioPath { get; set; } = "";

    /// <summary>
    /// True when captions transcribe that file instead of a video track. Null in legacy projects (file used whenever named).
    /// </summary>
    public bool? CaptionUseExternalAudio { get; set; }

    /// <summary>Which of the video's audio tracks captions listen to.</summary>
    public int CaptionAudioTrackIndex { get; set; }

    /// <summary>Recycle bin of deleted timeline clips for restoration.</summary>
    public List<DeletedClip> Deleted { get; set; } = [];

    /// <summary>The command text when it had been edited by hand; null when it was the generated one.</summary>
    public string? ManualCommand { get; set; }

    /// <summary>
    /// Undo history, oldest first, max 20 packed steps. Set only on save (MainViewModel.CaptureStateForSave) and read only on open; null
    /// for queued jobs, the unsaved-changes fingerprint and project listings, so never compared.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<UndoStepState>? UndoHistory { get; set; }

    /// <summary>Shallow copy sharing timeline and lists, for replacing (not mutating) parts (VodTrim.Remap).</summary>
    internal ProjectState ShallowCopy() => (ProjectState)MemberwiseClone();

    /// <summary>The redo history, as <see cref="UndoHistory"/> is kept: the step Redo puts back first is the last.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<UndoStepState>? RedoHistory { get; set; }

    /// <summary>
    /// Small JPEG of the Sequence at the playhead captured on save, shown by Project Manager, the launch window and Explorer
    /// (ProjectThumbnail). Set only on save (MainViewModel.CaptureStateForSave); null for queued jobs and the fingerprint (JsonIgnore).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? ThumbnailJpeg { get; set; }

    /// <summary>
    /// Window arrangement at save: dock layout, Media Browser view, 3D pose. Set on save (MainViewModel.WorkspaceForSave), read on open;
    /// excluded from the fingerprint (JsonIgnore) so rearranging never dirties the project.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ProjectWorkspace? Workspace { get; set; }
}

/// <summary>Project "workspace" section, applied on open when <c>AppSettings.RestoreProjectWorkspaceOnOpen</c> is on.</summary>
public sealed class ProjectWorkspace
{
    /// <summary>The dock layout (DockManager's JSON); null for none.</summary>
    public string? LayoutJson { get; set; }

    /// <summary>The Media Browser's view (its MediaView name); null for none.</summary>
    public string? MediaView { get; set; }

    public bool ExplodedOpen { get; set; }
    public double ExplodedYaw { get; set; }
    public double ExplodedPitch { get; set; }
    public double ExplodedZoom { get; set; }
    public double ExplodedSpacing { get; set; }
}

/// <summary>Serialized undo/redo step: label (e.g. "delete Logo") and packed timeline snapshot.</summary>
public sealed record UndoStepState(string What, byte[] State);

/// <summary>
/// Serialized output audio track (<see cref="AudioTrack"/>): stream index, action, codec, bitrate, title. Remaining fields (gain,
/// filters, slip, silenced ranges, ducking, voice) are legacy main-video mix data, consumed only by the LegacyMainVideo Sub-Lane
/// migration and neutral in newer files; round-tripped unchanged.
/// </summary>
public sealed record AudioTrackState(
    int Index, string Action, string Codec, string Bitrate, string? Title = null, double GainDb = 0, TrackAudioFilters? Filters = null,
    double Offset = 0, List<AudioPiece>? Pieces = null, bool AutoDuck = false, bool IsVoice = false);

public sealed record SubtitleTrackState(int Index, string Action);
