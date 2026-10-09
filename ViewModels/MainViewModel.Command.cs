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
        // So is the sound of video layers, for the same reason.
        var layerAudio = animated || tracks.Count == 0 ? [] : GetLayerAudioSources();
        var copyAnyAudio = !mixAudio && tracks.Where((_, index) => (voiceover is null && layerAudio.Count == 0) || index > 0).Any(t => t.IsPassthrough);

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

        // Audio that was edited on the timeline (slipped, silenced in places), that sits on the sequence
        // somewhere other than its start, that is ducked under a voice, or that has layer sound to take in, is
        // made ready first, each track on a chain of its own; what follows then works from that instead of
        // from the input. Not with cuts made by the demuxer, which copies packets: no filter can be put
        // in front of those.
        var preparedAudio = new Dictionary<int, string[]>();
        if (!cutWithDemuxer)
        {
            var mainTiming = BuildMainAudioTiming();

            // Auto-duck. The voices are mixed into one signal, and each ducked sound gets a copy of it to be
            // pressed down by. A copy that nothing listened to would be an error, so they are counted first.
            bool IsDucked(AudioTrack track) => track is { AutoDuck: true, IsVoice: false } && !ReferenceEquals(track, _silentBase);
            var ducked = tracks.Count(IsDucked) + layerAudio.Count(l => l is { AutoDuck: true, IsVoice: false });
            var keys = new Queue<string>();
            if (ducked > 0)
            {
                var voices = new List<string>();
                foreach (var track in tracks.Where(t => t.IsVoice && !ReferenceEquals(t, _silentBase)))
                    voices.Add($"[0:a:{track.Index}]{string.Join(",", track.BuildEditChain().Concat(mainTiming).DefaultIfEmpty("anull"))}");
                voices.AddRange(layerAudio.Where(l => l.IsVoice).Select(BuildLayerAudio));

                // The voiceover is timed against the finished video, which is the sequence only while nothing is cut out of it.
                if (voiceoverInput is { } voiceInput && segments.Count == 0)
                    voices.Add($"[{voiceInput}:a:0]{BuildVoiceoverChain()}");

                if (voices.Count > 0)
                {
                    for (var n = 0; n < voices.Count; n++)
                        graph.Add($"{voices[n]},aresample=48000,apad[dvoice{n}]");

                    var key = "[dvoice0]";
                    if (voices.Count > 1)
                    {
                        key = "[dkey]";
                        graph.Add($"{string.Concat(voices.Select((_, n) => $"[dvoice{n}]"))}amix=inputs={voices.Count}:normalize=0[dkey]");
                    }

                    if (ducked == 1)
                    {
                        keys.Enqueue(key);
                    }
                    else
                    {
                        var copies = Enumerable.Range(0, ducked).Select(n => $"[dkey{n}]").ToList();
                        graph.Add($"{key}asplit={ducked}{string.Concat(copies)}");
                        copies.ForEach(keys.Enqueue);
                    }
                }
            }

            for (var k = 0; k < tracks.Count; k++)
            {
                var track = tracks[k];
                var silent = ReferenceEquals(track, _silentBase);
                var edits = silent ? [] : track.BuildEditChain().Concat(mainTiming).ToList();

                // A mono track that now begins with silence is made two-channel. FFmpeg's AAC encoder has been
                // seen to stop dead on exactly that (one channel, digital silence first, 192k or more); the
                // same sound on two channels goes through.
                if (track.Stream.Channels == 1 && edits.Any(e => e.StartsWith("adelay", StringComparison.Ordinal)))
                    edits.Add("aformat=channel_layouts=stereo");

                var mixedIn = k == 0 ? layerAudio : [];
                var duck = keys.Count > 0 && IsDucked(track);
                if (edits.Count == 0 && mixedIn.Count == 0 && !duck && !silent)
                    continue;

                var label = $"[0:a:{track.Index}]";
                if (silent)
                {
                    // The sequence has no sound of its own: silence as long as it is, for the rest to be mixed into.
                    graph.Add($"anullsrc=r=48000:cl=stereo:d={Number(Math.Max(SequenceSeconds, 0.1))}[asilent]");
                    label = "[asilent]";
                }
                else if (edits.Count > 0)
                {
                    graph.Add($"{label}{string.Join(",", edits)}[aedit{track.Index}]");
                    label = $"[aedit{track.Index}]";
                }

                if (duck)
                {
                    graph.Add($"{label}{keys.Dequeue()}{DuckFilter}[aduck{track.Index}]");
                    label = $"[aduck{track.Index}]";
                }

                if (mixedIn.Count > 0)
                {
                    for (var n = 0; n < mixedIn.Count; n++)
                    {
                        if (keys.Count > 0 && mixedIn[n] is { AutoDuck: true, IsVoice: false })
                        {
                            graph.Add($"{BuildLayerAudio(mixedIn[n])}[alraw{n}]");
                            graph.Add($"[alraw{n}]{keys.Dequeue()}{DuckFilter}[alayer{n}]");
                        }
                        else
                        {
                            graph.Add($"{BuildLayerAudio(mixedIn[n])}[alayer{n}]");
                        }
                    }

                    graph.Add($"{label}{string.Concat(mixedIn.Select((_, n) => $"[alayer{n}]"))}amix=inputs={mixedIn.Count + 1}:duration=first:normalize=0[amixed{track.Index}]");
                    label = $"[amixed{track.Index}]";
                }

                // A filter's output can be read once; with cuts it is wanted once per segment.
                var uses = cutWithFilters ? segments.Count : 1;
                if (uses > 1)
                {
                    preparedAudio[track.Index] = Enumerable.Range(0, uses).Select(u => $"[aprep{track.Index}_{u}]").ToArray();
                    graph.Add($"{label}asplit={uses}{string.Concat(preparedAudio[track.Index])}");
                }
                else
                {
                    preparedAudio[track.Index] = [label];
                }
            }
        }

        if (cutWithFilters)
        {
            // Every kept stretch is cut out of the video and each audio track with its timestamps reset, then
            // the pieces are joined with one concat filter so all streams stay locked together.
            var concatInputs = new StringBuilder();
            for (var i = 0; i < segments.Count; i++)
            {
                var range = $"start={Seconds(segments[i].Start)}:end={Seconds(segments[i].End)}";
                var suffix = i.ToString(CultureInfo.InvariantCulture);
                var chain = new List<string>();
                if (FrameEngine)
                {
                    // The engine composes this stretch of the sequence on a clock that starts at 0. Burned-in
                    // subtitles are placed by the main video's own time, which the clock is set to while they are drawn.
                    chain.AddRange(BuildVideoFilters(suffix, segments[i].Start.TotalSeconds, segments[i].End.TotalSeconds));
                    if (burnFilters.Count > 0)
                    {
                        chain.Add($"setpts=PTS{Signed(segments[i].Start.TotalSeconds - MainShift)}/TB");
                        chain.AddRange(burnFilters);
                        chain.Add("setpts=PTS-STARTPTS");
                    }
                }
                else
                {
                    // Burned-in subtitles are placed by timestamp, so they must be drawn before the timestamps are reset.
                    var frameFilters = BuildVideoFilters(suffix);
                    chain.Add($"trim={range}");
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
                }

                if (framerate is not null)
                    chain.Add($"fps=fps={framerate}");

                graph.Add($"[0:v:0]{string.Join(",", chain)}[v{i}]");
                concatInputs.Append($"[v{i}]");

                foreach (var track in tracks)
                {
                    var from = preparedAudio.TryGetValue(track.Index, out var ready) ? ready[i] : $"[0:a:{track.Index}]";
                    graph.Add($"{from}atrim={range},asetpts=PTS-STARTPTS[a{track.Index}_{i}]");
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
                var chain = BuildVideoFilters(labelSuffix: "", 0, SequenceSeconds);

                // Burned-in subtitles are placed by the main video's own time: the clock is set to it while
                // they are drawn, wherever on the sequence the main video has been put.
                var shift = MainShift;
                if (burnFilters.Count > 0 && Math.Abs(shift) > 0.001)
                {
                    chain.Add($"setpts=PTS{Signed(-shift)}/TB");
                    chain.AddRange(burnFilters);
                    chain.Add($"setpts=PTS{Signed(shift)}/TB");
                }
                else
                {
                    chain.AddRange(burnFilters);
                }

                chain.AddRange(onceFilters);
                if (framerate is not null)
                    chain.Add($"fps=fps={framerate}");
                chain.AddRange(lastFilters);
                if (chain.Count > 0)
                    simpleVideoFilters = string.Join(",", chain);
            }
        }

        var audioOptions = BuildAudio(tracks, mixAudio, duckAudio, cutWithFilters, duration, voiceoverInput, preparedAudio, graph, maps);

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

    // ----- Live Preview -----

    /// <summary>Whether the player shows the picture with the export's filters applied. Bound to the box beside the playback controls, and remembered.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _livePreview = AppSettings.Current.LivePreview;

    /// <summary>Raised whenever a setting changed that may have changed what Live Preview should show.</summary>
    public event Action? LiveFilterInvalidated;

    partial void OnLivePreviewChanged(bool value)
    {
        if (AppSettings.Current.LivePreview == value)
            return;

        AppSettings.Current.LivePreview = value;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still holds for this session.
        }
    }

    /// <summary>
    /// The filter graph Live Preview runs on the picture, for mpv's lavfi-complex: the same per-frame chain the
    /// export uses (crop, the Frame &amp; Layer Engine, scaling, color, LUT), with burned-in subtitles, from the
    /// video track to the screen. Empty when the export would not filter the picture at all (Copy), which
    /// is then what the preview shows too.
    ///
    /// The cuts are not part of it: the preview stays on the source's own timeline, where Play only cut
    /// segments does the skipping. Fades are placed by the length of the output and are left out, and
    /// auto-captions are drawn only once they have been transcribed and while nothing is cut, which is when
    /// their times are the source's.
    /// </summary>
    /// <param name="surfaceWidth">Width of the player on screen, in screen pixels: the picture is not built larger than it is shown.</param>
    public string BuildLiveFilterGraph(double surfaceWidth, double surfaceHeight)
    {
        if (!HasSource || _mediaInfo is { Video: null } || (VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput))
            return "";

        // The whole graph is built for a frame the size the player shows, not the size of the export: a 4K
        // layout watched in a 900-pixel player is composed at a quarter of its width and a sixteenth of the
        // work. The scale goes in steps, so that resizing the window a little does not rebuild the graph.
        var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
        var shown = surfaceWidth > 0 && surfaceHeight > 0 ? Math.Min(surfaceWidth / frameWidth, surfaceHeight / frameHeight) : 1;
        _liveScale = LiveScaleSteps.FirstOrDefault(step => step >= shown, 1);

        _buildingLiveGraph = true;
        try
        {
            // The player's clock is the sequence's, so the whole of it is composed as one stretch from 0.
            var chain = BuildVideoFilters(labelSuffix: "", 0, SequenceSeconds);
            var (burn, shift) = (BuildSubtitleBurnFilters(LocalMediaPath), MainShift);
            if (burn.Count > 0 && Math.Abs(shift) > 0.001)
            {
                chain.Add($"setpts=PTS{Signed(-shift)}/TB");
                chain.AddRange(burn);
                chain.Add($"setpts=PTS{Signed(shift)}/TB");
            }
            else
            {
                chain.AddRange(burn);
            }

            if (AutoCaptions && Segments.Count == 0 && File.Exists(CaptionsFilePath))
                chain.Add(BuildCaptionOverlay(CaptionsFilePath));

            return chain.Count == 0 ? "" : $"[vid1]{string.Join(",", chain)}[vo]";
        }
        finally
        {
            (_buildingLiveGraph, _liveScale) = (false, 1);
        }
    }

    // What makes the live graph cheaper than the export's, while showing the same layers in the same places:
    //   - everything is sized for the player (see above), sources being scaled down before anything is done to them;
    //   - the blurred background is blurred at a quarter of that size and scaled back up, which a blur hides;
    //   - a layer's rounded or feathered outline is worked out once and reused, where the export works it
    //     out again for every frame;
    //   - a source faster than 30 frames a second is previewed at half its rate.
    private static readonly double[] LiveScaleSteps = [0.25, 1 / 3.0, 0.5, 0.75, 1];

    private bool _buildingLiveGraph;
    private double _liveScale = 1;

    /// <summary>A size in output pixels as the graph being built needs it: as it is for the export, scaled (and kept even) for Live Preview.</summary>
    private int Sized(int pixels) => _liveScale >= 1 ? pixels : Math.Max((int)Math.Round(pixels * _liveScale / 2) * 2, 2);

    /// <summary>A distance in output pixels, scaled the same way but not rounded to an even number.</summary>
    private int Placed(int pixels) => _liveScale >= 1 ? pixels : (int)Math.Round(pixels * _liveScale);

    // ----- Render preview -----

    /// <summary>
    /// A short, small encode that shows what the current filters do to the picture: the same per-frame
    /// chain, layers and burned-in subtitles as the real command, then scaled down. Speed matters more
    /// than quality here, so it always uses the fastest software settings.
    /// </summary>
    /// <param name="ranges">The stretches of the timeline to show, joined end to end: one, or several cut segments.</param>
    /// <param name="captionsPath">An auto-caption file made for these stretches, or null.</param>
    private string BuildPreviewCommand(string outputPath, List<(double Start, double End)> ranges, int percent, string? captionsPath)
    {
        var burnFilters = BuildSubtitleBurnFilters(LocalMediaPath);

        // What is done once, to the finished picture. Fades are left out: they are placed by the length of
        // the whole output, which a few seconds do not have.
        var finishing = BuildWholeVideoFilters(duration: 0);
        if (captionsPath is not null)
            finishing.Add(BuildCaptionOverlay(captionsPath));
        if (GetTargetFramerate() is { } framerate)
            finishing.Add($"fps=fps={framerate}");

        var factor = Number(Math.Clamp(percent, 5, 100) / 100.0);
        finishing.Add($"scale=trunc(iw*{factor}/2)*2:trunc(ih*{factor}/2)*2");
        finishing.Add("format=yuv420p");

        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? Quote(DependencyUpdater.FfmpegPath) : "ffmpeg";
        var hardware = HardwareDecoding ? "-hwaccel auto " : "";
        const string encode = "-c:v libx264 -preset ultrafast -crf 24 -c:a aac -b:a 128k -movflags +faststart";
        var hasSound = _mediaInfo is not { Audio.Count: 0 };

        if (!FrameEngine)
        {
            // Without the engine the timeline is the file's own: each stretch is simply a stretch of the file.
            var (startSeconds, durationSeconds) = (ranges[0].Start, ranges[0].End - ranges[0].Start);
            var chain = BuildVideoFilters(labelSuffix: "");
            chain.AddRange(burnFilters);
            chain.AddRange(finishing);

            if (ranges.Count > 1)
            {
                // Each stretch is opened as an input of its own, already cut to length, and the stretches are
                // joined before anything is done to the picture: what is filtered is the trimmed timeline.
                var cutInputs = string.Join(" ", ranges.Select(r => $"{hardware}-ss {Number(r.Start)} -t {Number(r.End - r.Start)} -i {Quote(LocalMediaPath)}"));
                var joined = $"{string.Concat(ranges.Select((_, i) => $"[{i}:v:0]"))}concat=n={ranges.Count}:v=1:a=0[joined];[joined]{string.Join(",", chain)}[v]";
                if (hasSound)
                    joined += $";{string.Concat(ranges.Select((_, i) => $"[{i}:a:0]"))}concat=n={ranges.Count}:v=0:a=1[a]";

                return $"{ffmpeg} -hide_banner -y {cutInputs} -filter_complex \"{joined}\" -map \"[v]\"{(hasSound ? " -map \"[a]\"" : "")} {encode} {Quote(outputPath)}";
            }

            // Seeking before the input is fast. Burned-in subtitles need the original timestamps, though,
            // so with those the seek comes after the input: slower, but the text lands on the right frames.
            var seek = $"-ss {Number(startSeconds)} -t {Number(durationSeconds)}";
            var input = hardware + (burnFilters.Count > 0 ? $"-i {Quote(LocalMediaPath)} {seek}" : $"{seek} -i {Quote(LocalMediaPath)}");
            return $"{ffmpeg} -hide_banner -y {input} -map 0:v:0 -map 0:a:0? -vf \"{string.Join(",", chain)}\" {encode} {Quote(outputPath)}";
        }

        // With the engine every stretch of the sequence is composed on its own, as the export composes a cut
        // segment. The file is opened once per stretch, wound forward to the first frame that stretch shows
        // of it, which is where its frames then count from.
        var (mainStart, mainEnd) = GetMainSpan();
        var shift = MainShift;
        var inputs = new List<string>();
        var graph = new List<string>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var (from, to) = ranges[i];
            var (seenFrom, seenTo) = (Math.Max(from, mainStart), Math.Min(to, mainEnd));
            var origin = Math.Max(Math.Min(seenFrom, mainEnd) - shift, 0);
            inputs.Add($"{hardware}-ss {Number(origin)} -i {Quote(LocalMediaPath)}");

            List<string> chain;
            _inputOrigin = origin;
            try
            {
                chain = BuildVideoFilters(i.ToString(CultureInfo.InvariantCulture), from, to);
            }
            finally
            {
                _inputOrigin = 0;
            }

            if (burnFilters.Count > 0)
            {
                chain.Add($"setpts=PTS{Signed(from - shift)}/TB");
                chain.AddRange(burnFilters);
                chain.Add("setpts=PTS-STARTPTS");
            }

            graph.Add($"[{i}:v:0]{string.Join(",", chain)}[pv{i}]");
            if (!hasSound)
                continue;

            // The main video's first track for as long as it is there in this stretch, and silence around it.
            var lead = (int)Math.Round((seenFrom - from) * 1000);
            graph.Add(seenTo - seenFrom > 0.001
                ? $"[{i}:a:0]atrim=end={Number(seenTo - seenFrom)},asetpts=PTS-STARTPTS{(lead > 0 ? $",adelay={lead}:all=1" : "")},apad=whole_dur={Number(to - from)}[pa{i}]"
                : $"anullsrc=r=48000:cl=stereo:d={Number(to - from)}[pa{i}]");
        }

        if (ranges.Count > 1)
        {
            graph.Add($"{string.Concat(ranges.Select((_, i) => hasSound ? $"[pv{i}][pa{i}]" : $"[pv{i}]"))}concat=n={ranges.Count}:v=1:a={(hasSound ? 1 : 0)}[joined]{(hasSound ? "[a]" : "")}");
            graph.Add($"[joined]{string.Join(",", finishing)}[v]");
        }
        else
        {
            graph.Add($"[pv0]{string.Join(",", finishing)}[v]");
        }

        var sound = !hasSound ? "" : ranges.Count > 1 ? " -map \"[a]\"" : " -map \"[pa0]\"";
        return $"{ffmpeg} -hide_banner -y {string.Join(" ", inputs)} -filter_complex \"{string.Join(";", graph)}\" -map \"[v]\"{sound} {encode} {Quote(outputPath)}";
    }

    // ----- Audio -----

    /// <summary>
    /// The audio tracks that reach the output, in order. Before a source has been inspected there is
    /// nothing to list, so a single stand-in track carries the tab's default codec and bitrate.
    /// </summary>
    private List<AudioTrack> GetOutputAudioTracks()
    {
        if (_mediaInfo is not null)
        {
            var kept = AudioTracks.Where(t => !t.IsDropped).ToList();

            // No sound of the sequence's own, but sound on its layers: a silent track is made for them to be
            // mixed into, so that they are heard. It is always encoded.
            if (kept.Count == 0 && GetLayerAudioSources().Count > 0)
            {
                ApplyAudioDefaults(_silentBase);
                (_silentBase.Action, _silentBase.Codec) = (AudioTrack.Reencode, MixedAudioCodec);
                kept.Add(_silentBase);
            }

            return kept;
        }

        var standIn = new AudioTrack(new AudioStreamInfo(0, "", 2, "", 48000, 0, "", ""));
        ApplyAudioDefaults(standIn);
        return [standIn];
    }

    // Stands for the silence that layer sound is mixed into when the sequence has none of its own. It is not
    // a stream of the file: the graph makes it (anullsrc), under a number no real track has.
    private readonly AudioTrack _silentBase = new(new AudioStreamInfo(900, "", 2, "stereo", 48000, 0, "", "")) { Title = "Timeline Audio" };

    /// <summary>How far a sound set to Auto-Duck is turned down while a voice speaks, in decibels.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private double _duckAmountDb = -15;

    /// <summary>
    /// The ducking step: its first input is the sound, its second the voices. The compressor is driven hard
    /// (the voices are heard by it far louder than they are), so that while anyone speaks it shuts the sound
    /// almost off; and only a share of the result is that shut-off sound, the rest being the sound untouched.
    /// The share is what makes the dip the number of decibels asked for, however loud the voice happens to be.
    /// </summary>
    private string DuckFilter =>
        $"sidechaincompress=threshold=0.02:ratio=20:attack=15:release=350:level_sc=16:mix={Number(1 - Math.Pow(10, Math.Clamp(DuckAmountDb, -40, -1) / 20))}";

    /// <summary>
    /// What puts the main video's own sound where its picture is on the sequence: cut to the part of the
    /// file that is used, and held back until the main video comes in. Empty while the main video is the
    /// whole sequence.
    /// </summary>
    private List<string> BuildMainAudioTiming()
    {
        var steps = new List<string>();
        if (IsMainWholeSequence)
            return steps;

        var (start, end) = GetMainSpan();
        var offset = _mainVideoRow.MediaOffset;
        if (offset > 0.001 || _mainVideoRow.Duration > 0.001)
            steps.Add($"atrim={(offset > 0.001 ? $"start={Number(offset)}:" : "")}end={Number(offset + end - start)},asetpts=PTS-STARTPTS");
        if (start > 0.001)
            steps.Add($"adelay={(int)Math.Round(start * 1000)}:all=1");
        return steps;
    }

    /// <summary>The voiceover's own steps: cut to its trim handles, moved to where it starts, set to its gain.</summary>
    private string BuildVoiceoverChain()
    {
        var steps = new List<string>();
        if (VoiceoverTrimStart > 0.05 || (VoiceoverDuration > 0 && VoiceoverTrimEnd < VoiceoverDuration - 0.05 && VoiceoverTrimEnd > VoiceoverTrimStart))
            steps.Add($"atrim=start={Number(Math.Max(VoiceoverTrimStart, 0))}:end={Number(VoiceoverTrimEnd)},asetpts=PTS-STARTPTS");
        if (VoiceoverStartSeconds >= 0.01)
            steps.Add($"adelay={(int)Math.Round(VoiceoverStartSeconds * 1000)}:all=1");
        if (Math.Abs(VoiceoverGainDb) >= 0.05)
            steps.Add($"volume={Number(VoiceoverGainDb)}dB");
        return steps.Count > 0 ? string.Join(",", steps) : "anull";
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
        Dictionary<int, string[]> preparedAudio, List<string> graph, List<string> maps)
    {
        var options = new List<string>();

        // After a filter-based cut each track continues from the concat filter; otherwise from the input.
        // (Or, without cuts, from what was made ready for it: see preparedAudio in BuildFfmpegCommand.)
        string Source(AudioTrack track) =>
            cutWithFilters ? $"[ac{track.Index}]"
            : preparedAudio.TryGetValue(track.Index, out var ready) ? ready[0]
            : $"[0:a:{track.Index}]";

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
            graph.Add($"[{input}:a:0]{BuildVoiceoverChain()}[voiceover]");
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
            else if (cutWithFilters || preparedAudio.ContainsKey(track.Index))
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
    /// <summary>
    /// The sound of a video layer, as a chain that ends where the caller labels it: read from the layer's file
    /// inside the graph, started where the layer's picture starts (or as far from there as it was slipped),
    /// and no longer than the layer is on screen.
    /// </summary>
    private string BuildLayerAudio(Layer layer)
    {
        var chain = new List<string> { $"amovie='{EscapeFilterPath(layer.ImagePath)}'" };
        var trim = layer.MediaOffset > 0.01 ? $"atrim=start={Number(layer.MediaOffset)}" : "";
        if (layer.Duration > 0.001)
            trim = (trim.Length > 0 ? trim + ":" : "atrim=") + $"end={Number(layer.MediaOffset + layer.Duration)}";
        if (trim.Length > 0)
            chain.Add(trim + ",asetpts=PTS-STARTPTS");

        chain.Add("aresample=48000");
        if (Math.Abs(layer.AudioGainDb) >= 0.05)
            chain.Add($"volume={Number(Math.Clamp(layer.AudioGainDb, -40, 24))}dB");
        var startsAt = Math.Max(layer.StartTime + (AudioLinked || layer.IsAudio ? 0 : layer.AudioOffset), 0);
        if (startsAt > 0.01)
            chain.Add($"adelay={(int)Math.Round(startsAt * 1000)}:all=1");
        return string.Join(",", chain);
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
    /// <param name="from">
    /// With the Layer Engine: where on the sequence the stretch of picture being composed begins, and
    /// (<paramref name="to"/>) where it ends. Every layer is placed in time by them.
    /// </param>
    private List<string> BuildVideoFilters(string labelSuffix, double from = 0, double to = 0)
    {
        var filters = new List<string>();
        if (Deinterlace)
            filters.Add("yadif");

        // Live Preview: fewer frames from a fast source, and fewer pixels, before anything else is done to them.
        if (_buildingLiveGraph)
        {
            if (SourceFrameRate > 40)
                filters.Add($"fps={Number(SourceFrameRate / 2)}");
            if (_liveScale < 1)
                filters.Add($"scale=trunc(iw*{Number(_liveScale)}/2)*2:trunc(ih*{Number(_liveScale)}/2)*2:flags=fast_bilinear");
        }

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
            filters.Add(BuildFrameEngine(crop, labelSuffix, from, to));
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
                filters.Add($"scale={Sized(vertical.Width)}:{Sized(vertical.Height)}");
            else if (hasWidth || hasHeight)
                filters.Add($"scale={(hasWidth ? Sized(width) : -2)}:{(hasHeight ? Sized(height) : -2)}");

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

    // A preview encode opens the main video's file already wound forward to where it begins; the file's frames
    // then count from there, and this is how far in that is.
    private double _inputOrigin;

    // Frame rates that are not the round numbers they are quoted as.
    private static readonly (double Rate, string Exact)[] BroadcastRates =
        [(23.976, "24000/1001"), (29.97, "30000/1001"), (59.94, "60000/1001"), (119.88, "120000/1001")];

    /// <summary>The rate the sequence's canvas ticks at: the main video's own, so that its frames fall one to a tick.</summary>
    private string CanvasRate
    {
        get
        {
            var rate = SourceFrameRate;
            foreach (var (near, exact) in BroadcastRates)
            {
                if (Math.Abs(rate - near) < 0.01)
                    return exact;
            }

            return Number(rate);
        }
    }

    /// <summary>A number with its sign always written, to follow PTS in a setpts expression.</summary>
    private static string Signed(double value) => (value < 0 ? "-" : "+") + Number(Math.Abs(value));

    /// <summary>The enable option for a step that is only applied while every one of the given conditions holds; nothing when there are none.</summary>
    private static string Enabled(string? first, string? second = null) =>
        first is null && second is null ? "" : $":enable='{(first is not null && second is not null ? $"{first}*{second}" : first ?? second)}'";

    /// <summary>
    /// Composes one stretch of the sequence. The base is the sequence's own canvas: black, the size of the
    /// project, as long as the stretch, with a clock that belongs to nothing that is laid on it. Every track
    /// is laid on it in stacking order, the main video being one of them: cut to the part of its file this
    /// stretch shows, and set down at the moment it comes in. Before it starts and after it ends the canvas
    /// simply goes on without it. A layer may lie partly or wholly outside the canvas: it is cut off at the edge.
    ///
    /// The main video's frames are split into independent branches, one per thing that shows them (the
    /// blurred background, the main video itself, the pieces cut from it); nothing done to an upper layer can
    /// reach the ones below, because by then they are a finished picture. One shortcut is kept: the blurred
    /// background is not made at all while the main video would hide every pixel of it.
    ///
    /// Live Preview is the exception to the canvas: the player's frames are the clock there, the player having
    /// been given the sequence to play, so the canvas is made from them and what comes from the main video is
    /// switched off outside the main video's stretch instead of being cut to it.
    /// </summary>
    /// <param name="from">Where on the sequence the stretch begins, in seconds: the clock of what is composed starts there.</param>
    /// <param name="to">Where it ends.</param>
    private string BuildFrameEngine(string? centerCrop, string s, double from, double to)
    {
        var live = _buildingLiveGraph;
        var (width, height) = (Sized(FrameWidth), Sized(FrameHeight));
        var full = GetCenterRect();
        var center = (X: Placed(full.X), Y: Placed(full.Y), Width: Sized(full.Width), Height: Sized(full.Height));
        var main = _mainVideoRow;

        // How much of this stretch the main video is there for. A file whose length is not known is taken to
        // be all of it, and lends the canvas its clock as it does for Live Preview.
        var (mainStart, mainEnd) = GetMainSpan();
        var (seenFrom, seenTo) = (Math.Max(from, mainStart), Math.Min(to, mainEnd));
        var known = to - from > 0.001 && MainMediaSeconds > 0;
        var mainSeen = !known || seenTo - seenFrom > 0.001;
        var throughout = !known || (seenFrom - from < 0.001 && to - seenTo < 0.001);
        var clockFromInput = live || !known;

        // The stack, bottom first, without the layers that have nothing to show.
        var stack = GetStack().Where(l => l.IsMainVideo ? !l.IsHidden && mainSeen : l.IsUsable && (!l.IsVideo || mainSeen)).ToList();
        var motions = stack.ConvertAll(l => GetMotion(l, width, height, from));

        var covers = center.X <= 0 && center.Y <= 0 && center.X + center.Width >= width && center.Y + center.Height >= height;
        var hidesBackground = stack.Count > 0 && stack[0].IsMainVideo && covers && throughout && IsOpaque(main) && !motions[0].Any;
        var blurred = mainSeen && !hidesBackground && !_backgroundRow.IsHidden;

        // One copy of the main video's frames for each thing that shows them. Layers from files bring their own picture.
        var branches = new List<string>();
        if (clockFromInput)
            branches.Add($"[canvas_in{s}]");
        if (blurred)
            branches.Add($"[bg_in{s}]");
        if (stack.Any(l => l.IsMainVideo))
            branches.Add($"[center_in{s}]");
        for (var n = 0; n < stack.Count; n++)
        {
            if (stack[n].IsVideo)
                branches.Add($"[ui{n}_in{s}]");
            else if (live && stack[n].IsImage && motions[n].Reshapes)
                branches.Add($"[ui{n}_tick{s}]");
        }

        var graph = new StringBuilder(1024);

        // The main video, cut to what this stretch shows of it and set down where it comes in. Left as it is
        // when it is the whole sequence and all of it is wanted.
        if (!clockFromInput && mainSeen && !(IsMainWholeSequence && from < 0.001 && to >= SequenceSeconds - 0.001 && _inputOrigin == 0))
        {
            var (cutFrom, cutTo, lead) = (seenFrom - MainShift - _inputOrigin, seenTo - MainShift - _inputOrigin, seenFrom - from);
            graph.Append("trim=");
            if (cutFrom > 0.001)
                graph.Append("start=").Append(Number(cutFrom)).Append(':');
            graph.Append("end=").Append(Number(cutTo)).Append(",setpts=PTS-STARTPTS");
            if (lead > 0.001)
                graph.Append('+').Append(Number(lead)).Append("/TB");
            graph.Append(',');
        }

        // Nothing of it wanted at all: one frame is read and dropped, and the file is left alone.
        graph.Append(branches.Count switch
        {
            0 => "trim=end_frame=1,nullsink;",
            1 => $"null{branches[0]};",
            _ => $"split={branches.Count}{string.Concat(branches)};",
        });

        // The canvas. For Live Preview four pixels of the player's own frame are blackened and stretched, which
        // keeps its clock: frame for frame, through seeking.
        if (clockFromInput)
            graph.Append($"[canvas_in{s}]crop=2:2:0:0,format=yuv420p,lutyuv=y=16:u=128:v=128,scale={width}:{height}:flags=neighbor");
        else
            graph.Append($"color=c=black:s={width}x{height}:r={CanvasRate}:d={Number(to - from)},format=yuv420p");

        // What comes from the main video is there only while the main video is. Composed on the canvas, its
        // frames run out and the canvas carries on; on the player's clock it is switched off outside its stretch.
        var mainEnding = clockFromInput ? "" : ":eof_action=pass";
        var mainGate = clockFromInput && !throughout ? $"gte(t,{Number(seenFrom - from)})*lt(t,{Number(seenTo - from)})" : null;

        if (blurred)
        {
            graph.Append($"[base{s}];[bg_in{s}]");
            if (live)
            {
                // Blurred small and scaled back up: the blur hides the difference, and it is a sixteenth of the work.
                var (smallWidth, smallHeight) = (Math.Max(width / 8 * 2, 16), Math.Max(height / 8 * 2, 16));
                var smallRadius = Math.Clamp((int)Math.Round(Math.Clamp(BlurRadius, 5, 50) * _liveScale / 4), 1, Math.Max(Math.Min(smallWidth, smallHeight) / 4 - 1, 1));
                graph.Append($"scale={smallWidth}:{smallHeight}:force_original_aspect_ratio=increase:flags=fast_bilinear,crop={smallWidth}:{smallHeight},"
                             + $"boxblur={smallRadius}:{Math.Clamp(BlurPasses, 1, 5)},eq=brightness={Number(Math.Clamp(BackgroundDim, -0.5, 0))},"
                             + $"scale={width}:{height}:flags=bilinear");
            }
            else
            {
                // The blur radius cannot exceed half the smaller side of the colour planes, which are half size.
                var radius = Math.Min(Math.Clamp(BlurRadius, 5, 50), Math.Max(Math.Min(width, height) / 4 - 1, 1));
                graph.Append($"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},"
                             + $"boxblur={radius}:{Math.Clamp(BlurPasses, 1, 5)},eq=brightness={Number(Math.Clamp(BackgroundDim, -0.5, 0))}");
            }

            graph.Append($"[bg{s}];[base{s}][bg{s}]overlay=0:0{mainEnding}{Enabled(mainGate)}");
        }

        // The layers, each on the picture built so far. The running picture is labelled between steps;
        // after the last one it is left open, so the rest of the chain continues from it.
        for (var n = 0; n < stack.Count; n++)
        {
            var (layer, motion) = (stack[n], motions[n]);
            var name = layer.IsMainVideo ? "center" : $"ui{n}";
            var (x, y, layerWidth, layerHeight) = layer.IsMainVideo
                ? center
                : layer.GetOutputRect(width, height, SourceWidth, SourceHeight);

            // A layer whose size is animated is built once, at the largest it gets, and scaled from there.
            var (builtWidth, builtHeight) = (layerWidth, layerHeight);
            if (motion.Width is not null)
            {
                builtWidth = Even(motion.LargestWidth * width);
                builtHeight = Even((double)layerHeight * builtWidth / layerWidth);
            }

            // The main video. The crop frames this layer only; the background uses the whole picture.
            var mainSource = layer.IsMainVideo ? $"[center_in{s}]{(centerCrop is null ? "" : centerCrop + ",")}scale={builtWidth}:{builtHeight}" : null;
            var tick = live && layer.IsImage && motion.Reshapes ? $"[ui{n}_tick{s}]" : null;
            graph.Append($"[stage{n}{s}];");
            graph.Append(BuildLayer(layer, name, s, builtWidth, builtHeight, from, motion, mainSource, tick));

            // A turned picture is larger than the layer it was; it stays where its middle was.
            var (boxWidth, boxHeight) = GetBox(layer, motion, builtWidth, builtHeight);
            string placeX, placeY;
            if (motion.Any)
            {
                // The box is that much larger than the layer on each side, and grows and shrinks with it.
                var size = motion.Width is null ? "" : $"*({motion.Width})/{builtWidth}";
                var (marginX, marginY) = ((boxWidth - builtWidth) / 2.0, (boxHeight - builtHeight) / 2.0);
                placeX = (motion.X ?? Number(x)) + (marginX == 0 ? "" : $"-{Number(marginX)}{size}");
                placeY = (motion.Y ?? Number(y)) + (marginY == 0 ? "" : $"-{Number(marginY)}{size}");
            }
            else
            {
                (placeX, placeY) = (Number(x - (boxWidth - layerWidth) / 2), Number(y - (boxHeight - layerHeight) / 2));
            }

            string At(int offset) => motion.Any
                ? $"x='{placeX}{(offset == 0 ? "" : "+" + offset)}':y='{placeY}{(offset == 0 ? "" : "+" + offset)}':eval=frame"
                : $"{int.Parse(placeX, CultureInfo.InvariantCulture) + offset}:{int.Parse(placeY, CultureInfo.InvariantCulture) + offset}";

            var ending = new StringBuilder();
            if (layer.IsMainVideo || layer.IsVideo)
                ending.Append(mainEnding);

            // A video from a file runs for as long as it is asked to, and so does a still that was made into
            // a stream to be animated; the picture under it decides when the output ends.
            if (layer.IsVideoFile || (layer.IsImage && motion.Reshapes && !live))
                ending.Append(":shortest=1");

            // A layer with a time to appear and a time to go is only laid on between the two. The clock the
            // filter sees starts at this stretch of the picture, so the times are counted from there.
            string? timed = null;
            if (!layer.IsMainVideo && layer.HasTiming && (layer.StartTime > 0.001 || layer.Duration > 0.001))
            {
                var appears = layer.StartTime - from;
                timed = $"between(t,{Number(appears)},{(layer.Duration > 0.001 ? Number(appears + layer.Duration) : "1e9")})";
            }

            ending.Append(Enabled(layer.IsMainVideo || layer.IsVideo ? mainGate : null, timed));

            if (layer.Shadow)
            {
                // The shadow is the layer's own outline in black at reduced opacity, laid down first and offset.
                var offset = Placed(Math.Clamp(layer.ShadowOffset, 0, 200));
                graph.Append($";[{name}_layer{s}]split[{name}_top{s}][{name}_shadow_in{s}]");
                graph.Append($";[{name}_shadow_in{s}]format=yuva420p,lutyuv=y=16:u=128:v=128:a=val*{Number(Math.Clamp(layer.ShadowOpacity, 0, 1))},"
                             + $"boxblur=2:1:0:0:2:1[{name}_shadow{s}]");
                graph.Append($";[stage{n}{s}][{name}_shadow{s}]overlay={At(offset)}{ending}[{name}_shaded{s}]");
                graph.Append($";[{name}_shaded{s}][{name}_top{s}]overlay={At(0)}{ending}");
            }
            else
            {
                graph.Append($";[stage{n}{s}][{name}_layer{s}]overlay={At(0)}{ending}");
            }
        }

        return graph.ToString();
    }

    /// <summary>
    /// The size of the picture a layer is laid on the frame as: the layer itself, the box its corners reach
    /// when it is turned, or, when its turning is animated, the square it can turn all the way round in.
    /// </summary>
    private static (int Width, int Height) GetBox(Layer layer, Motion motion, int width, int height)
    {
        if (motion.Angle is null)
            return GetTurnedSize(layer, width, height);

        var across = (int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height) / 2) * 2;
        return (across, across);
    }

    /// <summary>Whether a layer hides everything under the rectangle it covers: solid, square-cornered, unturned, with nothing keyed or masked out.</summary>
    private static bool IsOpaque(Layer layer) =>
        layer.Opacity >= 100 && !layer.ChromaKey && !(layer.CustomMask && File.Exists(layer.MaskPath.Trim().Trim('"')))
        && layer.CornerRadius == 0 && !layer.Feather && Math.Abs(layer.Rotation % 360) < 0.05;

    /// <summary>The size of the picture once it has been turned: the box its corners then reach, in even pixels.</summary>
    private static (int Width, int Height) GetTurnedSize(Layer layer, int width, int height)
    {
        if (!layer.HasFilters || Math.Abs(layer.Rotation % 360) < 0.05)
            return (width, height);

        var angle = layer.Rotation * Math.PI / 180;
        var (cos, sin) = (Math.Abs(Math.Cos(angle)), Math.Abs(Math.Sin(angle)));
        return ((int)Math.Ceiling((width * cos + height * sin) / 2) * 2, (int)Math.Ceiling((width * sin + height * cos) / 2) * 2);
    }

    /// <summary>The filters that are a layer's own: mirroring, its LUT, noise, colour, blur and sharpening, in that order.</summary>
    /// <param name="frameWidth">The width of the frame being composed, which a blur given for a 1080-wide frame is scaled to.</param>
    private static List<string> BuildLayerFilters(Layer layer, int frameWidth)
    {
        var filters = new List<string>();
        if (!layer.HasFilters)
            return filters;

        if (layer.FlipHorizontal)
            filters.Add("hflip");
        if (layer.FlipVertical)
            filters.Add("vflip");

        var lut = layer.FilterLut.Trim().Trim('"');
        if (lut.Length > 0 && File.Exists(lut))
            filters.Add($"lut3d=file='{EscapeFilterPath(lut)}'");
        if (layer.FilterDenoise)
            filters.Add("hqdn3d");

        var (contrast, brightness) = (Math.Clamp(layer.FilterContrast, 0, 2), Math.Clamp(layer.FilterBrightness, -1, 1));
        var (saturation, gamma) = (Math.Clamp(layer.FilterSaturation, 0, 3), Math.Clamp(layer.FilterGamma, 0.1, 3));
        if (Math.Abs(contrast - 1) >= 0.005 || Math.Abs(brightness) >= 0.005 || Math.Abs(saturation - 1) >= 0.005 || Math.Abs(gamma - 1) >= 0.005)
            filters.Add($"eq=contrast={Number(contrast)}:brightness={Number(brightness)}:saturation={Number(saturation)}:gamma={Number(gamma)}");
        if (Math.Abs(layer.FilterHue) >= 0.05)
            filters.Add($"hue=h={Number(Math.Clamp(layer.FilterHue, -180, 180))}");
        if (layer.FilterBlur >= 0.05)
            filters.Add($"gblur=sigma={Number(Math.Max(Math.Clamp(layer.FilterBlur, 0, 50) * frameWidth / 1080.0 / 2, 0.2))}");
        if (layer.FilterSharpen >= 0.005)
            filters.Add($"cas=strength={Number(Math.Clamp(layer.FilterSharpen, 0, 1))}");
        return filters;
    }

    /// <summary>
    /// One layer as a finished picture with its transparency, labelled [name_layer]: cut from the main video
    /// (or the main video itself), or read from its image or video file, and scaled. Its own filters are then
    /// applied to it. What shows of it is the product of everything that makes parts of it transparent: its
    /// own transparency (a PNG's, or a color keyed out), a custom mask from a file, and the rounded or
    /// feathered outline. Opacity scales whatever results, and last of all the picture is turned, and, when
    /// its size is animated, scaled frame by frame.
    /// </summary>
    /// <param name="width">The size it is built at: its size on the frame, or the largest it gets when that is animated.</param>
    /// <param name="mainSource">For the main video: the chain that brings its picture, already scaled.</param>
    /// <param name="tick">For a still that changes shape in Live Preview: the branch of the player's frames that gives it a clock.</param>
    private string BuildLayer(Layer layer, string name, string s, int width, int height, double startSeconds, Motion motion, string? mainSource = null, string? tick = null)
    {
        var chain = new StringBuilder(512);
        if (mainSource is not null)
        {
            chain.Append(mainSource);
        }
        else if (layer.IsVideoFile)
        {
            // Read by a source filter inside the graph, so no second -i is needed. It repeats without end,
            // is given an even clock, and is started as far in as the main video is.
            // Its own video begins when the layer appears: by then this stretch of the picture may be some
            // way in (so the start of the layer's video is skipped), or not there yet (so it is held back).
            chain.Append($"movie='{EscapeFilterPath(layer.ImagePath)}':loop=0,setpts=N/FRAME_RATE/TB");
            var into = startSeconds - layer.StartTime + layer.MediaOffset;
            if (into > 0.01)
                chain.Append($",trim=start={Number(into)},setpts=PTS-STARTPTS");
            else if (into < -0.01)
                chain.Append($",setpts=PTS+{Number(-into)}/TB");
            chain.Append($",scale={width}:{height}");
        }
        else if (layer.IsImage)
        {
            // A single frame, which the overlay repeats for as long as the video runs.
            chain.Append($"movie='{EscapeFilterPath(layer.ImagePath)}',scale={width}:{height}");
        }
        else
        {
            // Cut out of the source by fractions of the frame, so the same layer works at any source size.
            chain.Append($"[{name}_in{s}]crop=trunc(iw*{Fraction(layer.SourceWidth)}/2)*2:trunc(ih*{Fraction(layer.SourceHeight)}/2)*2:"
                         + $"iw*{Fraction(layer.SourceX)}:ih*{Fraction(layer.SourceY)},scale={width}:{height}");
        }

        var own = BuildLayerFilters(layer, Sized(FrameWidth));
        if (own.Count > 0 && layer.IsImage)
        {
            // The colour filters know nothing of transparency: a picture's own is set aside and put back after them.
            chain.Append($",format=yuva420p,split[{name}_fc{s}][{name}_fa_in{s}];[{name}_fa_in{s}]alphaextract"
                         + $"{string.Concat(own.Where(f => f is "hflip" or "vflip").Select(f => "," + f))}[{name}_fa{s}];"
                         + $"[{name}_fc{s}]{string.Join(",", own)}[{name}_fd{s}];[{name}_fd{s}][{name}_fa{s}]alphamerge");
        }
        else if (own.Count > 0)
        {
            chain.Append(',').Append(string.Join(",", own));
        }

        if (layer.ChromaKey && layer.HasKeying)
        {
            chain.Append($",chromakey=0x{KeyColor(layer.ChromaColor)}:{Number(Math.Clamp(layer.ChromaSimilarity, 0.01, 1))}:{Number(Math.Clamp(layer.ChromaBlend, 0, 1))}");
        }

        chain.Append(",format=yuva420p");

        // The masks: grey pictures the size of the layer, white where it is to show. Each is a single picture
        // made once and held, not worked out again for every frame.
        var masks = new List<string>();
        var sources = new StringBuilder();
        if (layer.CustomMask && layer.HasMask && File.Exists(layer.MaskPath.Trim().Trim('"')))
        {
            sources.Append($";movie='{EscapeFilterPath(layer.MaskPath)}',scale={width}:{height},format=gray,trim=end_frame=1,loop=loop=-1:size=1[{name}_cmask{s}]");
            masks.Add($"[{name}_cmask{s}]");
        }

        if (BuildLayerMask(layer, width, height, _liveScale) is { } shape)
        {
            sources.Append($";color=c=black:s={width}x{height}:r=1,format=gray,geq=lum='255*{shape}',trim=end_frame=1,loop=loop=-1:size=1[{name}_smask{s}]");
            masks.Add($"[{name}_smask{s}]");
        }

        if (masks.Count > 0)
        {
            // The layer's own transparency is taken out, multiplied by each mask in turn, and put back.
            chain.Append($",split[{name}_pic{s}][{name}_a_in{s}];[{name}_a_in{s}]alphaextract[{name}_a0{s}]");
            chain.Append(sources);
            for (var k = 0; k < masks.Count; k++)
                chain.Append($";[{name}_a{k}{s}]{masks[k]}blend=all_mode=multiply:shortest=1[{name}_a{k + 1}{s}]");
            chain.Append($";[{name}_pic{s}][{name}_a{masks.Count}{s}]alphamerge");
        }

        var opacity = Math.Clamp(layer.Opacity, 0, 100) / 100.0;
        if (opacity < 1)
            chain.Append($",lutyuv=a=val*{Number(opacity)}");

        // A still picture is one frame, and one frame cannot change shape as time passes: it is made into a
        // frame for every tick first. For an encode it is simply repeated at the canvas's rate. For Live
        // Preview it is laid on see-through frames cut from the player's own, which carry the player's clock
        // through every seek; a stream counted up from nothing would have to be run through to catch up.
        if (layer.IsImage && motion.Reshapes)
        {
            if (tick is null)
                chain.Append($",loop=loop=-1:size=1,setpts=N/({CanvasRate}*TB)");
            else
                chain.Append($"[{name}_still{s}];{tick}crop=2:2:0:0,format=yuva420p,lutyuv=a=0,scale={width}:{height}:flags=neighbor[{name}_clock{s}];"
                             + $"[{name}_clock{s}][{name}_still{s}]overlay=format=auto,format=yuva420p");
        }

        // Turned about its middle, into a box large enough for its corners; what the box adds is transparent.
        var (boxWidth, boxHeight) = GetBox(layer, motion, width, height);
        if (motion.Angle is not null)
            chain.Append($",rotate='({motion.Angle})*PI/180':ow={boxWidth}:oh={boxHeight}:c=black@0");
        else if ((boxWidth, boxHeight) != (width, height) || (layer.HasFilters && Math.Abs(layer.Rotation % 360) >= 0.05))
            chain.Append($",rotate={Number(layer.Rotation)}*PI/180:ow={boxWidth}:oh={boxHeight}:c=black@0");

        // An animated size: the finished picture, box and all, scaled for each frame from the size it was built at.
        if (motion.Width is not null)
        {
            var across = boxWidth == width ? motion.Width : $"({motion.Width})*{Ratio((double)boxWidth / width)}";
            chain.Append($",scale=w='max(2,trunc({across}))':h='max(2,trunc(({across})*{Ratio((double)boxHeight / boxWidth)}))':eval=frame");
            if (_buildingLiveGraph)
                chain.Append(":flags=fast_bilinear");
        }

        return chain.Append($"[{name}_layer{s}]").ToString();
    }

    private static string Ratio(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>#RRGGBB as FFmpeg writes a color, RRGGBB; anything unreadable becomes green.</summary>
    private static string KeyColor(string color)
    {
        var hex = (color ?? "").Trim().TrimStart('#');
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : "00FF00";
    }

    /// <summary>
    /// The geq expression (0 to 1 per pixel) for a layer's shape, or null when it is a plain rectangle.
    /// It measures how far a pixel is inside a rounded rectangle: zero on the edge, rising inwards. Rounded
    /// corners cut the picture off at that edge; a feather lets it fade in over its width instead.
    /// </summary>
    private static string? BuildLayerMask(Layer element, int width, int height, double pixelScale = 1)
    {
        var limit = Math.Max(Math.Min(width, height) / 2 - 1, 1);
        var corner = Math.Min((int)Math.Round(Math.Clamp(element.CornerRadius, 0, 50) / 100.0 * Math.Min(width, height)), limit);
        var feather = element.Feather ? Math.Clamp((int)Math.Round(element.FeatherRadius * pixelScale), 1, limit) : 0;
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

        // Hardware encoders are particular about the pixel format they are handed; 4:2:0 is the one they all take.
        if (VideoEncoder.IsHardware && !ExtraVideoArguments.Contains("-pix_fmt", StringComparison.Ordinal))
            parts.Add("-pix_fmt yuv420p");

        if (!string.IsNullOrWhiteSpace(ExtraVideoArguments))
            parts.Add(ExtraVideoArguments.Trim());

        return string.Join(" ", parts);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
