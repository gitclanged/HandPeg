using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

// The view model's side of an export. The export itself is GpuExport's and the command EncoderCommand's (5.2-Audit: FFmpeg
// only encodes and muxes), which read nothing but the state they are handed; what is here gathers that state from the
// settings as they are now, and does what they do not: write the lists a command reads beside it.
public partial class MainViewModel
{
    // ----- The sequence as the builder is given it -----

    /// <summary>
    /// Gathers everything an export, the GPU preview and the sound engine are made from into one snapshot. The sequence comes
    /// first: a copy of the timeline document (TimelineForBuilder: its tracks of clips, the frame it is composed on and the rate
    /// its canvas ticks at), which the lowering reads (TimelineLowering), so that the black canvas every clip is laid on is as
    /// long, as large and as fast as the timeline says. No clip and no file is privileged (5.2-AuditB): each reads its own file.
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
        foreach (var layer in Layers)
        {
            if (layer.IsUsable)
                layer.GetOutputRect(frameWidth, frameHeight, SourceWidth, SourceHeight);
        }

        // A clip that is still being looked at has no size or length yet, and is not rendered.
        var stack = GetStack().Where(l => l is { IsLoading: false, IsEmptyTrack: false }).ToList();
        var caption = Layer.FromState(CaptionLayer.ToState(), SourceWidth, SourceHeight, frameWidth, frameHeight);
        TimelineLowering.FoldConstantKeys(caption);

        // Which of the files the layers name are there: the builder leaves out a mask or a LUT that is missing.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Note(string? path)
        {
            var name = (path ?? "").Trim().Trim('"');
            if (name.Length > 0 && MaskStore.Exists(name))
                files.Add(name);
        }

