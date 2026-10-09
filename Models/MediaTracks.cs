using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp.Models;

/// <summary>One line of a label/value card in the Properties tab.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>
/// One part of an audio track that was cut up on the timeline: from where to where in the track's own time,
/// and whether it has been silenced.
/// </summary>
public sealed record AudioPiece(double Start, double End, bool Muted);

/// <summary>An audio stream of the source and what to do with it in the output.</summary>
public sealed partial class AudioTrack : ObservableObject
{
    public const string Passthrough = "Passthrough (Copy)";
    public const string Reencode = "Re-encode";
    public const string Drop = "Ignore / Drop";

    public static IReadOnlyList<string> AllActions { get; } = [Passthrough, Reencode, Drop];
    public static IReadOnlyList<string> AllCodecs { get; } = ["aac", "libopus", "libmp3lame", "ac3", "pcm_s16le"];
    public static IReadOnlyList<string> AllBitrates { get; } = ["96k", "128k", "160k", "192k", "256k", "320k"];

    public AudioTrack(AudioStreamInfo stream)
    {
        Stream = stream;

        _title = stream.Title.Length > 0 ? stream.Title : $"Track {stream.Index + 1}";

    }

    /// <summary>The name written to the output as the track's title. Starts as the source's own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private string _title;

    /// <summary>The file the track is a stream of, as its row is headed. Set when the file is loaded.</summary>
    [ObservableProperty] private string _sourceFileName = "";

    public AudioStreamInfo Stream { get; }

    /// <summary>Position among the audio streams, as used in "0:a:N".</summary>
    public int Index => Stream.Index;

    /// <summary>
    /// The second line of its row: which stream of the file it is (counted from 1), the title it has when it
    /// has one, its language, and its format. "Stream #2 (MIC)  ·  aac, stereo".
    /// </summary>
    public string Description
    {
        get
        {
            var named = Stream.Title.Length > 0 || Title != $"Track {Stream.Index + 1}" ? $" ({Title})" : "";
            var language = Stream.Language.Length > 0 && Stream.Language != "und" ? $" [{Stream.Language}]" : "";
            var layout = Stream.ChannelLayout.Length > 0 ? Stream.ChannelLayout : $"{Stream.Channels} ch";
            return $"Stream #{Stream.Index + 1}{named}{language}  \u00B7  {Stream.Codec}, {layout}";
        }
    }

