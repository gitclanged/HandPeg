using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The sequence: a timeline with a clock of its own, on which the main video is one clip among the others;
// and motion, the keyframes that move, size and turn a layer as that clock runs.
public partial class MainViewModel
{
    // ----- The sequence clock -----
    // The timeline is not the main video's any more. It is as long as whatever reaches furthest along it, and
    // the main video is clips on it like any other: one to begin with, more once it has been split, each
    // moved, trimmed and deleted on its own. Everything on the timeline is in the sequence's time.

    // The length of the main video's file as the player reported it, for a file that could not be inspected.
    private double _playerMediaSeconds;

    /// <summary>How long the main video's file is, in seconds; 0 while that is not known.</summary>
    private double MainMediaSeconds => _mediaInfo?.DurationSeconds > 0 ? _mediaInfo.DurationSeconds : _playerMediaSeconds;

    // The main video's clips after its first. The first is the row itself (its StartTime, Duration and
    // MediaOffset), which is also what its keyframes are counted from.
    private readonly List<MainPiece> _mainPieces = [];

    // Worked out once for each state of the timeline, not once for each of the dozen things that ask.
    private List<MainClip>? _mainClips;

    private void InvalidateMainClips() => _mainClips = null;

    /// <summary>
    /// The main video's clips in the order they come on the sequence. Where two were put over each other the
    /// earlier one ends where the later begins. Without the Layer Engine there is no sequence to place them
    /// on: the file is the whole timeline, from its first frame to its last.
    /// </summary>
    public IReadOnlyList<MainClip> GetMainClips()
    {
        if (_mainClips is { } known)
            return known;

        var media = MainMediaSeconds;
        var clips = new List<MainClip>(_mainPieces.Count + 1);
        if (!FrameEngine)
        {
            clips.Add(new MainClip(0, 0, media, 0));
            return _mainClips = clips;
        }

        void Add(int index, double start, double duration, double offset)
        {
            var left = Math.Max(media - offset, 0);
            var length = duration > 0.001 ? (media > 0 ? Math.Min(duration, left) : duration) : left;
            if (length > 0.001 || media <= 0)
                clips.Add(new MainClip(index, start, start + length, offset));
        }

        var main = _mainVideoRow;
        Add(0, main.StartTime, main.Duration, main.MediaOffset);
        for (var i = 0; i < _mainPieces.Count; i++)
            Add(i + 1, _mainPieces[i].Start, _mainPieces[i].Duration, _mainPieces[i].Offset);

        clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = clips.Count - 2; i >= 0; i--)
        {
            if (clips[i].End > clips[i + 1].Start)
                clips[i] = clips[i] with { End = Math.Max(clips[i + 1].Start, clips[i].Start) };
            if (clips[i].End - clips[i].Start < 0.001 && media > 0)
                clips.RemoveAt(i);
        }

