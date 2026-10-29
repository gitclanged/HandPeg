using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- The timeline as one document (v4.3.0, .hproj format 5) -----
// A sequence is tracks of clips, and no track is privileged: track 1 is VideoTracks[0], a video track like any other,
// and every clip on every track is trimmed, split, crossfaded, J/L split and moved the same way. Times are seconds on
// the sequence; while the document quantizes, every edge lies on a frame of its rate (FrameRate.Quantize), so clips
// that touch share one exact double. Clips on a track never overlap: a same-track crossfade is the later clip's
// TransitionIn across a cut where the two meet, centred on the cut and made from the files' handles when the graph is
// built, as 4.2.0 does it. Edits go through a transaction (TimelineTransaction.cs), which keeps the tracks in order and
// apart and can be rolled back whole.

/// <summary>What a track holds. Saved by name in projects: new kinds are added at the end, and none is renamed.</summary>
public enum TimelineTrackKind
{
    /// <summary>Pictures: video files, images, text, cut-outs of a video, the blurred background.</summary>
    Video,

    /// <summary>Sound with no picture of its own: a file's audio stream, music, a voice-over.</summary>
    Audio,

    /// <summary>A switch between the tracks under it (EasyCut, v5.2). Holds what a video track holds.</summary>
    Switch,

    /// <summary>Clips with no picture of their own, whose effect stacks work on everything under them while they last.</summary>
    Adjustment,

    /// <summary>The captions' box.</summary>
    Subtitle,
}

