using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Every video track is equal (5.1-TrackEqualityAndVram, the user's directive) -----
// A recording with several audio streams shows them all, whichever track it is on. The main video's streams are its AudioTracks;
// any other video's are sound clips of their own, one lane per stream in its track's audio stack, children of the video's clip,
// so that a split, trim, move, fold or delete of the clip carries them along (TimelineTransaction). A file with one stream keeps it
// on its clip: nothing to choose between.
public partial class MainViewModel
{
    /// <summary>
    /// The audio streams of a video clip that is not the main video's, each a sound clip on a lane of its own under the clip's track, and
    /// the clip itself made silent: nothing is heard twice. Clips of one file on one track share their lanes. Nothing for a file with one
    /// stream, or a clip already unpacked.
    /// </summary>
    private bool UnpackStreams(Layer view, MediaInfo info, bool undoable)
    {
        if (ClipOf(view) is not { ContentKind: LayerKind.VideoFile, AudioStreamIndex: >= 0 } clip || Document.TrackOf(clip) is not { } track || info.Audio.Count < 2)
            return false;

        var path = clip.SourcePath ?? view.ImagePath;
        var added = Edit($"unpack the audio streams of {view.Name}", edit =>
        {
            foreach (var stream in info.Audio)
            {
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

                var lane = StreamLaneOf(track, stream.Index, made.Start, made.End)
                    ?? edit.InsertTrack(Document.Tracks.Count, TimelineTrackKind.Audio, sound.Name);
                edit.SetTrackParent(lane, track.Id);
                edit.PlaceClip(lane, made, allowCrossfadeOverlap: false);
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

    // A lane under a track that already holds this stream and has room for a clip.
    private TimelineTrackModel? StreamLaneOf(TimelineTrackModel track, int stream, double from, double to) =>
        Document.Tracks.FirstOrDefault(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId == track.Id && t.Clips.Count > 0
            && t.Clips[0].AudioStreamIndex == stream && !t.Clips.Any(c => c.Start < to && c.End > from));

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
        foreach (var view in Layers.Where(l => l.ClipId != Guid.Empty && CanUnpackStreams(l)).DistinctBy(l => l.ImagePath, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (await ProbeStreamsAsync(view) is not { Audio.Count: > 1 } info || IsTimelineDragHeld?.Invoke() == true || IsRestoringTimeline)
                continue;

            // Every clip of that file: they share its lanes.
            foreach (var clip in Layers.Where(l => CanUnpackStreams(l) && string.Equals(l.ImagePath, view.ImagePath, StringComparison.OrdinalIgnoreCase)).ToList())
                UnpackStreams(clip, info, undoable: false);
        }
    }

    // ----- The master mute -----

    /// <summary>The tracks of the sound lanes in a track's audio stack.</summary>
    private IEnumerable<TimelineTrackModel> SoundLanesOf(TimelineTrackModel track) =>
        Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId == track.Id);

    /// <summary>Whether all that a video track's row can be heard is muted: the main file's streams, or its clips' sound and the lanes under it.</summary>
    public bool IsRowSoundMuted(Layer row)
    {
        if (row.IsMainVideo)
            return AudioTracks.Count > 0 && AudioTracks.All(t => t.IsMuted);
        if (TrackOfRow(row) is not { } track)
            return row.IsMuted;

        var lanes = SoundLanesOf(track).SelectMany(t => t.Clips).ToList();
        var own = track.Clips.Where(c => c.HasSound).ToList();
        if (lanes.Count == 0 && own.Count == 0)
            return row.IsMuted;
        return lanes.All(c => c.IsHidden) && own.All(c => c.IsMuted);
    }

    /// <summary>
    /// The master mute on every video track's header: all of what the track is heard by at once, in one step of Undo. The main video's
    /// streams, or the sound of the track's clips and every lane in its audio stack; the picture stays.
    /// </summary>
    public void SetTrackSoundMuted(Layer row, bool muted)
    {
        if (row.IsMainVideo)
        {
            Checkpoint(muted ? $"mute the sound of {row.Name}" : $"unmute the sound of {row.Name}");
            foreach (var stream in AudioTracks)
                stream.IsMuted = muted;
            GenerateCommand();
            return;
        }

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
        var mainStreams = HasSource ? AudioTracks.Select(t => new CaptionSource(CaptionSourceKind.MainFile, LocalMediaPath, t.Index)).ToList() : [];
        switch (requested)
        {
            case AudioTrack:
                return mainStreams;
            case Layer { IsMainVideo: true }:
                return mainStreams;
            case Layer row when TrackOfRow(row) is { } track:
                // A lane speaks for the track it is under.
                var owner = row.IsAudio && track.ParentTrackId is { } parentId && Document.Tracks.Find(t => t.Id == parentId) is { } parent ? parent : track;
                if (owner.Clips.Any(c => c.ContentKind == LayerKind.MainVideo))
                    return mainStreams;

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
