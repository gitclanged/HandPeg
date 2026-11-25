namespace HandPegApp.Services.Audio;

// ----- Couch Voice Auto-Switcher: which shot to show from who is talking -----
// Per-mic speech activity drives a shot list. Each mic's level (RMS per 20 ms hop) is read against its own adaptive noise floor (its quiet
// 10th percentile): a hop is speech above the floor by SpeechAboveFloorDb (and over SilenceDb), and only within BleedDb of the clearest mic
// that hop (a neighbour's voice leaking into a mic is quieter there than in its own). Syllable gaps up to HangoverSeconds are filled, blips
// under MinBurstSeconds dropped. Shots: the active speaker's; Wide while two or more talk or laugh together for OverlapSeconds; a listener's
// reaction cutaway during a long monologue. Cuts lead the new speaker's first word by LeadSeconds and follow the last one by TailSeconds
// (never past the newcomer's onset); a new speaker must hold the floor SwitchSeconds, and no shot is shorter than MinShotSeconds
// (hysteresis), except a reaction. Silence never cuts. Two modes: Angles (multicam: one angle on screen, reactions on) and Facecams (the game
// stays on; the spotlight follows the speaker, Wide shows every facecam, no reaction cutaways). Pure and deterministic: levels in, shots out.

/// <summary>What the switcher produces shots for.</summary>
public enum VoiceSceneMode
{
    /// <summary>Multicam: one angle per mic plus a wide angle; one on screen at a time, reaction cutaways during monologues.</summary>
    Angles,

    /// <summary>Game with facecams: the spotlight follows the speaker, Wide = every facecam; no reaction cutaways.</summary>
    Facecams,
}

public enum VoiceShotKind
{
    /// <summary>The talking mic's angle (or spotlighted facecam).</summary>
    Speaker,

    /// <summary>Two or more talking or laughing together (and before anyone talks): the wide angle, or every facecam.</summary>
    Wide,

    /// <summary>A listener's cutaway during a long monologue (Angles only).</summary>
    Reaction,
}

/// <summary>One shot of the switch: seconds from the start of the analysed audio, its kind and mic (-1 for Wide).</summary>
public readonly record struct VoiceShot(double Start, double End, VoiceShotKind Kind, int Mic)
{
    public double Length => End - Start;
}

/// <summary>Switcher settings (defaults suit a couch of 2-4 people on their own mics).</summary>
public sealed record VoiceSceneSettings
{
    public VoiceSceneMode Mode { get; init; } = VoiceSceneMode.Angles;

    /// <summary>Analysis hop, seconds.</summary>
    public double HopSeconds { get; init; } = 0.02;

    /// <summary>Shortest shot (hysteresis): no cut comes sooner after the last one.</summary>
    public double MinShotSeconds { get; init; } = 2.0;

    /// <summary>How long a new speaker must hold the floor before the shot follows.</summary>
    public double SwitchSeconds { get; init; } = 0.4;

    /// <summary>Two or more voices together this long (crosstalk, group laughter) go Wide.</summary>
    public double OverlapSeconds { get; init; } = 0.6;

    /// <summary>A cut to a new speaker lands this long before their first word.</summary>
    public double LeadSeconds { get; init; } = 0.25;

    /// <summary>A cut away from a speaker waits this long after their last word (while the newcomer has not started).</summary>
    public double TailSeconds { get; init; } = 0.5;

    /// <summary>One speaker this long on the floor gets a reaction cutaway (Angles); 0 = none.</summary>
    public double ReactionAfterSeconds { get; init; } = 12;

    /// <summary>Reaction cutaway length.</summary>
    public double ReactionSeconds { get; init; } = 1.5;

    /// <summary>Speech is this far above the mic's own noise floor.</summary>
    public double SpeechAboveFloorDb { get; init; } = 12;

    /// <summary>Never speech below this level, dBFS.</summary>
    public double SilenceDb { get; init; } = -50;

    /// <summary>A mic this far below the clearest one that hop is bleed, not a second voice.</summary>
    public double BleedDb { get; init; } = 9;

    /// <summary>Gaps in a voice up to this long (between syllables and words) stay speech.</summary>
    public double HangoverSeconds { get; init; } = 0.3;

    /// <summary>Speech shorter than this (a cough, a knock) is ignored.</summary>
    public double MinBurstSeconds { get; init; } = 0.12;
}

/// <summary>Couch Voice Auto-Switcher (see the file header): per-mic levels to activity to shots.</summary>
public static class VoiceSceneSolver
{
    private const float Quiet = -200f;