/// <summary>One track of a <see cref="TimelineDocument"/>: its clips in time, and what they share.</summary>
public sealed class TimelineTrackModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Its number among the tracks of its kind, from 1 at the bottom ("Video 1" is the lowest video track).</summary>
    public int TrackNumber { get; set; }

    /// <summary>The name the user gave it; empty for the usual one (<see cref="DisplayName"/>).</summary>
    public string Name { get; set; } = "";

    public TimelineTrackKind Kind { get; set; }

    /// <summary>Its name, or for one without, its kind and number: "Video 1", "Audio 2".</summary>
    [JsonIgnore]
    public string DisplayName => Name.Length > 0 ? Name : $"{Kind} {TrackNumber}";

    public bool IsHidden { get; set; }
    public bool IsMuted { get; set; }
    public bool IsSoloed { get; set; }

    /// <summary>Its clips are not edited, and ripples across tracks pass it by.</summary>
    public bool IsLocked { get; set; }

    // The track's sound: its gain, whether it ducks while a voice speaks and by how much, and its filters (null: none).
    public double GainDb { get; set; }
    public bool DuckAgainstVoice { get; set; }
    public double DuckAmountDb { get; set; } = -15;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrackAudioFilters? AudioFilters { get; set; }

    /// <summary>
    /// What its clips share, as a track did in 4.2.0: every value of a layer that <see cref="Layer.IsClipProperty"/> does not
    /// name (where they sit on the frame and their size, crop, look, filters, key, mask, edges, shadow). Each clip's
    /// <see cref="TimelineClip.State"/> holds what is its own. Its timing fields are not used. Null: none, and each clip's
    /// State holds everything.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerState? TrackLook { get; set; }

    /// <summary>
    /// The clips in time: by <see cref="TimelineClip.Start"/>, and apart (one may start where the one before it ends), so
    /// the ends are in order too. A crossfade does not overlap them: it is the later clip's <see cref="TimelineClip.TransitionIn"/>.
    /// </summary>
    public List<TimelineClip> Clips { get; set; } = [];

    /// <summary>
    /// The clips that show in the stretch from <paramref name="viewStart"/> to <paramref name="viewEnd"/> (seconds), found
    /// by two binary searches: what a timeline draws, without looking at the clips off screen. A view onto
    /// <see cref="Clips"/>: read it before the track is changed again.
    /// </summary>
    public ReadOnlySpan<TimelineClip> ClipsIntersecting(double viewStart, double viewEnd)
    {
        var clips = CollectionsMarshal.AsSpan(Clips);
        if (clips.IsEmpty || !(viewEnd > viewStart))
            return [];

        // The first clip still showing at the view's start (ends are in order), and the first starting after its end.
        int low = 0, high = clips.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (clips[middle].End > viewStart)
                high = middle;
            else
                low = middle + 1;
        }

        int first = low, last = clips.Length;
        while (low < last)
        {
            var middle = (low + last) >>> 1;
            if (clips[middle].Start < viewEnd)
                low = middle + 1;
            else
                last = middle;
        }

        return clips[first..last];
    }

    /// <summary>Where a clip is in <see cref="Clips"/> (a binary search by its start), or -1 when it is not on this track.</summary>
    public int IndexOf(TimelineClip clip)
    {
        var index = Find(clip);

        // Not where its start says: a list put in order by hand. Looked for the long way.
        return index >= 0 ? index : Clips.IndexOf(clip);
    }

    /// <summary>Where a clip is, by its start only: -1 when it is not on this track or not where its start says.</summary>
    internal int Find(TimelineClip clip)
    {
        var clips = CollectionsMarshal.AsSpan(Clips);
        int low = 0, high = clips.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (clips[middle].Start < clip.Start)
                low = middle + 1;
            else
                high = middle;
        }

        for (var i = low; i < clips.Length && clips[i].Start == clip.Start; i++)
        {
            if (ReferenceEquals(clips[i], clip))
                return i;
        }
        return -1;
    }

    /// <summary>The kind of track a clip of this content goes on.</summary>
    public static TimelineTrackKind KindFor(LayerKind content) => content switch
    {
        LayerKind.Audio => TimelineTrackKind.Audio,
        LayerKind.Adjustment => TimelineTrackKind.Adjustment,
        LayerKind.Captions => TimelineTrackKind.Subtitle,
        _ => TimelineTrackKind.Video,
    };

    /// <summary>Whether a clip can go on this track: one of its kind (a switch track takes what a video track takes).</summary>
    public bool Accepts(TimelineClip clip) =>
        KindFor(clip.ContentKind) is var kind && (kind == Kind || (Kind == TimelineTrackKind.Switch && kind == TimelineTrackKind.Video));

    /// <summary>A copy of its own values, sharing its clip list and look: what a rollback puts back (its clips are put back apart).</summary>
    internal TimelineTrackModel Snapshot() => (TimelineTrackModel)MemberwiseClone();

    /// <summary>Takes every value of another track but its clips (its id too): a rollback, in place, so whoever holds this track keeps it.</summary>
    internal void RestoreFields(TimelineTrackModel from)
    {
        (Id, TrackNumber, Name, Kind) = (from.Id, from.TrackNumber, from.Name, from.Kind);
        (IsHidden, IsMuted, IsSoloed, IsLocked) = (from.IsHidden, from.IsMuted, from.IsSoloed, from.IsLocked);
        (GainDb, DuckAgainstVoice, DuckAmountDb, AudioFilters, TrackLook) = (from.GainDb, from.DuckAgainstVoice, from.DuckAmountDb, from.AudioFilters, from.TrackLook);
    }
}

