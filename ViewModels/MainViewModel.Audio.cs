using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Sound: waveforms, the voiceover and its recording, and playback that skips what was cut.
// Also the resolution presets and the preset bar's place, which arrived in the same pass.
public partial class MainViewModel
{
    // ----- Waveforms -----

    // One colour per track, so that they can be told apart where they are laid over each other.
    private static readonly string[] WaveformPalette = ["#4FC3F7", "#FFB74D", "#81C784", "#F06292", "#BA68C8", "#FFF176"];

    /// <summary>The mix of the audio tracks in use, drawn behind the timeline. Null when there is none (yet), or waveforms are off.</summary>
    [ObservableProperty] private ImageSource? _timelineWaveform;

    private static string WaveformFolder => Path.Combine(SessionPaths.Root, "waves");

    private CancellationTokenSource? _waveformCancellation;
    private CancellationTokenSource? _timelineWaveformCancellation;

    /// <summary>Whether waveforms are drawn at all: the timeline's and the Audio tab's go on and off together.</summary>
    public bool ShowWaveforms => AppSettings.Current.ShowTimelineWaveform;

    /// <summary>Height in pixels of the timeline (waveform, keyframe lines, playhead) for the Timeline Height setting, 1 to 5.</summary>
    public double TimelineAreaHeight => 8 + 16 * Math.Clamp(AppSettings.Current.TimelineHeight, 1, 5);

    /// <summary>Height in pixels of one track's row in the Audio tab for the Audio Track Height setting, 1 to 5.</summary>
    public double AudioTrackRowHeight => ShowWaveforms ? 46 + 18 * Math.Clamp(AppSettings.Current.AudioTrackHeight, 1, 5) : 64;

