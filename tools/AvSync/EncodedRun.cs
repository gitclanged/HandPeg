using System.Globalization;
using System.IO;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// One graph played by libmpv from one moment, written to a file instead of the screen and the speakers (mpv's encoding
/// mode), for a few seconds.
///
/// While it encodes, mpv takes neither a new graph nor a seek (its encoder may not be set up twice), so the graph and
/// the moment are given before playback starts: the graph as lavfi-complex, the moment as start=. That is what the app
/// gets from "seek, then the graph" (MpvPlayer.SendPendingLocked): on a new lavfi-complex mpv builds the filters
/// afresh and runs them from the seek's target. The steps the app takes within a session (a graph without sound, then
/// the one with; seeks while playing; the rebuild while playing) are checked in real time by "session".
///
/// The output keeps mpv's own time stamps (orawts): the picture's and the sound's are the sequence's moments, as the
/// player has them when it lines the two up.
/// </summary>
internal sealed record EncodedRun(string Output, long RestartMs, long AudioReadyMs, string? Error, IReadOnlyList<string> Log)
{
    public static EncodedRun Play(string libmpv, string source, string graph, double at, double seconds, string output)
    {
        if (File.Exists(output))
            File.Delete(output);

        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var options = new List<(string, string)>
        {
            // As the app's player has them (Services/MpvPlayer.cs), where they bear on what is played and when.
            ("config", "no"), ("terminal", "no"), ("idle", "yes"), ("keep-open", "no"),
            ("hr-seek", "yes"), ("hr-seek-framedrop", "yes"), ("vd-lavc-fast", "yes"), ("audio-display", "no"), ("sid", "no"),
            ("profile", "fast"), ("vd-lavc-threads", "0"), ("hwdec", "no"), ("framedrop", Environment.GetEnvironmentVariable("AVSYNC_FRAMEDROP") ?? "no"),

            // Written to a file, with the player's time stamps.
            ("o", output), ("of", "matroska"), ("ovc", Environment.GetEnvironmentVariable("AVSYNC_OVC") ?? "ffv1"), ("oac", "pcm_s16le"), ("orawts", "yes"), ("ovcopts", Environment.GetEnvironmentVariable("AVSYNC_OVCOPTS") ?? ""),
            ("start", N(at)), ("end", N(at + seconds)),
        };
        if (graph.Length > 0)
            options.Add(("lavfi-complex", graph));

        using var mpv = MpvSession.Start(libmpv, options);
        var asked = mpv.Now;
        mpv.Command("loadfile", source, "replace");
        var ended = mpv.WaitEnded(TimeSpan.FromSeconds(600));
        var restart = mpv.RestartLog.Count > 0 ? mpv.RestartLog[0].Ms - asked : -1;
        mpv.Dispose();

        var log = mpv.Log.ToList();
        var audioReady = log.FirstOrDefault(l => l.Contains("cplayer: audio ready", StringComparison.Ordinal)) is { } line
            ? long.Parse(line[1..line.IndexOf(" ms]", StringComparison.Ordinal)].Trim(), CultureInfo.InvariantCulture) - asked
            : -1;
        string? error = !ended ? "mpv did not finish in ten minutes"
            : mpv.Refusals.Count > 0 ? string.Join("; ", mpv.Refusals)
            : log.FirstOrDefault(l => l.Contains("] fatal ", StringComparison.Ordinal) || l.Contains("] error ", StringComparison.Ordinal)) is { } bad ? bad
            : !File.Exists(output) ? "mpv wrote no output" : null;
        File.WriteAllLines(Path.ChangeExtension(output, ".log"), log);
        return new EncodedRun(output, restart, audioReady, error, log);
    }
}
