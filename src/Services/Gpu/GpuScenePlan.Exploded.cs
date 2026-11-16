namespace HandPegApp.Services.Gpu;

// ----- 3D exploded view depth planes -----
// Each layer draws as a plane at its stack depth: its track's plane, a per-track plane for blur-fill twins (beneath every track, where
// the flat render draws them), or one plane per overlay kind (sequence look/fade, subtitle pictures, captions), numbered in draw order.
// Depths are fixed per plan, so layers appearing or disappearing never shift the planes above or the stack depth the camera orbits.
// Built lazily on the evaluating thread the first time the plan draws in 3D; lookups are allocation-free.
public sealed partial class GpuScenePlan
{
    /// <summary>Plane per clip key (its track's), per blur-fill twin (by its video's key), and the total plane count.</summary>
    private sealed record Places(Dictionary<long, int> ByKey, Dictionary<long, int> TwinByKey, int Depth);

    private Places? _places;

    /// <summary>
    /// Writes each of the first <paramref name="count"/> evaluated layers' plane (<see cref="GpuLayerDescriptor.ZDepth"/>), never below
    /// the layer beneath it, and returns the stack depth (<see cref="FrameSceneHeader.ExplodedDepth"/>). Allocation-free once built.
    /// </summary>
    public int PlaceLayers(Span<GpuLayerDescriptor> layers, int count)
    {
        var places = Volatile.Read(ref _places) ?? MakePlaces();
        var under = 0;
        count = Math.Min(count, layers.Length);
        for (var k = 0; k < count; k++)
        {
            ref var layer = ref layers[k];

            // Unknown layers (none expected) share the plane of the layer beneath.
            var byKey = (layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0 ? places.TwinByKey : places.ByKey;
            var place = byKey.TryGetValue(layer.ClipKey, out var known) ? Math.Max(known, under) : under;
            layer.ZDepth = place;
            under = place;
        }

        return Math.Max(places.Depth, count > 0 ? under + 1 : 0);
    }

    /// <summary>Plane of a clip (its track's) whether or not it is visible now; -1 when the plan does not draw it.</summary>
    public int PlaceOf(long clipKey) => clipKey != 0 && (Volatile.Read(ref _places) ?? MakePlaces()).ByKey.TryGetValue(clipKey, out var place) ? place : -1;

    private Places MakePlaces()
    {
        // Track entries share their track's plane and blur-fill twins one plane per track (numbered where drawn, beneath all tracks);
        // other entries get one plane per kind (the sequence look and Sequence Output effects share one).
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
