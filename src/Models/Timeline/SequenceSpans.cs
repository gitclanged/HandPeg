namespace HandPegApp.Models.Timeline;

// ----- The Sequence as stretches of files (5.2-AuditB) -----
// No track and no file is privileged: what used to be asked of "the main video" (where its clips are, whether it is the
// whole sequence untouched, what a stream copy cuts) is asked of the clips that show a file's picture, wherever they are.
// Pure functions of a document, read in one pass over its clips: nothing is kept between calls (the document is changed in
// place by every edit), and nothing is allocated beyond the lists returned.

/// <summary>
/// One clip that shows a file's picture, as a stretch of the Sequence: from <paramref name="Start"/> to
/// <paramref name="End"/> (seconds of the Sequence) it plays <paramref name="Path"/> from <paramref name="MediaOffset"/>
/// (seconds of the file, on its picture's clock) at <paramref name="Speed"/>, from its video stream
/// <paramref name="VideoStream"/>.
/// </summary>
/// <param name="Path">The clip's file, cleaned (trimmed, its quotes taken off).</param>
public readonly record struct ClipSpan(Guid ClipId, string Path, double Start, double End, double MediaOffset, double Speed, int VideoStream)
{
    /// <summary>How long it lasts on the Sequence, in seconds.</summary>
    public double Duration => End - Start;

    /// <summary>The moment of its file shown at a moment of the Sequence (inside the span or not: the line it lies on).</summary>
    public double ToFile(double sequence) => MediaOffset + (sequence - Start) * Speed;

    /// <summary>The moment of the Sequence a moment of its file is shown at, on the line the span lies on.</summary>
    public double ToSequence(double file) => Start + (file - MediaOffset) / Speed;
}

/// <summary>
/// What a Copy export (a stream copy: nothing encoded) cuts from one file (<see cref="SequenceSpans.CopyCutOf"/>): the
/// file, the pieces of it the Sequence shows at speed 1 in the order they come (no two overlapping), and the names of the
/// pictures a copy leaves out (other files, images, words, the file at another speed...): what to warn of.
/// </summary>
public sealed record CopyCut(string Path, IReadOnlyList<ClipSpan> Pieces, IReadOnlyList<string> LeftOut)
{
    /// <summary>More than one piece, or one piece that is not the whole file from its start (a Copy needs a cuts list).</summary>
    public bool IsCut { get; init; }
}

/// <summary>The Sequence as stretches of the files whose pictures it shows (5.2-AuditB): read from a document, never changing it.</summary>
public static class SequenceSpans
{
    // Two moments this close are one (a clip "at the start", a file's offset of "none"), as the lowering counts them.
    private const double Moment = 0.001;

    // A speed this close to 1 is 1, and a length this close to its file's is the whole file (a frame or so, as 4.3B counted).
    private const double SameSpeed = 0.0005, SameLength = 0.02;

    /// <summary>
    /// Every clip the Sequence shows a file's picture by: a top-level video clip (<see cref="LayerKind.VideoFile"/>, no
    /// <see cref="TimelineClip.ParentClipId"/>: no Cutout, no Sub-Lane clip) that is not switched off, on a Video or Switch
    /// track that is not hidden (its own header or any it lies under: <see cref="TimelineDocument.IsHiddenWithParents"/>),
    /// lasting more than a millisecond. In the order they start; two that start together, the lower track's first.
    /// </summary>
    public static IReadOnlyList<ClipSpan> PictureSpans(TimelineDocument document) => Collect(document, null);

    /// <summary>The <see cref="PictureSpans"/> of one file (its path cleaned and compared without regard to case), in the order they start.</summary>
    public static IReadOnlyList<ClipSpan> SpansOf(TimelineDocument document, string path) => Collect(document, Clean(path));

    /// <summary>
    /// The files the whole Sequence is exactly one clip of: one of <see cref="PictureSpans"/> that starts the Sequence with
    /// its file's first moment, at speed 1, plays the whole file (its length its file's, to a frame or so) and ends where
    /// the Sequence does (<see cref="TimelineDocument.TotalDuration"/>). Whatever lies over it, such a file can be read as it
    /// is: its soft subtitles, its chapters and a stream of its sound copied are still in time. Cleaned paths, compared
    /// without regard to case.
    /// </summary>
    public static IReadOnlySet<string> WholeFiles(TimelineDocument document)
    {
        var whole = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var length = document.TotalDuration;
        foreach (var (_, clip, path) in Shown(document))
        {
            if (clip.Start < Moment && clip.MediaOffset < Moment && Math.Abs(clip.Speed - 1) < SameSpeed && clip.SourceDuration > 0
                && Math.Abs(clip.Duration - clip.SourceDuration) < SameLength && Math.Abs(length - clip.End) < SameLength)
            {
                whole.Add(path);
            }
        }

        return whole;
    }

    /// <summary>
    /// What a Copy export cuts: the file with the most seconds of picture on the Sequence (of <see cref="PictureSpans"/>;
    /// of two with as many, the one shown first). Its pieces are its spans at speed 1, in the order they start, two that
    /// overlap laid as one track lays them, the later winning (the earlier ends where the later starts; a piece left
    /// shorter than a millisecond goes). Every other picture shown (a clip of another file, an image, words, an adjustment,
    /// a Cutout, the file at another speed) is left out, and named once in <see cref="CopyCut.LeftOut"/>: the clip's name,
    /// else its file's, else its kind. Null when the Sequence shows no file's picture. The pieces are empty when every clip
    /// of the file runs at another speed (then nothing can be copied: <see cref="CopyCut.IsCut"/> is true).
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

        // Its pieces at speed 1; an overlap is laid out later-wins, from the last back (as 4.2.0 laid out a track that ran
        // into itself). A clip of them wholly under a later one is not left out: it is the file, and the file is what is copied.
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

        // A single piece is the file as it is only when it plays the whole of it from its first moment.
        var whole = false;
        if (pieces is [var only])
        {
            var length = document.FindClip(only.ClipId)?.SourceDuration ?? 0;
            whole = only.MediaOffset < Moment && length > 0 && Math.Abs(only.Duration - length) < SameLength;
        }

        return new CopyCut(file, pieces, leftOut) { IsCut = !whole };
    }

    // ----- Reading the document -----

    private static string Clean(string? path) => (path ?? "").Trim().Trim('"');

    /// <summary>Whether a track shows pictures that are seen: a Video or Switch track whose header (or that of any track it lies under) does not hide it.</summary>
    private static bool ShowsPictures(TimelineDocument document, TimelineTrackModel track) =>
        track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch && !document.IsHiddenWithParents(track);

    /// <summary>The clips of <see cref="PictureSpans"/> with their track's place and their cleaned file, in the tracks' order (bottom first), each track's in time.</summary>
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

    /// <summary>Every picture the Sequence shows, of any kind (a Cutout and an adjustment's effect too), in the order they start (the lower track's first).</summary>
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

    /// <summary>The spans of the clips shown (of one file, when <paramref name="path"/> is given), in the order they start (the lower track's first).</summary>
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
