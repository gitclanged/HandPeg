using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Sequence clock, I-frame layout, recycle bin and motion keyframes (placement, size, rotation over sequence time).
public partial class MainViewModel
{
    // ----- The sequence clock -----
    // The timeline belongs to no file: its length is the furthest clip end, and all positions are in sequence time.

    /// <summary>
    /// Last opened file duration (s): the probed value, else for unfinished recordings one GOP past the last listed I-frame;
    /// 0 while unknown.
    /// </summary>
    private double MainMediaSeconds => _mediaInfo?.DurationSeconds > 0 ? _mediaInfo.DurationSeconds : UnfinishedFileSeconds();

    private double UnfinishedFileSeconds()
    {
        if (_mediaInfo is null || _sourceIFrames.Count < 2)
            return 0;
        var gop = (_sourceIFrames[^1] - _sourceIFrames[0]) / (_sourceIFrames.Count - 1);
        return Math.Max(_sourceIFrames[^1] - _mediaInfo.ContainerStartSeconds + gop, 0);
    }

    // Last opened file's I-frames (length fallback) and the picture-clip layout signature at the last I-frame layout.
    private List<double> _sourceIFrames = [];
    private int _iFrameLayout;

    /// <summary>
    /// Cheap hash of picture-clip layout plus known I-frame counts, checked after every change so I-frames re-layout only when it changes.
    /// </summary>
    private int GetPictureLayout()
    {
        var hash = new HashCode();
        var (file, known) = ("", 0);
        foreach (var span in PictureSpans())
        {
            hash.Add(span.Start);
            hash.Add(span.End);
            hash.Add(span.MediaOffset);
            hash.Add(span.Speed);
            hash.Add(span.Path, StringComparer.OrdinalIgnoreCase);

            // Query once per run of same-file clips (silence-cut recordings can be thousands of clips).
            if (!string.Equals(span.Path, file, StringComparison.OrdinalIgnoreCase))
                (file, known) = (span.Path, MediaProbe.KnownSeekIndex(span.Path)?.IFrames.Length ?? 0);
            hash.Add(known);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Stores the last opened file's I-frame index and lays I-frames on the sequence; a file with unknown length gets its clip now.
    /// </summary>
    private void SetSourceIFrames(List<double> iFrames)
    {
        _sourceIFrames = iFrames;
        if (iFrames.Count > 1 && _openedOver is { } previous && !(_mediaInfo?.DurationSeconds > 0))
            PlaceOpenedFile(previous);
        PlaceIFrames();
    }

    /// <summary>
    /// Maps each picture clip's file I-frames (MediaProbe.KnownSeekIndex, file clock with first frame at 0) onto the sequence at
    /// the clip's speed. Files without an index yet contribute none until it arrives.
    /// </summary>
    private void PlaceIFrames()
    {
        _iFrameLayout = GetPictureLayout();
        var placed = new List<double>();
        var indexes = new Dictionary<string, (SeekIndex? Index, double Start)>(StringComparer.OrdinalIgnoreCase);
        foreach (var span in PictureSpans())
        {
            if (!indexes.TryGetValue(span.Path, out var known))
            {
                var seek = MediaProbe.KnownSeekIndex(span.Path);
                indexes[span.Path] = known = (seek, MediaProbe.KnownVideoStart(span.Path) ?? seek?.ContainerStart ?? 0);
            }

            if (known.Index is not { IFrames.Length: > 0 } index)
                continue;

            var speed = span.Speed > 0 ? span.Speed : 1;
            var from = known.Start + span.MediaOffset;
            var to = from + (span.End - span.Start) * speed;
            var first = Array.BinarySearch(index.IFrames, from);
            for (var i = first < 0 ? ~first : first; i < index.IFrames.Length && index.IFrames[i] < to; i++)
                placed.Add(span.Start + (index.IFrames[i] - from) / speed);
        }

        placed.Sort();
        IFrames = placed;
        UpdateIFrameMarks();
    }

    /// <summary>Recomputes the timeline length and its overlays from the clips.</summary>
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

        if (GetPictureLayout() != _iFrameLayout)
            PlaceIFrames();
    }

    // ----- The recycle bin -----
    // Deleted clips leave the timeline (not rendered) and are kept intact in the bin for restore to their origin.

    /// <summary>The deleted clips, the latest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<BinItem> RecycleBin { get; } = [];

    public bool HasDeleted => RecycleBin.Count > 0;

    /// <summary>Bin count label beside the trash icon, or empty.</summary>
    public string RecycleBinText => RecycleBin.Count > 0 ? RecycleBin.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>Bin entry: a snapshot copy of the clip, its track and a thumbnail.</summary>
    private (DeletedClip Clip, System.Windows.Media.ImageSource? Preview) Binned(TimelineClip clip, TimelineTrackModel track, Layer? view)
    {
        FlushLooks();
        return (new DeletedClip(CopyOfClip(clip), track.Id, track.Kind, Document.Tracks.IndexOf(track)) { TrackParentId = track.ParentTrackId }, view is null ? null : FilmstripOf(view) ?? WaveformOf(view));
    }

    /// <summary>Isolated clip copy with the same id (restored as itself), sharing its immutable look.</summary>
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

            var name = deleted.State.Name;
            var preview = deleted.SourcePath is { } path ? GetLayerPicture(path) : null;
            RecycleBin.Add(new BinItem(clip, name, $"{TimeDisplay.Format(deleted.Start)} to {TimeDisplay.Format(deleted.End)}", preview));
        }

