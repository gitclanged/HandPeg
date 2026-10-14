using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp.Models;

/// <summary>
/// An encode waiting in the batch queue. It holds the finished command line, so later
/// changes to the settings or to the loaded source do not affect it.
/// </summary>
public sealed partial class QueueJob(
    string source, string target, int segmentCount, string command, TimeSpan expectedDuration, ProjectState state)
    : ObservableObject
{
    /// <summary>The window state the job was made from, so it can be loaded back for editing.</summary>
    public ProjectState State { get; } = state;

    public const string Pending = "Pending";

    /// <summary>The job's captions are being transcribed: the step before its encode, for a job that has any to make.</summary>
    public const string Transcribing = "Transcribing";
    public const string Encoding = "Encoding";
    public const string Complete = "Complete";
    public const string Failed = "Failed";

    public string Source { get; } = source;
    public string Target { get; } = target;
    public int SegmentCount { get; } = segmentCount;
    /// <summary>Rewritten only by the hardware fallback, when the job is retried on a software encoder.</summary>
    [ObservableProperty] private string _command = command;

    /// <summary>Length of the output, used to turn FFmpeg's timestamps into a percentage.</summary>
    public TimeSpan ExpectedDuration { get; } = expectedDuration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private string _status = Pending;

    public bool IsPending => Status == Pending;

    [ObservableProperty] private double _progress;

    /// <summary>Why the job failed, shown as a tooltip on its row.</summary>
    [ObservableProperty] private string _detail = "";

    public bool CanRemove => Status is not (Encoding or Transcribing);

    /// <summary>
    /// The captions the job still has to make, with whisper's settings as they were when it was queued; null
    /// when it draws none, or draws ones that were already written (a subtitle track's cues).
    /// </summary>
    public CaptionJob? Captions { get; init; }

    /// <summary>Whether the job transcribes before it encodes.</summary>
    public bool GenerateCaptions => Captions is not null;

    /// <summary>The caption file has been written: a job that is run again (after a cancel, or on the software encoder) does not transcribe twice.</summary>
    public bool CaptionsReady { get; set; }
}
