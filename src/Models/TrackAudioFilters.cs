using System.Globalization;

namespace HandPegApp.Models;

/// <summary>
/// The processing applied to one audio track before it is encoded: each filter can be switched on by
/// itself, and the ones that are on run in a fixed, sensible order.
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

    public bool IsActive => Compressor || NoiseGate || DeNoise || BassTreble || HighPass || Limiter || Equalizer || PitchTempo || StereoWidener;

    /// <summary>How many filters are switched on, for the button that opens the dialog to show.</summary>
    public int ActiveCount =>
        new[] { Compressor, NoiseGate, DeNoise, BassTreble, HighPass, Limiter, Equalizer, PitchTempo, StereoWidener }.Count(on => on);

    public TrackAudioFilters Clone()
    {
        var copy = (TrackAudioFilters)MemberwiseClone();
        copy.EqualizerGains = [.. EqualizerGains.Concat(new double[10]).Take(10)];
        return copy;
    }

    /// <summary>
    /// The FFmpeg audio filters for what is switched on. Cleaning comes first (rumble, noise between words,
    /// steady noise), then dynamics, then tone, then the effects; a gain is applied after them, and the
    /// limiter last of all so that nothing before it can push the track into clipping.
    /// </summary>
    /// <param name="gainDb">The track's gain from the mixer; 0 adds no filter.</param>
    public List<string> BuildChain(double gainDb)
    {
        var chain = new List<string>();

        if (HighPass)
            chain.Add($"highpass=f={Number(Math.Clamp(HighPassHz, 20, 1000))}");
        if (NoiseGate)
            chain.Add($"agate=threshold={Number(Math.Clamp(GateThreshold, -80, 0))}dB");
        if (DeNoise)
            chain.Add($"afftdn=nr={Number(Math.Clamp(DeNoiseStrength, 1, 60))}");
        if (Compressor)
            chain.Add($"acompressor=threshold={Number(Math.Clamp(CompressorThreshold, -60, 0))}dB:ratio={Number(Math.Clamp(CompressorRatio, 1, 20))}");

        if (BassTreble)
        {
            if (Math.Abs(BassGain) >= 0.05)
                chain.Add($"bass=g={Number(Math.Clamp(BassGain, -20, 20))}");
            if (Math.Abs(TrebleGain) >= 0.05)
                chain.Add($"treble=g={Number(Math.Clamp(TrebleGain, -20, 20))}");
        }

        if (Equalizer)
        {
            // One band filter per slider that has been moved, each an octave wide.
            for (var band = 0; band < EqualizerBands.Count && band < EqualizerGains.Length; band++)
            {
                if (Math.Abs(EqualizerGains[band]) >= 0.05)
                    chain.Add($"equalizer=f={EqualizerBands[band]}:t=o:w=1:g={Number(Math.Clamp(EqualizerGains[band], -20, 20))}");
            }
        }

        if (PitchTempo)
        {
            // Playing the samples faster raises the pitch and shortens the sound together; atempo then puts
            // the speed where it is wanted without touching the pitch again.
            var pitch = Math.Pow(2, Math.Clamp(PitchSemitones, -12, 12) / 12);
            var speed = Math.Clamp(Tempo, 0.5, 2) / pitch;
            if (Math.Abs(pitch - 1) >= 0.001)
                chain.Add($"aresample=48000,asetrate={Number(48000 * pitch)},aresample=48000");

            // One atempo covers 0.5 to 100; slower than that takes two.
            if (speed < 0.5)
            {
                chain.Add("atempo=0.5");
                speed /= 0.5;
            }

            if (Math.Abs(speed - 1) >= 0.001)
                chain.Add($"atempo={Number(speed)}");
        }

        if (StereoWidener)
            chain.Add($"extrastereo=m={Number(Math.Clamp(StereoWidth, 0, 4))}");

        if (Math.Abs(gainDb) >= 0.05)
            chain.Add($"volume={Number(gainDb)}dB");

        if (Limiter)
            chain.Add($"alimiter=limit={Number(Math.Pow(10, Math.Clamp(LimiterCeiling, -24, 0) / 20))}:level=0");

        return chain;
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
