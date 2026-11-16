using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Target size/bitrate linkage, playback helpers (I-frames, frame steps, display settings), dead-air removal and the chapters file.
public partial class MainViewModel
{
    // ----- Output -----

    /// <summary>Target size in MB, bidirectionally linked to Target Bitrate.</summary>
    [ObservableProperty] private string _targetFileSize = "";

    [ObservableProperty] private string _targetSizeHint = "";

    [ObservableProperty] private bool _chaptersAtCuts;

    // Last-typed linked box; it stays fixed when the output duration changes and the other is recomputed.
    private bool _sizeDrivesBitrate;
    private bool _syncingSizeAndBitrate;

    // Minimum video bitrate a target size may imply (quality floor).
    private const int LowestVideoKbps = 50;

    partial void OnTargetFileSizeChanged(string value)
    {
        if (_syncingSizeAndBitrate)
            return;

        _sizeDrivesBitrate = !string.IsNullOrWhiteSpace(value);
        if (!_sizeDrivesBitrate)
            return;

        // Size targeting requires a bitrate mode; leave constant quality.
        if (RateControl == ConstantQuality)
            RateControl = ConstantBitrate;
        SyncSizeAndBitrate();
    }

    partial void OnTargetBitrateChanged(string value)
    {
        if (_syncingSizeAndBitrate)
            return;

        _sizeDrivesBitrate = false;
        SyncSizeAndBitrate();
    }

