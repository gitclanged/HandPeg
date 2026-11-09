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

    // The three heights: the Sequence Overview Scrubber's, and the track heights of the timeline's and the Audio tab's rows.
    // Each runs 1 to 50 (the Scrubber's 1 to 52) and is remembered. The Sequence Overview Scrubber's has no slider since 4.2B-2:
    // its lower edge and the border under it (a plain drag; Ctrl-drag shares the rows instead) and its tab's right-click presets
    // set it, a drag keeping to 16 to 120 px (TimelineAreaHeight; MainWindow.ScrubberStep).

    /// <summary>How tall the Sequence Overview Scrubber is, 1 (16 px) to 52.</summary>
    public double MasterTimelineHeight
    {
        get => Math.Clamp(AppSettings.Current.MasterTimelineHeight is > 0 and var set ? set : 4 * AppSettings.Current.TimelineHeight - 1, 1, MostMasterTimeline);
        set => SetHeight(s => s.MasterTimelineHeight, (s, v) => s.MasterTimelineHeight = v, value, nameof(MasterTimelineHeight), nameof(TimelineAreaHeight), MostMasterTimeline);
    }

    /// <summary>The Sequence Overview Scrubber's presets (its tab's menu): 44, 88 and (held to 120 px by MainWindow.ScrubberStep) Tall.</summary>
    public const int CompactMasterTimeline = 8, MediumMasterTimeline = AppSettings.DefaultMasterTimelineHeight, TallMasterTimeline = 37;

    // The Scrubber's tallest setting: 220 px, as before its steps were moved down by two (5.2-AuditB).
    private const int MostMasterTimeline = 52;

    /// <summary>How tall each track's time bar is on the timeline, 1 to 50.</summary>
    public double LayerTrackHeight
    {
        get => Math.Clamp(AppSettings.Current.LayerHeight, 1, 50);
        set => SetHeight(s => s.LayerHeight, (s, v) => s.LayerHeight = v, value, nameof(LayerTrackHeight), nameof(LayerBarHeight));
    }

    /// <summary>How tall each sound's row is on the Audio tab, 1 to 50.</summary>
    public double AudioTrackHeight
    {
        get => Math.Clamp(AppSettings.Current.AudioHeight, 1, 50);
        set => SetHeight(s => s.AudioHeight, (s, v) => s.AudioHeight = v, value, nameof(AudioTrackHeight), nameof(AudioTrackRowHeight));
    }

    private void SetHeight(Func<AppSettings, int> get, Action<AppSettings, int> set, double value, string name, string pixels, int most = 50)
    {
        var steps = (int)Math.Round(Math.Clamp(value, 1, most));
        if (get(AppSettings.Current) == steps)
            return;

        set(AppSettings.Current, steps);
        OnPropertyChanged(name);
        OnPropertyChanged(pixels);
        SaveSettingsSoon();
    }

    private CancellationTokenSource? _settingsSave;

    /// <summary>
    /// Writes the settings to disk once they have stopped changing. A slider reports every step of a drag;
    /// the file is written once, half a second after the last of them.
    /// </summary>
    private async void SaveSettingsSoon()
    {
        if (_isBackgroundWorker)
            return;

        _settingsSave?.Cancel();
        var wait = _settingsSave = new CancellationTokenSource();
        try
        {
            await Task.Delay(500, wait.Token);
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Superseded by a later change, or the file could not be written: the choice holds for this session.
        }
    }

    /// <summary>Height in pixels of the timeline (waveform, I-frame lines, playhead): 16 at its slimmest, 220 at its tallest.</summary>
    public double TimelineAreaHeight => 12 + 4 * MasterTimelineHeight;

    /// <summary>Height in pixels of a track's time bar on the timeline: 26 at the default of 25.</summary>
    // The same reckoning as an audio row's height, so that the two sliders reach the same smallest and largest.
    public double LayerBarHeight => Math.Round(46 + 2.16 * LayerTrackHeight);

    /// <summary>Height in pixels of one sound's row in the Audio tab: 100 at the default of 25.</summary>
    public double AudioTrackRowHeight => ShowWaveforms ? Math.Round(46 + 2.16 * AudioTrackHeight) : 64;
    /// <summary>
    /// Draws the timeline's waveform and one for every audio track, in the background, after a load: all of
    /// them from one reading of the file (see Waveforms.RenderManyAsync).
    /// </summary>
    private async Task GenerateWaveformsAsync(string mediaPath)
    {
        _waveformCancellation?.Cancel();
        _timelineWaveformCancellation?.Cancel();

        var tracks = ShowWaveforms ? AudioTracks.ToList() : [];
        var active = AudioTracks.Where(t => !t.IsDropped).Select(t => t.Index).ToList();
        if (!ShowWaveforms || active.Count == 0)
            TimelineWaveform = null;
        if (tracks.Count == 0)
            return;

        var requests = tracks.Select(t => new WaveformRequest([t.Index], 1200, 120, t.WaveformColor.Replace("#", "0x"))).ToList();
        if (active.Count > 0)
            requests.Add(new WaveformRequest(active, 1920, 150, "gray"));

        var cancellation = _waveformCancellation = new CancellationTokenSource();
        var timeline = _timelineWaveformCancellation = new CancellationTokenSource();
        try
        {
            var pictures = await Waveforms.RenderManyAsync(mediaPath, requests, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            for (var i = 0; i < tracks.Count; i++)
                tracks[i].Waveform = pictures[i];
            if (active.Count > 0 && !timeline.IsCancellationRequested)
                TimelineWaveform = pictures[^1];
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
            if (ReferenceEquals(_timelineWaveformCancellation, timeline))
                _timelineWaveformCancellation = null;
            cancellation.Dispose();
            timeline.Dispose();
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

    // ----- The layers list -----

    /// <summary>
    /// The timeline's picture rows: the video clips, Cutouts and images, and after them the
    /// caption box while auto-captions are on. One list, so every clip is placed and sized the same way.
    /// </summary>
    public ObservableCollection<Layer> LayerRows { get; } = [];

    private void RefreshLayerRows()
    {
        // As they are stacked, front first: the captions over everything, then the layers, and the blur fill's settings
        // row behind it all.
        // A row is a track: its first clip stands for it, and the others are drawn on the same time bar. The lists are
        // only filled again when their rows are other rows (every edit asks): a row re-made loses its selection and a
        // box being dragged in it.
        var rows = new List<Layer>();
        if (AutoCaptions)
            rows.Add(CaptionLayer);
        foreach (var track in Enumerable.Reverse(GetStackTracks()))
            rows.Add(track[0]);
        rows.Add(_backgroundRow);
        if (!rows.SequenceEqual(LayerRows))
        {
            LayerRows.Clear();
            foreach (var row in rows)
                LayerRows.Add(row);
        }

        // A video layer that has sound is a sound on the timeline too: it gets a row of its own on the Audio
        // tab, which is the layer itself seen as its sound. So the sound is where the picture is, as long as
        // the picture is, and goes where the picture goes; there is nothing to keep in step.
        var sounds = Layers.Where(l => l is { IsVideoFile: true, HasAudio: true }).GroupBy(l => l.TrackId).Select(t => t.First())
            .Concat(Layers.Where(l => l.IsAudio).GroupBy(l => l.TrackId).Select(t => t.First())).ToList();
        if (!sounds.SequenceEqual(AudioClipRows))
        {
            AudioClipRows.Clear();
            foreach (var sound in sounds)
                AudioClipRows.Add(sound);
        }
        OnPropertyChanged(nameof(HasAudioClips));
        RefreshAudioRows();
        RefreshAnyKeys();
        OnPropertyChanged(nameof(ActiveKeyLayer));
        OnPropertyChanged(nameof(SelectedTextLayer));
        OnPropertyChanged(nameof(IsTextLayerSelected));
    }

    /// <summary>
    /// The Audio tab's one list: the output audio tracks (the file last opened's streams, 5.2-AuditB: what each output track
    /// is made as), and after them every sound on the timeline, one row per track.
    /// </summary>
    public ObservableCollection<object> AudioRows { get; } = [];

    /// <summary>
    /// Hide Dropped Tracks: the Audio tab lists only the sounds that go into the output. A track of the video
    /// set to Ignore / Drop, and a clip that is muted, are still there; they are just not shown.
    /// </summary>
    [ObservableProperty] private bool _hideDroppedTracks;

    partial void OnHideDroppedTracksChanged(bool value)
    {
        RefreshAudioRows();
        var hidden = AudioTracks.Count + AudioClipRows.Count - AudioRows.Count;
        StatusText = !value ? "Showing every audio track."
            : hidden == 0 ? "Dropped and muted audio tracks are hidden. There are none right now."
            : $"Hiding {hidden} dropped or muted audio track{(hidden == 1 ? "" : "s")}. Press the button again to show {(hidden == 1 ? "it" : "them")}.";
    }

    /// <summary>Whether a sound's row is listed: always, unless dropped sounds are hidden and it is one.</summary>
    private bool IsAudioRowShown(object row) => !HideDroppedTracks || row switch
    {
        AudioTrack track => !track.IsDropped,
        Layer layer => layer.Action != AudioTrack.Drop,
        _ => true,
    };

    private void RefreshAudioRows()
    {
        var rows = AudioTracks.Cast<object>().Concat(AudioClipRows).Where(IsAudioRowShown).ToList();
        if (rows.SequenceEqual(AudioRows))
            return;

        AudioRows.Clear();
        foreach (var row in rows)
            AudioRows.Add(row);
    }

    /// <summary>The sounds added to the timeline from files, one row per track, for the Audio tab.</summary>
    public ObservableCollection<Layer> AudioClipRows { get; } = [];

    public bool HasAudioClips => AudioClipRows.Count > 0;

    /// <summary>Opens the Canvas by itself when the settings ask for that (they do to start with).</summary>
    private void OpenLayoutPaneIfWanted()
    {
        if (!_isBackgroundWorker && AppSettings.Current.AutoOpenLayoutPane && CanEditLayout)
            IsArrangeActive = true;
    }

    // ----- The voice among the tracks, found by its name (4.3E) -----

    /// <summary>
    /// On loading a new file (never a project or a queued job, whose clips keep what was saved): marks the sound of the stream
    /// whose title names a microphone or a voice as the voice (<see cref="VoiceTrackScanner"/>; 5.2-AuditB: its clips on the
    /// timeline, a Sub-Lane's sound, Layer.IsVoice), unless the settings say not to or a sound already is one. It is marked as a
    /// tick would mark it, so Auto-Captions follow it (<see cref="ListenToVoice(Layer)"/>) and the lanes set to Duck make room
    /// for it. Returns what the status line says of it; null when nothing was marked.
    /// </summary>
    private string? MarkVoiceTrack()
    {
        if (!AppSettings.Current.AutoMarkVoiceTrack || Layers.Any(l => l is { IsVoice: true, CarriesSound: true }))
            return null;

        // The titles the file gives its tracks ("Track 2", given to an untitled one, names nothing); an untitled track of an
        // MP4 or MOV recorder by the name it was handled under (a generic one names no voice either).
        if (VoiceTrackScanner.Pick(AudioTracks.Select(t => (t.Index, t.Stream.Title.Length > 0 ? t.Stream.Title : t.Stream.HandlerName))) is not { } index
            || AudioTracks.FirstOrDefault(t => t.Index == index) is not { } voice)
        {
            return null;
        }

        var sounds = Layers.Where(l => l.CarriesSound && ClipOf(l) is { AudioStreamIndex: var stream } clip && stream == index && SameFile(clip.SourcePath, OpenedFile)).ToList();
        if (sounds.Count == 0)
            return null;

        foreach (var sound in sounds)
            sound.IsVoice = true;
        ListenToVoice(sounds[0]);
        var said = $"\"{voice.Title}\" marked as the voice by its name: Auto-Captions listen to it, and sounds set to Duck make room for it (untick Voice on its clip to change that)";
        Log(said);
        return said;
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

        // Where on the timeline the take begins: it is put there when it is finished.
        if (!resuming)
            _recordingStartSeconds = Math.Max(PositionMs / 1000, 0);

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

        // The take being placed is an add under way (5.4D-TabPerf review, P1): the tabs stay as they are from the moment the recording
        // ends until it is on the timeline (or taken as the voiceover), so it lands in the project it was recorded for.
        using var placing = BeginAdd();
        (IsRecording, IsRecordingPaused) = (false, false);
        if (recorded && PlacesVoiceoverOnTimeline)
            await PlaceRecordingAsync();
        else if (recorded)
            await LoadVoiceoverAsync(RecordingPath, "Recorded");
        else if (!VoiceoverStatus.StartsWith("The recording", StringComparison.Ordinal))
            VoiceoverStatus = "Nothing was recorded. Check that the microphone is not in use by another program.";
    }

    // Where the playhead was when the take that is being recorded was begun, in seconds.
    private double _recordingStartSeconds;

    /// <summary>
    /// A voiceover is a clip like any other sound: once recorded it goes straight onto the
    /// timeline, where it is moved, trimmed, split and ducked against as the rest are. With nothing open there is no
    /// timeline to put it on, and the one voiceover is mixed in.
    /// </summary>
    public bool PlacesVoiceoverOnTimeline => HasSource;

    /// <summary>What the Voiceover Studio says about itself, which differs with where the recording goes.</summary>
    public string VoiceoverIntro => PlacesVoiceoverOnTimeline
        ? "Record a voiceover while the video plays in the main window. When you press Stop it goes onto the timeline as an audio clip of its own, starting where the playhead was when you pressed Record. It is marked as a voice, so sounds set to Auto-Duck make room for it; move, trim, split or delete it on the Audio tab like any other sound."
        : "Record a voiceover while the video plays in the main window, or bring one in from a file. It is added to the encode as a second input and mixed into the first audio track, after any cuts.";

    /// <summary>
    /// Makes the take that was just recorded an audio clip on the timeline, at the moment recording began. The
    /// recording is first given a file of its own in the Voiceovers folder: the next take would otherwise
    /// write over it, and a project that is saved has to find it again.
    /// </summary>
    private async Task PlaceRecordingAsync()
    {
        string kept;
        try
        {
            Directory.CreateDirectory(AppPaths.Voiceovers);
            kept = Path.Combine(AppPaths.Voiceovers, $"Voiceover {DateTime.Now:yyyy-MM-dd HH.mm.ss}.wav");
            File.Copy(RecordingPath, kept, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            VoiceoverStatus = $"The recording could not be saved to {AppPaths.Voiceovers}: {ex.Message}";
            return;
        }

        var at = _recordingStartSeconds;
        var name = $"Voiceover {Layers.Count(l => l.IsAudio && l.IsVoice) + 1}";
        var generation = _projectGeneration;
        if (!await AddAudioLayerAsync(kept, at, isVoice: true, name: name))
        {
            // Its project tab went behind all the same (P1): the take is placed in no other project, and kept in its file.
            VoiceoverStatus = generation != _projectGeneration
                ? $"The take was not placed: its project tab went behind first. It is kept as {kept}, to drag onto that project's timeline."
                : "Nothing was recorded. Check that the microphone is not in use by another program.";
            return;
        }

        VoiceoverStatus = $"{name} is on the timeline at {TimeDisplay.Format(at)}, as an audio clip of its own (Audio tab). Press Record for another take; Ctrl+Z takes this one off again.";
        Log($"Added {name} at {TimeDisplay.Format(at)}");
    }

    /// <summary>Takes an existing audio file as the voiceover, instead of recording one: as a clip at the playhead.</summary>
    public async Task ImportVoiceoverAsync(string path)
    {
        if (!PlacesVoiceoverOnTimeline)
        {
            await LoadVoiceoverAsync(path, "Loaded");
            return;
        }

        var at = Math.Max(PositionMs / 1000, 0);
        if (await AddAudioLayerAsync(path, at, isVoice: true))
        {
            VoiceoverStatus = $"{Path.GetFileName(path)} is on the timeline at {TimeDisplay.Format(at)}, as an audio clip marked as a voice.";
            Log($"Added voiceover {Path.GetFileName(path)} at {TimeDisplay.Format(at)}");
        }
        else
        {
            VoiceoverStatus = "That file has no audio that can be read.";
        }
    }

    [RelayCommand]
    private void RemoveVoiceover()
    {
        // A recording is the session's own and goes with it; a file brought in from elsewhere is the user's and stays.
        var recorded = VoiceoverPath.Equals(RecordingPath, StringComparison.OrdinalIgnoreCase);

        (VoiceoverPath, VoiceoverWaveform, VoiceoverDuration) = ("", null, 0);
        (VoiceoverTrimStart, VoiceoverTrimEnd, VoiceoverStartSeconds, CaptionUseVoiceover) = (0, 0, 0, false);
        VoiceoverStatus = "The voiceover was removed.";

        try
        {
            if (recorded)
                File.Delete(RecordingPath);
            File.Delete(Path.Combine(VoiceoverFolder, "voiceover_trimmed.wav"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere; it is in the session folder and goes when the application closes.
        }
    }

    /// <summary>Do-Over: throws away the take being recorded, or the one just made, so the next Record starts afresh.</summary>
    [RelayCommand]
    private async Task DiscardVoiceoverAsync()
    {
        await _recorder.DiscardAsync();
        (IsRecording, IsRecordingPaused) = (false, false);
        RemoveVoiceover();
        VoiceoverStatus = "The take was thrown away. Press Record to start again.";
    }

    // ----- The voiceover against the timeline -----

    /// <summary>Length of the finished video in seconds (see GetOutputDuration).</summary>
    public double OutputDurationSeconds => GetOutputDuration();

    /// <summary>
    /// Where one clip of a file's picture begins after another in the finished video (5.2-AuditB: every shown top-level video
    /// clip, PictureSpans; 4.3C: what 4.2.0's kept segments were), each as a fraction (0 to 1) of its length.
    /// </summary>
    public List<double> GetCutSplitFractions()
    {
        var total = SequenceSeconds;
        var splits = new List<double>();
        if (total <= 0)
            return splits;

        foreach (var span in PictureSpans().Skip(1))
        {
            var fraction = span.Start / total;
            if (span.Start > 0.001 && span.Start < total - 0.001 && (splits.Count == 0 || fraction - splits[^1] > 1e-9))
                splits.Add(fraction);
        }

        return splits;
    }

    /// <summary>The part of the voiceover's waveform that is kept by its trim, for the Master Mix View.</summary>
    [ObservableProperty] private ImageSource? _voiceoverMixWaveform;

    /// <summary>Where the voiceover begins and how long it is in the Master Mix View, as fractions (0 to 1) of the source's length.</summary>
    [ObservableProperty] private double _voiceoverMixStart;

    [ObservableProperty] private double _voiceoverMixWidth;

    /// <summary>
    /// Places the voiceover in the Master Mix View. The tracks there are drawn along the source, while the
    /// voiceover is timed against the finished video; so its start and end are taken back through the cuts
    /// to the moments of the source that are on screen when it starts and ends.
    /// </summary>
    private void UpdateVoiceoverMix()
    {
        var total = SequenceSeconds;
        if (!HasVoiceover || VoiceoverDuration <= 0 || total <= 0 || VoiceoverWaveform is null)
        {
            (VoiceoverMixWaveform, VoiceoverMixStart, VoiceoverMixWidth) = (null, 0, 0);
            return;
        }

        var from = Math.Clamp(VoiceoverTrimStart / VoiceoverDuration, 0, 1);
        var to = Math.Clamp(VoiceoverTrimEnd / VoiceoverDuration, 0, 1);
        if (to - from < 0.001)
            (from, to) = (0, 1);

        // The waveform is a drawing made from its peaks (not a bitmap to cut a strip out of): the part that
        // is kept is drawn again from the peaks between the two ends of the trim.
        var kept = Waveforms.Cut(VoiceoverWaveform, from, to, Math.Max((int)(1200 * (to - from)), 2), 120);
        if (kept is null)
        {
            (VoiceoverMixWaveform, VoiceoverMixStart, VoiceoverMixWidth) = (null, 0, 0);
            return;
        }

        // The finished video is the timeline (4.3C: nothing is cut out of it on the way).
        var start = VoiceoverStartSeconds;
        var end = Math.Max(VoiceoverStartSeconds + (to - from) * VoiceoverDuration, start);
        VoiceoverMixWaveform = kept;
        VoiceoverMixStart = Math.Clamp(start / total, 0, 1);
        VoiceoverMixWidth = Math.Clamp((end - start) / total, 0, 1 - VoiceoverMixStart);
    }

    /// <summary>Takes a wave file as the session's voiceover: measures it, draws it, and opens the trim to its whole length.</summary>
    private async Task LoadVoiceoverAsync(string path, string verb)
    {
        // The voiceover is the project's (5.4D-TabPerf review, P1): an add under way while its file is looked at, and taken by no other tab.
        using var adding = BeginAdd();
        var generation = _projectGeneration;
        var info = File.Exists(path) ? await MediaProbe.ProbeAsync(path, _shutdown.Token) : null;
        if (generation != _projectGeneration)
            return;
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
        var waveform = await Waveforms.RenderAsync(
            path, [0], Path.Combine(WaveformFolder, $"voiceover_{Guid.NewGuid():N}.png"), 1200, 120, "0xEF5350", _shutdown.Token);
        if (generation != _projectGeneration)
            return;
        VoiceoverWaveform = waveform;
        VoiceoverPath = path;
        VoiceoverStatus = string.Create(CultureInfo.InvariantCulture,
            $"{verb} {info.DurationSeconds:0.0} s. Drag the handles to trim it; Starts At moves it along the finished video.");
    }


    // ----- Resolution presets -----

    private const string CustomResolution = "Custom";

    [GeneratedRegex(@"\((\d+)x(\d+)\)")]
    private static partial Regex ResolutionRegex();

    /// <summary>The sizes offered, "Match Project Settings" first (5.4A: blank boxes, the Sequence's own size), then Custom and the common ones.</summary>
    public IReadOnlyList<string> ResolutionPresets { get; } =
        [MatchProject, CustomResolution, "4K (3840x2160)", "1440p (2560x1440)", "1080p (1920x1080)", "720p (1280x720)", "480p (854x480)"];

    /// <summary>The common size the Width and Height boxes currently hold, either way up; Match Project Settings while both are blank; or Custom.</summary>
    [ObservableProperty] private string _resolutionPreset = MatchProject;

    private bool _syncingResolutionPreset;

    partial void OnResolutionPresetChanged(string value)
    {
        if (_syncingResolutionPreset || value is null)
            return;

        // Match Project Settings: both boxes blank, the Sequence's own size.
        if (value == MatchProject)
        {
            SetOutputSize("", "");
            return;
        }

        if (ResolutionRegex().Match(value) is not { Success: true } match)
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

        if (match is null && string.IsNullOrWhiteSpace(OutputWidth) && string.IsNullOrWhiteSpace(OutputHeight))
            match = MatchProject;

        _syncingResolutionPreset = true;
        ResolutionPreset = match ?? CustomResolution;
        _syncingResolutionPreset = false;
    }

    // ----- Preset bar -----

    public bool ShowPresetBarAtTop => AppSettings.Current.PresetBarLocation == AppSettings.PresetBarAtTop;

    /// <summary>The Export Presets selector sits in the Export tab's first card (5.4B; 5.4A's preset bar, before that on the retired Summary tab).</summary>
    public bool ShowPresetBarInExport => !ShowPresetBarAtTop;

    // ----- Notifications -----

    /// <summary>Tells Windows that something long has finished, when the settings ask for that.</summary>
    private void NotifyFinished(string title, string message)
    {
        if (!_isBackgroundWorker && AppSettings.Current.NotifyOnEncodeComplete)
            Notifier.Show(title, message);
    }
}
