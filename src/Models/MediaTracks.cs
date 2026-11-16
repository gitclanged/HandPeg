using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>One line of a label/value card in the Properties tab.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>
/// A segment of a cut audio track: start/end in track time and whether it is silenced.
/// </summary>
public readonly record struct AudioPiece(double Start, double End, bool Muted);

/// <summary>
/// Output audio track for a stream index of the last opened file: copy, re-encode or drop, plus codec, bitrate and title. Output N
/// carries every Sub-Lane of stream N; audio itself plays through clips and Sub-Lanes (own gain, filters, ducking). The mixer and edit
/// fields (gain, filters, ducking, voice, slip, silenced ranges) are legacy input for the LegacyMainVideo Sub-Lane migration, neutral
/// afterwards and never read by playback or export.
/// </summary>
public sealed partial class AudioTrack : ObservableObject
{
    public const string Passthrough = "Passthrough (Copy)";
    public const string Reencode = "Re-encode";
    public const string Drop = "Ignore / Drop";

    public static IReadOnlyList<string> AllActions { get; } = [Passthrough, Reencode, Drop];
    /// <summary>Offered audio encoders (including lossless FLAC and PCM).</summary>
    public static IReadOnlyList<string> AllCodecs { get; } = ["aac", "libopus", "libmp3lame", "ac3", "flac", "pcm_s16le"];

    /// <summary>True for lossless codecs without a bitrate (PCM, FLAC); no bitrate is passed or shown.</summary>
    public static bool IsLossless(string codec) => codec == "flac" || codec.StartsWith("pcm_", StringComparison.Ordinal);
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
    /// Row subtitle: 1-based stream number, optional title, language and format, e.g. "Stream #2 (MIC)  ·  aac, stereo".
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
    /// Muted (excluded from the mix): the track-row name for Ignore / Drop.
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

    // ----- Mixer (legacy LegacyMainVideo.Migrate input; neutral afterwards, never read by the audio engine) -----

    /// <summary>Legacy stream gain in dB (now the Sub-Lane gain).</summary>
    [ObservableProperty] private double _gainDb;

    /// <summary>Legacy stream processing (compressor, gate, EQ, ...), now on the Sub-Lane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiltersCountText))]
    private TrackAudioFilters _filters = new();

    // ----- Audio tab row adapter -----
    // One row template serves both these tracks and file-based audio layers (see Layer); these members bridge the differences.

    /// <summary>One of the video's own audio streams, with a codec, filters and a solo button of its own.</summary>
    public bool IsStream => true;

    /// <summary>A track runs with the video, at the video's speed, and cannot be taken off the timeline.</summary>
    public bool HasSpeed => false;

    public double ClipSpeed { get => 1; set { } }
    public bool IsRemovable => false;

    // ----- On the timeline (legacy: the input of LegacyMainVideo.Migrate, neutral after it) -----

    /// <summary>Legacy audio slip in seconds (positive = later), now baked into Sub-Lane clips.</summary>
    [ObservableProperty] private double _offsetSeconds;

    /// <summary>Legacy cut segments in stream time (now Sub-Lane clips, silenced ones muted); empty when uncut.</summary>
    public IReadOnlyList<AudioPiece> Pieces { get; private set; } = [];

    public void SetEdits(double offset, IEnumerable<AudioPiece>? pieces)
    {
        Pieces = [.. pieces ?? []];
        OffsetSeconds = offset;
        OnPropertyChanged(nameof(Pieces));
    }

    /// <summary>Legacy ducking flag, now Sub-Lane ducking.</summary>
    [ObservableProperty] private bool _autoDuck;

    /// <summary>
    /// Legacy voice flag (mic/dialogue that ducked tracks yield to), now IsVoice on Sub-Lane clips.
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
