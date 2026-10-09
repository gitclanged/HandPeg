using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>The timeline as Undo keeps it: the cuts, the layers and what was done to the audio. Nothing else, and no pictures.</summary>
public sealed record TimelineSnapshot(
    List<SegmentState> Segments, List<LayerState> Layers, int MainVideoIndex, List<AudioEditState> Audio, bool AudioLinked,
    LayerState? MainLayer = null, bool BackgroundHidden = false, List<MainPiece>? MainPieces = null, List<DeletedClip>? Bin = null);

/// <summary>What was done to one audio track on the timeline: how far it was slipped, and its pieces.</summary>
public sealed record AudioEditState(int Index, double Offset, List<AudioPiece> Pieces);

/// <summary>What Remove Dead Air does with the silences it finds.</summary>
public enum DeadAirMode
{
    /// <summary>Encoder Mode's way: the cut list becomes everything between the silences.</summary>
    Classic,

    /// <summary>The silences are cut out as pieces of their own and marked as skipped: still there, greyed out, and restorable.</summary>
    SplitAndMark,

    /// <summary>The silences are cut out and removed.</summary>
    Delete,
}

// Editing on the timeline: undo and redo, deleting and trimming whatever is selected, audio that can be
// slipped and cut on its own, and the question of whether anything has changed since it was last saved.
public partial class MainViewModel
{
    // ----- Undo and redo -----
    // Before anything on the timeline is changed, the timeline as it is goes onto a stack, as a small piece
    // of JSON: the cuts, the layers, the audio edits. Twenty deep. Undo puts the last one back.

    private const int UndoDepth = 20;
    private readonly List<(string What, string State)> _undo = [];
    private readonly List<(string What, string State)> _redo = [];

    private string CaptureTimeline() => JsonSerializer.Serialize(new TimelineSnapshot(
        Segments.Select(s => new SegmentState(s.Start.TotalMilliseconds, s.End.TotalMilliseconds, s.IsSkipped)).ToList(),
        Layers.Select(l => l.ToState()).ToList(),
        MainVideoIndex,
        AudioTracks.Select(t => new AudioEditState(t.Index, t.OffsetSeconds, [.. t.Pieces])).ToList(),
        AudioLinked, _mainVideoRow.ToState(), _backgroundRow.IsHidden, [.. _mainPieces], RecycleBin.Select(i => i.Clip).ToList()));

