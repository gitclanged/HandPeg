namespace HandPegApp.Models;

/// <summary>
/// Keyframe-list edits shared by every editor (the Effects pane, the Color Studio, the Keyframes pane): one tolerance for "the key at
/// this time" (<see cref="Layer.KeyTolerance"/>, inclusive) and one rounding of a new key's time (to the millisecond, never before 0).
/// </summary>
public static class KeyEdit
{
    /// <summary>The index of the key at <paramref name="time"/> (within <see cref="Layer.KeyTolerance"/>), or -1.</summary>
    public static int IndexAt(IReadOnlyList<Keyframe> keys, double time)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (Math.Abs(keys[i].Time - time) <= Layer.KeyTolerance)
                return i;
        }

        return -1;
    }

    /// <summary>Whether a key sits at <paramref name="time"/>.</summary>
    public static bool HasKeyAt(IReadOnlyList<Keyframe> keys, double time) => IndexAt(keys, time) >= 0;

    /// <summary>A new key's time: not before 0, rounded to the millisecond.</summary>
    public static double TimeOf(double time) => Math.Round(Math.Max(time, 0), 3);

    /// <summary>A copy of <paramref name="keys"/> with the key at <paramref name="time"/> set to <paramref name="value"/> (added when none is there).</summary>
    public static List<Keyframe> Upsert(IReadOnlyList<Keyframe> keys, double time, double value)
    {
        var list = keys.ToList();
        var at = IndexAt(list, time);
        if (at >= 0)
            list[at] = list[at] with { Value = value };
        else
            list.Add(new Keyframe(TimeOf(time), value));
        return list;
    }

    /// <summary>A copy of <paramref name="keys"/> with the key at <paramref name="time"/> removed, or one added there with <paramref name="value"/>.</summary>
    public static List<Keyframe> Toggle(IReadOnlyList<Keyframe> keys, double time, double value, out bool removed)
    {
        var list = keys.ToList();
        var at = IndexAt(list, time);
        removed = at >= 0;
        if (removed)
            list.RemoveAt(at);
        else
            list.Add(new Keyframe(TimeOf(time), value));
        return list;
    }
}
