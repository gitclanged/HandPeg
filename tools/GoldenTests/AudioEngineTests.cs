using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// The v5.0 sound engine (Pass 5.0-AudioAnd3D, 5.0C), through its public way in: a SequenceExportState read by
/// AudioProgram.Build and mixed by AudioMixer.Render, the very code the device plays through, on synthetic WAV files made
/// here (Media Foundation decodes them). Gains and fades, a crossfade, J and L cuts, a slip, the resampler (THD+N), WSOLA's
/// timing, exact seeks, ducking; and, with HandPeg's FFmpeg installed, each processing filter and an AAC file's start
/// against FFmpeg's own output. The golden files hold what is expected; measured numbers go to the console only.
/// </summary>
public static class AudioEngineTests
{
    private const int Rate = 48000;

    public static IEnumerable<GraphResult> All()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.Audio", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        try
        {
            var files = new Files(folder);
            yield return Case("audio-engine-program", "# AudioProgram.Build (5.0C): a timeline read into strips and voices with the FFmpeg path's semantics.", ProgramText);
            yield return Case("audio-engine-gain-fades", "# A clip's gain and fades (linear, as afade), on a constant level of 0.5.", (t, p) => GainAndFades(files, t, p));
            yield return Case("audio-engine-crossfade", "# A crossfade on one track keeps the level (the two fades sum to one), as LayOverAcrossCut lays it.", (t, p) => Crossfade(files, t, p));
            yield return Case("audio-engine-jl-cuts", "# The main file's sound: J and L cuts placed sample for sample (MainSound), mixed where they overlap.", (t, p) => JlCuts(files, t, p));
            yield return Case("audio-engine-slip", "# An unlinked video clip's slip moves its sound; a sound clip's is ignored (the lowering's rule).", (t, p) => Slip(files, t, p));
            yield return Case("audio-engine-resampler", "# The polyphase resampler: a 1 kHz sine from 44.1 and 96 kHz to 48 kHz, THD+N.", (t, p) => Resampler(files, t, p));
            yield return Case("audio-engine-wsola", "# WSOLA: clips at 2x and 0.5x keep their timing (bursts where the speed puts them) and their pitch.", (t, p) => Wsola(files, t, p));
            yield return Case("audio-engine-seek", "# Seeks are exact to the sample (no resampling at the file's own rate), and silent before a clip.", (t, p) => Seek(files, t, p));
            yield return Case("audio-engine-ducking", "# A track ducked under a voice: the dip is the amount asked for (-15 dB), and it comes back after the release.", (t, p) => Ducking(files, t, p));
            yield return Case("audio-engine-ffmpeg-parity", "# Against HandPeg's FFmpeg (when installed): each processing filter, and an AAC file's start (its priming dropped where FFmpeg drops it).", (t, p) => FfmpegParity(files, t, p));
            yield return Case("audio-engine-stream-order", "# The main file's sound tracks by FFmpeg's numbers (0:a:N, what the export maps), three in an MP4 (FFmpeg decodes it: Media Foundation's order of MP4 tracks is its own) and in a Matroska file (OBS's recordings).", (t, p) => StreamOrder(files, t, p));
            yield return Case("audio-engine-export-track-scope", "# A main track's own processing is that track's sound alone (5.0-Verify Q1): the engine's mix against the FFmpeg export's sound of one timeline.", (t, p) => ExportTrackScope(files, t, p));
            yield return Case("audio-engine-container-starts", "# Media time from the first video frame in every container (5.0-Verify: an MPEG-TS layer's sound was 2.1 s early): the engine's sound against the export's, MP4, Matroska, MPEG-TS, M2TS and MPEG-PS made by HandPeg's FFmpeg with a non-zero start.", (t, p) => ContainerStarts(files, t, p));
            yield return Case("preview-sequence-fades", "# Render Preview's stretches and the sequence's fades (5.0-Verify): faded only where a stretch overlaps the sequence's first or last second, on the sequence's clock, picture and sound alike (the sound engine's render fades the same moments); before, every preview's picture faded in from black at its start.", PreviewFades);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Note(FormattableString line) => Console.WriteLine("        " + line.ToString(CultureInfo.InvariantCulture));

    // ----- The program as text -----

    private static void ProgramText(StringBuilder text, List<string> problems)
    {
        const string main = "D:/media/main.mp4", music = "D:/media/music.m4a", voice = "D:/media/voice.wav";
        var document = Document();
        var video = Track(document, TimelineTrackKind.Video);
        var sounds = Track(document, TimelineTrackKind.Audio);
        var voices = Track(document, TimelineTrackKind.Audio);
        sounds.GainDb = -3;
        sounds.DuckAgainstVoice = true;
        sounds.AudioFilters = new TrackAudioFilters { HighPass = true, HighPassHz = 90, Compressor = true, CompressorThreshold = -20, CompressorRatio = 3 };

        video.Clips.Add(MainClip(main, 0, 6, 0));
        var second = MainClip(main, 6, 12, 20);
        (second.AudioLeadInSeconds, second.AudioTailOutSeconds, second.GainDb) = (0.5, 0.25, -6);
        video.Clips.Add(second);
        var bed = SoundClip(music, 1, 11, 4);
        (bed.FadeInSeconds, bed.FadeOutSeconds, bed.Speed) = (1, 2, 1.25);
        sounds.Clips.Add(bed);
        var speech = SoundClip(voice, 3, 8, 0);
        speech.State.IsVoice = true;
        voices.Clips.Add(speech);

        var state = State(document) with
        {
            LocalMediaPath = main,
            HasSource = true,
            MainMediaSeconds = 60,
            AudioTracks =
            [
                new ExportAudioTrack(0, "Game", "aac", "160k", false, false, true, false, false, true, [], ["volume=-2dB"])
                {
                    Offset = 0.1,
                    MutedSpans = [(2.0, 2.5)],
                },
                new ExportAudioTrack(1, "Mic", "aac", "160k", false, true, false, true, false, false, [], []),
            ],
            FadeIn = true,
            MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [main] = 0.033 },
        };

        var program = AudioProgram.Build(state);
        Line(text, $"sequence {program.SequenceSeconds:0.###} s, fade in {program.FadeIn}, fade out {program.FadeOut}, duck mix {program.Duck.Mix:0.####}");
        foreach (var strip in program.Strips)
        {
            Line(text, $"strip {strip.Key.Replace("D:/media/", "", StringComparison.Ordinal)} ({strip.Kind}){(strip.IsVoice ? " voice" : "")}{(strip.Ducked ? " ducked" : "")}");
            foreach (var effect in strip.Effects)
                Line(text, $"  effect {effect}");
            foreach (var name in strip.Unsupported)
                Line(text, $"  not played: {name}");
        }

        foreach (var v in program.Voices)
        {
            Line(text, $"voice {v.Label} -> strip {v.Strip}: {System.IO.Path.GetFileName(v.Source.Path)} a:{v.Source.Stream} (video start {(double.IsNaN(v.Source.VideoStart) ? "?" : v.Source.VideoStart.ToString("0.###", CultureInfo.InvariantCulture))})");
            Line(text, $"  sequence {v.SequenceStart:0.###} to {End(v.SequenceEnd)}, media {v.MediaStart:0.###} to {End(v.MediaEnd)}, rate {v.Rate:0.###}, pitch {v.Pitch:0.###}, gain {AudioMathDb(v.Gain)}");
            Line(text, $"  fades in {v.FadeIn.Seconds:0.###} out {v.FadeOut.Seconds:0.###} (ending {End(v.FadeOutEnd)}), silenced {string.Join(", ", v.Silenced.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Start:0.###}-{s.End:0.###}")))}");
        }

        // The rules the text shows, checked as well.
        if (program.Strips.Count(s => s.Kind == AudioStripKind.MainTrack) != 2)
            problems.Add("the main file's two streams are not two strips");
        if (program.Voices.FirstOrDefault(v => v.Label.EndsWith("clip 1", StringComparison.Ordinal) && v.Source.Stream == 0) is not { } j
            || Math.Abs(j.SequenceStart - 5.5) > 1e-9 || Math.Abs(j.MediaStart - (19.5 - 0.1)) > 1e-9)
            problems.Add("the J cut's sound does not start at 5.5 s with the file at 19.4 s (19.5 less the track's slip)");
        if (program.Voices.FirstOrDefault(v => v.Label.StartsWith("clip music", StringComparison.Ordinal)) is not { } m || Math.Abs(m.Rate - 1.25) > 1e-9 || Math.Abs(m.End - 11) > 1e-9)
            problems.Add("the music clip is not at 1.25x ending at 11 s");
    }

    private static string End(double value) => double.IsPositiveInfinity(value) ? "end" : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string AudioMathDb(float gain) => (20 * Math.Log10(Math.Max(gain, 1e-9))).ToString("0.##", CultureInfo.InvariantCulture) + " dB";

    // ----- Gains, fades, crossfades -----

    private static void GainAndFades(Files files, StringBuilder text, List<string> problems)
    {
        var document = Document();
        var track = Track(document, TimelineTrackKind.Audio);
        var clip = SoundClip(files.Level, 1, 7, 0);
        (clip.GainDb, clip.FadeInSeconds, clip.FadeOutSeconds) = (-6, 1, 2);
        track.Clips.Add(clip);
        var output = Render(State(document), 0, 8);

        var gain = Math.Pow(10, -6 / 20.0);
        foreach (var t in new[] { 0.5, 1.25, 1.5, 1.9, 3.0, 5.5, 6.5, 6.9, 7.5 })
        {
            var expected = 0.5 * gain * (t < 1 || t >= 7 ? 0 : Math.Min(Math.Min((t - 1) / 1, 1), (7 - t) / 2));
            var seen = Mean(output, t, 0.0005);
            Line(text, $"{t:0.000} s: level {expected:0.0000}");
            if (Math.Abs(seen - expected) > 1e-3)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"at {t:0.000} s the level is {seen:0.00000}, not {expected:0.00000}"));
        }
    }

    private static void Crossfade(Files files, StringBuilder text, List<string> problems)
    {
        var document = Document();
        var track = Track(document, TimelineTrackKind.Audio);
        track.Clips.Add(SoundClip(files.Level, 0, 4, 0));
        var later = SoundClip(files.Level, 4, 8, 4);
        later.TransitionIn = 1;
        track.Clips.Add(later);
        var output = Render(State(document), 0, 8);

        var worst = 0.0;
        for (var t = 0.25; t < 7.75; t += 0.05)
            worst = Math.Max(worst, Math.Abs(Mean(output, t, 0.0005) - 0.5));
        Line(text, $"0.25 to 7.75 s every 50 ms: the level stays 0.5 within 0.002, through the crossfade from 3.5 to 4.5 s");
        Note($"crossfade: worst level error {worst:0.000000}");
        if (worst > 0.002)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the level strays by {worst:0.0000} through the crossfade"));
    }

    // ----- Placing: J and L cuts, slips -----

    private static void JlCuts(Files files, StringBuilder text, List<string> problems)
    {
        var path = files.Noise;
        var document = Document();
        var video = Track(document, TimelineTrackKind.Video);
        video.Clips.Add(MainClip(path, 0, 5, 0));
        var second = MainClip(path, 5, 10, 12);
        (second.AudioLeadInSeconds, second.AudioTailOutSeconds) = (0.5, 0.25);
        video.Clips.Add(second);
        video.Clips.Add(MainClip(path, 10, 12, 30));
        var state = State(document) with
        {
            LocalMediaPath = path,
            HasSource = true,
            MainMediaSeconds = Files.NoiseSeconds,
            AudioTracks = [new ExportAudioTrack(0, "Track 1", "aac", "160k", false, false, false, false, false, false, [], [])],
        };
        var output = Render(state, 0, 12);

        // Each moment: which file moments are heard there.
        (double At, double[] Media, string What)[] moments =
        [
            (2.0, [2.0], "the first clip alone"),
            (4.4, [4.4], "the first clip, before the J cut"),
            (4.6, [4.6, 11.6], "the first clip and the J cut's sound (12 s into the file less 0.4 s)"),
            (7.0, [14.0], "the second clip"),
            (10.1, [17.1, 30.1], "the L cut's tail and the third clip"),
            (10.3, [30.3], "the third clip, the tail over"),
        ];
        foreach (var (at, media, what) in moments)
        {
            Line(text, $"{at:0.0} s: {what}");
            var off = Compare(output, at, 2400, media.Select(m => (files.NoiseLeft, files.NoiseRight, m)).ToArray());
            if (off > 1e-6)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"at {at:0.0} s ({what}) the sound is off by {off:0.000000}"));
        }
    }

    private static void Slip(Files files, StringBuilder text, List<string> problems)
    {
        var document = Document();
        var video = Track(document, TimelineTrackKind.Video);
        var slipped = VideoClip(files.Noise, 2, 6, 3);
        (slipped.AudioLinked, slipped.AudioSlipSeconds) = (false, 0.25);
        video.Clips.Add(slipped);
        var output = Render(State(document), 0, 7);
        Line(text, $"video clip 2-6 s from 3 s, sound slipped 0.25 s later: at 3.0 s the file at 3.75 s; nothing before 2.25 s");
        if (Compare(output, 3.0, 2400, [(files.NoiseLeft, files.NoiseRight, 3.75)]) > 1e-6)
            problems.Add("the slipped sound is not at 3.75 s of its file at 3.0 s");
        if (Peak(output, 2.1, 0.1) > 0)
            problems.Add("the slipped sound starts before 2.25 s");

        var document2 = Document();
        var sounds = Track(document2, TimelineTrackKind.Audio);
        var sound = SoundClip(files.Noise, 2, 6, 3);
        (sound.AudioLinked, sound.AudioSlipSeconds) = (false, 0.25);
        sounds.Clips.Add(sound);
        var output2 = Render(State(document2), 0, 7);
        Line(text, $"sound clip 2-6 s from 3 s, slip set: ignored (a sound clip has no picture to slip from): at 3.0 s the file at 4.0 s");
        if (Compare(output2, 3.0, 2400, [(files.NoiseLeft, files.NoiseRight, 4.0)]) > 1e-6)
            problems.Add("the sound clip's slip was not ignored");
    }

    // ----- Rate and time -----

    private static void Resampler(Files files, StringBuilder text, List<string> problems)
    {
        foreach (var (path, rate) in new[] { (files.Sine44, 44100), (files.Sine96, 96000) })
        {
            var document = Document();
            Track(document, TimelineTrackKind.Audio).Clips.Add(SoundClip(path, 0, 2, 0));
            var output = Render(State(document), 0, 1.5);
            var (signal, error) = (0.0, 0.0);
            for (var i = (int)(0.2 * Rate); i < (int)(1.2 * Rate); i++)
            {
                var expected = 0.5 * Math.Sin(2 * Math.PI * 1000 * i / Rate);
                (signal, error) = (signal + expected * expected, error + Math.Pow(output[2 * i] - expected, 2));
            }

            var thdn = 10 * Math.Log10(error / signal);
            Line(text, $"{rate / 1000.0:0.#} kHz to 48 kHz: THD+N below -80 dB");
            Note($"resampler {rate} Hz: THD+N {thdn:0.0} dB");
            if (thdn > -80)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{rate} Hz to 48 kHz: THD+N {thdn:0.0} dB"));
        }
    }

    private static void Wsola(Files files, StringBuilder text, List<string> problems)
    {
        // Bursts at 1, 3, 5, 7 and 9 s of the file. At 2x from 0: 0.5, 1.5, 2.5, 3.5 s. At 0.5x from 1.5 s: 3 and 7 s.
        foreach (var (speed, start, end, offset, expected) in new[] { (2.0, 0.0, 4.0, 0.0, new[] { 0.5, 1.5, 2.5, 3.5 }), (0.5, 0.0, 7.5, 1.5, new[] { 3.0, 7.0 }) })
        {
            var document = Document();
            var clip = SoundClip(files.Bursts, start, end, offset);
            clip.Speed = speed;
            Track(document, TimelineTrackKind.Audio).Clips.Add(clip);
            var output = Render(State(document), 0, end + 0.2);
            var onsets = Onsets(output);
            Line(text, $"{speed:0.#}x: bursts at {string.Join(", ", expected.Select(e => e.ToString("0.0", CultureInfo.InvariantCulture)))} s, each within 15 ms, 1 kHz within 3 %, {0.05 / speed * 1000:0} ms long within 20 ms");
            Note($"wsola {speed}x: onsets {string.Join(", ", onsets.Select(o => o.Start.ToString("0.0000", CultureInfo.InvariantCulture)))}");
            if (onsets.Count != expected.Length)
            {
                problems.Add($"{speed}x: {onsets.Count} bursts heard, not {expected.Length}");
                continue;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                var (at, length, frequency) = onsets[i];
                if (Math.Abs(at - expected[i]) > 0.015)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{speed}x: a burst at {at:0.0000} s, not {expected[i]:0.000} s"));
                if (Math.Abs(frequency - 1000) > 30)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{speed}x: a burst at {frequency:0} Hz, not 1000 Hz (the pitch moved)"));
                if (Math.Abs(length - 0.05 / speed) > 0.02)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{speed}x: a burst {length * 1000:0} ms long, not {0.05 / speed * 1000:0} ms"));
            }
        }
    }

    private static void Seek(Files files, StringBuilder text, List<string> problems)
    {
        var document = Document();
        Track(document, TimelineTrackKind.Audio).Clips.Add(SoundClip(files.Noise, 1, 10, 1));
        var program = AudioProgram.Build(State(document));
        foreach (var from in new[] { 1.0, 2.5, 4.123456, 7.99, 9.5 })
        {
            var output = new float[4800 * 2];
            AudioMixer.Render(program, from, output, Rate);
            var first = (long)Math.Round(from * Rate);
            var off = 0.0;
            for (var i = 0; i < 4800; i++)
                off = Math.Max(off, Math.Max(Math.Abs(output[2 * i] - files.NoiseLeft[first + i]), Math.Abs(output[2 * i + 1] - files.NoiseRight[first + i])));
            Line(text, $"from {from:0.######} s: the file's sample {first} first, the next 4800 exact");
            if (off > 0)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"from {from} s the sound is off by {off}"));
        }

        var before = new float[9600 * 2];
        AudioMixer.Render(program, 0.9, before, Rate);
        Line(text, $"from 0.9 s: silence to 1.0 s, then the file from 1.0 s");
        if (Peak(before, 0, 0.0999) > 0 || Math.Abs(before[2 * 4800] - files.NoiseLeft[Rate]) > 0)
            problems.Add("from 0.9 s the clip at 1.0 s is not silent before it, or not exact at it");
    }

    // ----- Ducking -----

    private static void Ducking(Files files, StringBuilder text, List<string> problems)
    {
        var document = Document();
        var music = Track(document, TimelineTrackKind.Audio);
        music.DuckAgainstVoice = true;
        music.Clips.Add(SoundClip(files.Level, 0, 6, 0));
        var voices = Track(document, TimelineTrackKind.Audio);
        var speech = SoundClip(files.Tone, 2, 3, 0);
        speech.State.IsVoice = true;
        voices.Clips.Add(speech);
        var output = Render(State(document) with { DuckAmountDb = -15 }, 0, 6);

        // The music is the mix's constant part: the tone's whole cycles average to nothing.
        var (open, ducked, back) = (Mean(output, 1.5, 0.05), Mean(output, 2.6, 0.05), Mean(output, 4.5, 0.05));
        var dip = 20 * Math.Log10(ducked / open);
        var recovered = 20 * Math.Log10(back / open);
        Line(text, $"music 0.5 before the voice; ducked by 15 dB (12 to 17) while it speaks (2 to 3 s); back within 0.5 dB at 4.5 s");
        Note($"ducking: before {open:0.0000}, during {ducked:0.0000} ({dip:0.0} dB), after {back:0.0000} ({recovered:0.00} dB)");
        if (Math.Abs(open - 0.5) > 1e-3)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the music is {open:0.0000} before the voice, not 0.5"));
        if (dip is > -12 or < -17)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the dip is {dip:0.0} dB, not about -15 dB"));
        if (Math.Abs(recovered) > 0.5)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the music is {recovered:0.00} dB off at 4.5 s"));
    }

    // ----- Against FFmpeg -----

    private static void FfmpegParity(Files files, StringBuilder text, List<string> problems)
    {
        (string Name, TrackAudioFilters Filters, double LimitDb)[] chains =
        [
            ("highpass 120 Hz", new TrackAudioFilters { HighPass = true, HighPassHz = 120 }, -60),
            ("bass +4 dB, treble -3 dB", new TrackAudioFilters { BassTreble = true, BassGain = 4, TrebleGain = -3 }, -60),
            ("equalizer 125 Hz +5, 2 kHz -4", Eq(), -60),
            ("compressor -20 dB 4:1", new TrackAudioFilters { Compressor = true, CompressorThreshold = -20, CompressorRatio = 4 }, -35),
            ("noise gate -30 dB", new TrackAudioFilters { NoiseGate = true, GateThreshold = -30 }, -30),
            ("stereo widener 1.5", new TrackAudioFilters { StereoWidener = true, StereoWidth = 1.5 }, -80),
            ("limiter -6 dB (its delay only: the music peaks at -8 dB)", new TrackAudioFilters { Limiter = true, LimiterCeiling = -6 }, -80),
            ("limiter -12 dB (limiting)", new TrackAudioFilters { Limiter = true, LimiterCeiling = -12 }, -80),
        ];

        var ffmpeg = FindFfmpeg();
        foreach (var (name, _, limit) in chains)
            Line(text, $"{name}: within {limit:0} dB of FFmpeg's");
        Line(text, $"an AAC file (.m4a): the same samples as the export reads (amovie, cut on its time stamps), at the same moment (within a sample)");
        Line(text, $"an MP4 with a picture: its sound on the picture's clock as the export has it (within a sample)");
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the FFmpeg parity checks were not run");
            return;
        }

        foreach (var (name, filters, limit) in chains)
        {
            var chain = filters.BuildChain(0);
            var document = Document();
            var track = Track(document, TimelineTrackKind.Audio);
            track.AudioFilters = filters;
            track.Clips.Add(SoundClip(files.Music, 0, 3, 0));
            var ours = Render(State(document), 0, 2.5);
            var theirs = FfmpegDecode(ffmpeg, files.Music, $"-af \"{string.Join(",", chain)}\"", 0, 2.5);
            var db = ErrorDb(ours, theirs, (int)(0.1 * Rate), (int)(2.4 * Rate));
            Note($"ffmpeg {name}: {db:0.0} dB");
            if (db > limit)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {db:0.0} dB from FFmpeg's (limit {limit:0} dB)"));
        }

        // AAC: the engine's start of the file against the export's reading of it, by cross-correlation. Not "-ss" before
        // "-i": on a file of sound alone (an .m4a, an .mka) the pinned FFmpeg's input seek starts at the AAC frame it lands
        // in (0.5 s came out 576 samples early, 0 s with the 1024 samples of priming), while amovie and atrim on the time
        // stamps, the export's way (BuildLayerAudio), are exact.
        var m4a = Path.Combine(files.Folder, "music.m4a");
        var mp4 = Path.Combine(files.Folder, "picture.mp4");
        RunFfmpeg(ffmpeg, $"-i \"{files.Music}\" -c:a aac -b:a 256k -y \"{m4a}\"");
        RunFfmpeg(ffmpeg, $"-f lavfi -i \"testsrc=s=320x180:r=30:d=4\" -i \"{files.Music}\" -map 0:v -map 1:a -c:v libx264 -preset veryfast -bf 2 -c:a aac -b:a 256k -shortest -y \"{mp4}\"");
        foreach (var (path, kind, label) in new[] { (m4a, LayerKind.Audio, "m4a"), (mp4, LayerKind.VideoFile, "mp4") })
        {
            var document = Document();
            var clip = kind == LayerKind.Audio ? SoundClip(path, 0, 3, 0.5) : VideoClip(path, 0, 3, 0.5);
            Track(document, kind == LayerKind.Audio ? TimelineTrackKind.Audio : TimelineTrackKind.Video).Clips.Add(clip);
            var ours = Render(State(document), 0, 2.5);
            var theirs = FfmpegExportRead(ffmpeg, path, 0.5, 2.5);
            var (lag, correlation) = Lag(ours, theirs, (int)(0.2 * Rate), Rate, 2000);
            Note($"aac {label}: lag {lag} samples, correlation {correlation:0.0000}");
            if (Math.Abs(lag) > 1 || correlation < 0.99)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"the {label}'s sound is {lag} samples from FFmpeg's (correlation {correlation:0.000})"));
        }
    }

    /// <summary>
    /// The user's Q1 (5.0-Verify): a main track's gain and processing (the Audio tab) are that track's alone, in the export as
    /// in the engine: after its edits and its duck, before a clip's sound is mixed into it. One timeline (the main file's
    /// music track ducked under its voice track, -12 dB, compressed, its bass up; a 1 kHz clip on a sound track) exported by
    /// FFmpeg losslessly and mixed by AudioMixer.Render: the export's two tracks summed must be the engine's mix. Before Q1,
    /// the export ran the music track's chain over the clip's tone too (12 dB down, compressed, ducked).
    /// </summary>
    private static void ExportTrackScope(Files files, StringBuilder text, List<string> problems)
    {
        const double seconds = 4;
        const int width = 320, height = 180;
        Line(text, $"the main file's track 1 (music: auto-ducked, -12 dB, a compressor at -24 dB 4:1, bass +6 dB), track 2 (the voice: bursts at 1 and 3 s), a 1 kHz clip at -2 dB from 0.5 to 3.5 s");
        Line(text, $"the engine's mix against the export's two tracks summed: within -30 dB, correlation over 0.99, no lag");
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the export's sound was not compared");
            return;
        }

        var main = Path.Combine(files.Folder, "scope-main.mkv");
        var output = Path.Combine(files.Folder, "scope-export.mkv");
        RunFfmpeg(ffmpeg, string.Create(CultureInfo.InvariantCulture,
            $"-f lavfi -i \"testsrc=s={width}x{height}:r=30:d={seconds}\" -i \"{files.Music}\" -i \"{files.Bursts}\" -map 0:v -map 1:a -map 2:a -t {seconds} -c:v libx264 -preset veryfast -c:a aac -b:a 256k -y \"{main}\""));

        var document = new TimelineDocument { Width = width, Height = height, FrameRate = FrameRate.Fps30 };
        var mainClip = MainClip(main, 0, seconds, 0);
        (mainClip.SourceDuration, mainClip.State.MediaDuration, mainClip.State.ImageWidth, mainClip.State.ImageHeight) = (seconds, seconds, width, height);
        Track(document, TimelineTrackKind.Video).Clips.Add(mainClip);
        var tone = SoundClip(files.Tone, 0.5, 3.5, 0);
        tone.GainDb = -2;
        Track(document, TimelineTrackKind.Audio).Clips.Add(tone);

        var processing = new TrackAudioFilters { Compressor = true, CompressorThreshold = -24, CompressorRatio = 4, BassTreble = true, BassGain = 6 };
        var state = State(document) with
        {
            SourceWidth = width,
            SourceHeight = height,
            RequestedSize = (width, height),
            OutputSize = (width, height),
            CenterRect = (0, 0, width, height),
            LocalMediaPath = main,
            HasSource = true,
            MainMediaSeconds = seconds,
            FfmpegExecutable = $"\"{ffmpeg}\"",
            VideoCodecArguments = "-c:v ffv1 -pix_fmt yuv420p",
            Container = "mkv",
            OutputPath = output,
            MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [main] = 0 },
            CaptionLayer = Layer.FromState(new LayerState { Kind = LayerKind.Captions, Name = "Subtitles", SizeWidth = 0.9, SizeHeight = 0.2 }, width, height, width, height),
            AudioTracks =
            [
                new ExportAudioTrack(0, "Music", "pcm_s16le", "", false, false, AutoDuck: true, IsVoice: false, false, HasProcessing: true, [], processing.BuildChain(-12)),
                new ExportAudioTrack(1, "Voice", "pcm_s16le", "", false, false, AutoDuck: false, IsVoice: true, false, HasProcessing: false, [], []),
            ],
        };

        var command = FilterGraphBuilder.BuildExportCommand(state);
        Task.Run(() => FfmpegRunner.RunAsync(command.CommandLine, new Progress<FfmpegProgress>(), CancellationToken.None, graphInFile: true)).GetAwaiter().GetResult();
        var music = FfmpegStream(ffmpeg, output, 0, seconds);
        var voice = FfmpegStream(ffmpeg, output, 1, seconds);
        var theirs = new float[music.Length];
        for (var i = 0; i < theirs.Length; i++)
            theirs[i] = music[i] + voice[i];

        var ours = Render(state, 0, seconds);
        var db = ErrorDb(ours, theirs, (int)(0.1 * Rate), (int)((seconds - 0.1) * Rate));
        var (lag, correlation) = Lag(ours, theirs, (int)(0.2 * Rate), (int)(3.5 * Rate), 200);
        Note($"export track scope: {db:0.0} dB from the export, lag {lag}, correlation {correlation:0.0000}");
        if (db > -30)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the engine's mix is {db:0.0} dB from the export's (limit -30 dB)"));
        if (lag != 0 || correlation < 0.99)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the engine's mix is {lag} samples from the export's (correlation {correlation:0.000})"));
    }

    /// <summary>
    /// Media time is seconds from the file's first video frame in every container (5.0-Verify: the A/V check found an
    /// MPEG-TS layer's sound 2.1 s early, as Windows' TS source counts a clock of its own). A clip of each file, 0.25 s into
    /// it, rendered by AudioMixer.Render against the export's reading of it (BuildLayerAudio: amovie, atrim at the video
    /// start + 0.25 s, asetpts, aresample), the video start told (MediaStarts, what FFprobe found) and not told (the decoder
    /// learns it): no lag. Every file starts 2 s on (-output_ts_offset); the sound starts 0.5 s after the picture, and in the
    /// M2TS 0.5 s before it.
    /// </summary>
    private static void ContainerStarts(Files files, StringBuilder text, List<string> problems)
    {
        var ffmpeg = FindFfmpeg();
        var ffprobe = ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var containers = new[]
        {
            ("mp4", "MP4", "", "-itsoffset 0.5", "-c:v libx264 -preset veryfast -bf 2 -c:a aac -b:a 256k", 0),
            ("mkv", "Matroska", "", "-itsoffset 0.5", "-c:v libx264 -preset veryfast -bf 2 -c:a aac -b:a 256k", 0),
            ("ts", "MPEG-TS", "", "-itsoffset 0.5", "-c:v libx264 -preset veryfast -bf 2 -c:a aac -b:a 256k", 0),
            ("m2ts", "M2TS (sound first)", "-itsoffset 0.5", "", "-c:v libx264 -preset veryfast -bf 2 -c:a aac -b:a 256k -f mpegts -mpegts_m2ts_mode 1", 0),
            ("mpg", "MPEG-PS", "", "-itsoffset 0.5", "-c:v mpeg2video -q:v 4 -c:a mp2 -b:a 256k -f vob", 1),
        };
        foreach (var (_, label, _, _, _, within) in containers)
            Line(text, $"{label}: the engine's sound against the export's, video start told and learnt: lag {(within == 0 ? "0" : $"within {within} sample")}, correlation over 0.99");
        if (ffmpeg is null || !File.Exists(ffprobe))
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the containers' starts were not compared");
            return;
        }

        var configured = AppSettings.Current.FfmpegPath;
        AppSettings.Current.FfmpegPath = ffmpeg;
        try
        {
            foreach (var (extension, label, videoDelay, soundDelay, codecs, within) in containers)
                ContainerStart(ffmpeg, ffprobe!, files, extension, label, $"{videoDelay} -f lavfi -i \"testsrc=s=320x180:r=30:d=4\" {soundDelay} -i \"{files.Music}\" -map 0:v -map 1:a {codecs} -output_ts_offset 2", within, problems);
        }
        finally
        {
            AppSettings.Current.FfmpegPath = configured;
        }
    }

    private static void ContainerStart(string ffmpeg, string ffprobe, Files files, string extension, string label, string making, int within, List<string> problems)
    {
        const double offset = 0.25, seconds = 3;
        var path = Path.Combine(files.Folder, "starts." + extension);
        RunFfmpeg(ffmpeg, $"{making} -y \"{path}\"");
        var videoStart = ProbeStart(ffprobe, path, "v:0");
        var soundStart = ProbeStart(ffprobe, path, "a:0");
        var theirs = FfmpegExportRead(ffmpeg, path, videoStart + offset, seconds);
        foreach (var told in new[] { true, false })
        {
            var document = Document();
            Track(document, TimelineTrackKind.Video).Clips.Add(VideoClip(path, 0, seconds, offset));
            var state = State(document);
            if (told)
                state = state with { MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [path] = videoStart } };
            var ours = Render(state, 0, seconds);
            var (lag, correlation) = Lag(ours, theirs, (int)(0.1 * Rate), (int)(2.6 * Rate), 2400);
            Note($"{label} (picture at {videoStart:0.###} s, sound at {soundStart:0.###} s), {(told ? "told" : "learnt")}: lag {lag}, correlation {correlation:0.0000}, sound from {Onset(ours):0.####} s (export {Onset(theirs):0.####} s)");
            if (Math.Abs(lag) > within || correlation < 0.99)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{label}, video start {(told ? "told" : "learnt")}: the engine's sound is {lag} samples from the export's (correlation {correlation:0.000})"));
        }
    }

    /// <summary>
    /// The fades of Render Preview's commands (BuildGpuPreviewCommand's finishing, BuildPreviewCommand's stretches) for a 10 s
    /// sequence with both fades: stretches at the start, in the middle, at the end, across the whole, and two joined.
    /// </summary>
    private static void PreviewFades(StringBuilder text, List<string> problems)
    {
        const string main = "D:/media/main.mp4";
        var document = Document();
        var clip = MainClip(main, 0, 10, 0);
        Track(document, TimelineTrackKind.Video).Clips.Add(clip);
        var state = State(document) with
        {
            LocalMediaPath = main,
            HasSource = true,
            MainMediaSeconds = 10,
            FfmpegExecutable = "ffmpeg",
            Container = "mp4",
            OutputPath = "D:/media/out.mp4",
            MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [main] = 0 },
            AudioTracks = [new ExportAudioTrack(0, "Track 1", "aac", "160k", false, false, false, false, false, false, [], [])],
            FadeIn = true,
            FadeOut = true,
        };

        var fades = new System.Text.RegularExpressions.Regex(@"(a?setpts=PTS[+-][0-9.]+/TB,)?a?fade=t=(in|out)[^,;""\[\]]*(,a?fade=t=out[^,;""\[\]]*)?(,a?setpts=PTS[+-][0-9.]+/TB)?");
        string Fades(string command) => string.Join(" | ", fades.Matches(command).Select(m => m.Value).Distinct());
        foreach (var (name, ranges) in new (string, (double, double)[])[]
                 {
                     ("the start (0 to 2 s)", [(0, 2)]),
                     ("inside the fade in (0.4 to 2.4 s)", [(0.4, 2.4)]),
                     ("the middle (4 to 6 s)", [(4, 6)]),
                     ("the end (8.5 to 10 s)", [(8.5, 10)]),
                     ("the whole (0 to 10 s)", [(0, 10)]),
                     ("two joined (0.5 to 2 s, 9 to 10 s)", [(0.5, 2), (9, 10)]),
                 })
        {
            var overlapsIn = ranges.Any(r => r.Item1 < 1);
            var overlapsOut = ranges.Any(r => r.Item2 > 9);
            text.Append(CultureInfo.InvariantCulture, $"\n{name}:\n");
            if (ranges.Length == 1)
            {
                var gpu = FilterGraphBuilder.BuildGpuPreviewCommand(state, "D:/session/preview.mp4", ranges[0].Item1, ranges[0].Item2, 50, null, "D:/session/preview.f32", out var why);
                var line = gpu is null ? "refused: " + why : Fades(gpu.CommandLine);
                text.Append("  GPU: ").Append(line.Length == 0 ? "no fade" : line).Append('\n');
                if (gpu is null)
                    problems.Add($"{name}: the GPU preview refused it ({why})");
                else if (gpu.CommandLine.Contains("fade=t=in") != overlapsIn || gpu.CommandLine.Contains("fade=t=out") != overlapsOut)
                    problems.Add($"{name}: the GPU preview's fades are not where the stretch overlaps the sequence's");
            }

            var ffmpeg = FilterGraphBuilder.BuildPreviewCommand(state, "D:/session/preview.mp4", ranges.ToList(), 50, null);
            var shown = Fades(ffmpeg);
            text.Append("  FFmpeg: ").Append(shown.Length == 0 ? "no fade" : shown).Append('\n');
            foreach (var (filter, expected) in new[] { ("fade=t=in", overlapsIn), ("fade=t=out", overlapsOut) })
            {
                if ((ffmpeg.Contains("," + filter) || ffmpeg.Contains("]" + filter)) != expected)
                    problems.Add($"{name}: the FFmpeg preview's picture {(expected ? "lacks" : "has")} {filter}");
                if (ffmpeg.Contains("a" + filter) != expected)
                    problems.Add($"{name}: the FFmpeg preview's sound {(expected ? "lacks" : "has")} a{filter}");
            }
        }
    }

    // FFprobe's start_time of one stream of a file (what MediaProbe keeps as its start), in seconds.
    private static double ProbeStart(string ffprobe, string path, string stream)
    {
        using var process = Process.Start(new ProcessStartInfo(ffprobe, $"-v error -select_streams {stream} -show_entries stream=start_time -of default=nw=1:nk=1 \"{path}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return double.Parse(output, CultureInfo.InvariantCulture);
    }

    // Where a rendering's sound begins (its first sample above -26 dB), in seconds.
    private static double Onset(float[] stereo)
    {
        for (var i = 0; i < stereo.Length; i += 2)
        {
            if (Math.Abs(stereo[i]) > 0.05f)
                return i / 2 / (double)Rate;
        }

        return double.NaN;
    }

    /// <summary>
    /// Each of a main file's three sound tracks played alone (AudioBuildOptions.SoloStreams, as the Audio tab's solo) against
    /// FFmpeg's decode of the same "0:a:N": the same sound (correlation over 0.99, within a sample), and none of another
    /// track's. Media Foundation's MP4 source lists an MP4's tracks in an order of its own (two or three AAC tracks FFmpeg
    /// wrote came out reversed), so AudioDecoder.Open gives such a file to FFmpeg; Matroska keeps the file's order, and its
    /// tracks' CodecDelay (the AAC encoder's 1024 samples of priming, which Windows' Matroska source plays) is dropped as
    /// FFmpeg drops it (MatroskaTiming).
    /// </summary>
    private static void StreamOrder(Files files, StringBuilder text, List<string> problems)
    {
        string[] containers = ["mp4", "mkv"];
        foreach (var container in containers)
            Line(text, $"{container}: a:0 music, a:1 a 1 kHz tone, a:2 noise; each alone is FFmpeg's 0:a:N (correlation over 0.99, within a sample) and unlike the other two (under 0.5)");
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the order of sound tracks was not checked");
            return;
        }

        // The test keeps HandPeg's data in a folder of its own, with no FFmpeg in it: the decoders are pointed at this one.
        var configured = AppSettings.Current.FfmpegPath;
        AppSettings.Current.FfmpegPath = ffmpeg;
        try
        {
            foreach (var container in containers)
                StreamOrder(ffmpeg, files, container, problems);
        }
        finally
        {
            AppSettings.Current.FfmpegPath = configured;
        }
    }

    private static void StreamOrder(string ffmpeg, Files files, string container, List<string> problems)
    {
        const double seconds = 3;
        const int width = 320, height = 180;
        {
            var path = Path.Combine(files.Folder, $"order.{container}");
            RunFfmpeg(ffmpeg, string.Create(CultureInfo.InvariantCulture,
                $"-f lavfi -i \"testsrc=s={width}x{height}:r=30:d={seconds}\" -i \"{files.Music}\" -i \"{files.Tone}\" -i \"{files.Noise}\" -map 0:v -map 1:a -map 2:a -map 3:a -t {seconds} -c:v libx264 -preset veryfast -c:a aac -b:a 256k -y \"{path}\""));
            var document = new TimelineDocument { Width = width, Height = height, FrameRate = FrameRate.Fps30 };
            var clip = MainClip(path, 0, seconds, 0);
            (clip.SourceDuration, clip.State.MediaDuration, clip.State.ImageWidth, clip.State.ImageHeight) = (seconds, seconds, width, height);
            Track(document, TimelineTrackKind.Video).Clips.Add(clip);
            var state = State(document) with
            {
                SourceWidth = width,
                SourceHeight = height,
                LocalMediaPath = path,
                HasSource = true,
                MainMediaSeconds = seconds,
                MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [path] = 0 },
                AudioTracks = [.. Enumerable.Range(0, 3).Select(n => new ExportAudioTrack(n, $"Track {n + 1}", "aac", "160k", false, false, false, false, false, false, [], []))],
            };

            var theirs = Enumerable.Range(0, 3).Select(n => FfmpegStream(ffmpeg, path, n, seconds)).ToArray();
            for (var n = 0; n < 3; n++)
            {
                var ours = new float[(int)Math.Round(seconds * Rate) * 2];
                AudioMixer.Render(AudioProgram.Build(state, new AudioBuildOptions { SoloStreams = [n] }), 0, ours, Rate);
                for (var k = 0; k < 3; k++)
                {
                    var (lag, correlation) = Lag(ours, theirs[k], (int)(0.2 * Rate), (int)(2 * Rate), 1500);
                    Note($"stream order {container}: our a:{n} against FFmpeg's a:{k}: lag {lag}, correlation {correlation:0.0000}");
                    if (k == n && (Math.Abs(lag) > 1 || correlation < 0.99))
                        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{container}: a:{n} is {lag} samples from FFmpeg's (correlation {correlation:0.000})"));
                    if (k != n && correlation > 0.5)
                        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{container}: a:{n} sounds like FFmpeg's a:{k} (correlation {correlation:0.000})"));
                }
            }
        }
    }

    /// <summary>One audio stream of a file as FFmpeg decodes it whole, stereo float at 48 kHz.</summary>
    private static float[] FfmpegStream(string ffmpeg, string path, int stream, double seconds)
    {
        var raw = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".f32");
        RunFfmpeg(ffmpeg, string.Create(CultureInfo.InvariantCulture, $"-i \"{path}\" -map 0:a:{stream} -t {seconds:0.######} -ac 2 -ar {Rate} -f f32le -y \"{raw}\""));
        var bytes = File.ReadAllBytes(raw);
        File.Delete(raw);
        var floats = new float[(int)Math.Round(seconds * Rate) * 2];
        Buffer.BlockCopy(bytes, 0, floats, 0, Math.Min(bytes.Length, floats.Length * 4));
        return floats;
    }

    private static TrackAudioFilters Eq()
    {
        var filters = new TrackAudioFilters { Equalizer = true };
        (filters.EqualizerGains[2], filters.EqualizerGains[6]) = (5, -4);
        return filters;
    }

    // ----- Measuring -----

    /// <summary>The left side's mean over a window centred on a moment.</summary>
    private static double Mean(float[] stereo, double at, double half)
    {
        var (a, b) = ((int)Math.Round((at - half) * Rate), (int)Math.Round((at + half) * Rate));
        var sum = 0.0;
        for (var i = a; i < b; i++)
            sum += stereo[2 * i];
        return sum / Math.Max(b - a, 1);
    }

    private static double Peak(float[] stereo, double from, double length)
    {
        var (a, b) = ((int)Math.Round(from * Rate), (int)Math.Round((from + length) * Rate));
        var peak = 0.0;
        for (var i = Math.Max(a, 0); i < b && 2 * i + 1 < stereo.Length; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(stereo[2 * i]), Math.Abs(stereo[2 * i + 1])));
        return peak;
    }

    /// <summary>The largest difference between the output from a moment and the sum of the sources at their moments.</summary>
    private static double Compare(float[] stereo, double at, int frames, (float[] Left, float[] Right, double Media)[] sources)
    {
        var first = (int)Math.Round(at * Rate);
        var worst = 0.0;
        for (var i = 0; i < frames; i++)
        {
            float l = 0, r = 0;
            foreach (var (left, right, media) in sources)
            {
                var k = (int)Math.Round(media * Rate) + i;
                (l, r) = (l + left[k], r + right[k]);
            }

            worst = Math.Max(worst, Math.Max(Math.Abs(stereo[2 * (first + i)] - l), Math.Abs(stereo[2 * (first + i) + 1] - r)));
        }

        return worst;
    }

    /// <summary>The bursts heard: where each starts (5 ms windows over a level of 0.1), how long it lasts, and its frequency (zero crossings).</summary>
    private static List<(double Start, double Length, double Frequency)> Onsets(float[] stereo)
    {
        var found = new List<(double, double, double)>();
        const int window = Rate / 200;
        var frames = stereo.Length / 2;
        var inside = false;
        var start = 0;
        for (var w = 0; w + window <= frames; w += window)
        {
            var energy = 0.0;
            for (var i = w; i < w + window; i++)
                energy += stereo[2 * i] * stereo[2 * i];
            var loud = Math.Sqrt(energy / window) > 0.1;
            if (loud && !inside)
                (inside, start) = (true, w);
            else if (!loud && inside)
            {
                inside = false;

                // The frequency from the first to the last upward zero crossing (each placed between its two samples).
                var (first, last, count) = (0.0, 0.0, 0);
                for (var i = start + 1; i < w; i++)
                {
                    var (a, b) = (stereo[2 * (i - 1)], stereo[2 * i]);
                    if (a < 0 && b >= 0)
                    {
                        var at = i - 1 + a / (a - b);
                        (first, last, count) = (count == 0 ? at : first, at, count + 1);
                    }
                }

                var frequency = count > 1 ? (count - 1) * Rate / (last - first) : 0;
                found.Add((start / (double)Rate, (w - start) / (double)Rate, frequency));
            }
        }

        return found;
    }

    /// <summary>The error of one rendering against another, in dB of the second's power, over a range of frames.</summary>
    private static double ErrorDb(float[] ours, float[] theirs, int from, int to)
    {
        var (signal, error) = (0.0, 0.0);
        for (var i = 2 * from; i < 2 * to && i < ours.Length && i < theirs.Length; i++)
            (signal, error) = (signal + theirs[i] * theirs[i], error + Math.Pow(ours[i] - theirs[i], 2));
        return 10 * Math.Log10((error + 1e-20) / (signal + 1e-20));
    }

    /// <summary>The shift (frames) of ours against theirs that matches best, searched within a reach, and how well.</summary>
    private static (int Lag, double Correlation) Lag(float[] ours, float[] theirs, int from, int length, int reach)
    {
        var (best, bestScore) = (0, double.NegativeInfinity);
        for (var lag = -reach; lag <= reach; lag++)
        {
            double dot = 0, a = 0, b = 0;
            for (var i = from; i < from + length; i += 4)
            {
                var j = i + lag;
                if (j < 0 || 2 * j >= ours.Length || 2 * i >= theirs.Length)
                    continue;
                dot += ours[2 * j] * theirs[2 * i];
                a += ours[2 * j] * ours[2 * j];
                b += theirs[2 * i] * theirs[2 * i];
            }

            var score = dot / Math.Sqrt(a * b + 1e-20);
            if (score > bestScore)
                (bestScore, best) = (score, lag);
        }

        return (best, bestScore);
    }

    // ----- Fixtures -----

    private static float[] Render(SequenceExportState state, double from, double seconds)
    {
        var output = new float[(int)Math.Round(seconds * Rate) * 2];
        AudioMixer.Render(AudioProgram.Build(state), from, output, Rate);
        return output;
    }

    private static TimelineDocument Document() => new() { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };

    private static TimelineTrackModel Track(TimelineDocument document, TimelineTrackKind kind)
    {
        var track = new TimelineTrackModel { Kind = kind, TrackNumber = document.Tracks.Count(t => t.Kind == kind) + 1 };
        document.Tracks.Add(track);
        return track;
    }

    private static SequenceExportState State(TimelineDocument document) => new()
    {
        Timeline = document,
        SourceFrameRate = 30,
        SourceWidth = 1920,
        SourceHeight = 1080,
        RequestedSize = (1920, 1080),
        OutputSize = (1920, 1080),
        CenterRect = (0, 0, 1920, 1080),
        FrameEngine = true,
        CaptionLayer = Layer.FromState(new LayerState { Kind = LayerKind.Captions, Name = "Subtitles", SizeWidth = 0.9, SizeHeight = 0.2 }, 1920, 1080, 1920, 1080),
    };

    private static TimelineClip SoundClip(string path, double start, double end, double offset) => new()
    {
        ContentKind = LayerKind.Audio, SourcePath = path, AudioStreamIndex = 0, Start = start, End = end, MediaOffset = offset, SourceDuration = 120,
        State = new LayerState { Kind = LayerKind.Audio, Name = Path.GetFileNameWithoutExtension(path), ImagePath = path, MediaDuration = 120, HasAudio = true },
    };

    private static TimelineClip VideoClip(string path, double start, double end, double offset) => new()
    {
        ContentKind = LayerKind.VideoFile, SourcePath = path, AudioStreamIndex = 0, Start = start, End = end, MediaOffset = offset, SourceDuration = 120,
        State = new LayerState { Kind = LayerKind.VideoFile, Name = Path.GetFileNameWithoutExtension(path), ImagePath = path, ImageWidth = 320, ImageHeight = 180, SizeWidth = 0.5, MediaDuration = 120, HasAudio = true },
    };

    private static TimelineClip MainClip(string path, double start, double end, double offset) => new()
    {
        ContentKind = LayerKind.MainVideo, SourcePath = path, VideoStreamIndex = 0, AudioStreamIndex = 0, Start = start, End = end, MediaOffset = offset, SourceDuration = 60,
        State = new LayerState { Kind = LayerKind.MainVideo, Name = "Main", ImagePath = path, ImageWidth = 1920, ImageHeight = 1080, SizeWidth = 1, MediaDuration = 60, HasAudio = true },
    };

    // ----- FFmpeg -----

    private static string? FindFfmpeg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[] { Path.Combine(local, "HandPeg-Data", "deps", "ffmpeg", "ffmpeg.exe"), Path.Combine(local, "HandPeg", "deps", "ffmpeg", "ffmpeg.exe") })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void RunFfmpeg(string ffmpeg, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error " + arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed: {errors}");
    }

    /// <summary>FFmpeg's decode of a file from a moment (exact for sound), through a filter, as stereo float at 48 kHz.</summary>
    private static float[] FfmpegDecode(string ffmpeg, string path, string filter, double from, double seconds)
    {
        var raw = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".f32");
        RunFfmpeg(ffmpeg, string.Create(CultureInfo.InvariantCulture, $"-ss {from:0.######} -i \"{path}\" -t {seconds:0.######} {filter} -vn -ac 2 -ar {Rate} -f f32le -y \"{raw}\""));
        var bytes = File.ReadAllBytes(raw);
        File.Delete(raw);
        var floats = new float[(int)Math.Round(seconds * Rate) * 2];
        Buffer.BlockCopy(bytes, 0, floats, 0, Math.Min(bytes.Length, floats.Length * 4));
        return floats;
    }

    /// <summary>A file's sound from a moment as the export reads a clip's (BuildLayerAudio: amovie, atrim, asetpts, aresample), stereo float at 48 kHz.</summary>
    private static float[] FfmpegExportRead(string ffmpeg, string path, double from, double seconds)
    {
        var raw = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".f32");
        var movie = path.Replace("\\", "/").Replace(":", "\\:");
        RunFfmpeg(ffmpeg, string.Create(CultureInfo.InvariantCulture,
            $"-f lavfi -i \"amovie='{movie}',atrim=start={from:0.######},asetpts=PTS-{from:0.######}/TB,aresample=48000:async=1:first_pts=0\" -t {seconds:0.######} -ac 2 -ar {Rate} -f f32le -y \"{raw}\""));
        var bytes = File.ReadAllBytes(raw);
        File.Delete(raw);
        var floats = new float[(int)Math.Round(seconds * Rate) * 2];
        Buffer.BlockCopy(bytes, 0, floats, 0, Math.Min(bytes.Length, floats.Length * 4));
        return floats;
    }

    // ----- The synthetic sources -----

    /// <summary>WAV files made once for the cases, and the noise's own samples (as Media Foundation gives them: n / 32768).</summary>
    private sealed class Files
    {
        public const double NoiseSeconds = 40;

        public Files(string folder)
        {
            Folder = folder;
            var n = (int)(NoiseSeconds * Rate);
            (NoiseLeft, NoiseRight) = (new float[n], new float[n]);
            var seed = 0x2545F491u;
            short Next()
            {
                seed = seed * 1664525 + 1013904223;
                return (short)((int)(seed >> 16) - 32768 >> 1);
            }

            var left = new short[n];
            var right = new short[n];
            for (var i = 0; i < n; i++)
            {
                (left[i], right[i]) = (Next(), Next());
                (NoiseLeft[i], NoiseRight[i]) = (left[i] / 32768f, right[i] / 32768f);
            }

            Noise = Wav("noise.wav", Rate, left, right);
            Level = Wav("level.wav", Rate, Fill(12, Rate, _ => 16384), Fill(12, Rate, _ => 16384));
            Sine44 = Wav("sine44.wav", 44100, Fill(3, 44100, i => Sine(i, 44100, 0.5)), Fill(3, 44100, i => Sine(i, 44100, 0.5)));
            Sine96 = Wav("sine96.wav", 96000, Fill(3, 96000, i => Sine(i, 96000, 0.5)), Fill(3, 96000, i => Sine(i, 96000, 0.5)));
            Tone = Wav("tone.wav", Rate, Fill(4, Rate, i => Sine(i, Rate, 0.3)), Fill(4, Rate, i => Sine(i, Rate, 0.3)));
            var bursts = Fill(10, Rate, Burst);
            Bursts = Wav("bursts.wav", Rate, bursts, bursts);
            var music = Fill(4, Rate, i => (short)Math.Round(32767 * (0.25 * Math.Sin(2 * Math.PI * 220 * i / Rate) + 0.15 * Math.Sin(2 * Math.PI * 3150 * i / Rate) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 1.5 * i / Rate)))));
            Music = Wav("music.wav", Rate, music, Fill(4, Rate, i => (short)(music[i] / 2 + left[i] / 16)));
        }

        public string Folder { get; }
        public string Noise { get; }
        public float[] NoiseLeft { get; }
        public float[] NoiseRight { get; }
        public string Level { get; }
        public string Sine44 { get; }
        public string Sine96 { get; }
        public string Tone { get; }
        public string Bursts { get; }
        public string Music { get; }

        private static short Sine(int i, int rate, double amplitude) => (short)Math.Round(32767 * amplitude * Math.Sin(2 * Math.PI * 1000 * i / rate));

        // 50 ms of 1 kHz (0.5) at 1, 3, 5, 7 and 9 s.
        private static short Burst(int i)
        {
            var t = i / (double)Rate;
            var at = Math.Floor(t);
            return at % 2 == 1 && t - at < 0.05 ? Sine(i, Rate, 0.5) : (short)0;
        }

        private static short[] Fill(double seconds, int rate, Func<int, short> sample)
        {
            var samples = new short[(int)(seconds * rate)];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = sample(i);
            return samples;
        }

        private string Wav(string name, int rate, short[] left, short[] right)
        {
            var path = Path.Combine(Folder, name);
            using var file = new BinaryWriter(File.Create(path));
            var bytes = left.Length * 4;
            file.Write("RIFF"u8);
            file.Write(36 + bytes);
            file.Write("WAVEfmt "u8);
            file.Write(16);
            file.Write((short)1);
            file.Write((short)2);
            file.Write(rate);
            file.Write(rate * 4);
            file.Write((short)4);
            file.Write((short)16);
            file.Write("data"u8);
            file.Write(bytes);
            for (var i = 0; i < left.Length; i++)
            {
                file.Write(left[i]);
                file.Write(right[i]);
            }

            return path;
        }
    }
}
