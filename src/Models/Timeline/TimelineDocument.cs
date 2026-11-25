using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

// ----- Sequence document model -----
// A Sequence is tracks of clips with no privileged track; all clips are edited uniformly. Times are Sequence seconds; while
// quantizing, every edge lies on a frame (FrameRate.Quantize) so abutting clips share one exact double. Clips on a track never
// overlap: a same-track crossfade is the later clip's TransitionIn centred on the cut, built from source handles.
// All edits go through a transaction (TimelineTransaction.cs) that keeps tracks sorted and non-overlapping and rolls back atomically.

/// <summary>What a track holds. Saved by name in projects: new kinds are added at the end, and none is renamed.</summary>
public enum TimelineTrackKind
{
    /// <summary>Pictures: video files (a Cutout is one, a child of the clip it is cut from), images, text.</summary>
    Video,

    /// <summary>Sound with no picture of its own: a file's audio stream, music, a voice-over.</summary>
    Audio,

    /// <summary>EasyCut switch over the tracks below it; accepts the same clips as a video track.</summary>
    Switch,

    /// <summary>Adjustment clips: no pixels of their own; their effect stacks apply to everything below for their duration.</summary>
    Adjustment,

    /// <summary>The captions' box.</summary>
    Subtitle,
}

/// <summary>One track of a <see cref="TimelineDocument"/>: its clips in time, and what they share.</summary>
public sealed class TimelineTrackModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

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
    /// Parent track for a mini-stack (preset Cutout/mask tracks, caption lane), or null. Ripples and folds with the parent
    /// (<see cref="TimelineDocument.ChildTracksOf"/>); a missing parent makes it top-level. Omitted from JSON while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentTrackId { get; set; }

    /// <summary>
    /// Captions render directly beneath this track's pixels (above all tracks below); with no such track they render on top of
    /// everything including fades. At most one track (<see cref="ITimelineTransaction.SetCaptionsUnder"/>); deleting it moves the flag
    /// to the next picture track down (<see cref="ITimelineTransaction.RemoveTrack"/>). Omitted from JSON while false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CaptionsUnder { get; set; }

    /// <summary>
    /// Per-track row height (DIP) set by dragging its header edge; null uses the global Track Height. Omitted from JSON while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Height { get; set; }

    /// <summary>
    /// Audio Sub-Lane stream ordinal under a video track: 0 = each clip's first audio stream, 1 = second, ... (ordinal, not file
    /// stream index), so equal streams align on one row. -1 for a lane holding one detached stream (MainViewModel.MoveSoundToOwnLane).
    /// Null for other tracks. Omitted from JSON while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StreamOrdinal { get; set; }

    /// <summary>
    /// Track Preset: Game Preset name (Styles folder) applied to clips on this track, or null. Clips dropped or moved here take its
    /// placement, look, blur fill and Cutouts (MainViewModel.InheritTemplate); setting it applies to existing clips too. Picture tracks
    /// only. Omitted from JSON while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresetNodeName { get; set; }

    /// <summary>
    /// Legacy shared track look from old documents; new documents keep the full look in each <see cref="TimelineClip.State"/>.
    /// Timing fields unused. Null means each clip's State is complete.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerState? TrackLook { get; set; }

    /// <summary>
    /// Clips sorted by <see cref="TimelineClip.Start"/> and non-overlapping (abutting allowed), so ends are sorted too. Crossfades are
    /// the later clip's <see cref="TimelineClip.TransitionIn"/>, not overlaps.
    /// </summary>
    public List<TimelineClip> Clips { get; set; } = [];

    /// <summary>
    /// Clips intersecting [<paramref name="viewStart"/>, <paramref name="viewEnd"/>] (seconds) via two binary searches. Returns a live
    /// view of <see cref="Clips"/>; consume it before the track changes.
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

    /// <summary>True when the clip kind fits this track kind (switch tracks accept video-track clips).</summary>
    public bool Accepts(TimelineClip clip) =>
        KindFor(clip.ContentKind) is var kind && (kind == Kind || (Kind == TimelineTrackKind.Switch && kind == TimelineTrackKind.Video));

    /// <summary>Shallow rollback snapshot sharing the clip list and look (clips are restored separately).</summary>
    internal TimelineTrackModel Snapshot() => (TimelineTrackModel)MemberwiseClone();

    /// <summary>In-place rollback of every field except clips (including the id), preserving reference identity.</summary>
    internal void RestoreFields(TimelineTrackModel from)
    {
        (Id, TrackNumber, Name, Kind) = (from.Id, from.TrackNumber, from.Name, from.Kind);
        (IsHidden, IsMuted, IsSoloed, IsLocked) = (from.IsHidden, from.IsMuted, from.IsSoloed, from.IsLocked);
        (GainDb, DuckAgainstVoice, DuckAmountDb, AudioFilters, TrackLook) = (from.GainDb, from.DuckAgainstVoice, from.DuckAmountDb, from.AudioFilters, from.TrackLook);
        (ParentTrackId, CaptionsUnder, Height, StreamOrdinal, PresetNodeName) = (from.ParentTrackId, from.CaptionsUnder, from.Height, from.StreamOrdinal, from.PresetNodeName);
    }
}

