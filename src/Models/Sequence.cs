using HandPegApp.Models.Timeline;

namespace HandPegApp.Models;

// MainClip and MainCrossfade are the graph builder's: the primary chain as it lowers it (FilterGraphBuilder.Timeline.cs),
// read by the player's sequence and AvSync. The view model's timeline is a TimelineDocument (4.3C).

/// <summary>
/// One clip of the main video as it lies on the sequence: where it starts and ends there, and how far into the
/// file it begins, all in seconds. Index 0 is the row's own clip; the others follow in the order they were made.
/// Its fades in and out (seconds) and their styles come with it.
/// </summary>
public readonly record struct MainClip(int Index, double Start, double End, double Offset)
{
    public double FadeIn { get; init; }
    public double FadeOut { get; init; }
    public TransitionStyle FadeInStyle { get; init; }
    public TransitionStyle FadeOutStyle { get; init; }

    /// <summary>A crossfade from the clip of the main video that ends where this one starts, in seconds (0: a plain cut).</summary>
    public double TransitionIn { get; init; }

    /// <summary>The gain of its sound, in decibels: its clip's and its track's (4.3B, lowered from a TimelineClip; 0 for 4.2.0's clips).</summary>
    public double GainDb { get; init; }
}

/// <summary>
/// A crossfade between two clips of the main video, as a graph lays it: the later clip's picture from
/// <paramref name="From"/> to <paramref name="To"/> on the sequence (its file from <paramref name="Source"/>), coming up
/// over the earlier one, which plays on under it to <paramref name="To"/>.
/// </summary>
public readonly record struct MainCrossfade(double From, double To, double Source);

/// <summary>
/// A clip that was deleted from the timeline and waits in the recycle bin (4.3C): the clip as it was, and the track it
/// was on (its id, its kind, and where it was in the stacking order), so that it can go back there, or onto a new
/// track of its kind at that place when its own has gone or has no room for it.
/// </summary>
public sealed record DeletedClip(TimelineClip Clip, Guid TrackId, TimelineTrackKind TrackKind, int TrackIndex)
{
    /// <summary>
    /// What went with it (4.4): its children that the delete took off the timeline too (and theirs), each kept the same way,
    /// to go back with it; null for none, and so left out of the project file.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeletedClip>? Children { get; init; }

    /// <summary>
    /// The track its track lay under (4.4, TimelineTrackModel.ParentTrackId), so that a new track made for it lies under the
    /// same one again; null for none, and so left out of the project file.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TrackParentId { get; init; }
}

/// <summary>A deleted clip as the recycle bin's drawer lists it.</summary>
public sealed class BinItem(DeletedClip clip, string name, string timeText, System.Windows.Media.ImageSource? preview)
{
    public DeletedClip Clip { get; } = clip;

    public string Name { get; } = name;

    /// <summary>When it was on the timeline, from where to where.</summary>
    public string TimeText { get; } = timeText;

    /// <summary>A picture of it: a frame of its video or the shape of its sound, when one has been made.</summary>
    public System.Windows.Media.ImageSource? Preview { get; } = preview;
}