        OnPropertyChanged(nameof(HasDeleted));
        OnPropertyChanged(nameof(RecycleBinText));
    }

    /// <summary>Cached session thumbnail for a layer's file (video frames), or null.</summary>
    private System.Windows.Media.ImageSource? GetLayerPicture(string path) => ClipPictures.Film(path);

    /// <summary>Deletes a clip (leaving a gap) into the recycle bin together with the children removed with it. One undo step.</summary>
    private bool MoveToBin(Layer view, string? label = null)
    {
        if (ClipOf(view) is not { } clip || Document.TrackOf(clip) is not { } track)
            return false;

        var deleted = Binned(clip, track, view);
        var kin = BinnedKin(clip);
        if (!Edit(label ?? $"delete {view.Name}", edit => edit.DeleteClip(clip)))
            return false;

        // Descendants (pre-delete state) that the delete removed from the timeline.
        var gone = kin.Where(k => Document.TrackOf(k.Clip) is null).Select(k => k.Kept).ToList();
        AddToBin(gone.Count == 0 ? deleted.Clip : deleted.Clip with { Children = gone }, view.Name, deleted.Preview);
        return true;
    }

    /// <summary>All descendants of a clip (each once) as bin snapshots: copy plus track.</summary>
    private List<(TimelineClip Clip, DeletedClip Kept)> BinnedKin(TimelineClip clip)
    {
        var kin = new List<(TimelineClip Clip, DeletedClip Kept)>();
        var seen = new HashSet<TimelineClip> { clip };
        for (var next = -1; next < kin.Count; next++)
        {
            var parent = next < 0 ? clip : kin[next].Clip;
            foreach (var (track, child) in Document.ChildrenOf(parent).ToList())
            {
                if (seen.Add(child))
                    kin.Add((child, new DeletedClip(CopyOfClip(child), track.Id, track.Kind, Document.Tracks.IndexOf(track)) { TrackParentId = track.ParentTrackId }));
            }
        }
        return kin;
    }

    /// <summary>
    /// Restores a deleted clip and its removed descendants to their tracks when present and free, else to new tracks of the same
    /// kind at the original stack position. Links are preserved: an id taken since is reassigned and references updated.
    /// </summary>
    public void RestoreFromBin(BinItem item)
    {
        if (!RecycleBin.Contains(item))
            return;

        // Restore copies so bin entries stay pristine (undo can return them to the bin).
        var clips = new List<(TimelineClip Clip, DeletedClip From)> { (CopyOfClip(item.Clip.Clip), item.Clip) };
        foreach (var child in item.Clip.Children ?? [])
            clips.Add((CopyOfClip(child.Clip), child));

        var renamed = new Dictionary<Guid, Guid>();
        foreach (var (clip, _) in clips)
        {
            if (Document.FindClip(clip.Id) is null)
                continue;
            var id = Guid.NewGuid();
            renamed[clip.Id] = id;
            clip.Id = id;
        }
        foreach (var (clip, _) in clips)
        {
            if (clip.ParentClipId is { } parent && renamed.TryGetValue(parent, out var now))
                clip.ParentClipId = now;
        }

        // Recreate each missing track once for all items returning to it.
        var madeFor = new Dictionary<Guid, TimelineTrackModel>();
        if (!Edit($"restore {item.Name}", edit =>
            {
                foreach (var (clip, from) in clips)
                    PutBack(edit, clip, from, madeFor);
            }))
        {
            return;
        }

        RecycleBin.Remove(item);
        OnPropertyChanged(nameof(HasDeleted));
        OnPropertyChanged(nameof(RecycleBinText));
        Log($"Restored clip: {item.Name}, {item.TimeText}");
    }

    /// <summary>
    /// Restores a bin clip to its track if present, unlocked and free; otherwise to a recreated track of its kind at the original
    /// stack position (one per missing track via <paramref name="madeFor"/>, keyed by old id, under its recreated parent).
    /// </summary>
    private void PutBack(ITimelineTransaction edit, TimelineClip clip, DeletedClip from, Dictionary<Guid, TimelineTrackModel> madeFor)
    {
        var own = Document.Tracks.FirstOrDefault(t => t.Id == from.TrackId);
        var gone = own is null;
        var track = own ?? madeFor.GetValueOrDefault(from.TrackId);
        if (track is { IsLocked: false } && track.Accepts(clip))
        {
            try
            {
                edit.PlaceClip(track, clip, allowCrossfadeOverlap: false);
                return;
            }
            catch (TimelineEditException)
            {
                // No room: new track.
            }
        }

        var kind = TimelineTrackModel.KindFor(clip.ContentKind);
        var index = Math.Clamp(from.TrackIndex, 0, Document.Tracks.Count);
        var made = edit.InsertTrack(index, kind == TimelineTrackKind.Subtitle ? TimelineTrackKind.Video : kind);
        edit.PlaceClip(made, clip, allowCrossfadeOverlap: false);
        if (!gone || madeFor.ContainsKey(from.TrackId))
            return;

        madeFor[from.TrackId] = made;
        var parent = from.TrackParentId is { } was ? madeFor.TryGetValue(was, out var again) ? again.Id : was : (Guid?)null;
        if (parent is { } id && id != made.Id && Document.Tracks.Any(t => t.Id == id))
            edit.SetTrackParent(made, id);
    }

    /// <summary>Empties the recycle bin (recoverable only via undo).</summary>
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
    /// Releases filmstrips and waveforms of files no timeline clip uses (rebuilt if undo restores a clip). Returns the file count.
    /// </summary>
    private int ReleaseUnusedLayerPictures()
    {
        var used = new HashSet<string>(FilesInUse(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _layerWaveforms.Keys.Where(key => !used.Contains(key.Split('|')[0])))
            _layerWaveforms.Remove(key);
        return ClipPictures.Release();
    }

    // ----- Keyframes -----
    // A keyframe pins placement, size and rotation at one time; motion interpolates linearly between keys and holds outside them.
    // (Unrelated to encoder I-frames.)

    /// <summary>Whether transforms write keyframes; off, a keyed clip's whole motion path is offset instead.</summary>
    [ObservableProperty] private bool _keyframesEnabled;

    /// <summary>With keyframing on: update the nearest keyframe (true) or create one at the playhead (false).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(KeyframesCreate))] private bool _keyframesSnap;

    /// <summary>Inverse of the snap option: a move creates a keyframe at the playhead.</summary>
    public bool KeyframesCreate
    {
        get => !KeyframesSnap;
        set => KeyframesSnap = !value;
    }

    /// <summary>With keyframing on, key only the changed property; off, key every motion property (Key All).</summary>
    [ObservableProperty] private bool _keyChangedOnly = AppSettings.Current.KeyChangedOnly;

    partial void OnKeyChangedOnlyChanged(bool value) => SaveView(s => s.KeyChangedOnly = value);

    /// <summary>The Keyframes pane's graph of the selected row's value.</summary>
    [ObservableProperty] private bool _showKeyframeGraph = AppSettings.Current.ShowKeyframeGraph;

    partial void OnShowKeyframeGraphChanged(bool value) => SaveView(s => s.ShowKeyframeGraph = value);

    partial void OnKeyframesEnabledChanged(bool value) =>
        StatusText = value
            ? $"Keyframes on: moving, resizing or turning a clip {(KeyframesSnap ? "changes its keyframe nearest the playhead" : "sets a keyframe at the playhead")}."
            : "Keyframes off: a clip that is moved takes its whole motion with it.";

    /// <summary>Keyframes pane target: the last selected clip, else the playhead video.</summary>
    [ObservableProperty] private Layer? _keyLayer;

    // Playhead video last targeted by keyframe controls with no selection (OnPositionMsChanged).
    private Layer? _playheadVideoShown;

    /// <summary>Keyframe control target: the selection, else <see cref="PlayheadVideo"/>.</summary>
    public Layer ActiveKeyLayer => KeyLayer is { } layer && Layers.Contains(layer) && layer.IsPicture && !layer.IsEmptyTrack ? layer : PlayheadVideo;

    /// <summary>
    /// Default target with no selection (keyframes, Effects, split): lowest visible top-level video at the playhead, else the first;
    /// a hidden stand-in when none.
    /// </summary>
    public Layer PlayheadVideo => VideoViewAt(PositionMs / 1000) ?? FirstVideoView() ?? _noVideo;

    // Stand-in used when the timeline has no video.
    private readonly Layer _noVideo = new() { Kind = LayerKind.VideoFile, Name = "No Video", IsHidden = true };

    /// <summary>Selected text layer edited by Text Settings; null otherwise.</summary>
    public Layer? SelectedTextLayer => KeyLayer is { IsText: true } layer && Layers.Contains(layer) ? layer : null;

    public bool IsTextLayerSelected => SelectedTextLayer is not null;

    partial void OnKeyLayerChanged(Layer? value)
    {
        // New target (selection or added layer): the Effects pane switches away from the blur fill row.
        if (value is not null)
            BackgroundSelected = false;
        OnPropertyChanged(nameof(ActiveKeyLayer));
        OnPropertyChanged(nameof(SelectedTextLayer));
        OnPropertyChanged(nameof(IsTextLayerSelected));
        RefreshSelectedKey();
    }

    // ----- Selected keyframe and easing -----
    // The selected keyframe is the Keyframes pane target's key under the playhead (stepping or clicking a diamond selects it).

    /// <summary>The easings a keyframe can have, for the Keyframes pane's drop-down.</summary>
    public IReadOnlyList<EasingChoice> EasingChoices => EasingChoice.All;

    private (bool Has, EasingType Easing) _selectedKey;

    /// <summary>Whether the playhead is on a keyframe of the clip the Keyframes pane shows.</summary>
    public bool HasSelectedKeyframe => _selectedKey.Has;

    /// <summary>
    /// Outgoing easing of the selected keyframe (applies to placement, size and rotation); null when none. Setting it is one undo step.
    /// </summary>
    public EasingType? SelectedKeyEasing
    {
        get => _selectedKey.Has ? _selectedKey.Easing : null;
        set
        {
            // Ignore binding echoes during undo/redo and its settle.
            if (value is not { } easing || !_selectedKey.Has || easing == _selectedKey.Easing || IsRestoringTimeline)
                return;

            var layer = ActiveKeyLayer;

            // Multi-selection in the Keyframes pane: apply to all in one step.
            if (SelectedLaneKeys.Count > 1)
            {
                Checkpoint($"change the easing of {SelectedLaneKeys.Count} keyframes of {layer.Name}");
                if (SetSelectedKeysEasing(layer, easing))
                    Log($"Easing of {SelectedLaneKeys.Count} keyframes set to {EasingChoice.All.First(c => c.Type == easing).Name} ({layer.Name})");
                return;
            }

            Checkpoint($"change the easing of a keyframe of {layer.Name}");

            // Keyframe changes trigger redraw and command rebuild. The All row sets every motion property; other rows set only theirs.
            if (SetLaneEasing(layer, easing, null))
                Log($"Easing of the {LaneName(SelectedKeyLane)} keyframe at {TimeDisplay.Format(PositionMs / 1000)} set to {EasingChoice.All.First(c => c.Type == easing).Name} ({layer.Name})");
        }
    }

    /// <summary>Re-evaluates whether the playhead is on a keyframe of the selected row; raises only on change.</summary>
    /// <param name="force">Raise even if unchanged (e.g. Bezier handles edited).</param>
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

    // Persisted view toggles: side panes and keyframe diamonds.

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

    /// <summary>Playback bar controls chosen in the Views menu's Playback Bar column.</summary>
    public PlaybackControlSet PlaybackControls { get; } = new();

    /// <summary>Playback starts again at the end: of the selected cut segment, or of the whole sequence.</summary>
    [ObservableProperty] private bool _loopPlayback;

    /// <summary>The action-safe and title-safe areas are outlined over the picture.</summary>
    [ObservableProperty] private bool _showSafeMargins;

    /// <summary>On-player transform gizmo (toggled from Settings, the Canvas or the playback bar).</summary>
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
                ? "Edit Clips on Video Preview on: click a clip on the player to select it, then drag it, its corner, its round handle or its anchor."
                : "Edit Clips on Video Preview off: clips are arranged on the Canvas.";
        }
    }

    /// <summary>The player is silent (the export is not affected).</summary>
    [ObservableProperty] private bool _isPlayerMuted;

    /// <summary>Loads the playback bar control set and persists changes.</summary>
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
        RaiseTimelineChanged();
    }

    /// <summary>Persists a pane's position or size for the current mode.</summary>
    public void SavePaneLayout(Action<AppSettings> set) => SaveView(set);

    /// <summary>
    /// Persists a view setting changed in the Tabs and Panes menu and raises the settings-backed properties (ShowAdvancedPlayback,
    /// ShowCommandPreviewTab, ...).
    /// </summary>
    public void SetViewSetting(Action<AppSettings> set, params string[] properties)
    {
        SaveView(set);
        foreach (var property in properties)
            OnPropertyChanged(property);
    }

    /// <summary>Reset Panes Layout: restores the mode's default pane layout; the window then relays out.</summary>
    public void ResetPaneLayout()
    {
        SaveView(settings => settings.ResetPaneLayout());
        OnPropertyChanged(nameof(MasterTimelineHeight));
        OnPropertyChanged(nameof(TimelineAreaHeight));
        Log("Reset panes layout");
    }

    /// <summary>
    /// Track menu Auto-Duck / Voiceover toggles: marks audio to duck under voice, or marks it as the voice. Reports the resulting
    /// state, since ducking needs both a ducked sound and a voice.
    /// </summary>
    /// <param name="sound">An audio-carrying clip (stream lanes are clips on Sub-Lanes).</param>
    /// <param name="voice">True for the Voiceover / Dialogue toggle, false for Auto-Duck.</param>
    public void ToggleDucking(object sound, bool voice)
    {
        if (sound is not Layer layer)
            return;

        var name = layer.Name;
        Checkpoint($"change the ducking of {name}");
        bool on;
        if (voice)
        {
            // A voice-marked clip becomes the Auto-Captions source.
            on = layer.IsVoice = !layer.IsVoice;
            ListenToVoice(layer);
        }
        else
        {
            on = layer.AutoDuck = !layer.AutoDuck;
        }

        var voices = Layers.Count(l => l is { IsVoice: true, CarriesSound: true, IsHidden: false });
        var ducked = Layers.Count(l => l is { AutoDuck: true, IsVoice: false, CarriesSound: true, IsHidden: false });
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

        AppSettings.Update(set);
    }

    private bool _applyingKeys;
    private bool _recordingKey;
    private bool _anyKeys;

    private void RefreshAnyKeys()
    {
        _anyKeys = Layers.Any(l => l.IsAnimated);
        RefreshSelectedKey();
    }

    // Scale first: pictures scale about their anchor, so the corner derives from the size.
    private static readonly KeyProperty[] KeyOrder = [KeyProperty.Scale, KeyProperty.X, KeyProperty.Y, KeyProperty.Rotation, KeyProperty.Opacity, KeyProperty.TiltX, KeyProperty.TiltY];

    partial void OnPositionMsChanged(double value)
    {
        // With no selection, keyframe controls and Effects follow the playhead video, so crossing clips retargets them.
        if (KeyLayer is null && !_applyingKeys && !ReferenceEquals(PlayheadVideo, _playheadVideoShown))
        {
            _playheadVideoShown = PlayheadVideo;
            OnPropertyChanged(nameof(ActiveKeyLayer));
            RefreshSelectedKey();
        }

        if (!_anyKeys)
            return;

        // During playback, boxes and the layout pane update at 15 Hz (each update relayouts), snapping to the exact frame on stop
        // (OnIsPlayingChanged).
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
        // Pending background work is held during playback (Settings, Performance).
        if (!_isBackgroundWorker)
            WorkScheduler.IsPlaying = value;

        if (!value && _anyKeys)
        {
            ApplyKeyframes();
            RefreshSelectedKey();
        }
    }

    /// <summary>Set while applying keyframed values at the playhead for display; not an edit.</summary>
    public bool IsApplyingKeys => _applyingKeys;

    /// <summary>
    /// Updates animated clips' displayed values (row, Keyframes pane, layout box) at the playhead; display-only, no rebuild.
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

                // Size first about the anchor; unkeyed position follows the anchor (as the renderer does), keyed position then applies.
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
    /// Handles a manual transform: with keyframing on, writes a key at the playhead (or the nearest key when snapping); otherwise
    /// offsets a keyed clip's whole motion path by the delta.
    /// </summary>
    /// <param name="at">Clip-relative time (s) to record at instead of the playhead.</param>
    private void RecordKey(Layer layer, string? propertyName, double? at = null)
    {
        // Skipped during undo/redo and its settle: keys are restored with the step.
        if (_applyingKeys || _recordingKey || _commandDeferrals > 0 || IsRestoringTimeline || Layer.GetKeyProperty(propertyName) is not { } property || !HasSource)
            return;

        var writes = KeyframesEnabled && (layer.IsAnimated || !KeyframesSnap);
        if (!writes && !layer.HasKeys(property))
            return;

        var time = at ?? Math.Max(PositionMs / 1000 - layer.StartTime, 0);
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

    /// <summary>Adds a keyframe at the playhead with the current placement, size and rotation.</summary>
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

    /// <summary>Removes the clip's keyframe at the playhead.</summary>
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

    /// <summary>Removes all keyframes, freezing the clip at its current displayed values.</summary>
    public void ClearKeys(Layer layer)
    {
        if (!layer.IsAnimated)
            return;

        Checkpoint($"clear the keyframes of {layer.Name}");
        layer.ClearKeys();
        Log($"Cleared the keyframes of {layer.Name}");
    }
}