/// <summary>
/// A clip on a track: source, Sequence timing, source range, fades and look. One shape for every clip kind.
/// </summary>
public sealed class TimelineClip
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Unknown JSON fields from newer versions, preserved on save. Read-only after load: <see cref="Snapshot"/> and
    /// <see cref="Restore"/> share the dictionary.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>What it shows: a video file, an image, text, an adjustment, a sound...</summary>
    public LayerKind ContentKind { get; set; } = LayerKind.VideoFile;

    /// <summary>Its file; null for a clip with none (text, an adjustment).</summary>
    public string? SourcePath { get; set; }

    public int VideoStreamIndex { get; set; }

    /// <summary>Which audio stream of the file it plays; -1 for none.</summary>
    public int AudioStreamIndex { get; set; } = -1;

    // ----- Timing: Sequence seconds, frame-aligned while quantizing -----
    // Both edges are stored and the length derived, so abutting clips share the exact same double (start + length cannot
    // guarantee that under floating-point rounding).

    public double Start { get; set; }
    public double End { get; set; }

    [JsonIgnore]
    public double Duration => End - Start;

    /// <summary>Seconds into the file where it begins.</summary>
    public double MediaOffset { get; set; }

    /// <summary>How many times as fast as recorded it runs: a second on the sequence plays this many of the file.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Source duration in file seconds; 0 for stills (image, text), which have arbitrary length.</summary>
    public double SourceDuration { get; set; }

    /// <summary>
    /// Video stream duration when shorter than <see cref="SourceDuration"/> (audio padded past the last frame); 0 when unknown or
    /// equal. Omitted from JSON while 0.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double PictureDuration { get; set; }

    /// <summary>
    /// Whole-file clip length at 1x: <see cref="PictureDuration"/> when known and shorter, else <see cref="SourceDuration"/>.
    /// </summary>
    [JsonIgnore]
    public double WholeDuration => PictureDuration > 0 && SourceDuration > 0 ? Math.Min(PictureDuration, SourceDuration) : SourceDuration;

    /// <summary>Where it ends in the file.</summary>
    [JsonIgnore]
    public double MediaEnd => MediaOffset + Duration * Speed;

    [JsonIgnore]
    public bool HasSound => AudioStreamIndex >= 0 || ContentKind == LayerKind.Audio;

    /// <summary>
    /// True for time-based media (video, audio): trims shift <see cref="MediaOffset"/> and clamp at the file ends. Images, text and
    /// adjustments are unbounded. Legacy <see cref="LayerKind.MainVideo"/> also counts until <c>LegacyMainVideo.Migrate</c> converts it.
    /// </summary>
    [JsonIgnore]
    public bool PlaysFile => ContentKind is LayerKind.VideoFile or LayerKind.Audio or LayerKind.MainVideo;

    // ----- Its sound against its picture -----

    /// <summary>Whether its sound moves and trims with its picture (as one clip), or on its own.</summary>
    public bool AudioLinked { get; set; } = true;

    /// <summary>Audio offset from video in seconds while unlinked (positive = later); not frame-quantized.</summary>
    public double AudioSlipSeconds { get; set; }

    /// <summary>
    /// J-cut lead: seconds the audio starts before the video (negative = later), reading the source that much earlier.
    /// </summary>
    public double AudioLeadInSeconds { get; set; }

    /// <summary>L-cut tail: seconds the audio extends past the video (negative = ends sooner).</summary>
    public double AudioTailOutSeconds { get; set; }

    // ----- Fades -----

    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public TransitionStyle FadeInStyle { get; set; }
    public TransitionStyle FadeOutStyle { get; set; }

    /// <summary>
    /// Crossfade length (s) from the previous abutting clip, centred on the cut; each side supplies up to half from its handles,
    /// else dips through black. Max <see cref="TimelineDocument.LongestCrossfade"/>; reset to 0 when the clips no longer abut.
    /// </summary>
    public double TransitionIn { get; set; }

    // ----- Sound, visibility, playback -----

    public double GainDb { get; set; }
    public bool IsHidden { get; set; }
    public bool IsMuted { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ProxyChoice ProxyChoice { get; set; }

    /// <summary>
    /// Parent clip (Cutout, mask, Sub-Lane stream), or null. While overlapping the parent, it follows the parent's splits, trims,
    /// moves, deletes and folds (<see cref="TimelineDocument.ChildrenOf"/>); a missing parent makes it independent. Omitted while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentClipId { get; set; }

    /// <summary>
    /// User tether (MainViewModel.AttachClip): follows the parent in time but is never treated as a Game Preset piece, so presets,
    /// Track Presets and Cutouts applied to the parent never delete, copy or move it. Cleared with <see cref="ParentClipId"/>; kept
    /// when re-parented. Omitted while false (<see cref="TimelineDocument.TethersMarked"/> covers legacy files).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Tethered { get; set; }

    /// <summary>
    /// Detected beats (BeatDetector.cs), ascending, in media seconds like <see cref="MediaOffset"/>, so edits and speed changes keep
    /// them aligned; null until detected (omitted from JSON). Immutable: edits replace the array (<see cref="ITimelineTransaction.SetClipBeats"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? BeatTimes { get; set; }

    /// <summary>
    /// Clip look stored as a <see cref="Layer"/> (name, keyframes relative to clip start and allowed outside it, effect stack, and,
    /// without a <see cref="TimelineTrackModel.TrackLook"/>, placement, crop, mask). Its timing, fade, gain, visibility and proxy
    /// fields are ignored. Copy-on-write: edits replace the instance.
    /// </summary>
    public LayerState State { get; set; } = new();

    /// <summary>Rollback snapshot with the same id, sharing <see cref="State"/>.</summary>
    internal TimelineClip Snapshot() => (TimelineClip)MemberwiseClone();

    /// <summary>True when every field matches and <see cref="State"/> is the same instance (a no-op change).</summary>
    internal bool SameValues(TimelineClip other) =>
        Id == other.Id && ContentKind == other.ContentKind && SourcePath == other.SourcePath && VideoStreamIndex == other.VideoStreamIndex
        && AudioStreamIndex == other.AudioStreamIndex && Start == other.Start && End == other.End && MediaOffset == other.MediaOffset
        && Speed == other.Speed && SourceDuration == other.SourceDuration && PictureDuration == other.PictureDuration && AudioLinked == other.AudioLinked
        && AudioSlipSeconds == other.AudioSlipSeconds && AudioLeadInSeconds == other.AudioLeadInSeconds && AudioTailOutSeconds == other.AudioTailOutSeconds
        && FadeInSeconds == other.FadeInSeconds && FadeOutSeconds == other.FadeOutSeconds && FadeInStyle == other.FadeInStyle
        && FadeOutStyle == other.FadeOutStyle && TransitionIn == other.TransitionIn && GainDb == other.GainDb && IsHidden == other.IsHidden
        && IsMuted == other.IsMuted && ProxyChoice == other.ProxyChoice && ParentClipId == other.ParentClipId && Tethered == other.Tethered
        && ReferenceEquals(BeatTimes, other.BeatTimes) && ReferenceEquals(State, other.State) && ReferenceEquals(Extra, other.Extra);

    /// <summary>In-place rollback of every field (including the id), preserving reference identity.</summary>
    internal void Restore(TimelineClip from)
    {
        Id = from.Id;
        Extra = from.Extra;
        ContentKind = from.ContentKind;
        SourcePath = from.SourcePath;
        VideoStreamIndex = from.VideoStreamIndex;
        AudioStreamIndex = from.AudioStreamIndex;
        Start = from.Start;
        End = from.End;
        MediaOffset = from.MediaOffset;
        Speed = from.Speed;
        SourceDuration = from.SourceDuration;
        PictureDuration = from.PictureDuration;
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
        ParentClipId = from.ParentClipId;
        Tethered = from.Tethered;
        BeatTimes = from.BeatTimes;
        State = from.State;
    }
}

