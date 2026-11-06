using System.Text.Json.Serialization;

namespace HandPegApp.Models;

/// <summary>
/// The processing applied to one audio track before it is encoded: each filter can be switched on by
/// itself, and the ones that are on run in a fixed, sensible order (the sound engine's effects made of them:
/// Services/Audio/AudioEffects.cs, AudioChainBuilder).
/// </summary>
public sealed class TrackAudioFilters
{
    /// <summary>Centre frequencies of the ten equalizer bands, an octave apart.</summary>
    public static IReadOnlyList<int> EqualizerBands { get; } = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    // Basic
    public bool Compressor { get; set; }
    public double CompressorThreshold { get; set; } = -18;
    public double CompressorRatio { get; set; } = 4;

    public bool NoiseGate { get; set; }
    public double GateThreshold { get; set; } = -40;

    public bool DeNoise { get; set; }
    public double DeNoiseStrength { get; set; } = 12;

    public bool BassTreble { get; set; }
    public double BassGain { get; set; }
    public double TrebleGain { get; set; }

    public bool HighPass { get; set; }
    public double HighPassHz { get; set; } = 80;

    // Advanced
    public bool ShowAdvanced { get; set; }

    public bool Limiter { get; set; }
    public double LimiterCeiling { get; set; } = -1;

    public bool Equalizer { get; set; }
    public double[] EqualizerGains { get; set; } = new double[10];

    public bool PitchTempo { get; set; }
    public double PitchSemitones { get; set; }
    public double Tempo { get; set; } = 1;

    public bool StereoWidener { get; set; }
    public double StereoWidth { get; set; } = 1.5;

    // Studio Voice (Pass 5.2B): the one-click vocal chain, blended with the dry sound (Services/Audio/StudioVoice.cs).
    public bool StudioVoice { get; set; }

    /// <summary>How much of the chain's sound is heard, in percent (0 the dry sound, 100 the chain's alone).</summary>
    public double StudioVoiceMix { get; set; } = 85;

    /// <summary>
    /// The mix's keyframes (percent values), or null (or none) for the plain <see cref="StudioVoiceMix"/>. Times are seconds from the start of
    /// what the filters belong to: a clip's filters from the clip's Start on the Sequence, a track's from the Sequence's 0. Left out of the
    /// file while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? StudioVoiceMixKeys { get; set; }

    // De-Esser (Pass 5.2B): a split-band compressor on 5.5 to 9 kHz, how hard it works in percent.
    public bool DeEsser { get; set; }
    public double DeEsserAmount { get; set; } = 50;

    /// <summary>
    /// The parametric equalizer (Pass 5.2B): where each of the ten bands is, in Hz (each held to 20..20000), or null for the octave centres
    /// (<see cref="EqualizerBands"/>), as before. Left out of the file while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? EqualizerFrequencies { get; set; }

    /// <summary>How wide each band is, in octaves (each held to 0.1..4), or null for one octave each, as before. Left out of the file while null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? EqualizerWidths { get; set; }

    public bool IsActive => Compressor || NoiseGate || DeNoise || BassTreble || HighPass || Limiter || Equalizer || PitchTempo || StereoWidener || StudioVoice || DeEsser;

    /// <summary>
    /// Whether Studio Voice is in the chain (5.2B): switched on, with mix keys or a mix above 0 (to the four decimals the chain writes it
    /// with). At a mix of 0 with no keys AudioChainBuilder leaves it out, so the dry sound is untouched. Not written to the file.
    /// </summary>
    [JsonIgnore]
    public bool StudioVoiceInChain =>
        StudioVoice && (StudioVoiceMixKeys is { Count: > 0 } || Math.Round(Math.Clamp(StudioVoiceMix, 0, 100), 4, MidpointRounding.AwayFromZero) > 0);

    /// <summary>
    /// Whether the filters change the sound at all (5.2B): <see cref="IsActive"/>, with Studio Voice counted only while it is in the chain
    /// (<see cref="StudioVoiceInChain"/>). What the sound engine decides by: a clip's strip of its own, a sound copied as it is, the chain
    /// itself. <see cref="IsActive"/> and <see cref="ActiveCount"/> stay for the button's badge (a switch the user turned on). Not written to the file.
    /// </summary>
    [JsonIgnore]
    public bool Processes =>
        Compressor || NoiseGate || DeNoise || BassTreble || HighPass || Limiter || Equalizer || PitchTempo || StereoWidener || StudioVoiceInChain || DeEsser;

    /// <summary>How many filters are switched on, for the button that opens the dialog to show.</summary>
    public int ActiveCount =>
        new[] { Compressor, NoiseGate, DeNoise, BassTreble, HighPass, Limiter, Equalizer, PitchTempo, StereoWidener, StudioVoice, DeEsser }.Count(on => on);

    public TrackAudioFilters Clone()
    {
        var copy = (TrackAudioFilters)MemberwiseClone();
        copy.EqualizerGains = [.. EqualizerGains.Concat(new double[10]).Take(10)];
        copy.StudioVoiceMixKeys = StudioVoiceMixKeys is null ? null : [.. StudioVoiceMixKeys];
        copy.EqualizerFrequencies = EqualizerFrequencies is null ? null : [.. EqualizerFrequencies];
        copy.EqualizerWidths = EqualizerWidths is null ? null : [.. EqualizerWidths];
        return copy;
    }
}
