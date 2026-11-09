using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// What is known about the loaded source: its tracks, the Properties tab, and the size estimate.
public partial class MainViewModel
{
    // ----- HEVC on the processor (5.2A, the user's Q5: a hint, never a dialog) -----

    // Whether Windows has an HEVC decoder of its own (Media Foundation's: the Store's HEVC Video Extensions), asked once a session off this
    // thread; and whether the hint has been given this session.
    private static Task<bool>? _hevcInWindows;
    private bool _hevcHinted;

    /// <summary>
    /// A video of HEVC opened where Windows has no HEVC decoder: it plays at once, through the compositor's processor decoder (FFmpeg, 5.2-Arm64AndNpu),
    /// and the status bar says so once a session, with what would put its decoding on the graphics card. No dialog, nothing to answer.
    /// </summary>
    private void NoteHevcDecoder(VideoStreamInfo? video)
    {
        if (_hevcHinted || _isBackgroundWorker || video is null || !(video.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) || video.Codec.Equals("h265", StringComparison.OrdinalIgnoreCase)))
            return;

        _hevcHinted = true;
        _ = HintHevcAsync();
    }

    private async Task HintHevcAsync()
    {
        try
        {
            _hevcInWindows ??= Task.Run(() => Compositor.HardwareBenchmark.MediaFoundationDecoders().Contains("HEVC"));
            if (await _hevcInWindows)
                return;

            // After what the opening itself says (its own line comes first, then this one).
            await Task.Delay(1500);
            StatusText = "HEVC plays here through HandPeg's processor decoder, at full speed. Tip: Windows' \"HEVC Video Extensions\" (Microsoft Store) let the graphics card decode it instead.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Asking Windows for an HEVC decoder failed", ex);
        }
    }

    private MediaInfo? _mediaInfo;

    public ObservableCollection<AudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<SubtitleTrack> SubtitleTracks { get; } = [];

    /// <summary>Label/value lines for the two cards of the Properties tab.</summary>
    public ObservableCollection<InfoRow> SourceInfoRows { get; } = [];
    public ObservableCollection<InfoRow> OutputInfoRows { get; } = [];

    [ObservableProperty] private bool _mergeAudioTracks;
    [ObservableProperty] private bool _normalizeAudio;
    [ObservableProperty] private string _extraVideoArguments = "";
    [ObservableProperty] private bool _downloadSubtitles;

    [ObservableProperty] private string _audioTracksHint = "Load a source to list its audio tracks.";
    [ObservableProperty] private string _subtitleTracksHint = "Load a source to list its subtitle tracks.";

    /// <summary>False for stream copy and for editing codecs, which have no quality or bitrate setting.</summary>
    public bool HasRateControl => IsVideoReencoded && VideoEncoder.Family != EncoderFamily.Intermediate;

    // The "All tracks" codec and bitrate (the Audio tab, the Export tab's Audio Export) are pushed to every track that
    // still follows them (4.1.1): a track given a codec or bitrate of its own keeps it, and a dropped track stays dropped;
    // its gain, offset, filters and ducking are never touched here.
    partial void OnAudioEncoderChanged(string value) => ApplyAudioDefaultsToTracks(followersOnly: true);
    partial void OnAudioBitrateChanged(string value) => ApplyAudioDefaultsToTracks(followersOnly: true);

    // The codec and bitrate last pushed to the tracks: a track still on them follows the next change.
    private (string Encoder, string Bitrate)? _trackDefaults;

    /// <summary>
    /// Puts the "All tracks" codec and bitrate on the tracks: on every one (a preset, the default settings, a source
    /// loaded), or, with <paramref name="followersOnly"/>, on those still as the last push left them.
    /// </summary>
    private void ApplyAudioDefaultsToTracks(bool followersOnly = false)
    {
        var before = _trackDefaults;
        foreach (var track in AudioTracks)
        {
            if (!followersOnly || before is not { } last)
            {
                ApplyAudioDefaults(track);
                continue;
            }

            if (track.IsDropped)
                continue;

            var followsCodec = last.Encoder == CopyOption ? track.Action == AudioTrack.Passthrough : track.Action == AudioTrack.Reencode && track.Codec == last.Encoder;
            if (followsCodec)
            {
                if (AudioEncoder == CopyOption)
                    track.Action = AudioTrack.Passthrough;
                else
                    (track.Action, track.Codec) = (AudioTrack.Reencode, AudioEncoder);
            }

            if (track.Bitrate == last.Bitrate)
                track.Bitrate = AudioBitrate;
        }

        _trackDefaults = (AudioEncoder, AudioBitrate);
    }

    private void ApplyAudioDefaults(AudioTrack track)
    {
        if (AudioEncoder == CopyOption)
        {
            track.Action = AudioTrack.Passthrough;
        }
        else
        {
            track.Action = AudioTrack.Reencode;
            track.Codec = AudioEncoder;
        }

        track.Bitrate = AudioBitrate;
        _trackDefaults = (AudioEncoder, AudioBitrate);
    }

    /// <summary>
    /// Puts the default encoding settings (Settings, Export &amp; Hardware) in place: at launch, on + New Project, and after a style
    /// preset when they are set to override styles. Each blank one is left as it is. An encoding preset is never overridden:
    /// with one selected, its settings stay.
    /// </summary>
    private void ApplyEncodingDefaults()
    {
        var settings = AppSettings.Current;
        using var whole = DeferCommand();

        // The defaults are the slowest step (CLAUDE.md 8): a speed-first preset's state ends here (5.4D review), even where no setting
        // below changes the encoder or its step (a blank default, or Media Foundation, whose fastest step is only in the command).
        _speedFirst = false;

        // The encoder first: which preset steps and rate-control names exist depends on it.
        if (settings.DefaultVideoEncoder.Length > 0 && VideoEncoders.FirstOrDefault(e => e.Name == settings.DefaultVideoEncoder) is { } encoder)
            VideoEncoder = encoder;
        if (EncoderPresets.Contains(settings.DefaultEncoderPreset))
            EncoderPreset = settings.DefaultEncoderPreset;
        if (RateControls.Contains(settings.DefaultRateControl))
            RateControl = settings.DefaultRateControl;
        if (settings.DefaultQuality is { } quality)
            Crf = Math.Clamp(quality, 0, 51);
        if (settings.DefaultTargetBitrate.Trim() is { Length: > 0 } bitrate)
            TargetBitrate = bitrate;
        if (Framerates.Contains(settings.DefaultFramerate))
            (Framerate, CustomFramerate) = (settings.DefaultFramerate, settings.DefaultCustomFramerate);
        TenBit = settings.DefaultTenBit && CanTenBit;
        if (Containers.Contains(settings.DefaultContainer))
            Container = settings.DefaultContainer;
        WebOptimized = settings.DefaultWebOptimized;

        // The size asked for, as typed, without one side being worked out from the other on the way.
        if (settings.DefaultOutputWidth.Trim().Length > 0 || settings.DefaultOutputHeight.Trim().Length > 0)
        {
            _syncingDimensions = true;
            try
            {
                (OutputWidth, OutputHeight) = (settings.DefaultOutputWidth.Trim(), settings.DefaultOutputHeight.Trim());
            }
            finally
            {
                _syncingDimensions = false;
            }

            if (OutputWidth.Length == 0 || OutputHeight.Length == 0)
                RelinkDimensions();
        }

        if (AudioEncoders.Contains(settings.DefaultAudioEncoder))
            AudioEncoder = settings.DefaultAudioEncoder;
        if (AudioBitrates.Contains(settings.DefaultAudioBitrate))
            AudioBitrate = settings.DefaultAudioBitrate;
        if (settings.DefaultMergeAudio is "on" or "off")
            MergeAudioTracks = settings.DefaultMergeAudio == "on";
        NormalizeAudio = settings.DefaultNormalizeAudio;
        AudioSampleRate = AudioChannelChoice.NormalizeRate(settings.DefaultAudioSampleRate);
        AudioChannels = AudioChannelChoice.Normalize(settings.DefaultAudioChannels);
        ApplyAudioDefaultsToTracks();
    }

    /// <summary>After a style preset: the default encoding settings again, when they override styles and no encoding preset is in use.</summary>
    private void ApplyEncodingDefaultsOverStyle()
    {
        var settings = AppSettings.Current;
        if (!settings.DefaultsOverrideStyles || SelectedPreset is not null)
            return;

        ApplyEncodingDefaults();

        // The style may have turned the frame tall or wide: the setting that says what every video starts as says otherwise.
        if (UseVerticalResolution != settings.DefaultVerticalVideo)
            UseVerticalResolution = settings.DefaultVerticalVideo;
    }

    /// <summary>
    /// Takes over the result of inspecting a newly opened file (null when it could not be inspected): what the Info, Streams and
    /// Download panels show, and its streams as the output audio tracks' choices (5.2-AuditB: its sound plays through its clips).
    /// </summary>
    private void SetMediaInfo(MediaInfo? info)
    {
        _mediaInfo = info;
        (SourceWidth, SourceHeight, IsSourceHdr) = (info?.Video?.Width ?? 0, info?.Video?.Height ?? 0, info?.Video?.IsHdr ?? false);

        foreach (var track in AudioTracks)
            track.PropertyChanged -= OnTrackChanged;
        foreach (var track in SubtitleTracks)
            track.PropertyChanged -= OnTrackChanged;
        AudioTracks.Clear();
        SubtitleTracks.Clear();

        foreach (var stream in info?.Audio ?? [])
        {
            var track = new AudioTrack(stream)
            {
                WaveformColor = WaveformPalette[AudioTracks.Count % WaveformPalette.Length],
                SourceFileName = OpenedFile.Length > 0 ? System.IO.Path.GetFileName(OpenedFile) : "",
            };
            ApplyAudioDefaults(track);
            track.PropertyChanged += OnTrackChanged;
            AudioTracks.Add(track);
        }

        // A caption preview is of the video it was made for.
        PreviewSubtitles = false;

        foreach (var stream in info?.Subtitles ?? [])
        {
            var track = new SubtitleTrack(stream);
            track.PropertyChanged += OnTrackChanged;
            SubtitleTracks.Add(track);
        }

        AudioTracksHint = info is null ? "The source could not be inspected: one audio track with the settings above is assumed."
            : AudioTracks.Count == 0 ? "This source has no audio."
            : "";
        SubtitleTracksHint = info is null ? "The source could not be inspected."
            : SubtitleTracks.Count == 0 ? "This source has no embedded subtitle tracks."
            : "";

        // Frame-based times depend on the new source's frame rate.
        ApplyDisplaySettings();
        UpdateSourceInfo();
        GenerateCommand();
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A track dropped or taken back changes what the mix sounds like, and so what the timeline should show.
        if (e.PropertyName == nameof(AudioTrack.Action) && sender is AudioTrack && HasSource && !_isBackgroundWorker)
            _ = RefreshTimelineWaveformAsync(LocalMediaPath);

        // Dropped, or taken back, while dropped tracks are hidden: its row goes, or comes back.
        if (e.PropertyName == nameof(AudioTrack.Action) && HideDroppedTracks)
            RefreshAudioRows();

        // A picture arriving is not a change of settings.
        if (e.PropertyName is not (nameof(AudioTrack.Waveform) or nameof(AudioTrack.TrackWaveform)
            or nameof(AudioTrack.Description) or nameof(AudioTrack.SourceFileName) or nameof(AudioTrack.HasOwnFormat)))
            GenerateCommand();
    }

    // ----- Properties tab -----

    private void UpdateSourceInfo()
    {
        SourceInfoRows.Clear();
        if (_mediaInfo is not { } info)
            return;

        SourceInfoRows.Add(new("Container", info.Container));
        SourceInfoRows.Add(new("Original file size", GetSourceFileSize()));
        SourceInfoRows.Add(new("Duration", TimeDisplay.Format(info.DurationSeconds)));
        SourceInfoRows.Add(new("Overall bitrate", info.BitRate > 0 ? $"{info.BitRate / 1000:N0} kb/s" : "Unknown"));

        if (info.Video is { } video)
        {
            SourceInfoRows.Add(new("Dimensions", $"{video.Width} x {video.Height}"));
            SourceInfoRows.Add(new("Display aspect ratio", video.DisplayAspectRatio));
            SourceInfoRows.Add(new("Pixel aspect ratio", video.SampleAspectRatio));
            SourceInfoRows.Add(new("Video codec", video.Codec));
            SourceInfoRows.Add(new("Frame rate", video.FrameRate > 0 ? $"{FormatNumber(video.FrameRate)} fps" : "Unknown"));
            SourceInfoRows.Add(new("Color transfer / primaries",
                $"{OrUnknown(video.ColorTransfer)} / {OrUnknown(video.ColorPrimaries)}{(video.IsHdr ? "  (HDR)" : "")}"));
        }

        SourceInfoRows.Add(new("Audio streams", info.Audio.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var audio in info.Audio)
        {
            var layout = audio.ChannelLayout.Length > 0 ? audio.ChannelLayout : $"{audio.Channels} ch";
            SourceInfoRows.Add(new($"Audio #{audio.Index}", $"{audio.Codec}, {layout}, {audio.SampleRate:N0} Hz"));
        }

        SourceInfoRows.Add(new("Subtitle streams", info.Subtitles.Count.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Refreshes the "Projected Output" card from the current settings.</summary>
    private void UpdateProjectedOutput()
    {
        if (IsAudioOnlyExport)
        {
            UpdateProjectedAudioOnly();
            return;
        }

        var animated = IsAnimatedOutput;
        var copyVideo = !animated && VideoEncoder.Family == EncoderFamily.Copy;
        var size = GetOutputSize();
        var frameRate = GetOutputFrameRate();
        var duration = GetOutputDuration();
        var tracks = animated ? [] : GetOutputAudioTracks();
        var mixAudio = MergeAudioTracks && tracks.Count > 1;

        // The length or the audio may just have changed: bring Target File Size and Target Bitrate back in step (and the automatic
        // bitrate, which follows the size and rate, 5.4B).
        SyncSizeAndBitrate();
        OnPropertyChanged(nameof(EffectiveBitrateKbps));
        OnPropertyChanged(nameof(AutoBitrateText));

        string audio;
        if (tracks.Count == 0)
            audio = "None";
        else if (mixAudio)
            audio = $"{tracks.Count} tracks mixed into one, {MixedAudioCodec}{(AudioTrack.IsLossless(MixedAudioCodec) ? "" : $" {AudioBitrate}")}";
        else
            audio = string.Join("; ", tracks.Select(t => t.IsPassthrough ? $"#{t.Index} copy" : $"#{t.Index} {t.Codec}{(AudioTrack.IsLossless(t.Codec) ? "" : $" {t.Bitrate}")}"));
        if (NormalizeAudio && tracks.Any(t => !t.IsPassthrough || mixAudio))
            audio += $"  (normalized to {NormalizeLoudness(LoudnessTarget)} LUFS)";

        OutputInfoRows.Clear();
        OutputInfoRows.Add(new("Resolution", size is { } s ? $"{s.Width} x {s.Height}" : $"{FrameWidth} x {FrameHeight} (project)"));
        OutputInfoRows.Add(new("Frame rate", frameRate > 0 ? $"{FormatNumber(frameRate)} fps" : MatchProject));
        if (ProjectColorNote is { Length: > 0 } colour)
            OutputInfoRows.Add(new("Colour", colour));
        OutputInfoRows.Add(new("Video codec",
            animated ? (OutputExtension == "gif" ? "Animated GIF" : "Animated WebP")
            : copyVideo && _mediaInfo?.Video is { } video ? $"{video.Codec} (copied)"
            : VideoEncoder.DisplayName));
        OutputInfoRows.Add(new("Audio", audio));
        OutputInfoRows.Add(new("Duration", duration > 0 ? TimeDisplay.Format(duration) : "Unknown"));
        OutputInfoRows.Add(new("Estimated file size", EstimateOutputSize(size, frameRate, duration, tracks, mixAudio)));
    }

    /// <summary>
    /// The Projected Output card for an Audio Only file (5.4C): no picture, one track (every sound mixed, ExportSoundPlan.For), its
    /// codec the file's, normalized as asked, and a size from its bitrate (MP3) or 16-bit PCM's (WAV; FLAC about 60 % of it).
    /// </summary>
    private void UpdateProjectedAudioOnly()
    {
        SyncSizeAndBitrate();
        OnPropertyChanged(nameof(EffectiveBitrateKbps));
        OnPropertyChanged(nameof(AutoBitrateText));
        var extension = OutputExtension;
        var codec = EncoderCommand.AudioOnlyCodec(extension);
        var channels = AudioChannels == AudioChannelChoice.Mono ? 1 : 2;
        var duration = GetOutputDuration();
        var rate = AudioSampleRate > 0 ? AudioSampleRate : Document.SampleRate;
        var kbps = AudioTrack.IsLossless(codec)
            ? rate * 16.0 * channels / 1000 * (codec == "flac" ? 0.6 : 1)
            : double.TryParse(AudioBitrate.TrimEnd('k', 'K'), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed) ? typed : 128;
        var audio = $"Every sound mixed into one, {codec}{(AudioTrack.IsLossless(codec) ? "" : $" {AudioBitrate}")}, {(channels == 1 ? "mono" : "stereo")}"
                    + (NormalizeAudio ? $"  (normalized to {NormalizeLoudness(LoudnessTarget)} LUFS)" : "");

        OutputInfoRows.Clear();
        OutputInfoRows.Add(new("Output", $"Audio only ({extension.ToUpperInvariant()}): no picture is drawn"));
        OutputInfoRows.Add(new("Audio", audio));
        OutputInfoRows.Add(new("Sample rate", string.Create(CultureInfo.InvariantCulture, $"{rate:N0} Hz")));
        OutputInfoRows.Add(new("Duration", duration > 0 ? TimeDisplay.Format(duration) : "Unknown"));
        OutputInfoRows.Add(new("Estimated file size", duration > 0
            ? $"~{FormatSize(kbps * duration / (8 * 1024))} ({(AudioTrack.IsLossless(codec) ? codec == "flac" ? "FLAC: about 60 % of PCM" : "16-bit PCM" : "from the bitrate")})"
            : "Load a source for an estimate"));
    }

    /// <summary>
    /// Projected size of the output. With a bitrate target this is simple arithmetic; for constant
    /// quality it is a bits-per-pixel rule of thumb, and is labelled as an estimate.
    /// </summary>
    private string EstimateOutputSize((int Width, int Height)? size, double frameRate, double duration, List<AudioTrack> tracks, bool mixAudio)
    {
        if (duration <= 0)
            return "Load a source for an estimate";
        if (IsAnimatedOutput)
            return "Depends heavily on the content (GIF and WebP have no bitrate)";

        var audioKbps = GetOutputAudioKbps(tracks, mixAudio);

        double videoKbps;
        string basis;
        if (VideoEncoder.Family == EncoderFamily.Copy)
        {
            // Copied video keeps its bitrate; when the stream does not state one, it is what the audio leaves over.
            var video = _mediaInfo?.Video;
            var sourceAudio = _mediaInfo?.Audio.Sum(a => a.BitRate) ?? 0;
            var bits = video?.BitRate > 0 ? video.BitRate : Math.Max((_mediaInfo?.BitRate ?? 0) - sourceAudio, 0);
            if (bits <= 0)
                return "Unknown (the source does not report a bitrate)";

            videoKbps = bits / 1000.0;
            basis = "Estimate based on source bitrate";
        }
        else if (HasRateControl && IsBitrateMode && EffectiveBitrateKbps is > 0 and var target)
        {
            // The bitrate typed, else the automatic one: what the encoder is given, and the Target File Size box shows.
            // Size (MB) = (video + audio bitrates in kbps) x seconds / (8 x 1024)
            return FormatSize((target + audioKbps) * duration / (8 * 1024));
        }
        else
        {
            if (size is not { } frame || frameRate <= 0)
                return "Load a source for an estimate";

            double bitsPerPixel;
            if (VideoEncoder.Family == EncoderFamily.Intermediate)
            {
                // Proxy-grade editing codecs spend roughly this much whatever the content.
                bitsPerPixel = 0.7;
                basis = "Estimate for an editing codec";
            }
            else
            {
                // Typical bits per pixel at quality 23, halving for every 6 steps up the scale.
                var name = VideoEncoder.Name;
                var atReference = name.Contains("264") ? 0.10
                    : name.Contains("av1") ? 0.05
                    : 0.065;
                if (VideoEncoder.IsHardware)
                    atReference *= 1.4;

                bitsPerPixel = atReference * Math.Pow(2, (23 - Crf) / 6.0);
                basis = "Estimate based on CQ";
            }

            videoKbps = frame.Width * (double)frame.Height * frameRate * bitsPerPixel / 1000;
        }

        return $"~{FormatSize((videoKbps + audioKbps) * duration / (8 * 1024))} ({basis})";
    }

    /// <summary>Combined bitrate of the audio that reaches the output, in kbps.</summary>
    private double GetOutputAudioKbps(List<AudioTrack> tracks, bool mixAudio)
    {
        if (tracks.Count == 0)
            return 0;
        if (mixAudio)
            return GetEncodedAudioKbps(MixedAudioCodec, AudioBitrate, tracks[0].Stream);

        // A copied track keeps its own bitrate; when the stream does not state one, a typical value stands in.
        return tracks.Sum(t => t.IsPassthrough
            ? (t.Stream.BitRate > 0 ? t.Stream.BitRate / 1000.0 : 128)
            : GetEncodedAudioKbps(t.Codec, t.Bitrate, t.Stream));
    }

    /// <summary>Extension the output will have: from Save As when it has one, otherwise the chosen format.</summary>
    private string OutputExtension
    {
        get
        {
            var extension = System.IO.Path.GetExtension(DestinationPath.Trim().Trim('"')).TrimStart('.').ToLowerInvariant();
            return extension.Length > 0 ? extension : Container;
        }
    }

    /// <summary>GIF and WebP: always encoded, never with sound.</summary>
    private bool IsAnimatedOutput => OutputExtension is "gif" or "webp";

    /// <summary>
    /// FLAC, WAV and MP3 (5.4C): an Audio Only export, the Sequence's sound alone. The Export tab folds its video parts away and the
    /// Export Quality Preview is off; raised as the container or the destination changes.
    /// </summary>
    public bool IsAudioOnlyExport => EncoderCommand.IsAudioOnlyExtension(OutputExtension);

    /// <summary>Not <see cref="IsAudioOnlyExport"/>: what the Export tab's video parts are shown by.</summary>
    public bool IsVideoExport => !IsAudioOnlyExport;

    private static double GetEncodedAudioKbps(string codec, string bitrate, AudioStreamInfo stream)
    {
        // Uncompressed 16-bit PCM: sample rate x 16 bits x channels. FLAC (5.4B), lossless: about 60 % of that for most sound.
        if (AudioTrack.IsLossless(codec))
            return (stream.SampleRate > 0 ? stream.SampleRate : 48000) * 16.0 * Math.Max(stream.Channels, 1) / 1000 * (codec == "flac" ? 0.6 : 1);

        return double.TryParse(bitrate.TrimEnd('k', 'K'), NumberStyles.Float, CultureInfo.InvariantCulture, out var kbps) ? kbps : 160;
    }

    /// <summary>
    /// Frame size of the output: the frame the compositor draws (Resolution &amp; Cropping, else the Sequence's own), or for a Copy
    /// export the copied file's own picture (SequenceSpans.CopyCutOf); null when that is not known.
    /// </summary>
    private (int Width, int Height)? GetOutputSize()
    {
        if (IsAnimatedOutput || VideoEncoder.Family != EncoderFamily.Copy)
            return (FrameWidth, FrameHeight);

        if (SequenceSpans.CopyCutOf(Document) is not { } copy)
            return null;
        if (copy.Pieces.Select(p => _views.GetValueOrDefault(p.ClipId)).FirstOrDefault(v => v is { ImageWidth: > 0, ImageHeight: > 0 }) is { } view)
            return (view.ImageWidth, view.ImageHeight);
        return SameFile(copy.Path, OpenedFile) && SourceWidth > 0 && SourceHeight > 0 ? (SourceWidth, SourceHeight) : null;
    }

    private double GetOutputFrameRate()
    {
        if (VideoEncoder.Family != EncoderFamily.Copy
            && GetTargetFramerate() is { } target
            && double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            return fps;
        }

        // "Match Project Settings" (5.4A): the Sequence's own rate; a Copy export keeps its file's.
        return VideoEncoder.Family == EncoderFamily.Copy ? _mediaInfo?.Video?.FrameRate ?? 0 : Document.FrameRate.Fps;
    }

    /// <summary>
    /// Length of the output in seconds: the timeline's to its last picture (4.3C: nothing is cut out of it on the way; 5.4A: an audio
    /// tail past the last picture is not exported, a standalone sound placed past it is: SequenceSpans.ProgramSeconds), or, for a Copy
    /// export of a file's clips (joined end to end from its file: EncoderCommand.BuildCutsList, SequenceSpans.CopyCutOf), those clips end to end.
    /// </summary>
    private double GetOutputDuration() =>
        VideoEncoder.Family == EncoderFamily.Copy && !IsAnimatedOutput && !IsAudioOnlyExport && SequenceSpans.CopyCutOf(Document) is { IsCut: true, Pieces.Count: > 0 } copy
            ? copy.Pieces.Sum(p => p.End - p.Start)
            : SequenceSpans.ProgramSeconds(Document);

    /// <summary>Size of the loaded file, in the same MB/GB form as the estimate it sits beside.</summary>
    private string GetSourceFileSize()
    {
        try
        {
            var megabytes = new System.IO.FileInfo(LocalMediaPath).Length / (1024.0 * 1024.0);
            return megabytes < 1 ? $"{megabytes:0.00} MB" : FormatSize(megabytes);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "Unknown";
        }
    }

    private static string FormatSize(double megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024:0.00} GB" : $"{megabytes:0} MB";

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string OrUnknown(string value) => value.Length > 0 ? value : "unknown";
}
