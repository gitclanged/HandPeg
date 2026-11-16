namespace HandPegApp.Models.Timeline;

/// <summary>
/// The clip id index: <see cref="FindClip"/>, <see cref="Locate"/> and <see cref="TrackOf"/> in O(1) instead of a walk over every track
/// and clip (row rebuilds called them several times per block, which made a rebuild O(C²) on Dead Air and multicam projects).
/// <para>The index is an immutable dictionary, published through a volatile field and replaced, never changed in place, so a thread
/// reading a document (a render snapshot) never sees it half-built. It is dropped when the outermost transaction closes (commit or
/// rollback) and rebuilt by the next lookup; lookups made while a transaction is open walk the tracks, because a transaction moves,
/// splits and re-ids clips as it goes.</para>
/// <para>Self-healing rather than trusted: a hit is verified in O(1) (the same id, and the track and the clip still at the positions they
/// had when the index was built); a miss or a failed verification falls back to the walk, and the index is dropped when the walk finds what the index did not
/// (a clip added or moved outside a transaction: project loading, legacy conversion, the playback test's sequence). So a direct change to
/// <see cref="Tracks"/> or a track's clips can only cost speed, never a wrong answer. Genuine misses (a parent id whose clip is gone) keep
/// the index.</para>
/// </summary>
public sealed partial class TimelineDocument
{
    private volatile Dictionary<Guid, Entry>? _clipIndex;

    // Where a clip was when the index was built: its track's position in Tracks and its own on the track (both checked on every hit).
    private readonly record struct Entry(TimelineClip Clip, TimelineTrackModel Track, int TrackAt, int ClipAt);

    /// <summary>How many times the index was built (for the goldens: lookups between edits must not rebuild it).</summary>
    internal int ClipIndexBuilds { get; private set; }

    /// <summary>Drops the id index; the next lookup rebuilds it. Called when the outermost transaction closes.</summary>
    internal void InvalidateClipIndex() => _clipIndex = null;

    /// <summary>The clip with this id, or null when no track holds it (the first in track and clip order, as a walk finds it).</summary>
    public TimelineClip? FindClip(Guid id) => Locate(id)?.Clip;

    /// <summary>A clip by its id with its track, or null when no track holds it (the first, as <see cref="FindClip"/> finds it).</summary>
    internal (TimelineClip Clip, TimelineTrackModel Track)? Locate(Guid id)
    {
        if (OpenTransactions.Count > 0)
            return Walk(id);

        var index = _clipIndex ?? BuildClipIndex();
        if (index.TryGetValue(id, out var hit))
        {
            if (hit.Clip.Id == id && hit.TrackAt < Tracks.Count && ReferenceEquals(Tracks[hit.TrackAt], hit.Track)
                && hit.ClipAt < hit.Track.Clips.Count && ReferenceEquals(hit.Track.Clips[hit.ClipAt], hit.Clip))
                return (hit.Clip, hit.Track);
            _clipIndex = null; // the entry is provably stale: changed outside a transaction
            return Walk(id);
        }

        var found = Walk(id);
        if (found is not null)
            _clipIndex = null; // the walk found a clip the index never saw
        return found;
    }

    /// <summary>The track a clip is on, or null when it is on none of them.</summary>
    public TimelineTrackModel? TrackOf(TimelineClip clip)
    {
        if (Locate(clip.Id) is { } hit && ReferenceEquals(hit.Clip, clip))
            return hit.Track;

        // A clip that is not the document's clip of its id (a snapshot, a clip with a duplicated id): binary search on each track,
        // then the long way, as before the index.
        foreach (var track in Tracks)
        {
            if (track.Find(clip) >= 0)
                return track;
        }

        foreach (var track in Tracks)
        {
            if (track.Clips.Contains(clip))
                return track;
        }

        return null;
    }

    private (TimelineClip Clip, TimelineTrackModel Track)? Walk(Guid id)
    {
        foreach (var track in Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.Id == id)
                    return (clip, track);
            }
        }

        return null;
    }

    private Dictionary<Guid, Entry> BuildClipIndex()
    {
        var count = 0;
        foreach (var track in Tracks)
            count += track.Clips.Count;
        var index = new Dictionary<Guid, Entry>(count);
        for (var t = 0; t < Tracks.Count; t++)
        {
            var track = Tracks[t];
            for (var c = 0; c < track.Clips.Count; c++)
                index.TryAdd(track.Clips[c].Id, new Entry(track.Clips[c], track, t, c)); // first in track and clip order wins, as the walk
        }

        ClipIndexBuilds++;
        _clipIndex = index;
        return index;
    }
}
