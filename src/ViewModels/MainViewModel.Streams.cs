using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Stream unpacking (all video tracks equal) -----
// Multi-stream recordings expose every audio stream on any track: each becomes a child audio clip on a per-stream lane in the
// track's audio stack, so split/trim/move/fold/delete carry them (TimelineTransaction). Single-stream files keep audio on the clip.
// AudioTracks only describe output tracks.
// Lanes are per track and keyed by StreamOrdinal, so every clip's Nth stream shares the "Stream N" row. A stream can be moved to
// its own lane or an Audio track (MoveSoundToOwnLane; drag onto a video row).
public partial class MainViewModel
{
    /// <summary>
    /// Unpacks a video clip's audio streams into child audio clips on per-ordinal lanes (shared across the track) and mutes the clip
    /// so nothing plays twice. No-op for single-stream files or already-unpacked clips.
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

                // Clamp to the parent block so differing quantization or a short parent never leaves audio past the picture end.
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

    // Finds a lane for a stream ordinal with room for [from, to); adds a second lane only on collision. Legacy lanes without an
    // ordinal match by stream number; lanes a stream was manually moved to are never reused.
    private TimelineTrackModel? StreamLaneOf(TimelineTrackModel track, int ordinal, int stream, double from, double to) =>
        SoundLanesOf(track).FirstOrDefault(t => (t.StreamOrdinal == ordinal || (t.StreamOrdinal is null && t.Clips.Count > 0 && t.Clips[0].AudioStreamIndex == stream))
            && !t.Clips.Any(c => c.Start < to && c.End > from));

    /// <summary>
    /// Output audio track for a stream lane (output N carries all Sub-Lanes of ordinal N); null for non-stream rows or ordinals the
    /// last opened file lacks.
    /// </summary>
    public AudioTrack? OutputTrackOf(Layer row) => StreamOrdinalOf(row) is { } ordinal && ordinal < AudioTracks.Count ? AudioTracks[ordinal] : null;

    /// <summary>Stream ordinal of an audio lane ("Stream 1" = 0), or null for non-stream lanes; orders the audio stack.</summary>
    public int? StreamOrdinalOf(Layer row) => TrackOfRow(row) is { Kind: TimelineTrackKind.Audio, StreamOrdinal: >= 0 and var ordinal } ? ordinal : null;

    /// <summary>Stream Sub-Lane header name ("Stream N" until renamed), or null for non-stream rows.</summary>
    public string? StreamLaneName(Layer row) => TrackOfRow(row) is { Kind: TimelineTrackKind.Audio, StreamOrdinal: >= 0 } lane ? lane.DisplayName : null;

    /// <summary>
    /// Renames all Sub-Lanes of a stream ordinal and the matching output track title (ExportSoundPlan prefers the output title, so both
    /// are set). Called per keystroke without its own undo step (the caller takes one on the first keystroke). False for non-stream rows.
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
    /// Moves an audio clip from a shared lane to its own lane under <paramref name="underRow"/>'s video track (or a new Audio track),
    /// keeping time and parent so it still follows the video. An emptied lane is removed. One undo step.
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

    /// <summary>Whether an audio clip can move to its own lane under this row's track (drag onto a video row).</summary>
    public bool CanMoveSoundUnder(Layer clip, Layer row) =>
        ClipOf(clip) is { ContentKind: LayerKind.Audio } model && Document.TrackOf(model) is { IsLocked: false }
        && TrackOfRow(row) is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, IsLocked: false };

    /// <summary>Whether a file video clip may have unpackable streams (audio still on the clip).</summary>
    public bool CanUnpackStreams(Layer clip) => ClipOf(clip) is { ContentKind: LayerKind.VideoFile, AudioStreamIndex: >= 0 };

    /// <summary>Probes a clip's file and unpacks its streams (track menu). One undo step.</summary>
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
    /// Background stream unpack for an opened project's videos (as for new drops). Part of the open: no undo step, and changes before
    /// the first user input fold into the saved state (<see cref="NoteUserInput"/>).
    /// </summary>
    private async Task UnpackProjectStreamsAsync()
    {
        // A probe completing after its tab was backgrounded belongs to another project: no-op (AttachTab restarts unpacking).
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

                // All clips of that file share its lanes.
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

    /// <summary>Audio lane tracks in a track's audio stack.</summary>
    private IEnumerable<TimelineTrackModel> SoundLanesOf(TimelineTrackModel track) =>
        Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId == track.Id);

    /// <summary>
    /// Whether a video track produces audio (shows the header speaker): an audio lane with clips, or a clip with audio. Cutout, mask
    /// and image tracks never do (Cutout audio belongs to the source clip).
    /// </summary>
    public bool RowHasOwnSound(Layer row) => TrackOfRow(row) is { } track
        ? SoundLanesOf(track).Any(t => t.Clips.Count > 0) || track.Clips.Any(c => c.HasSound)
        : row.CarriesSound;

    /// <summary>Whether all audio of a video track (clips and lanes) is muted.</summary>
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

    /// <summary>Video track header master mute: mutes clip audio and all audio-stack lanes in one undo step; picture unaffected.</summary>
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
    /// Caption sources for a track's or clip's Generate Auto-Captions: exactly its audio streams. Null context (Add Layer menu) means
    /// the whole timeline.
    /// </summary>
    public IReadOnlyList<CaptionSource>? CaptionStreamsOf(object? requested)
    {
        switch (requested)
        {
            case AudioTrack:
                // Last opened file's streams (output track menu).
                return OpenedFile.Length > 0 ? AudioTracks.Select(t => new CaptionSource(CaptionSourceKind.Clip, OpenedFile, t.Index)).ToList() : [];
            case Layer row when TrackOfRow(row) is { } track:
                // A lane resolves to its parent track.
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

    /// <summary>Lane or clip a source plays from, for its Auto-Captions dialog label.</summary>
    private TimelineTrackModel? CaptionLaneOf(CaptionSource source) =>
        CaptionClips(source).Select(c => c.Track).FirstOrDefault(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId is not null);
}
