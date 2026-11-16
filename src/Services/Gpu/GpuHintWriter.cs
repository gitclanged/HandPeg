namespace HandPegApp.Services.Gpu;

/// <summary>
/// Builds the compositor worker's Instant Seek hints from a scene plan; used by the GPU engine's frame pump (playing and paused),
/// the GPU export and SeekBench --project.
/// <list type="bullet">
/// <item>Ahead (playing or paused): every cut within <see cref="Horizon"/> playback seconds (plan cut index), earliest first, with
/// its clip span. The worker pre-decodes each cut's first frames until the hint expires; the parked decoder continues when the
/// playhead arrives.</item>
/// <item>Loop on: the horizon continues past the loop end into the wrap target, deadlines offset by the wrap.</item>
/// <item>Paused: the scrub target, and the timeline pointer (its frame while moving, a <see cref="HoverRoll"/> play-ahead once it
/// rests <see cref="HoverRest"/>; never during a scrub). The worker serves pointer hints last.</item>
/// </list>
/// Emits only when the hint set changes, so a cut's hint stays stable from entering the horizon until passed and the worker's lane
/// keeps reading. Allocation-free; one instance per writing thread.
/// </summary>
public sealed class GpuHintWriter
{
    /// <summary>Cut pre-decode horizon ahead of the playhead, playback seconds.</summary>
    public const double Horizon = 3.0;

    /// <summary>Pointer rest time before its play-ahead is requested, seconds.</summary>
    public const double HoverRest = 0.12;

    /// <summary>Pointer play-ahead length, sequence seconds.</summary>
    public const double HoverRoll = 0.5;

    /// <summary>Maximum clip span a cut hint names (the worker keeps its first frames; shorter clips whole).</summary>
    private const double CutReach = 60;

    private readonly GpuMediaHint[] _media = new GpuMediaHint[CompositorProtocol.MaxHints];
    private ulong _written;

    /// <summary>Clears the last-written hash (new worker, cleared cache) so the next <see cref="Write"/> always emits.</summary>
    public void Reset() => _written = 0;

    /// <summary>
    /// Builds hints for sequence time <paramref name="at"/>. <paramref name="rate"/> = playback speed (horizon in playback seconds;
    /// nothing ahead when reversed); <paramref name="scrubAhead"/> = paused scrub target (NaN none); <paramref name="pointer"/> = timeline
    /// pointer time (NaN when absent, playing or scrubbing), <paramref name="pointerResting"/> = it has rested; <paramref name="extras"/> =
    /// caller hints placed first (drop pre-open, tab pre-roll; Kind 0 skipped); <paramref name="wrapTo"/> = Loop target (NaN no Loop) from
    /// <paramref name="wrapFrom"/> (sequence end when not finite). Returns true when the set differs from the last written; then pass
    /// <paramref name="into"/>[..<paramref name="count"/>] to CompositorClient.SetHints.
    /// </summary>
    public bool Write(GpuScenePlan plan, double at, double rate, Span<GpuSeekHint> into, out int count,
        double scrubAhead = double.NaN, double pointer = double.NaN, bool pointerResting = false, ReadOnlySpan<GpuSeekHint> extras = default,
        double wrapFrom = double.NaN, double wrapTo = double.NaN)
    {
        count = 0;
        foreach (var extra in extras)
        {
            if (extra.Kind != 0 && count < into.Length)
                into[count++] = extra;
        }
        var end = plan.SequenceSeconds;
        if (double.IsFinite(scrubAhead) && scrubAhead >= 0 && scrubAhead <= end)
            count = Frames(plan, scrubAhead, GpuSeekHintKind.ScrubAhead, into, count);
        if (rate > 0 && double.IsFinite(at))
        {
            count = CutsAhead(plan, at, Horizon * rate, GpuSeekHintKind.CutEntry, into, count);

            // Loop: the part of the horizon past the loop end plays from the wrap target.
            if (double.IsFinite(wrapTo) && wrapTo >= 0 && wrapTo < end)
            {
                var from = double.IsFinite(wrapFrom) ? Math.Min(wrapFrom, end) : end;
                var beyond = at + Horizon * rate - from;
                if (beyond > 0 && from > wrapTo && at <= from)
                {
                    count = Frames(plan, wrapTo, GpuSeekHintKind.CutEntry, into, count, deadline: from, span: Math.Min(beyond, CutReach));
                    count = CutsAhead(plan, wrapTo, beyond, GpuSeekHintKind.CutEntry, into, count, shift: from - wrapTo);
                }
            }
        }

        if (double.IsFinite(pointer) && pointer >= 0 && pointer <= end)
            count = pointerResting ? Roll(plan, pointer, into, count) : Frames(plan, pointer, GpuSeekHintKind.Pointer, into, count);

        var hash = Hash(into[..count]);
        if (hash == _written)
            return false;
        _written = hash;
        return true;
    }

