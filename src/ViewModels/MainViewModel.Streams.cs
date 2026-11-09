using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Every video track is equal (5.1-TrackEqualityAndVram, the user's directive; 5.2-AuditB: no main video) -----
// A recording with several audio streams shows them all, whichever track it is on: every video's streams are sound clips of their
// own, one lane per stream in its track's audio stack, children of the video's clip, so that a split, trim, move, fold or delete of
// the clip carries them along (TimelineTransaction); the file opened goes on the timeline the same way. A file with one stream keeps
// it on its clip: nothing to choose between. (AudioTracks are only what each output audio track is made as.)
// In line (5.2, the user's call): the lanes are the track's, one for each stream by its place in the file (StreamOrdinal), so
// every clip on the track puts its first stream on the "Stream 1" row, its second on "Stream 2", one after another, never on rows
// of their own. A stream can be moved off onto a lane of its own, or an Audio track (MoveSoundToOwnLane; a drag onto a video row).
public partial class MainViewModel
{
    /// <summary>
    /// The audio streams of a video clip, each a sound clip on its stream's lane under the clip's track,
    /// and the clip itself made silent: nothing is heard twice. Every clip on the track shares the lanes, by the stream's place in its
    /// file (5.2): its first stream on the first lane, in line with the others' first streams. Nothing for a file with one stream, or a
    /// clip already unpacked.
    /// </summary>
    private bool UnpackStreams(Layer view, MediaInfo info, bool undoable)
    {
        if (ClipOf(view) is not { ContentKind: LayerKind.VideoFile, AudioStreamIndex: >= 0 } clip || Document.TrackOf(clip) is not { } track || info.Audio.Count < 2)
            return false;

        var path = clip.SourcePath ?? view.ImagePath;
        var added = Edit($"unpack the audio streams of {view.Name}", edit =>
        {
            for (var ordinal = 0; ordinal < info.Audio.Count; ordinal++)
            {
                var stream = info.Audio[ordinal];
                var sound = new Layer
                {
                    Kind = LayerKind.Audio,
                    Name = new AudioTrack(stream).Title,
                    ImagePath = path,
                    HasAudio = true,
                    MediaDuration = Math.Max(info.DurationSeconds, 0),
                    IsVoice = false,
                };
                (sound.AudioCodec, sound.AudioChannels) = (stream.Codec, stream.ChannelLayout.Length > 0 ? stream.ChannelLayout : $"{stream.Channels} ch");

                var made = NewClipFor(sound, clip.Start, clip.End);
                (made.AudioStreamIndex, made.MediaOffset, made.Speed, made.ParentClipId) = (stream.Index, clip.MediaOffset, clip.Speed, clip.Id);
                made.SourceDuration = clip.SourceDuration;

                var lane = StreamLaneOf(track, ordinal, stream.Index, made.Start, made.End);
                if (lane is null)
                {
                    lane = edit.InsertTrack(Document.Tracks.Count, TimelineTrackKind.Audio, $"Stream {ordinal + 1}");
                    lane.StreamOrdinal = ordinal;
                }

                edit.SetTrackParent(lane, track.Id);
                var placed = edit.PlaceClip(lane, made, allowCrossfadeOverlap: false);

                // Never past its video's block (5.4A): a stream is the clip's span exactly as it is made, but a span placed otherwise
                // (quantized another way, or a parent laid out short) must not leave sound hanging past the picture's end.
                if (placed.End > clip.End + 1e-9 && clip.End - placed.Start >= TimelineDocument.MinimumClipSeconds)
                    edit.TrimClipEnd(placed, clip.End);
            }

            clip.AudioStreamIndex = -1;
        }, undoable);

        if (added)
        {
            view.HasAudio = false;
            StatusText = $"{view.Name}: its {info.Audio.Count} audio streams each have a lane in its track's audio stack.";
            RefreshLayerRows();
        }

        return added;
    }

    // The lane under a track for the stream at this place in its file (its ordinal, 5.2), with room for a clip from to to: a second lane
    // for one ordinal only where a clip of the first is in the way. A lane from before 5.2 (no ordinal) is still taken for the same
    // stream number, as it was then; a lane a stream was moved to on its own never.
    private TimelineTrackModel? StreamLaneOf(TimelineTrackModel track, int ordinal, int stream, double from, double to) =>
        SoundLanesOf(track).FirstOrDefault(t => (t.StreamOrdinal == ordinal || (t.StreamOrdinal is null && t.Clips.Count > 0 && t.Clips[0].AudioStreamIndex == stream))
            && !t.Clips.Any(c => c.Start < to && c.End > from));

    /// <summary>
    /// The output audio track a stream lane's place goes to (5.2-AuditB: AudioTracks are what each output track is made as, by stream
    /// place: output N carries every Sub-Lane of place N); null for a row of no stream place, or a place the file last opened has none for.
    /// </summary>
    public AudioTrack? OutputTrackOf(Layer row) => StreamOrdinalOf(row) is { } ordinal && ordinal < AudioTracks.Count ? AudioTracks[ordinal] : null;

    /// <summary>The place of an audio lane's stream among its clips' streams ("Stream 1" is 0), or null for a lane of no stream: the order of a track's audio stack.</summary>
    public int? StreamOrdinalOf(Layer row) => TrackOfRow(row) is { Kind: TimelineTrackKind.Audio, StreamOrdinal: >= 0 and var ordinal } ? ordinal : null;

    /// <summary>A stream Sub-Lane's name as its header shows it ("Stream 2" until it is named), or null for a row of no stream place.</summary>
    public string? StreamLaneName(Layer row) => TrackOfRow(row) is { Kind: TimelineTrackKind.Audio, StreamOrdinal: >= 0 } lane ? lane.DisplayName : null;

    /// <summary>
    /// Names a stream's Sub-Lane, and with it the output track its stream place goes to (5.2-Arm64AndNpu, the user's answer: the
    /// output track's title follows the lane's name; the export's sound, ExportSoundPlan, takes an output's title from its output
    /// choice before its lane's name, so both are set): every Sub-Lane of that place, which that one output carries, and the output track's title
    /// together. Typed in place, a keystroke at a time, so it takes no step of Undo of its own: its caller takes the one step at the
    /// first keystroke (the lanes' names and the output tracks' titles are both in a step). False for a row of no stream place.
    /// </summary>
    public bool RenameStreamLane(Layer row, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || StreamOrdinalOf(row) is not { } ordinal)
            return false;

        var lanes = Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Audio && t.StreamOrdinal == ordinal && t.Name != name).ToList();
        if (lanes.Count > 0)
        {
            Edit($"rename {name}", edit =>
            {
                foreach (var lane in lanes)
                    edit.SetTrack(lane, name: name);
            }, undoable: false);
        }

        if (ordinal < AudioTracks.Count)
            AudioTracks[ordinal].Title = name;
        return true;
    }

    /// <summary>
    /// Moves a sound clip off the lane it shares onto a lane of its own (5.2, the user's call: a stream dragged out of its row): a new
    /// lane in the audio stack of <paramref name="underRow"/>'s video track, or with none an Audio track of its own. It keeps its time
    /// and its parent clip, so it still moves, trims and splits with its video. A lane it leaves empty goes. One step of Undo.
    /// </summary>
    public bool MoveSoundToOwnLane(Layer clip, Layer? underRow)
    {
        if (ClipOf(clip) is not { ContentKind: LayerKind.Audio } model || Document.TrackOf(model) is not { IsLocked: false } from)
            return false;

        TimelineTrackModel? under = null;
        if (underRow is not null)
        {
            if (TrackOfRow(underRow) is not { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, IsLocked: false } found)
                return false;
            under = found;
        }

        var moved = Edit(under is null ? $"move {clip.Name} to an Audio track of its own" : $"move {clip.Name} to a lane of its own", edit =>
        {
            edit.MoveClip(model, from, model.Start, insertTrackAtIndex: Document.Tracks.IndexOf(from) + 1, allowCrossfadeOverlap: false);
            var lane = Document.TrackOf(model)!;
            lane.StreamOrdinal = -1;
            edit.SetTrack(lane, name: clip.Name);
            edit.SetTrackParent(lane, under?.Id);
            if (from.Clips.Count == 0)
                edit.RemoveTrack(from);
        });

        if (moved)
        {
            StatusText = under is null ? $"{clip.Name} is on an Audio track of its own." : $"{clip.Name} is on a lane of its own under {underRow!.Name}.";
            RefreshLayerRows();
        }

        return moved;
    }

    /// <summary>Whether a sound clip can be moved onto a lane of its own under this row's track (a drag onto a video row, 5.2).</summary>
    public bool CanMoveSoundUnder(Layer clip, Layer row) =>
        ClipOf(clip) is { ContentKind: LayerKind.Audio } model && Document.TrackOf(model) is { IsLocked: false }
        && TrackOfRow(row) is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, IsLocked: false };

    /// <summary>Whether a clip's file may hold streams it has not shown yet: a video from a file, with its sound still on the clip.</summary>
    public bool CanUnpackStreams(Layer clip) => ClipOf(clip) is { ContentKind: LayerKind.VideoFile, AudioStreamIndex: >= 0 };

    /// <summary>A clip's file looked at, its streams unpacked (the track menu): one step of Undo.</summary>
    public async Task UnpackAudioStreamsAsync(Layer clip)
    {
        if (await ProbeStreamsAsync(clip) is { } info)
        {
            if (!UnpackStreams(clip, info, undoable: true))
                StatusText = $"{clip.Name} has {info.Audio.Count} audio stream{(info.Audio.Count == 1 ? "" : "s")}: nothing to unpack.";
        }
    }

    private async Task<MediaInfo?> ProbeStreamsAsync(Layer clip)
    {
        var path = clip.ImagePath.Trim().Trim('"');
        if (path.Length == 0 || !FileState.Exists(path))
            return null;

        try
        {
            return await Task.Run(() => MediaProbe.ProbeAsync(path, _shutdown.Token, WorkLane.Background));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A project opened: its videos' streams shown, in the background, as a video dropped now has them. Part of opening it, no step of
    /// Undo; what it changes before the user's first touch is the saved state (<see cref="NoteUserInput"/>).
    /// </summary>
    private async Task UnpackProjectStreamsAsync()
    {
        // Pass 5.4D-TabPerf: a probe that comes back after its tab went behind is another project's now, and does nothing here (the
        // tab starts its unpacking again as it comes back: AttachTab).
        var generation = _projectGeneration;
        _unpacking++;
        try
        {
            foreach (var view in Layers.Where(l => l.ClipId != Guid.Empty && CanUnpackStreams(l)).DistinctBy(l => l.ImagePath, StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (await ProbeStreamsAsync(view) is not { Audio.Count: > 1 } info || generation != _projectGeneration
                    || IsTimelineDragHeld?.Invoke() == true || IsRestoringTimeline)
                {
                    if (generation != _projectGeneration)
                        return;
                    continue;
                }

                // Every clip of that file: they share its lanes.
                foreach (var clip in Layers.Where(l => CanUnpackStreams(l) && string.Equals(l.ImagePath, view.ImagePath, StringComparison.OrdinalIgnoreCase)).ToList())
                    UnpackStreams(clip, info, undoable: false);
            }
        }
        finally
        {
            if (generation == _projectGeneration)
                _unpacking = Math.Max(_unpacking - 1, 0);
        }
    }

    // ----- The master mute -----

    /// <summary>The tracks of the sound lanes in a track's audio stack.</summary>
    private IEnumerable<TimelineTrackModel> SoundLanesOf(TimelineTrackModel track) =>
        Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId == track.Id);

    /// <summary>Whether all that a video track's row can be heard is muted: its clips' sound and the lanes under it.</summary>
    public bool IsRowSoundMuted(Layer row)
    {
        if (TrackOfRow(row) is not { } track)
            return row.IsMuted;

        var lanes = SoundLanesOf(track).SelectMany(t => t.Clips).ToList();
        var own = track.Clips.Where(c => c.HasSound).ToList();
        if (lanes.Count == 0 && own.Count == 0)
            return row.IsMuted;
        return lanes.All(c => c.IsHidden) && own.All(c => c.IsMuted);
    }

    /// <summary>
    /// The master mute on every video track's header: all of what the track is heard by at once, in one step of Undo: the sound of
    /// the track's clips and every lane in its audio stack; the picture stays.
    /// </summary>
    public void SetTrackSoundMuted(Layer row, bool muted)
    {
        if (TrackOfRow(row) is not { } track)
        {
            row.IsMuted = muted;
            return;
        }

        if (track.Clips.Count == 0 && !SoundLanesOf(track).Any())
        {
            row.IsMuted = muted;
            return;
        }

        Edit(muted ? $"mute the sound of {row.Name}" : $"unmute the sound of {row.Name}", edit =>
        {
            foreach (var clip in track.Clips)
                edit.SetClipFlags(clip, muted: muted);
            foreach (var lane in SoundLanesOf(track))
            {
                foreach (var clip in lane.Clips)
                    edit.SetClipFlags(clip, hidden: muted);
            }
        });
    }

    // ----- Captions scoped to a track -----

    /// <summary>
    /// What a track's or a clip's Generate Auto-Captions can listen to: every audio stream of it and nothing else (the user's directive).
    /// Null for what is no track's (the Add Layer menu): the whole timeline.
    /// </summary>
    public IReadOnlyList<CaptionSource>? CaptionStreamsOf(object? requested)
    {
        switch (requested)
        {
            case AudioTrack:
                // The file last opened's streams (an output track's menu).
                return OpenedFile.Length > 0 ? AudioTracks.Select(t => new CaptionSource(CaptionSourceKind.Clip, OpenedFile, t.Index)).ToList() : [];
            case Layer row when TrackOfRow(row) is { } track:
                // A lane speaks for the track it is under.
                var owner = row.IsAudio && track.ParentTrackId is { } parentId && Document.Tracks.Find(t => t.Id == parentId) is { } parent ? parent : track;
                var sources = owner.Clips.Concat(SoundLanesOf(owner).SelectMany(t => t.Clips))
                    .Where(c => c.HasSound && c.SourcePath is { Length: > 0 })
                    .Select(c => new CaptionSource(CaptionSourceKind.Clip, c.SourcePath!, Math.Max(c.AudioStreamIndex, 0)))
                    .Distinct().ToList();
                return sources;
            default:
                return null;
        }
    }

    /// <summary>The lane or clip a source is heard from, for its name in the Auto-Captions dialog.</summary>
    private TimelineTrackModel? CaptionLaneOf(CaptionSource source) =>
        CaptionClips(source).Select(c => c.Track).FirstOrDefault(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId is not null);
}
