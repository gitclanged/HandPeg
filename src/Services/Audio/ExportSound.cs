using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services.Audio;

// ----- Export audio rendered by the sound engine (FFmpeg only encodes) -----
// The editor's AudioProgram renders the export audio: strips mapped onto buses (one per output track and per surround channel pair) and
// mixed in one pass (AudioMixer.RenderTo), so ducking still works across tracks. Output track N carries the Sub-Lanes of stream slot N
// (every clip's, on any track); the first also carries slot-less sounds and the voiceover. Each output is written as raw float (pairs
// re-interleaved, or folded to mono), loudness-normalized when requested (EBU R128 / BS.1770: LoudnessMeter), then encoded by FFmpeg.
// Passthrough tracks never touch the engine: FFmpeg maps them from the source file.

/// <summary>One output audio track of an export: content, encoding and render location.</summary>
/// <param name="Title">Stream title ("" for none).</param>
/// <param name="Codec">Encoder ("aac", "libopus", "pcm_s16le", ...), or "copy" for a stream mapped unchanged from a file.</param>
/// <param name="Channels">Channels written (1, 2, or a surround track's own count).</param>
/// <param name="Buses">Mixer buses it is built from: one for mono or stereo, one per channel pair for surround.</param>
/// <param name="CopyStream">Audio stream of <paramref name="CopyPath"/> to copy ("a:N") for <c>copy</c>; -1 for a render.</param>
/// <param name="CopyPath">File whose stream is copied (cleaned) for <c>copy</c>; "" for a render.</param>
public sealed record ExportSoundTrack(string Title, string Codec, string Bitrate, int Channels, int[] Buses, int CopyStream = -1, string CopyPath = "")
{
    /// <summary>Raw render path (f32le, interleaved, <see cref="Channels"/> wide) once made; null before rendering and for copies.</summary>
    public string? RenderPath { get; init; }

    public bool IsCopy => CopyStream >= 0;

    /// <summary>FFmpeg channel layout name for <see cref="Channels"/>, passed to the f32le input.</summary>
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

/// <summary>An export's audio plan: output tracks and their bus layout (no tracks = no audio).</summary>
public sealed record ExportSoundPlan(IReadOnlyList<ExportSoundTrack> Tracks, AudioBusLayout Layout, int SampleRate, bool Normalize)
{
    /// <summary>Maximum mixer buses per export (all output tracks' channel pairs).</summary>
    public const int MaxBuses = 32;

    /// <summary>Default normalization target (the former loudnorm settings): -16 LUFS integrated, -1.5 dBTP.</summary>
    public const double TargetLufs = -16, CeilingDb = -1.5;

    /// <summary>Integrated loudness target for this export (the state's -16 or -14 LUFS; <see cref="TargetLufs"/> by default).</summary>
    public double LoudnessLufs { get; init; } = TargetLufs;

    public int BusCount => Tracks.Count == 0 ? 0 : Tracks.SelectMany(t => t.Buses).DefaultIfEmpty(-1).Max() + 1;

    /// <summary>
    /// Plans an export's audio tracks by stream slot (never by a file's own streams). Without Merge Audio Tracks, output N carries every sound
    /// on a Sub-Lane of stream slot N (<see cref="TimelineTrackModel.StreamOrdinal"/>, any track); a slot is an output while one of its Sub-Lanes
    /// holds a clip with sound (a silenced lane yields a silent track) and its choice (<see cref="SequenceExportState.AudioOutputs"/>) does not
    /// drop it. The first output also carries slot-less sounds and the voiceover (its own output when no Sub-Lane has sound). Title, codec and
    /// copy follow each choice (defaults: <see cref="SequenceExportState.MixedAudioCodec"/>, <see cref="SequenceExportState.AudioBitrate"/>, the
    /// lane name); channels follow the Audio tab from the widest stream. Passthrough copies only one plain sound from a file the whole sequence
    /// is one clip of (<see cref="SequenceSpans.WholeFiles"/>), with nothing mixed in or applied globally. Merge Audio Tracks: one mix. Empty for
    /// picture-only outputs (GIF, WebP) and silent sequences. Solo is ignored. Audio-only outputs (<see cref="EncoderCommand.IsAudioOnly"/>) are
    /// planned by <see cref="For(SequenceExportState, bool, bool)"/>.
    /// </summary>
    public static ExportSoundPlan For(SequenceExportState state, bool pictureOnly) => For(state, pictureOnly, !pictureOnly && EncoderCommand.IsAudioOnly(state));

