using System.Diagnostics;
using System.Globalization;
using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- Trimming long recordings in bundles (AppSettings.TrimLongVodsOnBundle) -----
//
// Avoids bundling multi-GB recordings for a few used minutes. With the setting on (default), videos longer than LongSeconds with under
// MostUsedShare used are bundled as used ranges plus HandleSeconds at each end (trim, J/L cut, crossfade room), merging ranges closer
// than JoinGapSeconds. Each is a lossless stream copy (FFmpeg -c copy, all streams) starting at the key frame at or before the range;
// the first packet is verified (size and key flag), else (or if FFmpeg is missing or fails) the whole file is bundled. The bundled
// project remaps each clip (including Cutouts, stream lanes and Split & Mark folds) to its piece with MediaOffset shifted, so every
// frame and sample stays aligned. The working project is never modified.
public static class VodTrim
{
    public const double LongSeconds = 120, MostUsedShare = 0.6, HandleSeconds = 5, JoinGapSeconds = 30;

    /// <summary>A stretch of a file to keep, in its media seconds (its first frame 0), handles included.</summary>
    public readonly record struct Stretch(double From, double To);

    /// <summary>A file to trim: its length and the stretches its clips use.</summary>
    public sealed record Planned(string Path, double Duration, double UsedShare, IReadOnlyList<Stretch> Stretches);

    /// <summary>A cut piece: first-frame position in source media seconds and duration.</summary>
    public sealed record Piece(string Path, double From, double Length);

    // All file-playing clips, on tracks and in folds (Split & Mark folds can be restored).
    private static IEnumerable<TimelineClip> ClipsOf(ProjectState state) =>
        state.Timeline.Tracks.SelectMany(t => t.Clips).Concat((state.Timeline.Folds ?? []).SelectMany(f => f.Clips).Select(f => f.Clip))
            .Where(c => c is { PlaysFile: true, SourcePath.Length: > 0 });

    // Media range a clip reads: picture, audio J/L cuts and slip, and half its leading crossfade (trailing overlap is covered by the 5 s handles).
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
    /// Plans trimming: videos longer than <see cref="LongSeconds"/> (clip-reported, else <paramref name="durationOf"/>) with under
    /// <see cref="MostUsedShare"/> used, plus the ranges to keep.
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
    /// Returns a remapped state copy (clips point at the piece containing their range) and the trimmed files with pieces. A file with any
    /// clip not fully inside one piece is not trimmed. Recycle-bin clips of trimmed files are dropped; other references (opened file,
    /// caption audio) map to the first piece. The input state is unchanged.
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
            // A piece's video-vs-container duration is unknown (PictureDuration 0).
            (moved.SourcePath, moved.MediaOffset, moved.SourceDuration, moved.PictureDuration) = (piece.Path, clip.MediaOffset - piece.From, piece.Length, 0.0);
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
    /// Cuts all planned ranges into <paramref name="folder"/>; a file with any inexact cut is not trimmed. Returns pieces per full path.
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
    /// Stream-copies one range from the key frame at or before its start (file clock) to its end, verifying it begins on that key frame;
    /// null on failure.
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

        // Copy all streams from the key frame to the range end. FFmpeg's seek backs off before the target (B-frames in non-PTS-seeking
        // containers) by a build-dependent amount (from ~3/23 s to over 0.13 s on Matroska), so seek slightly past the key frame and
        // advance toward the next until the copy starts on it, never past the next key frame.
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
            using var adopted = ProcessPipes.Adopt(process.Id);
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
                // GetResult (not Result) surfaces the IOException unwrapped for the filter below.
                AppLog.Write($"Project bundle: {Path.GetFileName(tool)} failed ({process.ExitCode}): {errors.GetAwaiter().GetResult().Trim()}");
                return null;
            }

            return output.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            AppLog.Write($"Project bundle: {Path.GetFileName(tool)} could not run", ex);
            return null;
        }
    }
}
