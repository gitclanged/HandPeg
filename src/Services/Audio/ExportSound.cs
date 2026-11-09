using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services.Audio;

// ----- An export's sound, the sound engine's alone (Pass 5.2-Audit: FFmpeg only encodes it) -----
// What used to be FFmpeg's sound graph (each clip's sound placed, ducked and processed, several tracks kept apart, loudness
// normalization, surround kept as it is) is the sound engine's render: the program the editor plays (AudioProgram), its strips
// laid on buses, one bus for each output track and each pair of a surround track's channels, all mixed in one pass
// (AudioMixer.RenderTo) so a track still ducks under a voice on another. Output track N carries the sounds of the Sub-Lanes of
// stream place N (5.2-AuditB: every clip's, on whichever track; no file is the "main" one), the first also every sound of no
// stream place and the voiceover. Each output track is then written as raw float (its pairs side by side again, or folded to
// mono), loudness-normalized here when asked (EBU R128 / BS.1770: LoudnessMeter), and handed to FFmpeg to encode. A track copied
// as it is never touches the engine: FFmpeg maps it from its file.

/// <summary>One output sound track of an export: what it carries, how it is encoded, and where its render is.</summary>
/// <param name="Title">The stream's title ("" for none).</param>
/// <param name="Codec">The encoder ("aac", "libopus", "pcm_s16le"...), or "copy" for a stream of a file mapped as it is.</param>
/// <param name="Channels">The channels written (1, 2, or a surround track's own).</param>
/// <param name="Buses">The mixer buses it is made of: one for mono or stereo, one a pair of channels for surround.</param>
/// <param name="CopyStream">The audio stream of <paramref name="CopyPath"/> it copies ("a:N"), for <c>copy</c>; -1 for a render.</param>
/// <param name="CopyPath">The file whose stream it copies (cleaned), for <c>copy</c>; "" for a render.</param>
public sealed record ExportSoundTrack(string Title, string Codec, string Bitrate, int Channels, int[] Buses, int CopyStream = -1, string CopyPath = "")
{
    /// <summary>The raw render (f32le, interleaved, <see cref="Channels"/> wide) once made; null before, and for a copied stream.</summary>
    public string? RenderPath { get; init; }

    public bool IsCopy => CopyStream >= 0;

    /// <summary>FFmpeg's name for the layout of <see cref="Channels"/> channels, as its f32le input is told.</summary>
    public string Layout => Channels switch
    {
        1 => "mono",
        2 => "stereo",
        3 => "3.0",
        4 => "quad",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => string.Create(CultureInfo.InvariantCulture, $"{Channels}c"),
    };
}

/// <summary>An export's sound: its output tracks and the program that renders them (null tracks: no sound at all).</summary>
public sealed record ExportSoundPlan(IReadOnlyList<ExportSoundTrack> Tracks, AudioBusLayout Layout, int SampleRate, bool Normalize)
{
    /// <summary>The most mixer buses one export renders (every output track's pairs).</summary>
    public const int MaxBuses = 32;

    /// <summary>The loudness normalization's aim by default (what FFmpeg's loudnorm was given): -16 LUFS integrated, -1.5 dBTP.</summary>
    public const double TargetLufs = -16, CeilingDb = -1.5;

    /// <summary>The integrated loudness this export's normalization aims at (5.4B: the state's, -16 or -14 LUFS; <see cref="TargetLufs"/> by default).</summary>
    public double LoudnessLufs { get; init; } = TargetLufs;

    public int BusCount => Tracks.Count == 0 ? 0 : Tracks.SelectMany(t => t.Buses).DefaultIfEmpty(-1).Max() + 1;