/// <summary>
/// One clip on a track: what it plays, when, which part of its file, its fades, and its look. Everything the main
/// video's clips, cut segments, layers and audio pieces were, in one shape.
/// </summary>
public sealed class TimelineClip
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What it shows: a video file, an image, text, an adjustment, a sound...</summary>
    public LayerKind ContentKind { get; set; } = LayerKind.VideoFile;

    /// <summary>Its file; null for a clip with none (text, an adjustment).</summary>
    public string? SourcePath { get; set; }

    public int VideoStreamIndex { get; set; }

    /// <summary>Which audio stream of the file it plays; -1 for none.</summary>
    public int AudioStreamIndex { get; set; } = -1;

    // ----- When: seconds on the sequence, on frames while the document quantizes -----
    // Both edges are kept, and the length is what lies between them: a clip that ends where the next starts holds the
    // very double the next one starts on. (A start and a length cannot promise that: some ends are not the sum of the
    // start and any double.)

    public double Start { get; set; }
    public double End { get; set; }

    [JsonIgnore]
    public double Duration => End - Start;

    /// <summary>Seconds into the file where it begins.</summary>
    public double MediaOffset { get; set; }

    /// <summary>How many times as fast as recorded it runs: a second on the sequence plays this many of the file.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>How long the file is, in its own seconds; 0 for a still (an image, text), which lasts as long as it is made to.</summary>
    public double SourceDuration { get; set; }

    /// <summary>Where it ends in the file.</summary>
    [JsonIgnore]
    public double MediaEnd => MediaOffset + Duration * Speed;

    [JsonIgnore]
    public bool HasSound => AudioStreamIndex >= 0 || ContentKind == LayerKind.Audio;

    /// <summary>
    /// It plays its own file through in time (a video file, a sound), so its edges move its <see cref="MediaOffset"/> and
    /// stop at its file's ends, as in 4.2.0. Images, text and adjustments have no file to run out of, and a cut-out of a
    /// video (<see cref="LayerKind.Video"/>) or the blurred background shows what the video under it shows.
    /// </summary>
    [JsonIgnore]
    public bool PlaysFile => ContentKind is LayerKind.VideoFile or LayerKind.Audio or LayerKind.MainVideo;

    // ----- Its sound against its picture -----

    /// <summary>Whether its sound moves and trims with its picture (as one clip), or on its own.</summary>
    public bool AudioLinked { get; set; } = true;

    /// <summary>How far its sound is moved from its picture, in seconds, while unlinked (later when positive). Not on frames.</summary>
    public double AudioSlipSeconds { get; set; }

    /// <summary>
    /// How much sooner than its picture its sound starts, in seconds: a J-cut (negative: the sound starts later).
    /// The sound takes the file from that much earlier.
    /// </summary>
    public double AudioLeadInSeconds { get; set; }

    /// <summary>How much longer than its picture its sound lasts, in seconds: an L-cut (negative: it stops sooner).</summary>
    public double AudioTailOutSeconds { get; set; }

    // ----- Fades -----

    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public TransitionStyle FadeInStyle { get; set; }
    public TransitionStyle FadeOutStyle { get; set; }

    /// <summary>
    /// A crossfade from the clip before it on the track, in seconds (0: a plain cut), across the cut where they meet: centred
    /// on it, each file giving up to half of it from beyond its edge when the graph is built, or a dip through black where
    /// they have nothing to give. At most <see cref="TimelineDocument.LongestCrossfade"/>. Set to 0 by any edit after
    /// which the clip no longer meets the one before it.
    /// </summary>
    public double TransitionIn { get; set; }

    // ----- Sound, visibility, playback -----

    public double GainDb { get; set; }
    public bool IsHidden { get; set; }
    public bool IsMuted { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ProxyChoice ProxyChoice { get; set; }

    /// <summary>
    /// Its look, as a layer is saved, so everything a <see cref="Layer"/> and an <see cref="EffectInstance"/> do works on it
    /// unchanged: its name, keyframes (seconds from the clip's start; they may lie before it or past it, so a motion
    /// carries across a cut), effect stack and the rest of what <see cref="Layer.IsClipProperty"/> names, and, when its
    /// track has no <see cref="TimelineTrackModel.TrackLook"/>, everything else too (place on the frame, crop, mask...).
    /// Its timing, fades, gain, visibility and proxy fields are not used: the clip's own above are. An edit that
    /// changes it puts a changed copy here; it never changes the one that is here.
    /// </summary>
    public LayerState State { get; set; } = new();

    /// <summary>A copy with the same id and everything the same, sharing <see cref="State"/>: what a rollback puts back.</summary>
    internal TimelineClip Snapshot() => (TimelineClip)MemberwiseClone();

    /// <summary>Whether another clip has every value this one has (its look the very same object): a change that changed nothing.</summary>
    internal bool SameValues(TimelineClip other) =>
        Id == other.Id && ContentKind == other.ContentKind && SourcePath == other.SourcePath && VideoStreamIndex == other.VideoStreamIndex
        && AudioStreamIndex == other.AudioStreamIndex && Start == other.Start && End == other.End && MediaOffset == other.MediaOffset
        && Speed == other.Speed && SourceDuration == other.SourceDuration && AudioLinked == other.AudioLinked
        && AudioSlipSeconds == other.AudioSlipSeconds && AudioLeadInSeconds == other.AudioLeadInSeconds && AudioTailOutSeconds == other.AudioTailOutSeconds
        && FadeInSeconds == other.FadeInSeconds && FadeOutSeconds == other.FadeOutSeconds && FadeInStyle == other.FadeInStyle
        && FadeOutStyle == other.FadeOutStyle && TransitionIn == other.TransitionIn && GainDb == other.GainDb && IsHidden == other.IsHidden
        && IsMuted == other.IsMuted && ProxyChoice == other.ProxyChoice && ReferenceEquals(State, other.State);

    /// <summary>Takes every value of another clip (its id too): a rollback, in place, so whoever holds this clip keeps it.</summary>
    internal void Restore(TimelineClip from)
    {
        Id = from.Id;
        ContentKind = from.ContentKind;
        SourcePath = from.SourcePath;
        VideoStreamIndex = from.VideoStreamIndex;
        AudioStreamIndex = from.AudioStreamIndex;
        Start = from.Start;
        End = from.End;
        MediaOffset = from.MediaOffset;
        Speed = from.Speed;
        SourceDuration = from.SourceDuration;
        AudioLinked = from.AudioLinked;
        AudioSlipSeconds = from.AudioSlipSeconds;
        AudioLeadInSeconds = from.AudioLeadInSeconds;
        AudioTailOutSeconds = from.AudioTailOutSeconds;
        FadeInSeconds = from.FadeInSeconds;
        FadeOutSeconds = from.FadeOutSeconds;
        FadeInStyle = from.FadeInStyle;
        FadeOutStyle = from.FadeOutStyle;
        TransitionIn = from.TransitionIn;
        GainDb = from.GainDb;
        IsHidden = from.IsHidden;
        IsMuted = from.IsMuted;
        ProxyChoice = from.ProxyChoice;
        State = from.State;
    }
}

