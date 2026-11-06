using System.Collections.Concurrent;
using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

// ----- The audio chain as it was made until 5.2-AuditB: the oracle of audio-chain-typed -----
// What src deleted when a track's processing became typed (AudioChainBuilder.From): TrackAudioFilters.BuildChain, which wrote a
// track's settings as FFmpeg's words for them, and AudioChainParser, which read those words back into a strip's effects with
// each filter's FFmpeg defaults. Copied as they were (renamed so nothing clashes: BuildChain a static of the settings, the old
// AudioChain OracleChain), so the typed chain can be held to the very numbers the words gave, and the FFmpeg parity checks can
// still hand FFmpeg the words for the same settings.

/// <summary>A filter chain read back from FFmpeg's text: the effects, and what changes a voice's time (pitch, tempo); what it does not play.</summary>
internal sealed record OracleChain(AudioEffectSpec[] Effects, double Pitch, double Tempo, string[] Unsupported)
{
    public static OracleChain None { get; } = new([], 1, 1, []);
}

/// <summary>TrackAudioFilters.BuildChain and AudioChainParser as they were before 5.2-AuditB (see the file's header).</summary>
internal static class AudioChainOracle
{
    /// <summary>
    /// The FFmpeg audio filters for what is switched on. Cleaning comes first (rumble, noise between words,
    /// steady noise), then dynamics, then tone, then the effects; a gain is applied after them, and the
    /// limiter last of all so that nothing before it can push the track into clipping.
    /// </summary>
    /// <param name="gainDb">The track's gain from the mixer; 0 adds no filter.</param>
    public static List<string> BuildChain(TrackAudioFilters f, double gainDb)
    {
        var chain = new List<string>();

        if (f.HighPass)
            chain.Add($"highpass=f={Written(Math.Clamp(f.HighPassHz, 20, 1000))}");
        if (f.NoiseGate)
            chain.Add($"agate=threshold={Written(Math.Clamp(f.GateThreshold, -80, 0))}dB");
        if (f.DeNoise)
            chain.Add($"afftdn=nr={Written(Math.Clamp(f.DeNoiseStrength, 1, 60))}");
        if (f.Compressor)
            chain.Add($"acompressor=threshold={Written(Math.Clamp(f.CompressorThreshold, -60, 0))}dB:ratio={Written(Math.Clamp(f.CompressorRatio, 1, 20))}");

        if (f.BassTreble)
        {
            if (Math.Abs(f.BassGain) >= 0.05)
                chain.Add($"bass=g={Written(Math.Clamp(f.BassGain, -20, 20))}");
            if (Math.Abs(f.TrebleGain) >= 0.05)
                chain.Add($"treble=g={Written(Math.Clamp(f.TrebleGain, -20, 20))}");
        }

        if (f.Equalizer)
        {
            // One band filter per slider that has been moved, each an octave wide.
            var bands = TrackAudioFilters.EqualizerBands;
            for (var band = 0; band < bands.Count && band < f.EqualizerGains.Length; band++)
            {
                if (Math.Abs(f.EqualizerGains[band]) >= 0.05)
                    chain.Add($"equalizer=f={bands[band]}:t=o:w=1:g={Written(Math.Clamp(f.EqualizerGains[band], -20, 20))}");
            }
        }

        if (f.PitchTempo)
        {
            // Playing the samples faster raises the pitch and shortens the sound together; atempo then puts
            // the speed where it is wanted without touching the pitch again.
            var pitch = Math.Pow(2, Math.Clamp(f.PitchSemitones, -12, 12) / 12);
            var speed = Math.Clamp(f.Tempo, 0.5, 2) / pitch;
            if (Math.Abs(pitch - 1) >= 0.001)
                chain.Add($"aresample=48000,asetrate={Written(48000 * pitch)},aresample=48000");

            // One atempo covers 0.5 to 100; slower than that takes two.
            if (speed < 0.5)
            {
                chain.Add("atempo=0.5");
                speed /= 0.5;
            }

            if (Math.Abs(speed - 1) >= 0.001)
                chain.Add($"atempo={Written(speed)}");
        }

        if (f.StereoWidener)
            chain.Add($"extrastereo=m={Written(Math.Clamp(f.StereoWidth, 0, 4))}");

        if (Math.Abs(gainDb) >= 0.05)
            chain.Add($"volume={Written(gainDb)}dB");

        if (f.Limiter)
            chain.Add($"alimiter=limit={Written(Math.Pow(10, Math.Clamp(f.LimiterCeiling, -24, 0) / 20))}:level=0");

        return chain;
    }

