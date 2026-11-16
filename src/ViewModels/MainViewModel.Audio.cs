using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegApp.ViewModels;

// Audio: waveforms, row heights, Audio tab rows, voice detection, voiceover recording; plus resolution presets.
public partial class MainViewModel
{
    // ----- Waveforms -----

    // Distinct colour per track so overlaid waveforms stay distinguishable.
    private static readonly string[] WaveformPalette = ["#4FC3F7", "#FFB74D", "#81C784", "#F06292", "#BA68C8", "#FFF176"];

    /// <summary>Mixed waveform of active audio tracks drawn behind the timeline; null when absent or waveforms are off.</summary>
    [ObservableProperty] private ImageSource? _timelineWaveform;

    private static string WaveformFolder => Path.Combine(SessionPaths.Root, "waves");

    private CancellationTokenSource? _waveformCancellation;
    private CancellationTokenSource? _timelineWaveformCancellation;

    /// <summary>Global waveform toggle for the timeline and the Audio tab.</summary>
    public bool ShowWaveforms => AppSettings.Current.ShowTimelineWaveform;

    // Persisted heights: Overview Scrubber (1..52), timeline tracks and Audio tab rows (1..50). The scrubber has no slider: its
    // lower edge (plain drag; Ctrl-drag redistributes) and tab presets set it, drags clamped to 16..120 px (TimelineAreaHeight,
    // MainWindow.ScrubberStep).

    /// <summary>Overview Scrubber height step, 1 (16 px) to 52.</summary>
    public double MasterTimelineHeight
    {
        get => Math.Clamp(AppSettings.Current.MasterTimelineHeight is > 0 and var set ? set : 4 * AppSettings.Current.TimelineHeight - 1, 1, MostMasterTimeline);
        set => SetHeight(s => s.MasterTimelineHeight, (s, v) => s.MasterTimelineHeight = v, value, nameof(MasterTimelineHeight), nameof(TimelineAreaHeight), MostMasterTimeline);
    }

    /// <summary>Overview Scrubber tab-menu presets: 44, 88 and Tall (clamped to 120 px by MainWindow.ScrubberStep).</summary>
    public const int CompactMasterTimeline = 8, MediumMasterTimeline = AppSettings.DefaultMasterTimelineHeight, TallMasterTimeline = 37;

    // Tallest scrubber step: 220 px (the step scale is offset by two).
    private const int MostMasterTimeline = 52;

    /// <summary>Timeline track height step, 1 to 50.</summary>
    public double LayerTrackHeight
    {
        get => Math.Clamp(AppSettings.Current.LayerHeight, 1, 50);
        set => SetHeight(s => s.LayerHeight, (s, v) => s.LayerHeight = v, value, nameof(LayerTrackHeight), nameof(LayerBarHeight));
    }

    /// <summary>Audio tab row height step, 1 to 50.</summary>
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