/// <summary>
/// What a committed transaction changed: its name, the ids of the clips it made, changed or removed, and of the tracks
/// it made, numbered again or whose clips it changed.
/// </summary>
public sealed record TimelineCommit(string Label, IReadOnlyList<Guid> TouchedClipIds, IReadOnlyList<Guid> TouchedTrackIds);

/// <summary>
/// A stretch folded out of the timeline (4.3D: Remove Dead Air's Split &amp; Mark): what lay there on each track it was taken
/// out of, kept to be put back (<see cref="ITimelineTransaction.UnfoldSegment"/>). It belongs to the cut it was folded at,
/// the seam: the start of the clip that came after it (<see cref="AtEnd"/>: the end of the one before, when none came after)
/// on the track it was found on, which every edit carries along. A fold whose clip is gone has no seam and is passed over.
/// What is on the timeline is what renders: a fold takes no time and shows nothing until it is put back.
/// </summary>
/// <param name="SeamClipId">The clip whose edge the seam is.</param>
/// <param name="AtEnd">The seam is that clip's end rather than its start.</param>
/// <param name="AllTracks">It was taken out of every unlocked track (the main video's dead air), not its own track alone.</param>
/// <param name="Length">How long the stretch was, in seconds of the timeline.</param>
/// <param name="Clips">What lay in it, each with its track's id: copies, their times counted from the seam (the stretch's start).</param>
public sealed record FoldedSegment(Guid Id, Guid SeamClipId, bool AtEnd, bool AllTracks, double Length, IReadOnlyList<FoldedClip> Clips)
{
    /// <summary>Where the seam is now: the edge of its clip in the document, or null when that clip is gone.</summary>
    public double? SeamIn(TimelineDocument document) =>
        document.FindClip(SeamClipId) is { } clip ? AtEnd ? clip.End : clip.Start : null;
}

