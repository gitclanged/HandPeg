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

    public SubtitleCue Clone() => new() { Index = Index, Start = Start, End = End, Text = Text, WordBreaks = (double[]?)WordBreaks?.Clone() };
}

/// <summary>A subtitle as it is written to a project.</summary>
/// <param name="Breaks">Where each word but the first begins, as fractions of the cue; left out while the words share the time by their length.</param>
public sealed record SubtitleCueState(
    double Start, double End, string Text,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double[]? Breaks = null);

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
}