    /// <summary>One hint per frame the scene needs at <paramref name="t"/> (due at <paramref name="deadline"/> when given, reading <paramref name="span"/> ahead).</summary>
    private int Frames(GpuScenePlan plan, double t, GpuSeekHintKind kind, Span<GpuSeekHint> into, int count, double deadline = double.NaN, double span = 0)
    {
        var found = plan.MediaAt(t, _media);
        var due = (float)(double.IsFinite(deadline) ? deadline : t);
        for (var i = 0; i < found && count < into.Length; i++)
            count = Add(into, count, new GpuSeekHint { SourceId = _media[i].SourceId, Kind = (int)kind, MediaSeconds = _media[i].MediaSeconds, At = due, Span = (float)span });
        return count;
    }

    /// <summary>
    /// Cuts after <paramref name="from"/> within <paramref name="span"/> sequence seconds, earliest first, each with its media time and
    /// read span (the whole clip for a cut; the remaining stretch for a pointer play-ahead).
    /// </summary>
    /// <param name="shift">Added to each cut's start for its deadline (a stretch replayed after a loop wrap).</param>
    private static int CutsAhead(GpuScenePlan plan, double from, double span, GpuSeekHintKind kind, Span<GpuSeekHint> into, int count, double shift = 0)
    {
        var cuts = plan.Cuts;
        for (var i = plan.FirstCutAfter(from); i < cuts.Length && count < into.Length; i++)
        {
            var cut = cuts[i];
            if (cut.Start > from + span)
                break;
            var reach = cut.MediaEnd - cut.Media;
            if (kind == GpuSeekHintKind.HoverRoll)
                reach = Math.Min(reach, (from + span - cut.Start) * cut.Speed);
            count = Add(into, count, new GpuSeekHint
            {
                SourceId = cut.SourceId, Kind = (int)kind, MediaSeconds = cut.Media, PathHash = cut.PathHash,
                Span = (float)Math.Clamp(reach, 0, CutReach), At = (float)(cut.Start + shift),
            });
        }

        return count;
    }

    /// <summary>Resting pointer: the clips under it, then the cuts within <see cref="HoverRoll"/> seconds.</summary>
    private static int Roll(GpuScenePlan plan, double t, Span<GpuSeekHint> into, int count)
    {
        var cuts = plan.Cuts;
        for (var i = 0; i < cuts.Length && count < into.Length; i++)
        {
            var cut = cuts[i];
            if (cut.Start > t)
                break;
            if (t >= cut.End)
                continue;
            var media = cut.MediaAt(t);
            count = Add(into, count, new GpuSeekHint
            {
                SourceId = cut.SourceId, Kind = (int)GpuSeekHintKind.HoverRoll, MediaSeconds = media, PathHash = cut.PathHash,
                Span = (float)Math.Clamp(Math.Min(HoverRoll * cut.Speed, cut.MediaEnd - media), 0, CutReach), At = (float)t,
            });
        }

        return CutsAhead(plan, t, HoverRoll, GpuSeekHintKind.HoverRoll, into, count);
    }

    /// <summary>Adds a hint, or widens the span of an existing hint with the same kind, source and media time.</summary>
    private static int Add(Span<GpuSeekHint> into, int count, GpuSeekHint hint)
    {
        for (var k = 0; k < count; k++)
        {
            if (into[k].Kind == hint.Kind && into[k].SourceId == hint.SourceId && Math.Abs(into[k].MediaSeconds - hint.MediaSeconds) < 1e-6)
            {
                into[k].Span = Math.Max(into[k].Span, hint.Span);
                return count;
            }
        }

        if (count < into.Length)
            into[count++] = hint;
        return count;
    }

    // FNV-1a over the hint contents; never 0 (Reset's sentinel).
    private static ulong Hash(ReadOnlySpan<GpuSeekHint> hints)
    {
        var hash = 14695981039346656037UL;
        void Mix(ulong value) => hash = (hash ^ value) * 1099511628211UL;
        Mix((ulong)hints.Length);
        foreach (var hint in hints)
        {
            Mix((ulong)(uint)hint.SourceId | (ulong)(uint)hint.Kind << 32);
            Mix((ulong)BitConverter.DoubleToInt64Bits(hint.MediaSeconds));
            Mix((ulong)hint.PathHash);
            Mix((ulong)BitConverter.SingleToUInt32Bits(hint.Span) | (ulong)BitConverter.SingleToUInt32Bits(hint.At) << 32);
        }

        return hash == 0 ? 1 : hash;
    }
}