    /// <summary>
    /// The sound tracks of an export of <paramref name="state"/> (5.2-AuditB: by stream place, never a file's own streams). Without
    /// Merge Audio Tracks, output N carries every sound on a Sub-Lane of stream place N (<see cref="TimelineTrackModel.StreamOrdinal"/>,
    /// on any track): a place is an output while a Sub-Lane of it holds a clip with sound (a silenced lane is a silent track) and its
    /// choice (<see cref="SequenceExportState.AudioOutputs"/>) does not leave it out. The first output also carries every sound of no
    /// stream place and the voiceover (an output of its own when no Sub-Lane holds any). Each is titled, encoded and copied as its
    /// choice says (without one: <see cref="SequenceExportState.MixedAudioCodec"/>, <see cref="SequenceExportState.AudioBitrate"/>,
    /// titled with its lane's name), in the channels the Audio tab asks for, of the widest of its sounds' streams. A passthrough
    /// output is copied as it is only while it is exactly one sound played plainly from a file the whole Sequence is one clip of
    /// (<see cref="SequenceSpans.WholeFiles"/>), nothing mixed into it and nothing done to the whole. Merge Audio Tracks: one mix of
    /// them all. Empty for a picture-only output (GIF, WebP) and a sequence with nothing to hear. Every track is heard, whatever is
    /// soloed. An audio-only output (<see cref="EncoderCommand.IsAudioOnly"/>) is planned as <see cref="For(SequenceExportState, bool, bool)"/> plans it.
    /// </summary>
    public static ExportSoundPlan For(SequenceExportState state, bool pictureOnly) => For(state, pictureOnly, !pictureOnly && EncoderCommand.IsAudioOnly(state));

    /// <summary>
    /// The sound of an export, as <see cref="For(SequenceExportState, bool)"/> plans it; with <paramref name="audioOnly"/> (5.4C: a FLAC,
    /// WAV or MP3 file, which holds one stream) always one rendered track: every stream place and the loose sounds mixed on bus 0
    /// (each place's own choice, dropped or copied, does not apply), never copied as it is, stereo (mono when the Audio tab says so),
    /// encoded with the file's own codec (<see cref="EncoderCommand.AudioOnlyCodec"/>) at <see cref="SequenceExportState.AudioBitrate"/>,
    /// normalized as asked. No tracks when nothing is heard.
    /// </summary>
    public static ExportSoundPlan For(SequenceExportState state, bool pictureOnly, bool audioOnly) =>
        PlanOf(state, pictureOnly, audioOnly) is var plan && plan.LoudnessLufs != state.LoudnessTargetLufs ? plan with { LoudnessLufs = state.LoudnessTargetLufs } : plan;

