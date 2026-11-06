using HandPegApp.Models.Timeline;

namespace HandPegApp.Models;

// The view model's timeline is a TimelineDocument (4.3C); the Sequence as stretches of files is Models/Timeline/SequenceSpans.

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