    // (BuildChain's Number.)
    private static string Written(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // ----- AudioChainParser -----

    private static readonly ConcurrentDictionary<string, OracleChain> Read = new(StringComparer.Ordinal);

    public static OracleChain Parse(IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
            return OracleChain.None;
        var key = string.Join(",", filters);
        if (Read.TryGetValue(key, out var known))
            return known;

        var chain = ParseFresh(key);
        if (Read.Count > 512)
            Read.Clear();
        Read[key] = chain;
        return chain;
    }

    private static OracleChain ParseFresh(string text)
    {
        var effects = new List<AudioEffectSpec>();
        var unsupported = new List<string>();
        var (pitch, tempo, rate) = (1.0, 1.0, 48000.0);
        foreach (var filter in SplitTopLevel(text, ','))
        {
            var equals = filter.IndexOf('=');
            var name = (equals < 0 ? filter : filter[..equals]).Trim();
            var options = Options(equals < 0 ? "" : filter[(equals + 1)..]);
            double Get(string a, string b, double fallback) =>
                options.TryGetValue(a, out var v) && Number(v, out var n) ? n : options.TryGetValue(b, out v) && Number(v, out n) ? n : fallback;

            switch (name)
            {
                case "volume":
                    if (options.ContainsKey("enable"))
                        unsupported.Add(filter);
                    else
                        effects.Add(AudioEffectSpec.Gain(Get("volume", "", 1)));
                    break;
                case "highpass":
                case "lowpass":
                    effects.Add(Biquad(name == "highpass" ? AudioEffectKind.HighPass : AudioEffectKind.LowPass, Get("frequency", "f", name == "highpass" ? 3000 : 500), Get("width", "w", 0.707), WidthType(options, 3), 0));
                    break;
                case "bass":
                case "lowshelf":
                    effects.Add(Biquad(AudioEffectKind.LowShelf, Get("frequency", "f", 100), Get("width", "w", 0.5), WidthType(options, 3), DbOrPlain(options, "gain", "g"), name == "bass"));
                    break;
                case "treble":
                case "highshelf":
                    effects.Add(Biquad(AudioEffectKind.HighShelf, Get("frequency", "f", 3000), Get("width", "w", 0.5), WidthType(options, 3), DbOrPlain(options, "gain", "g"), name == "treble"));
                    break;
                case "equalizer":
                    effects.Add(Biquad(AudioEffectKind.Peaking, Get("frequency", "f", 0), Get("width", "w", 1), WidthType(options, 3), DbOrPlain(options, "gain", "g")));
                    break;
                case "acompressor":
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Compressor, Math.Clamp(Get("threshold", "", 0.125), 0.000976563, 1), Math.Clamp(Get("ratio", "", 2), 1, 20),
                        Math.Clamp(Get("attack", "", 20), 0.01, 2000), Math.Clamp(Get("release", "", 250), 0.01, 9000), Math.Clamp(Get("knee", "", 2.82843), 1, 8),
                        Math.Clamp(Get("makeup", "", 1), 1, 64), Math.Clamp(Get("mix", "", 1), 0, 1), Math.Clamp(Get("level_in", "", 1), 0.015625, 64)));
                    break;
                case "agate":
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Gate, Math.Clamp(Get("threshold", "", 0.125), 0, 1), Math.Clamp(Get("ratio", "", 2), 1, 9000),
                        Math.Clamp(Get("attack", "", 20), 0.01, 9000), Math.Clamp(Get("release", "", 250), 0.01, 9000), Math.Clamp(Get("range", "", 0.06125), 0, 1),
                        Math.Clamp(Get("knee", "", 2.828427125), 1, 8), Math.Clamp(Get("makeup", "", 1), 1, 64), Math.Clamp(Get("level_in", "", 1), 0.015625, 64)));
                    break;
                case "alimiter":
                {
                    // Its auto level (on unless "level" says 0 or false) makes the limit full scale: the output over the limit.
                    var limit = Math.Clamp(Get("limit", "", 1), 0.0625, 1);
                    var autoLevel = !options.TryGetValue("level", out var level) || level.Trim() is not ("0" or "false" or "off" or "no");
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Limiter, limit, Math.Clamp(Get("attack", "", 5), 0.1, 80),
                        Math.Clamp(Get("release", "", 50), 1, 8000), Math.Clamp(Get("level_in", "", 1), 0.015625, 64),
                        Math.Clamp(Get("level_out", "", 1), 0.015625, 64) * (autoLevel ? 1 / limit : 1)));
                    break;
                }
                case "afftdn":
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Denoise, Math.Clamp(Get("noise_reduction", "nr", 12), 0.01, 97)));
                    break;
                case "extrastereo":
                    effects.Add(new AudioEffectSpec(AudioEffectKind.Widener, Math.Clamp(Get("m", "", 2.5), -10, 10), Get("c", "", 1) != 0 ? 1 : 0));
                    break;
                case "aresample":
                    rate = Get("sample_rate", "osr", rate);
                    break;
                case "asetrate":
                    var set = Get("sample_rate", "r", rate);
                    if (set > 0 && rate > 0)
                        pitch *= set / rate;
                    rate = set;
                    break;
                case "atempo":
                    tempo *= Math.Clamp(Get("tempo", "", 1), 0.5, 100);
                    break;
                case "anull":
                    break;
                default:
                    unsupported.Add(name);
                    break;
            }
        }

        return new OracleChain([.. effects], pitch, tempo, [.. unsupported]);
    }

    private static AudioEffectSpec Biquad(AudioEffectKind kind, double frequency, double width, int widthType, double gainDb, bool bassOrTreble = false) =>
        new(kind, frequency, width, widthType, gainDb, bassOrTreble ? 1 : 0);

    // FFmpeg's width types: h 1, o 2, q 3, s 4, k 5.
    private static int WidthType(Dictionary<string, string> options, int fallback) =>
        (options.TryGetValue("width_type", out var t) || options.TryGetValue("t", out t)) ? t switch { "h" => 1, "o" => 2, "q" => 3, "s" => 4, "k" => 5, _ => fallback } : fallback;

    // The biquads' gain is in decibels whether written with "dB" or not.
    private static double DbOrPlain(Dictionary<string, string> options, string a, string b)
    {
        if (!options.TryGetValue(a, out var text) && !options.TryGetValue(b, out text))
            return 0;
        text = text.Trim();
        if (text.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
            text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>A number as FFmpeg reads an option: plain, or in decibels ("-18dB" is 10^(-18/20)).</summary>
    private static bool Number(string text, out double value)
    {
        text = text.Trim().Trim('\'');
        var decibels = text.EndsWith("dB", StringComparison.OrdinalIgnoreCase);
        if (decibels)
            text = text[..^2];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        if (decibels)
            value = Math.Pow(10, value / 20);
        return true;
    }

    // "a=1:b=2" or a first value without a name (volume=-3dB, atempo=2): the first is stored under the filter's first option's name.
    private static Dictionary<string, string> Options(string text)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = true;
        foreach (var part in SplitTopLevel(text, ':'))
        {
            var equals = part.IndexOf('=');
            if (equals < 0)
            {
                if (first)
                {
                    foreach (var name in (ReadOnlySpan<string>)["volume", "tempo", "sample_rate", "threshold", "frequency"])
                        options.TryAdd(name, part);
                }
            }
            else
            {
                options[part[..equals].Trim()] = part[(equals + 1)..].Trim();
            }

            first = false;
        }

        return options;
    }

    // Splits at a separator outside quotes and brackets.
    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var (depth, quoted, start) = (0, false, 0);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
                quoted = !quoted;
            else if (!quoted && c is '(' or '[')
                depth++;
            else if (!quoted && c is ')' or ']')
                depth--;
            else if (!quoted && depth == 0 && c == separator)
            {
                if (i > start)
                    yield return text[start..i];
                start = i + 1;
            }
        }

        if (start < text.Length)
            yield return text[start..];
    }
}
