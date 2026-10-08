using CommunityToolkit.Mvvm.ComponentModel;

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

    public bool CanRemove => Status != Encoding;
}