/// <summary>
/// Committed transaction summary: label, and ids of clips added/changed/removed and tracks added/renumbered/changed.
/// </summary>
public sealed record TimelineCommit(string Label, IReadOnlyList<Guid> TouchedClipIds, IReadOnlyList<Guid> TouchedTrackIds);

/// <summary>
/// Range folded out of the Sequence (Remove Dead Air), holding the removed clips per track for <see cref="ITimelineTransaction.UnfoldSegment"/>.
/// Anchored to a seam: the start of the following clip (or with <see cref="AtEnd"/> the end of the preceding one), which edits carry.
/// A fold whose seam clip is gone is skipped. Folds occupy no time and do not render until unfolded.
/// </summary>
/// <param name="SeamClipId">The clip whose edge the seam is.</param>
/// <param name="AtEnd">The seam is that clip's end rather than its start.</param>
/// <param name="AllTracks">Folded from every unlocked track rather than only the seam's track.</param>
/// <param name="Length">Folded length in Sequence seconds.</param>
/// <param name="Clips">Removed clip copies with their track ids, times relative to the seam.</param>
public sealed record FoldedSegment(Guid Id, Guid SeamClipId, bool AtEnd, bool AllTracks, double Length, IReadOnlyList<FoldedClip> Clips)
{
    /// <summary>Where the seam is now: the edge of its clip in the document, or null when that clip is gone.</summary>
    public double? SeamIn(TimelineDocument document) =>
        document.FindClip(SeamClipId) is { } clip ? AtEnd ? clip.End : clip.Start : null;
}

/// <summary>Immutable clip copy inside a <see cref="FoldedSegment"/>, times relative to the seam.</summary>
public sealed record FoldedClip(Guid TrackId, TimelineTrackKind TrackKind, TimelineClip Clip);

