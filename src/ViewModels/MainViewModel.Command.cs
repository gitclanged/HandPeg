using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The view model's side of building FFmpeg commands. The building itself is FilterGraphBuilder's, which reads
// nothing but the state it is handed; what is here gathers that state from the settings as they are now, asks
// for the command or the graph, and does what the builder does not: write the lists a command reads beside it.
public partial class MainViewModel
{
    // ----- The sequence as the builder is given it -----

    /// <summary>
    /// Gathers everything a filter graph is built from into one snapshot. The sequence comes first: a copy of the timeline
    /// document (TimelineForBuilder: its tracks of clips, the frame it is composed on and the rate its canvas ticks at),
    /// which the builder lowers into the graph (FilterGraphBuilder.Timeline.cs), so that the black canvas it lays every
    /// clip on is as long, as large and as fast as the timeline says, whatever the main video is doing on it.
    ///
    /// The document copy and the caption box are made for the snapshot: its own, so the builder cannot be handed a clip
    /// that changes while it works (it keeps what it lowers by the document), nor change one itself.
    /// </summary>
    private SequenceExportState CompileExportState()
    {
        var (frameWidth, frameHeight) = (FrameWidth, FrameHeight);

        // A layer learns its shape (its height for each unit of its width) whenever it is laid out, which is
        // what lets it grow about its middle. Building the graph used to lay the real layers out; now that it
        // works on copies, they are laid out here.
        if (FrameEngine)
        {
            foreach (var layer in Layers)
            {
                if (layer.IsUsable)
                    layer.GetOutputRect(frameWidth, frameHeight, SourceWidth, SourceHeight);
            }
        }

        EnsureDrawnMasks();

        // A clip that is still being looked at has no size or length yet, and is not rendered.
        var stack = GetStack().Where(l => l is { IsLoading: false, IsEmptyTrack: false }).ToList();
        var caption = Layer.FromState(CaptionLayer.ToState(), SourceWidth, SourceHeight, frameWidth, frameHeight);
        FilterGraphBuilder.FoldConstantKeys(caption);

        // Which of the files the layers name are there: the builder leaves out a mask or a LUT that is missing.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Note(string? path)
        {
            var name = (path ?? "").Trim().Trim('"');
            if (name.Length > 0 && FileState.Exists(name))
                files.Add(name);
        }

        // And which of the masks are read by their transparency: painted shows, see-through is cut away.
        var alphaMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in stack.Append(caption))
        {
            Note(layer.MaskPath);
            foreach (var lut in layer.Effects.Where(e => e.Type == Models.Effects.EffectRegistry.Lut))
                Note(lut.GetText("file"));
            var mask = layer.MaskPath.Trim().Trim('"');
            if (layer.CustomMask && files.Contains(mask) && MaskStore.UsesAlpha(mask))
                alphaMasks.Add(mask);
        }

        Note(CaptionsFilePath);
        foreach (var lut in SequenceEffects.Where(e => e.Type == Models.Effects.EffectRegistry.Lut))
            Note(lut.GetText("file"));

        // The blurred background's own cards (5.0-Release): its LUT files too, or neither renderer draws them.
        foreach (var lut in _backgroundRow.Effects.Where(e => e.Type == Models.Effects.EffectRegistry.Lut))
            Note(lut.GetText("file"));