        // And which of the masks are read by their transparency: painted shows, see-through is cut away. A drawn mask is its
        // outlines, a key that is no file (5.1-Release): there, and never read by its transparency.
        var alphaMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in stack.Append(caption))
        {
            var mask = layer.MaskSource;
            Note(mask);
            foreach (var lut in layer.Effects.Where(e => e.Type == Models.Effects.EffectRegistry.Lut))
                Note(lut.GetText("file"));
            if (layer.CustomMask && files.Contains(mask) && !MaskStore.IsDrawn(mask) && MaskStore.UsesAlpha(mask))
                alphaMasks.Add(mask);
        }

        Note(CaptionsFilePath);
        foreach (var lut in SequenceEffects.Where(e => e.Type == Models.Effects.EffectRegistry.Lut))
            Note(lut.GetText("file"));

        // The blur fill's own cards (5.0-Release): its LUT files too, or neither renderer draws them.
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

        // And where the picture of each proxy in use starts, on the proxy's own clock.
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
            // The sequence: its tracks of clips, its frame, its clock (the Sequence's own rate).
            Timeline = timeline,
            SourceFrameRate = timeline.FrameRate.IsValid ? timeline.FrameRate.Fps : SourceFrameRate,
            TargetFramerate = GetTargetFramerate(),
            OutputWidth = OutputWidth,
            OutputHeight = OutputHeight,
            RequestedSize = GetRequestedSize(),
            OutputSize = GetOutputSize(),
            SampleAspectRatio = GetSampleAspectRatio(),

            // The file whose own subtitle streams and chapters the Subtitles and Chapters settings name (the file last opened).
            StreamSourcePath = LocalMediaPath,

            // The blur fill's look (every clip's that has one), and what is laid on the canvas besides the clips.
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
            // 5.4A: "Match Project Settings" on an HDR project tone-maps too, unless (5.4B) the export keeps the HDR: 10-bit HEVC or AV1,
            // every picture HDR of the project's transfer (IsHdrPassthrough). Then the frames are read back as P010 and tagged BT.2020.
            TonemapToSdr = Colorspace == TonemapToSdr || (Colorspace == MatchProject && Document.ColorSpace != Models.Timeline.ProjectColorSpace.Sdr709 && !hdr),
            HdrTransfer = hdr ? HdrTransferOf(Document.ColorSpace) : null,
            LutPath = LutPath,
            Grade = GetSequenceGrade(),
            FadeIn = FadeIn,
            FadeOut = FadeOut,

            // Chapters (at the clips' edges, and at the markers), the Copy export's cuts list, the file's own subtitles.
            ChapterMarkers = ChapterMarkers,
            ChaptersAtCuts = ChaptersAtCuts,
            ChaptersAtMarkers = ChaptersAtMarkers && Markers.Count > 0,
            CutsFilePath = CutsFilePath,
            ChaptersFilePath = ChaptersFilePath,
            SoftSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.SoftSub).Select(t => t.Index).ToList(),
            BurnedSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.HardSub).Select(t => t.Index).ToList(),

            // Sound: what each output audio track is made as, by its stream place (Ordinal: its place among the file's streams).
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

            // The command around the graph. A bare "ffmpeg" means the local copy; one chosen in the settings is spelled out.
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

            // Read by the preview only: an export reads the originals.
            PreviewPaths = new Dictionary<string, string>(_proxiesInUse, StringComparer.OrdinalIgnoreCase),
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
                    {
                        // The preview, and the I-frames along the timeline, which its clips show now.
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
    /// The export's command line for the settings as they are (5.2-Audit): the encode the GPU export runs (FFmpeg reading the
    /// compositor's frames and the sound engine's tracks, their renders named by tokens until they are made; HandPeg's own for a
    /// GIF), or a Copy export's stream copy. A cuts or chapters list it reads is written here: the builder only says it is needed.
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
    /// A Copy export: the file's own picture copied, which no frame is drawn for (GIF and WebP are always drawn; an Audio Only file,
    /// 5.4C, has no picture, so the Copy encoder left selected never makes it a stream copy).
    /// </summary>
    private static bool IsCopyExport(SequenceExportState state) => state.CopyVideoEncoder && !EncoderCommand.IsPictureOnly(state) && !EncoderCommand.IsAudioOnly(state);

    /// <summary>
    /// Why the export as it is set now is refused before it starts: a Copy export that cannot cut (5.2-Arm64AndNpu, the user's call:
    /// <see cref="EncoderCommand.WhyCopyCannotCut"/>, said in the status bar, nothing started, rather than warned of and handed to
    /// FFmpeg to fail). Null for any other export, for one that can be made, and for a command edited by hand, which is the user's own.
    /// </summary>
    private string? CopyRefusal()
    {
        if (IsCommandManuallyEdited || VideoEncoder.Family != EncoderFamily.Copy)
            return null;

        var state = CompileExportState();
        return IsCopyExport(state) ? EncoderCommand.WhyCopyCannotCut(state.Timeline) : null;
    }

    /// <summary>The state the export's command is made from (Start Encode and its software retry); null in a background worker, which only queues.</summary>
    private SequenceExportState? ExportStateForGpu() => _isBackgroundWorker ? null : CompileExportState();

    /// <summary>
    /// Runs an export's command (Start Encode and its software retry, RunEncodeAsync; each queued job, StartQueueAsync), 5.2-Audit:
    /// a Copy export, and a hand-edited command that reads no piped picture, are FFmpeg's to run as they stand; every other export is
    /// the GPU export of <paramref name="state"/> (GpuExport: the compositor draws the frames, the sound engine renders the sound,
    /// FFmpeg encodes), with the command's own arguments when it was edited by hand. There is no other renderer to fall back to: an
    /// export that cannot be made says why (<see cref="GpuExportException"/>); a hardware encoder that fails is the caller's to retry
    /// in software.
    /// </summary>
    /// <param name="state">The state the command was made from (the settings' now, or a queued job's snapshot).</param>
    private async Task RunExportAsync(string command, SequenceExportState? state, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        _exportLeftOut = "";

        // An Audio Only file (5.4C: FLAC, WAV, MP3): the engine renders the sound, FFmpeg encodes it, no compositor. Never handed to
        // FfmpegRunner as it stands (its {sound:1} token is a render still to be made); a command edited by hand keeps its arguments.
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

        // The settings' own chapters list, as it stands now; a queued job reads the copy it was given when it was queued.
        if (string.Equals(state.ChaptersFilePath, ChaptersFilePath, StringComparison.OrdinalIgnoreCase))
            WriteChaptersFile(false);

        // A command edited by hand keeps its own arguments; the frames and the sound are still the state's.
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

    /// <summary>What the last export left out (5.2-Audit: what the compositor cannot draw yet); "" for nothing.</summary>
    private string _exportLeftOut = "";

    // What the compositor cannot draw is not in the export: never left out silently.
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
    /// The sequence as the GPU preview engine draws and plays it (5.0): the state gathered once per change (see
    /// <see cref="_previewState"/>); the preview's own (<see cref="SequenceExportState.IsPreview"/>: its video layers read their
    /// proxies where they asked for one; the soloed tracks are the document's, which the sound engine hears alone). The player
    /// always shows it (5.3-Stage2, the user's call: the Live Preview switch is gone), save while Interactive Crop or a Draw
    /// Target shows a clip's own file.
    /// </summary>
    public SequenceExportState LivePreviewState() => SdrPreviewOf(_previewState ??= CompileExportState()) with { IsPreview = true };

    /// <summary>
    /// A state as a screen shows it: an export that keeps the project's HDR (5.4B) is tone-mapped for the player and anything else
    /// drawn in 8-bit BT.709 (the export itself reads the frames back as P010 and keeps them HDR).
    /// </summary>
    internal static SequenceExportState SdrPreviewOf(SequenceExportState state) =>
        state.HdrTransfer is null ? state : state with { TonemapToSdr = true, HdrTransfer = null };

    // The state the GPU engine was last given. It is the same for every scene and every seek until something changes: it
    // is gathered again only then (every change raises PreviewStateInvalidated, which drops it).
    private SequenceExportState? _previewState;

    /// <summary>
    /// Raised whenever something the player's picture or sound, or the Timeline's rows, are made from changed: after every
    /// edit and setting change (GenerateCommand), I-frames learnt, the proxies in use, the Subtitle Studio opened or closed.
    /// </summary>
    public event Action? PreviewStateInvalidated;

    /// <summary>Something the preview is made from has changed: the state is gathered afresh for the next scene plan.</summary>
    private void InvalidatePreviewState()
    {
        _previewState = null;
        PreviewStateInvalidated?.Invoke();
    }


    // ----- Audio -----

    /// <summary>
    /// The audio tracks the output is said to have (the Projected Output card, the size estimate), in order: the file last opened's
    /// streams that are kept (what each output track is made as, AudioOutputs). With none, a single stand-in with the tab's default
    /// codec and bitrate for the sound the Sequence's clips carry: the sound engine makes an output of its own for it.
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

    /// <summary>How far a sound set to Auto-Duck is turned down while a voice speaks, in decibels.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private double _duckAmountDb = -15;


    /// <summary>A mix is always encoded: when the tab's default is Copy, AAC stands in.</summary>
    private string MixedAudioCodec => AudioEncoder == CopyOption ? "aac" : AudioEncoder;


    // ----- Video encoder -----

    /// <summary>
    /// The 4:2:0 format a hardware encoder is handed (p010le where 10-bit is ticked and the card can, else yuv420p; NV12 for
    /// a Media Foundation one, which refuses yuv420p: MediaFoundationEncoder.PixelFormat), which the export graph also ends
    /// in; null for a software or Copy encoder, or when the extra options set their own -pix_fmt.
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

        if (VideoEncoder.Family == EncoderFamily.MediaFoundation)
        {
            // Media Foundation's whole option string from the one place that builds it (5.4C): its rate control, its slowest step
            // (-compression_level 100; 5.4D: its fastest, 0, while a speed-first preset holds) and hardware only; one -quality at most, the CQ level.
            parts.Add(HasRateControl
                ? MediaFoundationEncoder.ExportArguments(IsBitrateMode, RateControl == ConstantBitrate, EffectiveBitrateKbps, Crf, _speedFirst)
                : MediaFoundationEncoder.StepArgument(_speedFirst) + " " + MediaFoundationEncoder.HardwareOnlyArgument);
        }
        else if (!HasRateControl || (isNvenc && NvencTune == NvencLossless))
        {
            // ProRes and DNxHR take their quality from the profile given in the extra options, and a lossless
            // encode has no quality or bitrate to aim for.
        }
        else if (IsBitrateMode && EffectiveBitrateKbps is > 0 and var bitrate)
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
                // ICQ is asked for by "-global_quality" with no bitrate beside it, and starts at 1.
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

    /// <summary>NVENC's tuning as set in the Export tab. What the card cannot do (see <see cref="EncoderProber"/>) is left out.</summary>
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
    /// QuickSync's lookahead as set in the Export tab. Only its H.264 encoder has the "-look_ahead" switch; HEVC
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

}
