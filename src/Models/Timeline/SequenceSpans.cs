namespace HandPegApp.Models.Timeline;

// ----- Sequence as file spans -----
// No track or file is privileged: queries once asked of a "main video" (clip placement, whole-file identity, stream-copy cuts) are
// answered from all clips showing a file's picture. Pure single-pass functions of a document: no state between calls (documents
// mutate in place) and no allocation beyond returned lists.

/// <summary>
/// A picture clip as a Sequence span: from <paramref name="Start"/> to <paramref name="End"/> (Sequence s) it plays
/// <paramref name="Path"/> from <paramref name="MediaOffset"/> (file s, video clock) at <paramref name="Speed"/> using
/// <paramref name="VideoStream"/>.
/// </summary>
/// <param name="Path">Normalized file path (trimmed, unquoted).</param>
public readonly record struct ClipSpan(Guid ClipId, string Path, double Start, double End, double MediaOffset, double Speed, int VideoStream)
{
    /// <summary>How long it lasts on the Sequence, in seconds.</summary>
    public double Duration => End - Start;

    /// <summary>File time at a Sequence time (linear extrapolation outside the span).</summary>
    public double ToFile(double sequence) => MediaOffset + (sequence - Start) * Speed;

    /// <summary>The moment of the Sequence a moment of its file is shown at, on the line the span lies on.</summary>
    public double ToSequence(double file) => Start + (file - MediaOffset) / Speed;
}

/// <summary>
/// Stream-copy (Copy export) plan for one file (<see cref="SequenceSpans.CopyCutOf"/>): the file, its non-overlapping speed-1 pieces in
/// order, and names of omitted pictures (other files, images, text, other speeds) for the warning.
/// </summary>
public sealed record CopyCut(string Path, IReadOnlyList<ClipSpan> Pieces, IReadOnlyList<string> LeftOut)
{
    /// <summary>True when a cut list is needed (multiple pieces, or one piece that is not the whole file from its start).</summary>
    public bool IsCut { get; init; }
}

/// <summary>Read-only queries over a document's picture spans.</summary>
public static class SequenceSpans
{
    // Two moments this close are one (a clip "at the start", a file's offset of "none"), as the lowering counts them.
    private const double Moment = 0.001;

    // Tolerances: speed counts as 1, and a duration as the whole file (about one frame).
    private const double SameSpeed = 0.0005, SameLength = 0.02;

    /// <summary>
    /// All visible picture clips: enabled top-level <see cref="LayerKind.VideoFile"/> clips (no <see cref="TimelineClip.ParentClipId"/>,
    /// so no Cutouts or Sub-Lane clips) on unhidden Video/Switch tracks (<see cref="TimelineDocument.IsHiddenWithParents"/>), longer than
    /// 1 ms. Sorted by start, lower track first on ties.
    /// </summary>
    public static IReadOnlyList<ClipSpan> PictureSpans(TimelineDocument document) => Collect(document, null);

    /// <summary><see cref="PictureSpans"/> for one file (normalized, case-insensitive path), sorted by start.</summary>
    public static IReadOnlyList<ClipSpan> SpansOf(TimelineDocument document, string path) => Collect(document, Clean(path));

