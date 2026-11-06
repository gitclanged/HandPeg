using System.Diagnostics;
using System.Globalization;
using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- Long recordings trimmed into a project bundle (Pass 5.2C, AppSettings.TrimLongVodsOnBundle) -----
//
// A bundle (.hpb) of a project cut from a two-hour stream recording would carry the whole recording, gigabytes of it, for the few minutes
// the Sequence uses. With the setting on (as it comes), a video file longer than LongSeconds of which the clips use less than MostUsedShare
// goes into the bundle as the stretches they use instead, each with HandleSeconds more at both ends (room to trim, a J or L cut, a
// crossfade), stretches nearer than JoinGapSeconds made one. Each is a lossless stream copy (FFmpeg -c copy, every stream: all of an OBS
// recording's audio tracks) that starts on the key frame at or before its first moment, so nothing is decoded or encoded again. The copy's
// first frame is checked to be that very key frame (its packet's size and flag): where it is not, or FFmpeg is missing or fails, the
// whole file goes in as before. The bundle's project names the stretches, each clip of the file (its Cutouts and stream lanes too, the
// stretches Split & Mark folded out of it) on the one that holds it, its MediaOffset moved by where the stretch starts in the file, so
// every frame and sample lands where it was. The working project is never touched: only the copy that is packed.
public static class VodTrim
{
    public const double LongSeconds = 120, MostUsedShare = 0.6, HandleSeconds = 5, JoinGapSeconds = 30;

    /// <summary>A stretch of a file to keep, in its media seconds (its first frame 0), handles included.</summary>
    public readonly record struct Stretch(double From, double To);

    /// <summary>A file to trim: its length and the stretches its clips use.</summary>
    public sealed record Planned(string Path, double Duration, double UsedShare, IReadOnlyList<Stretch> Stretches);

    /// <summary>A stretch cut into a file of its own: where its first frame is in the source's media seconds, and how long it is.</summary>
    public sealed record Piece(string Path, double From, double Length);

    // Every clip of a project that plays a file: the tracks' and the folds' (a stretch Split & Mark folded out can be put back).
    private static IEnumerable<TimelineClip> ClipsOf(ProjectState state) =>
        state.Timeline.Tracks.SelectMany(t => t.Clips).Concat((state.Timeline.Folds ?? []).SelectMany(f => f.Clips).Select(f => f.Clip))
            .Where(c => c is { PlaysFile: true, SourcePath.Length: > 0 });

    // The media seconds a clip reads: its picture's, its sound's J and L cuts and slip, and half its crossfade before its start (half the next
    // clip's beyond its end is at most half the longest crossfade, 5 s: the handles).
    private static Stretch Reads(TimelineClip clip)
    {
        var speed = Math.Max(clip.Speed, 1e-6);
        var before = Math.Max(clip.AudioLeadInSeconds, 0) + Math.Max(clip.AudioSlipSeconds, 0) + clip.TransitionIn / 2;
        var after = Math.Max(clip.AudioTailOutSeconds, 0) + Math.Max(-clip.AudioSlipSeconds, 0);
        return new Stretch(clip.MediaOffset - before * speed, clip.MediaEnd + after * speed);
    }

    private static string? FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The video files of a project to trim (see the top of this file), with the stretches to keep: longer than <see cref="LongSeconds"/>
    /// (as long as its clips say; <paramref name="durationOf"/> for one they do not), less than <see cref="MostUsedShare"/> of it used.
    /// </summary>
    public static IReadOnlyList<Planned> Plan(ProjectState state, Func<string, double>? durationOf = null)
    {
        var planned = new List<Planned>();
        foreach (var file in ClipsOf(state).GroupBy(c => FullPath(c.SourcePath!) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (file.Key.Length == 0 || !file.Any(c => c.ContentKind is LayerKind.VideoFile or LayerKind.MainVideo))
                continue;

            var duration = file.Max(c => c.SourceDuration);
            if (!(duration > 0))
                duration = durationOf?.Invoke(file.Key) ?? 0;
            if (!(duration > LongSeconds))
                continue;

            // The stretches the clips read, joined where they overlap: how much of the file is used.
            var used = Join(file.Select(Reads).Select(s => new Stretch(Math.Max(s.From, 0), Math.Min(s.To, duration))).Where(s => s.To > s.From), 0);
            var share = used.Sum(s => s.To - s.From) / duration;
            if (used.Count == 0 || share >= MostUsedShare)
                continue;

            // Kept with their handles, joined where they come within JoinGapSeconds of each other.
            var kept = Join(used.Select(s => new Stretch(Math.Max(s.From - HandleSeconds, 0), Math.Min(s.To + HandleSeconds, duration))), JoinGapSeconds);
            planned.Add(new Planned(file.Key, duration, share, kept));
        }

        return planned;
    }

    private static List<Stretch> Join(IEnumerable<Stretch> stretches, double gap)
    {
        var joined = new List<Stretch>();
        foreach (var stretch in stretches.OrderBy(s => s.From))
        {
            if (joined.Count > 0 && stretch.From <= joined[^1].To + gap)
                joined[^1] = joined[^1] with { To = Math.Max(joined[^1].To, stretch.To) };
            else
                joined.Add(stretch);
        }

        return joined;
    }

    /// <summary>
    /// A copy of the state whose clips of each trimmed file name the piece that holds what they read (see the top of this file), and the files
    /// so trimmed with their pieces. A file one of whose clips no piece holds whole is not trimmed at all (it goes in whole, every clip of it
    /// as it was). The recycle bin's clips of a trimmed file are left out (nothing of them may be in a piece), and the project's own names of
    /// such a file (the file opened, the captions' sound) name its first piece. The state given is not changed.
    /// </summary>
    public static (ProjectState State, Dictionary<string, IReadOnlyList<Piece>> Trimmed) Remap(ProjectState state, IReadOnlyDictionary<string, IReadOnlyList<Piece>> given)
    {
        static Piece? Holding(IReadOnlyList<Piece> cut, TimelineClip clip)
        {
            var reads = Reads(clip);
            return cut.FirstOrDefault(p => p.From <= Math.Max(reads.From, 0) + 1e-6 && p.From + p.Length >= reads.To - 1e-3);
        }

        // Only the files every clip of which a piece holds whole.
        var pieces = new Dictionary<string, IReadOnlyList<Piece>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, cut) in given)
        {
            if (cut.Count > 0 && ClipsOf(state).Where(c => string.Equals(FullPath(c.SourcePath!), path, StringComparison.OrdinalIgnoreCase)).All(c => Holding(cut, c) is not null))
                pieces[path] = cut;
        }

