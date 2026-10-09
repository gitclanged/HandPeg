using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;

namespace HandPegApp.ViewModels;

// The sequence: a timeline with a clock of its own, on which the main video is one clip among the others;
// and motion, the keyframes that move, size and turn a layer as that clock runs.
public partial class MainViewModel
{
    // ----- The sequence clock -----
    // The timeline is not the main video's any more. It is as long as whatever reaches furthest along it, and
    // the main video sits on it where it was put: later than the start, trimmed at either end, or not there
    // at all for a stretch. Everything on the timeline (cuts, layers, the playhead) is in the sequence's time.

    // The length of the main video's file as the player reported it, for a file that could not be inspected.
    private double _playerMediaSeconds;

    /// <summary>How long the main video's file is, in seconds; 0 while that is not known.</summary>
    private double MainMediaSeconds => _mediaInfo?.DurationSeconds > 0 ? _mediaInfo.DurationSeconds : _playerMediaSeconds;

    /// <summary>What to add to a time in the main video's file to get the time it is on the sequence.</summary>
    private double MainShift => FrameEngine ? _mainVideoRow.StartTime - _mainVideoRow.MediaOffset : 0;

    /// <summary>
    /// When the main video is on the sequence, in seconds. Without the Layer Engine there is no sequence
    /// to place it on: it is the whole timeline, from its first frame to its last.
    /// </summary>
    public (double Start, double End) GetMainSpan()
    {
        var media = MainMediaSeconds;
        if (!FrameEngine)
            return (0, media);

        var main = _mainVideoRow;
        var left = Math.Max(media - main.MediaOffset, 0);
        var length = main.Duration > 0.001 ? (media > 0 ? Math.Min(main.Duration, left) : main.Duration) : left;
        return (main.StartTime, main.StartTime + length);
    }

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

    // Where the main video was on the sequence when the keyframe index was last laid along it.
    private double _keyframeShift;
    private List<double> _sourceKeyframes = [];

    /// <summary>Takes the keyframe index of the main video's file, and lays it along the sequence.</summary>
    private void SetSourceKeyframes(List<double> keyframes)
    {
        _sourceKeyframes = keyframes;
        _keyframeShift = MainShift;
        Keyframes = Math.Abs(_keyframeShift) < 0.0005 ? keyframes : keyframes.ConvertAll(k => k + _keyframeShift);
        UpdateKeyframeMarks();
    }

    /// <summary>Brings the length of the timeline, and what is drawn along it, up to date with the clips.</summary>
    private void RefreshSequence()
    {
        var milliseconds = SequenceSeconds * 1000;
        if (milliseconds > 0 && Math.Abs(DurationMs - milliseconds) > 0.5)
        {
            DurationMs = milliseconds;
            if (PositionMs > milliseconds)
                PositionMs = milliseconds;
            UpdateKeyframeMarks();
        }

        if (Math.Abs(MainShift - _keyframeShift) > 0.0005)
            SetSourceKeyframes(_sourceKeyframes);
    }

    // ----- The main video as a clip -----

    /// <summary>
    /// Moves the main video along the sequence, and its cut segments with it: they mark stretches of its
    /// picture, and stay on them.
    /// </summary>
    public void MoveMain(double bySeconds)
    {
        var main = _mainVideoRow;
        bySeconds = Math.Max(bySeconds, -main.StartTime);
        if (Math.Abs(bySeconds) < 0.005)
            return;

        Checkpoint("move the main video");
        using (DeferCommand())
        {
            main.StartTime = Math.Round(main.StartTime + bySeconds, 3);
            var by = TimeSpan.FromSeconds(bySeconds);
            for (var i = 0; i < Segments.Count; i++)
                Segments[i] = new CutSegment(Segments[i].Start + by, Segments[i].End + by) { IsSkipped = Segments[i].IsSkipped };
            PendingStartMs = null;
        }

        StatusText = $"The main video now starts at {TimeDisplay.Format(main.StartTime)} on the timeline.";
    }