/// <summary>
/// Side of a <see cref="FoldedSegment"/> that a seam-adjacent clip edge extends into or folds further (<see cref="ITimelineTransaction.ResizeFold"/>).
/// </summary>
public enum FoldSide
{
    /// <summary>Leading part, adjacent to the clip before the seam (resized by that clip's end).</summary>
    Head,

    /// <summary>Trailing part, adjacent to the clip after the seam (resized by that clip's start).</summary>
    Tail,
}

/// <summary>Clip edge anchoring a <see cref="RestoredFold"/>: its start, or its end with <paramref name="AtEnd"/>.</summary>
public sealed record FoldEdge(Guid ClipId, bool AtEnd)
{
    /// <summary>Where the edge is now, or null when its clip is gone.</summary>
    public double? In(TimelineDocument document) => document.FindClip(ClipId) is { } clip ? AtEnd ? clip.End : clip.Start : null;
}

/// <summary>
/// Immutable record of an unfolded range so it can be re-folded (Hide pill, <see cref="ITimelineTransaction.FoldRestored"/>). Spans
/// <paramref name="From"/> to <paramref name="To"/> (or <paramref name="Length"/> from the known edge), both resolved by clip id so it
/// follows edits. Dropped when the range disappears (<see cref="SpanIn"/> null) or is folded again. Keeps the original fold id.
/// </summary>
/// <param name="TrackId">Track of the original seam.</param>
/// <param name="AllTracks">Folded from, and re-folds, every unlocked track.</param>
/// <param name="Length">Length at unfold time, Sequence seconds.</param>
public sealed record RestoredFold(Guid Id, Guid TrackId, FoldEdge? From, FoldEdge? To, bool AllTracks, double Length)
{
    /// <summary>
    /// Current span in seconds, or null when the start clip is gone/moved tracks or the remainder is shorter than a minimum clip.
    /// End = start of the following clip on the track, else start + length. Linear scan; use <see cref="TimelineDocument.RestoredSpans"/> for all.
    /// </summary>
    public (double From, double To)? SpanIn(TimelineDocument document) =>
        Span(id => document.Locate(id) is { } hit ? (hit.Clip, hit.Track.Id) : null);

    internal (double From, double To)? Span(Func<Guid, (TimelineClip Clip, Guid TrackId)?> find)
    {
        double? from = null, to = null;
        if (From is { } first)
        {
            if (find(first.ClipId) is not { } found || found.TrackId != TrackId)
                return null;
            from = first.AtEnd ? found.Clip.End : found.Clip.Start;
        }

        if (To is { } last && find(last.ClipId) is { } after && after.TrackId == TrackId)
            to = last.AtEnd ? after.Clip.End : after.Clip.Start;

        var (start, end) = (from, to) switch
        {
            ({ } a, { } b) => (a, b),
            ({ } a, null) => (a, a + Length),
            (null, { } b) => (b - Length, b),
            _ => (double.NaN, double.NaN),
        };
        return end - start >= TimelineDocument.MinimumClipSeconds - 1e-6 && start >= -1e-6 ? (start, end) : null;
    }
}

/// <summary>The whole sequence: its frame, its rate, its tracks (bottom first) and what lies over all of them.</summary>
public sealed partial class TimelineDocument : IJsonOnDeserialized
{
    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// Unknown project-level JSON fields (<see cref="ProjectExtras"/>) carried through copies, tabs and undo and written back on save.
    /// Not serialized as part of the timeline.
    /// </summary>
    [JsonIgnore]
    public ProjectExtras? CarriedProjectFields { get; set; }
    /// <summary>Minimum clip length an edit can produce (whole frames while quantizing).</summary>
    public const double MinimumClipSeconds = 0.05;

    /// <summary>Maximum drop-over crossfade and permitted overlap (also at most half of either clip).</summary>
    public const double LongestDragCrossfade = 2;

    /// <summary>The longest same-track crossfade, and the longest fade.</summary>
    public const double LongestCrossfade = 10, LongestFade = 60;

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool UseVerticalResolution { get; set; }

    /// <summary>
    /// Natural Sequence frame size in pixels, before Use Vertical Resolution rotates it. Set from the first video placed on an empty
    /// Sequence (cropped, even dimensions) or by legacy migration; 0 while unset (omitted from JSON). Later crops never resize it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int NaturalWidth { get => _naturalWidth; set => _naturalWidth = Math.Max(value, 0); }

    /// <summary>The height of <see cref="NaturalWidth"/>'s size; 0 while unset, and then left out of the file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int NaturalHeight { get => _naturalHeight; set => _naturalHeight = Math.Max(value, 0); }

    private int _naturalWidth, _naturalHeight;

    /// <summary>Whether <see cref="NaturalWidth"/> and <see cref="NaturalHeight"/> are both set.</summary>
    [JsonIgnore]
    public bool HasNaturalSize => _naturalWidth > 0 && _naturalHeight > 0;

    public FrameRate FrameRate { get; set; } = FrameRate.Fps60;

