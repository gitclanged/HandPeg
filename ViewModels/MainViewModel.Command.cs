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
    /// Gathers everything a filter graph is built from into one snapshot. The sequence comes first: how long
    /// it is, the frame it is composed on and the rate its canvas ticks at, and what is cropped. Those four are
    /// worked out here and passed as numbers, so that the black canvas the builder lays every clip on is as
    /// long, as large and as fast as the timeline says, whatever the main video is doing on it.
    ///
    /// The layers are copied: the snapshot's are its own, so the builder cannot be handed a layer that changes
    /// while it works, nor change one itself.
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

        // One copy of each layer, however many of the lists below it is in.
        var copies = new Dictionary<Layer, Layer>(ReferenceEqualityComparer.Instance);
        Layer Snapshot(Layer layer)
        {
            if (!copies.TryGetValue(layer, out var copy))
            {
                copies[layer] = copy = Layer.FromState(layer.ToState(), SourceWidth, SourceHeight, frameWidth, frameHeight);

                // Keyframes that change nothing are not compiled: see FilterGraphBuilder.FoldConstantKeys.
                FilterGraphBuilder.FoldConstantKeys(copy);
            }

            return copy;
        }

        // A clip that is still being looked at has no size or length yet, and is not rendered.
        var stack = GetStack().Where(l => !l.IsLoading).Select(Snapshot).ToList();
        var sounds = GetLayerAudioSources().Select(Snapshot).ToList();
        var caption = Snapshot(CaptionLayer);

        // Which of the files the layers name are there: the builder leaves out a mask or a LUT that is missing.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Note(string? path)
        {
            var name = (path ?? "").Trim().Trim('"');
            if (name.Length > 0 && File.Exists(name))
                files.Add(name);
        }

        foreach (var layer in stack.Append(caption))
        {
            Note(layer.MaskPath);
            Note(layer.FilterLut);
        }

        Note(CaptionsFilePath);
        var liveCaptions = PreviewSubtitles ? _liveCaptionsPath : "";
        Note(liveCaptions);

        var tracks = GetOutputAudioTracks()
            .Select(t => new ExportAudioTrack(
                t.Index, t.Title, t.Codec, t.Bitrate, t.IsPassthrough, t.Stream.Channels == 1, t.AutoDuck, t.IsVoice,
                ReferenceEquals(t, _silentBase), t.HasProcessing, t.BuildEditChain(), t.GetFilterChain()))
            .ToList();

        return new SequenceExportState
        {
            // The sequence: its length, its frame, its clock, its crop.
            SequenceSeconds = SequenceSeconds,
            OutputDuration = GetOutputDuration(),
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
            SourceFrameRate = SourceFrameRate,
            TargetFramerate = GetTargetFramerate(),
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            OutputWidth = OutputWidth,
            OutputHeight = OutputHeight,
            RequestedSize = GetRequestedSize(),
            OutputSize = GetOutputSize(),
            UseVerticalResolution = UseVerticalResolution,
            SampleAspectRatio = GetSampleAspectRatio(),
            HasCrop = HasCrop,
            CropFractions = GetCropFractions(),

            // The main video on it.
            LocalMediaPath = LocalMediaPath,
            HasSource = HasSource,
            SourceHasNoVideo = _mediaInfo is { Video: null },
            SourceMayHaveAudio = _mediaInfo is not { Audio.Count: 0 },
            MainMediaSeconds = MainMediaSeconds,
            MainClips = [.. GetMainClips()],
            MainShift = MainShift,
            IsMainSpliced = IsMainSpliced,
            IsMainWholeSequence = IsMainWholeSequence,
            CenterRect = GetCenterRect(),

            // What is laid on the canvas.
            FrameEngine = FrameEngine,
            Stack = stack,
            MainLayer = Snapshot(_mainVideoRow),
            BackgroundHidden = _backgroundRow.IsHidden,
            BlurRadius = BlurRadius,
            BlurPasses = BlurPasses,
            BackgroundDim = BackgroundDim,
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

            // Cuts, chapters, subtitles.
            Segments = GetMergedSegments().Select(s => new ExportSegment(s.Start, s.End)).ToList(),
            HasCutSegments = Segments.Count > 0,
            ChapterMarkers = ChapterMarkers,
            ChaptersAtCuts = ChaptersAtCuts,
            CutsFilePath = CutsFilePath,
            ChaptersFilePath = ChaptersFilePath,
            SoftSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.SoftSub).Select(t => t.Index).ToList(),
            BurnedSubtitleStreams = SubtitleTracks.Where(t => t.Action == SubtitleTrack.HardSub).Select(t => t.Index).ToList(),

            // Sound.
            AudioTracks = tracks,
            LayerAudio = sounds,
            AudioLinked = AudioLinked,
            MergeAudioTracks = MergeAudioTracks,
            NormalizeAudio = NormalizeAudio,
            DuckAudio = DuckAudio,
            DuckAmountDb = DuckAmountDb,
            MixedAudioCodec = MixedAudioCodec,
            AudioBitrate = AudioBitrate,
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
            Container = Container,
            WebOptimized = WebOptimized,
            OutputPath = string.IsNullOrWhiteSpace(DestinationPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), $"HandPeg_output.{Container}")
                : DestinationPath.Trim().Trim('"'),
            LiveCaptionsPath = liveCaptions,
            ExistingFiles = files,
        };
    }

    /// <summary>
    /// The export's command line for the settings as they are. When the command reads its cuts or its chapters
    /// from a list beside it, that list is written here: the builder only says that it is needed.
    /// </summary>
    private string BuildFfmpegCommand()
    {
        var command = FilterGraphBuilder.BuildExportCommand(CompileExportState());
        if (command.UsesCutsFile)
            WriteCutsFile(string.IsNullOrWhiteSpace(LocalMediaPath) ? "<source>" : LocalMediaPath, GetMergedSegments());
        if (command.UsesChaptersFile)
            WriteChaptersFile(GetMergedSegments());
        return command.CommandLine;
    }

    /// <summary>
    /// The filter graph Live Preview runs on the picture, for mpv's lavfi-complex; empty when the export would
    /// not filter the picture at all (Copy), which is then what the preview shows too.
    /// </summary>
    /// <param name="surfaceWidth">Width of the player on screen, in screen pixels: the picture is not built larger than it is shown.</param>
    public string BuildLiveFilterGraph(double surfaceWidth, double surfaceHeight) =>
        FilterGraphBuilder.BuildLiveGraph(CompileExportState(), surfaceWidth, surfaceHeight);

    /// <summary>Render Preview's command: a short, small encode of the given stretches of the timeline.</summary>
    private string BuildPreviewCommand(string outputPath, List<(double Start, double End)> ranges, int percent, string? captionsPath) =>
        FilterGraphBuilder.BuildPreviewCommand(CompileExportState(), outputPath, ranges, percent, captionsPath);

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