    /// <summary>
    /// Plans export audio as <see cref="For(SequenceExportState, bool)"/>; with <paramref name="audioOnly"/> (FLAC, WAV or MP3: one stream)
    /// always one rendered track: all stream slots and loose sounds mixed on bus 0 (per-slot drop/copy choices ignored), never copied, stereo
    /// (mono when selected), encoded with the container's codec (<see cref="EncoderCommand.AudioOnlyCodec"/>) at
    /// <see cref="SequenceExportState.AudioBitrate"/>, normalized as requested. No tracks when nothing is audible.
    /// </summary>
    public static ExportSoundPlan For(SequenceExportState state, bool pictureOnly, bool audioOnly) =>
        PlanOf(state, pictureOnly, audioOnly) is var plan && plan.LoudnessLufs != state.LoudnessTargetLufs ? plan with { LoudnessLufs = state.LoudnessTargetLufs } : plan;

    private static ExportSoundPlan PlanOf(SequenceExportState state, bool pictureOnly, bool audioOnly)
    {
        // 0 = "Match Project Settings": the project's sample rate (48 kHz unless set otherwise).
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

        // Output stream slots in order, each with its first lane's name (the title when the slot has no choice).
        var places = new SortedDictionary<int, string>();
        foreach (var track in document.Tracks)
        {
            if (track.StreamOrdinal is >= 0 and var ordinal && !LeftOut(ordinal) && track.Clips.Exists(c => c.HasSound)
                && (!places.TryGetValue(ordinal, out var named) || named.Length == 0))
                places[ordinal] = track.Name.Trim();
        }

        // The first output takes slot-less sounds and the voiceover: the lowest slot's output, or an output of its own.
        var takesLoose = voiceover || sounds.Exists(s => s.Ordinal < 0);
        var outputs = places.Keys.ToList();
        if (outputs.Count == 0 && takesLoose)
            outputs.Add(-1);
        if (outputs.Count == 0)
            return new ExportSoundPlan([], AudioBusLayout.AllOnOne, rate, false);

        // Merged: every slot's audio and the loose sounds on bus 0, stereo (or mono), encoded as a mix.
        if (state.MergeAudioTracks && outputs.Count > 1)
        {
            var channels = state.AudioChannels == AudioChannelChoice.Mono ? 1 : 2;
            return new ExportSoundPlan([new ExportSoundTrack("", state.MixedAudioCodec, state.AudioBitrate, channels, [0])],
                new AudioBusLayout(places.Keys.ToDictionary(n => n, _ => new[] { 0 }), 0), rate, state.NormalizeAudio);
        }

        // A copied sound must not take part in ducking (as a voice that ducks others, or as a ducked sound).
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

            // Copy unchanged only for one plain sound from a whole-sequence file, nothing mixed in, and the master fader at 0 dB (the
            // mixer's own test, AudioProgram.MasterGain 1, so a moved fader is audible in the file).
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

        // Loose sounds and the voiceover go to the first output's first bus (its L/R pair for surround).
        return new ExportSoundPlan(tracks, new AudioBusLayout(buses, looseBus >= 0 ? looseBus : 0), rate, state.NormalizeAudio);
    }

    // Audio-only file: one track mixing every stream slot with sound, the loose sounds and the voiceover on bus 0; no drops, no copies;
    // the container's own codec.
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

    // True for a sound played exactly as its file has it: spanning the whole sequence from file start at 1x, with no slip, fades, gain
    // (clip or track) or processing, from a file the whole sequence is one clip of.
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
    /// Channel count from a layout name (Layer.AudioChannels: FFmpeg's "mono", "stereo", "5.1(side)", "quad" or "6 ch"): full-range plus
    /// LFE channels for "N.M" layouts; 2 when unrecognized.
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
    /// Renders every non-copy output track from <paramref name="fromSeconds"/> for <paramref name="frames"/> frames at the plan's rate (exactly
    /// the piped picture's length) into the session previews folder; returns the tracks with <see cref="ExportSoundTrack.RenderPath"/> set.
    /// Call off the UI thread (decodes).
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