    // Instance accessors so the row template can bind to them.
    public IReadOnlyList<string> Actions => AllActions;
    public IReadOnlyList<string> Codecs => AllCodecs;
    public IReadOnlyList<string> Bitrates => AllBitrates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReencoded))]
    [NotifyPropertyChangedFor(nameof(HasOwnFormat))]
    private string _action = Reencode;

    /// <summary>Whether its codec and bitrate can be chosen: while it is re-encoded.</summary>
    public bool HasOwnFormat => IsReencoded;

    [ObservableProperty] private string _codec = "aac";
    [ObservableProperty] private string _bitrate = "160k";

    // ----- Mixer -----

    /// <summary>Louder (positive) or quieter, in decibels. Applies to tracks that are re-encoded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProcessing))]
    private double _gainDb;

    /// <summary>The track's processing: compressor, gate, equalizer and so on. Replaced as a whole by its dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiltersCountText))]
    [NotifyPropertyChangedFor(nameof(HasProcessing))]
    private TrackAudioFilters _filters = new();

    /// <summary>A picture of the whole track's sound, drawn in the background after a load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackWaveform))]
    private System.Windows.Media.ImageSource? _waveform;

    // ----- As a row of the Audio tab -----
    // The rows there are made from one template, for these tracks and for the sounds added from files alike
    // (see Layer). These say what a track is where the two differ, under names both have.

    /// <summary>One of the video's own audio streams, with a codec, filters and a solo button of its own.</summary>
    public bool IsStream => true;

    /// <summary>What the solo button plays: this track.</summary>
    public object SoloTarget => this;

    /// <summary>The picture behind the whole row: the track's waveform.</summary>
    public System.Windows.Media.ImageSource? TrackWaveform => Waveform;

    /// <summary>A track runs with the video, at the video's speed, and cannot be taken off the timeline.</summary>
    public bool HasSpeed => false;

    public double ClipSpeed { get => 1; set { } }
    public bool IsRemovable => false;

    // ----- On the timeline -----

    /// <summary>How far the track has been slipped against the picture, in seconds: positive is later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private double _offsetSeconds;

    /// <summary>The parts the track was cut into, in its own time; empty while it is still whole.</summary>
    public IReadOnlyList<AudioPiece> Pieces { get; private set; } = [];

    /// <summary>Moved, or silenced in places: sound like that cannot be copied, only encoded again.</summary>
    public bool IsEdited => Math.Abs(OffsetSeconds) > 0.001 || Pieces.Any(p => p.Muted);

    public void SetEdits(double offset, IEnumerable<AudioPiece>? pieces)
    {
        Pieces = [.. pieces ?? []];
        OffsetSeconds = offset;
        OnPropertyChanged(nameof(Pieces));
        OnPropertyChanged(nameof(IsEdited));
    }

    /// <summary>Cuts the track in two at a moment of its own time. <paramref name="total"/> is its length, for the first cut of a whole track.</summary>
    public void SplitAt(double seconds, double total)
    {
        var pieces = Pieces.Count > 0 ? Pieces.ToList() : [new AudioPiece(0, total, false)];
        var index = pieces.FindIndex(p => seconds > p.Start + 0.02 && seconds < p.End - 0.02);
        if (index < 0)
            return;

        var piece = pieces[index];
        pieces[index] = piece with { End = seconds };
        pieces.Insert(index + 1, piece with { Start = seconds });
        SetEdits(OffsetSeconds, pieces);
    }

    public void ToggleMuted(AudioPiece piece)
    {
        var pieces = Pieces.ToList();
        var index = pieces.IndexOf(piece);
        if (index < 0)
            return;

        pieces[index] = piece with { Muted = !piece.Muted };
        SetEdits(OffsetSeconds, pieces);
    }

    /// <summary>
    /// What the edits come to as audio filters, applied to the track before anything else: the silenced
    /// parts turned down to nothing, then the whole track moved later (with silence in front) or earlier.
    /// </summary>
    public List<string> BuildEditChain()
    {
        static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var chain = Pieces.Where(p => p.Muted).Select(p => $"volume=0:enable='between(t,{Number(p.Start)},{Number(p.End)})'").ToList();
        if (OffsetSeconds > 0.001)
            chain.Add($"adelay={(int)Math.Round(OffsetSeconds * 1000)}:all=1");
        else if (OffsetSeconds < -0.001)
            chain.Add($"atrim=start={Number(-OffsetSeconds)},asetpts=PTS-STARTPTS");
        return chain;
    }

    /// <summary>The track is turned down while a voice is speaking.</summary>
    [ObservableProperty] private bool _autoDuck;

    /// <summary>The track is a voice (a microphone, dialogue): what the ducked tracks make room for.</summary>
    [ObservableProperty] private bool _isVoice;

    /// <summary>The player is playing this track instead of the first one. Set by the view model.</summary>
    [ObservableProperty] private bool _isSolo;

    /// <summary>The colour the waveform is drawn in, so that tracks can be told apart in the master view.</summary>
    public string WaveformColor { get; init; } = "#4FC3F7";

    /// <summary>How many filters are on, for the button that opens them to show; nothing when none is.</summary>
    public string FiltersCountText => Filters.ActiveCount > 0 ? Filters.ActiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>Whether there is anything for FFmpeg to do to the sound of this track.</summary>
    public bool HasProcessing => Math.Abs(GainDb) >= 0.05 || Filters.IsActive;

    /// <summary>The audio filters for this track's gain and processing; empty when there is none.</summary>
    public List<string> GetFilterChain() => Filters.BuildChain(GainDb);

    public bool IsReencoded => Action == Reencode;
    public bool IsPassthrough => Action == Passthrough;
    public bool IsDropped => Action == Drop;
}

/// <summary>A subtitle stream of the source and what to do with it in the output.</summary>
public sealed partial class SubtitleTrack : ObservableObject
{
    public const string None = "None";
    public const string SoftSub = "Pass-through (Soft Sub)";
    public const string HardSub = "Burn into Video (Hard Sub)";

    public SubtitleTrack(SubtitleStreamInfo stream)
    {
        Stream = stream;

        var name = stream.Title.Length > 0 ? stream.Title : $"Subtitle {stream.Index + 1}";
        var language = stream.Language.Length > 0 && stream.Language != "und" ? $" [{stream.Language}]" : "";
        Description = $"#{stream.Index}  {name}{language}  ·  {stream.Codec}";

        // Burning in needs text subtitles; bitmap formats can only be passed through.
        Actions = stream.IsText ? [None, SoftSub, HardSub] : [None, SoftSub];
    }

    public SubtitleStreamInfo Stream { get; }

    /// <summary>Position among the subtitle streams, as used in "0:s:N".</summary>
    public int Index => Stream.Index;

    public string Description { get; }
    public IReadOnlyList<string> Actions { get; }

    [ObservableProperty] private string _action = None;
}
