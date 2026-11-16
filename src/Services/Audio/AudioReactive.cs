using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services.Audio;

// ----- Audio-reactive shader bus -----
// Sequence audio as three band energies per frame for pulsing GPU effects (Film Halation & Glow, Neon Rim Light, Impact Shockwave, Anime
// Lines: Audio Reactive Pulse and Pulse Band, EffectRegistry.GpuFx.cs). One deterministic analyzer for every path (AudioBandFollower): mono
// mix; Bass 20-160 Hz, Mid 160 Hz-2 kHz, High 2-10 kHz, each between two 4th-order Butterworth edges; each band's square follows fast up
// (5 ms) and slow down (120 ms), rooted against 0.25 full scale (-12 dBFS reads 1). Exports read an AudioEnergyMap of the rendered program
// (GpuExport); the player uses the same map when ready (built in the background while an effect pulses, rebuilt on audio changes) so paused
// frames match the export, else the engine's live bands while playing (WasapiAudioEngine.LiveBands), else 0. Bands reach the compositor in
// FrameSceneHeader.AudioBass / AudioMid / AudioHigh.

/// <summary>Streaming three-band energy follower (see file header). Single-threaded; allocation-free while running.</summary>
internal sealed class AudioBandFollower
{
    /// <summary>Band edges, Hz.</summary>
    public const double BassLow = 20, BassHigh = 160, MidHigh = 2000, HighHigh = 10000;

    /// <summary>Level that reads 1: a quarter of full scale (-12 dBFS).</summary>
    public const double Reference = 0.25;

    // A 4th-order Butterworth edge = two biquads with these Qs.
    private const double FirstQ = 0.54119610014619701, SecondQ = 1.3065629648763764;

    // Per band: two high-pass sections, then two low-pass sections.
    private readonly RbjSection[] _sections = new RbjSection[12];
    private readonly double _attack, _release;
    private double _bass, _mid, _high;

    public AudioBandFollower(int sampleRate)
    {
        var rate = Math.Max(sampleRate, 8000);
        (double Low, double High)[] edges = [(BassLow, BassHigh), (BassHigh, MidHigh), (MidHigh, HighHigh)];
        for (var band = 0; band < 3; band++)
        {
            var (low, high) = edges[band];
            _sections[4 * band] = RbjSection.HighPass(low, FirstQ, rate);
            _sections[4 * band + 1] = RbjSection.HighPass(low, SecondQ, rate);
            _sections[4 * band + 2] = RbjSection.LowPass(Math.Min(high, rate * 0.45), FirstQ, rate);
            _sections[4 * band + 3] = RbjSection.LowPass(Math.Min(high, rate * 0.45), SecondQ, rate);
        }

        _attack = 1 - Math.Exp(-1 / (0.005 * rate));
        _release = 1 - Math.Exp(-1 / (0.120 * rate));
    }

    public float Bass => Energy(_bass);

    public float Mid => Energy(_mid);

    public float High => Energy(_high);

    private static float Energy(double meanSquare) => (float)Math.Min(Math.Sqrt(meanSquare) / Reference, 1);

    /// <summary>Processes a block of interleaved stereo.</summary>
    public void Process(ReadOnlySpan<float> stereo, int frames)
    {
        var sections = _sections;
        var (bass, mid, high, attack, release) = (_bass, _mid, _high, _attack, _release);
        for (var i = 0; i < frames; i++)
        {
            var x = 0.5 * (stereo[2 * i] + (double)stereo[2 * i + 1]);
            var b = sections[3].Next(sections[2].Next(sections[1].Next(sections[0].Next(x))));
            var m = sections[7].Next(sections[6].Next(sections[5].Next(sections[4].Next(x))));
            var h = sections[11].Next(sections[10].Next(sections[9].Next(sections[8].Next(x))));
            (b, m, h) = (b * b, m * m, h * h);
            bass += (b - bass) * (b > bass ? attack : release);
            mid += (m - mid) * (m > mid ? attack : release);
            high += (h - high) * (h > high ? attack : release);
        }

        for (var k = 0; k < sections.Length; k++)
            sections[k].Flush();
        (_bass, _mid, _high) = (bass < 1e-30 ? 0 : bass, mid < 1e-30 ? 0 : mid, high < 1e-30 ? 0 : high);
    }