    /// <summary>RMS level (dBFS) of each hop of a mono signal; <see cref="Quiet"/> for digital silence.</summary>
    public static float[] Levels(ReadOnlySpan<float> mono, int sampleRate, double hopSeconds = 0.02)
    {
        var hop = Math.Max((int)Math.Round(Math.Max(sampleRate, 1) * Math.Max(hopSeconds, 1e-3)), 1);
        var levels = new float[(mono.Length + hop - 1) / hop];
        for (var h = 0; h < levels.Length; h++)
        {
            var part = mono.Slice(h * hop, Math.Min(hop, mono.Length - h * hop));
            var sum = 0.0;
            foreach (var sample in part)
                sum += sample * (double)sample;
            var rms = Math.Sqrt(sum / Math.Max(part.Length, 1));
            levels[h] = rms > 1e-10 ? (float)(20 * Math.Log10(rms)) : Quiet;
        }

        return levels;
    }

    /// <summary>
    /// Each mic's noise floor (dBFS): the 10th percentile of its levels, held to -100 and to the speech margin over the silence level (a mic
    /// that talks nearly throughout has no quiet to learn from, so its threshold never rises past SilenceDb + 2 x SpeechAboveFloorDb).
    /// </summary>
    public static double[] Floors(IReadOnlyList<float[]> levels, VoiceSceneSettings? settings = null)
    {
        settings ??= new VoiceSceneSettings();
        var highest = Math.Max(settings.SilenceDb + settings.SpeechAboveFloorDb, -100);
        var floors = new double[levels.Count];
        for (var m = 0; m < levels.Count; m++)
        {
            var sorted = levels[m].Where(float.IsFinite).ToArray();
            Array.Sort(sorted);
            floors[m] = sorted.Length == 0 ? -100 : Math.Clamp(sorted[(int)((sorted.Length - 1) * 0.1)], -100, highest);
        }

        return floors;
    }

    /// <summary>
    /// Speech activity per mic and hop: above its floor and the silence level, within <see cref="VoiceSceneSettings.BleedDb"/> of the clearest
    /// mic that hop, gaps up to the hangover filled and bursts under the minimum dropped.
    /// </summary>
    public static bool[][] Activity(IReadOnlyList<float[]> levels, VoiceSceneSettings settings)
    {
        var mics = levels.Count;
        var hops = mics == 0 ? 0 : levels.Max(l => l.Length);
        var floors = Floors(levels, settings);
        var active = new bool[mics][];
        for (var m = 0; m < mics; m++)
            active[m] = new bool[hops];

        for (var h = 0; h < hops; h++)
        {
            var clearest = double.NegativeInfinity;
            for (var m = 0; m < mics; m++)
            {
                if (Speaks(levels[m], h, floors[m], settings, out var above))
                    clearest = Math.Max(clearest, above);
            }

            for (var m = 0; m < mics; m++)
                active[m][h] = Speaks(levels[m], h, floors[m], settings, out var above) && above >= clearest - settings.BleedDb;
        }

        var (hang, burst) = (Hops(settings.HangoverSeconds, settings), Hops(settings.MinBurstSeconds, settings));
        foreach (var lane in active)
        {
            FillGaps(lane, hang);
            DropBursts(lane, burst);
        }

        return active;
    }

    // True when a hop of a mic is above its floor by the speech margin and above the silence level; above = its margin over the floor.
    private static bool Speaks(float[] levels, int hop, double floor, VoiceSceneSettings settings, out double above)
    {
        var level = hop < levels.Length && float.IsFinite(levels[hop]) ? levels[hop] : Quiet;
        above = level - floor;
        return level >= settings.SilenceDb && above >= settings.SpeechAboveFloorDb;
    }

    private static int Hops(double seconds, VoiceSceneSettings settings) => Math.Max((int)Math.Round(seconds / Math.Max(settings.HopSeconds, 1e-3)), 0);

    // Fills inactive runs of at most `most` hops between active hops.
    private static void FillGaps(bool[] lane, int most)
    {
        var last = -1;
        for (var h = 0; h < lane.Length; h++)
        {
            if (!lane[h])
                continue;
            if (last >= 0 && h - last - 1 <= most)
                Array.Fill(lane, true, last + 1, h - last - 1);
            last = h;
        }
    }

    // Clears active runs shorter than `least` hops.
    private static void DropBursts(bool[] lane, int least)
    {
        for (var h = 0; h < lane.Length;)
        {
            if (!lane[h])
            {
                h++;
                continue;
            }

            var end = h;
            while (end < lane.Length && lane[end])
                end++;
            if (end - h < least)
                Array.Fill(lane, false, h, end - h);
            h = end;
        }
    }

