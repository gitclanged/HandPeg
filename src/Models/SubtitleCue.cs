using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>
/// One subtitle: text and timeline start/end in seconds (before the track offset). Sourced from .srt/.vtt, auto-captions or the
/// Subtitle Studio. Bitmap tracks (PGS, VobSub) carry timing only.
/// </summary>
public sealed partial class SubtitleCue : ObservableObject
{
    [ObservableProperty] private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration))]
    private double _start;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration))]
    private double _end;

    [ObservableProperty] private string _text = "";

    public double Duration => Math.Max(End - Start, 0);

    // ----- Words -----
    // Word boundaries are stored as fractions (0-1) of the cue length, one per inter-word gap, so words keep their share when the
    // cue is moved or stretched. Without stored boundaries, time is distributed by word length.

    /// <summary>How close two word boundaries may come, as a fraction of the cue: no word is squeezed to nothing.</summary>
    public const double SmallestWordShare = 0.02;

    /// <summary>Start fractions of each word after the first; null until set manually.</summary>
    public double[]? WordBreaks { get; set; }

    public string[] GetWords() => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Effective boundaries: stored ones if they match the words, else letter-share boundaries.</summary>
    public double[] GetBreaks()
    {
        var words = GetWords();
        if (words.Length < 2)
            return [];

        if (WordBreaks is { } kept && kept.Length == words.Length - 1)
        {
            var ordered = kept[0] > 0 && kept[^1] < 1;
            for (var i = 1; i < kept.Length && ordered; i++)
                ordered = kept[i] > kept[i - 1];
            if (ordered)
                return kept;
        }

        var breaks = new double[words.Length - 1];
        double letters = Math.Max(words.Sum(w => w.Length), 1), before = 0;
        for (var i = 0; i < breaks.Length; i++)
        {
            before += words[i].Length;
            breaks[i] = before / letters;
        }

        return breaks;
    }

    /// <summary>Moves the boundary after word <paramref name="index"/>, clamped between its neighbours.</summary>
    public void SetBreak(int index, double fraction)
    {
        var breaks = (double[])GetBreaks().Clone();
        if (index < 0 || index >= breaks.Length)
            return;

        var low = (index > 0 ? breaks[index - 1] : 0) + SmallestWordShare;
        var high = (index + 1 < breaks.Length ? breaks[index + 1] : 1) - SmallestWordShare;
        if (high < low)
            return;

        breaks[index] = Math.Clamp(fraction, low, high);
        WordBreaks = breaks;
    }

    /// <summary>Which word is being said at a point of the cue (a fraction of its length).</summary>
    public int WordAt(double fraction)
    {
        var breaks = GetBreaks();
        var word = 0;
        while (word < breaks.Length && fraction >= breaks[word])
            word++;
        return word;
    }

    // Boundaries are for the words they were set between: with a word more or a word fewer they mean nothing.
    partial void OnTextChanged(string value)
    {
        if (WordBreaks is { } kept && kept.Length != GetWords().Length - 1)
            WordBreaks = null;
    }

    /// <summary>
    /// Anchored caption piece shown through one clip; null for timeline-clock cues (imported, typed, or from a clip-less source).
    /// Rebuilt on every projection.
    /// </summary>
    public CaptionPiece? Piece { get; set; }

    public SubtitleCue Clone() => new() { Index = Index, Start = Start, End = End, Text = Text, WordBreaks = (double[]?)WordBreaks?.Clone(), Piece = Piece };
}

/// <summary>Serialized subtitle in a project.</summary>
/// <param name="Breaks">Word start fractions after the first; omitted when time is shared by length.</param>
public sealed record SubtitleCueState(
    double Start, double End, string Text,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double[]? Breaks = null)
{
    /// <summary>
    /// Anchored word times: start/end pairs in source seconds, one per word of <see cref="Text"/>. Omitted for timeline-clock cues and
    /// length-distributed captions.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Words { get; init; }

    /// <summary>
    /// Projected cue of an anchored caption. Written for readers without anchor support; skipped on load and re-projected from anchors.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Projected { get; init; }

    /// <summary>Projected caption piece, preserved by Subtitle Studio undo. Never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CaptionPiece? Piece { get; init; }
}