/// <summary>A clip kept in a fold (<see cref="FoldedSegment"/>): a copy nobody changes, its times counted from the seam.</summary>
public sealed record FoldedClip(Guid TrackId, TimelineTrackKind TrackKind, TimelineClip Clip);

/// <summary>The whole sequence: its frame, its rate, its tracks (bottom first) and what lies over all of them.</summary>
public sealed class TimelineDocument
{
    /// <summary>The shortest a clip is made by an edit (as 4.2.0's layer edits keep it), in whole frames while quantizing.</summary>
    public const double MinimumClipSeconds = 0.05;

    /// <summary>The longest crossfade a clip dropped over its neighbour makes, and the overlap it may be dropped with (at most half of either clip).</summary>
    public const double LongestDragCrossfade = 2;

    /// <summary>The longest same-track crossfade, and the longest fade.</summary>
    public const double LongestCrossfade = 10, LongestFade = 60;

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool UseVerticalResolution { get; set; }

    public FrameRate FrameRate { get; set; } = FrameRate.Fps60;

    /// <summary>Every edge lands on a frame of <see cref="FrameRate"/> (an edit can ask to be left free).</summary>
    public bool QuantizeToFrames { get; set; } = true;

    /// <summary>The tracks in their stacking order, bottom first.</summary>
    public List<TimelineTrackModel> Tracks { get; set; } = [];

    /// <summary>The Sequence Output's effect stack: on the finished frame. Keyframe times are seconds of the sequence.</summary>
    public List<EffectState> SequenceEffects { get; set; } = [];

    public bool SequenceEffectsBypassed { get; set; }

    public List<TimelineMarker> Markers { get; set; } = [];

    /// <summary>The stretches folded out of the timeline, each at its seam (4.3D); null while there are none, and so left out of the file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FoldedSegment>? Folds { get; set; }

    /// <summary>The video tracks, bottom first: track 1 is the first.</summary>
    [JsonIgnore]
    public IReadOnlyList<TimelineTrackModel> VideoTracks => TracksOf(TimelineTrackKind.Video);

    [JsonIgnore]
    public IReadOnlyList<TimelineTrackModel> AudioTracks => TracksOf(TimelineTrackKind.Audio);

    public IReadOnlyList<TimelineTrackModel> TracksOf(TimelineTrackKind kind) => Tracks.Where(t => t.Kind == kind).ToList();

    /// <summary>
    /// How long the sequence is: where the last clip ends, its sound included (an L-cut lasts past its picture, and so does
    /// an unlinked sound slipped later). The timeline's own length, which the canvas of an export is: a clip switched off
    /// or silenced, on a track hidden or muted, still holds its place on it (so hiding the last clip's picture never cuts
    /// its sound, or the export, short). Only the captions' box does not count: it lasts as long as the rest, and does not
    /// ripple. (4.3B: 4.3A left hidden and muted things out, which 4.2.0's sequence never did.)
    /// </summary>
    [JsonIgnore]
    public double TotalDuration
    {
        get
        {
            var end = 0.0;
            foreach (var track in Tracks)
            {
                if (track.Kind == TimelineTrackKind.Subtitle)
                    continue;

                foreach (var clip in track.Clips)
                {
                    var sound = !clip.HasSound ? 0 : clip.AudioLinked ? clip.AudioTailOutSeconds : clip.AudioSlipSeconds;
                    end = Math.Max(end, clip.End + Math.Max(sound, 0));
                }
            }
            return end;
        }
    }

