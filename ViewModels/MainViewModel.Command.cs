using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Building the FFmpeg command line from the current settings.
public partial class MainViewModel
{
    // loudnorm works at 192 kHz internally and would hand that rate to the encoder; bring it back down.
    private const string NormalizeFilters = "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000";

    // Presses the first input down while the second (the side chain) is above the threshold.
    private const string DuckingFilter = "sidechaincompress=threshold=0.05:ratio=8:attack=20:release=400";

    private const double FadeSeconds = 1;

    private string BuildFfmpegCommand()
    {
        var input = string.IsNullOrWhiteSpace(LocalMediaPath) ? "<source>" : LocalMediaPath;
        var output = string.IsNullOrWhiteSpace(DestinationPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), $"HandPeg_output.{Container}")
            : DestinationPath.Trim().Trim('"');
        var segments = GetMergedSegments();
        var duration = GetOutputDuration();

        // What the file can hold depends on the name it is saved under, which the user may have changed by hand.
        var extension = Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0)
            extension = Container;

        // GIF and WebP are picture formats: always encoded, never with sound.
        var animated = extension is "gif" or "webp";

        var copyVideo = !animated && VideoEncoder.Family == EncoderFamily.Copy;
        var tracks = animated ? [] : GetOutputAudioTracks();
        var mixAudio = MergeAudioTracks && tracks.Count > 1;

        // The voiceover from the Voiceover Studio, as one more input. It is mixed into the first audio
        // track, which therefore cannot be a copied one.
        var voiceover = !animated && IncludeVoiceover ? VoiceoverPath : null;
        var copyAnyAudio = !mixAudio && tracks.Where((_, index) => voiceover is null || index > 0).Any(t => t.IsPassthrough);

        // Ducking needs the game and microphone tracks decoded, which a mix always does.
        var duckAudio = DuckAudio && tracks.Count >= 2 && (mixAudio || tracks.Take(2).All(t => !t.IsPassthrough));

        // A copied stream cannot pass through filter_complex, so as soon as any stream is copied
        // the cuts are made by the concat demuxer instead, which works on packets.
        var cutWithDemuxer = segments.Count > 0 && (copyVideo || copyAnyAudio);
        var cutWithFilters = segments.Count > 0 && !cutWithDemuxer;

        // A bare "ffmpeg" means the local copy; an ffmpeg.exe chosen in the settings is spelled out.
        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? Quote(DependencyUpdater.FfmpegPath) : "ffmpeg";
        var args = new List<string> { ffmpeg, "-hide_banner", "-y" };

        if (HardwareDecoding && !copyVideo)
            args.Add("-hwaccel auto");

        if (cutWithDemuxer)
        {
            WriteCutsFile(input, segments);
            args.Add($"-f concat -safe 0 -i {Quote(CutsFilePath)}");
        }
        else
        {
            args.Add($"-i {Quote(input)}");
        }

        // One chapter per kept segment, read from a metadata file given as a second input.
        var chaptersFromCuts = ChaptersAtCuts && segments.Count > 0 && !animated;
        if (chaptersFromCuts)
        {
            WriteChaptersFile(segments);
            args.Add($"-i {Quote(ChaptersFilePath)}");
        }

        int? voiceoverInput = null;
        if (voiceover is not null)
        {
            // Input 0 is the video; the chapters file, when there is one, is input 1.
            voiceoverInput = chaptersFromCuts ? 2 : 1;
            args.Add($"-i {Quote(voiceover)}");
        }

        var burnFilters = copyVideo ? [] : BuildSubtitleBurnFilters(input);
        var framerate = copyVideo ? null : GetTargetFramerate();

        // Applied once to the finished picture: the fades, and for GIF the palette (which has to come last).
        var onceFilters = copyVideo ? [] : BuildWholeVideoFilters(duration);

        // Auto-captions are timed against the finished output, so they are drawn last of all.
        if (AutoCaptions && !copyVideo)
            onceFilters.Add(BuildCaptionOverlay(CaptionsFilePath));
        var lastFilters = extension == "gif" ? new List<string> { "split[gs0][gs1];[gs0]palettegen[gp];[gs1][gp]paletteuse" } : [];

        // Every label in the graph is unique: v/a per segment, ac per track after the join, then the outputs.
        var graph = new List<string>();
        var maps = new List<string>();
        string? simpleVideoFilters = null;

        if (cutWithFilters)
        {
            // Trim every segment out of the video and each audio track and reset its timestamps, then join
            // the pieces with one concat filter so all streams stay locked together.
            var concatInputs = new StringBuilder();
            for (var i = 0; i < segments.Count; i++)
            {
                var range = $"start={Seconds(segments[i].Start)}:end={Seconds(segments[i].End)}";
                var frameFilters = BuildVideoFilters(labelSuffix: i.ToString(CultureInfo.InvariantCulture));

                // Burned-in subtitles are placed by timestamp, so they must be drawn before the timestamps are reset.
                var chain = new List<string> { $"trim={range}" };
                if (burnFilters.Count > 0)
                {
                    chain.AddRange(frameFilters);
                    chain.AddRange(burnFilters);
                    chain.Add("setpts=PTS-STARTPTS");
                }
                else
                {
                    chain.Add("setpts=PTS-STARTPTS");
                    chain.AddRange(frameFilters);
                }

                if (framerate is not null)
                    chain.Add($"fps=fps={framerate}");

                graph.Add($"[0:v:0]{string.Join(",", chain)}[v{i}]");
                concatInputs.Append($"[v{i}]");

                foreach (var track in tracks)
                {
                    graph.Add($"[0:a:{track.Index}]atrim={range},asetpts=PTS-STARTPTS[a{track.Index}_{i}]");
                    concatInputs.Append($"[a{track.Index}_{i}]");
                }
            }

            var finishing = onceFilters.Concat(lastFilters).ToList();
            var joinedVideo = finishing.Count > 0 ? "[vcat]" : "[vout]";
            graph.Add($"{concatInputs}concat=n={segments.Count}:v=1:a={tracks.Count}{joinedVideo}{string.Concat(tracks.Select(t => $"[ac{t.Index}]"))}");
            if (finishing.Count > 0)
                graph.Add($"[vcat]{string.Join(",", finishing)}[vout]");

            maps.Add("-map \"[vout]\"");
        }
        else
        {
            maps.Add("-map 0:v:0");

            if (!copyVideo)
            {
                var chain = BuildVideoFilters(labelSuffix: "");
                chain.AddRange(burnFilters);
                chain.AddRange(onceFilters);
                if (framerate is not null)
                    chain.Add($"fps=fps={framerate}");
                chain.AddRange(lastFilters);
                if (chain.Count > 0)
                    simpleVideoFilters = string.Join(",", chain);
            }
        }

        var audioOptions = BuildAudio(tracks, mixAudio, duckAudio, cutWithFilters, duration, voiceoverInput, graph, maps);

        // Soft subtitles cannot follow filter-based cuts, so they are carried over for uncut and demuxer-cut encodes only.
        var softSubtitles = cutWithFilters || animated ? [] : SubtitleTracks.Where(t => t.Action == SubtitleTrack.SoftSub).ToList();
        foreach (var subtitle in softSubtitles)
            maps.Add($"-map 0:s:{subtitle.Index}?");

        if (graph.Count > 0)
            args.Add($"-filter_complex \"{string.Join(";", graph)}\"");
        args.AddRange(maps);
        if (simpleVideoFilters is not null)
            args.Add($"-vf \"{simpleVideoFilters}\"");

        args.Add(extension switch
        {
            "gif" => "-c:v gif -loop 0",
            "webp" => "-c:v libwebp -q:v 75 -loop 0",
            _ => BuildVideoCodecArguments(),
        });
        args.AddRange(audioOptions);
        if (animated)
            args.Add("-an");

        if (softSubtitles.Count > 0)
        {
            var subtitleCodec = extension switch { "mp4" or "mov" or "m4v" => "mov_text", "webm" => "webvtt", _ => "copy" };
            args.Add($"-c:s {subtitleCodec}");
        }

        // Source chapter times no longer line up once sections have been removed.
        args.Add(chaptersFromCuts ? "-map_chapters 1"
            : ChapterMarkers && segments.Count == 0 && !animated ? "-map_chapters 0"
            : "-map_chapters -1");

        if (WebOptimized && extension is "mp4" or "mov" or "m4v")
            args.Add("-movflags +faststart");

        args.Add(Quote(output));
        return string.Join(" ", args);
    }

    // ----- Render preview -----

    /// <summary>
    /// A short, small encode that shows what the current filters do to the picture: the same per-frame
    /// chain, layers and burned-in subtitles as the real command, then scaled down. Speed matters more
    /// than quality here, so it always uses the fastest software settings.
    /// </summary>
    /// <param name="captionsPath">An auto-caption file made for this stretch of the video, or null.</param>
    private string BuildPreviewCommand(string outputPath, double startSeconds, int durationSeconds, int percent, string? captionsPath)
    {
        var chain = BuildVideoFilters(labelSuffix: "");
        var burnFilters = BuildSubtitleBurnFilters(LocalMediaPath);
        chain.AddRange(burnFilters);

        // Fades are left out: they are placed by the length of the whole output, which a few seconds do not have.
        chain.AddRange(BuildWholeVideoFilters(duration: 0));
        if (captionsPath is not null)
            chain.Add(BuildCaptionOverlay(captionsPath));
        if (GetTargetFramerate() is { } framerate)
            chain.Add($"fps=fps={framerate}");

        var factor = Number(Math.Clamp(percent, 5, 100) / 100.0);
        chain.Add($"scale=trunc(iw*{factor}/2)*2:trunc(ih*{factor}/2)*2");
        chain.Add("format=yuv420p");

        // Seeking before the input is fast. Burned-in subtitles need the original timestamps, though,
        // so with those the seek comes after the input: slower, but the text lands on the right frames.
        var seek = $"-ss {Number(startSeconds)} -t {durationSeconds}";
        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? Quote(DependencyUpdater.FfmpegPath) : "ffmpeg";
        var input = burnFilters.Count > 0 ? $"-i {Quote(LocalMediaPath)} {seek}" : $"{seek} -i {Quote(LocalMediaPath)}";
        if (HardwareDecoding)
            input = "-hwaccel auto " + input;

        return $"{ffmpeg} -hide_banner -y {input} -map 0:v:0 -map 0:a:0? -vf \"{string.Join(",", chain)}\" "
               + $"-c:v libx264 -preset ultrafast -crf 24 -c:a aac -b:a 128k -movflags +faststart {Quote(outputPath)}";
    }

    // ----- Audio -----

    /// <summary>
    /// The audio tracks that reach the output, in order. Before a source has been inspected there is
    /// nothing to list, so a single stand-in track carries the tab's default codec and bitrate.
    /// </summary>
    private List<AudioTrack> GetOutputAudioTracks()
    {
        if (_mediaInfo is not null)
            return AudioTracks.Where(t => !t.IsDropped).ToList();

        var standIn = new AudioTrack(new AudioStreamInfo(0, "", 2, "", 48000, 0, "", ""));
        ApplyAudioDefaults(standIn);
        return [standIn];
    }

    /// <summary>A mix is always encoded: when the tab's default is Copy, AAC stands in.</summary>
    private string MixedAudioCodec => AudioEncoder == CopyOption ? "aac" : AudioEncoder;

    /// <summary>
    /// Adds the audio side of the command: graph pieces for mixing, ducking, filter-based cuts and the
    /// voiceover, the maps, and (returned) the per-stream encoder options. Tracks the graph does not need
    /// are mapped directly.
    /// </summary>
    /// <param name="voiceoverInput">Which input (-i) the voiceover is, or null when there is none to mix in.</param>
    private List<string> BuildAudio(
        List<AudioTrack> tracks, bool mix, bool duck, bool cutWithFilters, double duration, int? voiceoverInput,
        List<string> graph, List<string> maps)
    {
        var options = new List<string>();

        // After a filter-based cut each track continues from the concat filter; otherwise from the input.
        string Source(AudioTrack track) => cutWithFilters ? $"[ac{track.Index}]" : $"[0:a:{track.Index}]";

        // A track with a gain or processing of its own (the Audio tab) gets that first, as a step of the
        // graph, and whatever is done with the track afterwards continues from there.
        var prepared = new Dictionary<int, string>();
        string Prepared(AudioTrack track)
        {
            if (!track.HasProcessing || track.GetFilterChain() is not { Count: > 0 } chain)
                return Source(track);

            if (!prepared.TryGetValue(track.Index, out var label))
            {
                label = $"[at{track.Index}]";
                graph.Add($"{Source(track)}{string.Join(",", chain)}{label}");
                prepared[track.Index] = label;
            }

            return label;
        }

        // Applied to whatever is encoded: loudness first, so the fades are not undone by it.
        var finishing = new List<string>();
        if (NormalizeAudio)
            finishing.Add(NormalizeFilters);
        if (FadeIn)
            finishing.Add($"afade=t=in:st=0:d={Number(FadeSeconds)}");
        if (FadeOut && duration > FadeSeconds)
            finishing.Add($"afade=t=out:st={Number(duration - FadeSeconds)}:d={Number(FadeSeconds)}");
        var finishingChain = string.Join(",", finishing);
        var thenFinishing = finishing.Count > 0 ? "," + finishingChain : "";

        // The voiceover: its own input, cut to its trim handles, moved to where it starts and set to its
        // gain. It was made against the finished timeline, so it is mixed in after the video's own sound
        // has been through the cuts: its times are times in the output, whatever was cut out before them.
        string? voice = null;
        if (voiceoverInput is { } input)
        {
            var steps = new List<string>();
            if (VoiceoverTrimStart > 0.05 || (VoiceoverDuration > 0 && VoiceoverTrimEnd < VoiceoverDuration - 0.05 && VoiceoverTrimEnd > VoiceoverTrimStart))
                steps.Add($"atrim=start={Number(Math.Max(VoiceoverTrimStart, 0))}:end={Number(VoiceoverTrimEnd)},asetpts=PTS-STARTPTS");
            if (VoiceoverStartSeconds >= 0.01)
                steps.Add($"adelay={(int)Math.Round(VoiceoverStartSeconds * 1000)}:all=1");
            if (Math.Abs(VoiceoverGainDb) >= 0.05)
                steps.Add($"volume={Number(VoiceoverGainDb)}dB");
            graph.Add($"[{input}:a:0]{(steps.Count > 0 ? string.Join(",", steps) : "anull")}[voiceover]");
            voice = "[voiceover]";
        }

        // Lays the voiceover over a track. The track decides the length; neither is turned down to make room.
        string WithVoice(string label)
        {
            if (voice is null)
                return label;

            graph.Add($"{label}{voice}amix=inputs=2:duration=first:normalize=0[a_voiced]");
            return "[a_voiced]";
        }

        // A video without sound of its own: the voiceover is its audio, ending where the picture does.
        if (tracks.Count == 0)
        {
            if (voice is not null)
            {
                maps.Add($"-map \"{voice}\"");
                options.Add(BuildAudioEncodeOptions(0, MixedAudioCodec, AudioBitrate));
                options.Add("-shortest");
            }

            return options;
        }

        if (mix)
        {
            var inputs = tracks.Select(Prepared).ToList();
            if (duck)
            {
                // The microphone both steers the compressor and is heard, so it is split in two.
                graph.Add($"{inputs[1]}asplit=2[mic_key][mic_out]");
                graph.Add($"{inputs[0]}[mic_key]{DuckingFilter}[ducked]");
                (inputs[0], inputs[1]) = ("[ducked]", "[mic_out]");
            }

            graph.Add($"{string.Concat(inputs)}amix=inputs={inputs.Count}:duration=longest{thenFinishing}[a_mixed]");
            maps.Add($"-map \"{WithVoice("[a_mixed]")}\"");
            options.Add(BuildAudioEncodeOptions(0, MixedAudioCodec, AudioBitrate));
            return options;
        }

        for (var k = 0; k < tracks.Count; k++)
        {
            var track = tracks[k];
            string? label = null;

            // The name from the track list, written as the stream's title. Quotes would end the argument early.
            if (!string.IsNullOrWhiteSpace(track.Title))
                options.Add($"-metadata:s:a:{k} title=\"{track.Title.Trim().Replace('"', '\'')}\"");

            if (duck && k == 0)
            {
                graph.Add($"{Prepared(tracks[1])}asplit=2[mic_key][mic_out]");
                graph.Add($"{Prepared(track)}[mic_key]{DuckingFilter}{thenFinishing}[ducked]");
                label = "[ducked]";
            }
            else if (duck && k == 1)
            {
                label = "[mic_out]";
                if (finishing.Count > 0)
                {
                    graph.Add($"[mic_out]{finishingChain}[mic_done]");
                    label = "[mic_done]";
                }
            }
            else if (cutWithFilters)
            {
                label = Prepared(track);
                if (finishing.Count > 0)
                {
                    graph.Add($"{label}{finishingChain}[an{track.Index}]");
                    label = $"[an{track.Index}]";
                }
            }

            // The voiceover goes into the first track. That track is then decoded and encoded whatever it
            // was set to: sound cannot be mixed into a stream that is only being copied.
            if (k == 0 && voice is not null)
            {
                if (label is null)
                {
                    var own = track.GetFilterChain().Concat(finishing).ToList();
                    label = Source(track);
                    if (own.Count > 0)
                    {
                        graph.Add($"{label}{string.Join(",", own)}[a_first]");
                        label = "[a_first]";
                    }
                }

                maps.Add($"-map \"{WithVoice(label)}\"");
                options.Add(track.IsPassthrough
                    ? BuildAudioEncodeOptions(k, MixedAudioCodec, AudioBitrate)
                    : BuildAudioEncodeOptions(k, track.Codec, track.Bitrate));
                continue;
            }

            if (label is not null)
            {
                maps.Add($"-map \"{label}\"");
                options.Add(BuildAudioEncodeOptions(k, track.Codec, track.Bitrate));
                continue;
            }

            // Straight from the input. "?" keeps the command valid for a source without such a stream.
            maps.Add($"-map 0:a:{track.Index}?");
            if (track.IsPassthrough)
            {
                options.Add($"-c:a:{k} copy");
                continue;
            }

            // Straight from the input, so the track's own processing and the finishing go on as a simple filter.
            options.Add(BuildAudioEncodeOptions(k, track.Codec, track.Bitrate));
            var simple = track.GetFilterChain().Concat(finishing).ToList();
            if (simple.Count > 0)
                options.Add($"-filter:a:{k} \"{string.Join(",", simple)}\"");
        }

        return options;
    }
    /// <param name="outputIndex">Position of the stream among the output's audio streams.</param>
    private static string BuildAudioEncodeOptions(int outputIndex, string codec, string bitrate) =>
        codec.StartsWith("pcm_", StringComparison.Ordinal)
            ? $"-c:a:{outputIndex} {codec}"
            : $"-c:a:{outputIndex} {codec} -b:a:{outputIndex} {bitrate}";

    // ----- Video filters -----

    /// <summary>
    /// The per-frame chain applied to re-encoded video. Order matters: deinterlace whole frames, crop,
    /// clean up what is left, resize, flag the pixel shape, then convert the colours.
    /// </summary>
    /// <param name="labelSuffix">
    /// Makes the labels inside a step unique. With filter-based cuts the chain is written once per segment
    /// into one graph, where two pieces may not share a label.
    /// </param>
    private List<string> BuildVideoFilters(string labelSuffix)
    {
        var filters = new List<string>();
        if (Deinterlace)
            filters.Add("yadif");

        // The crop is written as fractions of the input frame, so the same settings cut the same part out
        // of a source of any resolution. Width and height are kept even, as most encoders need.
        string? crop = null;
        if (HasCrop)
        {
            var (left, top, right, bottom) = GetCropFractions();
            crop = $"crop=trunc(iw*{Fraction(1 - left - right)}/2)*2:trunc(ih*{Fraction(1 - top - bottom)}/2)*2:trunc(iw*{Fraction(left)}):trunc(ih*{Fraction(top)})";
        }

        if (FrameEngine)
        {
            filters.Add(BuildFrameEngine(crop, labelSuffix));
            if (Denoise)
                filters.Add("hqdn3d");
            filters.Add("setsar=1");
        }
        else
        {
            if (crop is not null)
                filters.Add(crop);
            if (Denoise)
                filters.Add("hqdn3d");

            var hasWidth = int.TryParse(OutputWidth, out var width) && width > 0;
            var hasHeight = int.TryParse(OutputHeight, out var height) && height > 0;

            // A vertical size is not the source's shape, so both sides are spelled out; otherwise a side
            // left blank follows the other in the source's shape (-2).
            if (UseVerticalResolution && GetRequestedSize() is { } vertical)
                filters.Add($"scale={vertical.Width}:{vertical.Height}");
            else if (hasWidth || hasHeight)
                filters.Add($"scale={(hasWidth ? width : -2)}:{(hasHeight ? height : -2)}");

            if (GetSampleAspectRatio() is { } sampleAspectRatio)
                filters.Add($"setsar={sampleAspectRatio}");
        }

        if (Colorspace == TonemapToSdr)
            filters.Add(TonemapFilters);

        if (!string.IsNullOrWhiteSpace(LutPath))
            filters.Add($"lut3d=file='{EscapeFilterPath(LutPath)}'");

        // Colour correction, then sharpening last of all, so that it sharpens the picture as it will be seen.
        filters.AddRange(BuildColorFilters());

        return filters;
    }

    /// <summary>
    /// Composes the output frame from layers. The source is split into independent branches, one per layer
    /// that shows it: the background is scaled to fill the frame, blurred and dimmed entirely on its own
    /// branch; only then are the sharp video and the elements laid on top of it, one after another.
    /// Nothing done to an upper layer can reach the background, because by then it is a finished picture.
    /// The frame can be any size and shape: everything is placed from fractions of it.
    /// </summary>
    private string BuildFrameEngine(string? centerCrop, string s)
    {
        var (width, height) = (FrameWidth, FrameHeight);
        var elements = UiElements.Where(e => e.IsUsable).ToList();
        var center = GetCenterRect();

        // A sharp video that fills the whole frame hides the background completely, so none is made.
        var needsBackground = center.X > 0 || center.Y > 0 || center.X + center.Width < width || center.Y + center.Height < height;

        // One copy of the frame for each layer that shows the video: background, centre, and video elements.
        // Image elements bring their own picture and need no copy.
        var branches = new List<string>();
        if (needsBackground)
            branches.Add($"[bg_in{s}]");
        branches.Add($"[center_in{s}]");
        for (var n = 1; n <= elements.Count; n++)
        {
            if (elements[n - 1].IsVideo)
                branches.Add($"[ui{n}_in{s}]");
        }

        // A lone layer needs no copies: the chain simply runs on through it.
        var isSplit = branches.Count > 1;
        var graph = new StringBuilder(isSplit ? $"split={branches.Count}{string.Concat(branches)};" : "");

        // Centre. The Dimensions crop frames this layer only; the background uses the whole picture.
        var centerChain = $"{(centerCrop is null ? "" : centerCrop + ",")}scale={center.Width}:{center.Height}";

        if (needsBackground)
        {
            // The blur radius cannot exceed half the smaller side of the colour planes, which are half size.
            var radius = Math.Min(Math.Clamp(BlurRadius, 5, 50), Math.Max(Math.Min(width, height) / 4 - 1, 1));
            graph.Append($"[bg_in{s}]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},"
                         + $"boxblur={radius}:{Math.Clamp(BlurPasses, 1, 5)},eq=brightness={Number(Math.Clamp(BackgroundDim, -0.5, 0))}[bg_layer{s}]");
            graph.Append($";[center_in{s}]{centerChain}[center_layer{s}]");
            graph.Append($";[bg_layer{s}][center_layer{s}]overlay={center.X}:{center.Y}");
        }
        else
        {
            // What of the sharp video lies inside the frame is the base picture.
            graph.Append($"{(isSplit ? $"[center_in{s}]" : "")}{centerChain},crop={width}:{height}:{-center.X}:{-center.Y}");
        }

        // Elements, each on the picture built so far. The running picture is labelled between steps;
        // after the last one it is left open, so the rest of the chain continues from it.
        for (var n = 1; n <= elements.Count; n++)
        {
            var element = elements[n - 1];
            var (x, y, elementWidth, elementHeight) = element.GetOutputRect(width, height, SourceWidth, SourceHeight);
            graph.Append($"[stage{n}{s}];");
            graph.Append(BuildElementLayer(element, $"ui{n}", s, elementWidth, elementHeight));

            if (element.Shadow)
            {
                // The shadow is the element's own outline in black at reduced opacity, laid down first and offset.
                var offset = Math.Clamp(element.ShadowOffset, 0, 200);
                graph.Append($";[ui{n}_layer{s}]split[ui{n}_top{s}][ui{n}_shadow_in{s}]");
                graph.Append($";[ui{n}_shadow_in{s}]format=yuva420p,lutyuv=y=16:u=128:v=128:a=val*{Number(Math.Clamp(element.ShadowOpacity, 0, 1))},"
                             + $"boxblur=2:1:0:0:2:1[ui{n}_shadow{s}]");
                graph.Append($";[stage{n}{s}][ui{n}_shadow{s}]overlay={x + offset}:{y + offset}[ui{n}_shaded{s}]");
                graph.Append($";[ui{n}_shaded{s}][ui{n}_top{s}]overlay={x}:{y}");
            }
            else
            {
                graph.Append($";[stage{n}{s}][ui{n}_layer{s}]overlay={x}:{y}");
            }
        }

        return graph.ToString();
    }

    /// <summary>
    /// One element as a finished layer, labelled [name_layer]: cut from the video or read from its image
    /// file, scaled, then given its transparency. Rounded corners and soft edges are an alpha mask worked
    /// out per pixel by geq; opacity scales whatever alpha results.
    /// </summary>
    private static string BuildElementLayer(OverlayRegion element, string name, string s, int width, int height)
    {
        var mask = BuildElementMask(element, width, height);
        var opacity = Math.Clamp(element.Opacity, 0, 100) / 100.0;
        var chain = new StringBuilder();

        if (element.IsImage)
        {
            // The picture is read by a source filter inside the graph, so no second -i is needed. It is a
            // single frame, which the overlay repeats for as long as the video runs; its mask is therefore
            // computed once. A picture may come with transparency of its own, which the mask multiplies
            // rather than replaces.
            chain.Append($"movie='{EscapeFilterPath(element.ImagePath)}',scale={width}:{height}");
            if (mask is not null)
                chain.Append($",format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{mask}{(opacity < 1 ? "*" + Number(opacity) : "")}'");
            else
                chain.Append(opacity < 1 ? $",format=rgba,colorchannelmixer=aa={Number(opacity)}" : ",format=rgba");

            return chain.Append($"[{name}_layer{s}]").ToString();
        }

        // Cut out of the source by fractions of the frame, so the same element works at any source size.
        chain.Append($"[{name}_in{s}]crop=trunc(iw*{Fraction(element.SourceWidth)}/2)*2:trunc(ih*{Fraction(element.SourceHeight)}/2)*2:"
                     + $"iw*{Fraction(element.SourceX)}:ih*{Fraction(element.SourceY)},scale={width}:{height}");

        if (mask is not null)
        {
            // A grey picture the size of the element, white where it is to show, merged in as its alpha channel.
            // It is drawn from a copy of the element itself, so the two always arrive frame for frame.
            chain.Append($",format=yuva420p,split[{name}_pic{s}][{name}_mask_in{s}]");
            chain.Append($";[{name}_mask_in{s}]format=gray,geq=lum='255*{mask}'[{name}_mask{s}]");
            chain.Append($";[{name}_pic{s}][{name}_mask{s}]alphamerge");
        }
        else if (opacity < 1)
        {
            chain.Append(",format=yuva420p");
        }

        if (opacity < 1)
            chain.Append($",lutyuv=a=val*{Number(opacity)}");

        return chain.Append($"[{name}_layer{s}]").ToString();

        static string Fraction(double value) => Math.Clamp(value, 0, 1).ToString("0.#####", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The geq expression (0 to 1 per pixel) for an element's shape, or null when it is a plain rectangle.
    /// It measures how far a pixel is inside a rounded rectangle: zero on the edge, rising inwards. Rounded
    /// corners cut the picture off at that edge; a feather lets it fade in over its width instead.
    /// </summary>
    private static string? BuildElementMask(OverlayRegion element, int width, int height)
    {
        var limit = Math.Max(Math.Min(width, height) / 2 - 1, 1);
        var corner = Math.Min((int)Math.Round(Math.Clamp(element.CornerRadius, 0, 50) / 100.0 * Math.Min(width, height)), limit);
        var feather = element.Feather ? Math.Clamp(element.FeatherRadius, 1, limit) : 0;
        if (corner == 0 && feather == 0)
            return null;

        // A feathered edge is at least as round as it is soft; without a feather the edge is one pixel of anti-aliasing.
        var radius = Math.Max(corner, feather);
        var ramp = Math.Max(feather, 1);
        return $"clip(({radius}.5-hypot(max({radius}-X,0)+max(X-(W-1-{radius}),0),max({radius}-Y,0)+max(Y-(H-1-{radius}),0)))/{ramp},0,1)";
    }

    private static string Fraction(double value) => Math.Clamp(value, 0, 1).ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>What is applied to the finished picture, once: the fades over everything.</summary>
    private List<string> BuildWholeVideoFilters(double duration)
    {
        var filters = new List<string>();

        if (FadeIn)
            filters.Add($"fade=t=in:st=0:d={Number(FadeSeconds)}");
        if (FadeOut && duration > FadeSeconds)
            filters.Add($"fade=t=out:st={Number(duration - FadeSeconds)}:d={Number(FadeSeconds)}");

        return filters;
    }
    /// <summary>One subtitles filter per track that is to be burned into the picture.</summary>
    private List<string> BuildSubtitleBurnFilters(string sourcePath)
    {
        var escaped = EscapeFilterPath(sourcePath);
        return SubtitleTracks
            .Where(t => t.Action == SubtitleTrack.HardSub)
            .Select(t => $"subtitles='{escaped}':si={t.Index}")
            .ToList();
    }

    /// <summary>
    /// A file path for use inside a quoted filter option. Even in quotes FFmpeg treats ":" as an option
    /// separator, and a quote ends the string.
    /// </summary>
    private static string EscapeFilterPath(string path) =>
        path.Trim().Trim('"').Replace('\\', '/').Replace(":", @"\:").Replace("'", @"'\\\''");

    // ----- Video encoder -----

    private string BuildVideoCodecArguments()
    {
        if (VideoEncoder.Family == EncoderFamily.Copy)
            return "-c:v copy";

        var encoder = VideoEncoder.Name;
        var parts = new List<string> { $"-c:v {encoder}" };

        // The speed/quality step, under the name each kind of encoder gives it.
        if (HasEncoderPresets && EncoderPresets.Contains(EncoderPreset))
            parts.Add(VideoEncoder.Family == EncoderFamily.Amf ? $"-quality {EncoderPreset}" : $"-preset {EncoderPreset}");

        if (AreSoftwareEncoderOptionsEnabled)
        {
            var isX264 = encoder == "libx264";

            // x265 knows fewer names than x264; an unknown one would stop the encode, so it is left out.
            if (Tune != NoneOption && (isX264 || Tune != "film"))
                parts.Add($"-tune {Tune}");
            if (Profile != AutoOption && (isX264 || Profile == "main"))
                parts.Add($"-profile:v {Profile}");
            if (Level != AutoOption)
                parts.Add(isX264 ? $"-level {Level}" : $"-x265-params level-idc={Level}");
        }

        if (!HasRateControl)
        {
            // ProRes and DNxHR take their quality from the profile given in the extra options.
        }
        else if (IsBitrateMode && int.TryParse(TargetBitrate, out var bitrate) && bitrate > 0)
        {
            // Hardware encoders only honour a bitrate when their own rate-control mode is named with it.
            var constant = RateControl == ConstantBitrate;
            parts.Add(VideoEncoder.Family switch
            {
                EncoderFamily.Qsv => constant ? $"-b:v {bitrate}k -maxrate {bitrate}k" : $"-b:v {bitrate}k",
                EncoderFamily.Amf => constant ? $"-rc cbr -b:v {bitrate}k" : $"-rc vbr_peak -b:v {bitrate}k",
                EncoderFamily.Nvenc => constant ? $"-rc cbr -b:v {bitrate}k" : $"-rc vbr -b:v {bitrate}k",
                _ => constant ? $"-b:v {bitrate}k -maxrate {bitrate}k -bufsize {bitrate * 2}k" : $"-b:v {bitrate}k",
            });
        }
        else
        {
            // Each encoder family has its own constant-quality control; the slider value feeds whichever applies.
            parts.Add(VideoEncoder.Family switch
            {
                EncoderFamily.Nvenc => $"-rc vbr -cq {Crf} -b:v 0",
                // Constant QP. Measured on Intel hardware: "-global_quality" on its own left the output
                // identical at every value, for H.264, HEVC and AV1 alike, while "-q:v" is honoured.
                EncoderFamily.Qsv => $"-q:v {Crf}",
                EncoderFamily.Amf => $"-rc cqp -qp_i {Crf} -qp_p {Crf}",
                _ when encoder == "libvpx-vp9" => $"-crf {Crf} -b:v 0",
                _ => $"-crf {Crf}",
            });
        }

        if (!string.IsNullOrWhiteSpace(ExtraVideoArguments))
            parts.Add(ExtraVideoArguments.Trim());

        return string.Join(" ", parts);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
