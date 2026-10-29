using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// The timeline as Undo keeps it (4.3C): the document (every track and clip, the markers, the Sequence Output's effects),
/// what was done to the main video's own audio tracks, the background's switch, the recycle bin, the subtitles and the
/// caption box. Nothing else, and no pictures.
/// </summary>
public sealed record TimelineSnapshot(
    Models.Timeline.TimelineDocument Document, List<AudioEditState> Audio, bool BackgroundHidden = false, List<DeletedClip>? Bin = null,
    SubtitleTrackData? Subtitles = null, LayerState? CaptionLayer = null, CaptionStyle? CaptionStyle = null);

/// <summary>What was done to one audio track on the timeline: how far it was slipped, and its pieces.</summary>
public sealed record AudioEditState(int Index, double Offset, List<AudioPiece> Pieces);

/// <summary>What Remove Dead Air does with the silences it finds.</summary>
public enum DeadAirMode
{
    /// <summary>The cut list becomes everything between the silences (Encoder Mode's way before 4.2; the value at rest).</summary>
    Classic,

    /// <summary>
    /// The silences are folded out of the timeline (4.3D): cut out, what follows closed up, and each kept at its cut to be
    /// put back (a FoldedSegment; right-click the clip after the cut). What is on the timeline is what renders.
    /// </summary>
    SplitAndMark,

    /// <summary>The silences are cut out and removed.</summary>
    Delete,
}

// Editing on the timeline: undo and redo, deleting and trimming whatever is selected, audio that can be
// slipped and cut on its own, and the question of whether anything has changed since it was last saved.
public partial class MainViewModel
{
    // ----- The time bars -----

    /// <summary>Whether a dragged or dropped clip's edges catch on the other clips' edges, the end of the sequence and the playhead. Remembered.</summary>
    [ObservableProperty] private bool _snapClips = AppSettings.Current.SnapClips;

    partial void OnSnapClipsChanged(bool value)
    {
        AppSettings.Current.SnapClips = value;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether the layers' controls are shown beside their time bars; folded away, every row's time bar is wider, all alike.</summary>
    [ObservableProperty] private bool _layerControlsShown = true;

    // ----- Undo and redo -----
    // Before anything on the timeline is changed, the timeline as it is goes onto a stack, as a small piece
    // of JSON: the cuts, the layers, the audio edits. Twenty deep. Undo puts the last one back.

    private const int UndoDepth = 20;
    // Each state is kept compressed (Brotli): the JSON of a timeline is mostly the same property names over
    // and over, and shrinks to a small fraction. It is only made text again when it is stepped back to.
    private readonly List<(string What, byte[] State)> _undo = [];
    private readonly List<(string What, byte[] State)> _redo = [];

    // The room a state is written into as UTF-8 before it is compressed, and read back into, kept and used again:
    // with a long subtitle track a state is hundreds of kilobytes of text, which made afresh at every checkpoint
    // (as a string, then its bytes) went on the large object heap each time. Only the compressed copy is kept.
    private readonly System.Buffers.ArrayBufferWriter<byte> _stateText = new(64 * 1024), _otherText = new(16 * 1024);

    /// <summary>A value as JSON (UTF-8), in the room given, which it replaces.</summary>
    private static ReadOnlySpan<byte> WriteJson<T>(T value, System.Buffers.ArrayBufferWriter<byte> room)
    {
        room.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(room))
            JsonSerializer.Serialize(writer, value);
        return room.WrittenSpan;
    }

    /// <summary>Whether two values come out as the same JSON: for "has this part of the state changed".</summary>
    private bool SameJson<T>(T first, T second) => WriteJson(first, _stateText).SequenceEqual(WriteJson(second, _otherText));

    private byte[] Pack(TimelineSnapshot state)
    {
        var text = WriteJson(state, _stateText);
        var room = System.Buffers.ArrayPool<byte>.Shared.Rent(System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(text.Length));
        try
        {
            // Quality 1: what CompressionLevel.Fastest is.
            return System.IO.Compression.BrotliEncoder.TryCompress(text, room, out var written, quality: 1, window: 22)
                ? room.AsSpan(0, written).ToArray()
                : throw new InvalidOperationException("An undo step could not be compressed.");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(room);
        }
    }