    /// <summary>Draws the timeline's waveform and one for every audio track, in the background, after a load.</summary>
    private async Task GenerateWaveformsAsync(string mediaPath)
    {
        _waveformCancellation?.Cancel();
        _ = RefreshTimelineWaveformAsync(mediaPath);

        var tracks = AudioTracks.ToList();
        if (tracks.Count == 0 || !ShowWaveforms)
            return;

        var cancellation = _waveformCancellation = new CancellationTokenSource();
        var run = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var track in tracks)
            {
                var image = await Waveforms.RenderAsync(
                    mediaPath, [track.Index], Path.Combine(WaveformFolder, $"track{track.Index}_{run}.png"), 1200, 120,
                    track.WaveformColor.Replace("#", "0x"), cancellation.Token);
                if (cancellation.IsCancellationRequested)
                    return;
                track.Waveform = image;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Waveforms are a picture of the sound, not the sound: everything works without them.
        }
        finally
        {
            if (ReferenceEquals(_waveformCancellation, cancellation))
                _waveformCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Draws the timeline's waveform: every audio track that is not dropped, mixed together, so that the
    /// picture is of what the output will sound like. Run again whenever a track is dropped or taken back.
    /// </summary>
    private async Task RefreshTimelineWaveformAsync(string mediaPath)
    {
        _timelineWaveformCancellation?.Cancel();

        var active = AudioTracks.Where(t => !t.IsDropped).Select(t => t.Index).ToList();
        if (active.Count == 0 || !ShowWaveforms)
        {
            TimelineWaveform = null;
            return;
        }

        var cancellation = _timelineWaveformCancellation = new CancellationTokenSource();
        try
        {
            var image = await Waveforms.RenderAsync(
                mediaPath, active, Path.Combine(WaveformFolder, $"timeline_{Guid.NewGuid():N}.png"), 1920, 150, "gray", cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                TimelineWaveform = image;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
        finally
        {
            if (ReferenceEquals(_timelineWaveformCancellation, cancellation))
                _timelineWaveformCancellation = null;
            cancellation.Dispose();
        }
    }

    // ----- The elements list -----

    /// <summary>
    /// The rows of the Filters tab's elements list: the pieces of video and the images, and after them the
    /// caption box while auto-captions are on. One list, so every layer is placed and sized the same way.
    /// </summary>
    public ObservableCollection<OverlayRegion> ElementRows { get; } = [];

    private void RefreshElementRows()
    {
        ElementRows.Clear();
        foreach (var element in UiElements)
            ElementRows.Add(element);
        if (AutoCaptions)
            ElementRows.Add(CaptionLayer);
    }

    /// <summary>Opens the layout pane by itself when the settings ask for that (Editor Mode does).</summary>
    private void OpenLayoutPaneIfWanted()
    {
        if (!_isBackgroundWorker && AppSettings.Current.AutoOpenLayoutPane && CanEditLayout)
            IsArrangeActive = true;
    }
    // ----- Voiceover -----
    // Recorded (or brought in from a file) in the Voiceover Studio, kept for the session, and mixed into
    // the first audio track of the encode.

    private readonly VoiceRecorder _recorder = new();

    public ObservableCollection<string> Microphones { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordVoiceoverCommand))]
    private string? _selectedMicrophone;

    /// <summary>A stretch is being recorded right now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordVoiceoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseVoiceoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopVoiceoverCommand))]
    private bool _isRecording;

    /// <summary>A recording was paused and can be carried on, or stopped.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordVoiceoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseVoiceoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopVoiceoverCommand))]
    private bool _isRecordingPaused;

    [ObservableProperty] private string _voiceoverStatus = "";

    /// <summary>The session's voiceover, or blank when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVoiceover))]
    [NotifyPropertyChangedFor(nameof(IncludeVoiceover))]
    private string _voiceoverPath = "";

    public bool HasVoiceover => VoiceoverPath.Length > 0;

    [ObservableProperty] private ImageSource? _voiceoverWaveform;

    /// <summary>Length of the voiceover in seconds: the range the trim handles move in.</summary>
    [ObservableProperty] private double _voiceoverDuration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceoverTrimText))]
    private double _voiceoverTrimStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceoverTrimText))]
    private double _voiceoverTrimEnd;

    /// <summary>Louder or quieter than recorded, in decibels.</summary>
    [ObservableProperty] private double _voiceoverGainDb;

    /// <summary>How far into the finished video the voiceover begins, in seconds.</summary>
    [ObservableProperty] private double _voiceoverStartSeconds;

    /// <summary>Whether the voiceover goes into the encode. It also does whenever captions are made from it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IncludeVoiceover))]
    private bool _mixVoiceover = true;

    /// <summary>True when the encode takes the voiceover as a second input and mixes it in.</summary>
    public bool IncludeVoiceover => HasVoiceover && (MixVoiceover || CaptionUseVoiceover);

    public string VoiceoverTrimText =>
        string.Create(CultureInfo.InvariantCulture, $"Keep {VoiceoverTrimStart:0.0} s to {VoiceoverTrimEnd:0.0} s ({Math.Max(VoiceoverTrimEnd - VoiceoverTrimStart, 0):0.0} s)");

    private string VoiceoverFolder => Path.Combine(_workFolder, "voiceover");

    /// <summary>Where a finished recording is written.</summary>
    private string RecordingPath => Path.Combine(VoiceoverFolder, "temp_voiceover.wav");

    [RelayCommand]
    private async Task RefreshMicrophonesAsync()
    {
        VoiceoverStatus = "Looking for microphones...";
        List<string> found;
        try
        {
            found = await VoiceRecorder.ListMicrophonesAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var previous = SelectedMicrophone;
        Microphones.Clear();
        foreach (var name in found)
            Microphones.Add(name);
        SelectedMicrophone = found.Contains(previous ?? "") ? previous : found.FirstOrDefault();

        VoiceoverStatus = found.Count switch
        {
            0 when !File.Exists(DependencyUpdater.FfmpegPath) => "FFmpeg is not installed: install it from Settings to record.",
            0 => "No microphone found.",
            1 => "1 microphone found.",
            _ => $"{found.Count} microphones found.",
        };
    }

    private bool CanRecordVoiceover() => !IsRecording && !string.IsNullOrWhiteSpace(SelectedMicrophone);

    /// <summary>Starts a new recording, or carries a paused one on.</summary>
    [RelayCommand(CanExecute = nameof(CanRecordVoiceover))]
    private async Task RecordVoiceoverAsync()
    {
        var resuming = IsRecordingPaused;
        VoiceoverStatus = "Opening the microphone...";
        try
        {
            await _recorder.StartAsync(SelectedMicrophone!, VoiceoverFolder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            VoiceoverStatus = $"Could not record from {SelectedMicrophone}: {ex.Message}";
            return;
        }

        (IsRecording, IsRecordingPaused) = (true, false);
        VoiceoverStatus = resuming ? $"Recording again from {SelectedMicrophone}..." : $"Recording from {SelectedMicrophone}...";
    }

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private async Task PauseVoiceoverAsync()
    {
        await _recorder.PauseAsync();
        (IsRecording, IsRecordingPaused) = (false, true);
        VoiceoverStatus = "Paused. Record carries on from here; Stop finishes the recording.";
    }

    private bool CanStopVoiceover() => IsRecording || IsRecordingPaused;

    /// <summary>Finishes the recording and takes it as the session's voiceover.</summary>
    [RelayCommand(CanExecute = nameof(CanStopVoiceover))]
    private async Task StopVoiceoverAsync()
    {
        VoiceoverStatus = "Finishing the recording...";
        bool recorded;
        try
        {
            recorded = await _recorder.StopAsync(RecordingPath, _shutdown.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            recorded = false;
            VoiceoverStatus = $"The recording could not be finished: {ex.Message}";
        }

        (IsRecording, IsRecordingPaused) = (false, false);
        if (recorded)
            await LoadVoiceoverAsync(RecordingPath, "Recorded");
        else if (!VoiceoverStatus.StartsWith("The recording", StringComparison.Ordinal))
            VoiceoverStatus = "Nothing was recorded. Check that the microphone is not in use by another program.";
    }

    /// <summary>Takes an existing audio file as the voiceover, instead of recording one.</summary>
    public Task ImportVoiceoverAsync(string path) => LoadVoiceoverAsync(path, "Loaded");

    [RelayCommand]
    private void RemoveVoiceover()
    {
        (VoiceoverPath, VoiceoverWaveform, VoiceoverDuration) = ("", null, 0);
        (VoiceoverTrimStart, VoiceoverTrimEnd, VoiceoverStartSeconds, CaptionUseVoiceover) = (0, 0, 0, false);
        VoiceoverStatus = "The voiceover was removed.";
    }
    /// <summary>Takes a wave file as the session's voiceover: measures it, draws it, and opens the trim to its whole length.</summary>
    private async Task LoadVoiceoverAsync(string path, string verb)
    {
        var info = File.Exists(path) ? await MediaProbe.ProbeAsync(path, _shutdown.Token) : null;
        if (info is not { DurationSeconds: > 0, Audio.Count: > 0 })
        {
            VoiceoverStatus = File.Exists(path) ? "That file has no audio that can be read." : "Nothing was recorded. Check that the microphone is not in use by another program.";
            return;
        }

        // Cleared first, so that everything bound to the voiceover sees a change even when the file is the
        // same one as before with a new recording in it.
        VoiceoverPath = "";
        VoiceoverDuration = info.DurationSeconds;
        (VoiceoverTrimStart, VoiceoverTrimEnd) = (0, info.DurationSeconds);
        VoiceoverWaveform = await Waveforms.RenderAsync(
            path, [0], Path.Combine(WaveformFolder, $"voiceover_{Guid.NewGuid():N}.png"), 1200, 120, "0xEF5350", _shutdown.Token);
        VoiceoverPath = path;
        VoiceoverStatus = string.Create(CultureInfo.InvariantCulture,
            $"{verb} {info.DurationSeconds:0.0} s. Drag the handles to trim it; Starts At moves it along the finished video.");
    }

    /// <summary>
    /// The voiceover as it is to be used: the recording itself, or a copy cut to the trim handles when they
    /// have been moved. Null when there is no voiceover.
    /// </summary>
    public async Task<string?> GetTrimmedVoiceoverAsync(CancellationToken cancellationToken)
    {
        if (!HasVoiceover || !File.Exists(VoiceoverPath))
            return null;

        var (start, end) = (Math.Max(VoiceoverTrimStart, 0), Math.Min(VoiceoverTrimEnd, VoiceoverDuration));
        if (start < 0.05 && end > VoiceoverDuration - 0.05 || end - start < 0.1)
            return VoiceoverPath;

        var trimmed = Path.Combine(Path.GetDirectoryName(VoiceoverPath)!, "voiceover_trimmed.wav");
        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? Quote(DependencyUpdater.FfmpegPath) : "ffmpeg";
        await FfmpegRunner.RunAsync(
            $"{ffmpeg} -hide_banner -y -ss {Number(start)} -to {Number(end)} -i {Quote(VoiceoverPath)} -c:a pcm_s16le {Quote(trimmed)}",
            new Progress<FfmpegProgress>(), cancellationToken);
        return trimmed;
    }

    // ----- Smart playback -----

    /// <summary>While on, playback jumps over everything outside the cut segments, so what plays is what will be kept.</summary>
    [ObservableProperty] private bool _playOnlySegments;

    /// <summary>
    /// Where playback should jump to from a position that lies outside every segment: the start of the next
    /// segment, in milliseconds. Null when the position is inside a segment, or smart playback does not apply.
    /// </summary>
    /// <param name="isPastLast">True when there is no segment after the position: playback has nothing left to show.</param>
    public double? GetSegmentSkipTarget(double positionMs, out bool isPastLast)
    {
        isPastLast = false;
        if (!PlayOnlySegments || Segments.Count == 0)
            return null;

        var ordered = GetMergedSegments();
        if (ordered.Any(s => positionMs >= s.Start.TotalMilliseconds && positionMs < s.End.TotalMilliseconds))
            return null;

        var next = ordered.FirstOrDefault(s => s.Start.TotalMilliseconds > positionMs);
        if (next is null)
        {
            isPastLast = true;
            return null;
        }

        return next.Start.TotalMilliseconds;
    }

    // ----- Resolution presets -----

    private const string CustomResolution = "Custom";

    [GeneratedRegex(@"\((\d+)x(\d+)\)")]
    private static partial Regex ResolutionRegex();

    public IReadOnlyList<string> ResolutionPresets { get; } =
        [CustomResolution, "4K (3840x2160)", "1440p (2560x1440)", "1080p (1920x1080)", "720p (1280x720)", "480p (854x480)"];

    /// <summary>The common size the Width and Height boxes currently hold, either way up, or Custom.</summary>
    [ObservableProperty] private string _resolutionPreset = CustomResolution;

    private bool _syncingResolutionPreset;

    partial void OnResolutionPresetChanged(string value)
    {
        if (_syncingResolutionPreset || value is null || ResolutionRegex().Match(value) is not { Success: true } match)
            return;

        // The presets are named the wide way round; with Use Vertical Resolution they go in on their side.
        var (width, height) = (match.Groups[1].Value, match.Groups[2].Value);
        if (UseVerticalResolution)
            (width, height) = (height, width);
        SetOutputSize(width, height);
    }

    /// <summary>Shows in the drop-down which preset the size boxes amount to, without that counting as choosing it.</summary>
    private void SyncResolutionPreset()
    {
        var match = ResolutionPresets.FirstOrDefault(p =>
            ResolutionRegex().Match(p) is { Success: true } m
            && ((m.Groups[1].Value == OutputWidth && m.Groups[2].Value == OutputHeight)
                || (m.Groups[1].Value == OutputHeight && m.Groups[2].Value == OutputWidth)));

        _syncingResolutionPreset = true;
        ResolutionPreset = match ?? CustomResolution;
        _syncingResolutionPreset = false;
    }

    // ----- Preset bar -----

    public bool ShowPresetBarAtTop => AppSettings.Current.PresetBarLocation == AppSettings.PresetBarAtTop;

    public bool ShowPresetBarInSummary => !ShowPresetBarAtTop;

    // ----- Notifications -----

    /// <summary>Tells Windows that something long has finished, when the settings ask for that.</summary>
    private void NotifyFinished(string title, string message)
    {
        if (!_isBackgroundWorker && AppSettings.Current.NotifyOnEncodeComplete)
            Notifier.Show(title, message);
    }
}