    public void Reset()
    {
        for (var k = 0; k < _sections.Length; k++)
            _sections[k].Clear();
        (_bass, _mid, _high) = (0, 0, 0);
    }
}

/// <summary>
/// A sound's three band energies over time: the follower's state every 5 ms from <see cref="Origin"/> (sequence seconds), quantized to
/// 1/255. Filled progressively from the start on its own thread; unfilled ticks are unknown (<see cref="TryAt"/> false). Reads are thread-safe.
/// </summary>
public sealed class AudioEnergyMap
{
    /// <summary>Readings per second.</summary>
    public const int TicksPerSecond = 200;

    /// <summary>Sample rate of the analysis render.</summary>
    internal const int Rate = 48000;

    private const int FramesPerTick = Rate / TicksPerSecond;

    private readonly byte[] _bass, _mid, _high;
    private int _ready;

    private AudioEnergyMap(double origin, int ticks)
    {
        Origin = origin;
        (_bass, _mid, _high) = (new byte[ticks], new byte[ticks], new byte[ticks]);
    }

    /// <summary>Sequence time of the first reading.</summary>
    public double Origin { get; }

    /// <summary>Total readings when complete.</summary>
    public int Count => _bass.Length;

    /// <summary>True when every reading is filled.</summary>
    public bool IsComplete => Volatile.Read(ref _ready) >= _bass.Length;

    /// <summary>Band energies at a sequence time (nearest reading); false and 0 when unknown (outside the map or not yet filled).</summary>
    public bool TryAt(double sequenceSeconds, out float bass, out float mid, out float high)
    {
        var at = (sequenceSeconds - Origin) * TicksPerSecond;
        var tick = double.IsFinite(at) && at > -0.5 && at < int.MaxValue / 2 ? (int)Math.Round(at) : -1;
        if (tick < 0 || tick >= Volatile.Read(ref _ready))
        {
            (bass, mid, high) = (0, 0, 0);
            return false;
        }

        (bass, mid, high) = (_bass[tick] / 255f, _mid[tick] / 255f, _high[tick] / 255f);
        return true;
    }

    /// <summary>Empty map over a sequence range, filled later by <see cref="Fill"/>.</summary>
    public static AudioEnergyMap Of(double fromSeconds, double toSeconds)
    {
        var from = double.IsFinite(fromSeconds) ? Math.Max(fromSeconds, 0) : 0;
        var seconds = double.IsFinite(toSeconds) ? Math.Clamp(toSeconds - from, 0, 24 * 3600) : 0;
        return new AudioEnergyMap(from, (int)Math.Ceiling(seconds * TicksPerSecond) + 1);
    }

    /// <summary>Builds and fills a map now (call off the UI thread: decodes and mixes the whole range).</summary>
    public static AudioEnergyMap Build(AudioProgram program, double fromSeconds, double toSeconds, CancellationToken token)
    {
        var map = Of(fromSeconds, toSeconds);
        map.Fill(program, token);
        return map;
    }

    /// <summary>Fills the map from the program's mix, rendered by the sound engine from <see cref="Origin"/>; throws when cancelled.</summary>
    public void Fill(AudioProgram program, CancellationToken token)
    {
        if (_bass.Length <= 1)
        {
            Volatile.Write(ref _ready, _bass.Length);
            return;
        }

        using var analyzer = new Analyzer(this);
        AudioMixer.RenderTo(program, Origin, (long)(_bass.Length - 1) * FramesPerTick, Rate, analyzer, null, token);
    }

    private void Record(int tick, AudioBandFollower follower)
    {
        if (tick >= _bass.Length)
            return;
        (_bass[tick], _mid[tick], _high[tick]) = (Quantized(follower.Bass), Quantized(follower.Mid), Quantized(follower.High));
        Volatile.Write(ref _ready, tick + 1);
    }

    private static byte Quantized(float energy) => (byte)Math.Clamp((int)MathF.Round(energy * 255), 0, 255);

    /// <summary>Stream sink that feeds the render's interleaved stereo floats through the follower, recording at every tick.</summary>
    private sealed class Analyzer : Stream
    {
        private readonly AudioEnergyMap _map;
        private readonly AudioBandFollower _follower = new(Rate);
        private int _inTick, _tick;

