using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

// VM side of export: GpuExport renders and EncoderCommand builds the command (FFmpeg only encodes and muxes), both from the
// handed state alone. This file compiles that state from current settings and writes the side lists a command reads.
public partial class MainViewModel
{
    // ----- The sequence as the builder is given it -----

    /// <summary>
    /// Compiles the single snapshot that export, GPU preview and audio engine consume. Starts from a document copy
    /// (TimelineForBuilder: tracks, frame size, frame rate) read by TimelineLowering, so the canvas matches the timeline's length,
    /// size and rate; every clip reads its own file.
    /// <para>The document copy and caption box are snapshot-owned, so the builder (which caches by document) never sees mutation.</para>
    /// </summary>
    internal SequenceExportState CompileExportState()
    {
        var (frameWidth, frameHeight) = (FrameWidth, FrameHeight);

        // Layout computes each layer's aspect (needed for centre-anchored resizing); the builder works on copies, so lay out the
        // live layers here.
        foreach (var layer in Layers)
        {
            if (layer.IsUsable)
                layer.GetOutputRect(frameWidth, frameHeight, SourceWidth, SourceHeight);
        }

        // Clips still probing have no size or length and are not rendered.
        var stack = GetStack().Where(l => l is { IsLoading: false, IsEmptyTrack: false }).ToList();
        var caption = Layer.FromState(CaptionLayer.ToState(), SourceWidth, SourceHeight, frameWidth, frameHeight);
        TimelineLowering.FoldConstantKeys(caption);

        // Existing referenced files; the builder skips missing masks and LUTs.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Note(string? path)
        {
            var name = (path ?? "").Trim().Trim('"');
            if (name.Length > 0 && MaskStore.Exists(name))
                files.Add(name);
        }

        // Masks read by alpha (opaque shows, transparent cuts). Drawn masks are outline keys, not files: always present, never alpha-read.
        var alphaMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in stack.Append(caption))
        {
            var mask = MaskStore.SourceOf(layer);
            Note(mask);
            NoteLuts(layer.Effects);
            if (layer.CustomMask && files.Contains(mask) && !MaskStore.IsDrawn(mask) && MaskStore.UsesAlpha(mask))
                alphaMasks.Add(mask);
        }

        Note(CaptionsFilePath);
        NoteLuts(SequenceEffects);

        // Include the blur fill's LUT files too, or no renderer draws them.
        NoteLuts(_backgroundRow.Effects);

        // LUT and Color Grade card LUT files: the render plan only draws files listed here.
        void NoteLuts(IEnumerable<Models.Effects.EffectInstance> cards)
        {
            foreach (var card in cards)
            {
                if (card.Type == Models.Effects.EffectRegistry.Lut)
                    Note(card.GetText("file"));
                else if (card.Type == Models.Effects.EffectRegistry.ColorStudio)
                    Note(card.GetText(Models.Effects.ColorGradeKeys.Lut));
            }
        }

        // Per-file picture start offsets for seeking (MediaStarts) and I-frame indexes so decoders open on a keyframe (SeekIndexes).
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

        // Picture start offsets of active proxies, on proxy time.
        foreach (var proxy in _proxiesInUse.Values)
        {
            if (MediaProbe.KnownVideoStart(proxy) is { } start)
                starts[proxy] = start;
            else
                LearnVideoStart(proxy);
        }