    /// <summary>The track a clip is on, or null when it is on none of them (a binary search on each track, then the long way).</summary>
    public TimelineTrackModel? TrackOf(TimelineClip clip)
    {
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

    /// <summary>
    /// A copy that nothing done to this one reaches: its own tracks, clips, markers and sequence effects, the same ids.
    /// The clips' looks (<see cref="TimelineClip.State"/>) and the tracks' are shared, which is safe because no edit
    /// changes a look in place (it puts a changed copy on the clip); <paramref name="deep"/> copies them as well, for a
    /// copy that is itself changed in place (a project file's looks get their masks' keys, say). The copy has no
    /// subscribers and no open transactions.
    /// </summary>
    public TimelineDocument Clone(bool deep = false)
    {
        LayerState? Look(LayerState? look) => look is null ? null : deep ? TimelineTransaction.CopyOf(look) : look;

        var copy = new TimelineDocument
        {
            Width = Width,
            Height = Height,
            UseVerticalResolution = UseVerticalResolution,
            FrameRate = FrameRate,
            QuantizeToFrames = QuantizeToFrames,
            SequenceEffects = deep ? [.. SequenceEffects.Select(e => System.Text.Json.JsonSerializer.Deserialize<EffectState>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e, TimelineTransaction.CopyOptions), TimelineTransaction.CopyOptions)!)] : [.. SequenceEffects],
            SequenceEffectsBypassed = SequenceEffectsBypassed,
            Markers = [.. Markers],
            Folds = Folds is null ? null : [.. Folds],
        };
        foreach (var track in Tracks)
        {
            var made = track.Snapshot();
            made.Clips = new List<TimelineClip>(track.Clips.Count);
            made.TrackLook = Look(track.TrackLook);
            made.AudioFilters = deep ? track.AudioFilters?.Clone() : track.AudioFilters;
            foreach (var clip in track.Clips)
            {
                var clipCopy = clip.Snapshot();
                clipCopy.State = Look(clip.State) ?? new LayerState();
                made.Clips.Add(clipCopy);
            }
            copy.Tracks.Add(made);
        }
        return copy;
    }

    public TimelineClip? FindClip(Guid id)
    {
        foreach (var track in Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.Id == id)
                    return clip;
            }
        }
        return null;
    }

    // ----- Changes -----

    /// <summary>The transactions open now, the outermost first (see <see cref="BeginTransaction"/>).</summary>
    internal readonly List<TimelineTransaction> OpenTransactions = [];

    /// <summary>
    /// Raised once for each outermost transaction committed with something changed: what Undo keeps a step of
    /// (4.3C) and what views redraw from.
    /// </summary>
    public event Action<TimelineCommit>? Committed;

    internal void RaiseCommitted(TimelineCommit commit) => Committed?.Invoke(commit);

    /// <summary>
    /// Opens a change. Edit through it, then <see cref="ITimelineTransaction.Commit"/>; disposing it uncommitted puts
    /// everything it changed back. One opened inside another folds into it (and rolls back only its own part).
    /// </summary>
    public ITimelineTransaction BeginTransaction(string label) => new TimelineTransaction(this, label);

    // ----- What must always be true -----

    /// <summary>
    /// How far past its file's end a clip may read and still count as inside it: a quantized edge is within half a
    /// microsecond of its frame, so a clip's length moves by up to a microsecond as it moves, and its file by that times
    /// its speed; a clip at its file's end may land a little over a microsecond out once or twice.
    /// </summary>
    internal static double FileSlack(TimelineClip clip) => 4e-6 * Math.Max(1, clip.Speed);

    /// <summary>
    /// What is wrong with the document, in words (empty when nothing is): clips out of order or overlapping, crossfades
    /// where clips do not meet, clips on a track of the wrong kind, tracks numbered out of order, numbers that are not
    /// numbers, lengths, speeds, fades and file offsets out of range, and sound (a J or L cut) that starts before the
    /// timeline or reads outside its file. With <paramref name="requireFrames"/>, also edges off the frames while the
    /// document quantizes (an edit asked to be free may leave one). For tests, and for checking a project file written by hand.
    /// </summary>
    public IReadOnlyList<string> FindProblems(bool requireFrames = false)
    {
        const double slack = 1e-6;
        var problems = new List<string>();
        void Add(FormattableString problem) => problems.Add(problem.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!FrameRate.IsValid)
            Add($"Frame rate {FrameRate.Numerator}/{FrameRate.Denominator} is not positive.");

        var ids = new HashSet<Guid>();
        var tracks = new HashSet<Guid>();
        var numbers = new Dictionary<TimelineTrackKind, int>();
        foreach (var track in Tracks)
        {
            var name = track.DisplayName;
            if (!tracks.Add(track.Id))
                Add($"{name}: its id {track.Id} is used twice.");
            numbers[track.Kind] = numbers.GetValueOrDefault(track.Kind) + 1;
            if (track.TrackNumber != numbers[track.Kind])
                Add($"{name}: numbered {track.TrackNumber}, but it is {track.Kind} track {numbers[track.Kind]} from the bottom.");

            for (var i = 0; i < track.Clips.Count; i++)
            {
                var clip = track.Clips[i];
                var at = $"{name}, clip {i + 1}";
                if (!ids.Add(clip.Id))
                    Add($"{at}: its id {clip.Id} is used twice.");
                if (!track.Accepts(clip))
                    Add($"{at}: a {clip.ContentKind} clip on a {track.Kind} track.");

                double[] values = [clip.Start, clip.End, clip.MediaOffset, clip.Speed, clip.SourceDuration, clip.AudioSlipSeconds, clip.AudioLeadInSeconds,
                                   clip.AudioTailOutSeconds, clip.FadeInSeconds, clip.FadeOutSeconds, clip.TransitionIn, clip.GainDb];
                if (!values.All(double.IsFinite))
                {
                    Add($"{at}: a time or level that is not a number.");
                    continue;
                }

                if (clip.Start < -slack)
                    Add($"{at}: starts at {clip.Start}.");
                if (clip.Duration <= 0)
                    Add($"{at}: ends at {clip.End}, {clip.Duration} s after it starts.");
                if (!(clip.Speed >= Layer.SlowestSpeed && clip.Speed <= Layer.FastestSpeed))
                    Add($"{at}: its speed {clip.Speed} is outside {Layer.SlowestSpeed} to {Layer.FastestSpeed}.");
                if (!(clip.FadeInSeconds is >= 0 and <= LongestFade) || !(clip.FadeOutSeconds is >= 0 and <= LongestFade))
                    Add($"{at}: fades of {clip.FadeInSeconds} and {clip.FadeOutSeconds} s (0 to {LongestFade}).");
                if (!(clip.TransitionIn is >= 0 and <= LongestCrossfade))
                    Add($"{at}: a crossfade of {clip.TransitionIn} s (0 to {LongestCrossfade}).");
                if (requireFrames && QuantizeToFrames && (!FrameRate.IsOnFrame(clip.Start) || !FrameRate.IsOnFrame(clip.End)))
                    Add($"{at}: from {clip.Start} to {clip.End} is not on frames of {FrameRate}.");

                var fileSlack = FileSlack(clip);
                var (lead, tail) = clip.HasSound && clip.AudioLinked ? (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) : (0, 0);
                if (clip.Start - lead < -slack)
                    Add($"{at}: its sound starts {lead} s before its picture, before the timeline's start.");
                if (clip.Duration + lead + tail <= 0)
                    Add($"{at}: its sound has no length (from {lead} s before its picture to {tail} s after).");
                if (clip.PlaysFile)
                {
                    if (clip.MediaOffset - Math.Max(lead, 0) * clip.Speed < -fileSlack)
                        Add($"{at}: begins {clip.MediaOffset - Math.Max(lead, 0) * clip.Speed} s into its file, its sound's lead included.");
                    var end = clip.MediaEnd + Math.Max(tail, 0) * clip.Speed;
                    if (clip.SourceDuration > 0 && end > clip.SourceDuration + fileSlack)
                        Add($"{at}: ends {end} s into a file of {clip.SourceDuration} s, its sound's tail included.");
                }

                if (i == 0)
                {
                    if (clip.TransitionIn != 0)
                        Add($"{at}: a crossfade of {clip.TransitionIn} s from no clip.");
                    continue;
                }

                var before = track.Clips[i - 1];
                if (clip.Start < before.End - slack)
                    Add($"{at}: starts at {clip.Start}, before the clip before it ends ({before.End}).");
                else if (clip.TransitionIn != 0 && clip.Start > before.End + slack)
                    Add($"{at}: a crossfade of {clip.TransitionIn} s, but it does not meet the clip before it.");
            }
        }
        return problems;
    }
}