    /// <summary>
    /// Playback/export length without trailing black: end of the last picture (video, Cutout, image or title on any Video/Switch track),
    /// extended only by standalone audio (top-level Audio-track clips such as music or voice-over). L-cut tails, slipped audio and
    /// adjustments add nothing. Without pictures: <see cref="TimelineDocument.TotalDuration"/>.
    /// </summary>
    public static double ProgramSeconds(TimelineDocument document)
    {
        double picture = 0, loose = 0;
        var any = false;
        foreach (var track in document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch && clip.ContentKind is LayerKind.VideoFile or LayerKind.Image or LayerKind.Text)
                {
                    any = true;
                    picture = Math.Max(picture, clip.End);
                }
                else if (track.Kind == TimelineTrackKind.Audio && clip is { ContentKind: LayerKind.Audio, ParentClipId: null })
                {
                    loose = Math.Max(loose, clip.End + Math.Max(clip.AudioLinked ? clip.AudioTailOutSeconds : clip.AudioSlipSeconds, 0));
                }
            }
        }

        return any ? Math.Max(picture, loose) : document.TotalDuration;
    }

    /// <summary>
    /// Files of which the Sequence is exactly one whole, unaltered clip: a <see cref="PictureSpans"/> entry starting at 0 from file time 0,
    /// speed 1, full length (within a frame; video end per <see cref="TimelineClip.WholeDuration"/>) and ending at
    /// <see cref="TimelineDocument.TotalDuration"/>. Their soft subtitles, chapters and copied audio stay in sync regardless of overlays.
    /// Normalized, case-insensitive paths.
    /// </summary>
    public static IReadOnlySet<string> WholeFiles(TimelineDocument document)
    {
        var whole = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var length = document.TotalDuration;
        foreach (var (_, clip, path) in Shown(document))
        {
            if (clip.Start < Moment && clip.MediaOffset < Moment && Math.Abs(clip.Speed - 1) < SameSpeed && clip.SourceDuration > 0
                && Math.Abs(clip.Duration - clip.WholeDuration) < SameLength && Math.Abs(length - clip.End) < SameLength)
            {
                whole.Add(path);
            }
        }

        return whole;
    }

    /// <summary>
    /// Copy export plan: the file with the most on-screen seconds (ties: shown first). Pieces are its speed-1 spans by start, overlaps
    /// resolved later-wins (earlier piece truncated; sub-millisecond remnants dropped). All other pictures (other files, images, text,
    /// adjustments, Cutouts, other speeds) are listed once in <see cref="CopyCut.LeftOut"/> by clip name, else file name, else kind.
    /// Null without picture clips; empty pieces when every clip is off-speed (<see cref="CopyCut.IsCut"/> true).
    /// </summary>
    public static CopyCut? CopyCutOf(TimelineDocument document)
    {
        var spans = PictureSpans(document);
        if (spans.Count == 0)
            return null;

        // The file shown longest; the spans come in the order they start, so the first file met wins a tie.
        var seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var (file, most) = ("", -1.0);
        foreach (var span in spans)
            seconds[span.Path] = seconds.GetValueOrDefault(span.Path) + span.Duration;
        foreach (var span in spans)
        {
            if (seconds[span.Path] > most)
                (file, most) = (span.Path, seconds[span.Path]);
        }

        // Speed-1 pieces, overlaps resolved later-wins from the end backwards. A fully covered piece is not reported as left out
        // (it is the copied file itself).
        var pieces = new List<ClipSpan>();
        var ofFile = new HashSet<Guid>();
        foreach (var span in spans)
        {
            if (string.Equals(span.Path, file, StringComparison.OrdinalIgnoreCase) && Math.Abs(span.Speed - 1) < SameSpeed)
            {
                pieces.Add(span);
                ofFile.Add(span.ClipId);
            }
        }

        for (var i = pieces.Count - 2; i >= 0; i--)
        {
            if (pieces[i].End > pieces[i + 1].Start)
                pieces[i] = pieces[i] with { End = Math.Max(pieces[i + 1].Start, pieces[i].Start) };
            if (pieces[i].End - pieces[i].Start < Moment)
                pieces.RemoveAt(i);
        }

        // Every other picture shown, named once each, in the order they start.
        var leftOut = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in Drawn(document))
        {
            if (!ofFile.Contains(clip.Id) && NameOf(clip) is var name && named.Add(name))
                leftOut.Add(name);
        }

        // A single piece is uncut only if it plays the whole file from 0 (to the video end).
        var whole = false;
        if (pieces is [var only])
        {
            var length = document.FindClip(only.ClipId)?.WholeDuration ?? 0;
            whole = only.MediaOffset < Moment && length > 0 && Math.Abs(only.Duration - length) < SameLength;
        }

        return new CopyCut(file, pieces, leftOut) { IsCut = !whole };
    }

    // ----- Reading the document -----

    private static string Clean(string? path) => (path ?? "").Trim().Trim('"');

    /// <summary>True for a Video/Switch track not hidden by itself or any ancestor.</summary>
    private static bool ShowsPictures(TimelineDocument document, TimelineTrackModel track) =>
        track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch && !document.IsHiddenWithParents(track);

    /// <summary><see cref="PictureSpans"/> clips with track index and normalized path, bottom track first, each track in time order.</summary>
    private static IEnumerable<(int TrackIndex, TimelineClip Clip, string Path)> Shown(TimelineDocument document)
    {
        for (var t = 0; t < document.Tracks.Count; t++)
        {
            var track = document.Tracks[t];
            if (!ShowsPictures(document, track))
                continue;

            foreach (var clip in track.Clips)
            {
                if (clip is { ContentKind: LayerKind.VideoFile, ParentClipId: null, IsHidden: false } && clip.Duration > Moment && Clean(clip.SourcePath) is { Length: > 0 } path)
                    yield return (t, clip, path);
            }
        }
    }

    /// <summary>All visible pictures of any kind (including Cutouts and adjustments), by start, lower track first.</summary>
    private static List<TimelineClip> Drawn(TimelineDocument document)
    {
        var drawn = new List<(int TrackIndex, TimelineClip Clip)>();
        for (var t = 0; t < document.Tracks.Count; t++)
        {
            var track = document.Tracks[t];
            if (!(ShowsPictures(document, track) || (track.Kind == TimelineTrackKind.Adjustment && !document.IsHiddenWithParents(track))))
                continue;

            foreach (var clip in track.Clips)
            {
                if (!clip.IsHidden && clip.Duration > Moment && clip.ContentKind is not (LayerKind.Audio or LayerKind.Captions or LayerKind.Background))
                    drawn.Add((t, clip));
            }
        }

        drawn.Sort(static (a, b) => a.Clip.Start != b.Clip.Start ? a.Clip.Start.CompareTo(b.Clip.Start) : a.TrackIndex.CompareTo(b.TrackIndex));
        return drawn.ConvertAll(d => d.Clip);
    }

    /// <summary>Visible clip spans (optionally for <paramref name="path"/> only), by start, lower track first.</summary>
    private static List<ClipSpan> Collect(TimelineDocument document, string? path)
    {
        var found = new List<(int TrackIndex, ClipSpan Span)>();
        foreach (var (t, clip, file) in Shown(document))
        {
            if (path is null || string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
                found.Add((t, new ClipSpan(clip.Id, file, clip.Start, clip.End, clip.MediaOffset, clip.Speed, clip.VideoStreamIndex)));
        }

        // (A track's clips are in time and apart: two spans that start together are of two tracks.)
        found.Sort(static (a, b) => a.Span.Start != b.Span.Start ? a.Span.Start.CompareTo(b.Span.Start) : a.TrackIndex.CompareTo(b.TrackIndex));
        return found.ConvertAll(f => f.Span);
    }

    /// <summary>What a clip is called where a warning names it: its name, else its file's name, else its kind.</summary>
    private static string NameOf(TimelineClip clip)
    {
        if (clip.State?.Name is { } name && !string.IsNullOrWhiteSpace(name))
            return name;
        var file = System.IO.Path.GetFileName(Clean(clip.SourcePath));
        return file.Length > 0 ? file : clip.ContentKind.ToString();
    }
}
