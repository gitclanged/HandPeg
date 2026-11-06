using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp.Models;

/// <summary>One line of a label/value card in the Properties tab.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>
/// One part of an audio track that was cut up on the timeline: from where to where in the track's own time,
/// and whether it has been silenced.
/// </summary>
public readonly record struct AudioPiece(double Start, double End, bool Muted);

/// <summary>
/// An output audio track (5.2-AuditB): a stream place of the file last opened, and what is made of it in the output: copied,
/// encoded again or dropped, its codec, bitrate and title. Output N carries the sound of every Sub-Lane of stream place N.
/// Nothing is heard through it: the file's sound plays through its clips and their Sub-Lanes, each lane with its own gain,
/// filters and ducking. Its mixer and edits (gain, filters, ducking, voice, slip, silenced pieces) are what a project from
/// before mixed the main video's stream with: the input of the conversion that puts them on Sub-Lanes as a 5.1 project is opened
/// (LegacyMainVideo, which then sets them back to neutral); nothing the export or the player hears reads them.
/// </summary>
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
    [NotifyPropertyChangedFor(nameof(IsMuted))]
    private string _action = Reencode;

    // What the track was doing before it was muted, to go back to.
    private string? _actionBeforeMute;

    /// <summary>
    /// Muted: left out of the mix. It is the same thing as Ignore / Drop, under the name the track rows give it,
    /// where a sound is either heard or not and how it is encoded is not a question asked of each track.
    /// </summary>
    public bool IsMuted
    {
        get => Action == Drop;
        set
        {
            if (value == IsMuted)
                return;

            if (value)
                (_actionBeforeMute, Action) = (Action, Drop);
            else
                Action = _actionBeforeMute is { } before && before != Drop && Actions.Contains(before) ? before : Reencode;
        }
    }

    /// <summary>Whether its codec and bitrate can be chosen: while it is re-encoded.</summary>
    public bool HasOwnFormat => IsReencoded;

    [ObservableProperty] private string _codec = "aac";
    [ObservableProperty] private string _bitrate = "160k";

    // ----- Mixer (legacy: the input of LegacyMainVideo.Migrate, neutral after it; nothing the sound engine hears reads it) -----

    /// <summary>Louder (positive) or quieter, in decibels, as a project from before 5.2-AuditB mixed the stream (now its Sub-Lane's gain).</summary>
    [ObservableProperty] private double _gainDb;

    /// <summary>The stream's processing as a project from before 5.2-AuditB had it (compressor, gate, equalizer...): now its Sub-Lane's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiltersCountText))]
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

    /// <summary>The picture behind the whole row: the track's waveform.</summary>
    public System.Windows.Media.ImageSource? TrackWaveform => Waveform;

    /// <summary>A track runs with the video, at the video's speed, and cannot be taken off the timeline.</summary>
    public bool HasSpeed => false;

    public double ClipSpeed { get => 1; set { } }
    public bool IsRemovable => false;

    // ----- On the timeline (legacy: the input of LegacyMainVideo.Migrate, neutral after it) -----

    /// <summary>How far the stream was slipped against the picture, in seconds (positive is later), before 5.2-AuditB: now baked into its Sub-Lane clips.</summary>
    [ObservableProperty] private double _offsetSeconds;

    /// <summary>The parts the stream was cut into, in its own time, before 5.2-AuditB (now Sub-Lane clips, the silenced ones muted); empty while it is still whole.</summary>
    public IReadOnlyList<AudioPiece> Pieces { get; private set; } = [];

    public void SetEdits(double offset, IEnumerable<AudioPiece>? pieces)
    {
        Pieces = [.. pieces ?? []];
        OffsetSeconds = offset;
        OnPropertyChanged(nameof(Pieces));
    }

    /// <summary>The stream was turned down while a voice spoke, before 5.2-AuditB: now its Sub-Lane's ducking.</summary>
    [ObservableProperty] private bool _autoDuck;

    /// <summary>
    /// The stream was a voice (a microphone, dialogue: what the ducked tracks made room for), before 5.2-AuditB: now its
    /// Sub-Lane clips are (their look's IsVoice).
    /// </summary>
    [ObservableProperty] private bool _isVoice;

    /// <summary>The colour the waveform is drawn in, so that tracks can be told apart in the master view.</summary>
    public string WaveformColor { get; init; } = "#4FC3F7";

    /// <summary>How many filters are on, for the button that opens them to show; nothing when none is.</summary>
    public string FiltersCountText => Filters.ActiveCount > 0 ? Filters.ActiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

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