        public Analyzer(AudioEnergyMap map)
        {
            _map = map;
            map.Record(_tick++, _follower);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            var frames = samples.Length / 2;
            for (var at = 0; at < frames;)
            {
                var take = Math.Min(FramesPerTick - _inTick, frames - at);
                _follower.Process(samples.Slice(at * 2, take * 2), take);
                (_inTick, at) = (_inTick + take, at + take);
                if (_inTick == FramesPerTick)
                {
                    _inTick = 0;
                    _map.Record(_tick++, _follower);
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Audio-reactive helpers for a state: whether anything pulses, its energy map, and a content key for its program.</summary>
public static class AudioReactive
{
    /// <summary>Card parameters: Audio Reactive Pulse (%) and Pulse Band (Bass, Mid, High, All).</summary>
    public const string PulseKey = EffectRegistry.AudioPulseKey, BandKey = EffectRegistry.AudioBandKey;

    /// <summary>Effects that support the pulse.</summary>
    public static IReadOnlySet<string> Effects { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        EffectRegistry.Glow, EffectRegistry.NeonRim, EffectRegistry.Shockwave, EffectRegistry.AnimeLines,
    };

    /// <summary>
    /// True when any enabled card in <see cref="Effects"/> has a pulse above 0 (static or anywhere in its keyframes) on a clip, the Sequence
    /// Output or the blur fill. Only then is a map worth building.
    /// </summary>
    public static bool Pulses(SequenceExportState state)
    {
        foreach (var track in state.Timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (Pulses(clip.State.Effects))
                    return true;
            }
        }

        return Pulses(state.Timeline.SequenceEffects) || Pulses(state.BackgroundEffects);
    }

    private static bool Pulses(IReadOnlyList<EffectState>? cards)
    {
        if (cards is null)
            return false;

        foreach (var card in cards)
        {
            if (!card.On || !Effects.Contains(card.Type))
                continue;

            // In use as the packets see it (EffectRegistry.PulseInUse: keyframes when present, else the static value).
            var keyed = card.Keys?.GetValueOrDefault(PulseKey);
            var pulse = card.Values is { } values && values.TryGetValue(PulseKey, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var set) ? set : 0;
            if (EffectRegistry.PulseInUse(keyed, pulse))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Program the bands come from: the state's audio as the export hears it (all tracks, solo ignored) on one bus. Export frames and paused
    /// player frames share it.
    /// </summary>
    public static AudioProgram ProgramOf(SequenceExportState state) => AudioProgram.Build(state, new AudioBuildOptions { IgnoresSolo = true });

    /// <summary>Builds a state's energy map over a sequence range now (run on its own thread: decodes and mixes).</summary>
    public static AudioEnergyMap MapOf(SequenceExportState state, double fromSeconds, double toSeconds, CancellationToken token)
    {
        var program = ProgramOf(state);
        return AudioEnergyMap.Build(program, fromSeconds, Math.Min(toSeconds, program.SequenceSeconds), token);
    }

    /// <summary>Content key of a program: a map built for one program stays valid for another with the same key.</summary>
    public static int KeyOf(AudioProgram program)
    {
        var hash = new HashCode();
        hash.Add(program.SequenceSeconds);
        hash.Add(program.FadeIn);
        hash.Add(program.FadeOut);
        hash.Add(program.MasterGain);
        hash.Add(program.Duck);
        foreach (var voice in program.VoiceArray)
        {
            hash.Add(voice.Source);
            hash.Add(voice.SequenceStart);
            hash.Add(voice.SequenceEnd);
            hash.Add(voice.MediaStart);
            hash.Add(voice.MediaEnd);
            hash.Add(voice.Rate);
            hash.Add(voice.Pitch);
            hash.Add(voice.Spectral);
            hash.Add(voice.Gain);
            hash.Add(voice.FadeIn);
            hash.Add(voice.FadeOut);
            hash.Add(voice.FadeOutEnd);
            hash.Add(voice.Strip);
        }

        foreach (var strip in program.StripArray)
        {
            hash.Add(strip.Key);
            hash.Add(strip.IsVoice);
            hash.Add(strip.Ducked);
            foreach (var effect in strip.Effects)
                hash.Add(effect);
        }

        return hash.ToHashCode();
    }
}
