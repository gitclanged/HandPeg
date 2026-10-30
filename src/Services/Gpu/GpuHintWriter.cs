namespace HandPegApp.Services.Gpu;

/// <summary>
/// The compositor worker's seek hints, from a scene plan (Instant Seek; Pass 5.0-Release): what the GPU engine's frame
/// pump leaves the worker while it plays and while it rests, and what the GPU export and the bench (tools\SeekBench
/// --project) leave it the same way.
/// <list type="bullet">
/// <item>Ahead of the playhead, playing or not (as if Space were pressed): every cut within <see cref="Horizon"/> seconds of
/// playback (the plan's cut boundary index), earliest first, each with how far its clip goes. The worker decodes the first
/// frames of each and keeps them until the hint goes (the playhead passed it, or it left the horizon); the decoder parked
/// there reads on when the playhead comes.</item>
/// <item>With Loop on, the horizon goes on past the loop's end into the stretch playback goes back to (the frames where it
/// lands, then its cuts), each with a deadline as far past the end as it will be played (5.0-Release: the first second after
/// a wrap of a quick-cut project played at a quarter of its rate).</item>
/// <item>Paused: where a scrub is heading, and the pointer over a timeline: its frame while it moves, a short play-ahead
/// from its moment once it rests (<see cref="HoverRest"/>), never while a scrub runs. The worker does the pointer's work
/// last (after playback, a scrub and the playhead's own pre-roll).</item>
/// </list>
/// The hints are written only when what they say changed: a cut's hint stays the same from the moment it enters the
/// horizon until it is passed, so the worker's lane on it reads on. Allocation-free; one writer per thread that writes hints.
/// </summary>
public sealed class GpuHintWriter
{
    /// <summary>How far ahead of the playhead the cuts are made ready, seconds of playback (the user's directive: 3 s).</summary>
    public const double Horizon = 3.0;

    /// <summary>How long the pointer rests on one moment before its play-ahead is asked for, seconds.</summary>
    public const double HoverRest = 0.12;

    /// <summary>The pointer's play-ahead from its moment, seconds of the sequence.</summary>
    public const double HoverRoll = 0.5;

    /// <summary>The longest stretch of a clip a cut's hint names (the worker keeps the first frames of it; a shorter clip whole).</summary>
    private const double CutReach = 60;

    private readonly GpuMediaHint[] _media = new GpuMediaHint[CompositorProtocol.MaxHints];
    private ulong _written;

    /// <summary>Forgets what was written last (a new worker, a cleared cache): the next <see cref="Write"/> writes.</summary>
    public void Reset() => _written = 0;

    /// <summary>
    /// The hints at a moment of the sequence. <paramref name="rate"/> is the playback speed (the horizon is seconds of
    /// playback; reversed, no cut is ahead); <paramref name="scrubAhead"/> where a paused scrub is heading (NaN none);
    /// <paramref name="pointer"/> the moment under the pointer over a timeline (NaN none: it left, or playback or a scrub
    /// runs), <paramref name="pointerResting"/> whether it has rested there; <paramref name="extra"/> one more hint of the
    /// caller's (a file opened ahead of a drop; Kind 0 none), first; <paramref name="wrapTo"/> where Loop takes playback back
    /// to (NaN: no Loop) from <paramref name="wrapFrom"/> (the sequence's end when not finite). True when they differ from those
    /// written last: hand <paramref name="into"/>[..<paramref name="count"/>] to CompositorClient.SetHints then.
    /// </summary>
    public bool Write(GpuScenePlan plan, double at, double rate, Span<GpuSeekHint> into, out int count,
        double scrubAhead = double.NaN, double pointer = double.NaN, bool pointerResting = false, GpuSeekHint extra = default,
        double wrapFrom = double.NaN, double wrapTo = double.NaN)
    {
        count = 0;
        if (extra.Kind != 0 && into.Length > 0)
            into[count++] = extra;
        var end = plan.SequenceSeconds;
        if (double.IsFinite(scrubAhead) && scrubAhead >= 0 && scrubAhead <= end)
            count = Frames(plan, scrubAhead, GpuSeekHintKind.ScrubAhead, into, count);
        if (rate > 0 && double.IsFinite(at))
        {
            count = CutsAhead(plan, at, Horizon * rate, GpuSeekHintKind.CutEntry, into, count);

            // Looping: what the horizon reaches past the loop's end is played from where Loop goes back to.
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

    /// <summary>The frames the scene needs at a moment, one hint each (due at <paramref name="deadline"/> when given, reading <paramref name="span"/> on).</summary>
    private int Frames(GpuScenePlan plan, double t, GpuSeekHintKind kind, Span<GpuSeekHint> into, int count, double deadline = double.NaN, double span = 0)
    {
        var found = plan.MediaAt(t, _media);
        var due = (float)(double.IsFinite(deadline) ? deadline : t);
        for (var i = 0; i < found && count < into.Length; i++)
            count = Add(into, count, new GpuSeekHint { SourceId = _media[i].SourceId, Kind = (int)kind, MediaSeconds = _media[i].MediaSeconds, At = due, Span = (float)span });
        return count;
    }

    /// <summary>
    /// Every cut after <paramref name="from"/> up to <paramref name="span"/> seconds of the sequence on, earliest first: its
    /// file's moment, how far its clip reads (all of it for a cut; the rest of the stretch for the pointer's play-ahead).
    /// </summary>
    /// <param name="shift">Added to each cut's start for its deadline (a stretch played again after a loop's wrap: as far past the end as it comes).</param>
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

    /// <summary>The pointer rested: what the sequence reads from its moment for <see cref="HoverRoll"/> seconds (its clips there, then the cuts in that stretch).</summary>
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

    /// <summary>Adds a hint unless one of its kind for the same source and moment is there already.</summary>
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

    // FNV-1a over what the hints say (never 0: Reset's "nothing written").
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