    /// <summary>
    /// Project colour space (Project Settings): from the first video's transfer function or the New Project studio; default BT.709 SDR
    /// is omitted from JSON. "Match Project Settings" tone-maps HDR projects to BT.709 SDR, the only pipeline output.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ProjectColorSpace ColorSpace { get; set; }

    /// <summary>Project pixel aspect; default square is omitted from JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ProjectPixelAspect PixelAspect { get; set; }

    /// <summary>Default project sample rate (Hz): 48 kHz.</summary>
    public const int DefaultSampleRate = 48000;

    /// <summary>
    /// Project sample rate (Hz) used for every render under "Match Project Settings". Accepts 44100, 48000 or 96000; other values
    /// read as <see cref="DefaultSampleRate"/>, which is omitted from JSON.
    /// </summary>
    public int SampleRate
    {
        get => _sampleRate;
        set => _sampleRate = value is 44100 or 48000 or 96000 ? value : DefaultSampleRate;
    }

    private int _sampleRate = DefaultSampleRate;

    /// <summary>
    /// Frame explicitly chosen in the New Project studio: the first file keeps the chosen size, orientation and colour space instead
    /// of imposing its own. Omitted from JSON while false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExplicitFrame { get; set; }

    /// <summary>Frame rate explicitly chosen; the first file keeps it. Omitted from JSON while false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExplicitFrameRate { get; set; }

    /// <summary>Read-only Project Settings view; not serialized.</summary>
    [JsonIgnore]
    public ProjectSettings ProjectSettings =>
        new(NaturalWidth, NaturalHeight, UseVerticalResolution, FrameRate, ColorSpace, PixelAspect, SampleRate);

    /// <summary>Every edge lands on a frame of <see cref="FrameRate"/> (an edit can ask to be left free).</summary>
    public bool QuantizeToFrames { get; set; } = true;

    /// <summary>The tracks in their stacking order, bottom first.</summary>
    public List<TimelineTrackModel> Tracks { get; set; } = [];

    /// <summary>Sequence Output effect stack applied to the final frame; keyframe times in Sequence seconds.</summary>
    public List<EffectState> SequenceEffects { get; set; } = [];

    public bool SequenceEffectsBypassed { get; set; }

    /// <summary>The most and least the Sequence's master output fader goes to, in decibels.</summary>
    public const double LoudestMasterGainDb = 12, QuietestMasterGainDb = -40;

    /// <summary>
    /// Master fader (dB, clamped -40..+12): gain on the summed mix before Sequence fades, in playback and export
    /// (AudioProgram.MasterGain). 0 is omitted from JSON (<see cref="ITimelineTransaction.SetMasterGain"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double MasterGainDb
    {
        get => _masterGainDb;
        set => _masterGainDb = double.IsFinite(value) ? Math.Clamp(value, QuietestMasterGainDb, LoudestMasterGainDb) : 0;
    }

    private double _masterGainDb;

    public List<TimelineMarker> Markers { get; set; } = [];

    /// <summary>Folded ranges, each anchored at its seam; null (omitted from JSON) when empty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FoldedSegment>? Folds { get; set; }

    /// <summary>
    /// Unfolded ranges that can be re-folded (<see cref="RestoredFold"/>), in unfold order; null (omitted) when empty. The outermost
    /// commit prunes stale entries.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RestoredFold>? RestoredFolds { get; set; }

    /// <summary>
    /// Project Media bin: unique file paths in insertion order. Placed clips add their file (<see cref="PoolsPlacedFiles"/>), which stays
    /// after the last clip is deleted; New Project and Media Browser drops add files directly. Mutated only via transactions
    /// (<see cref="ITimelineTransaction.AddToMediaPool"/>) so Undo covers it. Omitted from JSON when empty.
    /// </summary>
    public List<string> MediaPool { get; set; } = [];

    /// <summary>
    /// When true, placing a clip adds its file to <see cref="MediaPool"/>. Set by the view model on its live document; standalone
    /// documents (tests, thumbnail reads) leave the pool unchanged. Not serialized.
    /// </summary>
    [JsonIgnore]
    public bool PoolsPlacedFiles { get; set; }

    /// <summary>A path as Project Media compares it: trimmed, its quotes taken off (compared without case).</summary>
    public static string PoolPath(string? path) => (path ?? "").Trim().Trim('"');

    /// <summary>
    /// File a clip contributes to Project Media (video, audio, image); null for text, adjustments and web sources.
    /// </summary>
    public static string? PoolFileOf(TimelineClip clip)
    {
        if (!clip.PlaysFile && clip.ContentKind != LayerKind.Image)
            return null;

        var path = PoolPath(clip.SourcePath is { Length: > 0 } source ? source : clip.State.ImagePath);
        return path.Length == 0 || path.Contains("://", StringComparison.Ordinal) ? null : path;
    }