    /// <summary>Syncs Target File Size and Target Bitrate: video kbps = (TargetMB x 8192) / seconds - audio kbps (and inverse).</summary>
    private void SyncSizeAndBitrate()
    {
        if (_syncingSizeAndBitrate)
            return;

        var duration = GetOutputDuration();
        var hint = "";
        _syncingSizeAndBitrate = true;
        try
        {
            // Constant quality has no bitrate to derive a size from.
            if (!IsBitrateMode)
            {
                _sizeDrivesBitrate = false;
                TargetFileSize = "";
                return;
            }

            if (duration <= 0 || !HasRateControl || IsAnimatedOutput)
            {
                hint = string.IsNullOrWhiteSpace(TargetFileSize) ? ""
                    : duration <= 0 ? "Load a video to work out the bitrate."
                    : "Not used with this encoder or format.";
                return;
            }

            var tracks = GetOutputAudioTracks();
            var audioKbps = GetOutputAudioKbps(tracks, MergeAudioTracks && tracks.Count > 1);

            if (_sizeDrivesBitrate)
            {
                if (!double.TryParse(TargetFileSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var megabytes) || megabytes <= 0)
                    return;

                var kbps = megabytes * 8192 / duration - audioKbps;
                if (kbps < LowestVideoKbps)
                    hint = $"Too small for this length: the video is held at {LowestVideoKbps} kbps, so the file will be larger.";
                TargetBitrate = ((int)Math.Max(kbps, LowestVideoKbps)).ToString(CultureInfo.InvariantCulture);
            }
            else if (EffectiveBitrateKbps is > 0 and var bitrate)
            {
                // Show the size implied by the typed or automatic bitrate as a guide; a typed size overrides it.
                TargetFileSize = ((bitrate + audioKbps) * duration / 8192).ToString("0.#", CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _syncingSizeAndBitrate = false;
            TargetSizeHint = hint;
        }
    }

    // ----- Playback helpers -----

    public IReadOnlyList<string> PlaybackSpeeds { get; } = ["0.25x", "0.5x", "0.75x", "1x", "1.25x", "1.5x", "2x"];

    [ObservableProperty] private string _playbackSpeed = "1x";

    public float PlaybackRate =>
        float.TryParse(PlaybackSpeed.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 1;

    /// <summary>
    /// Speed pill index into <see cref="PlaybackSpeeds"/> (0 = 0.25x .. 6 = 2x; off-list speeds map to nearest). Setting selects that
    /// step's speed. <see cref="PlaybackSpeed"/> remains authoritative (read by <see cref="PlaybackRate"/> and the window); both sync.
    /// </summary>
    public double PlaybackSpeedStep
    {
        get => StepOfSpeed(PlaybackSpeed);
        set
        {
            if (double.IsNaN(value))
                return;

            var step = (int)Math.Round(Math.Max(0, Math.Min(value, PlaybackSpeeds.Count - 1)), MidpointRounding.AwayFromZero);
            var before = PlaybackSpeed;
            PlaybackSpeed = PlaybackSpeeds[step];

            // Snap an in-between binding value back to the current step (a speed change already raised it).
            if (PlaybackSpeed == before && step != value)
                OnPropertyChanged(nameof(PlaybackSpeedStep));
        }
    }

    partial void OnPlaybackSpeedChanged(string value) => OnPropertyChanged(nameof(PlaybackSpeedStep));

    // Index of a speed: exact match, else nearest by rate ("3x" -> 2x), else 1x.
    private int StepOfSpeed(string? speed)
    {
        for (var i = 0; i < PlaybackSpeeds.Count; i++)
        {
            if (string.Equals(PlaybackSpeeds[i], speed, StringComparison.Ordinal))
                return i;
        }

        static double? RateOf(string? text) =>
            double.TryParse(text?.Trim().TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : null;

        var (nearest, gap) = (-1, double.MaxValue);
        if (RateOf(speed) is { } wanted)
        {
            for (var i = 0; i < PlaybackSpeeds.Count; i++)
            {
                if (RateOf(PlaybackSpeeds[i]) is { } rate && Math.Abs(rate - wanted) < gap)
                    (nearest, gap) = (i, Math.Abs(rate - wanted));
            }
        }

        return nearest >= 0 ? nearest : speed == "1x" ? 0 : StepOfSpeed("1x");
    }

    /// <summary>The full transport row under the player: I-frame jumps, frame steps, play/pause, speed.</summary>
    public bool ShowAdvancedPlayback => AppSettings.Current.ShowAdvancedPlayback;

    /// <summary>Just the play button beside the timeline.</summary>
    public bool ShowSimplePlayback => !ShowAdvancedPlayback;

    /// <summary>Whether the player is playing (drives the play/pause glyph); set by the window.</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>Jumps to the previous (-1) or next (+1) I-frame from the cut-snap index; false when none.</summary>
    public bool SeekToIFrame(int direction)
    {
        if (IFrames.Count == 0)
        {
            StatusText = "No I-frame index for this video (yet).";
            return false;
        }

        // 1 ms tolerance: a playhead on an I-frame counts as on it, so the jump goes to the next one.
        var seconds = PositionMs / 1000;
        var index = direction < 0
            ? IFrames.FindLastIndex(k => k < seconds - 0.001)
            : IFrames.FindIndex(k => k > seconds + 0.001);
        if (index < 0)
        {
            StatusText = direction < 0 ? "Already at the first I-frame." : "Already past the last I-frame.";
            return false;
        }

        PositionMs = IFrames[index] * 1000;
        StatusText = $"I-frame {index + 1} of {IFrames.Count} at {TimeDisplay.Format(IFrames[index])}";
        return true;
    }

    /// <summary>Steps the playhead by whole frames (1000 / fps ms each).</summary>
    public void StepFrames(int frames)
    {
        if (DurationMs <= 0)
            return;

        var rate = Document.FrameRate;
        var seconds = rate.IsValid
            ? rate.SecondsFromFrame(Math.Max(0, rate.FrameFromSeconds(PositionMs / 1000) + frames))
            : PositionMs / 1000 + frames / SequenceFps;
        PositionMs = Math.Clamp(seconds * 1000, 0, DurationMs);
    }

    /// <summary>I-frame positions as fractions (0..1) of the timeline length.</summary>
    public ObservableCollection<double> IFrameMarks { get; } = [];

    private void UpdateIFrameMarks()
    {
        IFrameMarks.Clear();

        var duration = SequenceSeconds;
        if (duration <= 0 || IFrames.Count == 0)
            return;

        var step = Math.Max(1, (int)Math.Ceiling(IFrames.Count / (double)MaxIFrameMarks));
        for (var i = 0; i < IFrames.Count; i += step)
            IFrameMarks.Add(Math.Clamp(IFrames[i] / duration, 0, 1));
    }

    /// <summary>The I-frame nearest a time, in milliseconds, or null without an I-frame index.</summary>
    public double? GetNearestIFrameMs(double positionMs)
    {
        var seconds = positionMs / 1000;
        var floor = GetIFrameFloor(seconds, 0);
        var ceiling = GetIFrameCeiling(seconds, 0);
        var nearest = floor is null ? ceiling
            : ceiling is null ? floor
            : seconds - floor.Value <= ceiling.Value - seconds ? floor : ceiling;
        return nearest * 1000;
    }

    /// <summary>Timecode format, frames (HH:MM:SS:FF) or ms (HH:MM:SS.mmm), from the Timeline Settings menu; persisted and applied immediately.</summary>
    public void SetTimesAsFrames(bool on)
    {
        SetViewSetting(s => s.ShowTimesAsFrames = on);
        ApplyDisplaySettings();
    }

    /// <summary>Applies the display settings: time format, and which playback controls are shown.</summary>
    private void ApplyDisplaySettings()
    {
        // Background VMs (export queue) must not change the global time format.
        if (_isBackgroundWorker)
            return;

        TimeDisplay.UseFrames = AppSettings.Current.ShowTimesAsFrames;
        ApplySequenceRate();
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ShowAdvancedPlayback));
        OnPropertyChanged(nameof(ShowSimplePlayback));
        OnPropertyChanged(nameof(ShowCommandPreviewTab));
        OnPropertyChanged(nameof(ShowAdvancedFiltersTab));
        OnPropertyChanged(nameof(IsMediaBrowserVisible));
        OnPropertyChanged(nameof(ShowTimelineOptionsBelow));
        OnPropertyChanged(nameof(ShowPresetBarAtTop));
        OnPropertyChanged(nameof(ShowPresetBarInExport));
        OnPropertyChanged(nameof(TimelineAreaHeight));
        OnPropertyChanged(nameof(AudioTrackRowHeight));
        OnPropertyChanged(nameof(ShowWaveforms));
        OnPropertyChanged(nameof(ShowTimelineThumbnails));
        OnPropertyChanged(nameof(CaptionHint));
    }

    /// <summary>Sets timecode readouts to the Sequence rate (after display settings and on possible rate changes).</summary>
    private void ApplySequenceRate()
    {
        if (_isBackgroundWorker || TimeDisplay.FrameRate == SequenceFps)
            return;

        TimeDisplay.FrameRate = SequenceFps;
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
    }

    // ----- Dead air -----

    /// <summary>
    /// Remove Dead Air by silence: a selected clip with its own audio is cut alone (gaps left); otherwise the voice-marked audio, else
    /// the first stream of the given/playhead video (<see cref="ListenedSound"/>), with every clip of that file cut at silences mapped
    /// from file time. Remove ripples across unlocked tracks; Split &amp; Mark folds (restorable). Thresholds come from Settings
    /// (Remove Dead Air dialog).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task RemoveDeadAirAsync() => RunOperationAsync("Removing dead air", WorkLane.Interactive, loadsDocument: true, operation: async cancellationToken =>
    {
        // Mode, target and detection source from RunDeadAir; defaults to classic removal on the listened sound by silence.
        var (mode, target, by) = (_deadAirMode, _deadAirTarget, _deadAirSource);
        (_deadAirMode, _deadAirTarget, _deadAirSource) = (DeadAirMode.Classic, null, DeadAirSource.Audio);

        if (!HasSource)
            return "Load a video first.";

        IsProgressIndeterminate = true;
        var mark = mode == DeadAirMode.SplitAndMark;
        if (by == DeadAirSource.Captions)
            return await RemoveCaptionGapsAsync(target, mark, cancellationToken);

        StatusText = "Listening for silence...";
        var noiseDb = Math.Clamp(AppSettings.Current.DeadAirThresholdDb, -80, -10);
        var minimumSeconds = Math.Clamp(AppSettings.Current.DeadAirMinSeconds, 0.2, 30);

        // Clip with its own audio: analyse and cut that clip only, leaving gaps.
        if (target is Layer { CarriesSound: true } layer && ClipOf(layer) is { } model)
        {
            var info = await MediaProbe.ProbeAsync(layer.ImagePath, cancellationToken);
            if (info is not { DurationSeconds: > 0, Audio.Count: > 0 })
                return $"{layer.Name} has no sound to listen to.";

            if (await Waveforms.PeaksOfAsync(layer.ImagePath, Math.Max(model.AudioStreamIndex, 0), cancellationToken) is not { } own)
                return $"The sound of {layer.Name} could not be read.";

            return ApplyDeadAir([layer], own.Silences(noiseDb, minimumSeconds, info.DurationSeconds), mark, closeUp: false);
        }

        // Voice-marked audio (a recording's first stream is usually never-silent game audio), else the first stream of the given/playhead
        // video; cut every clip of that file at its silences.
        if (ListenedSound(target) is not var (path, stream))
            return "There is no sound to listen to: select a clip with sound, or put a video on the timeline.";

        var heard = await MediaProbe.ProbeAsync(path, cancellationToken);
        if (heard is not { Audio.Count: > 0 })
            return $"{System.IO.Path.GetFileName(path)} has no audio to analyse.";
        if (await Waveforms.PeaksOfAsync(path, stream, cancellationToken) is not { } peaks)
            return $"The sound of {System.IO.Path.GetFileName(path)} could not be read.";

        var silences = peaks.Silences(noiseDb, minimumSeconds, Math.Max(heard.DurationSeconds, 0)).Where(s => s.End - s.Start > 0.05).ToList();
        if (silences.Count == 0)
            return string.Create(CultureInfo.InvariantCulture, $"Nothing quieter than {noiseDb:0} dB for {minimumSeconds:0.#} s was found: {System.IO.Path.GetFileName(path)} was left as it is.");

        var views = ClipsOfFile(path);
        return views.Count == 0 ? $"{System.IO.Path.GetFileName(path)} is not on the timeline." : ApplyDeadAir(views, silences, mark, closeUp: true);
    });

    /// <summary>
    /// Default analysed audio for Remove Dead Air and Auto-Captions: a voice-marked clip, else the first audio stream of the given video
    /// (row, or clip with Sub-Lane streams), else of CropTarget (selected, else playhead). Returns file and stream; null when none.
    /// </summary>
    private (string Path, int Stream)? ListenedSound(object? given = null)
    {
        var voice = Layers.FirstOrDefault(l => l is { IsVoice: true, CarriesSound: true, IsHidden: false } && ClipOf(l) is not null);
        if (voice is not null)
            return (voice.ImagePath.Trim().Trim('"'), Math.Max(ClipOf(voice)!.AudioStreamIndex, 0));

        if ((given is Layer { IsVideoFile: true } chosen ? chosen : CropTarget) is not { IsVideoFile: true } video || ClipOf(video) is not { SourcePath: { Length: > 0 } source } clip)
            return null;

        var path = source.Trim().Trim('"');
        if (clip.AudioStreamIndex >= 0)
            return (path, clip.AudioStreamIndex);

        // Streams on Sub-Lanes: take the first.
        var lanes = Document.ChildrenOf(clip).Where(c => c.Clip is { ContentKind: LayerKind.Audio, AudioStreamIndex: >= 0 }).Select(c => c.Clip.AudioStreamIndex).ToList();
        return lanes.Count > 0 ? (path, lanes.Min()) : MediaProbe.KnownInfo(path) is { Audio.Count: > 0 } info ? (path, info.Audio[0].Index) : (path, 0);
    }

    /// <summary>Short Remove Dead Air target description (dialog title): the clip, or the listened file's clips.</summary>
    public string DeadAirTargetName(object? target) => target is Layer { CarriesSound: true } layer ? layer.Name
        : ListenedSound(target) is var (path, _) ? $"the clips of {Path.GetFileName(path)}"
        : "the video";

    /// <summary>Top-level clip views playing a file (picture, or audio on its own track): the dead-air cut targets.</summary>
    private List<Layer> ClipsOfFile(string path) =>
        Document.Tracks.SelectMany(t => t.Clips).Where(c => c is { ParentClipId: null, ContentKind: LayerKind.VideoFile or LayerKind.Audio } && SameFile(c.SourcePath, path))
            .Select(ViewOf).OfType<Layer>().ToList();

    /// <summary>
    /// Remove Dead Air by caption gaps: word gaps (source time) at least the minimum length, minus per-word padding, cut like silences
    /// (ApplyDeadAir). Uses anchored captions from that source, else transcribes once (cached) without adding captions.
    /// </summary>
    private async Task<string?> RemoveCaptionGapsAsync(object? target, bool mark, CancellationToken cancellationToken)
    {
        // Own-audio clip: cut itself; otherwise all clips of the listened file (requested source, or the Auto-Captions clip source).
        IReadOnlyList<Layer> views;
        bool closeUp;
        CaptionSource source;
        if (target is Layer { CarriesSound: true } layer && ClipOf(layer) is { } clip)
        {
            views = [layer];
            closeUp = false;
            source = new CaptionSource(CaptionSourceKind.Clip, layer.ImagePath, Math.Max(clip.AudioStreamIndex, 0));
        }
        else
        {
            var listened = ListenedSound(target);
            if (target is not Layer && ResolveCaptionSource() is { Kind: CaptionSourceKind.Clip, Path.Length: > 0 } resolved)
                listened = (resolved.Path, resolved.Stream);
            if (listened is not var (path, stream))
                return "There is no sound to listen to: select a clip with sound, or put a video on the timeline.";

            views = ClipsOfFile(path);
            closeUp = true;
            if (views.Count == 0)
                return $"{System.IO.Path.GetFileName(path)} is not on the timeline.";
            source = new CaptionSource(CaptionSourceKind.Clip, path, Math.Max(stream, 0));
        }

        var words = AnchoredCaptionWords(source);
        if (words is null)
        {
            StatusText = "Listening for the words...";
            (words, var problem) = await GetCaptionWordsAsync(source, cancellationToken);
            if (words is null)
                return problem;
        }

        var settings = AppSettings.Current;
        var minimumGap = Math.Clamp(settings.DeadAirCaptionMinGapSeconds, 0.1, 3);
        var gaps = CaptionProjection.WordGaps(words, minimumGap, Math.Clamp(settings.DeadAirCaptionPadBeforeSeconds, 0, 0.5),
            Math.Clamp(settings.DeadAirCaptionPadAfterSeconds, 0, 0.5), CaptionSourceSeconds(source));
        if (gaps.Count == 0)
        {
            return words.Count == 0 ? "No words were heard, so there are no gaps between them to cut."
                : string.Create(CultureInfo.InvariantCulture, $"No gap of {minimumGap:0.##} s or more was found between the words: nothing was cut.");
        }

        // Word gaps are far more numerous than silences; raise the per-clip cut cap.
        return ApplyDeadAir(views, gaps, mark, closeUp, layerLimit: 2000);
    }

    // ----- Chapters at cut points -----

    /// <summary>FFmpeg chapters metadata: video-track clip edges (Chapters at Cut Points) and markers.</summary>
    public string ChaptersFilePath => Path.Combine(_workFolder, "chapters.txt");

    /// <param name="throughCuts">Copy export via the cuts list: chapter times use the list's concatenated clock.</param>
    private void WriteChaptersFile(bool throughCuts = false)
    {
        // Each chapter ends at the next; the last ends at the output end (last picture, not an audio tail).
        var lines = new List<string> { ";FFMETADATA1" };
        var chapters = BuildChapters(throughCuts);
        var total = throughCuts ? GetOutputDuration() : SequenceSpans.ProgramSeconds(Document);
        for (var i = 0; i < chapters.Count; i++)
        {
            var end = i + 1 < chapters.Count ? chapters[i + 1].Start : Math.Max(total, chapters[i].Start + 0.001);
            lines.Add("[CHAPTER]");
            lines.Add("TIMEBASE=1/1000");
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"START={(long)Math.Round(chapters[i].Start * 1000)}"));
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"END={(long)Math.Round(end * 1000)}"));
            lines.Add($"title={EscapeMetadata(chapters[i].Title)}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChaptersFilePath)!);
            File.WriteAllLines(ChaptersFilePath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Could not write {ChaptersFilePath}: {ex.Message}", $"Write the chapters file {ChaptersFilePath}", ex);
        }
    }
}
