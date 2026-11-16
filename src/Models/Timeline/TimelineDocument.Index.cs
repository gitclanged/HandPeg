namespace HandPegApp.Models.Timeline;

/// <summary>
/// Clip id index giving O(1) <see cref="FindClip"/>, <see cref="Locate"/> and <see cref="TrackOf"/> (linear walks made row rebuilds
/// O(C^2) on Dead Air and multicam projects).
/// <para>An immutable dictionary published via a volatile field and replaced wholesale, so concurrent readers (render snapshots) never
/// see partial state. Dropped when the outermost transaction closes and rebuilt lazily; lookups during a transaction walk the tracks
/// since clips move, split and change ids.</para>
/// <para>Self-healing: hits are verified in O(1) (same id, track and clip positions unchanged); misses or failed checks fall back to the
/// walk, and the index is dropped if the walk finds what it missed (out-of-transaction changes: loading, legacy conversion, test
/// sequences). Direct <see cref="Tracks"/> edits can only cost speed, never correctness; genuine misses keep the index.</para>
/// </summary>
public sealed partial class TimelineDocument
{
    private volatile Dictionary<Guid, Entry>? _clipIndex;

    // Track and clip positions at build time (verified on every hit).
    private readonly record struct Entry(TimelineClip Clip, TimelineTrackModel Track, int TrackAt, int ClipAt);

    /// <summary>How many times the index was built (for the goldens: lookups between edits must not rebuild it).</summary>
    internal int ClipIndexBuilds { get; private set; }

    /// <summary>Drops the id index; the next lookup rebuilds it. Called when the outermost transaction closes.</summary>
    internal void InvalidateClipIndex() => _clipIndex = null;

    /// <summary>Clip by id (first in track/clip order), or null.</summary>
    public TimelineClip? FindClip(Guid id) => Locate(id)?.Clip;

    /// <summary>Clip and track by id (same match as <see cref="FindClip"/>), or null.</summary>
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

        // Not the indexed instance (snapshot or duplicate id): binary search per track, then a full walk.
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
                index.TryAdd(track.Clips[c].Id, new Entry(track.Clips[c], track, t, c)); // first in track/clip order wins, matching the walk
        }

        ClipIndexBuilds++;
        _clipIndex = index;
        return index;
    }
}