    /// <summary>Whether Project Media holds a file (compared as <see cref="PoolPath"/>, without case).</summary>
    public bool PoolHolds(string path)
    {
        var wanted = PoolPath(path);
        return MediaPool.Exists(p => string.Equals(PoolPath(p), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The video tracks, bottom first: track 1 is the first.</summary>
    [JsonIgnore]
    public IReadOnlyList<TimelineTrackModel> VideoTracks => TracksOf(TimelineTrackKind.Video);

    [JsonIgnore]
    public IReadOnlyList<TimelineTrackModel> AudioTracks => TracksOf(TimelineTrackKind.Audio);

    public IReadOnlyList<TimelineTrackModel> TracksOf(TimelineTrackKind kind) => Tracks.Where(t => t.Kind == kind).ToList();

    /// <summary>Track captions render beneath (<see cref="TimelineTrackModel.CaptionsUnder"/>); null renders them on top.</summary>
    [JsonIgnore]
    public TimelineTrackModel? CaptionsUnderTrack => Tracks.FirstOrDefault(t => t.CaptionsUnder);

    /// <summary>
    /// Sequence duration: latest clip end including audio extensions (L-cuts, slipped unlinked audio). Hidden, muted or disabled
    /// clips still count, so hiding the last clip never shortens the export. The caption clip is excluded (it spans the rest).
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

    /// <summary>
    /// Independent copy with its own tracks, clips, markers and Sequence effects (same ids). Looks (<see cref="TimelineClip.State"/>)
    /// are shared since edits are copy-on-write; <paramref name="deep"/> also copies them for callers that mutate looks in place
    /// (e.g. mask key assignment on save). No subscribers or open transactions.
    /// </summary>
    public TimelineDocument Clone(bool deep = false)
    {
        LayerState? Look(LayerState? look) => look is null ? null : deep ? TimelineTransaction.CopyOf(look) : look;

        var copy = new TimelineDocument
        {
            Width = Width,
            Height = Height,
            UseVerticalResolution = UseVerticalResolution,
            NaturalWidth = NaturalWidth,
            NaturalHeight = NaturalHeight,
            FrameRate = FrameRate,
            ColorSpace = ColorSpace,
            PixelAspect = PixelAspect,
            SampleRate = SampleRate,
            ExplicitFrame = ExplicitFrame,
            ExplicitFrameRate = ExplicitFrameRate,
            QuantizeToFrames = QuantizeToFrames,
            SequenceEffects = deep ? [.. SequenceEffects.Select(e => System.Text.Json.JsonSerializer.Deserialize<EffectState>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e, TimelineTransaction.CopyOptions), TimelineTransaction.CopyOptions)!)] : [.. SequenceEffects],
            SequenceEffectsBypassed = SequenceEffectsBypassed,
            MasterGainDb = MasterGainDb,
            Markers = [.. Markers],
            Folds = Folds is null ? null : [.. Folds],
            RestoredFolds = RestoredFolds is null ? null : [.. RestoredFolds],
            MediaPool = [.. MediaPool],
            PoolsPlacedFiles = PoolsPlacedFiles,
            Extra = Extra,
            CarriedProjectFields = CarriedProjectFields,
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
                if (deep && clip.BeatTimes is { } beats)
                    clipCopy.BeatTimes = [.. beats];
                made.Clips.Add(clipCopy);
            }
            copy.Tracks.Add(made);
        }
        return copy;
    }

    /// <summary>
    /// Current spans of all <see cref="RestoredFolds"/> (record, track, from/to seconds) in unfold order, excluding stale ones.
    /// Single clip scan; used by timeline pills and commit-time pruning.
    /// </summary>
    public IReadOnlyList<(RestoredFold Restored, Guid TrackId, double From, double To)> RestoredSpans()
    {
        if (RestoredFolds is not { Count: > 0 } restored)
            return [];

        var where = new Dictionary<Guid, (TimelineClip Clip, Guid TrackId)>();
        foreach (var track in Tracks)
        {
            foreach (var clip in track.Clips)
                where.TryAdd(clip.Id, (clip, track.Id));
        }

        (TimelineClip Clip, Guid TrackId)? Find(Guid id) => where.TryGetValue(id, out var found) ? found : null;
        var spans = new List<(RestoredFold Restored, Guid TrackId, double From, double To)>(restored.Count);
        foreach (var stretch in restored)
        {
            if (stretch.Span(Find) is { } span)
                spans.Add((stretch, stretch.TrackId, span.From, span.To));
        }
        return spans;
    }

    // ----- Parent/child clips and tracks -----