        var copy = state.ShallowCopy();
        copy.Timeline = state.Timeline.Clone(deep: true);
        IReadOnlyList<Piece>? PiecesOf(string? path) => path is { Length: > 0 } && FullPath(path) is { } full && pieces.TryGetValue(full, out var found) ? found : null;

        TimelineClip Moved(TimelineClip clip)
        {
            if (!clip.PlaysFile || PiecesOf(clip.SourcePath) is not { } cut || Holding(cut, clip) is not { } piece)
                return clip;

            var moved = clip.Snapshot();
            (moved.SourcePath, moved.MediaOffset, moved.SourceDuration) = (piece.Path, clip.MediaOffset - piece.From, piece.Length);
            var look = TimelineTransaction.CopyOf(clip.State);
            (look.ImagePath, look.MediaOffset, look.MediaDuration) = (piece.Path, Math.Max(look.MediaOffset - piece.From, 0), piece.Length);
            moved.State = look;
            if (clip.BeatTimes is { } beats)
                moved.BeatTimes = [.. beats.Select(b => b - piece.From).Where(b => b >= 0 && b <= piece.Length)];
            return moved;
        }

        foreach (var track in copy.Timeline.Tracks)
        {
            for (var i = 0; i < track.Clips.Count; i++)
                track.Clips[i] = Moved(track.Clips[i]);
        }

        if (copy.Timeline.Folds is { } folds)
            copy.Timeline.Folds = [.. folds.Select(f => f with { Clips = [.. f.Clips.Select(c => c with { Clip = Moved(c.Clip) })] })];

