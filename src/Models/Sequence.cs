using HandPegApp.Models.Timeline;

namespace HandPegApp.Models;

// The view model's timeline is a TimelineDocument; file-span queries live in Models/Timeline/SequenceSpans.

/// <summary>
/// Recycle bin entry: the deleted clip and its original track (id, kind, stacking index) for restoration there, or onto a new track of
/// that kind at that index when the original is gone or full.
/// </summary>
public sealed record DeletedClip(TimelineClip Clip, Guid TrackId, TimelineTrackKind TrackKind, int TrackIndex)
{
    /// <summary>
    /// Descendant clips deleted with it (recursively), restored together; null (omitted) when none.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeletedClip>? Children { get; init; }

    /// <summary>
    /// Parent track id of the original track (TimelineTrackModel.ParentTrackId) for nesting a recreated track; null (omitted) when none.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TrackParentId { get; init; }
}