    private static ExportSoundPlan PlanOf(SequenceExportState state, bool pictureOnly, bool audioOnly)
    {
        // 0, "Match Project Settings" (5.4A): the project's own rate (48 kHz unless it says otherwise).
        var rate = state.AudioSampleRate > 0 ? Math.Clamp(state.AudioSampleRate, 8000, 192000) : state.Timeline.SampleRate;
        if (pictureOnly)
            return new ExportSoundPlan([], AudioBusLayout.AllOnOne, rate, false);
        if (audioOnly)
            return AudioOnlyPlan(state, rate);

        var document = state.Timeline;
        var lowering = TimelineLowering.Lowering(state);
        var sounds = AudioProgramBuilder.Heard(state, lowering, solo: false);
        var voiceover = state.IncludeVoiceover && AudioProgramBuilder.Clean(state.VoiceoverPath).Length > 0;
        var choices = new Dictionary<int, AudioOutputChoice>();
        foreach (var output in state.AudioOutputs)
            choices.TryAdd(output.Ordinal, output);
        bool LeftOut(int ordinal) => choices.TryGetValue(ordinal, out var made) && made.Dropped;

        // The stream places that are outputs, in order, each with its first lane's name (for a place without a choice).
        var places = new SortedDictionary<int, string>();
        foreach (var track in document.Tracks)
        {
            if (track.StreamOrdinal is >= 0 and var ordinal && !LeftOut(ordinal) && track.Clips.Exists(c => c.HasSound)
                && (!places.TryGetValue(ordinal, out var named) || named.Length == 0))
                places[ordinal] = track.Name.Trim();
        }

        // The first output takes the sounds of no stream place and the voiceover: the lowest place's, or one of their own.
        var takesLoose = voiceover || sounds.Exists(s => s.Ordinal < 0);
        var outputs = places.Keys.ToList();
        if (outputs.Count == 0 && takesLoose)
            outputs.Add(-1);
        if (outputs.Count == 0)
            return new ExportSoundPlan([], AudioBusLayout.AllOnOne, rate, false);

        // One mix: every place's sound and the loose sounds on bus 0, stereo (or mono), encoded as a mix is.
        if (state.MergeAudioTracks && outputs.Count > 1)
        {
            var channels = state.AudioChannels == AudioChannelChoice.Mono ? 1 : 2;
            return new ExportSoundPlan([new ExportSoundTrack("", state.MixedAudioCodec, state.AudioBitrate, channels, [0])],
                new AudioBusLayout(places.Keys.ToDictionary(n => n, _ => new[] { 0 }), 0), rate, state.NormalizeAudio);
        }

        // What a sound copied as it is must not take part in: the ducking (a voice ducks, a ducked sound is ducked).
        var anyVoice = voiceover || sounds.Exists(s => s.IsVoice);
        var anyDucked = sounds.Exists(s => s.Ducked);
        var wholeFiles = SequenceSpans.WholeFiles(document);
        var tracks = new List<ExportSoundTrack>();
        var buses = new Dictionary<int, int[]>();
        var (next, looseBus) = (0, -1);
        for (var k = 0; k < outputs.Count; k++)
        {
            var ordinal = outputs[k];
            var choice = ordinal >= 0 ? choices.GetValueOrDefault(ordinal) : choices.TryGetValue(0, out var first) && !first.Dropped ? first : null;
            var title = choice?.Title ?? (ordinal >= 0 ? places[ordinal] : "");
            var own = ordinal >= 0 ? sounds.FindAll(s => s.Ordinal == ordinal) : [];
            var mixedIn = k == 0 && takesLoose;
            var widest = own.Count == 0 ? 2 : own.Max(s => ChannelsOf(s.Layer.AudioChannels));

            // Copied as it is: one sound played as its file has it, of a file the Sequence is the whole of, nothing mixed into it, and the
            // master fader at 0 dB (5.2B: the mixer's own test, AudioProgram.MasterGain 1, so a moved fader is heard in the file too).
            if (choice is { Passthrough: true } && !mixedIn && !state.NormalizeAudio && !state.FadeIn && !state.FadeOut
                && AudioProgramBuilder.MasterFactor(state.MasterGainDb) == 1f && own is [var only] && IsPlain(only, wholeFiles, lowering.SequenceSeconds) && !(only.Ducked ? anyVoice : only.IsVoice && anyDucked))
            {
                tracks.Add(new ExportSoundTrack(title, "copy", choice.Bitrate, widest, [], Math.Max(only.Layer.AudioStream, 0), only.Path));
                continue;
            }

            var channels = state.AudioChannels switch
            {
                AudioChannelChoice.Mono => 1,
                AudioChannelChoice.Source => mixedIn ? Math.Max(widest, 2) : Math.Max(widest, 1),
                _ => mixedIn ? 2 : Math.Clamp(widest, 1, 2),
            };
            var pairs = channels > 2 ? (channels + 1) / 2 : 1;
            var ownBuses = Enumerable.Range(next, pairs).ToArray();
            next += pairs;
            if (ordinal >= 0)
                buses[ordinal] = ownBuses;
            if (mixedIn)
                looseBus = ownBuses[0];
            var codec = choice is { Passthrough: false, Codec.Length: > 0 } ? choice.Codec : state.MixedAudioCodec;
            var bitrate = choice is { Passthrough: false } ? choice.Bitrate : state.AudioBitrate;
            tracks.Add(new ExportSoundTrack(title, codec, bitrate, channels, ownBuses));
        }

        if (next > MaxBuses)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"the export's sound tracks need {next} mixer buses (at most {MaxBuses})"));

        // The loose sounds and the voiceover go into the first output's first bus (its left and right, for a surround one).
        return new ExportSoundPlan(tracks, new AudioBusLayout(buses, looseBus >= 0 ? looseBus : 0), rate, state.NormalizeAudio);
    }

    // An audio-only file's one track (5.4C): every stream place that holds a clip with sound, and the loose sounds and the voiceover,
    // on bus 0; nothing left out by a place's choice, nothing copied; the file's own codec.
    private static ExportSoundPlan AudioOnlyPlan(SequenceExportState state, int rate)
    {
        var lowering = TimelineLowering.Lowering(state);
        var sounds = AudioProgramBuilder.Heard(state, lowering, solo: false);
        var voiceover = state.IncludeVoiceover && AudioProgramBuilder.Clean(state.VoiceoverPath).Length > 0;
        var places = new SortedSet<int>();
        foreach (var track in state.Timeline.Tracks)
        {
            if (track.StreamOrdinal is >= 0 and var ordinal && track.Clips.Exists(c => c.HasSound))
                places.Add(ordinal);
        }

        if (places.Count == 0 && !voiceover && !sounds.Exists(s => s.Ordinal < 0))
            return new ExportSoundPlan([], AudioBusLayout.AllOnOne, rate, false);

        var channels = state.AudioChannels == AudioChannelChoice.Mono ? 1 : 2;
        var codec = EncoderCommand.AudioOnlyCodec(EncoderCommand.Extension(state));
        return new ExportSoundPlan([new ExportSoundTrack("", codec, state.AudioBitrate, channels, [0])],
            new AudioBusLayout(places.ToDictionary(n => n, _ => new[] { 0 }), 0), rate, state.NormalizeAudio);
    }

    // A sound played as its file has it: from the Sequence's start to its end, from the file's start at its own speed, with no slip,
    // fades, gain (its clip's or its track's) or processing; of a file the whole Sequence is one clip of.
    private static bool IsPlain(HeardSound sound, IReadOnlySet<string> wholeFiles, double sequenceSeconds)
    {
        var (layer, clip, track) = (sound.Layer, sound.Clip, sound.Track);
        return clip is not null && track is not null && wholeFiles.Contains(sound.Path)
               && layer.StartTime < 0.001 && layer.MediaOffset < 0.001 && (!layer.HasSpeed || Math.Abs(layer.Speed - 1) < 0.0005)
               && (layer.IsAudio || Math.Abs(layer.AudioOffset) < 0.001)
               && layer.Duration > 0.001 && Math.Abs(layer.StartTime + layer.Duration - sequenceSeconds) < 0.02
               && layer.FadeInSeconds <= 0.01 && layer.FadeOutSeconds <= 0.01
               && Math.Abs(clip.GainDb) < 0.05 && Math.Abs(track.GainDb) < 0.05
               && track.AudioFilters is not { Processes: true } && layer.AudioFilters is not { Processes: true };
    }

    /// <summary>
    /// How many channels a stream has, as its layout names them (Layer.AudioChannels: FFmpeg's "mono", "stereo", "5.1(side)", "quad",
    /// or "6 ch"): the full-range channels and the low-frequency ones of an "N.M" layout; 2 when it cannot be told.
    /// </summary>
    internal static int ChannelsOf(string layout)
    {
        var text = layout.Trim().ToLowerInvariant();
        if (text.IndexOf('(') is > 0 and var bracket)
            text = text[..bracket].Trim();
        switch (text)
        {
            case "mono":
                return 1;
            case "stereo" or "downmix":
                return 2;
            case "quad":
                return 4;
            case "hexagonal":
                return 6;
            case "octagonal":
                return 8;
            case "hexadecagonal":
                return 16;
        }

        var count = text.Split(' ', 'c')[0];
        if (int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
            return Math.Min(n, 64);
        var dot = text.IndexOf('.');
        return dot > 0 && int.TryParse(text.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var full)
                       && int.TryParse(text.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var low) && full + low > 0
            ? Math.Min(full + low, 64)
            : 2;
    }

    /// <summary>
    /// Renders every output track that is not a copy, from <paramref name="fromSeconds"/> on the sequence for
    /// <paramref name="frames"/> frames of the plan's rate (the export's length: exactly as long as its piped picture), into the
    /// session's previews folder: the tracks with their <see cref="ExportSoundTrack.RenderPath"/>. Off the interface's thread; it decodes.
    /// </summary>
    public IReadOnlyList<ExportSoundTrack> Render(SequenceExportState state, double fromSeconds, long frames, Action<double>? progress, CancellationToken token)
    {
        if (Tracks.All(t => t.IsCopy) || frames <= 0)
            return Tracks;

        var program = AudioProgram.Build(state, new AudioBuildOptions { Buses = Layout, IgnoresSolo = true });
        Directory.CreateDirectory(SessionPaths.Previews);
        var stem = Path.Combine(SessionPaths.Previews, $"export_sound_{Guid.NewGuid():N}");
        var busPaths = Enumerable.Range(0, BusCount).Select(b => string.Create(CultureInfo.InvariantCulture, $"{stem}_bus{b}.f32")).ToArray();
        var rendered = new List<ExportSoundTrack>(Tracks.Count);
        try
        {
            var streams = busPaths.Select(p => (Stream)new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)).ToList();
            try
            {
                AudioMixer.RenderTo(program, fromSeconds, frames, SampleRate, streams, progress, token);
            }
            finally
            {
                foreach (var stream in streams)
                    stream.Dispose();
            }

            for (var n = 0; n < Tracks.Count; n++)
            {
                var track = Tracks[n];
                if (track.IsCopy)
                {
                    rendered.Add(track);
                    continue;
                }

                token.ThrowIfCancellationRequested();
                var gain = Normalize ? NormalizingGain(track, busPaths, frames, n) : 1f;

                // A stereo track at its own level is its bus's render as it is; anything else is written anew.
                if (track is { Channels: 2, Buses: [var only] } && gain == 1f)
                {
                    rendered.Add(track with { RenderPath = busPaths[only] });
                    continue;
                }

                var path = string.Create(CultureInfo.InvariantCulture, $"{stem}_track{n}.f32");
                Assemble(track, busPaths, frames, gain, path);
                rendered.Add(track with { RenderPath = path });
            }

            // The buses no track kept as its own render.
            var kept = rendered.Select(t => t.RenderPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var bus in busPaths.Where(b => !kept.Contains(b)))
                Delete(bus);
            return rendered;
        }
        catch
        {
            foreach (var bus in busPaths)
                Delete(bus);
            foreach (var track in rendered)
                Delete(track.RenderPath);
            throw;
        }
    }

    /// <summary>Deletes the renders of tracks made by <see cref="Render"/> (the export is done, or failed).</summary>
    public static void DeleteRenders(IEnumerable<ExportSoundTrack> tracks)
    {
        foreach (var track in tracks)
            Delete(track.RenderPath);
    }

    private static void Delete(string? path)
    {
        try
        {
            if (path is not null)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open: it is in the session's previews folder, which goes when HandPeg closes.
        }
    }

    // ----- Writing a track from its buses -----

    private const int Block = 1 << 14;

    // The gain that brings a track to the target loudness, held so its peak stays under the ceiling (linear normalization: the
    // sound's own dynamics are kept; a track too peaky to reach the target is as loud as its ceiling lets it be). 1 for silence.
    private float NormalizingGain(ExportSoundTrack track, string[] busPaths, long frames, int n)
    {
        var meter = new LoudnessMeter(SampleRate, track.Channels);
        ReadTrack(track, busPaths, frames, (samples, count) => meter.Add(samples, count));

        var (loudness, peak) = (meter.IntegratedLufs, meter.SamplePeak);
        if (!double.IsFinite(loudness) || peak <= 0)
            return 1f;

        // The sample peak with half a decibel kept back for the peaks between samples (a true-peak meter's 4x look, roughly).
        var wanted = LoudnessLufs - loudness;
        var room = CeilingDb - 0.5 - 20 * Math.Log10(peak);
        var gainDb = Math.Min(wanted, room);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Export sound: track {n + 1} is {loudness:0.0} LUFS, peak {20 * Math.Log10(peak):0.0} dBFS; {gainDb:+0.0;-0.0} dB brings it to {loudness + gainDb:0.0} LUFS{(room < wanted ? " (held by its peak)" : "")}."));
        return (float)Math.Pow(10, gainDb / 20);
    }

    // The track's buses side by side again (a surround track's pairs: channels 1 and 2 from the first, 3 and 4 from the next...),
    // or folded to one (a mono track: the two sides' mean), at a gain, into a raw file.
    private static void Assemble(ExportSoundTrack track, string[] busPaths, long frames, float gain, string path)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var bytes = new byte[Block * track.Channels * 4];
        ReadTrack(track, busPaths, frames, (samples, count) =>
        {
            var values = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, count * track.Channels * 4));
            AudioMath.Scale(samples, values, gain);
            output.Write(bytes, 0, count * track.Channels * 4);
        });
    }

    // Reads a track's frames block by block, interleaved in its own channels, from its buses' stereo renders.
    private static void ReadTrack(ExportSoundTrack track, string[] busPaths, long frames, ReadOnlySpanAction<float, int> take)
    {
        var inputs = track.Buses.Select(b => new FileStream(busPaths[b], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)).ToArray();
        try
        {
            var stereo = track.Buses.Select(_ => new byte[Block * 8]).ToArray();
            var interleaved = new float[Block * track.Channels];
            for (long done = 0; done < frames; done += Block)
            {
                var count = (int)Math.Min(Block, frames - done);
                for (var b = 0; b < inputs.Length; b++)
                {
                    var wanted = count * 8;
                    var got = 0;
                    while (got < wanted)
                    {
                        var read = inputs[b].Read(stereo[b], got, wanted - got);
                        if (read == 0)
                        {
                            Array.Clear(stereo[b], got, wanted - got);
                            break;
                        }

                        got += read;
                    }
                }

                // A stereo track's bus is its frames as they are, handed on in place (5.3: the renders are the processor's own floats, little-endian).
                if (track.Channels == 2 && BitConverter.IsLittleEndian)
                {
                    take(MemoryMarshal.Cast<byte, float>(stereo[0].AsSpan(0, count * 8)), count);
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    if (track.Channels == 1)
                    {
                        var (l, r) = (BinaryPrimitives.ReadSingleLittleEndian(stereo[0].AsSpan(i * 8)), BinaryPrimitives.ReadSingleLittleEndian(stereo[0].AsSpan(i * 8 + 4)));
                        interleaved[i] = (l + r) * 0.5f;
                        continue;
                    }

                    for (var c = 0; c < track.Channels; c++)
                        interleaved[i * track.Channels + c] = BinaryPrimitives.ReadSingleLittleEndian(stereo[c / 2].AsSpan(i * 8 + c % 2 * 4));
                }

                take(interleaved.AsSpan(0, count * track.Channels), count);
            }
        }
        finally
        {
            foreach (var input in inputs)
                input.Dispose();
        }
    }
}