        return _mainClips = clips;
    }

    /// <summary>What to add to a time in the main video's file to get the time it is on the sequence, for its first clip.</summary>
    private double MainShift => FrameEngine ? _mainVideoRow.StartTime - _mainVideoRow.MediaOffset : 0;

    /// <summary>From where the main video first appears on the sequence to where it is last seen, in seconds.</summary>
    public (double Start, double End) GetMainSpan()
    {
        var clips = GetMainClips();
        return clips.Count == 0 ? (_mainVideoRow.StartTime, _mainVideoRow.StartTime) : (clips[0].Start, clips.Max(c => c.End));
    }

    /// <summary>Whether the main video has been cut into more than one clip.</summary>
    private bool IsMainSpliced => FrameEngine && _mainPieces.Count > 0;

    /// <summary>Length of the sequence in seconds: to the end of whichever clip ends last.</summary>
    public double SequenceSeconds
    {
        get
        {
            var end = GetMainSpan().End;
            if (!FrameEngine)
                return end;

            // A layer with no length of its own stays "to the end", and so does not move the end.
            foreach (var layer in Layers)
            {
                if (layer.Duration > 0.001)
                    end = Math.Max(end, layer.StartTime + layer.Duration);
            }

            return end;
        }
    }

    /// <summary>Whether the main video is the whole sequence, untouched: then the file can be played and read as it is.</summary>
    private bool IsMainWholeSequence
    {
        get
        {
            if (!FrameEngine)
                return true;
            if (_mainPieces.Count > 0)
                return false;

            var (start, end) = GetMainSpan();
            var media = MainMediaSeconds;
            return media <= 0 || (start < 0.001 && _mainVideoRow.MediaOffset < 0.001 && Math.Abs(end - media) < 0.02 && SequenceSeconds - end < 0.02);
        }
    }

    /// <summary>The player says how long what it opened is. Only needed for a file that could not be inspected.</summary>
    /// <param name="isFile">Whether it is playing the main video's file as it is, not the sequence built around it.</param>
    public void ReportPlayerDuration(double seconds, bool isFile)
    {
        if (_mediaInfo?.DurationSeconds > 0 || !isFile || seconds <= 0 || Math.Abs(seconds - _playerMediaSeconds) < 0.001)
            return;

        _playerMediaSeconds = seconds;
        _mainVideoRow.MediaDuration = seconds;
        GenerateCommand();
    }

    // The I-frames of the main video's file, and how its clips lay when they were last put along the sequence.
    private List<double> _sourceIFrames = [];
    private int _iFrameLayout;

    private int GetMainLayout()
    {
        var hash = new HashCode();
        foreach (var clip in GetMainClips())
            hash.Add(clip);
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
        InvalidateMainClips();
        var milliseconds = SequenceSeconds * 1000;
        if (milliseconds > 0 && Math.Abs(DurationMs - milliseconds) > 0.5)
        {
            DurationMs = milliseconds;
            if (PositionMs > milliseconds)
                PositionMs = milliseconds;
            UpdateIFrameMarks();
        }

        if (GetMainLayout() != _iFrameLayout)
            SetSourceIFrames(_sourceIFrames);
    }

    // ----- The main video's clips -----

    /// <summary>The clip of the main video that is on screen at a moment of the sequence, or null when none is.</summary>
    public MainClip? GetMainClipAt(double seconds)
    {
        foreach (var clip in GetMainClips())
        {
            if (seconds >= clip.Start && seconds < clip.End)
                return clip;
        }

        return null;
    }

    private void SetMainClip(int index, double start, double duration, double offset)
    {
        var main = _mainVideoRow;
        if (index == 0)
        {
            // Its keyframes are counted from where the row begins: they stay where they are on the sequence.
            if (Math.Abs(start - main.StartTime) > 0.0005)
                main.ShiftKeys(main.StartTime - start);
            (main.StartTime, main.Duration, main.MediaOffset) = (start, duration, offset);
        }
        else
        {
            _mainPieces[index - 1] = new MainPiece(start, duration, offset);
        }

        InvalidateMainClips();
    }

    private MainClip? FindMainClip(int index)
    {
        foreach (var clip in GetMainClips())
        {
            if (clip.Index == index)
                return clip;
        }

        return null;
    }

    /// <summary>
    /// The razor on the main video: the clip under the playhead becomes two clips that meet there, each of
    /// which can then be moved, trimmed or deleted on its own.
    /// </summary>
    public bool SplitMain(double seconds)
    {
        if (GetMainClipAt(seconds) is not { } clip || seconds - clip.Start < 0.05 || clip.End - seconds < 0.05)
        {
            StatusText = "The playhead is not inside the main video: move it to where the video should be split.";
            return false;
        }

        Checkpoint("split the main video");
        using (DeferCommand())
        {
            _mainPieces.Add(new MainPiece(Math.Round(seconds, 3), Math.Round(clip.End - seconds, 3), Math.Round(clip.Offset + seconds - clip.Start, 3)));
            SetMainClip(clip.Index, clip.Start, Math.Round(seconds - clip.Start, 3), clip.Offset);
            GenerateCommand();
        }

        Log($"Split clip: {_mainVideoRow.Name} at {TimeDisplay.Format(seconds)}");
        return true;
    }

    /// <summary>Moves one clip of the main video along the sequence.</summary>
    public void MoveMain(int index, double bySeconds)
    {
        if (FindMainClip(index) is not { } clip)
            return;

        bySeconds = Math.Max(bySeconds, -clip.Start);
        if (Math.Abs(bySeconds) < 0.005)
            return;

        Checkpoint("move the main video");
        using (DeferCommand())
        {
            SetMainClip(index, Math.Round(clip.Start + bySeconds, 3), index == 0 ? _mainVideoRow.Duration : _mainPieces[index - 1].Duration, clip.Offset);
            GenerateCommand();
        }

        Log($"Moved clip: {_mainVideoRow.Name} now starts at {TimeDisplay.Format(clip.Start + bySeconds)}");
    }

    /// <summary>Sets how long one clip of the main video stays, from where it starts: dragging the right edge of its block.</summary>
    public void SetMainLength(int index, double seconds)
    {
        if (FindMainClip(index) is not { } clip)
            return;

        var left = Math.Max(MainMediaSeconds - clip.Offset, 0);
        Checkpoint("trim the main video");
        using (DeferCommand())
        {
            // As long as what is left of the file, or longer, is simply "to its end".
            SetMainClip(index, clip.Start, left > 0 && seconds >= left - 0.02 ? (index == 0 ? 0 : left) : Math.Round(Math.Max(seconds, 0.1), 3), clip.Offset);
            GenerateCommand();
        }

        Log($"Trimmed clip: {_mainVideoRow.Name} now ends at {TimeDisplay.Format(FindMainClip(index)?.End ?? 0)}");
    }

    /// <summary>Trims one clip of the main video to the playhead: its start (what came before goes) or its end.</summary>
    public bool TrimMain(int index, bool start, double seconds)
    {
        if (FindMainClip(index) is not { } clip || seconds <= clip.Start + 0.05 || seconds >= clip.End - 0.05)
        {
            StatusText = "Put the playhead inside the clip of the main video to trim it there.";
            return false;
        }

        Checkpoint("trim the main video");
        using (DeferCommand())
        {
            if (start)
                SetMainClip(index, Math.Round(seconds, 3), Math.Round(clip.End - seconds, 3), Math.Round(clip.Offset + seconds - clip.Start, 3));
            else
                SetMainClip(index, clip.Start, Math.Round(seconds - clip.Start, 3), clip.Offset);
            GenerateCommand();
        }

        Log($"Trimmed clip: {_mainVideoRow.Name} now {(start ? "starts" : "ends")} at {TimeDisplay.Format(seconds)}");
        return true;
    }

    /// <summary>Puts the main video back as a newly opened one is: one clip, whole, at the start of the timeline.</summary>
    public void ResetMainTiming()
    {
        var main = _mainVideoRow;
        if (_mainPieces.Count == 0 && main.StartTime < 0.001 && main.MediaOffset < 0.001 && main.Duration < 0.001)
            return;

        Checkpoint("reset the main video's timing");
        using (DeferCommand())
        {
            _mainPieces.Clear();
            SetMainClip(0, 0, 0, 0);
            GenerateCommand();
        }

        StatusText = $"{main.Name} is one clip again, whole, at the start of the timeline.";
    }

    // ----- The recycle bin -----
    // A clip that is deleted is not thrown away. It is taken off the timeline (so nothing of it is rendered)
    // and kept, as it was, in the bin; from there it can be put back where it came from.

    /// <summary>The deleted clips, the latest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<BinItem> RecycleBin { get; } = [];

    public bool HasDeleted => RecycleBin.Count > 0;

    /// <summary>What the trash can says beside it: how many clips are in the bin, or nothing.</summary>
    public string RecycleBinText => RecycleBin.Count > 0 ? RecycleBin.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    private void AddToBin(DeletedClip clip, string name, double start, double end, System.Windows.Media.ImageSource? preview)
    {
        RecycleBin.Insert(0, new BinItem(clip, name, $"{TimeDisplay.Format(start)} to {TimeDisplay.Format(end)}", preview));
        OnPropertyChanged(nameof(HasDeleted));
        OnPropertyChanged(nameof(RecycleBinText));
    }

    private void SetBin(IEnumerable<DeletedClip>? clips)
    {
        RecycleBin.Clear();
        foreach (var clip in clips ?? [])
        {
            if (clip.Layer is { } layer)
            {
                var end = layer.Duration > 0.001 ? layer.StartTime + layer.Duration : SequenceSeconds;
                RecycleBin.Add(new BinItem(clip, layer.Name, $"{TimeDisplay.Format(layer.StartTime)} to {TimeDisplay.Format(end)}", GetLayerPicture(layer.ImagePath)));
            }
            else if (clip.Main is { } piece)
            {
                RecycleBin.Add(new BinItem(clip, _mainVideoRow.Name, $"{TimeDisplay.Format(piece.Start)} to {TimeDisplay.Format(piece.Start + piece.Duration)}", null));
            }
        }

        OnPropertyChanged(nameof(HasDeleted));
        OnPropertyChanged(nameof(RecycleBinText));
    }

    /// <summary>A picture already made of a layer's file this session: frames of its video, or failing that nothing.</summary>
    private System.Windows.Media.ImageSource? GetLayerPicture(string path) => _layerFilmstrips.GetValueOrDefault(path);

    /// <summary>Deletes a layer's clip: it leaves the timeline and waits in the recycle bin.</summary>
    private void MoveToBin(Layer clip)
    {
        var index = Layers.IndexOf(clip);
        if (index < 0)
            return;

        var state = clip.ToState();
        state.IsDeleted = true;
        var (start, end) = clip.GetSpan(SequenceSeconds);
        var preview = clip.Filmstrip ?? clip.Waveform;
        Detach(clip);
        AddToBin(new DeletedClip(state, null, index), clip.Name, start, end, preview);
    }

    /// <summary>Deletes one clip of the main video. Its last clip cannot go: the main video is hidden instead.</summary>
    public bool DeleteMainClip(int index)
    {
        if (FindMainClip(index) is not { } clip)
            return false;

        if (GetMainClips().Count < 2)
        {
            StatusText = $"That is all there is of {_mainVideoRow.Name}. To leave its picture out, hide it (right-click its row); to shorten it, trim it.";
            return false;
        }

        Checkpoint("delete a clip of the main video");
        using (DeferCommand())
        {
            var main = _mainVideoRow;
            var deleted = new MainPiece(clip.Start, clip.End - clip.Start, clip.Offset);
            if (index == 0)
            {
                // The row's own clip goes: the next clip along becomes the row's.
                var next = _mainPieces.MinBy(p => p.Start)!;
                _mainPieces.Remove(next);
                SetMainClip(0, next.Start, next.Duration, next.Offset);
            }
            else
            {
                _mainPieces.RemoveAt(index - 1);
            }

            InvalidateMainClips();
            AddToBin(new DeletedClip(null, deleted, 0), main.Name, clip.Start, clip.End, null);
            GenerateCommand();
        }

        Log($"Deleted clip of {_mainVideoRow.Name} (in the recycle bin; Ctrl+Z brings it back)");
        return true;
    }

    /// <summary>Puts a deleted clip back on the timeline where it was.</summary>
    public void RestoreFromBin(BinItem item)
    {
        if (!RecycleBin.Contains(item))
            return;

        Checkpoint($"restore {item.Name}");
        using (DeferCommand())
        {
            RecycleBin.Remove(item);
            if (item.Clip.Layer is { } state)
            {
                state.IsDeleted = false;
                var layer = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
                Hook(layer);
                InsertLayer(item.Clip.Index, layer);
                _ = LoadLayerPicturesAsync(layer);
            }
            else if (item.Clip.Main is { } piece)
            {
                _mainPieces.Add(piece);
                InvalidateMainClips();
            }

            OnPropertyChanged(nameof(HasDeleted));
            OnPropertyChanged(nameof(RecycleBinText));
            GenerateCommand();
        }

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

    // The main video is placed about its middle (zoom and offsets), which is what keeps it centred when the
    // frame changes shape. Its row shows the same place the way every other layer's does, as a corner and a
    // width, and the two are kept in step here.
    private bool _syncingMainRow;

    /// <summary>Writes the main video's place into its row, from the zoom and offsets.</summary>
    /// <param name="moved">Whether it was moved by hand (dragged in the layout pane, say), which is something a keyframe may have to record.</param>
    private void SyncMainRow(bool moved = false)
    {
        if (_syncingMainRow || _applyingKeys)
            return;

        var main = _mainVideoRow;
        var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
        var (x, y, width, _) = GetCenterRect();
        var before = (main.SizeWidth, main.PositionX, main.PositionY);
        _syncingMainRow = true;
        try
        {
            (main.SizeWidth, main.PositionX, main.PositionY) = (width / frameWidth, x / frameWidth, y / frameHeight);
        }
        finally
        {
            _syncingMainRow = false;
        }

        // Not while a preset or a project is being put in place: that is not the user moving it.
        if (!moved || _commandDeferrals > 0)
            return;

        if (main.SizeWidth != before.SizeWidth)
            RecordKey(main, nameof(Layer.SizeWidth));
        if (main.PositionX != before.PositionX)
            RecordKey(main, nameof(Layer.PositionX));
        if (main.PositionY != before.PositionY)
            RecordKey(main, nameof(Layer.PositionY));
    }

    partial void OnCenterZoomChanged(double value) => SyncMainRow(moved: true);
    partial void OnCenterOffsetXChanged(double value) => SyncMainRow(moved: true);
    partial void OnCenterOffsetYChanged(double value) => SyncMainRow(moved: true);

    private void OnMainRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingMainRow)
            return;

        // A picture of the clip arriving is something to draw, and nothing to do with the command.
        if (e.PropertyName is nameof(Layer.Waveform) or nameof(Layer.Filmstrip))
        {
            TimelineChanged?.Invoke();
            return;
        }

        var main = _mainVideoRow;
        if (Layer.GetKeyProperty(e.PropertyName) is { } property and not KeyProperty.Rotation)
        {
            // Set in the row, or by a keyframe: carried over to the zoom and offsets.
            _syncingMainRow = true;
            try
            {
                var (frameWidth, frameHeight) = ((double)FrameWidth, (double)FrameHeight);
                var (sourceWidth, sourceHeight) = GetCroppedSourceSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
                var zoom = Math.Clamp(main.SizeWidth, SmallestCenterZoom, LargestCenterZoom);
                if (property == KeyProperty.Scale)
                    CenterZoom = zoom;
                else if (property == KeyProperty.X)
                    CenterOffsetX = Math.Clamp((main.PositionX - (1 - zoom) / 2) * 100, -100, 100);
                else
                    CenterOffsetY = Math.Clamp((main.PositionY - (1 - zoom * frameWidth * sourceHeight / sourceWidth / frameHeight) / 2) * 100, -100, 100);
            }
            finally
            {
                _syncingMainRow = false;
            }

            // Resized, it grows about its middle: its corner has moved, and so have the keyframes that hold it.
            if (property == KeyProperty.Scale && !_applyingKeys)
            {
                SyncMainRow();
                RecordKey(main, nameof(Layer.PositionX));
                RecordKey(main, nameof(Layer.PositionY));
            }
        }

        if (_applyingKeys)
            return;

        if (e.PropertyName == Layer.KeysChanged)
            RefreshAnyKeys();
        RecordKey(main, e.PropertyName);
        TimelineChanged?.Invoke();
        GenerateCommand();
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

    partial void OnKeyframesEnabledChanged(bool value) =>
        StatusText = value
            ? $"Keyframes on: moving, resizing or turning a clip {(KeyframesSnap ? "changes its keyframe nearest the playhead" : "sets a keyframe at the playhead")}."
            : "Keyframes off: a clip that is moved takes its whole motion with it.";

    /// <summary>The clip whose keyframes the Keyframes pane shows and the keyframe buttons work on: the one last selected, or the main video.</summary>
    [ObservableProperty] private Layer? _keyLayer;

    /// <summary>The clip the keyframe controls work on.</summary>
    public Layer ActiveKeyLayer => KeyLayer is { } layer && (layer.IsMainVideo || Layers.Contains(layer)) && layer.IsPicture ? layer : _mainVideoRow;

    /// <summary>The text layer that is selected, whose words and font the Text Settings panel edits; null when what is selected is not one.</summary>
    public Layer? SelectedTextLayer => KeyLayer is { IsText: true } layer && Layers.Contains(layer) ? layer : null;

    public bool IsTextLayerSelected => IsEditorMode && SelectedTextLayer is not null;

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
            var time = PositionMs / 1000 - layer.StartTime;
            Checkpoint($"change the easing of a keyframe of {layer.Name}");

            // The keyframes changing is what redraws the clip and writes the command again.
            if (layer.SetEasingAt(time, easing))
                Log($"Easing of the keyframe at {TimeDisplay.Format(PositionMs / 1000)} set to {EasingChoice.All.First(c => c.Type == easing).Name} ({layer.Name})");
            RefreshSelectedKey();
        }
    }

    /// <summary>Looks again at whether the playhead is on a keyframe, and says so only when that has changed.</summary>
    private void RefreshSelectedKey()
    {
        var layer = ActiveKeyLayer;
        var easing = HasSource && layer.IsAnimated ? layer.GetEasingAt(PositionMs / 1000 - layer.StartTime) : null;
        var now = (easing is not null, easing ?? EasingType.Linear);
        if (now == _selectedKey)
            return;

        _selectedKey = now;
        OnPropertyChanged(nameof(HasSelectedKeyframe));
        OnPropertyChanged(nameof(SelectedKeyEasing));
    }

    // Which panes are open beside the player, and whether clips show their keyframes: per mode, and remembered.

    /// <summary>The Cut Segments pane beside the player.</summary>
    [ObservableProperty] private bool _showCutSegmentsPane = AppSettings.Current.ShowCutSegmentsPane;

    /// <summary>The Keyframes pane beside the player.</summary>
    [ObservableProperty] private bool _showKeyframesPane = AppSettings.Current.ShowKeyframesPane;

    /// <summary>The Source pane at the top of the side column. Editor Mode's: Encoder Mode has the source in its top bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourcePaneVisible))]
    private bool _showSourcePane = AppSettings.Current.ShowSourcePane;

    /// <summary>Whether the Source pane is on screen: open, and in the mode that has it.</summary>
    public bool IsSourcePaneVisible => IsEditorMode && ShowSourcePane;

    /// <summary>Whether a clip's block on the timeline shows a diamond at each of its keyframes.</summary>
    [ObservableProperty] private bool _showClipKeyframes = AppSettings.Current.ShowClipKeyframes;

    partial void OnShowCutSegmentsPaneChanged(bool value) => SaveView(s => s.ShowCutSegmentsPane = value);
    partial void OnShowKeyframesPaneChanged(bool value) => SaveView(s => s.ShowKeyframesPane = value);
    partial void OnShowSourcePaneChanged(bool value) => SaveView(s => s.ShowSourcePane = value);

    partial void OnShowClipKeyframesChanged(bool value)
    {
        SaveView(s => s.ShowClipKeyframes = value);
        TimelineChanged?.Invoke();
    }

    /// <summary>Remembers where a pane was put or how large it was made, for the mode in use.</summary>
    public void SavePaneLayout(Action<AppSettings> set) => SaveView(set);

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
        _anyKeys = _mainVideoRow.IsAnimated || Layers.Any(l => l.IsAnimated);
        RefreshSelectedKey();
    }

    // The order matters for the main video, whose corner is worked out from its size.
    private static readonly KeyProperty[] KeyOrder = [KeyProperty.Scale, KeyProperty.X, KeyProperty.Y, KeyProperty.Rotation];

    partial void OnPositionMsChanged(double value)
    {
        if (_anyKeys)
        {
            ApplyKeyframes();
            RefreshSelectedKey();
        }
    }

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
            Apply(_mainVideoRow);
            foreach (var layer in Layers)
                Apply(layer);

            void Apply(Layer layer)
            {
                if (!layer.IsAnimated)
                    return;

                var time = seconds - layer.StartTime;
                layer.FromCorner(() =>
                {
                    foreach (var property in KeyOrder)
                    {
                        if (layer.HasKeys(property))
                            layer.SetValue(property, layer.Evaluate(property, time));
                    }
                });
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
    /// no clip of the main video is, the clock still needs frames to run on; stretches of the same file stand
    /// in, and Live Preview leaves the main video's picture out of them.
    /// </summary>
    public string PlayerSource
    {
        get
        {
            // The proxy, where there is one: the player shows that; everything that is exported reads LocalMediaPath.
            var path = PreviewMediaPath;
            if (!HasSource || IsMainWholeSequence)
                return path;

            var media = MainMediaSeconds;
            var name = $"%{Encoding.UTF8.GetByteCount(path)}%{path}";
            var parts = new StringBuilder("edl://");
            void Part(double start, double length) => parts.Append(parts.Length > 6 ? ";" : "").Append(name).Append(",start=").Append(Number(start)).Append(",length=").Append(Number(length));
            void StandIn(double seconds)
            {
                for (; seconds > 0.001; seconds -= media)
                    Part(0, Math.Min(seconds, media));
            }

            var reached = 0.0;
            foreach (var clip in GetMainClips())
            {
                StandIn(clip.Start - reached);
                Part(clip.Offset, clip.End - clip.Start);
                reached = clip.End;
            }

            StandIn(SequenceSeconds - reached);
            return parts.ToString();
        }
    }

    /// <summary>The audio filter that goes with <see cref="PlayerSource"/>: silence where the main video is not there. Empty for none.</summary>
    public string PlayerAudioFilter =>
        !HasSource || IsMainWholeSequence ? "" : $"lavfi=[volume=0:enable='not({FilterGraphBuilder.BuildMainGate(GetMainClips(), 0, SequenceSeconds)})']";
}