        copy.Deleted = [.. state.Deleted.Where(d => PiecesOf(d.Clip.SourcePath) is null)];
        if (PiecesOf(copy.SourcePath) is { } source)
            copy.SourcePath = source[0].Path;
        if (PiecesOf(copy.LocalMediaPath) is { } local)
            copy.LocalMediaPath = local[0].Path;
        if (PiecesOf(copy.CaptionAudioPath) is { } captions)
            copy.CaptionAudioPath = captions[0].Path;
        return (copy, pieces);
    }

    /// <summary>
    /// Cuts every planned file's stretches into <paramref name="folder"/> (see the top of this file). A file any stretch of which cannot be cut
    /// exactly is not trimmed at all. Returns the pieces of each file trimmed (by its full path).
    /// </summary>
    public static Dictionary<string, IReadOnlyList<Piece>> Cut(IReadOnlyList<Planned> plan, string folder, CancellationToken cancel)
    {
        var made = new Dictionary<string, IReadOnlyList<Piece>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(DependencyUpdater.FfmpegPath) || !File.Exists(DependencyUpdater.FfprobePath))
            return made;

        Directory.CreateDirectory(folder);
        foreach (var file in plan)
        {
            var pieces = new List<Piece>();
            foreach (var stretch in file.Stretches)
            {
                cancel.ThrowIfCancellationRequested();
                var name = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(file.Path)} [{stretch.From:0}-{stretch.To:0} s]{Path.GetExtension(file.Path)}");
                if (CutOne(file.Path, stretch, Path.Combine(folder, name), cancel) is not { } piece)
                {
                    AppLog.Write($"Project bundle: {Path.GetFileName(file.Path)} could not be trimmed exactly; it goes in whole");
                    pieces.Clear();
                    break;
                }

                pieces.Add(piece);
            }

            if (pieces.Count > 0)
                made[file.Path] = pieces;
        }

        return made;
    }

    /// <summary>
    /// One stretch as a file of its own: a stream copy from the key frame at or before its start (the file's own clock) to its end, checked to
    /// begin on that key frame. Null when it cannot be cut so.
    /// </summary>
    internal static Piece? CutOne(string source, Stretch stretch, string target, CancellationToken cancel)
    {
        // Where the picture starts on the file's clock (media second 0), and the key frames up to the stretch's start.
        if (Probe(source, "-select_streams v:0 -show_entries stream=start_time", cancel) is not { } facts
            || !double.TryParse(Field(facts, "start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var videoStart))
            return null;

        var at = videoStart + stretch.From;
        (double Time, long Size)? key = null;
        double? nextKey = null;
        if (stretch.From > 0.001)
        {
            foreach (var window in new[] { 20.0, 120.0 })
            {
                var listed = Probe(source, string.Create(CultureInfo.InvariantCulture,
                    $"-select_streams v:0 -read_intervals {Math.Max(at - window, 0):0.######}%{at + 1:0.######} -show_entries packet=pts_time,size,flags"), cancel);
                foreach (var line in (listed ?? "").Split('\n'))
                {
                    if (!(Field(line, "flags") ?? "").Contains('K') || !double.TryParse(Field(line, "pts_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                        || !long.TryParse(Field(line, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                        continue;
                    if (time <= at + 1e-6 && (key is null || time > key.Value.Time))
                        key = (time, size);
                    else if (time > at + 1e-6 && (nextKey is null || time < nextKey))
                        nextKey = time;
                }

                if (key is not null)
                    break;
            }

            if (key is null)
                return null;
        }

        // From the key frame, to the stretch's end, every stream as it is. FFmpeg's seek steps back before the point asked for (for B-frames,
        // in a container that does not seek by presentation time), and by how much changes between its builds (5.2.0's re-cut: 3/23 s, then
        // more than 0.13 s on Matroska and a hair on MP4 from its 2026-10-31 build on). So the seek goes a hair past the key frame, then further
        // towards the next one, until the copy is seen to begin on it; never past the next key frame, so it lands on no later one.
        var from = key?.Time - videoStart ?? 0;
        var length = stretch.To - from;
        var gap = key is { } found ? (nextKey ?? found.Time + 1) - found.Time : 0;
        double[] margins = key is null ? [0] : [.. new[] { Math.Min(0.131, gap / 2), gap / 2, gap * 0.9 }.Distinct()];
        var partial = target + ".part" + Path.GetExtension(target);
        try
        {
            var landed = false;
            foreach (var margin in margins)
            {
                var seek = key is { } k ? string.Create(CultureInfo.InvariantCulture, $"-seek_timestamp 1 -ss {k.Time + margin:0.######} ") : "";
                var ran = Run(DependencyUpdater.FfmpegPath, string.Create(CultureInfo.InvariantCulture,
                    $"-hide_banner -loglevel error -y {seek}-t {length:0.######} -i \"{source}\" -map 0 -dn -c copy -avoid_negative_ts make_zero -map_metadata 0 \"{partial}\""), cancel);
                if (ran is null || !File.Exists(partial))
                    return null;

                // Its first picture is that key frame: the same packet (its size), a key frame.
                if (key is not { } wanted)
                {
                    landed = true;
                    break;
                }

                var first = Probe(partial, "-select_streams v:0 -read_intervals %+2 -show_entries packet=size,flags", cancel);
                if (first is not null && (Field(first, "flags") ?? "").Contains('K') && Field(first, "size") == wanted.Size.ToString(CultureInfo.InvariantCulture))
                {
                    landed = true;
                    break;
                }
            }

            if (!landed)
                return null;

            var lasts = Probe(partial, "-show_entries format=duration", cancel);
            if (double.TryParse(Field(lasts ?? "", "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) && duration > 0)
                length = Math.Min(length, duration);
            File.Move(partial, target, overwrite: true);
            return new Piece(target, from, length);
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // A "key=value" field of ffprobe's compact output (the first of that key).
    private static string? Field(string text, string key)
    {
        foreach (var part in text.Split('|', '\n', '\r'))
        {
            if (part.StartsWith(key + "=", StringComparison.Ordinal))
                return part[(key.Length + 1)..].Trim();
        }

        return null;
    }

    private static string? Probe(string path, string what, CancellationToken cancel) =>
        Run(DependencyUpdater.FfprobePath, $"-v error {what} -of compact=p=0 \"{path}\"", cancel);

    // A tool run to its end (its output), or null when it failed; killed when cancelled.
    private static string? Run(string tool, string arguments, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(tool, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            var errors = process.StandardError.ReadToEndAsync(cancel);
            using (cancel.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }))
            {
                process.WaitForExit();
            }

            cancel.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                AppLog.Write($"Project bundle: {Path.GetFileName(tool)} failed ({process.ExitCode}): {errors.Result.Trim()}");
                return null;
            }

            return output.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            AppLog.Write($"Project bundle: {Path.GetFileName(tool)} could not run", ex);
            return null;
        }
    }
}