    private TimelineSnapshot? Unpack(byte[] state)
    {
        _stateText.ResetWrittenCount();
        using (var brotli = new System.IO.Compression.BrotliStream(new MemoryStream(state), System.IO.Compression.CompressionMode.Decompress))
        {
            int read;
            while ((read = brotli.Read(_stateText.GetSpan(65536))) > 0)
                _stateText.Advance(read);
        }

        return JsonSerializer.Deserialize<TimelineSnapshot>(_stateText.WrittenSpan);
    }

    /// <summary>
    /// The timeline as it is now, the views' looks written into the document first. The document is a copy of its own (its
    /// tracks and clips; the looks shared, which no edit changes in place): an edit's step is packed only once the edit is
    /// committed (OnDocumentCommitted), when the document itself has changed.
    /// </summary>
    private TimelineSnapshot CaptureSnapshot()
    {
        FlushLooks();
        return new(
            Document.Clone(),
            AudioTracks.Select(t => new AudioEditState(t.Index, t.OffsetSeconds, [.. t.Pieces])).ToList(),
            _backgroundRow.IsHidden, RecycleBin.Select(i => i.Clip).ToList(),
            CaptureSubtitles(), CaptionLayer.ToState(), CaptionStyle.Clone());
    }

    /// <summary>
    /// Puts an undo step on the stack that brings back the state given, which may be other than the present one: an edit
    /// of the document keeps the timeline as it was before it (MainViewModel.Document.cs, OnDocumentCommitted).
    /// </summary>
    private void Checkpoint(string what, TimelineSnapshot before)
    {
        if (_isBackgroundWorker)
            return;

        _undo.Add((what, Pack(before)));
        if (_undo.Count > UndoDepth)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>
    /// Call before changing what is not an edit of the timeline document (a clip's look, its keyframes and effects, the
    /// subtitles, the caption box, the video's own audio tracks): what it all looks like now can then be brought back with
    /// Undo. (An edit of the document keeps its own step.)
    /// </summary>
    /// <param name="what">The change about to be made, in a word or two, for the status bar.</param>
    public void Checkpoint(string what)
    {
        if (_isBackgroundWorker)
            return;

        _undo.Add((what, Pack(CaptureSnapshot())));
        if (_undo.Count > UndoDepth)
            _undo.RemoveAt(0);
        _redo.Clear();
        _checkpointedAt = Environment.TickCount64;
    }

    public void Undo() => Step(_undo, _redo, "Undid");

    public void Redo() => Step(_redo, _undo, "Redid");

    private void Step(List<(string What, byte[] State)> from, List<(string What, byte[] State)> to, string verb)
    {
        if (from.Count == 0)
        {
            StatusText = verb == "Undid" ? "Nothing to undo." : "Nothing to redo.";
            return;
        }

        var (what, state) = from[^1];

        // The Subtitle Editor holds the cues it shows: a step that changes them waits until it is closed.
        if (IsSubtitleEditorOpen && Unpack(state) is { } target
            && !SameJson(target.Subtitles, CaptureSubtitles()))
        {
            StatusText = $"Close the Subtitle Editor first: {what} changes the subtitles.";
            return;
        }

        from.RemoveAt(from.Count - 1);
        to.Add((what, Pack(CaptureSnapshot())));
        RestoreTimeline(Unpack(state));
        Log($"{verb}: {what}");
    }

    private void RestoreTimeline(TimelineSnapshot? state)
    {
        if (state is not { } snapshot)
            return;

        // One rebuild of the command for the whole step, not one for every clip that is put back.
        using var whole = DeferCommand();
        InstallDocument(snapshot.Document);
        SetBin(snapshot.Bin);
        _backgroundRow.IsHidden = snapshot.BackgroundHidden;
        foreach (var edit in snapshot.Audio)
        {
            if (AudioTracks.FirstOrDefault(t => t.Index == edit.Index) is { } track)
                track.SetEdits(edit.Offset, edit.Pieces);
        }

        // The subtitles, the caption box and its style, each only where it differs: a step that did not touch
        // them leaves the caption preview as it is.
        if (!SameJson(snapshot.Subtitles, CaptureSubtitles()))
        {
            ApplySubtitles(snapshot.Subtitles);
            RefreshSubtitlePreview();
        }

        if (snapshot.CaptionLayer is { } caption && !SameJson(caption, CaptionLayer.ToState()))
            SetCaptionLayer(caption);
        if (snapshot.CaptionStyle is { } style && !SameJson(style, CaptionStyle))
            SetCaptionStyle(style.Clone());

        RefreshLayerRows();
        GenerateCommand();
        ApplyKeyframes();
    }

    // ----- Whatever is selected -----

    /// <summary>Removes the selected clip: it goes to the recycle bin, its time left empty. Any clip, the main video's too.</summary>
    public bool DeleteClip(object? clip)
    {
        if (clip is Layer layer && ClipOf(layer) is not null)
        {
            var name = layer.Name;
            if (!MoveToBin(layer))
                return false;

            Log($"Deleted clip: {name} (in the recycle bin; Ctrl+Z brings it back)");
            return true;
        }

        StatusText = "Select a clip to delete: click its block on the Layers tab.";
        return false;
    }

    /// <summary>
    /// Sets how fast a clip runs. The clip plays the same stretch of its file as before, so its block on the
    /// timeline is as much shorter or longer as it is faster or slower (what it grows into on its track moves on).
    /// </summary>
    public void SetClipSpeed(Layer clip, double speed)
    {
        speed = Math.Round(Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed), 3);
        if (ClipOf(clip) is not { PlaysFile: true } model || Math.Abs(model.Speed - speed) < 0.0005)
        {
            // A box bound to the speed shows what it is.
            OnViewSpeedRefused(clip);
            return;
        }

        if (Edit($"change the speed of {clip.Name}", edit => edit.SetClipSpeed(model, speed), undoable: TakesUndoStep(clip, nameof(Layer.Speed))))
            Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Set speed of {clip.Name} to {model.Speed:0.##}x: it now runs {TimeDisplay.Format(model.Start)} to {TimeDisplay.Format(model.End)}"));
        else
            OnViewSpeedRefused(clip);
    }

    /// <summary>A view whose speed box asked for what could not be: the box shows the clip's speed again.</summary>
    private void OnViewSpeedRefused(Layer view)
    {
        if (ClipOf(view) is { } clip && Document.TrackOf(clip) is { } track)
        {
            _syncingViews = true;
            try
            {
                TakeClip(view, clip, track);
            }
            finally
            {
                _syncingViews = false;
            }
        }

        view.NotifySpeed();
    }

    /// <summary>Trims the selected clip to the playhead: its start (everything before goes) or its end (everything after).</summary>
    public bool TrimClip(object? clip, bool start, double seconds)
    {
        if (clip is Layer layer && ClipOf(layer) is { } model && seconds > model.Start + TimelineDocument.MinimumClipSeconds && seconds < model.End - TimelineDocument.MinimumClipSeconds)
        {
            var moved = 0.0;
            if (!Edit($"trim {layer.Name}", edit => moved = start ? edit.TrimClipStart(model, seconds) : edit.TrimClipEnd(model, seconds)) || Math.Abs(moved) < 1e-6)
                return false;

            Log($"Trimmed clip: {layer.Name} now {(start ? "starts" : "ends")} at {TimeDisplay.Format(start ? model.Start : model.End)}");
            return true;
        }

        StatusText = "Select a clip, and put the playhead inside it, to trim it there.";
        return false;
    }

    /// <summary>Copies a clip's whole track (every clip on it) onto a new track just above, moved a little on the frame.</summary>
    public void DuplicateLayer(Layer layer)
    {
        if (TrackOfRow(layer) is not { } track || track.Clips.Count == 0)
            return;

        FlushLooks();
        Edit($"duplicate {layer.Name}", edit =>
        {
            var copy = edit.InsertTrack(Document.Tracks.IndexOf(track) + 1, track.Kind == TimelineTrackKind.Switch ? TimelineTrackKind.Video : track.Kind);
            foreach (var clip in track.Clips.ToList())
            {
                var look = TimelineTransaction.CopyOf(clip.State);
                (look.Name, look.PositionX, look.PositionY) = (look.Name + " copy", look.PositionX + 0.03, look.PositionY + 0.03);
                var made = CopyOfClip(clip);
                (made.Id, made.State) = (Guid.NewGuid(), look);

                // A copy of the main video is a picture in picture of its file: a layer of its own.
                if (made.ContentKind == LayerKind.MainVideo)
                    made.ContentKind = LayerKind.VideoFile;
                edit.PlaceClip(copy, made, allowCrossfadeOverlap: false);
            }
        });

        Log($"Duplicated clip: {layer.Name}");
    }

    /// <summary>Removes a track: every clip on it goes to the recycle bin, and the track goes.</summary>
    public void RemoveTrack(Layer layer)
    {
        if (TrackOfRow(layer) is not { } track)
            return;

        var binned = track.Clips.Select(c => (Deleted: Binned(c, track, ViewOf(c)), Name: ViewOf(c)?.Name ?? c.State.Name)).ToList();
        if (!Edit($"remove {layer.Name}", edit => edit.RemoveTrack(track)))
            return;

        foreach (var (deleted, name) in binned)
            AddToBin(deleted.Clip, name, deleted.Preview);
        StatusText = binned.Count > 0
            ? $"Removed {layer.Name}. Its clips are in the recycle bin (the trash can), and Ctrl+Z brings them back."
            : $"Removed {layer.Name}.";
    }

    /// <summary>Raised when a track has been hidden or shown: the layout pane draws its boxes again.</summary>
    public event Action? LayerVisibilityChanged;

    /// <summary>
    /// Hides a row, or shows it again: the background's blur; the main video's picture (its track's switch: its sound,
    /// its length and its cuts stay); any other track's clips, picture and sound (as 4.2.0 did).
    /// </summary>
    public void ToggleHidden(Layer layer)
    {
        var hide = !layer.IsHidden;
        if (layer.IsBackground || layer.IsCaptions || TrackOfRow(layer) is not { } track)
        {
            Checkpoint(hide ? $"hide {layer.Name}" : $"show {layer.Name}");
            layer.IsHidden = hide;
            GenerateCommand();
        }
        else if (!Edit(hide ? $"hide {layer.Name}" : $"show {layer.Name}", edit =>
                 {
                     if (layer.IsMainVideo)
                         edit.SetTrack(track, hidden: hide);
                     else
                         foreach (var clip in track.Clips)
                             edit.SetClipFlags(clip, hidden: hide);
                 }))
        {
            return;
        }

        RefreshLayerRows();
        LayerVisibilityChanged?.Invoke();
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

        // Every clip with sound has its own link (the document's); this switch sets them all.
        var clips = Document.Tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c))).Where(c => c.Clip.HasSound).ToList();
        var slipped = AudioTracks.Any(t => Math.Abs(t.OffsetSeconds) > 0.001) || clips.Any(c => Math.Abs(c.Clip.AudioSlipSeconds) > 0.001);
        var choice = 1;
        if (linked && slipped)
        {
            // Which of the two gives way: the sound goes back to its picture, or the picture goes to its sound.
            choice = Choose?.Invoke("Link Audio and Video",
                "The audio has been moved out of step with its video. Linking puts them back together, one way or the other:\n\n"
                + "Snap Audio to Video: the sound goes back to where its picture is.\n"
                + "Snap Video to Audio: each clip moves to where its sound was put. (The main video's own audio tracks go back to it.)",
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
        }

        // The stream rows' slips were put back with a step of their own just above; the clips' links are one edit.
        if (!Edit(linked ? "link audio and video" : "unlink audio and video", edit =>
            {
                foreach (var (track, clip) in clips)
                {
                    var slip = clip.AudioSlipSeconds;
                    edit.SetAudioLink(clip, linked, linked ? 0 : slip);
                    if (linked && choice == 2 && Math.Abs(slip) > 0.001 && !track.IsLocked)
                    {
                        try
                        {
                            edit.MoveClip(clip, track, Math.Max(clip.Start + slip, 0));
                        }
                        catch (TimelineEditException)
                        {
                            // No room where its sound was: it stays, its sound brought back to it.
                        }
                    }
                }
            }, undoable: !(linked && slipped)))
        {
            OnPropertyChanged(nameof(AudioLinked));
            return;
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
        // The track's pieces are in the file's own time: the moment of the file the main video's clip under the playhead shows there.
        var total = MainMediaSeconds;
        var content = GetMainClipAt(seconds) is { } under ? under.Offset + (seconds - under.Start) - track.OffsetSeconds : -1;
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
        Layers.Where(l => l.CarriesSound && !l.IsHidden && !l.IsSoundMuted && FileState.Exists(l.ImagePath)).ToList();

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
                path, [0], Path.Combine(WaveformFolder, $"layer_{Guid.NewGuid():N}.png"), 1200, 60, "white", PictureToken(path));
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

    // The background work that draws layers' pictures, by file: all of it hangs off one source (the session's),
    // and each file's can be stopped on its own. It is, the moment no clip on the timeline uses the file any
    // more: FFmpeg is not left decoding a long file for a clip that was dropped and taken off again.
    private readonly Dictionary<string, CancellationTokenSource> _pictureWork = new(StringComparer.OrdinalIgnoreCase);

    private CancellationToken PictureToken(string path)
    {
        if (!_pictureWork.TryGetValue(path, out var work))
            _pictureWork[path] = work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        return work.Token;
    }

    /// <summary>Stops what is still being drawn of a file that no clip uses any more.</summary>
    private void CancelStalePictureWork(string path)
    {
        if (path.Length == 0 || !_pictureWork.TryGetValue(path, out var work) || Layers.Any(l => string.Equals(l.ImagePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        _pictureWork.Remove(path);
        if (_layerWaveforms.TryGetValue(path, out var drawing) && !drawing.IsCompleted)
            _layerWaveforms.Remove(path);
        work.Cancel();
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
    /// any other there, and is drawn like one.
    /// </summary>
    private async Task LoadMainFilmstripAsync(string path)
    {
        if (_isBackgroundWorker || _mediaInfo?.Video is null || !File.Exists(path))
            return;

        // More frames than a layer gets: the main video is usually the longest thing on the timeline.
        var count = Math.Clamp(AppSettings.Current.LayerThumbnailCount * 2, 8, 25);
        if (await GetFilmstripAsync(path, count, MainMediaSeconds) is not { } picture || !string.Equals(LocalMediaPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        // Every clip of it, whichever asked: one split while the strip was being made is two by now.
        foreach (var view in Layers.Where(l => l.IsMainVideo))
            view.Filmstrip = picture;
    }

    /// <summary>The main video's strip of frames, for a view of one of its clips that has none yet (made once, kept for the session).</summary>
    private Task LoadMainFilmstripAsync() => LoadMainFilmstripAsync(LocalMediaPath);

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
            var kind = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"strip{count}");

            // Both the extraction and the reading of the picture happen away from the interface. The picture
            // is frozen there, before it is handed over: a bitmap made on one thread cannot be shown by
            // another until it is, and a frozen one is also the only kind WPF does not keep watching.
            var picture = await Task.Run(async () =>
            {
                // Made before, for this file as it is: taken from the cache.
                if (PreviewCache.Timeline.Find(file, kind, ".jpg") is { } kept)
                    path = kept;
                else if (await FfmpegRunner.GenerateFilmstripAsync(file, path, count, seconds, token))
                    path = PreviewCache.Timeline.Keep(file, kind, ".jpg", path);
                else
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

    /// <summary>
    /// Cuts clips at the silences in their sound (found in their file's own time): each is split where a silence it plays
    /// begins and ends, and the silent parts are then folded (<paramref name="fold"/>, 4.3D: taken out, what follows closed
    /// up, each kept at its cut to be put back, <see cref="UnfoldSegment"/>) or taken out. With <paramref name="closeUp"/>
    /// (the main video's dead air) they go from every unlocked track and what follows on all of them closes up behind them,
    /// as a cut list's left-out stretches did; otherwise (a layer's) a fold closes up its own track, and a silence taken out
    /// leaves its time empty, as in 4.2.0. One step of Undo for them all.
    /// </summary>
    private string ApplyDeadAir(IReadOnlyList<Layer> views, List<(double Start, double End)> silences, bool fold, bool closeUp)
    {
        var clips = views.Select(v => (View: v, Clip: ClipOf(v))).Where(c => c.Clip is { PlaysFile: true }).ToList();
        if (clips.Count == 0)
            return "Nothing to cut: select a clip that plays a file.";

        // Each clip's silent stretches on the sequence: where in its file it is, at its speed.
        var cuts = new List<(TimelineClip Clip, double From, double To)>();
        foreach (var (_, clip) in clips)
        {
            var (start, end, offset, speed) = (clip!.Start, clip.End, clip.MediaOffset, clip.Speed);
            foreach (var (from, to) in silences.OrderBy(s => s.Start))
            {
                var (a, b) = (Math.Max(start + (from - offset) / speed, start), Math.Min(start + (to - offset) / speed, end));
                if (b - a >= TimelineDocument.MinimumClipSeconds)
                    cuts.Add((clip, a, b));
            }
        }

        if (cuts.Count == 0)
            return $"No silence found in {(clips.Count == 1 ? clips[0].View.Name : "the main video")}.";
        // A layer is cut into clips of its own, which a few dozen keep usable; the main videos silences are taken out of the
        // timeline (4.2.0s cut list had no limit), however many a long recording has.
        if (cuts.Count > (clips.Any(c => c.View.IsMainVideo) ? 5000 : 60))
            return $"{(clips.Count == 1 ? clips[0].View.Name : "The main video")} has too many silences to cut ({cuts.Count}): raise the shortest silence and try again.";

        var name = clips.Count == 1 ? clips[0].View.Name : "the main video";
        var removed = 0.0;
        var marked = 0;
        Edit($"remove dead air from {name}", edit =>
        {
            // Latest first: what is cut out (and closed up behind) never moves a silence still to be cut.
            foreach (var (clip, from, to) in cuts.OrderByDescending(c => c.From))
            {
                if (Document.TrackOf(clip) is null)
                    continue;

                // Folded out of the timeline at its cut, to be put back: what is on the timeline is what renders.
                if (fold)
                {
                    try
                    {
                        if (edit.FoldStretch(Document.TrackOf(clip)!, from, to, rippleAllUnlockedTracks: closeUp) is { } made)
                            (marked, removed) = (marked + 1, removed + made.Length);
                    }
                    catch (TimelineEditException)
                    {
                        // Too near another cut or an edge, or what follows cannot close up: left as it is.
                    }

                    continue;
                }

                // Taken out of every unlocked track, and what follows closed up behind it, as 4.2.0s cut list left it out.
                if (closeUp)
                {
                    marked++;
                    removed += to - from;
                    TakeOutStretch(edit, from, to);
                    continue;
                }

                var silent = clip;
                try
                {
                    if (to < silent.End - TimelineDocument.MinimumClipSeconds)
                        edit.SplitClipAt(silent, to);
                    if (from > silent.Start + TimelineDocument.MinimumClipSeconds)
                        silent = edit.SplitClipAt(silent, from);
                }
                catch (TimelineEditException)
                {
                    // Too near another cut or an edge to be a clip of its own: left as it is.
                    continue;
                }

                marked++;
                removed += silent.Duration;
                edit.DeleteClip(silent);
            }
        });

        return fold
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{marked} silent stretches ({removed:0.#} s) were folded out of {name}: right-click the clip after a cut and choose Restore Folded Silence to put one back.")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{marked} silent stretches ({removed:0.#} s) were removed from {name}{(closeUp ? ", and what follows closed up" : "")}.");
    }

    // ----- Folded silences (4.3D) -----

    /// <summary>The stretches folded out at a clip's cuts (its start, or its end where nothing came after), latest seam first.</summary>
    public IReadOnlyList<FoldedSegment> FoldsAt(Layer clip) =>
        ClipOf(clip) is { } model && Document.Folds is { } folds ? [.. folds.Where(f => f.SeamClipId == model.Id)] : [];

    /// <summary>How many stretches are folded out of the timeline at cuts that are still there.</summary>
    public int FoldCount => Document.Folds?.Count(f => f.SeamIn(Document) is not null) ?? 0;

    /// <summary>Puts a folded stretch back at its cut, as one step of Undo: what follows moves on by its length.</summary>
    public void UnfoldSegment(FoldedSegment fold)
    {
        if (Edit($"put back {fold.Length:0.#} s at a cut", edit => edit.UnfoldSegment(fold)))
            Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Put back {fold.Length:0.#} s at the cut at {TimeDisplay.Format(fold.SeamIn(Document) ?? 0)}"));
    }

    /// <summary>Puts every folded stretch back, latest first (so none moves a seam still to be opened), as one step of Undo.</summary>
    public void UnfoldAll()
    {
        var folds = (Document.Folds ?? []).Where(f => f.SeamIn(Document) is not null).OrderByDescending(f => f.SeamIn(Document)).ToList();
        if (folds.Count > 0 && Edit($"put back {folds.Count} folded stretches", edit =>
            {
                foreach (var fold in folds)
                    edit.UnfoldSegment(fold);
            }))
        {
            Log($"Put back {folds.Count} folded stretch{(folds.Count == 1 ? "" : "es")}");
        }
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