    /// <summary>Sets how long the main video stays, from where it starts: dragging the right edge of its block.</summary>
    public void SetMainLength(double seconds)
    {
        var main = _mainVideoRow;
        var left = Math.Max(MainMediaSeconds - main.MediaOffset, 0);
        Checkpoint("trim the main video");

        // As long as what is left of the file, or longer, is simply "to its end".
        main.Duration = left > 0 && seconds >= left - 0.02 ? 0 : Math.Round(Math.Max(seconds, 0.1), 3);
        StatusText = $"The main video now ends at {TimeDisplay.Format(GetMainSpan().End)} on the timeline.";
    }

    /// <summary>Puts the main video back where a newly opened one is: at the start of the timeline, whole.</summary>
    public void ResetMainTiming()
    {
        var main = _mainVideoRow;
        if (main.StartTime < 0.001 && main.MediaOffset < 0.001 && main.Duration < 0.001)
            return;

        Checkpoint("reset the main video's timing");
        var back = main.StartTime;
        using (DeferCommand())
        {
            (main.MediaOffset, main.Duration) = (0, 0);
            main.StartTime = 0;
            var by = TimeSpan.FromSeconds(-back);
            for (var i = 0; i < Segments.Count; i++)
                Segments[i] = new CutSegment(Segments[i].Start + by < TimeSpan.Zero ? TimeSpan.Zero : Segments[i].Start + by, Segments[i].End + by) { IsSkipped = Segments[i].IsSkipped };
        }

        StatusText = "The main video is whole again, at the start of the timeline.";
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

    /// <summary>
    /// Auto-keyframing: moving, sizing or turning a layer sets a keyframe at the playhead, starting a motion
    /// where there was none.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAutoKeying))] private bool _autoKeyGenerate;

    /// <summary>Moving a layer changes the keyframe nearest the playhead instead of making a new one.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAutoKeying))] private bool _autoKeySnap;

    /// <summary>Whether either of the two is on, for the stopwatch to show.</summary>
    public bool IsAutoKeying => AutoKeyGenerate || AutoKeySnap;

    partial void OnAutoKeyGenerateChanged(bool value)
    {
        if (value)
            AutoKeySnap = false;
        StatusText = value
            ? "Auto-keyframing: moving, resizing or turning a layer now sets a keyframe at the playhead."
            : "Auto-keyframing is off: a layer that is moved takes its whole motion with it.";
    }

    partial void OnAutoKeySnapChanged(bool value)
    {
        if (value)
        {
            AutoKeyGenerate = false;
            StatusText = "Snap to keyframe: moving a layer changes its keyframe nearest the playhead, and makes no new ones.";
        }
    }

    private bool _applyingKeys;
    private bool _recordingKey;
    private bool _anyKeys;

    private void RefreshAnyKeys() => _anyKeys = _mainVideoRow.IsAnimated || Layers.Any(l => l.IsAnimated);

    // The order matters for the main video, whose corner is worked out from its size.
    private static readonly KeyProperty[] KeyOrder = [KeyProperty.Scale, KeyProperty.X, KeyProperty.Y, KeyProperty.Rotation];

    partial void OnPositionMsChanged(double value)
    {
        if (_anyKeys)
            ApplyKeyframes();
    }

    /// <summary>
    /// Shows every animated layer as it is at the playhead: its row, and its box in the layout pane, follow
    /// the motion. Nothing is changed by it, so nothing is rebuilt.
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
    /// A layer was moved, sized or turned by hand. What becomes of that depends on the stopwatch: a new
    /// keyframe at the playhead, a new value for the nearest keyframe, or (with neither) the whole motion
    /// moved by as much. A property with no keyframes is simply set, unless keyframes are being generated.
    /// </summary>
    private void RecordKey(Layer layer, string? propertyName)
    {
        if (_applyingKeys || _recordingKey || Layer.GetKeyProperty(propertyName) is not { } property)
            return;
        if (!layer.HasKeys(property) && !(AutoKeyGenerate && HasSource))
            return;

        var time = Math.Max(PositionMs / 1000 - layer.StartTime, 0);
        var value = layer.GetValue(property);
        _recordingKey = true;
        try
        {
            if (AutoKeyGenerate)
                layer.SetKey(property, time, value);
            else if (AutoKeySnap && layer.GetNearestKey(property, time) is { } nearest)
                layer.SetKey(property, nearest.Time, value);
            else
                layer.OffsetKeys(property, value - layer.Evaluate(property, time));
        }
        finally
        {
            _recordingKey = false;
        }
    }

    /// <summary>
    /// The diamond beside a property: sets a keyframe at the playhead, with the value the property has there,
    /// or takes away the one that is there. The first keyframe starts the property's motion; taking away the
    /// last one ends it.
    /// </summary>
    public void ToggleKey(Layer layer, KeyProperty property)
    {
        var time = Math.Max(PositionMs / 1000 - layer.StartTime, 0);
        var what = property switch { KeyProperty.X => "X", KeyProperty.Y => "Y", KeyProperty.Scale => "size", _ => "rotation" };
        Checkpoint($"keyframe the {what} of {layer.Name}");

        if (layer.RemoveKey(property, time))
        {
            StatusText = layer.HasKeys(property)
                ? $"Removed the {what} keyframe of {layer.Name} at {TimeDisplay.Format(PositionMs / 1000)}."
                : $"The {what} of {layer.Name} is no longer animated.";
        }
        else
        {
            var first = !layer.HasKeys(property);
            layer.SetKey(property, time, layer.Evaluate(property, time));

            // A size that changes is held about the layer's middle by its corner moving with it.
            if (property == KeyProperty.Scale)
            {
                foreach (var corner in new[] { KeyProperty.X, KeyProperty.Y })
                    layer.SetKey(corner, time, layer.Evaluate(corner, time));
            }

            StatusText = first
                ? $"The {what} of {layer.Name} has its first keyframe, at {TimeDisplay.Format(PositionMs / 1000)}. Move the playhead and set another (or switch on the stopwatch and just move the layer) to make it move."
                : $"Set a {what} keyframe for {layer.Name} at {TimeDisplay.Format(PositionMs / 1000)}.";
        }

        ApplyKeyframes();
    }

    /// <summary>Takes every keyframe off a clip: it stays as it is shown now.</summary>
    public void ClearKeys(Layer layer)
    {
        if (!layer.IsAnimated)
            return;

        Checkpoint($"clear the keyframes of {layer.Name}");
        layer.ClearKeys();
        StatusText = $"{layer.Name} is no longer animated: it stays where it is now.";
    }

    // ----- Motion as FFmpeg expressions -----

    /// <summary>
    /// A property's keyframes as an expression of t, the time on the picture being composed: the first value,
    /// plus for every stretch between two keyframes its change times how far through that stretch t is, held
    /// to 0..1. That is the straight line from each keyframe to the next, level before the first and after
    /// the last, in one flat sum: one term per keyframe, and nothing nested for the parser to descend into.
    /// </summary>
    /// <param name="clipStart">Where the clip begins on that picture's clock, in seconds.</param>
    /// <param name="scale">What a value is multiplied by: the frame's width or height in pixels, or 1.</param>
    private static string KeyExpression(IReadOnlyList<Keyframe> keys, double clipStart, double scale)
    {
        var text = new StringBuilder(16 + keys.Count * 36);
        text.Append(Number(keys[0].Value * scale));
        for (var i = 1; i < keys.Count; i++)
        {
            var change = (keys[i].Value - keys[i - 1].Value) * scale;
            if (Math.Abs(change) < 0.0005)
                continue;

            var (from, length) = (clipStart + keys[i - 1].Time, keys[i].Time - keys[i - 1].Time);
            text.Append(change < 0 ? '-' : '+').Append(Number(Math.Abs(change)));

            // Two keyframes at the same moment are a jump.
            if (length < 0.001)
                text.Append("*gte(t,").Append(Number(from)).Append(')');
            else
                text.Append("*clip((t").Append(from < 0 ? '+' : '-').Append(Number(Math.Abs(from))).Append(")/").Append(Number(length)).Append(",0,1)");
        }

        return text.ToString();
    }

    /// <summary>How a layer moves while a stretch of the picture is composed; every part is null for a layer that keeps still in that respect.</summary>
    /// <param name="X">Its left edge in pixels, as an expression.</param>
    /// <param name="Width">Its width in pixels, as an expression.</param>
    /// <param name="Angle">Degrees it is turned, as an expression.</param>
    /// <param name="LargestWidth">The widest it gets, as a fraction of the frame: what it is built at, so that it is only ever scaled down.</param>
    private readonly record struct Motion(string? X, string? Y, string? Width, string? Angle, double LargestWidth)
    {
        public bool Any => X is not null || Y is not null || Width is not null || Angle is not null;

        /// <summary>Whether the picture itself changes from frame to frame, not only where it is laid.</summary>
        public bool Reshapes => Width is not null || Angle is not null;
    }

    /// <param name="from">Where on the sequence the stretch being composed begins: its clock starts there.</param>
    private static Motion GetMotion(Layer layer, int frameWidth, int frameHeight, double from)
    {
        if (!layer.IsAnimated)
            return default;

        var start = layer.StartTime - from;
        string? Of(KeyProperty property, double scale) => layer.HasKeys(property) ? KeyExpression(layer.GetKeys(property), start, scale) : null;
        return new Motion(
            Of(KeyProperty.X, frameWidth), Of(KeyProperty.Y, frameHeight), Of(KeyProperty.Scale, frameWidth), layer.HasFilters ? Of(KeyProperty.Rotation, 1) : null,
            layer.HasKeys(KeyProperty.Scale) ? layer.GetKeys(KeyProperty.Scale).Max(k => k.Value) : 0);
    }

    // ----- What the player plays -----

    /// <summary>
    /// What the player opens: the main video's file while that is the whole sequence, and otherwise the
    /// sequence written out for it (mpv's EDL), so that its clock is the sequence's. Before and after the main
    /// video the clock still needs frames to run on; stretches of the same file stand in, and Live Preview
    /// leaves the main video's picture out of them.
    /// </summary>
    public string PlayerSource
    {
        get
        {
            var path = LocalMediaPath;
            if (!HasSource || IsMainWholeSequence)
                return path;

            var (start, end) = GetMainSpan();
            var media = MainMediaSeconds;
            var name = $"%{Encoding.UTF8.GetByteCount(path)}%{path}";
            var parts = new List<string>();

            void StandIn(double seconds)
            {
                for (; seconds > 0.001; seconds -= media)
                    parts.Add($"{name},start=0,length={Number(Math.Min(seconds, media))}");
            }

            StandIn(start);
            if (end - start > 0.001)
                parts.Add($"{name},start={Number(_mainVideoRow.MediaOffset)},length={Number(end - start)}");
            StandIn(SequenceSeconds - end);
            return "edl://" + string.Join(";", parts);
        }
    }

    /// <summary>The audio filter that goes with <see cref="PlayerSource"/>: silence where the main video is not there. Empty for none.</summary>
    public string PlayerAudioFilter
    {
        get
        {
            if (!HasSource || IsMainWholeSequence)
                return "";

            var (start, end) = GetMainSpan();
            return $"lavfi=[volume=0:enable='lt(t,{Number(start)})+gte(t,{Number(end)})']";
        }
    }
}
