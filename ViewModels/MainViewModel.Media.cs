using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// What is known about the loaded source: its tracks, the Properties tab, and the size estimate.
public partial class MainViewModel
{
    private MediaInfo? _mediaInfo;

    public ObservableCollection<AudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<SubtitleTrack> SubtitleTracks { get; } = [];

    /// <summary>Label/value lines for the two cards of the Properties tab.</summary>
    public ObservableCollection<InfoRow> SourceInfoRows { get; } = [];
    public ObservableCollection<InfoRow> OutputInfoRows { get; } = [];

    [ObservableProperty] private bool _mergeAudioTracks;
    [ObservableProperty] private bool _normalizeAudio;
    // On by default: letting the GPU decode speeds up most encodes and previews.
    [ObservableProperty] private bool _hardwareDecoding = true;
    [ObservableProperty] private string _extraVideoArguments = "";
    [ObservableProperty] private bool _downloadSubtitles;

    [ObservableProperty] private string _audioTracksHint = "Load a source to list its audio tracks.";
    [ObservableProperty] private string _subtitleTracksHint = "Load a source to list its subtitle tracks.";

    /// <summary>False for stream copy and for editing codecs, which have no quality or bitrate setting.</summary>
    public bool HasRateControl => IsVideoReencoded && VideoEncoder.Family != EncoderFamily.Intermediate;

    // The "All tracks" codec and bitrate at the top of the Audio tab are pushed to every track.
    partial void OnAudioEncoderChanged(string value) => ApplyAudioDefaultsToTracks();
    partial void OnAudioBitrateChanged(string value) => ApplyAudioDefaultsToTracks();

    private void ApplyAudioDefaultsToTracks()
    {
        foreach (var track in AudioTracks)
            ApplyAudioDefaults(track);
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
    }

