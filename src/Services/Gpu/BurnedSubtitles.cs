using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CliWrap;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services.Gpu;

// ----- A file's subtitles burned into an export, by the compositor (Pass 5.2-Audit) -----
// What FFmpeg's libass (the subtitles filter) used to draw on the video's frames is the GPU's now, through the very path the
// preview draws a picture subtitle track by (PictureSubtitleTrack, GpuScenePlan.Pictures.cs: each cue's picture a still over the
// picture of the clip it is shown in). A picture stream (PGS, VobSub, DVB) is decoded as the preview decodes one
// (PictureSubtitleDecoder); a text stream (SubRip, ASS, mov_text, WebVTT) is read out as SubRip by FFmpeg (a subtitle
// transcode, no filter) and each cue drawn by WPF as libass draws SubRip by default: Arial, white, a thin black outline, bottom
// centre, on the video's own frame. The stream is the file the Subtitles settings name's (SequenceExportState.StreamSourcePath,
// the file last opened); each cue is placed on the sequence by that file's clips (SequenceSpans.SpansOf, 5.2-AuditB: on any
// track, at each one's speed; a cue across a cut is cut with it), and drawn through the clip each piece of it is placed by.

public static class BurnedSubtitles
{
    /// <summary>
    /// The burned subtitle streams of an export's state as the track its plan draws (null for none, or none readable: the log
    /// says why). Off the interface's thread: it decodes and draws (an STA thread of its own for WPF).
    /// </summary>
    public static async Task<PictureSubtitleTrack?> PrepareAsync(SequenceExportState state, CancellationToken cancellationToken)
    {
        var path = state.StreamSourcePath.Trim().Trim('"');
        if (state.BurnedSubtitleStreams.Count == 0 || path.Length == 0 || !File.Exists(path))
            return null;
        var spans = SequenceSpans.SpansOf(state.Timeline, path);
        if (spans.Count == 0)
            return null;

        var ffmpeg = DependencyUpdater.FfmpegPath;
        var info = MediaProbe.KnownInfo(path);
        var stream = state.BurnedSubtitleStreams[0];
        if (state.BurnedSubtitleStreams.Count > 1)
            AppLog.Write("Burned subtitles: only the first subtitle stream asked for is burned in.");

        var isText = info?.Subtitles is { } subtitles && stream < subtitles.Count ? subtitles[stream].IsText : true;
        var (width, height) = info?.Video is { Width: > 0, Height: > 0 } video ? (video.Width, video.Height) : PictureSizeOf(state.Timeline, spans);
        PictureSubtitleSet? set;
        try
        {
            set = isText
                ? await DrawTextAsync(path, stream, ffmpeg, width, height, cancellationToken)
                : await PictureSubtitleDecoder.DecodeAsync(path, stream, ffmpeg, null, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Burned subtitles: the stream could not be read", ex);
            return null;
        }

        if (set is null || set.Pictures.Count == 0)
            return null;

        // Each picture where the file's clips put its moment (a cue across a cut is cut with it), at each clip's speed. The
        // stream's times are on the file's container clock, the clips' moments on its picture's (the worker's, MediaStarts).
        var shift = ContainerShiftOf(state, path);
        var (file, shown, clips) = (new List<(double, double)>(), new List<(double, double)>(), new List<Guid>());
        foreach (var picture in set.Pictures)
        {
            var end = double.IsFinite(picture.End) ? picture.End : picture.Start + 5;
            foreach (var span in spans)
            {
                var speed = double.IsFinite(span.Speed) ? Math.Clamp(span.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
                var spanEnd = span.MediaOffset + (span.End - span.Start) * speed;
                var (from, to) = (Math.Max(picture.Start - shift, span.MediaOffset), Math.Min(end - shift, spanEnd));
                if (to - from < 0.001)
                    continue;
                file.Add((picture.Start, end));
                shown.Add((span.Start + (from - span.MediaOffset) / speed, span.Start + (to - span.MediaOffset) / speed));
                clips.Add(span.ClipId);
            }
        }

        var track = PictureSubtitleTrack.Match(set, file, shown, clips);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Burned subtitles: {set.Pictures.Count} {(isText ? "lines drawn" : "pictures decoded")} of stream {stream}, {track?.Cues.Count ?? 0} cues on the sequence."));
        return track;
    }

    /// <summary>The picture size of the first clip of a file that knows it (the frame a text stream is drawn for); 1920 x 1080 when none does.</summary>
    private static (int Width, int Height) PictureSizeOf(TimelineDocument document, IReadOnlyList<ClipSpan> spans)
    {
        foreach (var span in spans)
        {
            if (document.FindClip(span.ClipId)?.State is { ImageWidth: > 0, ImageHeight: > 0 } look)
                return (look.ImageWidth, look.ImageHeight);
        }

        return (1920, 1080);
    }

    /// <summary>
    /// Where a file's picture starts on its container's clock (VideoStart - ContainerStart): how much later its subtitle
    /// stream's times are than its clips' moments. 0 while either is not known.
    /// </summary>
    private static double ContainerShiftOf(SequenceExportState state, string path)
    {
        double? video = state.MediaStarts.TryGetValue(path, out var known) ? known : MediaProbe.KnownVideoStart(path);
        double? container = state.SeekIndexes.TryGetValue(path, out var index) ? index.ContainerStart : MediaProbe.KnownContainerStart(path);
        return video is { } start && container is { } clock && double.IsFinite(start - clock) ? start - clock : 0;
    }

    // ----- Text subtitles -----

    /// <summary>A text subtitle stream's cues, each drawn as libass draws SubRip by default, on the video's frame.</summary>
    private static async Task<PictureSubtitleSet?> DrawTextAsync(string path, int stream, string ffmpeg, int width, int height, CancellationToken token)
    {
        if (!File.Exists(ffmpeg))
            return null;

        var srt = new StringBuilder();
        var command = Cli.Wrap(ffmpeg)
            .WithArguments(["-nostdin", "-hide_banner", "-loglevel", "error", "-i", path, "-map", string.Create(CultureInfo.InvariantCulture, $"0:s:{stream}"), "-c:s", "srt", "-f", "srt", "pipe:1"])
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(srt, Encoding.UTF8))
            .WithValidation(CommandResultValidation.None);
        await ProcessPipes.RunAsync(command, token);
        var cues = SubtitleFiles.ParseText(srt.ToString());
        if (cues.Count == 0)
            return null;

        var pictures = await OnStaThread(() => cues.Select((cue, n) => Draw(cue, n, path, stream, width, height)).OfType<PictureSubtitlePicture>().ToList(), token);
        return new PictureSubtitleSet(path, width, height, pictures, isComplete: true, overBudget: false);
    }

    // libass's style for SubRip (FFmpeg's default ASS header): 16 / 288 of the height, outline 1 / 288, bottom centre, 10 / 288 up.
    private static PictureSubtitlePicture? Draw(SubtitleCue cue, int n, string path, int stream, int width, int height)
    {
        var text = cue.Text.Trim();
        if (text.Length == 0 || !(cue.End > cue.Start))
            return null;

        var unit = height / 288.0;
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 16 * unit * 1.25, Brushes.White, 1)
        {
            MaxTextWidth = Math.Max(width - 20 * unit, 1),
            TextAlignment = TextAlignment.Center,
        };
        var outline = Math.Max(unit, 1);
        var (boxWidth, boxHeight) = ((int)Math.Ceiling(formatted.Width + 2 * outline + 2), (int)Math.Ceiling(formatted.Height + 2 * outline + 2));
        var origin = new Point((boxWidth - formatted.MaxTextWidth) / 2, outline + 1);
        var geometry = formatted.BuildGeometry(origin);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawGeometry(null, new Pen(Brushes.Black, 2 * outline) { LineJoin = PenLineJoin.Round }, geometry);
            context.DrawGeometry(Brushes.White, null, geometry);
        }

        var bitmap = new RenderTargetBitmap(boxWidth, boxHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[boxWidth * boxHeight * 4];
        bitmap.CopyPixels(pixels, boxWidth * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a is > 0 and < 255)
            {
                for (var c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Min(pixels[i + c] * 255 / a, 255);
            }
        }

        var (x, y) = ((width - boxWidth) / 2, height - (int)Math.Round(10 * unit) - boxHeight);
        var hash = default(GpuHash).Add("burned-text").Add(path).Add(stream).Add(n).Add(text).Value;
        return PictureSubtitlePicture.Create(cue.Start, cue.End, x, Math.Max(y, 0), boxWidth, boxHeight, pixels, hash);
    }

    private static Task<T> OnStaThread<T>(Func<T> work, CancellationToken token)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { Name = "Burned subtitles", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