    /// <summary>
    /// All clips whose <see cref="TimelineClip.ParentClipId"/> is <paramref name="parent"/>, with their tracks, including non-overlapping
    /// ones (parent edits only carry overlapping children). Full scan for menus and edits, not per frame; consume before the next change.
    /// </summary>
    public IEnumerable<(TimelineTrackModel Track, TimelineClip Clip)> ChildrenOf(TimelineClip parent)
    {
        foreach (var track in Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.ParentClipId == parent.Id && !ReferenceEquals(clip, parent))
                    yield return (track, clip);
            }
        }
    }

    /// <summary>
    /// True when the track or any ancestor track header hides video (header switches apply to the whole family). Ignores
    /// <see cref="TimelineClip.IsHidden"/>.
    /// </summary>
    public bool IsHiddenWithParents(TimelineTrackModel track)
    {
        TimelineTrackModel? at = track;
        for (var steps = 0; at is not null && steps <= Tracks.Count; steps++)
        {
            if (at.IsHidden)
                return true;
            var parentId = at.ParentTrackId;
            at = parentId is null ? null : Tracks.Find(t => t.Id == parentId);
        }

        return false;
    }

    /// <summary>The tracks that lie under <paramref name="track"/> (<see cref="TimelineTrackModel.ParentTrackId"/>), in the stacking order.</summary>
    public IEnumerable<TimelineTrackModel> ChildTracksOf(TimelineTrackModel track)
    {
        foreach (var child in Tracks)
        {
            if (child.ParentTrackId == track.Id && !ReferenceEquals(child, track))
                yield return child;
        }
    }

    // ----- Tethers: children that follow the parent in time but are not Game Preset pieces -----
    // TimelineClip.Tethered is set by the Timeline drag and survives moves of the clip, lane or parent. Legacy documents without the
    // mark infer tethers on read: preset pieces live under the parent's own track, so a picture child elsewhere is a tether. Cutouts
    // (same source file as the parent) are never tethers (AttachRefusal), so a Cutout dragged off its lane stays a preset piece.

    /// <summary>
    /// True disables legacy tether inference on read. Serialized only when the document has an untethered loose child
    /// (<see cref="LooseChildren"/>), so other files are unchanged; without it, loose children are marked tethered in
    /// <see cref="IJsonOnDeserialized.OnDeserialized"/>.
    /// </summary>
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    internal bool TethersMarked
    {
        get => LooseChildren().Any(c => !c.Tethered);
        set => _tethersMarkedRead = value;
    }

    // What the text read said of TethersMarked (false when it said nothing): read once, as the document is read.
    private bool _tethersMarkedRead;

    void IJsonOnDeserialized.OnDeserialized()
    {
        // Map legacy Game Preset names on Track Presets to current names.
        foreach (var track in Tracks)
            track.PresetNodeName = LegacyPresetNames.Map(track.PresetNodeName);

        if (_tethersMarkedRead)
            return;

        foreach (var child in LooseChildren())
            child.Tethered = true;
    }

    /// <summary>
    /// Legacy inferred tethers: parented clips on picture tracks (not audio or captions) outside the parent's track subtree, excluding
    /// Cutouts (same source as parent) and pre-migration legacy kinds.
    /// </summary>
    internal List<TimelineClip> LooseChildren() => LooseIn(null);

    /// <summary>
    /// <see cref="LooseChildren"/> across tracks, folds and, with <paramref name="bin"/>, the recycle bin. Each child resolves its track
    /// from its own record (or the parent track when its track was deleted) and its parent wherever it now lives.
    /// </summary>
    internal List<TimelineClip> LooseIn(IReadOnlyList<DeletedClip>? bin)
    {
        var loose = new List<TimelineClip>();
        var places = new List<(TimelineClip Clip, Guid TrackId, Guid? TrackParentId, TimelineTrackKind Kind, bool Look)>();
        foreach (var track in Tracks)
        {
            foreach (var clip in track.Clips)
                places.Add((clip, track.Id, null, track.Kind, bin is null));
        }

        foreach (var segment in Folds ?? [])
        {
            foreach (var folded in segment.Clips)
                places.Add((folded.Clip, folded.TrackId, null, folded.TrackKind, bin is null));
        }

        void Deleted(IReadOnlyList<DeletedClip>? entries)
        {
            foreach (var entry in entries ?? [])
            {
                places.Add((entry.Clip, entry.TrackId, entry.TrackParentId, entry.TrackKind, true));
                Deleted(entry.Children);
            }
        }

        Deleted(bin);
        if (!places.Exists(p => p.Clip.ParentClipId is not null))
            return loose;

        var byId = new Dictionary<Guid, (TimelineClip Clip, Guid TrackId)>();
        foreach (var place in places)
            byId.TryAdd(place.Clip.Id, (place.Clip, place.TrackId));

        // With a bin, only the bin's own entries are looked at (the document's are its own reading's, OnDeserialized).
        foreach (var (clip, trackId, trackParentId, kind, look) in places)
        {
            if (!look || kind is TimelineTrackKind.Audio or TimelineTrackKind.Subtitle || clip.ParentClipId is not { } parentId
                || clip.ContentKind is LayerKind.MainVideo or LayerKind.Video)
            {
                continue;
            }

            if (byId.TryGetValue(parentId, out var parent) && !ReferenceEquals(parent.Clip, clip) && parent.Clip.ContentKind is not (LayerKind.MainVideo or LayerKind.Video)
                && !LiesUnder(trackId, trackParentId, parent.TrackId) && !IsCutoutOf(clip, parent.Clip))
            {
                loose.Add(clip);
            }
        }

        return loose;
    }

    // True when track id (or, if deleted, its recorded parent) descends from the other track id at any depth.
    private bool LiesUnder(Guid laneId, Guid? recordedParent, Guid trackId)
    {
        var up = Tracks.Find(t => t.Id == laneId) is { } lane ? lane.ParentTrackId : recordedParent;
        for (var steps = 0; steps <= Tracks.Count && up is { } id; steps++)
        {
            if (id == trackId)
                return true;
            up = Tracks.Find(t => t.Id == id)?.ParentTrackId;
        }

        return false;
    }


    // A Cutout of its parent: a video clip of the video clip's own file (MainViewModel.IsCutout, here on the model alone).
    private static bool IsCutoutOf(TimelineClip clip, TimelineClip parent) =>
        clip.ContentKind == LayerKind.VideoFile && parent.ContentKind == LayerKind.VideoFile && PoolPath(clip.SourcePath).Length > 0
        && string.Equals(PoolPath(clip.SourcePath), PoolPath(parent.SourcePath), StringComparison.OrdinalIgnoreCase);

    // ----- Changes -----

    /// <summary>The transactions open now, the outermost first (see <see cref="BeginTransaction"/>).</summary>
    internal readonly List<TimelineTransaction> OpenTransactions = [];

    /// <summary>
    /// Raised once per outermost committed transaction that changed something; drives Undo steps and view refresh.
    /// </summary>
    public event Action<TimelineCommit>? Committed;

    internal void RaiseCommitted(TimelineCommit commit) => Committed?.Invoke(commit);

    /// <summary>
    /// Opens a transaction; edit through it and <see cref="ITimelineTransaction.Commit"/>. Disposing uncommitted rolls back.
    /// Nested transactions merge into the outer one and roll back only their own part.
    /// </summary>
    public ITimelineTransaction BeginTransaction(string label) => new TimelineTransaction(this, label);

    // ----- What must always be true -----

    /// <summary>
    /// Tolerance for reading past the file end: quantized edges are within 0.5 us of their frame, so a moved clip's length (and
    /// source span, times speed) can drift about a microsecond.
    /// </summary>
    internal static double FileSlack(TimelineClip clip) => 4e-6 * Math.Max(1, clip.Speed);

    /// <summary>
    /// Invariant check returning human-readable problems (empty when valid): ordering/overlap, crossfades without abutment, wrong track
    /// kinds, track numbering, NaN, out-of-range lengths/speeds/fades/offsets, J/L audio before 0 or outside the file, parent cycles
    /// (missing parents are fine) and tethers without a parent. <paramref name="requireFrames"/> also flags off-frame edges. Tests and hand-written files.
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
        var clipParents = new Dictionary<Guid, Guid>();
        var trackParents = new Dictionary<Guid, Guid>();
        var trackNames = new Dictionary<Guid, string>();
        foreach (var track in Tracks)
        {
            var name = track.DisplayName;
            if (!tracks.Add(track.Id))
                Add($"{name}: its id {track.Id} is used twice.");
            trackNames[track.Id] = name;
            if (track.ParentTrackId == track.Id)
                Add($"{name}: it lies under itself.");
            else if (track.ParentTrackId is { } parentTrack)
                trackParents[track.Id] = parentTrack;
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
                if (clip.ParentClipId == clip.Id)
                    Add($"{at}: it is its own parent.");
                else if (clip.ParentClipId is { } parentClip)
                    clipParents[clip.Id] = parentClip;
                else if (clip.Tethered)
                    Add($"{at}: tethered, but to no clip.");

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

        foreach (var id in LoopsIn(clipParents))
            Add($"The clip {id}: its parents lead back to it.");
        foreach (var id in LoopsIn(trackParents))
            Add($"{trackNames[id]}: the tracks it lies under lead back to it.");
        return problems;
    }

    /// <summary>Ids on a parent cycle; a parent missing from the map terminates the chain.</summary>
    private static IEnumerable<Guid> LoopsIn(Dictionary<Guid, Guid> parents)
    {
        foreach (var (id, parent) in parents)
        {
            var (at, steps) = (parent, 0);
            while (at != id && steps++ < parents.Count && parents.TryGetValue(at, out var next))
                at = next;
            if (at == id)
                yield return id;
        }
    }
}
// ----- Gaps (Pass 5.6.5: double-click ripple close) -----
public sealed partial class TimelineDocument
{
    /// <summary>
    /// The empty stretch of <paramref name="track"/> around <paramref name="time"/> (Sequence seconds): from the end of the clip before it
    /// (or 0) to the start of the clip after it. Null on a clip, after the last clip, or for a stretch shorter than a microsecond.
    /// </summary>
    public (double Start, double End)? GapAround(TimelineTrackModel track, double time)
    {
        if (!double.IsFinite(time) || time < 0)
            return null;

        var (start, end) = (0.0, double.PositiveInfinity);
        foreach (var clip in track.Clips)
        {
            if (clip.Start <= time && clip.End > time)
                return null;
            if (clip.End <= time)
                start = Math.Max(start, clip.End);
            else
                end = Math.Min(end, clip.Start);
        }

        return double.IsPositiveInfinity(end) || end - start <= 1e-6 ? null : (start, end);
    }
}