                // A stereo track at unity gain uses its bus render directly; anything else is written anew.
                if (track is { Channels: 2, Buses: [var only] } && gain == 1f)
                {
                    rendered.Add(track with { RenderPath = busPaths[only] });
                    continue;
                }

                var path = string.Create(CultureInfo.InvariantCulture, $"{stem}_track{n}.f32");
                Assemble(track, busPaths, frames, gain, path);
                rendered.Add(track with { RenderPath = path });
            }

            // Delete bus renders that no track kept as its own.
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

    /// <summary>Deletes the renders made by <see cref="Render"/> (export finished or failed).</summary>
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
            // Still open: it lives in the session previews folder, which is removed when HandPeg closes.
        }
    }

    // ----- Track assembly from buses -----

    private const int Block = 1 << 14;

    // Gain that brings a track to the target loudness, capped so its peak stays under the ceiling (linear normalization keeps the
    // dynamics; a track too peaky to reach the target gets as loud as the ceiling allows). 1 for silence.
    private float NormalizingGain(ExportSoundTrack track, string[] busPaths, long frames, int n)
    {
        var meter = new LoudnessMeter(SampleRate, track.Channels);
        ReadTrack(track, busPaths, frames, (samples, count) => meter.Add(samples, count));

        var (loudness, peak) = (meter.IntegratedLufs, meter.SamplePeak);
        if (!double.IsFinite(loudness) || peak <= 0)
            return 1f;

        // Sample peak with 0.5 dB held back for inter-sample peaks (a rough stand-in for a 4x true-peak meter).
        var wanted = LoudnessLufs - loudness;
        var room = CeilingDb - 0.5 - 20 * Math.Log10(peak);
        var gainDb = Math.Min(wanted, room);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Export sound: track {n + 1} is {loudness:0.0} LUFS, peak {20 * Math.Log10(peak):0.0} dBFS; {gainDb:+0.0;-0.0} dB brings it to {loudness + gainDb:0.0} LUFS{(room < wanted ? " (held by its peak)" : "")}."));
        return (float)Math.Pow(10, gainDb / 20);
    }

    // Re-interleaves a track's buses (surround: channels 1-2 from the first pair bus, 3-4 from the next, ...) or folds to mono (L/R mean),
    // applies the gain and writes a raw file.
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

    // Reads a track block by block, interleaved in its own channel count, from its buses' stereo renders.
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

                // A stereo track's bus already holds its interleaved frames: pass it through in place (renders are native little-endian floats).
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
/// Maps export audio onto mixer buses by stream slot: each rendered slot's buses (one per channel pair) and the bus for slot-less sounds
/// and the voiceover.
/// </summary>
/// <param name="OrdinalBuses">
/// Buses for the Sub-Lanes of each stream slot (<see cref="TimelineTrackModel.StreamOrdinal"/>): one = stereo; several = a surround
/// output's pairs. Slots not listed are not rendered (copied or dropped).
/// </param>
/// <param name="DefaultBus">Bus for slot-less sounds and the voiceover.</param>
public sealed record AudioBusLayout(IReadOnlyDictionary<int, int[]> OrdinalBuses, int DefaultBus)
{
    private static readonly int[] FirstBus = [0];

    /// <summary>Everything on bus 0, stereo: editor playback.</summary>
    public static AudioBusLayout AllOnOne { get; } = new(new Dictionary<int, int[]>(), 0);

    /// <summary>Buses for a sound in stream slot <paramref name="ordinal"/> (-1 = none); null when that slot is not rendered.</summary>
    public int[]? BusesOf(int ordinal) =>
        ReferenceEquals(this, AllOnOne) ? FirstBus : ordinal < 0 ? [DefaultBus] : OrdinalBuses.TryGetValue(ordinal, out var buses) ? buses : null;
}