    /// <summary>The shots for per-mic levels (dBFS per hop, <see cref="Levels"/>), covering the analysed span; empty without mics or hops.</summary>
    public static List<VoiceShot> Solve(IReadOnlyList<float[]> levels, VoiceSceneSettings? settings = null)
    {
        settings ??= new VoiceSceneSettings();
        var active = Activity(levels, settings);
        var floors = Floors(levels, settings);
        var hops = active.Length == 0 ? 0 : active[0].Length;
        if (hops == 0)
            return [];

        var hop = Math.Max(settings.HopSeconds, 1e-3);
        var total = hops * hop;
        var (switchHops, overlapHops) = (Math.Max(Hops(settings.SwitchSeconds, settings), 1), Math.Max(Hops(settings.OverlapSeconds, settings), 1));
        var shots = new List<VoiceShot>();
        var (kind, mic, start) = (VoiceShotKind.Wide, -1, 0.0);
        var lastSpeech = new int[active.Length];
        Array.Fill(lastSpeech, -1);
        var (overlapFrom, overlapLast) = (-1, -1);
        var (pendingKind, pendingMic, pendingFrom) = (VoiceShotKind.Wide, -2, -1);

        for (var h = 0; h < hops; h++)
        {
            // Who talks this hop, the clearest of them, and how long two or more have talked together.
            var (count, dominant, clearest) = (0, -1, double.NegativeInfinity);
            for (var m = 0; m < active.Length; m++)
            {
                if (!active[m][h])
                    continue;
                count++;
                var level = h < levels[m].Length ? levels[m][h] - floors[m] : double.NegativeInfinity;
                if (level > clearest)
                    (clearest, dominant) = (level, m);
            }

            if (count >= 2)
                (overlapFrom, overlapLast) = (overlapFrom < 0 ? h : overlapFrom, h);
            else
                overlapFrom = -1;

            // The shot this hop asks for: Wide once voices overlapped long enough, nothing while an overlap is younger (an interjection, or
            // crosstalk to come), the clearest speaker alone; silence asks for nothing.
            (VoiceShotKind Kind, int Mic, int From)? wanted = count >= 2
                ? h - overlapFrom + 1 >= overlapHops ? (VoiceShotKind.Wide, -1, overlapFrom) : null
                : dominant >= 0 ? (VoiceShotKind.Speaker, dominant, h) : null;

            if (wanted is not { } want || want.Kind == kind && want.Mic == mic)
            {
                // A young overlap pauses a newcomer's claim; silence or the current shot ends it.
                if (wanted is not null || count < 2)
                    pendingMic = -2;
            }
            else
            {
                if (want.Kind != pendingKind || want.Mic != pendingMic)
                    (pendingKind, pendingMic, pendingFrom) = (want.Kind, want.Mic, want.From);

                var held = want.Kind == VoiceShotKind.Wide || h - pendingFrom + 1 >= switchHops;
                if (held && h * hop >= start + settings.MinShotSeconds || held && shots.Count == 0 && kind == VoiceShotKind.Wide && Opening(settings, start, pendingFrom * hop))
                {
                    var cut = CutAt(settings, kind, mic, start, pendingKind, pendingFrom * hop, lastSpeech, overlapLast, hop, shots.Count == 0 && kind == VoiceShotKind.Wide);
                    if (cut > start + 1e-9)
                        shots.Add(new VoiceShot(start, cut, kind, mic));
                    (kind, mic, start, pendingMic) = (pendingKind, pendingMic, Math.Max(cut, start), -2);
                }
            }

            for (var m = 0; m < active.Length; m++)
            {
                if (active[m][h])
                    lastSpeech[m] = h;
            }
        }

        shots.Add(new VoiceShot(start, total, kind, mic));
        if (settings.Mode == VoiceSceneMode.Angles && settings.ReactionAfterSeconds > 0 && settings.ReactionSeconds > 0)
            shots = WithReactions(shots, active, settings, hop);
        return Merged(shots);
    }

    // The opening wide shot gives way to the first speaker at once when they start within the shortest shot (no establishing wide that short).
    private static bool Opening(VoiceSceneSettings settings, double start, double onset) => onset - start < settings.MinShotSeconds;

