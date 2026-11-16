using System.Text.Json.Serialization;

namespace HandPegApp.Models;

/// <summary>
/// Per-track audio processing: independently toggled filters run in a fixed order (Services/Audio/AudioEffects.cs, AudioChainBuilder).
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

    // Studio Voice: one-click vocal chain blended with the dry signal (Services/Audio/StudioVoice.cs).
    public bool StudioVoice { get; set; }

    /// <summary>How much of the chain's sound is heard, in percent (0 the dry sound, 100 the chain's alone).</summary>
    public double StudioVoiceMix { get; set; } = 85;

    /// <summary>
    /// Mix keyframes (percent), or null/empty for static <see cref="StudioVoiceMix"/>. Times in seconds from the owner's start (clip Start,
    /// or Sequence 0 for tracks). Omitted when null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? StudioVoiceMixKeys { get; set; }

    /// <summary>Keys closer than this (s) are merged; setting one there replaces its value.</summary>
    public const double MixKeyTolerance = 0.001;

    /// <summary>Sets a mix key at <paramref name="at"/> to the current mix (replacing any there), keeping time order.</summary>
    public void PutMixKey(double at)
    {
        var keys = StudioVoiceMixKeys ??= [];
        keys.RemoveAll(k => Math.Abs(k.Time - at) < MixKeyTolerance);
        keys.Add(new Keyframe(at, Math.Clamp(StudioVoiceMix, 0, 100), EasingType.Linear));
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    // De-Esser: split-band compressor on the sibilance band (5500-9000 Hz); amount in percent.
    public bool DeEsser { get; set; }
    public double DeEsserAmount { get; set; } = 50;

    /// <summary>
    /// Parametric EQ centre frequencies for ten bands (Hz, clamped 20-20000), or null for octave centres (<see cref="EqualizerBands"/>).
    /// Omitted when null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? EqualizerFrequencies { get; set; }

    /// <summary>Band widths in octaves (clamped 0.1-4), or null for one octave each. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? EqualizerWidths { get; set; }

    public bool IsActive => Compressor || NoiseGate || DeNoise || BassTreble || HighPass || Limiter || Equalizer || PitchTempo || StereoWidener || StudioVoice || DeEsser;

    /// <summary>
    /// True when Studio Voice is enabled with keys or a mix above 0 (at chain precision); otherwise AudioChainBuilder omits it so dry
    /// audio is untouched. Not serialized.
    /// </summary>
    [JsonIgnore]
    public bool StudioVoiceInChain =>
        StudioVoice && (StudioVoiceMixKeys is { Count: > 0 } || Math.Round(Math.Clamp(StudioVoiceMix, 0, 100), 4, MidpointRounding.AwayFromZero) > 0);

    /// <summary>
    /// True when the filters alter audio: <see cref="IsActive"/> with Studio Voice counted only when <see cref="StudioVoiceInChain"/>.
    /// Drives audio-engine decisions (own strip, stream copy, chain); <see cref="IsActive"/> and <see cref="ActiveCount"/> feed the button
    /// badge. Not serialized.
    /// </summary>
    [JsonIgnore]
    public bool Processes =>
        Compressor || NoiseGate || DeNoise || BassTreble || HighPass || Limiter || Equalizer || PitchTempo || StereoWidener || StudioVoiceInChain || DeEsser;

    /// <summary>How many filters are switched on, for the button that opens the dialog to show.</summary>
    public int ActiveCount =>
        new[] { Compressor, NoiseGate, DeNoise, BassTreble, HighPass, Limiter, Equalizer, PitchTempo, StereoWidener, StudioVoice, DeEsser }.Count(on => on);

    /// <summary>
    /// True when every switch and value is default. Non-default sets persist through saves, undo and cold tabs even with all filters
    /// off (e.g. a bypassed compressor keeps its threshold). Not serialized.
    /// </summary>
    [JsonIgnore]
    public bool IsDefault =>
        !Compressor && CompressorThreshold == -18 && CompressorRatio == 4 && !NoiseGate && GateThreshold == -40 && !DeNoise && DeNoiseStrength == 12
        && !BassTreble && BassGain == 0 && TrebleGain == 0 && !HighPass && HighPassHz == 80 && !ShowAdvanced && !Limiter && LimiterCeiling == -1
        && !Equalizer && GainsAreDefault(EqualizerGains) && !PitchTempo && PitchSemitones == 0 && Tempo == 1 && !StereoWidener && StereoWidth == 1.5
        && !StudioVoice && StudioVoiceMix == 85 && StudioVoiceMixKeys is null && !DeEsser && DeEsserAmount == 50
        && EqualizerFrequencies is null && EqualizerWidths is null;

    /// <summary>Default gains: exactly ten bands, all 0 dB (a shorter, longer or null array is a non-default set, as when serialized).</summary>
    private static bool GainsAreDefault(double[]? gains)
    {
        if (gains is not { Length: 10 })
            return false;
        foreach (var gain in gains)
        {
            if (gain != 0)
                return false;
        }

        return true;
    }

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
