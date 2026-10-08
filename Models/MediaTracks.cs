using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp.Models;

/// <summary>One line of a label/value card in the Properties tab.</summary>
public sealed record InfoRow(string Label, string Value);

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

        var language = stream.Language.Length > 0 && stream.Language != "und" ? $" [{stream.Language}]" : "";
        var layout = stream.ChannelLayout.Length > 0 ? stream.ChannelLayout : $"{stream.Channels} ch";
        Description = $"#{stream.Index}{language}  ·  {stream.Codec}, {layout}";
    }

    /// <summary>The name written to the output as the track's title. Starts as the source's own.</summary>
    [ObservableProperty] private string _title;

    public AudioStreamInfo Stream { get; }

    /// <summary>Position among the audio streams, as used in "0:a:N".</summary>
    public int Index => Stream.Index;

    public string Description { get; }

    // Instance accessors so the row template can bind to them.
    public IReadOnlyList<string> Actions => AllActions;
    public IReadOnlyList<string> Codecs => AllCodecs;
    public IReadOnlyList<string> Bitrates => AllBitrates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReencoded))]
    private string _action = Reencode;

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
    [ObservableProperty] private System.Windows.Media.ImageSource? _waveform;

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
