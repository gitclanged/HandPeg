using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>
/// One subtitle: what is shown, from when to when (seconds of the timeline, before the track's offset). Read
/// from an .srt or .vtt file, made from the auto-captions, or typed in the Subtitle Editor. For a track of
/// pictures (PGS, VobSub) there is no text to read: the cue is its timing only.
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
    // A cue's words are spoken one after another through its time. Where one ends and the next begins is kept
    // as a fraction of the cue's length (0 to 1), one for each gap between two words, so the words keep their
    // share of the cue when it is moved or made longer. With none kept, each word gets time by its length.

    /// <summary>How close two word boundaries may come, as a fraction of the cue: no word is squeezed to nothing.</summary>
    public const double SmallestWordShare = 0.02;

    /// <summary>Where each word but the first begins, as fractions of the cue's length; null while they are not set by hand.</summary>
    public double[]? WordBreaks { get; set; }

    public string[] GetWords() => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The boundaries in use: the ones set, when they still fit the words; otherwise each word's share is its share of the letters.</summary>
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

    /// <summary>Moves the boundary between word <paramref name="index"/> and the next, as far as its neighbours allow.</summary>
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
    /// The anchored caption this cue shows a piece of, seen through one clip (4.4); null for a cue on the timeline's own
    /// clock (imported, typed by hand, or made from a source with no clips of its own). Rebuilt by every projection.
    /// </summary>
    public CaptionPiece? Piece { get; set; }

    public SubtitleCue Clone() => new() { Index = Index, Start = Start, End = End, Text = Text, WordBreaks = (double[]?)WordBreaks?.Clone(), Piece = Piece };
}

/// <summary>A subtitle as it is written to a project.</summary>
/// <param name="Breaks">Where each word but the first begins, as fractions of the cue; left out while the words share the time by their length.</param>
public sealed record SubtitleCueState(
    double Start, double End, string Text,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double[]? Breaks = null)
{
    /// <summary>
    /// An anchored caption's words as they were heard (4.4): each word's start and end in its source's own seconds, in
    /// pairs, one pair for each word of <see cref="Text"/>. Left out for a cue on the timeline's clock and for an anchored
    /// caption whose words share its time by their length.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Words { get; init; }

    /// <summary>
    /// A cue that shows a piece of an anchored caption on the timeline (4.4). It is written with the others, so that a
    /// HandPeg that knows no anchors still shows the captions where they are; this one leaves it out on reading and makes
    /// it again from the anchors.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Projected { get; init; }

    /// <summary>The piece of an anchored caption a cue on the timeline shows, kept with it by undo in the Subtitle Editor. Never written.</summary>
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

    /// <summary>What the anchored captions were made from and follow on the timeline (4.4); left out while there are none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CaptionSource? Source { get; set; }

    /// <summary>
    /// The anchored captions (4.4): in their source's own time, each with its words' own times, ordered by their start.
    /// What the cues marked <see cref="SubtitleCueState.Projected"/> show of them is made again from these and the timeline.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SubtitleCueState>? Anchors { get; set; }
}

/// <summary>What auto-captions listen to, and so what the captions made from it follow on the timeline (4.4).</summary>
public enum CaptionSourceKind
{
    /// <summary>One of the main video's file's audio streams; with stream -1, its picture (an imported track timed to the file).</summary>
    MainFile,

    /// <summary>A file the timeline plays as clips of its own: a sound clip, or a video layer's sound.</summary>
    Clip,

    /// <summary>A separate recording in sync with the timeline: its time is the timeline's.</summary>
    External,

    /// <summary>The voiceover recorded in the Voiceover Studio, where it is placed.</summary>
    Voiceover,
}

/// <summary>A source captions are made from: its kind, its file, and which of its audio streams (counted among them from 0).</summary>
public sealed record CaptionSource(CaptionSourceKind Kind, string Path, int Stream)
{
    /// <summary>
    /// Its captions are kept in its own time and projected through the clips that play it (the main file's, or a clip's file):
    /// cuts, trims, folds and moves made before or after the captions carry them along.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAnchored => Kind is CaptionSourceKind.MainFile or CaptionSourceKind.Clip;

    /// <summary>Whether it is the same source: the same kind of the same file (any stream of it: the words are where they are said).</summary>
    public bool SameAs(CaptionSource? other) =>
        other is not null && other.Kind == Kind && string.Equals(other.Path, Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One clip's window onto a source (4.4): the part of the source it plays and where that lands on the sequence. A moment
/// <c>t</c> of the source inside it is heard at <c>Start + (t - MediaOffset) / Speed</c>, J and L cuts and slips included.
/// </summary>
/// <param name="Start">Where the window opens on the sequence, in seconds.</param>
/// <param name="End">Where it closes on the sequence.</param>
/// <param name="MediaOffset">The moment of the source heard at <paramref name="Start"/>.</param>
/// <param name="Speed">How many seconds of the source a second of the sequence plays.</param>
public readonly record struct CaptionSpan(double Start, double End, double MediaOffset, double Speed)
{
    /// <summary>Where the window closes in the source.</summary>
    public double MediaEnd => MediaOffset + (End - Start) * Speed;

    public double ToSequence(double source) => Start + (source - MediaOffset) / Speed;

    public double ToSource(double sequence) => MediaOffset + (sequence - Start) * Speed;

    /// <summary>A source whose time is the sequence's: an external recording, or a main video that is the whole sequence.</summary>
    public static CaptionSpan Identity { get; } = new(0, 1e9, 0, 1);
}

/// <summary>
/// What a cue on the timeline shows of an anchored caption (4.4): the clip window it is seen through, the caption it is
/// a piece of (<paramref name="Anchor"/>, the very one projected), the words of it shown (<paramref name="Source"/>, in the
/// source's time), and the cue as the projection made it, by which a cue edited since is told from one that is not: an
/// unedited cue gives back its words exactly as they were heard.
/// </summary>
public sealed record CaptionPiece(CaptionSpan Span, SubtitleCueState Anchor, SubtitleCueState Source, double Start, double End, string Text, double[]? Breaks);