        // Where each video layer's picture starts in its file, so the builder can seek in it (see MediaStarts).
        // And where their I-frames are, so a live graph opens each exactly at one (see SeekIndexes).
        var starts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var seekIndexes = new Dictionary<string, SeekIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in stack.Where(l => l.IsVideoFile))
        {
            var file = layer.ImagePath.Trim().Trim('"');
            if (MediaProbe.KnownVideoStart(file) is { } start)
                starts[file] = start;
            else
                LearnVideoStart(file);
            if (MediaProbe.KnownSeekIndex(file) is { } index)
                seekIndexes[file] = index;
        }

        // And where the picture of each proxy in use starts, on the proxy's own clock (see FilterGraphBuilder.AppendVideoFile).
        foreach (var proxy in _proxiesInUse.Values.Append(MainProxy).OfType<string>())
        {
            if (MediaProbe.KnownVideoStart(proxy) is { } start)
                starts[proxy] = start;
            else
                LearnVideoStart(proxy);
        }

        var tracks = GetOutputAudioTracks()
            .Select(t => new ExportAudioTrack(
                t.Index, t.Title, t.Codec, t.Bitrate, t.IsPassthrough, t.Stream.Channels == 1, t.AutoDuck, t.IsVoice,
                ReferenceEquals(t, _silentBase), t.HasProcessing, t.BuildEditChain(), t.GetFilterChain())
            {
                Offset = t.OffsetSeconds,
                Channels = t.Stream.Channels > 0 ? t.Stream.Channels : 2,
                MutedSpans = t.Pieces.Where(p => p.Muted).Select(p => (p.Start, p.End)).ToList(),
            })
            .ToList();

        return new SequenceExportState
        {
            // The sequence: its tracks of clips, its frame, its clock; its crop.
            Timeline = TimelineForBuilder(frameWidth, frameHeight),
            SourceFrameRate = SourceFrameRate,
            TargetFramerate = GetTargetFramerate(),
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            OutputWidth = OutputWidth,
            OutputHeight = OutputHeight,
            RequestedSize = GetRequestedSize(),
            OutputSize = GetOutputSize(),
            SampleAspectRatio = GetSampleAspectRatio(),
            HasCrop = HasCrop,
            CropFractions = GetCropFractions(),

            // The main video's file, and where its clips sit on the frame.
            LocalMediaPath = LocalMediaPath,
            HasSource = HasSource,
            SourceHasNoVideo = _mediaInfo is { Video: null },
            SourceMayHaveAudio = _mediaInfo is not { Audio.Count: 0 },
            MainMediaSeconds = MainMediaSeconds,
            CenterRect = GetCenterRect(),

            // What is laid on the canvas besides the clips.
            FrameEngine = FrameEngine,
            BackgroundHidden = _backgroundRow.IsHidden,
            BlurRadius = BlurRadius,
            BlurPasses = BlurPasses,
            BackgroundDim = BackgroundDim,
            BackgroundZoom = BackgroundZoom,
            BackgroundEffects = _backgroundRow.EffectsBypassed ? [] : CaptureBackgroundEffects() ?? [],
            CaptionLayer = caption,
            AutoCaptions = AutoCaptions,
            CaptionsFilePath = CaptionsFilePath,

            // Filters on the whole picture.
            Deinterlace = Deinterlace,
            Denoise = Denoise,
            TonemapToSdr = Colorspace == TonemapToSdr,
            LutPath = LutPath,
            ColorFilters = BuildColorFilters(),
            FadeIn = FadeIn,
            FadeOut = FadeOut,

            // Chapters (at the main video's clips, and at the markers), the Copy export's cuts list, subtitles.
            ChapterMarkers = ChapterMarkers,
            ChaptersAtCuts = ChaptersAtCuts,
            ChaptersAtMarkers = ChaptersAtMarkers && Markers.Count > 0,
            CutsFilePath = CutsFilePath,
            ChaptersFilePath = ChaptersFilePath,
            SoftSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.SoftSub).Select(t => t.Index).ToList(),
            BurnedSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.HardSub).Select(t => t.Index).ToList(),

            // Sound.
            AudioTracks = tracks,
            MergeAudioTracks = MergeAudioTracks,
            NormalizeAudio = NormalizeAudio,
            DuckAudio = DuckAudio,
            DuckAmountDb = DuckAmountDb,
            MixedAudioCodec = MixedAudioCodec,
            AudioBitrate = AudioBitrate,
            AudioSampleRate = AudioSampleRate,
            AudioChannels = AudioChannels,
            IncludeVoiceover = IncludeVoiceover,
            VoiceoverPath = VoiceoverPath,
            VoiceoverDuration = VoiceoverDuration,
            VoiceoverTrimStart = VoiceoverTrimStart,
            VoiceoverTrimEnd = VoiceoverTrimEnd,
            VoiceoverStartSeconds = VoiceoverStartSeconds,
            VoiceoverGainDb = VoiceoverGainDb,

            // The command around the graph. A bare "ffmpeg" means the local copy; one chosen in the settings is spelled out.
            FfmpegExecutable = DependencyUpdater.IsFfmpegOverridden ? Quote(DependencyUpdater.FfmpegPath) : "ffmpeg",
            HardwareDecodeArguments = HardwareDecoding ? GpuAdapters.DecodeArguments : "",
            OpenClBlur = EncoderProber.OpenClBlurAvailable,
            OpenClDeviceArguments = EncoderProber.OpenClDeviceArguments,
            CopyVideoEncoder = VideoEncoder.Family == EncoderFamily.Copy,
            IsAnimatedOutput = IsAnimatedOutput,
            VideoCodecArguments = BuildVideoCodecArguments(),
            EncoderPixelFormat = IsAnimatedOutput ? null : HardwarePixelFormat,
            Container = Container,
            WebOptimized = WebOptimized,
            OutputPath = string.IsNullOrWhiteSpace(DestinationPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), $"HandPeg_output.{Container}")
                : DestinationPath.Trim().Trim('"'),
            ExistingFiles = files,
            AlphaMasks = alphaMasks,
            MediaStarts = starts,
            SeekIndexes = seekIndexes,

            // Read by Live Preview only: an export reads the originals.
            PreviewPaths = new Dictionary<string, string>(_proxiesInUse, StringComparer.OrdinalIgnoreCase),
            MainPreviewHeight = MainProxyHeight,
        };
    }

    // Layer files being inspected for where their picture starts, so each is asked about once.
    private readonly HashSet<string> _learningStarts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Inspects a video layer's file that has not been inspected this session (one from a project that was
    /// opened, say), in the background; once it is known, the graphs are built again with it.
    /// </summary>
    private void LearnVideoStart(string file)
    {
        if (_isBackgroundWorker || !_learningStarts.Add(file))
            return;

        _ = Task.Run(async () =>
        {
            if (File.Exists(file) && await MediaProbe.ProbeAsync(file, _shutdown.Token) is not null)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(GenerateCommand);
        });
    }

    // Layer files whose seek tables have been warmed and whose I-frames have been asked for, this session.
    private readonly HashSet<string> _warmedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets every video layer's file ready to be seeked in, as soon as it is on the timeline (a clip dropped, a project
    /// opened): the start and end of the file (where its seek tables are), and points spread across a large one, read
    /// into the system's file cache, then its I-frames listed (from the disk cache when listed before), so the first
    /// seek into a part of it never visited is as quick as the next one. A file whose seek index is known already (taken
    /// up with the project or from the Timeline cache) is neither inspected nor listed again: only read. Background
    /// work, once per file and session; the live graph is built again after a listing.
    /// </summary>
    private void WarmLayerFiles()
    {
        if (_isBackgroundWorker)
            return;

        foreach (var layer in Layers)
        {
            var file = layer.IsVideoFile && !layer.IsDeleted ? layer.ImagePath.Trim().Trim('"') : "";
            if (file.Length == 0 || !_warmedFiles.Add(file))
                continue;

            var indexed = MediaProbe.KnownSeekIndex(file) is not null;
            if (MediaProbe.KnownVideoStart(file) is null)
                LearnVideoStart(file);
            _ = Task.Run(async () =>
            {
                try
                {
                    await WorkScheduler.Run(new WorkSpec($"Reading the index of {Path.GetFileName(file)}", WorkLane.Background) { Visible = false, Key = $"warm|{file}" },
                        work =>
                        {
                            MediaProbe.WarmSeekTables(file, work.Token);
                            return Task.CompletedTask;
                        }, _shutdown.Token);
                    if (!indexed && File.Exists(file) && (await FfmpegRunner.GetIFramesAsync(file, _shutdown.Token)).Count > 0)
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(InvalidateLiveFilter);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    AppLog.Write($"The index of {Path.GetFileName(file)} could not be read", ex);
                }
            });
        }
    }

    /// <summary>
    /// The export's command line for the settings as they are. When the command reads its cuts or its chapters
    /// from a list beside it, that list is written here: the builder only says that it is needed.
    /// </summary>
    private string BuildFfmpegCommand()
    {
        var state = CompileExportState();
        var command = FilterGraphBuilder.BuildExportCommand(state);
        if (command.UsesCutsFile)
            WriteCutsFile(state);
        if (command.UsesChaptersFile)
            WriteChaptersFile(command.UsesCutsFile);
        return command.CommandLine;
    }

    /// <summary>
    /// The state the export's command is made from, for <see cref="RunExportAsync"/> to try the GPU with (Start Encode and
    /// its software retry): null when the GPU does not render exports, the command was edited by hand, or in a background
    /// worker (which only queues).
    /// </summary>
    private SequenceExportState? ExportStateForGpu() =>
        !AppSettings.Current.WantsGpuExport || IsCommandManuallyEdited || _isBackgroundWorker ? null : CompileExportState();

    /// <summary>
    /// Runs an export's command (Start Encode and its software retry, RunEncodeAsync; each queued job, StartQueueAsync), 5.0:
    /// with <see cref="AppSettings.ExportRenderer"/> on the GPU and a <paramref name="state"/> that makes this very command
    /// (BuildExportCommand(state) is it: not edited by hand, nor changed by the hardware fallback, since), the GPU compositor
    /// draws the frames and FFmpeg encodes them with the command's own sound, captions and subtitles (GpuExport; the user's
    /// call, 5.0-Verify Q5: queued jobs too). An export it cannot draw, and any GPU export that fails before or while it
    /// renders, FFmpeg's graph makes, from the beginning, with the command as it stands (a hardware encoder that fails there
    /// is the caller's to retry in software).
    /// </summary>
    /// <param name="state">The state the command was made from (the settings' now, or a queued job's snapshot); null for FFmpeg only.</param>
    private async Task RunExportAsync(string command, SequenceExportState? state, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (AppSettings.Current.WantsGpuExport && state is not null)
        {
            try
            {
                var built = FilterGraphBuilder.BuildExportCommand(state);
                if (!string.Equals(built.CommandLine, command, StringComparison.Ordinal))
                {
                    AppLog.Write("GPU export: the command to run is not the one its state makes; FFmpeg runs it as it stands.");
                }
                else
                {
                    // The settings' own chapters list, as it stands now (the GPU command reads the same one); a queued
                    // job reads the copy it was given when it was queued, which nothing rewrites.
                    if (built.UsesChaptersFile && string.Equals(state.ChaptersFilePath, ChaptersFilePath, StringComparison.OrdinalIgnoreCase))
                        WriteChaptersFile(built.UsesCutsFile);
                    if (await GpuExport.TryRunAsync(state, progress, cancellationToken))
                    {
                        CrashReporter.Note("export", "rendered on the GPU compositor");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("GPU export failed; FFmpeg's graph makes the export instead", ex);
                CrashReporter.Note("export", "GPU render failed, FFmpeg's graph used: " + ex.GetType().Name);
                StatusText = "The GPU export failed: FFmpeg renders it instead...";
                ProgressValue = 0;
                IsProgressIndeterminate = true;
            }
        }

        await FfmpegRunner.RunAsync(command, progress, cancellationToken, graphInFile: true);
    }

    /// <summary>
    /// The sequence as the GPU preview engine draws and plays it (5.0): the state gathered once per change (see
    /// <see cref="_liveState"/>), with the soloed tracks applied; the preview's own (<see cref="SequenceExportState.IsPreview"/>:
    /// its video layers read their proxies where they asked for one).
    /// </summary>
    public SequenceExportState LivePreviewState() => Soloed((_liveState ??= CompileExportState()) with { IsPreview = true });

    // The state the GPU engine was last given. It is the same for every scene and every seek until something changes: it
    // is gathered again only then (every change raises LiveFilterInvalidated, which drops it).
    private SequenceExportState? _liveState;

    /// <summary>Render Preview's command: a short, small encode of the given stretches of the timeline.</summary>
    private string BuildPreviewCommand(string outputPath, List<(double Start, double End)> ranges, int percent, string? captionsPath) =>
        FilterGraphBuilder.BuildPreviewCommand(CompileExportState(), outputPath, ranges, percent, captionsPath);

    // ----- Live Preview -----

    /// <summary>
    /// Whether the player shows the picture with the export's filters applied. Always on as HandPeg starts and whenever a
    /// video, a style or a project is opened (the user's call, 4.3E: a style dropped on with it off showed the plain source);
    /// the box beside the playback controls turns it off for what is open.
    /// </summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _livePreview = true;

    /// <summary>Raised whenever a setting changed that may have changed what Live Preview should show.</summary>
    public event Action? LiveFilterInvalidated;

    /// <summary>Something a live graph is built from has changed: the state is gathered afresh for the next one.</summary>
    private void InvalidateLiveFilter()
    {
        _liveState = null;
        LiveFilterInvalidated?.Invoke();
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


    /// <summary>A mix is always encoded: when the tab's default is Copy, AAC stands in.</summary>
    private string MixedAudioCodec => AudioEncoder == CopyOption ? "aac" : AudioEncoder;


    // ----- Video encoder -----

    /// <summary>
    /// The 4:2:0 format a hardware encoder is handed (p010le where 10-bit is ticked and the card can, else yuv420p),
    /// which the export graph also ends in; null for a software or Copy encoder, or when the extra options set
    /// their own -pix_fmt.
    /// </summary>
    private string? HardwarePixelFormat =>
        VideoEncoder.IsHardware && VideoEncoder.Family != EncoderFamily.Copy && !ExtraVideoArguments.Contains("-pix_fmt", StringComparison.Ordinal)
            ? TenBit && CanTenBit ? "p010le" : "yuv420p"
            : null;

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

        var isNvenc = VideoEncoder.Family == EncoderFamily.Nvenc;

        if (!HasRateControl || (isNvenc && NvencTune == NvencLossless))
        {
            // ProRes and DNxHR take their quality from the profile given in the extra options, and a lossless
            // encode has no quality or bitrate to aim for.
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
                EncoderFamily.MediaFoundation => MediaFoundationEncoder.BitrateArguments(bitrate, constant),
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
                // ICQ is asked for by "-global_quality" with no bitrate beside it, and starts at 1.
                EncoderFamily.Qsv => QsvIcq ? $"-global_quality {Math.Max(Crf, 1)}" : $"-q:v {Crf}",
                EncoderFamily.Amf => $"-rc cqp -qp_i {Crf} -qp_p {Crf}",
                EncoderFamily.MediaFoundation => MediaFoundationEncoder.QualityArguments(Crf),
                _ when encoder == "libvpx-vp9" => $"-crf {Crf} -b:v 0",
                _ => $"-crf {Crf}",
            });
        }

        if (isNvenc)
            parts.AddRange(BuildNvencArguments());
        else if (VideoEncoder.Family == EncoderFamily.Amf)
            parts.Add($"-usage {VendorToken(AmfUsage)}");
        else if (VideoEncoder.Family == EncoderFamily.Qsv)
            parts.AddRange(BuildQsvArguments());
        else if (VideoEncoder.Family == EncoderFamily.MediaFoundation)
            parts.Add(MediaFoundationEncoder.HardwareOnlyArgument);

        // Hardware encoders are particular about the pixel format they are handed; 4:2:0 is the one they all take,
        // in 8 bits or, where 10-bit is ticked and the card can, in 10. A software encoder takes what the
        // graph gives it, and is only told a format when it is to encode in 10 bits.
        if (!ExtraVideoArguments.Contains("-pix_fmt", StringComparison.Ordinal))
        {
            if (HardwarePixelFormat is { } format)
                parts.Add($"-pix_fmt {format}");
            else if (TenBit && HasTenBit)
                parts.Add("-pix_fmt yuv420p10le");
        }

        // B-frames stop an NVENC encoder that has none (HEVC before the RTX 20 series), so a "-bf" left in
        // the extra options from another encoder is not passed on to one.
        var extra = ExtraVideoArguments.Trim();
        if (isNvenc && !EncoderProber.Supports(encoder, EncoderProber.BFrames))
            extra = System.Text.RegularExpressions.Regex.Replace(extra, @"(?<!\S)-bf\s+\d+\s*", "").Trim();

        if (extra.Length > 0)
            parts.Add(extra);

        return string.Join(" ", parts);
    }

    /// <summary>NVENC's tuning as set in the Video tab. What the card cannot do (see <see cref="EncoderProber"/>) is left out.</summary>
    private IEnumerable<string> BuildNvencArguments()
    {
        yield return $"-tune {VendorToken(NvencTune)}";
        if (NvencMultipass != NvencSinglePass)
            yield return $"-multipass {VendorToken(NvencMultipass)}";
        if (NvencSpatialAq)
            yield return "-spatial-aq 1";
        if (NvencTemporalAq && CanNvencTemporalAq)
            yield return "-temporal-aq 1";
        if (Lookahead > 0)
            yield return $"-rc-lookahead {Math.Min(Lookahead, LargestNvencLookahead).ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// QuickSync's lookahead as set in the Video tab. Only its H.264 encoder has the "-look_ahead" switch; HEVC
    /// and AV1 take the depth alone.
    /// </summary>
    private IEnumerable<string> BuildQsvArguments()
    {
        if (Lookahead <= 0)
            yield break;

        if (VideoEncoder.Name == "h264_qsv")
            yield return "-look_ahead 1";
        yield return $"-look_ahead_depth {Math.Min(Lookahead, LargestQsvLookahead).ToString(CultureInfo.InvariantCulture)}";
    }

    // The name FFmpeg knows each choice by is the one in brackets: "Low Latency (ll)" is "ll".
    private static string VendorToken(string choice) =>
        choice.LastIndexOf('(') is >= 0 and var open ? choice[(open + 1)..].TrimEnd(')') : choice.ToLowerInvariant();

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// A drawn mask whose picture has gone (the masks folder was emptied, or the project came without it) is
    /// drawn again from the outlines the layer keeps. Only on the UI thread, which is where it can be drawn.
    /// </summary>
    private void EnsureDrawnMasks()
    {
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() != true)
            return;

        foreach (var layer in Layers.Where(l => l.CustomMask && l.MaskShapes.Length > 0 && !FileState.Exists(l.MaskPath)))
        {
            try
            {
                var (width, height) = MaskStore.SizeFor(SourceWidth, SourceHeight);
                if (MaskStore.Render(layer.MaskShapes, width, height) is { } path)
                {
                    FileState.Forget(path);
                    layer.MaskPath = path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"The drawn mask of {layer.Name} could not be drawn again", ex);
            }
        }
    }
}
