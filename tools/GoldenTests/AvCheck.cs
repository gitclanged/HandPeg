using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The A/V invariant (5.0-Verify): for every video clip with sound, a moment of its file is seen and heard at the same moment
/// of the sequence, and that moment is where the timeline puts it (the clip's start plus how far into the clip the moment is, at
/// the clip's speed). Since 5.2-Audit the export has no graph to follow: the GPU scene and the sound engine's program are the
/// export's picture and sound, and <see cref="GpuInvariant"/> checks them against each other at the same moments.
/// </summary>
public static partial class AvCheck
{
    // Times are written to the millisecond; a few of them add up.
    private const double Tolerance = 0.002;

    /// <summary>The offsets into a clip, in seconds of its file, of the moments followed.</summary>
    private static readonly double[] Offsets = [0.02, 0.5, 1.5, 7];

    /// <summary>
    /// Every video clip on the timeline whose own sound is heard, the first of each file (the graph's first source of the file is
    /// the one followed). (A clip whose sound is on its Sub-Lanes is silent itself: its moments are followed as a clip of
    /// <see cref="Unpacked"/>, as the main video's were.)
    /// </summary>
    private static IEnumerable<HandPegApp.Models.Timeline.TimelineClip> HeardLayers(SequenceExportState state) => state.Timeline.Tracks
        .Where(t => t.Kind is not (HandPegApp.Models.Timeline.TimelineTrackKind.Audio or HandPegApp.Models.Timeline.TimelineTrackKind.Subtitle) && !t.IsMuted)
        .SelectMany(t => t.Clips)
        .Where(c => c.ContentKind == LayerKind.VideoFile && c.HasSound && !c.IsHidden && !c.IsMuted && c.SourcePath is not null)
        .DistinctBy(c => c.SourcePath!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The clips whose sound is heard from their Sub-Lanes (a file's streams unpacked under its clip: 5.2-AuditB, the main video's
    /// once a project from before is opened): the ids of the clips that sound clips are children of.
    /// </summary>
    private static HashSet<Guid> Unpacked(SequenceExportState state) => state.Timeline.Tracks
        .SelectMany(t => t.Clips)
        .Where(c => c.ContentKind == LayerKind.Audio && c.ParentClipId is not null)
        .Select(c => c.ParentClipId!.Value)
        .ToHashSet();

    /// <summary>
    /// The moments of a clip's file that are followed (each on a frame of the file, from <paramref name="playhead"/> or the
    /// clip's start on), with the moment of the sequence the timeline shows each at.
    /// </summary>
    private static IEnumerable<(double File, double Sequence)> FileMoments(HandPegApp.Models.Timeline.TimelineClip layer, Media media, double playhead)
    {
        var speed = Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed);
        var into = Math.Max(playhead - layer.Start, 0);
        var lowest = media.VideoStart + layer.MediaOffset + into * speed;
        var highest = media.VideoStart + layer.MediaOffset + layer.Duration * speed;
        foreach (var offset in Offsets)
        {
            var moment = OnFrame(media, lowest + offset * speed);
            if (moment < highest - 0.05)
                yield return (moment, layer.Start + (moment - media.VideoStart - layer.MediaOffset) / speed);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().Trim('"').Replace('\\', '/'), b.Trim().Trim('"').Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The frame of the file at or after a moment.</summary>
    private static double OnFrame(Media media, double moment) =>
        media.VideoStart + Math.Ceiling((moment - media.VideoStart) * media.Fps - 1e-6) / media.Fps;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