    /// <summary>Takes over the result of inspecting a newly loaded source (null when it could not be inspected).</summary>
    private void SetMediaInfo(MediaInfo? info)
    {
        _mediaInfo = info;

        // The main video is a clip like the others, and is called what its file is.
        _mainVideoRow.MediaDuration = Math.Max(info?.DurationSeconds ?? 0, 0);
        _mainVideoRow.Name = HasSource ? System.IO.Path.GetFileName(LocalMediaPath) : "Main Video";
        (SourceWidth, SourceHeight, IsSourceHdr) = (info?.Video?.Width ?? 0, info?.Video?.Height ?? 0, info?.Video?.IsHdr ?? false);

        foreach (var track in AudioTracks)
            track.PropertyChanged -= OnTrackChanged;
        foreach (var track in SubtitleTracks)
            track.PropertyChanged -= OnTrackChanged;
        SoloTrack = null;
        AudioTracks.Clear();
        SubtitleTracks.Clear();

        foreach (var stream in info?.Audio ?? [])
        {
            var track = new AudioTrack(stream)
            {
                WaveformColor = WaveformPalette[AudioTracks.Count % WaveformPalette.Length],
                SourceFileName = HasSource ? System.IO.Path.GetFileName(LocalMediaPath) : "",
            };
            ApplyAudioDefaults(track);
            track.PropertyChanged += OnTrackChanged;
            AudioTracks.Add(track);
        }

        ApplyLegacyDuck();

        // A caption preview is of the video it was made for.
        PreviewSubtitles = false;
        NoteEditorFile(LocalMediaPath);
        RefreshProxies();

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

        // Set back to a copy while it is ducked: a copied track cannot be turned down, so the ducking goes.
        if (e.PropertyName == nameof(AudioTrack.Action) && sender is AudioTrack { AutoDuck: true, IsPassthrough: true } copied)
        {
            copied.AutoDuck = false;
            StatusText = $"{copied.Title} is copied as it is, so Auto-Duck was switched off for it: a ducked track has to be re-encoded.";
        }

        // A sound that is ducked is changed, and sound that is changed cannot be copied.
        if (e.PropertyName == nameof(AudioTrack.AutoDuck) && sender is AudioTrack { AutoDuck: true, IsPassthrough: true } ducked)
        {
            ducked.Action = AudioTrack.Reencode;
            if (AudioEncoder != CopyOption)
                ducked.Codec = AudioEncoder;
        }

        // (The command is what the time bars listen to as well: a slipped or split track redraws through it.)
        // A picture arriving is not a change of settings, and nor is which track the player plays.
        if (e.PropertyName is not (nameof(AudioTrack.Waveform) or nameof(AudioTrack.IsSolo) or nameof(AudioTrack.TrackWaveform)
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
        SourceInfoRows.Add(new("Duration", CutSegment.FormatTime(TimeSpan.FromSeconds(info.DurationSeconds))));
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
        var animated = IsAnimatedOutput;
        var copyVideo = !animated && VideoEncoder.Family == EncoderFamily.Copy;
        var size = GetOutputSize();
        var frameRate = GetOutputFrameRate();
        var duration = GetOutputDuration();
        var tracks = animated ? [] : GetOutputAudioTracks();
        var mixAudio = MergeAudioTracks && tracks.Count > 1;

        // The length or the audio may just have changed: bring Target File Size and Target Bitrate back in step.
        SyncSizeAndBitrate();

        string audio;
        if (tracks.Count == 0)
            audio = "None";
        else if (mixAudio)
            audio = $"{tracks.Count} tracks mixed into one, {MixedAudioCodec}{(MixedAudioCodec.StartsWith("pcm_", StringComparison.Ordinal) ? "" : $" {AudioBitrate}")}";
        else
            audio = string.Join("; ", tracks.Select(t => t.IsPassthrough ? $"#{t.Index} copy" : $"#{t.Index} {t.Codec}{(t.Codec.StartsWith("pcm_", StringComparison.Ordinal) ? "" : $" {t.Bitrate}")}"));
        if (NormalizeAudio && tracks.Any(t => !t.IsPassthrough || mixAudio))
            audio += "  (normalized)";

        OutputInfoRows.Clear();
        OutputInfoRows.Add(new("Resolution", size is { } s ? $"{s.Width} x {s.Height}" : "Same as source"));
        OutputInfoRows.Add(new("Frame rate", frameRate > 0 ? $"{FormatNumber(frameRate)} fps" : "Same as source"));
        OutputInfoRows.Add(new("Video codec",
            animated ? (OutputExtension == "gif" ? "Animated GIF" : "Animated WebP")
            : copyVideo && _mediaInfo?.Video is { } video ? $"{video.Codec} (copied)"
            : VideoEncoder.DisplayName));
        OutputInfoRows.Add(new("Audio", audio));
        OutputInfoRows.Add(new("Duration", duration > 0 ? CutSegment.FormatTime(TimeSpan.FromSeconds(duration)) : "Unknown"));
        OutputInfoRows.Add(new("Estimated file size", EstimateOutputSize(size, frameRate, duration, tracks, mixAudio)));
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
        else if (HasRateControl && IsBitrateMode && int.TryParse(TargetBitrate, out var target) && target > 0)
        {
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

    private static double GetEncodedAudioKbps(string codec, string bitrate, AudioStreamInfo stream)
    {
        // Uncompressed 16-bit PCM: sample rate x 16 bits x channels.
        if (codec.StartsWith("pcm_", StringComparison.Ordinal))
            return (stream.SampleRate > 0 ? stream.SampleRate : 48000) * 16.0 * Math.Max(stream.Channels, 1) / 1000;

        return double.TryParse(bitrate.TrimEnd('k', 'K'), NumberStyles.Float, CultureInfo.InvariantCulture, out var kbps) ? kbps : 160;
    }

    /// <summary>Frame size of the output after cropping and scaling, or null when the source size is not known.</summary>
    private (int Width, int Height)? GetOutputSize()
    {
        var hasWidth = int.TryParse(OutputWidth, out var width) && width > 0;
        var hasHeight = int.TryParse(OutputHeight, out var height) && height > 0;
        var copyVideo = !IsAnimatedOutput && VideoEncoder.Family == EncoderFamily.Copy;
        if (!copyVideo && (FrameEngine || UseVerticalResolution))
            return (FrameWidth, FrameHeight);
        if (!copyVideo && hasWidth && hasHeight)
            return (width, height);

        if (SourceWidth <= 0 || SourceHeight <= 0)
            return null;
        if (copyVideo)
            return (SourceWidth, SourceHeight);

        var (_, _, croppedWidth, croppedHeight) = GetCropRect();
        if (croppedWidth <= 0 || croppedHeight <= 0)
            return null;

        // One dimension given: the other follows the cropped shape, rounded to an even number like scale=-2.
        if (hasWidth)
            return (width, Even((double)width * croppedHeight / croppedWidth));
        if (hasHeight)
            return (Even((double)height * croppedWidth / croppedHeight), height);
        return (croppedWidth, croppedHeight);

        static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
    }

    private double GetOutputFrameRate()
    {
        if (VideoEncoder.Family != EncoderFamily.Copy
            && GetTargetFramerate() is { } target
            && double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            return fps;
        }

        return _mediaInfo?.Video?.FrameRate ?? 0;
    }

    /// <summary>Length of the output in seconds: the kept segments, or the whole source.</summary>
    private double GetOutputDuration()
    {
        var segments = GetMergedSegments();
        if (segments.Count > 0)
            return segments.Sum(s => s.Duration.TotalSeconds);

        return SequenceSeconds;
    }

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
