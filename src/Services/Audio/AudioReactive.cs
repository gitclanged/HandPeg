using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services.Audio;

// ----- The audio-reactive shader bus (Pass 5.2B) -----
// The Sequence's sound in three bands, frame by frame, for the GPU effects that pulse with it (Film Halation & Glow, Neon Rim Light, Impact
// Shockwave, Anime Lines: their Audio Reactive Pulse and Pulse Band, EffectRegistry.GpuFx.cs). One analyzer, deterministic, for every path
// (AudioBandFollower): the mix made mono; Bass 20-160 Hz, Mid 160 Hz-2 kHz and High 2-10 kHz each between two fourth-order Butterworth
// edges; each band's square followed fast up (5 ms) and slowly down (120 ms), its root taken against a quarter of full scale (a band
// reaching -12 dBFS is 1). The export's frames take theirs from an AudioEnergyMap of the program its sound is rendered from (GpuExport), and
// so do the player's while it can (GpuPreviewEngine: built in the background while an effect pulses, made again when the sound changes), so
// a paused frame is the exported one; else, while playing, the engine's own bands of what it is playing (WasapiAudioEngine.LiveBands), and
// otherwise 0, which pulses nothing. The bands reach the compositor in FrameSceneHeader.AudioBass / AudioMid / AudioHigh.

/// <summary>The three bands' energies of a sound as it goes (see the top of this file). One thread at a time; nothing made while it runs.</summary>
internal sealed class AudioBandFollower
{
    /// <summary>The bands' edges, Hz.</summary>
    public const double BassLow = 20, BassHigh = 160, MidHigh = 2000, HighHigh = 10000;

    /// <summary>The level a band reads 1 at: a quarter of full scale (-12 dBFS).</summary>
    public const double Reference = 0.25;

    // A fourth-order Butterworth edge is two biquads of these Qs.
    private const double FirstQ = 0.54119610014619701, SecondQ = 1.3065629648763764;

    // Per band: its two high-pass sections, then its two low-pass ones.
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

    /// <summary>A block of interleaved stereo.</summary>
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
/// A sound's three bands moment by moment (5.2B): the AudioBandFollower's reading every 5 ms from <see cref="Origin"/> (a second of the
/// Sequence), as the follower stands once the sound before that moment has passed through it, to a 255th. Filled from its start as it is
/// made (on a thread of its own): what is not made yet is not known (<see cref="TryAt"/> false). Read from any thread.
/// </summary>
public sealed class AudioEnergyMap
{
    /// <summary>Readings a second.</summary>
    public const int TicksPerSecond = 200;

    /// <summary>The rate the program is rendered at to be analysed.</summary>
    internal const int Rate = 48000;

    private const int FramesPerTick = Rate / TicksPerSecond;

    private readonly byte[] _bass, _mid, _high;
    private int _ready;

    private AudioEnergyMap(double origin, int ticks)
    {
        Origin = origin;
        (_bass, _mid, _high) = (new byte[ticks], new byte[ticks], new byte[ticks]);
    }

    /// <summary>The second of the Sequence the first reading is of.</summary>
    public double Origin { get; }

    /// <summary>How many readings it holds when it is made.</summary>
    public int Count => _bass.Length;

    /// <summary>Whether all of it is made.</summary>
    public bool IsComplete => Volatile.Read(ref _ready) >= _bass.Length;

    /// <summary>The bands at a moment of the Sequence (the nearest reading); false, and 0, where nothing is known (outside it, not made yet).</summary>
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

    /// <summary>An empty map of a stretch of the Sequence, to be filled by <see cref="Fill"/>.</summary>
    public static AudioEnergyMap Of(double fromSeconds, double toSeconds)
    {
        var from = double.IsFinite(fromSeconds) ? Math.Max(fromSeconds, 0) : 0;
        var seconds = double.IsFinite(toSeconds) ? Math.Clamp(toSeconds - from, 0, 24 * 3600) : 0;
        return new AudioEnergyMap(from, (int)Math.Ceiling(seconds * TicksPerSecond) + 1);
    }

    /// <summary>A program's map of a stretch, made now (off the interface's thread: it decodes and mixes the whole stretch).</summary>
    public static AudioEnergyMap Build(AudioProgram program, double fromSeconds, double toSeconds, CancellationToken token)
    {
        var map = Of(fromSeconds, toSeconds);
        map.Fill(program, token);
        return map;
    }

    /// <summary>Fills the map from a program's mix, rendered by the sound engine from its first moment; throws when cancelled.</summary>
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

    /// <summary>The render's output (interleaved stereo floats) through the follower, a reading at every tick.</summary>
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

/// <summary>What the audio-reactive effects need of a state (5.2B): whether any pulses, the map of its sound, and a key of what it hears.</summary>
public static class AudioReactive
{
    /// <summary>The cards' parameters: the Audio Reactive Pulse (%) and the Pulse Band (Bass, Mid, High, All).</summary>
    public const string PulseKey = EffectRegistry.AudioPulseKey, BandKey = EffectRegistry.AudioBandKey;

    /// <summary>The effects that carry the pulse.</summary>
    public static IReadOnlySet<string> Effects { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        EffectRegistry.Glow, EffectRegistry.NeonRim, EffectRegistry.Shockwave, EffectRegistry.AnimeLines,
    };

    /// <summary>
    /// Whether any card of the state pulses with its sound: one of <see cref="Effects"/>, on, its pulse above 0 (set, or anywhere along its
    /// keyframes), on a clip, a track's look, the Sequence Output or the blur fill. Only then is a map worth its making.
    /// </summary>
    public static bool Pulses(SequenceExportState state)
    {
        foreach (var track in state.Timeline.Tracks)
        {
            if (Pulses(track.TrackLook?.Effects))
                return true;
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

            // In use as the card's packets have it (EffectRegistry.PulseInUse: its keys when it has any, else its value).
            var keyed = card.Keys?.GetValueOrDefault(PulseKey);
            var pulse = card.Values is { } values && values.TryGetValue(PulseKey, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var set) ? set : 0;
            if (EffectRegistry.PulseInUse(keyed, pulse))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The program the bands are taken from: the state's sound as an export hears it (every track, whatever is soloed), mixed to one bus.
    /// The export's frames and the player's paused ones take theirs from the same.
    /// </summary>
    public static AudioProgram ProgramOf(SequenceExportState state) => AudioProgram.Build(state, new AudioBuildOptions { IgnoresSolo = true });

    /// <summary>The map of a state's sound over a stretch of the Sequence, made now (a thread of its own: it decodes and mixes).</summary>
    public static AudioEnergyMap MapOf(SequenceExportState state, double fromSeconds, double toSeconds, CancellationToken token)
    {
        var program = ProgramOf(state);
        return AudioEnergyMap.Build(program, fromSeconds, Math.Min(toSeconds, program.SequenceSeconds), token);
    }

    /// <summary>What a program hears, as one number: a map made of it is still the map of another with the same key.</summary>
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