    /// <summary>Call before changing the timeline: what it looks like now can then be brought back with Undo.</summary>
    /// <param name="what">The change about to be made, in a word or two, for the status bar.</param>
    public void Checkpoint(string what)
    {
        if (_isBackgroundWorker)
            return;

        _undo.Add((what, CaptureTimeline()));
        if (_undo.Count > UndoDepth)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo() => Step(_undo, _redo, "Undid");

    public void Redo() => Step(_redo, _undo, "Redid");

    private void Step(List<(string What, string State)> from, List<(string What, string State)> to, string verb)
    {
        if (from.Count == 0)
        {
            StatusText = verb == "Undid" ? "Nothing to undo." : "Nothing to redo.";
            return;
        }

        var (what, state) = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add((what, CaptureTimeline()));
        RestoreTimeline(state);
        Log($"{verb}: {what}");
    }

    private void RestoreTimeline(string state)
    {
        if (JsonSerializer.Deserialize<TimelineSnapshot>(state) is not { } snapshot)
            return;

        // One rebuild of the command for the whole step, not one for every clip that is put back.
        using var whole = DeferCommand();
        Segments.Clear();
        foreach (var segment in snapshot.Segments)
            Segments.Add(new CutSegment(TimeSpan.FromMilliseconds(segment.StartMs), TimeSpan.FromMilliseconds(segment.EndMs)) { IsSkipped = segment.Skipped });

        SetLayers(snapshot.Layers);
        _mainVideoRow.ApplyLook(snapshot.MainLayer);
        _mainVideoRow.ApplyTiming(snapshot.MainLayer);
        _mainPieces.Clear();
        _mainPieces.AddRange(snapshot.MainPieces ?? []);
        InvalidateMainClips();
        SetBin(snapshot.Bin);
        _backgroundRow.IsHidden = snapshot.BackgroundHidden;
        MainVideoIndex = Math.Clamp(snapshot.MainVideoIndex, 0, Layers.Count);
        foreach (var edit in snapshot.Audio)
        {
            if (AudioTracks.FirstOrDefault(t => t.Index == edit.Index) is { } track)
                track.SetEdits(edit.Offset, edit.Pieces);
        }

        AudioLinked = snapshot.AudioLinked;
        RefreshLayerRows();
        GenerateCommand();
        ApplyKeyframes();
    }

    // ----- Whatever is selected -----

    /// <summary>Removes the selected clip: a layer goes, a cut segment goes (what it kept is no longer kept).</summary>
    public bool DeleteClip(object? clip)
    {
        switch (clip)
        {
            case Layer { IsRemovable: true } layer when Layers.Contains(layer):
                Checkpoint($"delete {layer.Name}");
                MoveToBin(layer);
                Log($"Deleted clip: {layer.Name} (in the recycle bin; Ctrl+Z brings it back)");
                return true;

            case CutSegment segment when Segments.Contains(segment):
                Checkpoint("delete a segment");
                Segments.Remove(segment);
                Log($"Deleted segment {segment.Display}");
                return true;

            default:
                StatusText = "Select a layer or a cut segment to delete: click its block on the Layers tab.";
                return false;
        }
    }

    /// <summary>
    /// Sets how fast a clip runs. The clip plays the same stretch of its file as before, so its block on the
    /// timeline is as much shorter or longer as it is faster or slower.
    /// </summary>
    public void SetClipSpeed(Layer clip, double speed)
    {
        speed = Math.Round(Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed), 3);
        if (!clip.HasSpeed || Math.Abs(clip.Speed - speed) < 0.0005)
            return;

        Checkpoint($"change the speed of {clip.Name}");
        using (DeferCommand())
        {
            clip.SetSpeed(speed);
            GenerateCommand();
        }

        var (from, to) = clip.GetSpan(SequenceSeconds);
        Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Set speed of {clip.Name} to {speed:0.##}x: it now runs {TimeDisplay.Format(from)} to {TimeDisplay.Format(to)}"));
    }

    /// <summary>Trims the selected clip to the playhead: its start (everything before goes) or its end (everything after).</summary>
    public bool TrimClip(object? clip, bool start, double seconds)
    {
        var total = SequenceSeconds;
        switch (clip)
        {
            case Layer { IsMainVideo: true }:
                if (GetMainClipAt(seconds) is { } under)
                    return TrimMain(under.Index, start, seconds);
                break;

            case Layer { HasTiming: true } layer:
                var end = layer.GetSpan(total).End;
                if (seconds <= layer.StartTime + 0.05 || seconds >= end - 0.05)
                    break;

                Checkpoint($"trim {layer.Name}");
                using (DeferCommand())
                {
                    if (start)
                    {
                        // Its keyframes are counted from where it begins, which has just moved.
                        layer.ShiftKeys(layer.StartTime - seconds);
                        (layer.MediaOffset, layer.StartTime, layer.Duration) = (layer.MediaOffset + (seconds - layer.StartTime) * layer.Speed, seconds, end - seconds);
                    }
                    else
                    {
                        layer.Duration = seconds - layer.StartTime;
                    }
                }

                Log($"Trimmed clip: {layer.Name} now {(start ? "starts" : "ends")} at {TimeDisplay.Format(seconds)}");
                return true;

            case CutSegment segment when Segments.Contains(segment):
                var at = TimeSpan.FromSeconds(seconds);
                if (at <= segment.Start || at >= segment.End)
                    break;

                Checkpoint("trim a segment");
                Segments[Segments.IndexOf(segment)] = start
                    ? new CutSegment(at, segment.End) { IsSkipped = segment.IsSkipped }
                    : new CutSegment(segment.Start, at) { IsSkipped = segment.IsSkipped };
                Log($"Trimmed segment: now {(start ? "starts" : "ends")} at {TimeDisplay.Format(seconds)}");
                return true;
        }

        StatusText = "Select a layer or a cut segment, and put the playhead inside it, to trim it there.";
        return false;
    }

    /// <summary>Marks a cut segment as skipped, or takes the mark off: a skipped segment stays on the timeline but is left out of the output.</summary>
    public void ToggleSkip(CutSegment segment)
    {
        Checkpoint(segment.IsSkipped ? "restore a segment" : "skip a segment");
        segment.IsSkipped = !segment.IsSkipped;
        GenerateCommand();
        StatusText = segment.IsSkipped ? "The segment is skipped: it stays on the timeline, greyed out, and is left out of the output." : "The segment is kept again.";
    }

    public void DuplicateLayer(Layer layer)
    {
        if (!layer.IsRemovable)
            return;

        Checkpoint($"duplicate {layer.Name}");
        // The whole track: every clip on it, on a new track just above.
        var clips = GetTrackClips(layer);
        var track = Layers.Select(l => l.TrackId).DefaultIfEmpty().Max() + 1;
        var at = Layers.IndexOf(clips[^1]) + 1;
        foreach (var clip in clips)
        {
            var copy = Layer.FromState(clip.ToState(), SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            (copy.Name, copy.TrackId) = (clip.Name + " copy", track);
            (copy.PositionX, copy.PositionY) = (clip.PositionX + 0.03, clip.PositionY + 0.03);
            (copy.Waveform, copy.Filmstrip) = (clip.Waveform, clip.Filmstrip);
            Hook(copy);
            InsertLayer(at++, copy);
        }

        Log($"Duplicated clip: {layer.Name}");
    }

    /// <summary>Removes a track: every clip on it.</summary>
    public void RemoveTrack(Layer layer)
    {
        if (!layer.IsRemovable || !Layers.Contains(layer))
            return;

        Checkpoint($"remove {layer.Name}");
        using var whole = DeferCommand();
        foreach (var clip in GetTrackClips(layer))
            MoveToBin(clip);

        StatusText = $"Removed {layer.Name}. Its clips are in the recycle bin (the trash can), and Ctrl+Z brings them back.";
    }

    public void ToggleHidden(Layer layer)
    {
        Checkpoint(layer.IsHidden ? $"show {layer.Name}" : $"hide {layer.Name}");
        var hide = !layer.IsHidden;
        foreach (var clip in GetTrackClips(layer))
            clip.IsHidden = hide;
        if (layer.IsMainVideo || layer.IsBackground)
            GenerateCommand();
        RefreshLayerRows();
        StatusText = !hide ? $"{layer.Name} is shown again."
            : layer.IsBackground ? "The blurred background is hidden: the canvas behind the layers is black."
            : layer.IsMainVideo ? "The main video's picture is hidden. Its sound, its length and its cuts stay as they are."
            : $"{layer.Name} is hidden: it stays in the list but is left out of the picture.";
    }

    /// <summary>Has auto-captions listen to a video layer's own sound.</summary>
    public void UseLayerAudioForCaptions(Layer layer)
    {
        if (!layer.CarriesSound)
        {
            StatusText = $"{layer.Name} has no sound to make captions from.";
            return;
        }

        (CaptionAudioPath, CaptionUseExternalAudio) = (layer.ImagePath, true);
        StatusText = $"Auto-captions now listen to {layer.Name} (Subtitles tab: External Audio File).";
    }

    // ----- Audio on the timeline -----

    /// <summary>
    /// Whether the audio follows the picture. Linked, an audio track cannot be moved by itself, and a video
    /// layer's sound starts where the layer does. Unlinked, each can be slipped on its own.
    /// </summary>
    [ObservableProperty] private bool _audioLinked = true;

    /// <summary>Whether the blocks of the Layers tab show the waveform of the sound that is linked to their picture, drawn over them.</summary>
    [ObservableProperty] private bool _showLinkedAudio = true;

    /// <summary>
    /// Links or unlinks audio and video. Linking again after something was slipped asks first: linked means
    /// in step, so the audio that was moved has to come back to where its picture is.
    /// </summary>
    public void SetAudioLinked(bool linked)
    {
        if (linked == AudioLinked)
            return;

        var slipped = AudioTracks.Any(t => Math.Abs(t.OffsetSeconds) > 0.001) || Layers.Any(l => Math.Abs(l.AudioOffset) > 0.001);
        if (linked && slipped)
        {
            // Which of the two gives way: the sound goes back to its picture, or the picture goes to its sound.
            var choice = Choose?.Invoke("Link Audio and Video",
                "The audio has been moved out of step with its video. Linking puts them back together, one way or the other:\n\n"
                + "Snap Audio to Video: the sound goes back to where its picture is.\n"
                + "Snap Video to Audio: each video layer moves to where its sound was put. (The main video is the timeline itself and cannot move: its own audio tracks go back to it.)",
                "Snap Audio to Video", "Snap Video to Audio") ?? 0;
            if (choice == 0)
            {
                OnPropertyChanged(nameof(AudioLinked));
                StatusText = "Left unlinked: the audio stays where it was moved to.";
                return;
            }

            Checkpoint("link audio and video");
            foreach (var track in AudioTracks)
                track.OffsetSeconds = 0;
            foreach (var layer in Layers)
            {
                if (choice == 2 && Math.Abs(layer.AudioOffset) > 0.001)
                    layer.StartTime = Math.Round(Math.Max(layer.StartTime + layer.AudioOffset, 0), 2);
                layer.AudioOffset = 0;
            }
        }

        AudioLinked = linked;
        GenerateCommand();
        StatusText = linked
            ? "Audio and video are linked: they move and are cut together."
            : "Audio and video are unlinked: an audio block can now be dragged on its own.";
    }

    /// <summary>Slips an audio track in time. Only while unlinked; a track that was being copied is re-encoded from now on, as moved sound has to be.</summary>
    public bool SlipAudio(AudioTrack track, double offsetSeconds)
    {
        if (AudioLinked)
        {
            StatusText = "Audio and video are linked. Unlink them (the chain button) to move the audio on its own.";
            return false;
        }

        Checkpoint($"move {track.Title}");
        track.OffsetSeconds = Math.Round(offsetSeconds, 2);
        MakeEditable(track);
        StatusText = $"{track.Title} moved {(track.OffsetSeconds >= 0 ? "later" : "earlier")} by {Math.Abs(track.OffsetSeconds):0.00} s.";
        return true;
    }

    /// <summary>Cuts an audio track's block in two at a time, so that either part can be silenced on its own.</summary>
    public bool SplitAudio(AudioTrack track, double seconds)
    {
        var total = MainMediaSeconds;
        var content = seconds - track.OffsetSeconds - MainShift;
        if (content <= 0.05 || content >= total - 0.05)
        {
            StatusText = $"The playhead is not inside {track.Title}.";
            return false;
        }

        Checkpoint($"split {track.Title}");
        track.SplitAt(content, total);
        Log($"Split clip: {track.Title} at {TimeDisplay.Format(seconds)} (select a part and press Delete to silence it)");
        return true;
    }

    /// <summary>Silences one piece of an audio track, or brings it back.</summary>
    public void ToggleAudioPiece(AudioTrack track, AudioPiece piece)
    {
        Checkpoint(piece.Muted ? $"restore part of {track.Title}" : $"silence part of {track.Title}");
        track.ToggleMuted(piece);
        MakeEditable(track);
        StatusText = piece.Muted ? $"That part of {track.Title} plays again." : $"That part of {track.Title} is silenced. Ctrl+Z brings it back.";
    }

    // Sound that has been moved or silenced in places cannot be copied: it has to be decoded and encoded again.
    private void MakeEditable(AudioTrack track)
    {
        if (track.IsEdited && track.IsPassthrough)
        {
            track.Action = AudioTrack.Reencode;
            if (AudioEncoder != CopyOption)
                track.Codec = AudioEncoder;
        }

        GenerateCommand();
    }

    /// <summary>The video layers whose own sound goes into the output: those that have any, and are not hidden.</summary>
    private List<Layer> GetLayerAudioSources() =>
        Layers.Where(l => l.CarriesSound && !l.IsHidden && !l.IsSoundMuted && File.Exists(l.ImagePath)).ToList();

    /// <summary>Draws the waveform of a video layer's sound, for its block on the Layers tab. In the background; the layer works without it.</summary>
    private async Task LoadLayerWaveformAsync(Layer layer)
    {
        if (_isBackgroundWorker || !layer.CarriesSound || layer.Waveform is not null || !(ShowWaveforms || layer.IsAudio))
            return;

        // The picture of a file is made once in a session, however often its clips are split, undone and
        // put back: every one of those makes new clips, and none of them a new picture.
        var path = layer.ImagePath;
        if (!_layerWaveforms.TryGetValue(path, out var drawing))
        {
            _layerWaveforms[path] = drawing = Waveforms.RenderAsync(
                path, [0], Path.Combine(WaveformFolder, $"layer_{Guid.NewGuid():N}.png"), 1200, 60, "white", _shutdown.Token);
        }

        try
        {
            layer.Waveform = await drawing;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not kept: the next clip of this file may have better luck.
            _layerWaveforms.Remove(path);
        }
    }

    // The pictures made of layers' files this session, by file: waveforms, and strips of frames. No more
    // than this many of each are kept: the file used longest ago gives way to a new one. A clip that is on
    // the timeline holds its own pictures whatever happens here, so nothing that is showing goes blank; what
    // is bounded is how much is remembered of files that are no longer in use. A strip is at most 25 frames
    // 54 pixels high (about half a megabyte), a waveform 1200 by 60.
    private const int PicturesKept = 24;

    private readonly LruCache<string, Task<System.Windows.Media.ImageSource?>> _layerWaveforms = new(PicturesKept, StringComparer.OrdinalIgnoreCase);
    private readonly LruCache<string, System.Windows.Media.ImageSource> _layerFilmstrips = new(PicturesKept, StringComparer.OrdinalIgnoreCase);

    /// <summary>The pictures a layer's block is drawn with: the waveform of its sound, and for a video a strip of its frames.</summary>
    private async Task LoadLayerPicturesAsync(Layer layer)
    {
        // Side by side: the frames do not wait for the waveform to be drawn.
        await Task.WhenAll(LoadLayerWaveformAsync(layer), LoadLayerFilmstripAsync(layer));
    }

    /// <summary>
    /// Takes a strip of frames from a video layer's file, evenly spread over its length, for its block on the
    /// Layers tab. In the background, once for each file; the layer works without it.
    /// </summary>
    private async Task LoadLayerFilmstripAsync(Layer layer)
    {
        if (_isBackgroundWorker || !layer.IsVideoFile || layer.Filmstrip is not null || !File.Exists(layer.ImagePath))
            return;

        try
        {
            if (layer.MediaDuration <= 0 && await MediaProbe.ProbeAsync(layer.ImagePath, _shutdown.Token) is { DurationSeconds: > 0 } info)
                layer.MediaDuration = info.DurationSeconds;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Its length stays unknown: one frame is taken, from its start.
        }

        var file = layer.ImagePath;
        if (await GetFilmstripAsync(file, Math.Clamp(AppSettings.Current.LayerThumbnailCount, 1, 25), layer.MediaDuration) is not { } picture)
            return;

        // Every clip of the file, whichever of them asked: a clip that was split while the strip was being
        // made is two clips by now.
        foreach (var clip in Layers.Where(l => l.IsVideoFile && l.Filmstrip is null && string.Equals(l.ImagePath, file, StringComparison.OrdinalIgnoreCase)).ToList())
            clip.Filmstrip = picture;
        if (layer.Filmstrip is null)
            layer.Filmstrip = picture;
    }

    /// <summary>
    /// The main video's own strip of frames, for its clips on the Layers tab: the main video is a clip like
    /// any other there, and is drawn like one. Only in Editor Mode, which is where that tab is.
    /// </summary>
    private async Task LoadMainFilmstripAsync(string path)
    {
        if (_isBackgroundWorker || !IsEditorMode || _mediaInfo?.Video is null || !File.Exists(path))
            return;

        // More frames than a layer gets: the main video is usually the longest thing on the timeline.
        var count = Math.Clamp(AppSettings.Current.LayerThumbnailCount * 2, 8, 25);
        if (await GetFilmstripAsync(path, count, MainMediaSeconds) is { } picture && string.Equals(LocalMediaPath, path, StringComparison.OrdinalIgnoreCase))
            _mainVideoRow.Filmstrip = picture;
    }

    // The strips that are being made right now, by file: a second clip of a file waits for the first one's.
    private readonly Dictionary<string, Task<System.Windows.Media.ImageSource?>> _filmstripsBeingMade = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The strip of frames of a file: the one made earlier this session, the one being made, or a new one.</summary>
    private Task<System.Windows.Media.ImageSource?> GetFilmstripAsync(string file, int count, double seconds)
    {
        if (_layerFilmstrips.TryGetValue(file, out var made))
            return Task.FromResult<System.Windows.Media.ImageSource?>(made);
        if (!_filmstripsBeingMade.TryGetValue(file, out var making))
            _filmstripsBeingMade[file] = making = MakeFilmstripAsync(file, count, seconds);
        return making;
    }

    private async Task<System.Windows.Media.ImageSource?> MakeFilmstripAsync(string file, int count, double seconds)
    {
        // Let the caller go on first, so that this is in the list of strips being made before it can leave it.
        await Task.Yield();
        try
        {
            var path = Path.Combine(SpriteFolder, $"layer_{Guid.NewGuid():N}.jpg");
            var token = _shutdown.Token;

            // Both the extraction and the reading of the picture happen away from the interface. The picture
            // is frozen there, before it is handed over: a bitmap made on one thread cannot be shown by
            // another until it is, and a frozen one is also the only kind WPF does not keep watching.
            var picture = await Task.Run(async () =>
            {
                if (!await FfmpegRunner.GenerateFilmstripAsync(file, path, count, seconds, token))
                    return null;

                // Read into memory, so that the file is not held open.
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return (System.Windows.Media.ImageSource)bitmap;
            }, token);

            if (picture is not null)
                _layerFilmstrips[file] = picture;
            return picture;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or UriFormatException or System.ComponentModel.Win32Exception)
        {
            if (ex is not OperationCanceledException)
                AppLog.Write($"Thumbnails of {Path.GetFileName(file)} could not be read: {ex.Message}");
            return null;
        }
        finally
        {
            // Made or not, it is no longer being made: a file that failed is tried again when next asked for.
            _filmstripsBeingMade.Remove(file);
        }
    }

    // ----- Dead air -----

    private DeadAirMode _deadAirMode;
    private object? _deadAirTarget;

    /// <summary>
    /// Remove Dead Air on what is selected: a video layer (its own sound is listened to and the layer cut up),
    /// an audio track (it is the one listened to, and the main video is cut), or otherwise the main video.
    /// </summary>
    public void RunDeadAir(DeadAirMode mode, object? target)
    {
        (_deadAirMode, _deadAirTarget) = (mode, target);
        if (RemoveDeadAirCommand.CanExecute(null))
            RemoveDeadAirCommand.Execute(null);
    }

    /// <summary>Cuts a video layer at the silences in its own sound: into the parts with sound, and (marked hidden, or left out) the silent ones.</summary>
    private string ApplyDeadAirToLayer(Layer layer, List<(double Start, double End)> silences, bool keepSilent, double fileSeconds)
    {
        var total = SequenceSeconds;
        var visibleFor = layer.Duration > 0.001 ? layer.Duration : Math.Max(total - layer.StartTime, 0);
        // The silences are found in the file's own time; the layer covers it at its speed.
        var speed = layer.HasSpeed ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
        var (from, to) = (layer.MediaOffset, Math.Min(layer.MediaOffset + visibleFor * speed, fileSeconds));

        // The layer's stretch of its own video, as alternating parts: sound, silence, sound...
        var parts = new List<(double Start, double End, bool Silent)>();
        var cursor = from;
        foreach (var (start, end) in silences.OrderBy(s => s.Start))
        {
            var (a, b) = (Math.Max(start, from), Math.Min(end, to));
            if (b - a < 0.05)
                continue;
            if (a - cursor >= ShortestKeptSeconds)
                parts.Add((cursor, a, false));
            parts.Add((a, b, true));
            cursor = b;
        }

        if (to - cursor >= ShortestKeptSeconds)
            parts.Add((cursor, to, false));
        if (parts.Count(p => p.Silent) == 0)
            return $"No silence found in {layer.Name}.";
        if (parts.Count > 60)
            return $"{layer.Name} has too many silences to cut into layers ({parts.Count} parts): raise the shortest silence and try again.";

        Checkpoint($"remove dead air from {layer.Name}");
        using var whole = DeferCommand();
        var index = Layers.IndexOf(layer);
        var state = layer.ToState();
        Detach(layer);
        foreach (var (start, end, silent) in parts.Where(p => keepSilent || !p.Silent))
        {
            var part = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            (part.StartTime, part.Duration, part.MediaOffset) = (layer.StartTime + (start - from) / speed, (end - start) / speed, start);
            (part.IsHidden, part.Name, part.Waveform, part.Filmstrip) = (silent, state.Name, layer.Waveform, layer.Filmstrip);
            part.ShiftKeys(layer.StartTime - part.StartTime);
            Hook(part);
            InsertLayer(Math.Min(index++, Layers.Count), part);
        }

        var removed = parts.Where(p => p.Silent).Sum(p => p.End - p.Start);
        return keepSilent
            ? $"{state.Name} was cut at {parts.Count(p => p.Silent)} silent stretches ({removed:0.#} s): they are still there, hidden, and can be shown again."
            : $"{parts.Count(p => p.Silent)} silent stretches ({removed:0.#} s) were removed from {state.Name}.";
    }

    // ----- Unsaved changes -----

    // What the project looked like when it was last saved or loaded, to tell whether anything has changed since.
    private string _savedFingerprint = "";

    private string Fingerprint()
    {
        var state = CaptureState();
        state.SavedAt = default;
        return JsonSerializer.Serialize(state);
    }

    /// <summary>Takes the project as it is now as the saved one: called after a load and after a save.</summary>
    public void MarkSaved() => _savedFingerprint = HasSource ? Fingerprint() : "";

    /// <summary>Whether there is a video open whose cuts or settings have changed since it was loaded or last saved as a project.</summary>
    public bool HasUnsavedChanges => HasSource && _savedFingerprint.Length > 0 && Fingerprint() != _savedFingerprint;
}
