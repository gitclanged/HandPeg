namespace HandPegApp.Services.Gpu;

// ----- The 3D exploded view's places (Pass 5.0-Release) -----
// In the 3D view each layer is a plane at its place in the stack. A place is its track's (a blur fill's twin has one of its
// own, behind its video's whole family, where the flat picture draws it: 5.2.0's re-cut, the user's P0; until then it shared
// its video's plane), or, for what lies over every track, its own (the whole picture's look and fade, the
// subtitle pictures, the captions), numbered in the order the compositor draws them, the same for the whole plan: a layer
// that comes or goes (a caption between its cues, a karaoke line's sung half, a layer waiting for its texture) never moves
// the planes above it, and the stack's depth (the camera orbits its middle) never changes while the plan is drawn. The
// places are worked out once, the first time a plan is drawn in 3D, on the thread that evaluates it; reading them
// allocates nothing.
public sealed partial class GpuScenePlan
{
    /// <summary>Each clip's place by its key (its track's), each blur fill's (its twin's, by its video's key), and how many places there are.</summary>
    private sealed record Places(Dictionary<long, int> ByKey, Dictionary<long, int> TwinByKey, int Depth);

    private Places? _places;

    /// <summary>
    /// Sets each of the <paramref name="count"/> layers <see cref="Evaluate"/> wrote to its place in the plan's stack
    /// (<see cref="GpuLayerDescriptor.ZDepth"/>), never lower than the layer under it, and returns how many places the stack
    /// has (<see cref="FrameSceneHeader.ExplodedDepth"/>). Allocation-free once the plan's places are made.
    /// </summary>
    public int PlaceLayers(Span<GpuLayerDescriptor> layers, int count)
    {
        var places = Volatile.Read(ref _places) ?? MakePlaces();
        var under = 0;
        count = Math.Min(count, layers.Length);
        for (var k = 0; k < count; k++)
        {
            ref var layer = ref layers[k];

            // A layer the plan does not know (none should be) shares the place of the one under it.
            var byKey = (layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0 ? places.TwinByKey : places.ByKey;
            var place = byKey.TryGetValue(layer.ClipKey, out var known) ? Math.Max(known, under) : under;
            layer.ZDepth = place;
            under = place;
        }

        return Math.Max(places.Depth, count > 0 ? under + 1 : 0);
    }

    /// <summary>A clip's place in the plan's stack (its track's), whether or not it is on now; -1 for a clip the plan does not draw.</summary>
    public int PlaceOf(long clipKey) => clipKey != 0 && (Volatile.Read(ref _places) ?? MakePlaces()).ByKey.TryGetValue(clipKey, out var place) ? place : -1;

    private Places MakePlaces()
    {
        // Entries of one track share its place, and the blur fills of one track's clips theirs (numbered where they are drawn: under
        // the track's family); the rest have one each of their kind (the whole picture's look and the Sequence Output's effects,
        // both over the whole picture, share one).
        var groups = new Dictionary<(int Kind, int Track), int>();
        var (byKey, twinByKey) = (new Dictionary<long, int>(), new Dictionary<long, int>());
        foreach (var entry in _entries)
        {
            var onTrack = entry.Kind is EntryKind.File or EntryKind.Still or EntryKind.Text or EntryKind.Adjustment;
            var group = entry.Kind == EntryKind.Twin ? (-2, entry.TrackIndex) : onTrack && entry.TrackIndex >= 0 ? (-1, entry.TrackIndex) : ((int)entry.Kind, -1);
            if (!groups.TryGetValue(group, out var place))
                groups[group] = place = groups.Count;

            (entry.Kind == EntryKind.Twin ? twinByKey : byKey).TryAdd(entry.ClipKey, place);
        }

        var made = new Places(byKey, twinByKey, groups.Count);
        return Interlocked.CompareExchange(ref _places, made, null) ?? made;
    }
}
