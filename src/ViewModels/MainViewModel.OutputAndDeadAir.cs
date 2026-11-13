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

// Creator tools, continued: target size and bitrate, playback helpers (I-frames, frame steps, display settings), dead-air
// removal and the chapters file.
public partial class MainViewModel
{
    // ----- Output -----

    /// <summary>
    /// Target size in MB, tied to the Target Bitrate box: either one can be typed and the other follows.
    /// </summary>
    [ObservableProperty] private string _targetFileSize = "";

    [ObservableProperty] private string _targetSizeHint = "";

    [ObservableProperty] private bool _chaptersAtCuts;

    // Which of the two linked boxes the user typed in last. That one stays put when the length of the
    // output changes; the other is recalculated.
    private bool _sizeDrivesBitrate;
    private bool _syncingSizeAndBitrate;

    // Below this the picture falls apart, so a target size never asks for less.
    private const int LowestVideoKbps = 50;

    partial void OnTargetFileSizeChanged(string value)
    {
        if (_syncingSizeAndBitrate)
            return;

        _sizeDrivesBitrate = !string.IsNullOrWhiteSpace(value);
        if (!_sizeDrivesBitrate)
            return;

        // A size can only be aimed at with a bitrate, so constant quality gives way.
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

    /// <summary>
    /// Keeps Target File Size and Target Bitrate in step:
    /// video kbps = (TargetMB x 8192) / output seconds - audio kbps, and the same sum the other way round.
    /// </summary>
    private void SyncSizeAndBitrate()
    {
        if (_syncingSizeAndBitrate)
            return;

        var duration = GetOutputDuration();
        var hint = "";
        _syncingSizeAndBitrate = true;
        try
        {
            // Constant quality has no bitrate to tie a size to.
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
                // The bitrate typed, or the automatic one (5.4B): the size it gives, as a guide that a size typed takes over from.
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
    /// The speed as its place in <see cref="PlaybackSpeeds"/> (5.4.4, the transport row's speed pill): 0 for 0.25x to 6 for 2x; a speed
    /// not in the list, the nearest one's. Set, it picks the nearest step's speed. <see cref="PlaybackSpeed"/> stays the one that counts
    /// (<see cref="PlaybackRate"/> and the window read it): each follows the other, both ways.
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

            // A value between steps on the speed it already had: the binding is told the step it is on (a new speed told it already).
            if (PlaybackSpeed == before && step != value)
                OnPropertyChanged(nameof(PlaybackSpeedStep));
        }
    }

    partial void OnPlaybackSpeedChanged(string value) => OnPropertyChanged(nameof(PlaybackSpeedStep));

    // A speed's place in the list: its own, else the nearest by rate ("3x": 2x's), else 1x's.
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

    /// <summary>Whether the player is running, for the play/pause button to show the right symbol. Set by the window.</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>
    /// Moves the playhead onto the I-frame before (-1) or after (+1) where it is, using the same I-frame
    /// index that cut points snap to. Returns false when there is no such I-frame.
    /// </summary>
    public bool SeekToIFrame(int direction)
    {
        if (IFrames.Count == 0)
        {
            StatusText = "No I-frame index for this video (yet).";
            return false;
        }

        // A millisecond of slack either way: the playhead sitting on an I-frame counts as being on it,
        // so the jump goes to the one beyond.
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

    /// <summary>Moves the playhead by whole frames: one frame is 1000 / frame rate milliseconds.</summary>
    public void StepFrames(int frames)
    {
        if (DurationMs > 0)
            PositionMs = Math.Clamp(PositionMs + frames * 1000 / SourceFrameRate, 0, DurationMs);
    }

    /// <summary>Where the I-frames fall along the timeline, each as a fraction (0 to 1) of its length.</summary>
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

    /// <summary>Times in frames (HH:MM:SS:FF) or milliseconds (HH:MM:SS.mmm), from the Timeline Settings menu (5.3.2): saved, and shown at once.</summary>
    public void SetTimesAsFrames(bool on)
    {
        SetViewSetting(s => s.ShowTimesAsFrames = on);
        ApplyDisplaySettings();
    }

    /// <summary>Applies the display settings: time format, and which playback controls are shown.</summary>
    private void ApplyDisplaySettings()
    {
        TimeDisplay.UseFrames = AppSettings.Current.ShowTimesAsFrames;
        TimeDisplay.FrameRate = SourceFrameRate;

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

    // ----- Dead air -----

    /// <summary>
    /// Finds the silent stretches of a sound and cuts them out of what plays it: the selected clip with sound of its own (it alone
    /// is cut up, its time left empty, as in 4.2.0); or else (5.2-AuditB: no clip is privileged) the sound marked as the
    /// voice, else the first stream of the video given or at the playhead (<see cref="ListenedSound"/>), and every clip of that
    /// sound's file is cut where the silences it plays are (heard in the file's own time, each clip placing them where it shows
    /// that time). Remove (and the classic way) takes them out and closes up what follows on every unlocked track, as a cut
    /// list's left-out stretches did; Split &amp; Mark folds them out the same way and keeps each at its cut, to be put back
    /// (4.3D). What counts as silence (how quiet, for how long) is in the settings, set from the Remove Dead Air dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task RemoveDeadAirAsync() => RunOperationAsync("Removing dead air", WorkLane.Interactive, async cancellationToken =>
    {
        // How, on what and by what: set by RunDeadAir; left alone, it is the classic way on the listened sound, by its silences.
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

        // A clip with sound of its own: its own sound is listened to, and the clip is what gets cut up (its time left empty, as in 4.2.0).
        if (target is Layer { CarriesSound: true } layer && ClipOf(layer) is { } model)
        {
            var info = await MediaProbe.ProbeAsync(layer.ImagePath, cancellationToken);
            if (info is not { DurationSeconds: > 0, Audio.Count: > 0 })
                return $"{layer.Name} has no sound to listen to.";

            if (await Waveforms.PeaksOfAsync(layer.ImagePath, Math.Max(model.AudioStreamIndex, 0), cancellationToken) is not { } own)
                return $"The sound of {layer.Name} could not be read.";

            return ApplyDeadAir([layer], own.Silences(noiseDb, minimumSeconds, info.DurationSeconds), mark, closeUp: false);
        }

        // The sound marked as the voice (4.3E: a recording's first stream is usually the game, never silent), else the first stream
        // of the video asked for or at the playhead: every clip of its file is cut where the silences it plays are.
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
    /// The sound Remove Dead Air and Auto-Captions listen to when no clip with sound of its own is chosen (5.2-AuditB: where the
    /// main video's Voice track was): a sound clip marked as the voice, else the first audio stream of the video given (a row, a
    /// clip whose streams are on Sub-Lanes), else of the video clip the Crop boxes stand for (the selected one, else the one at
    /// the playhead). Its file and stream; null while there is none.
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

        // Its streams on Sub-Lanes: the first of them.
        var lanes = Document.ChildrenOf(clip).Where(c => c.Clip is { ContentKind: LayerKind.Audio, AudioStreamIndex: >= 0 }).Select(c => c.Clip.AudioStreamIndex).ToList();
        return lanes.Count > 0 ? (path, lanes.Min()) : MediaProbe.KnownInfo(path) is { Audio.Count: > 0 } info ? (path, info.Audio[0].Index) : (path, 0);
    }

    /// <summary>What Remove Dead Air cuts for what is given, in a few words (its dialog's title): a clip with sound of its own, or the clips of the listened sound's file.</summary>
    public string DeadAirTargetName(object? target) => target is Layer { CarriesSound: true } layer ? layer.Name
        : ListenedSound(target) is var (path, _) ? $"the clips of {Path.GetFileName(path)}"
        : "the video";

    /// <summary>The views of the top-level clips that play a file (its pictures, or its sound on a track of its own): what its dead air is cut out of.</summary>
    private List<Layer> ClipsOfFile(string path) =>
        Document.Tracks.SelectMany(t => t.Clips).Where(c => c is { ParentClipId: null, ContentKind: LayerKind.VideoFile or LayerKind.Audio } && SameFile(c.SourcePath, path))
            .Select(ViewOf).OfType<Layer>().ToList();

    /// <summary>
    /// Remove Dead Air by the captions (4.4): the gaps between the words heard in a sound, in its own time, at least the
    /// minimum gap long less a little kept before and after each word, cut out of what plays it exactly as silences are
    /// (ApplyDeadAir). The words are the anchored captions' when they were made from that sound; otherwise it is
    /// transcribed (once: the words are kept) without putting any captions on the timeline.
    /// </summary>
    private async Task<string?> RemoveCaptionGapsAsync(object? target, bool mark, CancellationToken cancellationToken)
    {
        // A clip with sound of its own is cut itself; otherwise every clip of the listened sound's file, by the source asked for
        // or the one Auto-Captions listen to when it is a clip's.
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

        // The gaps between words are many by nature: a clip may take far more of them than of silences.
        return ApplyDeadAir(views, gaps, mark, closeUp, layerLimit: 2000);
    }

    // ----- Chapters at cut points -----

    /// <summary>FFmpeg metadata file naming the chapters: one at each edge of a clip on a video track (Chapters at Cut Points), and the markers.</summary>
    public string ChaptersFilePath => Path.Combine(_workFolder, "chapters.txt");

    /// <param name="throughCuts">The export reads the cuts list (a Copy export of one file's clips end to end): its clock is the list's.</param>
    private void WriteChaptersFile(bool throughCuts = false)
    {
        // Each chapter runs to the start of the next, the last to the end of the output (5.4A: its last picture's, not an audio tail's).
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
            StatusText = $"Could not write {ChaptersFilePath}: {ex.Message}";
        }
    }
}
