using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The sequence: a timeline with a clock of its own, on which the main video is clips like any other (the document's,
// see MainViewModel.Document.cs); and motion, the keyframes that move, size and turn a layer as that clock runs.
public partial class MainViewModel
{
    // ----- The sequence clock -----
    // The timeline is not the main video's. It is as long as whatever reaches furthest along it, and the main video is
    // clips on it like any other: one to begin with, more once it has been split, each moved, trimmed and deleted on its
    // own. Everything on the timeline is in the sequence's time.

    // The length of the main video's file as the player reported it, for a file that could not be inspected.
    private double _playerMediaSeconds;

    /// <summary>How long the main video's file is, in seconds; 0 while that is not known.</summary>
    private double MainMediaSeconds => _mediaInfo?.DurationSeconds > 0 ? _mediaInfo.DurationSeconds : _playerMediaSeconds;

    /// <summary>
    /// The player says how long what it opened is. Only needed for a file that could not be inspected: its length is
    /// then known, and the main video is put on the timeline for it.
    /// </summary>
    /// <param name="isFile">Whether it is playing the main video's file as it is, not the sequence built around it.</param>
    public void ReportPlayerDuration(double seconds, bool isFile)
    {
        if (_mediaInfo?.DurationSeconds > 0 || !isFile || seconds <= 0 || Math.Abs(seconds - _playerMediaSeconds) < 0.001)
            return;

        _playerMediaSeconds = seconds;
        if (!MainVideoClips().Any())
            PlaceMainVideo();
        GenerateCommand();
    }

    // The I-frames of the main video's file, and how its clips lay when they were last put along the sequence.
    private List<double> _sourceIFrames = [];
    private int _iFrameLayout;

    /// <summary>
    /// How the main video's clips lie, as a number that changes when they do: read from the document (cheap, asked after
    /// every change), so that the builder's lowering is only asked for when there is something to lay out again.
    /// </summary>
    private int GetMainLayout()
    {
        var hash = new HashCode();
        hash.Add(FrameEngine);
        hash.Add(MainMediaSeconds);
        foreach (var track in Document.Tracks)
        {
            hash.Add(track.Kind);
            hash.Add(track.IsHidden);
            foreach (var clip in track.Clips)
            {
                if (clip.ContentKind != LayerKind.MainVideo)
                    continue;
                hash.Add(clip.Start);
                hash.Add(clip.End);
                hash.Add(clip.MediaOffset);
                hash.Add(clip.Speed);
                hash.Add(clip.IsHidden);
                hash.Add(clip.TransitionIn);
                hash.Add(clip.SourcePath);
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>Takes the I-frame index of the main video's file, and lays it along the sequence: each I-frame where the clip that shows it puts it.</summary>
    private void SetSourceIFrames(List<double> iFrames)
    {
        _sourceIFrames = iFrames;
        _iFrameLayout = GetMainLayout();
        if (IsMainWholeSequence || iFrames.Count == 0)
        {
            IFrames = iFrames;
        }
        else
        {
            var placed = new List<double>(iFrames.Count);
            foreach (var clip in GetMainClips())
            {
                var first = iFrames.BinarySearch(clip.Offset);
                for (var i = first < 0 ? ~first : first; i < iFrames.Count && iFrames[i] < clip.Offset + clip.End - clip.Start; i++)
                    placed.Add(iFrames[i] - clip.Offset + clip.Start);
            }

            IFrames = placed;
        }

        UpdateIFrameMarks();
    }

    /// <summary>Brings the length of the timeline, and what is drawn along it, up to date with the clips.</summary>
    private void RefreshSequence()
    {
        var milliseconds = SequenceSeconds * 1000;
        if ((milliseconds > 0 || HasSource) && Math.Abs(DurationMs - milliseconds) > 0.5)
        {
            DurationMs = milliseconds;
            if (PositionMs > milliseconds)
                PositionMs = milliseconds;
            UpdateIFrameMarks();
        }

        if (GetMainLayout() != _iFrameLayout)
            SetSourceIFrames(_sourceIFrames);
    }

    /// <summary>
    /// The clip of the main video that is on screen at a moment of the sequence, as the player plays it (the graph
    /// builder's primary chain), or null when none is.
    /// </summary>
    public MainClip? GetMainClipAt(double seconds)
    {
        foreach (var clip in GetMainClips())
        {
            if (seconds >= clip.Start - 0.0005 && seconds < clip.End)
                return clip;
        }

        return null;
    }

    /// <summary>
    /// Puts the main video back as it is when a source is loaded: one clip of the whole file at the start of the timeline,
    /// its other clips gone (the main video row's "Back to the Start of the Timeline, Whole"). One step of Undo.
    /// </summary>
    public void ResetMainTiming()
    {
        if (!HasSource || MainMediaSeconds <= 0.05)
            return;

        FlushLooks();
        var before = CaptureSnapshot();
        PlaceMainVideo();
        Checkpoint("put the main video back whole", before);
        Log("The main video is back at the start of the timeline, whole");
    }

    // ----- The recycle bin -----
    // A clip that is deleted is not thrown away. It is taken off the timeline (so nothing of it is rendered)
    // and kept, as it was, in the bin; from there it can be put back where it came from.

    /// <summary>The deleted clips, the latest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<BinItem> RecycleBin { get; } = [];

    public bool HasDeleted => RecycleBin.Count > 0;

    /// <summary>What the trash can says beside it: how many clips are in the bin, or nothing.</summary>
    public string RecycleBinText => RecycleBin.Count > 0 ? RecycleBin.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>A clip about to be deleted, as the bin keeps it: a copy of it as it is now, its track, and a picture of it.</summary>
    private (DeletedClip Clip, System.Windows.Media.ImageSource? Preview) Binned(TimelineClip clip, TimelineTrackModel track, Layer? view)
    {
        FlushLooks();
        return (new DeletedClip(CopyOfClip(clip), track.Id, track.Kind, Document.Tracks.IndexOf(track)), view?.Filmstrip ?? view?.Waveform);
    }

    /// <summary>A copy of a clip that nothing done to it reaches, the same id (it is put back as itself), its look shared (never changed in place).</summary>
    private static TimelineClip CopyOfClip(TimelineClip clip) =>
        System.Text.Json.JsonSerializer.Deserialize<TimelineClip>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(clip, BinOptions), BinOptions)!;

    private static readonly System.Text.Json.JsonSerializerOptions BinOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private void AddToBin(DeletedClip clip, string name, System.Windows.Media.ImageSource? preview)
    {
        RecycleBin.Insert(0, new BinItem(clip, name, $"{TimeDisplay.Format(clip.Clip.Start)} to {TimeDisplay.Format(clip.Clip.End)}", preview));
        OnPropertyChanged(nameof(HasDeleted));
        OnPropertyChanged(nameof(RecycleBinText));
    }

    private void SetBin(IEnumerable<DeletedClip>? clips)
    {
        RecycleBin.Clear();
        foreach (var clip in clips ?? [])
        {
            if (clip?.Clip is not { } deleted)
                continue;

            var name = deleted.ContentKind == LayerKind.MainVideo ? MainVideoName() : deleted.State.Name;
            var preview = deleted.SourcePath is { } path ? GetLayerPicture(path) : null;
            RecycleBin.Add(new BinItem(clip, name, $"{TimeDisplay.Format(deleted.Start)} to {TimeDisplay.Format(deleted.End)}", preview));
        }

        OnPropertyChanged(nameof(HasDeleted));
        OnPropertyChanged(nameof(RecycleBinText));
    }

    /// <summary>A picture already made of a layer's file this session: frames of its video, or failing that nothing.</summary>
    private System.Windows.Media.ImageSource? GetLayerPicture(string path) => _layerFilmstrips.GetValueOrDefault(path);

    /// <summary>Deletes a clip: it leaves the timeline, its time left empty, and waits in the recycle bin. One step of Undo.</summary>
    private bool MoveToBin(Layer view, string? label = null)
    {
        if (ClipOf(view) is not { } clip || Document.TrackOf(clip) is not { } track)
            return false;

        var deleted = Binned(clip, track, view);
        if (!Edit(label ?? $"delete {view.Name}", edit => edit.DeleteClip(clip)))
            return false;

        AddToBin(deleted.Clip, view.Name, deleted.Preview);
        return true;
    }

    /// <summary>
    /// Puts a deleted clip back on the timeline where it was: on its track when that is still there and free for it,
    /// otherwise on a new track of its kind at the place its own had in the stack.
    /// </summary>
    public void RestoreFromBin(BinItem item)
    {
        if (!RecycleBin.Contains(item))
            return;

        // A copy is put back, so the bin's own stays as it was (an undo step can bring it back to the bin).
        var clip = CopyOfClip(item.Clip.Clip);
        if (Document.FindClip(clip.Id) is not null)
            clip.Id = Guid.NewGuid();
        if (!Edit($"restore {item.Name}", edit =>
            {
                if (Document.Tracks.FirstOrDefault(t => t.Id == item.Clip.TrackId) is { IsLocked: false } track && track.Accepts(clip))
                {
                    try
                    {
                        edit.PlaceClip(track, clip, allowCrossfadeOverlap: false);
                        return;
                    }
                    catch (TimelineEditException)
                    {
                        // No room there now: a track of its own.
                    }
                }

                var kind = TimelineTrackModel.KindFor(clip.ContentKind);
                var index = Math.Clamp(item.Clip.TrackIndex, 0, Document.Tracks.Count);
                edit.PlaceClip(edit.InsertTrack(index, kind == TimelineTrackKind.Subtitle ? TimelineTrackKind.Video : kind), clip, allowCrossfadeOverlap: false);
            }))
        {
            return;
        }

        RecycleBin.Remove(item);
        OnPropertyChanged(nameof(HasDeleted));
        OnPropertyChanged(nameof(RecycleBinText));
        Log($"Restored clip: {item.Name}, {item.TimeText}");
    }

    /// <summary>Empties the recycle bin: what was in it is gone for good (Ctrl+Z aside).</summary>
    public void EmptyBin()
    {
        if (RecycleBin.Count == 0)
            return;

        Checkpoint("empty the recycle bin");
        SetBin(null);
        var released = ReleaseUnusedLayerPictures();
        StatusText = released > 0
            ? $"The recycle bin is empty. {released} file{(released == 1 ? " is" : "s are")} no longer on the timeline, and what was held of {(released == 1 ? "it" : "them")} in memory has been let go."
            : "The recycle bin is empty.";
    }

    /// <summary>
    /// Lets go of what is held in memory for files that no clip on the timeline uses any more: the strips of
    /// frames and the waveforms drawn for their blocks. (Ctrl+Z can still bring such a clip back; its pictures
    /// are then simply made again.) Returns how many files that was.
    /// </summary>
    private int ReleaseUnusedLayerPictures()
    {
        var used = new HashSet<string>(Layers.Select(l => l.ImagePath), StringComparer.OrdinalIgnoreCase) { LocalMediaPath };
        var unused = _layerFilmstrips.Keys.Concat(_layerWaveforms.Keys).Where(path => !used.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in unused)
        {
            _layerFilmstrips.Remove(path);
            _layerWaveforms.Remove(path);
        }

        return unused.Count;
    }

    // ----- Keyframes -----
    // A keyframe is one thing: a moment at which a clip's place, size and turn are all pinned at once.
    // Between two keyframes the clip moves in straight lines; before the first and after the last it holds.
    // (The I-frames of the video's encoding are another matter altogether, and are called that.)

    /// <summary>
    /// Whether moving, sizing or turning a clip writes keyframes. Off, a clip that has keyframes takes its
    /// whole motion with it when it is moved, and one that has none is simply moved.
    /// </summary>
    [ObservableProperty] private bool _keyframesEnabled;

    /// <summary>
    /// What a move writes while keyframes are enabled: the keyframe nearest the playhead (true), or a new
    /// one at the playhead (false).
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(KeyframesCreate))] private bool _keyframesSnap;

    /// <summary>The other of the two: a move sets a new keyframe at the playhead.</summary>
    public bool KeyframesCreate
    {
        get => !KeyframesSnap;
        set => KeyframesSnap = !value;
    }

    /// <summary>With keyframes enabled, a change by hand keys only the property that changed; off, every motion property (Key All).</summary>
    [ObservableProperty] private bool _keyChangedOnly = AppSettings.Current.KeyChangedOnly;

    partial void OnKeyChangedOnlyChanged(bool value) => SaveView(s => s.KeyChangedOnly = value);

    /// <summary>The Keyframes pane's graph of the selected row's value.</summary>
    [ObservableProperty] private bool _showKeyframeGraph = AppSettings.Current.ShowKeyframeGraph;

    partial void OnShowKeyframeGraphChanged(bool value) => SaveView(s => s.ShowKeyframeGraph = value);

    partial void OnKeyframesEnabledChanged(bool value) =>
        StatusText = value
            ? $"Keyframes on: moving, resizing or turning a clip {(KeyframesSnap ? "changes its keyframe nearest the playhead" : "sets a keyframe at the playhead")}."
            : "Keyframes off: a clip that is moved takes its whole motion with it.";

    /// <summary>The clip whose keyframes the Keyframes pane shows and the keyframe buttons work on: the one last selected, or the main video.</summary>
    [ObservableProperty] private Layer? _keyLayer;

    // The main video's clip the keyframe controls last showed, with nothing selected (see OnPositionMsChanged).
    private Layer? _mainLayerShown;

    /// <summary>The clip the keyframe controls work on.</summary>
    public Layer ActiveKeyLayer => KeyLayer is { } layer && Layers.Contains(layer) && layer.IsPicture && !layer.IsEmptyTrack ? layer : MainLayer;

    /// <summary>The text layer that is selected, whose words and font the Text Settings panel edits; null when what is selected is not one.</summary>
    public Layer? SelectedTextLayer => KeyLayer is { IsText: true } layer && Layers.Contains(layer) ? layer : null;

    public bool IsTextLayerSelected => SelectedTextLayer is not null;

    partial void OnKeyLayerChanged(Layer? value)
    {
        OnPropertyChanged(nameof(ActiveKeyLayer));
        OnPropertyChanged(nameof(SelectedTextLayer));
        OnPropertyChanged(nameof(IsTextLayerSelected));
        RefreshSelectedKey();
    }

    // ----- The selected keyframe, and its easing -----
    // The keyframe that is selected is the one the playhead is on, of the clip the Keyframes pane shows:
    // stepping onto a keyframe (the previous and next buttons, or a click on its diamond) selects it.

    /// <summary>The easings a keyframe can have, for the Keyframes pane's drop-down.</summary>
    public IReadOnlyList<EasingChoice> EasingChoices => EasingChoice.All;

    private (bool Has, EasingType Easing) _selectedKey;

    /// <summary>Whether the playhead is on a keyframe of the clip the Keyframes pane shows.</summary>
    public bool HasSelectedKeyframe => _selectedKey.Has;

    /// <summary>
    /// How the clip moves on from the selected keyframe to the next one; null while none is selected.
    /// Setting it changes the keyframe (place, size and turn leave it the same way), as one step that can be undone.
    /// </summary>
    public EasingType? SelectedKeyEasing
    {
        get => _selectedKey.Has ? _selectedKey.Easing : null;
        set
        {
            if (value is not { } easing || !_selectedKey.Has || easing == _selectedKey.Easing)
                return;

            var layer = ActiveKeyLayer;

            // Several keyframes selected in the Keyframes pane (4.1.1-B): each of them takes the easing, as one step.
            if (SelectedLaneKeys.Count > 1)
            {
                Checkpoint($"change the easing of {SelectedLaneKeys.Count} keyframes of {layer.Name}");
                if (SetSelectedKeysEasing(layer, easing))
                    Log($"Easing of {SelectedLaneKeys.Count} keyframes set to {EasingChoice.All.First(c => c.Type == easing).Name} ({layer.Name})");
                return;
            }

            Checkpoint($"change the easing of a keyframe of {layer.Name}");

            // The keyframes changing is what redraws the clip and writes the command again. On the All row every motion
            // property leaves the keyframe that way; on a property's or an effect value's row, that one alone.
            if (SetLaneEasing(layer, easing, null))
                Log($"Easing of the {LaneName(SelectedKeyLane)} keyframe at {TimeDisplay.Format(PositionMs / 1000)} set to {EasingChoice.All.First(c => c.Type == easing).Name} ({layer.Name})");
        }
    }

    /// <summary>Looks again at whether the playhead is on a keyframe of the selected row, and says so only when that has changed.</summary>
    /// <param name="force">Announce it even when it looks the same (a Bezier's handles changed).</param>
    private void RefreshSelectedKey(bool force = false)
    {
        var layer = ActiveKeyLayer;
        var key = HasSource ? KeyOnLane(layer, SelectedKeyLane) : null;
        var now = (key is not null, key?.Easing ?? EasingType.Linear);
        var handles = key is { } found ? EasingCurves.Handles(found) : EasingCurves.DefaultHandles;
        if (now == _selectedKey && handles == _selectedHandles && !force)
            return;

        (_selectedKey, _selectedHandles) = (now, handles);
        OnPropertyChanged(nameof(HasSelectedKeyframe));
        OnPropertyChanged(nameof(SelectedKeyEasing));
        OnPropertyChanged(nameof(IsBezierSelected));
        OnPropertyChanged(nameof(BezierX1));
        OnPropertyChanged(nameof(BezierY1));
        OnPropertyChanged(nameof(BezierX2));
        OnPropertyChanged(nameof(BezierY2));
        OnPropertyChanged(nameof(SelectedBezierPreset));
    }

    // Which panes are open beside the player, and whether clips show their keyframes: remembered.

    /// <summary>The Keyframes pane beside the player.</summary>
    [ObservableProperty] private bool _showKeyframesPane = AppSettings.Current.ShowKeyframesPane;

    /// <summary>The media browser pane in the side column.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaBrowserVisible))]
    private bool _showMediaBrowser = AppSettings.Current.ShowMediaBrowser;

    /// <summary>Whether the media browser is on screen.</summary>
    public bool IsMediaBrowserVisible => ShowMediaBrowser;

    partial void OnShowMediaBrowserChanged(bool value) => SaveView(s => s.ShowMediaBrowser = value);

    // ----- The playback bar -----

    /// <summary>Which controls the playback bar shows, chosen with its settings button.</summary>
    public PlaybackControlSet PlaybackControls { get; } = new();

    /// <summary>Playback starts again at the end: of the selected cut segment, or of the whole sequence.</summary>
    [ObservableProperty] private bool _loopPlayback;

    /// <summary>The action-safe and title-safe areas are outlined over the picture.</summary>
    [ObservableProperty] private bool _showSafeMargins;

    /// <summary>Layers are moved, resized and turned on the player itself (Settings, the Canvas, the playback bar; 4.1.1-B).</summary>
    public bool EditLayersOnPlayer
    {
        get => AppSettings.Current.EditLayersOnPlayer;
        set
        {
            if (value == AppSettings.Current.EditLayersOnPlayer)
                return;

            SaveView(s => s.EditLayersOnPlayer = value);
            OnPropertyChanged();
            StatusText = value
                ? "Edit Layers on Video Preview on: with Live Preview, click a layer on the player to select it, then drag it, its corner, its round handle or its anchor."
                : "Edit Layers on Video Preview off: layers are arranged on the Canvas.";
        }
    }

    /// <summary>The player is silent (the export is not affected).</summary>
    [ObservableProperty] private bool _isPlayerMuted;

    /// <summary>Takes up the playback bar's choice of controls, and saves it whenever it changes.</summary>
    private void LoadPlaybackControls()
    {
        PlaybackControls.Changed -= SavePlaybackControls;
        PlaybackControls.Load(AppSettings.Current.PlaybackControls);
        PlaybackControls.Changed += SavePlaybackControls;
    }

    private void SavePlaybackControls() => SaveView(s => s.PlaybackControls = PlaybackControls.Save());

    /// <summary>Whether a clip's block on the timeline shows a diamond at each of its keyframes.</summary>
    [ObservableProperty] private bool _showClipKeyframes = AppSettings.Current.ShowClipKeyframes;

    partial void OnShowKeyframesPaneChanged(bool value) => SaveView(s => s.ShowKeyframesPane = value);

    partial void OnShowClipKeyframesChanged(bool value)
    {
        SaveView(s => s.ShowClipKeyframes = value);
        TimelineChanged?.Invoke();
    }

    /// <summary>Remembers where a pane was put or how large it was made, for the mode in use.</summary>
    public void SavePaneLayout(Action<AppSettings> set) => SaveView(set);

    /// <summary>
    /// A view setting of the mode in use changed from the window's Tabs and Panes menu (Pass 4.1B): saved, and the
    /// properties that show it raised (they read the settings: ShowAdvancedPlayback, ShowCommandPreviewTab...).
    /// </summary>
    public void SetViewSetting(Action<AppSettings> set, params string[] properties)
    {
        SaveView(set);
        foreach (var property in properties)
            OnPropertyChanged(property);
    }

    /// <summary>Reset Panes Layout: every pane back where this mode starts with it. The window then lays them out again.</summary>
    public void ResetPaneLayout()
    {
        SaveView(settings => settings.ResetPaneLayout());
        OnPropertyChanged(nameof(MasterTimelineHeight));
        OnPropertyChanged(nameof(TimelineAreaHeight));
        Log("Reset panes layout");
    }

    /// <summary>
    /// Auto-Duck from a track's right-click menu: the sound is turned down while a voice speaks, or (the other
    /// box) it is the voice the ducked sounds make room for. Says what that comes to, because ducking takes
    /// two: a sound to turn down, and a voice to turn it down for.
    /// </summary>
    /// <param name="sound">An audio track of the video, or a clip that carries sound.</param>
    /// <param name="voice">Whether it is the Voiceover / Dialogue box that was clicked, not Auto-Duck.</param>
    public void ToggleDucking(object sound, bool voice)
    {
        var name = sound switch { AudioTrack track => track.Title, Layer layer => layer.Name, _ => "" };
        if (name.Length == 0)
            return;

        Checkpoint($"change the ducking of {name}");
        bool on;
        switch (sound)
        {
            case AudioTrack track when voice: on = track.IsVoice = !track.IsVoice; break;
            case AudioTrack track: on = track.AutoDuck = !track.AutoDuck; break;
            case Layer layer when voice: on = layer.IsVoice = !layer.IsVoice; break;
            default: on = ((Layer)sound).AutoDuck = !((Layer)sound).AutoDuck; break;
        }

        var voices = AudioTracks.Count(t => t.IsVoice && !t.IsDropped) + Layers.Count(l => l is { IsVoice: true, CarriesSound: true, IsHidden: false });
        var ducked = AudioTracks.Count(t => t is { AutoDuck: true, IsVoice: false, IsDropped: false }) + Layers.Count(l => l is { AutoDuck: true, IsVoice: false, CarriesSound: true, IsHidden: false });
        var amount = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Abs(DuckAmountDb):0} dB");
        Log((voice, on) switch
        {
            (false, true) when voices == 0 => $"Auto-Duck on for {name}. Nothing is marked as a voice yet: right-click the voice's track and tick Voiceover / Dialogue",
            (false, true) => $"Auto-Duck on for {name}: turned down by {amount} while a voice speaks",
            (false, false) => $"Auto-Duck off for {name}",
            (true, true) when ducked == 0 => $"{name} is marked as a voice. No sound is set to Auto-Duck yet: right-click the one to turn down",
            (true, true) => $"{name} is marked as a voice: {ducked} sound{(ducked == 1 ? "" : "s")} duck{(ducked == 1 ? "s" : "")} under it by {amount}",
            _ => $"{name} is no longer marked as a voice",
        });
    }

    private void SaveView(Action<AppSettings> set)
    {
        if (_isBackgroundWorker)
            return;

        set(AppSettings.Current);
        SaveSettingsSoon();
    }

    private bool _applyingKeys;
    private bool _recordingKey;
    private bool _anyKeys;

    private void RefreshAnyKeys()
    {
        _anyKeys = Layers.Any(l => l.IsAnimated);
        RefreshSelectedKey();
    }

    // The order matters for the main video, whose corner is worked out from its size.
    private static readonly KeyProperty[] KeyOrder = [KeyProperty.Scale, KeyProperty.X, KeyProperty.Y, KeyProperty.Rotation, KeyProperty.Opacity];

    partial void OnPositionMsChanged(double value)
    {
        // With nothing selected, the keyframe controls and the Effects pane work on the main video's clip at the playhead:
        // the playhead crossing into another of its clips is another clip to show.
        if (KeyLayer is null && !_applyingKeys && !ReferenceEquals(MainLayer, _mainLayerShown))
        {
            _mainLayerShown = MainLayer;
            OnPropertyChanged(nameof(ActiveKeyLayer));
            RefreshSelectedKey();
        }

        if (!_anyKeys)
            return;

        // While playing, the boxes and the layout pane follow the motion fifteen times a second, not at the
        // player's frame rate: they are there to be read, and every change re-lays them out. They come to rest on
        // the exact frame when playback stops (OnIsPlayingChanged).
        if (IsPlaying && Environment.TickCount64 - _keysAppliedAt < KeyEchoIntervalMs)
            return;

        _keysAppliedAt = Environment.TickCount64;
        ApplyKeyframes();
        RefreshSelectedKey();
    }

    private const long KeyEchoIntervalMs = 66;
    private long _keysAppliedAt;

    partial void OnIsPlayingChanged(bool value)
    {
        // Background work waiting to start waits while the player plays (Settings, Performance).
        if (!_isBackgroundWorker)
            WorkScheduler.IsPlaying = value;

        if (!value && _anyKeys)
        {
            ApplyKeyframes();
            RefreshSelectedKey();
        }
    }

    /// <summary>Set while the clips are being shown as they are at the playhead (their keyframes applied): nothing is being changed.</summary>
    public bool IsApplyingKeys => _applyingKeys;

    /// <summary>
    /// Shows every animated clip as it is at the playhead: its row, the Keyframes pane and its box in the
    /// layout pane follow the motion. Nothing is changed by it, so nothing is rebuilt.
    /// </summary>
    private void ApplyKeyframes()
    {
        if (_applyingKeys)
            return;

        _applyingKeys = true;
        try
        {
            var seconds = PositionMs / 1000;
            foreach (var layer in Layers)
                Apply(layer);

            void Apply(Layer layer)
            {
                if (!layer.IsAnimated)
                    return;

                // The size first, about the anchor point: a place with no keyframes of its own is the anchor's, and is
                // moved with it (as the graph places it); a place with keyframes is then set from them.
                var time = seconds - layer.StartTime;
                foreach (var property in KeyOrder)
                {
                    if (layer.HasKeys(property))
                        layer.SetValue(property, layer.Evaluate(property, time));
                }
            }
        }
        finally
        {
            _applyingKeys = false;
        }
    }

    /// <summary>
    /// A clip was moved, sized or turned by hand. With keyframes enabled that is written into a keyframe: a
    /// new one at the playhead holding the clip as it now is, or (snapping) the nearest one. Otherwise a clip
    /// with keyframes takes its whole motion along by as much as it was moved.
    /// </summary>
    private void RecordKey(Layer layer, string? propertyName)
    {
        if (_applyingKeys || _recordingKey || _commandDeferrals > 0 || Layer.GetKeyProperty(propertyName) is not { } property || !HasSource)
            return;

        var writes = KeyframesEnabled && (layer.IsAnimated || !KeyframesSnap);
        if (!writes && !layer.HasKeys(property))
            return;

        var time = Math.Max(PositionMs / 1000 - layer.StartTime, 0);
        _recordingKey = true;
        try
        {
            using var once = DeferCommand();
            if (!writes)
                layer.OffsetKeys(property, layer.GetValue(property) - layer.Evaluate(property, time));
            else if (KeyframesSnap && layer.GetNearestKeyTime(time) is { } nearest)
                layer.SetKey(property, nearest, layer.GetValue(property));
            else if (KeyChangedOnly)
                layer.SetKey(property, time, layer.GetValue(property));
            else
                layer.SetMasterKey(time);
        }
        finally
        {
            _recordingKey = false;
        }
    }

    /// <summary>Whether the clip has a keyframe at the playhead.</summary>
    public bool HasKeyframeAtPlayhead(Layer layer) => layer.HasKeyAt(PositionMs / 1000 - layer.StartTime);

    /// <summary>Sets a keyframe at the playhead, holding the clip's place, size and turn as they are there.</summary>
    public void AddKeyframe(Layer layer)
    {
        if (!HasSource)
        {
            StatusText = "Load a video first: a keyframe is set at the playhead.";
            return;
        }

        var time = Math.Max(PositionMs / 1000 - layer.StartTime, 0);
        Checkpoint($"set a keyframe for {layer.Name}");
        var first = !layer.IsAnimated;
        layer.SetMasterKey(time);
        Log(first
            ? $"Added keyframe at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name}): move the playhead, move the clip, and add another"
            : $"Added keyframe at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name})");
    }

    /// <summary>Takes away the clip's keyframe at the playhead.</summary>
    public void RemoveKeyframe(Layer layer)
    {
        var time = PositionMs / 1000 - layer.StartTime;
        if (!layer.HasKeyAt(time))
        {
            StatusText = $"{layer.Name} has no keyframe at the playhead. Step onto one with the previous and next keyframe buttons.";
            return;
        }

        Checkpoint($"remove a keyframe of {layer.Name}");
        layer.RemoveMasterKey(time);
        Log(layer.IsAnimated ? $"Removed keyframe at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name})" : $"Removed the last keyframe of {layer.Name}: it stays as it is now");
        ApplyKeyframes();
    }

    /// <summary>Moves the playhead onto the clip's keyframe before (-1) or after (+1) it.</summary>
    public bool GoToKeyframe(Layer layer, int direction)
    {
        var time = PositionMs / 1000 - layer.StartTime;
        double? target = null;
        foreach (var key in layer.KeyTimes)
        {
            if (direction < 0 && key < time - Layer.KeyTolerance)
                target = key;
            else if (direction > 0 && key > time + Layer.KeyTolerance && target is null)
                target = key;
        }

        if (target is not { } found)
        {
            StatusText = !layer.IsAnimated ? $"{layer.Name} has no keyframes." : direction < 0 ? "That is its first keyframe." : "That is its last keyframe.";
            return false;
        }

        PositionMs = Math.Clamp((found + layer.StartTime) * 1000, 0, Math.Max(DurationMs, 0));
        return true;
    }

    /// <summary>Takes every keyframe off a clip: it stays as it is shown now.</summary>
    public void ClearKeys(Layer layer)
    {
        if (!layer.IsAnimated)
            return;

        Checkpoint($"clear the keyframes of {layer.Name}");
        layer.ClearKeys();
        Log($"Cleared the keyframes of {layer.Name}");
    }


    // ----- What the player plays -----

    /// <summary>
    /// What the player opens: the main video's file while that is the whole sequence, and otherwise the
    /// sequence written out for it (mpv's EDL), clip after clip, so that its clock is the sequence's. Where
    /// no clip of the main video is, the clock still needs frames to run on: stretches of a tiny black file
    /// stand in (see <see cref="StandInClock"/>), with as many silent audio tracks as the main video has, so
    /// that nothing of the main video is decoded where it is not on screen. Until that file has been made,
    /// stretches of the main video's own file stand in, and Live Preview leaves their picture out.
    ///
    /// When the picture comes from a proxy (the composed timeline proxy, or the main video's own: see
    /// MainViewModel.Proxies), the EDL has two streams: the first the proxy's picture, laid out the same way
    /// (with a picture-only clock between clips), the second the sequence as above, for its sound. The player's
    /// [vid1] is then the proxy and its [aidN] the original's tracks.
    /// </summary>
    public string PlayerSource
    {
        get
        {
            var path = LocalMediaPath;
            if (!HasSource)
                return path;

            var sound = SequenceParts(path, 0, StandInClock(), withMainSound: true);
            if (_timelineProxy is { } timeline)
                return $"edl://!new_stream;{EdlName(timeline)};!new_stream;{sound ?? EdlName(path)}";

            // The original's clips are placed by the original's time stamps; the main video's proxy has its own clock, its first
            // frame being the original's (FFmpeg moves the time stamps when it makes one): the clips' offsets are moved by as much.
            if (MainProxy is { } proxy && (IsMainWholeSequence || StandInClock(withSound: false) is not null))
            {
                var shift = (_mediaInfo?.VideoStartSeconds ?? 0) - (MediaProbe.KnownVideoStart(proxy) ?? 0);
                var picture = IsMainWholeSequence ? EdlName(proxy) : SequenceParts(proxy, shift, StandInClock(withSound: false), withMainSound: false);
                return $"edl://!new_stream;{picture};!new_stream;{sound ?? EdlName(path)}";
            }

            return sound is null ? path : "edl://" + sound;
        }
    }

    private static string EdlName(string path) => $"%{Encoding.UTF8.GetByteCount(path)}%{path}";

    /// <summary>
    /// The sequence as parts of an EDL: each clip of the main video, from <paramref name="file"/> (its clip offsets
    /// less <paramref name="origin"/>), and stand-in stretches between them; null when the main video is the whole
    /// sequence (the file is played as it is).
    /// </summary>
    /// <param name="clock">The stand-in clock, when it has been made; until then the main video's own file stands in.</param>
    private string? SequenceParts(string file, double origin, string? clock, bool withMainSound)
    {
        if (IsMainWholeSequence)
            return null;

        var name = EdlName(file);
        var parts = new StringBuilder();
        void Part(string source, double start, double length) => parts.Append(parts.Length > 0 ? ";" : "").Append(source).Append(",start=").Append(Number(start)).Append(",length=").Append(Number(length));

        var (standInName, standInLength) = clock is null ? (EdlName(LocalMediaPath), MainMediaSeconds) : (EdlName(clock), ClockSeconds);
        void StandIn(double seconds)
        {
            for (; seconds > 0.001; seconds -= standInLength)
                Part(standInName, 0, Math.Min(seconds, standInLength));
        }

        var reached = 0.0;
        foreach (var clip in GetMainClips())
        {
            StandIn(clip.Start - reached);
            Part(name, Math.Max(clip.Offset - origin, 0), clip.End - clip.Start);
            reached = clip.End;
        }

        StandIn(SequenceSeconds - reached);
        // Nothing on the timeline at all: the file is played as it is.
        return parts.Length > 0 ? parts.ToString() : null;
    }

    // ----- The stand-in clock -----
    // Where the main video is not on the sequence, the player still plays something, whose frames are the clock
    // Live Preview composes on. It used to be stretches of the main video's own file: a 1440p60 recording decoded
    // (and copied back from the graphics card) for half an hour of nothing. It is now a black file 64 by 36
    // pixels, at the main video's frame rate, with one silent track for each of its audio tracks so that the
    // graph's [aidN] are there throughout; made once by FFmpeg and kept with the caches.

    /// <summary>How long the clock file is: one stretch of it serves ten minutes of the sequence.</summary>
    private const double ClockSeconds = 600;

    private readonly HashSet<string> _clocksReady = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _clocksBeingMade = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The clock file for the main video as it is, when it has been made; null while it is being made (which
    /// this starts, and the player is given the sequence again when it is done) or when it cannot be.
    /// </summary>
    /// <param name="withSound">With a silent track for each of the main video's audio tracks; without, picture only (for an EDL whose sound comes from a stream of its own).</param>
    private string? StandInClock(bool withSound = true)
    {
        if (_mediaInfo is not { Video: not null } info || _isBackgroundWorker)
            return null;

        var rate = info.Video.FrameRate is > 1 and < 1000 and var r ? r : 30;
        var tracks = withSound ? info.Audio.Select(a => ClockLayout(a.Channels)).ToList() : [];
        var layouts = string.Join("-", tracks);
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"clock_{rate:0.###}_{(layouts.Length > 0 ? layouts : "silent")}_v1.mkv");
        var path = Path.Combine(AppPaths.Cache, "clock", key);
        if (_clocksReady.Contains(path))
            return path;

        if (_clocksBeingMade.Add(path))
        {
            _ = Task.Run(async () =>
            {
                var made = File.Exists(path) || await WorkScheduler.Run(new WorkSpec("Making the player's stand-in clock", WorkLane.Interactive) { Visible = false },
                    work => FfmpegRunner.MakeClockFileAsync(path, rate, tracks, ClockSeconds, work.Token), _shutdown.Token);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _clocksBeingMade.Remove(path);
                    if (!made)
                        return;

                    _clocksReady.Add(path);
                    GenerateCommand();
                });
            });
        }

        return null;
    }

    /// <summary>The channel layout a silent track is made with, for a track of the main video with this many channels.</summary>
    private static string ClockLayout(int channels) => channels switch
    {
        1 => "mono",
        6 => "5.1",
        8 => "7.1",
        _ => "stereo",
    };

    /// <summary>The audio filter that goes with <see cref="PlayerSource"/>: silence where the main video is not there. Empty for none.</summary>
    public string PlayerAudioFilter =>
        !HasSource || IsMainWholeSequence ? "" : $"lavfi=[volume=0:enable='not({FilterGraphBuilder.BuildMainGate(Lowering.MainHeard, 0, SequenceSeconds)})']";
}