    /// <summary>
    /// Where a cut lands: Wide at the overlap's start; a speaker <see cref="VoiceSceneSettings.LeadSeconds"/> before their first word when the
    /// silence since the outgoing speech (its speaker's last word, or the overlap a Wide shot showed) also holds the
    /// <see cref="VoiceSceneSettings.TailSeconds"/>; a shorter silence is shared between tail and lead; an interruption cuts at the
    /// newcomer's first word. Never before the current shot has run <see cref="VoiceSceneSettings.MinShotSeconds"/>; an opening wide that
    /// would be shorter is dropped (the first speaker from the start).
    /// </summary>
    private static double CutAt(VoiceSceneSettings settings, VoiceShotKind kind, int mic, double start, VoiceShotKind next, double onset,
        int[] lastSpeech, int overlapLast, double hop, bool opening)
    {
        var cut = onset;
        if (next != VoiceShotKind.Wide)
        {
            var outgoing = kind == VoiceShotKind.Speaker && mic >= 0 ? lastSpeech[mic] : kind == VoiceShotKind.Wide ? overlapLast : -1;
            var ended = outgoing >= 0 ? (outgoing + 1) * hop : double.NegativeInfinity;
            var (lead, tail, gap) = (Math.Max(settings.LeadSeconds, 0), Math.Max(settings.TailSeconds, 0), onset - ended);
            cut = gap >= lead + tail ? onset - lead
                : gap > 0 ? ended + gap * (lead + tail > 0 ? tail / (lead + tail) : 0)
                : onset;
        }

        cut = Math.Max(cut, opening ? start : start + settings.MinShotSeconds);
        return opening && cut - start < settings.MinShotSeconds ? start : cut;
    }

    // Reaction cutaways in long speaker shots: after ReactionAfterSeconds on the floor, at the speaker's next pause (plus the tail) or then,
    // a listener (the last other mic to talk, else the next mic) for ReactionSeconds, back on the speaker by their next word less the lead;
    // repeated through a long monologue while the speaker shot left after it holds MinShotSeconds.
    private static List<VoiceShot> WithReactions(List<VoiceShot> shots, bool[][] active, VoiceSceneSettings settings, double hop)
    {
        if (active.Length < 2)
            return shots;

        var result = new List<VoiceShot>(shots.Count);
        foreach (var shot in shots)
        {
            if (shot.Kind != VoiceShotKind.Speaker)
            {
                result.Add(shot);
                continue;
            }

            var at = shot.Start;
            while (true)
            {
                var due = at + settings.ReactionAfterSeconds;
                if (due + settings.ReactionSeconds + settings.MinShotSeconds > shot.End)
                    break;

                var lane = active[shot.Mic];
                var (from, back) = ReactionSpan(lane, due, Math.Min(due + settings.ReactionAfterSeconds / 2, shot.End - settings.MinShotSeconds), settings, hop);
                if (back - from < settings.ReactionSeconds * 0.5 || back + settings.MinShotSeconds > shot.End)
                    break;

                result.Add(new VoiceShot(at, from, VoiceShotKind.Speaker, shot.Mic));
                result.Add(new VoiceShot(from, back, VoiceShotKind.Reaction, Listener(active, shot.Mic, (int)(from / hop))));
                at = back;
            }

            result.Add(new VoiceShot(at, shot.End, VoiceShotKind.Speaker, shot.Mic));
        }

        return result;
    }

    // A reaction's span: from the speaker's first pause in [due, latest] plus the tail (else due), for ReactionSeconds, cut short so the
    // return leads the speaker's next word.
    private static (double From, double Back) ReactionSpan(bool[] lane, double due, double latest, VoiceSceneSettings settings, double hop)
    {
        var from = due;
        for (var h = (int)Math.Ceiling(due / hop); h < lane.Length && h * hop <= latest; h++)
        {
            if (!lane[h] && h > 0 && lane[h - 1])
            {
                from = Math.Min(h * hop + settings.TailSeconds, latest);
                break;
            }
        }

        var back = from + settings.ReactionSeconds;
        for (var h = (int)Math.Ceiling(from / hop); h < lane.Length && h * hop < back; h++)
        {
            if (lane[h] && h > 0 && !lane[h - 1] && h * hop - settings.LeadSeconds > from)
            {
                back = h * hop - settings.LeadSeconds;
                break;
            }
        }

        return (from, back);
    }

    // The listener a reaction shows: the last mic other than the speaker to talk before the hop, else the next mic round.
    private static int Listener(bool[][] active, int speaker, int hop)
    {
        var (best, latest) = ((speaker + 1) % active.Length, -1);
        for (var m = 0; m < active.Length; m++)
        {
            if (m == speaker)
                continue;
            for (var h = Math.Min(hop, active[m].Length - 1); h > latest; h--)
            {
                if (active[m][h])
                {
                    (best, latest) = (m, h);
                    break;
                }
            }
        }

        return best;
    }

    // Joins neighbours showing the same shot and drops empty ones.
    private static List<VoiceShot> Merged(List<VoiceShot> shots)
    {
        var merged = new List<VoiceShot>(shots.Count);
        foreach (var shot in shots)
        {
            if (shot.Length <= 1e-9)
                continue;
            if (merged.Count > 0 && merged[^1] is var last && last.Kind == shot.Kind && last.Mic == shot.Mic && Math.Abs(last.End - shot.Start) < 1e-9)
                merged[^1] = last with { End = shot.End };
            else
                merged.Add(shot);
        }

        return merged;
    }
}