/// <summary>The imported or hand-made subtitle track as it is written to a project.</summary>
public sealed class SubtitleTrackData
{
    /// <summary>The file the track was imported from; empty for one made in HandPeg.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Pictures, not text (PGS, VobSub): the cues are timings of the pictures in the file.</summary>
    public bool IsImage { get; set; }

    /// <summary>Milliseconds every cue is shifted by.</summary>
    public double OffsetMs { get; set; }

    public List<SubtitleCueState> Cues { get; set; } = [];

    /// <summary>Source the anchored captions follow; omitted when there are none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CaptionSource? Source { get; set; }

    /// <summary>
    /// Anchored captions in source time with word times, sorted by start; <see cref="SubtitleCueState.Projected"/> cues are rebuilt
    /// from these and the timeline.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SubtitleCueState>? Anchors { get; set; }
}

/// <summary>Caption source kind: what auto-captions transcribe and what the resulting captions follow.</summary>
public enum CaptionSourceKind
{
    /// <summary>
    /// Legacy: a main-video audio stream (stream -1: its picture). Read-only for old projects, migrated to <see cref="Clip"/> on open
    /// (LegacyMainVideo.MigrateCaptions); kept to preserve enum values.
    /// </summary>
    MainFile,

    /// <summary>
    /// A file played as clips (audio clip, video clip audio, or a Sub-Lane stream); stream -1 means its picture (imported track).
    /// </summary>
    Clip,

    /// <summary>A separate recording in sync with the timeline: its time is the timeline's.</summary>
    External,

    /// <summary>The voiceover recorded in the Voiceover Studio, where it is placed.</summary>
    Voiceover,
}

/// <summary>Caption source: kind, file and zero-based audio stream index.</summary>
public sealed record CaptionSource(CaptionSourceKind Kind, string Path, int Stream)
{
    /// <summary>
    /// True when captions are stored in source time and projected through its clips, following cuts, trims, folds and moves.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAnchored => Kind is CaptionSourceKind.Clip;

    /// <summary>True for the same kind and file (stream ignored: word timing is identical).</summary>
    public bool SameAs(CaptionSource? other) =>
        other is not null && other.Kind == Kind && string.Equals(other.Path, Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A clip's window onto a source: source time <c>t</c> maps to Sequence time <c>Start + (t - MediaOffset) / Speed</c> (J/L cuts and
/// slips included).
/// </summary>
/// <param name="Start">Window start on the Sequence (s).</param>
/// <param name="End">Window end on the Sequence (s).</param>
/// <param name="MediaOffset">Source time at <paramref name="Start"/>.</param>
/// <param name="Speed">Source seconds per Sequence second.</param>
public readonly record struct CaptionSpan(double Start, double End, double MediaOffset, double Speed)
{
    /// <summary>Where the window closes in the source.</summary>
    public double MediaEnd => MediaOffset + (End - Start) * Speed;

    public double ToSequence(double source) => Start + (source - MediaOffset) / Speed;

    public double ToSource(double sequence) => MediaOffset + (sequence - Start) * Speed;

    /// <summary>Identity window: source time equals Sequence time.</summary>
    public static CaptionSpan Identity { get; } = new(0, 1e9, 0, 1);
}

/// <summary>
/// Projection record for a timeline cue: its clip window, the projected <paramref name="Anchor"/>, the shown words
/// (<paramref name="Source"/>, source time) and the cue as projected, used to detect edits (unedited cues write back their exact words).
/// </summary>
public sealed record CaptionPiece(CaptionSpan Span, SubtitleCueState Anchor, SubtitleCueState Source, double Start, double End, string Text, double[]? Breaks);