        var timeline = TimelineForBuilder(frameWidth, frameHeight);
        var hdr = IsHdrPassthrough;
        return new SequenceExportState
        {
            // Sequence: tracks, frame size and frame rate.
            Timeline = timeline,
            SourceFrameRate = timeline.FrameRate.IsValid ? timeline.FrameRate.Fps : SourceFrameRate,
            TargetFramerate = GetTargetFramerate(),
            OutputWidth = OutputWidth,
            OutputHeight = OutputHeight,
            RequestedSize = GetRequestedSize(),
            OutputSize = GetOutputSize(),
            SampleAspectRatio = GetSampleAspectRatio(),

            // Source of embedded subtitle streams and chapters (the last opened file).
            StreamSourcePath = LocalMediaPath,

            // Blur fill look and non-clip canvas content.
            BlurRadius = BlurRadius,
            BlurPasses = BlurPasses,
            BackgroundDim = BackgroundDim,
            BackgroundZoom = BackgroundZoom,
            BackgroundEffects = _backgroundRow.EffectsBypassed ? [] : CaptureBackgroundEffects() ?? [],
            CaptionLayer = caption,
            AutoCaptions = AutoCaptions,
            CaptionsFilePath = CaptionsFilePath,

            // Whole-frame filters.
            Deinterlace = Deinterlace,
            Denoise = Denoise,
            // Match Project Settings on an HDR project tone-maps unless HDR passthrough applies (10-bit HEVC/AV1, all sources HDR with
            // the project transfer: IsHdrPassthrough); then frames read back as P010 tagged BT.2020.
            TonemapToSdr = Colorspace == TonemapToSdr || (Colorspace == MatchProject && Document.ColorSpace != Models.Timeline.ProjectColorSpace.Sdr709 && !hdr),
            HdrTransfer = hdr ? HdrTransferOf(Document.ColorSpace) : null,
            LutPath = LutPath,
            Grade = GetSequenceGrade(),
            FadeIn = FadeIn,
            FadeOut = FadeOut,

            // Chapters (clip edges and markers), Copy cuts list, embedded subtitles.
            ChapterMarkers = ChapterMarkers,
            ChaptersAtCuts = ChaptersAtCuts,
            ChaptersAtMarkers = ChaptersAtMarkers && Markers.Count > 0,
            CutsFilePath = CutsFilePath,
            ChaptersFilePath = ChaptersFilePath,
            SoftSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.SoftSub).Select(t => t.Index).ToList(),
            BurnedSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.HardSub).Select(t => t.Index).ToList(),

            // Audio: per output track handling, keyed by stream ordinal.
            AudioOutputs = AudioTracks.Select((t, ordinal) => new AudioOutputChoice(ordinal, t.Title, t.Codec, t.Bitrate, t.IsPassthrough, t.IsDropped)).ToList(),
            MergeAudioTracks = MergeAudioTracks,
            NormalizeAudio = NormalizeAudio,
            LoudnessTargetLufs = NormalizeLoudness(LoudnessTarget),
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

            // FFmpeg executable: bare "ffmpeg" means the bundled copy; a Settings override is spelled out.
            FfmpegExecutable = DependencyUpdater.IsFfmpegOverridden ? Quote(DependencyUpdater.FfmpegPath) : "ffmpeg",
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

            // Preview-only; exports read the originals.
            PreviewPaths = new Dictionary<string, string>(_proxiesInUse, StringComparer.OrdinalIgnoreCase),
        };
    }

    // Files with an in-flight start-offset probe (deduplication).
    private readonly HashSet<string> _learningStarts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Background-probes a video file not yet probed this session (e.g. from an opened project), then rebuilds render state.</summary>
    private void LearnVideoStart(string file)
    {
        if (_isBackgroundWorker || !_learningStarts.Add(file))
            return;

        _ = Task.Run(async () =>
        {
            if (File.Exists(file) && await MediaProbe.ProbeAsync(file, _shutdown.Token) is not null)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => GenerateCommand());
        });
    }

    // Files whose seek tables were pre-read and I-frames requested this session.
    private readonly HashSet<string> _warmedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seek warm-up per video file once on the timeline: pre-reads the start/end (seek tables) and sampled offsets into the OS file
    /// cache, then lists I-frames (disk-cached), so first seeks are as fast as later ones. Files with a known index are only pre-read.
    /// Background, once per file per session; render state rebuilds after a listing.
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
                    {
                        // Refresh the preview and timeline I-frames for the new index.
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            InvalidatePreviewState();
                            RefreshSequence();
                        });
                    }
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
    /// Builds the export command for current settings: the GPU export's encode (FFmpeg reading compositor frames and audio engine
    /// tracks, tokenized until rendered; HandPeg's own for GIF) or a Copy stream copy. Writes any required cuts/chapters lists.
    /// </summary>
    private string BuildFfmpegCommand()
    {
        var state = CompileExportState();
        if (IsCopyExport(state))
        {
            var copy = EncoderCommand.BuildCopy(state);
            if (copy.UsesCutsFile)
                WriteCutsFile(state);
            if (copy.UsesChaptersFile)
                WriteChaptersFile(copy.UsesCutsFile);
            return copy.CommandLine;
        }

        if (GpuExport.CommandFor(state, out var why) is not { } command)
            return $"# This export cannot be made: {why}.";
        if (command.UsesChaptersFile)
            WriteChaptersFile(false);
        return command.CommandLine;
    }

    /// <summary>
    /// Whether this is a Copy (stream copy) export; never for GIF/WebP (always rendered) or Audio Only (no picture).
    /// </summary>
    private static bool IsCopyExport(SequenceExportState state) => state.CopyVideoEncoder && !EncoderCommand.IsPictureOnly(state) && !EncoderCommand.IsAudioOnly(state);

    /// <summary>
    /// Pre-start refusal reason for an impossible Copy export (<see cref="EncoderCommand.WhyCopyCannotCut"/>), shown in the status bar
    /// instead of letting FFmpeg fail. Null otherwise, including for hand-edited commands.
    /// </summary>
    private string? CopyRefusal()
    {
        if (IsCommandManuallyEdited || VideoEncoder.Family != EncoderFamily.Copy)
            return null;

        var state = CompileExportState();
        return IsCopyExport(state) ? EncoderCommand.WhyCopyCannotCut(state.Timeline) : null;
    }

    /// <summary>State the export command was built from (Start Encode and its software retry); null in queue-only background workers.</summary>
    private SequenceExportState? ExportStateForGpu() => _isBackgroundWorker ? null : CompileExportState();

    /// <summary>
    /// Runs an export (RunEncodeAsync incl. software retry; StartQueueAsync per job). Copy exports and hand-edited commands without a
    /// piped picture run in FFmpeg as-is; all others run as GpuExport of <paramref name="state"/> (compositor frames, audio engine,
    /// FFmpeg encode), keeping hand-edited arguments. No fallback renderer: failures throw <see cref="GpuExportException"/>; callers
    /// retry hardware encoder failures in software.
    /// </summary>
    /// <param name="state">Source state (current settings or a queued job's snapshot).</param>
    private async Task RunExportAsync(string command, SequenceExportState? state, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        _exportLeftOut = "";

        // Audio Only (FLAC, WAV, MP3): audio engine renders, FFmpeg encodes, no compositor. Never passed raw to FfmpegRunner (its
        // {sound:1} token is an unrendered input); hand-edited arguments are kept.
        if (state is not null && EncoderCommand.IsAudioOnly(state))
        {
            if (GpuExport.CommandFor(state, out var nothing) is not { } sound)
                throw new GpuExportException($"This export cannot be made: {nothing}.");
            IReadOnlyList<string>? edited = null;
            if (!string.Equals(sound.CommandLine, command, StringComparison.Ordinal))
            {
                var parts = FfmpegRunner.SplitArguments(command);
                if (parts.Count < 2)
                    throw new FfmpegCommandException("The command is empty.");
                edited = parts.Skip(1).ToList();
                AppLog.Write("Export: the command was edited by hand; its own arguments encode the sound.");
            }

            await GpuExport.RunAudioOnlyAsync(state, edited, progress, cancellationToken);
            CrashReporter.Note("export", "audio only, rendered by the sound engine");
            return;
        }

        if (state is null || IsCopyExport(state) || !command.Contains("pipe:0", StringComparison.Ordinal) && GpuExport.CommandFor(state, out _) is not { Encoder: ExportEncoder.Gif })
        {
            await FfmpegRunner.RunAsync(command, progress, cancellationToken);
            return;
        }

        // Current chapters list; queued jobs use the copy captured at queue time.
        if (string.Equals(state.ChaptersFilePath, ChaptersFilePath, StringComparison.OrdinalIgnoreCase))
            WriteChaptersFile(false);

        // Hand-edited commands keep their arguments; frames and audio still come from the state.
        IReadOnlyList<string>? arguments = null;
        if (GpuExport.CommandFor(state, out var why) is not { } built)
            throw new GpuExportException($"This export cannot be made: {why}.");
        if (built.Encoder == ExportEncoder.Ffmpeg && !string.Equals(built.CommandLine, command, StringComparison.Ordinal))
        {
            var parts = FfmpegRunner.SplitArguments(command);
            if (parts.Count < 2)
                throw new FfmpegCommandException("The command is empty.");
            arguments = parts.Skip(1).ToList();
            AppLog.Write("Export: the command was edited by hand; its own arguments encode the GPU's frames.");
        }

        SayWhatIsLeftOut(state);
        await GpuExport.RunAsync(state, arguments, progress, cancellationToken);
        CrashReporter.Note("export", "rendered on the GPU compositor");
    }

    /// <summary>Features the last export omitted (unsupported by the compositor); "" for none.</summary>
    private string _exportLeftOut = "";

    // Unsupported features are reported, never dropped silently.
    private void SayWhatIsLeftOut(SequenceExportState state)
    {
        try
        {
            if (GpuExport.LeftOut(state) is not { Count: > 0 } names)
                return;

            _exportLeftOut = $"Left out of this export (the GPU engine cannot draw it yet): {string.Join(", ", names)}.";
            StatusText = _exportLeftOut;
            AppLog.Write($"Export: left out what the compositor cannot draw: {string.Join(", ", names)}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Could not check what the export leaves out", ex);
        }
    }

    // ----- The preview state -----

    /// <summary>
    /// GPU preview state, compiled once per change (<see cref="_previewState"/>) with <see cref="SequenceExportState.IsPreview"/> (proxies
    /// where requested; soloed tracks from the document). The player always shows it except during Interactive Crop or Draw Target.
    /// </summary>
    public SequenceExportState LivePreviewState() => SdrPreviewOf(_previewState ??= CompileExportState()) with { IsPreview = true };

    /// <summary>
    /// Display variant of a state: HDR-passthrough exports are tone-mapped to 8-bit BT.709 for on-screen use (the export keeps P010 HDR).
    /// </summary>
    internal static SequenceExportState SdrPreviewOf(SequenceExportState state) =>
        state.HdrTransfer is null ? state : state with { TonemapToSdr = true, HdrTransfer = null };

    // Cached preview state, reused across scenes and seeks until PreviewStateInvalidated drops it.
    private SequenceExportState? _previewState;

    /// <summary>
    /// Raised when preview picture/audio or Timeline row inputs change: edits and settings (GenerateCommand), new I-frames, proxy
    /// changes, Subtitle Studio open/close.
    /// </summary>
    public event Action? PreviewStateInvalidated;

    /// <summary>
    /// Invalidates the cached preview state; listeners read the scope from <see cref="LastChange"/> (Timeline rows ignore
    /// <see cref="PreviewChange.Look"/>).
    /// </summary>
    internal void InvalidatePreviewState(PreviewChange change = PreviewChange.Output)
    {
        _previewState = null;
        var outer = _lookWrites.Announcing;
        _lookWrites.Announcing = change;
        try
        {
            PreviewStateInvalidated?.Invoke();
        }
        finally
        {
            _lookWrites.Announcing = outer;
        }
    }


    // ----- Audio -----

    /// <summary>
    /// Projected output audio tracks (Projected Output card, size estimate): kept streams of the last opened file (AudioOutputs), or
    /// one stand-in with the default codec/bitrate for the clips' mixed audio, which the audio engine renders.
    /// </summary>
    private List<AudioTrack> GetOutputAudioTracks()
    {
        var kept = AudioTracks.Where(t => !t.IsDropped).ToList();
        if (kept.Count > 0 || (_mediaInfo is not null && GetLayerAudioSources().Count == 0))
            return kept;

        var standIn = new AudioTrack(new AudioStreamInfo(0, "", 2, "", 48000, 0, "", ""));
        ApplyAudioDefaults(standIn);
        if (_mediaInfo is not null)
            (standIn.Action, standIn.Codec) = (AudioTrack.Reencode, MixedAudioCodec);
        return [standIn];
    }

    /// <summary>Auto-Duck attenuation (dB) applied while the voice is active.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private double _duckAmountDb = -15;


    /// <summary>Mixed audio is always encoded; AAC replaces a Copy default.</summary>
    private string MixedAudioCodec => AudioEncoder == CopyOption ? "aac" : AudioEncoder;


    // ----- Video encoder -----

    /// <summary>
    /// 4:2:0 input format for hardware encoders (p010le when 10-bit is enabled and supported, else yuv420p; NV12 for MF, which rejects
    /// yuv420p: MediaFoundationEncoder.PixelFormat), also the export pipe format. Null for software/Copy or when extra options set -pix_fmt.
    /// </summary>
    private string? HardwarePixelFormat =>
        VideoEncoder.IsHardware && VideoEncoder.Family != EncoderFamily.Copy && !ExtraVideoArguments.Contains("-pix_fmt", StringComparison.Ordinal)
            ? VideoEncoder.Family == EncoderFamily.MediaFoundation ? MediaFoundationEncoder.PixelFormat(TenBit && CanTenBit)
            : TenBit && CanTenBit ? "p010le" : "yuv420p"
            : null;

    private string BuildVideoCodecArguments()
    {
        if (VideoEncoder.Family == EncoderFamily.Copy)
            return "-c:v copy";

        var encoder = VideoEncoder.Name;
        var parts = new List<string> { $"-c:v {encoder}" };

        // Speed/quality step under each encoder family's option name.
        if (HasEncoderPresets && EncoderPresets.Contains(EncoderPreset))
            parts.Add(VideoEncoder.Family == EncoderFamily.Amf ? $"-quality {EncoderPreset}" : $"-preset {EncoderPreset}");

        if (AreSoftwareEncoderOptionsEnabled)
        {
            var isX264 = encoder == "libx264";

            // x265 accepts fewer tune names than x264; omit unknown ones, which would abort the encode.
            if (Tune != NoneOption && (isX264 || Tune != "film"))
                parts.Add($"-tune {Tune}");
            if (Profile != AutoOption && (isX264 || Profile == "main"))
                parts.Add($"-profile:v {Profile}");
            if (Level != AutoOption)
                parts.Add(isX264 ? $"-level {Level}" : $"-x265-params level-idc={Level}");
        }

        var isNvenc = VideoEncoder.Family == EncoderFamily.Nvenc;

        if (VideoEncoder.Family == EncoderFamily.MediaFoundation)
        {
            // MF options from their single builder: rate control, step (-compression_level 100, or 0 when speed-first), hardware-only,
            // and at most one -quality (the CQ level).
            parts.Add(HasRateControl
                ? MediaFoundationEncoder.ExportArguments(IsBitrateMode, RateControl == ConstantBitrate, EffectiveBitrateKbps, Crf, _speedFirst)
                : MediaFoundationEncoder.StepArgument(_speedFirst) + " " + MediaFoundationEncoder.HardwareOnlyArgument);
        }
        else if (!HasRateControl || (isNvenc && NvencTune == NvencLossless))
        {
            // ProRes/DNxHR quality comes from the profile in extra options; lossless has no quality or bitrate target.
        }
        else if (IsBitrateMode && EffectiveBitrateKbps is > 0 and var bitrate)
        {
            // Hardware encoders honour a bitrate only alongside their rate-control mode option.
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
                // Constant QP: measured on Intel, "-global_quality" alone produced identical output at every value (H.264, HEVC,
                // AV1) while "-q:v" is honoured. ICQ is "-global_quality" without a bitrate, minimum 1.
                EncoderFamily.Qsv => QsvIcq ? $"-global_quality {Math.Max(Crf, 1)}" : $"-q:v {Crf}",
                EncoderFamily.Amf => $"-rc cqp -qp_i {Crf} -qp_p {Crf}",
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

        // Hardware encoders need 4:2:0 (8-bit, or 10-bit when enabled and supported); software encoders get an explicit format
        // only for 10-bit.
        if (!ExtraVideoArguments.Contains("-pix_fmt", StringComparison.Ordinal))
        {
            if (HardwarePixelFormat is { } format)
                parts.Add($"-pix_fmt {format}");
            else if (TenBit && HasTenBit)
                parts.Add("-pix_fmt yuv420p10le");
        }

        // Strip "-bf" left from another encoder: NVENC without B-frame support (HEVC before RTX 20) fails on it.
        var extra = ExtraVideoArguments.Trim();
        if (isNvenc && !EncoderProber.Supports(encoder, EncoderProber.BFrames))
            extra = System.Text.RegularExpressions.Regex.Replace(extra, @"(?<!\S)-bf\s+\d+\s*", "").Trim();

        if (extra.Length > 0)
            parts.Add(extra);

        return string.Join(" ", parts);
    }

    /// <summary>NVENC tuning from the Export tab, minus unsupported options (<see cref="EncoderProber"/>).</summary>
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

    /// <summary>QSV lookahead from the Export tab; only H.264 has "-look_ahead", HEVC/AV1 take just the depth.</summary>
    private IEnumerable<string> BuildQsvArguments()
    {
        if (Lookahead <= 0)
            yield break;

        if (VideoEncoder.Name == "h264_qsv")
            yield return "-look_ahead 1";
        yield return $"-look_ahead_depth {Math.Min(Lookahead, LargestQsvLookahead).ToString(CultureInfo.InvariantCulture)}";
    }

    // FFmpeg value is the bracketed token: "Low Latency (ll)" -> "ll".
    private static string VendorToken(string choice) =>
        choice.LastIndexOf('(') is >= 0 and var open ? choice[(open + 1)..].TrimEnd(')') : choice.ToLowerInvariant();

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

}