        SaveView(settings => set(settings, steps));
        OnPropertyChanged(name);
        OnPropertyChanged(pixels);
    }

    /// <summary>Scrubber area height in pixels (waveform, I-frame lines, playhead): 16 to 220.</summary>
    public double TimelineAreaHeight => 12 + 4 * MasterTimelineHeight;

    /// <summary>Timeline track height in pixels (26 at the default step 25).</summary>
    // Same mapping as audio row height so both sliders share the same range.
    public double LayerBarHeight => Math.Round(46 + 2.16 * LayerTrackHeight);

    /// <summary>Audio tab row height in pixels (100 at the default step 25).</summary>
    public double AudioTrackRowHeight => ShowWaveforms ? Math.Round(46 + 2.16 * AudioTrackHeight) : 64;
    /// <summary>
    /// Renders the timeline waveform and one per audio track in the background after a load, from a single file pass
    /// (Waveforms.RenderManyAsync).
    /// </summary>
    /// <summary>Per-track waveforms of the opened file, layered in the Master Mix view.</summary>
    public System.Collections.ObjectModel.ObservableCollection<System.Windows.Media.ImageSource> SourceWaveforms { get; } = [];

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

            SourceWaveforms.Clear();
            foreach (var picture in pictures.Take(tracks.Count))
            {
                if (picture is not null)
                    SourceWaveforms.Add(picture);
            }

            if (active.Count > 0 && !timeline.IsCancellationRequested)
                TimelineWaveform = pictures[^1];
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Waveforms are optional decoration; failures are ignored.
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

    /// <summary>Renders the timeline waveform as the mix of non-dropped tracks; re-run when a track is dropped or restored.</summary>
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
    /// Picture rows: video clips, Cutouts and images, then the caption box while auto-captions are on; one list so all share placement logic.
    /// </summary>
    public ObservableCollection<Layer> LayerRows { get; } = [];

    private void RefreshLayerRows()
    {
        // Stack order front-first: captions, layers, then the blur fill row. Each row is a track represented by its first clip.
        // Lists are refilled only when the row set changes, since recreating a row drops its selection and any in-progress drag.
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

        // A video layer with audio also gets an Audio tab row backed by the same layer, so picture and audio cannot drift.
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

    /// <summary>Audio tab list: output audio tracks (last opened file's streams), then one row per timeline audio track.</summary>
    public ObservableCollection<object> AudioRows { get; } = [];

    /// <summary>Hide Dropped Tracks: list only audio reaching the output; Ignore/Drop tracks and muted clips are hidden, not removed.</summary>
    [ObservableProperty] private bool _hideDroppedTracks;

    partial void OnHideDroppedTracksChanged(bool value)
    {
        RefreshAudioRows();
        var hidden = AudioTracks.Count + AudioClipRows.Count - AudioRows.Count;
        StatusText = !value ? "Showing every audio track."
            : hidden == 0 ? "Dropped and muted audio tracks are hidden. There are none right now."
            : $"Hiding {hidden} dropped or muted audio track{(hidden == 1 ? "" : "s")}. Press the button again to show {(hidden == 1 ? "it" : "them")}.";
    }

    /// <summary>Row visibility: hidden only when it is dropped and Hide Dropped Tracks is on.</summary>
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

    /// <summary>Audio tab rows for file audio on the timeline, one per track.</summary>
    public ObservableCollection<Layer> AudioClipRows { get; } = [];

    public bool HasAudioClips => AudioClipRows.Count > 0;

    /// <summary>Auto-opens the Canvas when the setting is enabled (default on).</summary>
    private void OpenLayoutPaneIfWanted()
    {
        if (!_isBackgroundWorker && AppSettings.Current.AutoOpenLayoutPane && CanEditLayout)
            IsArrangeActive = true;
    }

    // ----- Voice track detection by name -----

    /// <summary>
    /// On fresh file loads only (not projects or queued jobs): marks the stream whose title names a microphone/voice as the voice
    /// (<see cref="VoiceTrackScanner"/>, Layer.IsVoice on its Sub-Lane clips) unless disabled or a voice exists. Equivalent to the manual
    /// tick, so Auto-Captions (<see cref="ListenToVoice(Layer)"/>) and ducking follow. Returns a status line, or null when nothing was marked.
    /// </summary>
    private string? MarkVoiceTrack()
    {
        if (!AppSettings.Current.AutoMarkVoiceTrack || Layers.Any(l => l is { IsVoice: true, CarriesSound: true }))
            return null;

        // Track titles (placeholder "Track N" ignored); untitled MP4/MOV tracks fall back to their handler name (generic ones ignored).
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
    // Recorded or imported in the Voiceover Studio, session-scoped, mixed into output audio track 1.

    private readonly VoiceRecorder _recorder = new();

    public ObservableCollection<string> Microphones { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordVoiceoverCommand))]
    private string? _selectedMicrophone;

    /// <summary>Recording is active.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordVoiceoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseVoiceoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopVoiceoverCommand))]
    private bool _isRecording;

    /// <summary>Recording is paused (resumable or stoppable).</summary>
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

    /// <summary>Voiceover gain in dB.</summary>
    [ObservableProperty] private double _voiceoverGainDb;

    /// <summary>How far into the finished video the voiceover begins, in seconds.</summary>
    [ObservableProperty] private double _voiceoverStartSeconds;

    /// <summary>Whether the voiceover is mixed into the encode (also implied when captions use it).</summary>
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Voiceover: recording from {SelectedMicrophone}", ex);
            VoiceoverStatus = $"Could not record from {SelectedMicrophone}: {ex.Message}";
            return;
        }

        // Timeline start of the take; it is placed there when finished.
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

    private bool CanStopVoiceover() => HoldsRecording;

    /// <summary>
    /// Whether a take (recording or paused, keeping recorder, file and start time) belongs to the open project; tab switches are
    /// blocked meanwhile so Stop places it in this project.
    /// </summary>
    public bool HoldsRecording => IsRecording || IsRecordingPaused;

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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            recorded = false;
            AppLog.Write("Voiceover: finishing the recording", ex);
            VoiceoverStatus = $"The recording could not be finished: {ex.Message}";
        }

        // Count placement as a pending add so tabs cannot switch between stop and placement.
        using var placing = BeginAdd();
        (IsRecording, IsRecordingPaused) = (false, false);
        if (recorded && PlacesVoiceoverOnTimeline)
            await PlaceRecordingAsync();
        else if (recorded)
            await LoadVoiceoverAsync(RecordingPath, "Recorded");
        else if (!VoiceoverStatus.StartsWith("The recording", StringComparison.Ordinal))
            VoiceoverStatus = "Nothing was recorded. Check that the microphone is not in use by another program.";
    }

    // Playhead position (s) when the current take started.
    private double _recordingStartSeconds;

    /// <summary>
    /// Takes are placed as ordinary audio clips on the timeline; with nothing open, the single session voiceover is mixed instead.
    /// </summary>
    public bool PlacesVoiceoverOnTimeline => HasSource;

    /// <summary>Voiceover Studio hint text, depending on where takes go.</summary>
    public string VoiceoverIntro => PlacesVoiceoverOnTimeline
        ? "Record a voiceover while the video plays in the main window. When you press Stop it goes onto the timeline as an audio clip of its own, starting where the playhead was when you pressed Record. It is marked as a voice, so sounds set to Auto-Duck make room for it; move, trim, split or delete it on the Audio tab like any other sound."
        : "Record a voiceover while the video plays in the main window, or bring one in from a file. It is added to the encode as a second input and mixed into the first audio track, after any cuts.";

    /// <summary>
    /// Places the finished take as an audio clip at its start time, after moving it to its own file in the Voiceovers folder
    /// (the next take would overwrite it, and saved projects must find it).
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
            AppLog.Write($"Voiceover: saving to {AppPaths.Voiceovers}", ex);
            VoiceoverStatus = $"The recording could not be saved to {AppPaths.Voiceovers}: {ex.Message}";
            return;
        }

        var at = _recordingStartSeconds;
        var name = $"Voiceover {Layers.Count(l => l.IsAudio && l.IsVoice) + 1}";
        var generation = _projectGeneration;
        if (!await AddAudioLayerAsync(kept, at, isVoice: true, name: name))
        {
            // Tab was backgrounded anyway: never place it in another project; the file is kept.
            VoiceoverStatus = generation != _projectGeneration
                ? $"The take was not placed: its project tab went behind first. It is kept as {kept}, to drag onto that project's timeline."
                : "Nothing was recorded. Check that the microphone is not in use by another program.";
            return;
        }

        VoiceoverStatus = $"{name} is on the timeline at {TimeDisplay.Format(at)}, as an audio clip of its own (Audio tab). Press Record for another take; Ctrl+Z takes this one off again.";
        Log($"Added {name} at {TimeDisplay.Format(at)}");
    }

    /// <summary>Imports an audio file as the voiceover, placed as a clip at the playhead.</summary>
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
        // Session recordings are deleted with the session; imported files are never deleted.
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
            // Still locked; the session folder is cleaned on exit.
        }
    }

    /// <summary>Do-Over: discards the current or just-finished take so the next Record starts fresh.</summary>
    [RelayCommand]
    private async Task DiscardVoiceoverAsync()
    {
        await _recorder.DiscardAsync();
        (IsRecording, IsRecordingPaused) = (false, false);
        RemoveVoiceover();
        VoiceoverStatus = "The take was thrown away. Press Record to start again.";
    }

    // ----- The voiceover against the timeline -----

    /// <summary>Output duration in seconds (GetOutputDuration).</summary>
    public double OutputDurationSeconds => GetOutputDuration();

    /// <summary>Cut positions between visible top-level picture clips (PictureSpans) as fractions (0..1) of the output length.</summary>
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

    /// <summary>Trimmed voiceover waveform for the Master Mix View.</summary>
    [ObservableProperty] private ImageSource? _voiceoverMixWaveform;

    /// <summary>Voiceover start and length in the Master Mix View as fractions (0..1) of the source length.</summary>
    [ObservableProperty] private double _voiceoverMixStart;

    [ObservableProperty] private double _voiceoverMixWidth;

    /// <summary>
    /// Positions the voiceover in the Master Mix View: tracks are drawn in source time while the voiceover is in output time, so its
    /// start and end are mapped back through the cuts to source time.
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

        // Waveforms are vector drawings from peaks, so the trimmed part is redrawn from the peaks within the trim.
        var kept = Waveforms.Cut(VoiceoverWaveform, from, to, Math.Max((int)(1200 * (to - from)), 2), 120);
        if (kept is null)
        {
            (VoiceoverMixWaveform, VoiceoverMixStart, VoiceoverMixWidth) = (null, 0, 0);
            return;
        }

        // Output time equals timeline time.
        var start = VoiceoverStartSeconds;
        var end = Math.Max(VoiceoverStartSeconds + (to - from) * VoiceoverDuration, start);
        VoiceoverMixWaveform = kept;
        VoiceoverMixStart = Math.Clamp(start / total, 0, 1);
        VoiceoverMixWidth = Math.Clamp((end - start) / total, 0, 1 - VoiceoverMixStart);
    }

    /// <summary>Adopts a WAV file as the session voiceover: measures, renders its waveform and sets the trim to full length.</summary>
    private async Task LoadVoiceoverAsync(string path, string verb)
    {
        // Counts as a pending add while probing so no other tab can receive it.
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

        // Clear first so bindings see a change even when the path is reused for a new recording.
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

    /// <summary>Resolution choices: Match Project Settings (blank boxes), Custom, then common sizes.</summary>
    public IReadOnlyList<string> ResolutionPresets { get; } =
        [MatchProject, CustomResolution, "4K (3840x2160)", "1440p (2560x1440)", "1080p (1920x1080)", "720p (1280x720)", "480p (854x480)"];

    /// <summary>Preset matching the size boxes in either orientation; Match Project Settings when blank; else Custom.</summary>
    [ObservableProperty] private string _resolutionPreset = MatchProject;

    private bool _syncingResolutionPreset;

    partial void OnResolutionPresetChanged(string value)
    {
        if (_syncingResolutionPreset || value is null)
            return;

        // Match Project Settings: both boxes blank (Sequence size).
        if (value == MatchProject)
        {
            SetOutputSize("", "");
            return;
        }

        if (ResolutionRegex().Match(value) is not { Success: true } match)
            return;

        // Presets are landscape; transpose under Use Vertical Resolution.
        var (width, height) = (match.Groups[1].Value, match.Groups[2].Value);
        if (UseVerticalResolution)
            (width, height) = (height, width);
        SetOutputSize(width, height);
    }

    /// <summary>Syncs the drop-down to the matching preset without treating it as a user choice.</summary>
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

    /// <summary>Whether the Export Presets selector is shown in the Export tab's first card.</summary>
    public bool ShowPresetBarInExport => !ShowPresetBarAtTop;

    // ----- Notifications -----

    /// <summary>Raises a Windows completion notification for long tasks when enabled.</summary>
    private void NotifyFinished(string title, string message)
    {
        if (!_isBackgroundWorker && AppSettings.Current.NotifyOnEncodeComplete)
            Notifier.Show(title, message);
    }
}
