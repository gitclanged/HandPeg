using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>Batch queue encode job holding its final command line, isolated from later settings or source changes.</summary>
public sealed partial class QueueJob(
    string source, string target, string command, TimeSpan expectedDuration, ProjectState state)
    : ObservableObject
{
    /// <summary>Source window state, for loading back into the editor.</summary>
    public ProjectState State { get; } = state;

    public const string Pending = "Pending";

    /// <summary>Transcribing captions (pre-encode step for jobs that need it).</summary>
    public const string Transcribing = "Transcribing";
    public const string Encoding = "Encoding";
    public const string Complete = "Complete";
    public const string Failed = "Failed";

    public string Source { get; } = source;
    public string Target { get; } = target;
    /// <summary>Rewritten only by the hardware fallback when retrying on a software encoder.</summary>
    [ObservableProperty] private string _command = command;

    /// <summary>Output duration, for converting FFmpeg timestamps to percent.</summary>
    public TimeSpan ExpectedDuration { get; } = expectedDuration;

    /// <summary>Output duration as displayed in the queue.</summary>
    public string LengthText => TimeDisplay.Format(ExpectedDuration.TotalSeconds);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private string _status = Pending;

    public bool IsPending => Status == Pending;

    [ObservableProperty] private double _progress;

    /// <summary>Failure reason, shown as the row tooltip.</summary>
    [ObservableProperty] private string _detail = "";

    public bool CanRemove => Status is not (Encoding or Transcribing);

    /// <summary>
    /// Pending caption transcription with queue-time whisper settings; null when no captions or already written (subtitle-track cues).
    /// </summary>
    public CaptionJob? Captions { get; init; }

    /// <summary>Whether transcription precedes the encode.</summary>
    public bool GenerateCaptions => Captions is not null;

    /// <summary>Caption file already written; reruns (after cancel or software fallback) skip transcription.</summary>
    public bool CaptionsReady { get; set; }

    /// <summary>
    /// Snapshot Sequence with per-job captions, cuts, chapters and voiceover, rendered by the GPU export (sole renderer; a hand-edited
    /// <see cref="Command"/> keeps its own arguments). Null for hand-edited commands; a fallback-rewritten command no longer matches it
    /// and uses FFmpeg's graph. In-memory only, like the queue.
    /// </summary>
    public SequenceExportState? ExportState { get; init; }
}