/// <summary>
/// How an export's sound is laid on the mixer's buses (5.2-AuditB: by stream place): the buses of each stream place that is
/// rendered (one per channel pair), and the bus of the sounds of no stream place and the voiceover.
/// </summary>
/// <param name="OrdinalBuses">
/// The buses of the sounds on the Sub-Lanes of each stream place (<see cref="TimelineTrackModel.StreamOrdinal"/>):
/// one, read as stereo; several, a surround output's pairs. A place it does not name is not rendered (copied, or left out).
/// </param>
/// <param name="DefaultBus">Where the sounds of no stream place and the voiceover are mixed.</param>
public sealed record AudioBusLayout(IReadOnlyDictionary<int, int[]> OrdinalBuses, int DefaultBus)
{
    private static readonly int[] FirstBus = [0];

    /// <summary>Everything on bus 0, stereo: what the editor plays.</summary>
    public static AudioBusLayout AllOnOne { get; } = new(new Dictionary<int, int[]>(), 0);

    /// <summary>The buses a sound of stream place <paramref name="ordinal"/> (-1: of none) is mixed into; null when it is not rendered.</summary>
    public int[]? BusesOf(int ordinal) =>
        ReferenceEquals(this, AllOnOne) ? FirstBus : ordinal < 0 ? [DefaultBus] : OrdinalBuses.TryGetValue(ordinal, out var buses) ? buses : null;
}
