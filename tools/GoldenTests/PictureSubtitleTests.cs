using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegGoldenTests;

/// <summary>
/// Picture subtitles in the GPU preview (5.0-Verify, gpu-picture-subtitles). A PGS file written here (three pictures on a
/// 640 x 360 canvas: white, black and half-transparent palette colours) is read for its cues (SubtitleFiles), decoded by
/// HandPeg's FFmpeg (PictureSubtitleDecoder: sub2video) and checked pixel for pixel against what was written, paired with
/// its cues (one retimed, the offset added, two of them then over one another), and laid by GpuScenePlan with no worker:
/// each cue's still on from its start to its end and off outside it, where the main video's picture is (a plain frame, and
/// a vertical one with the main video cropped and centred), handed over only once the playhead is near it, and evaluated
/// allocation-free. Without FFmpeg the pictures are made here as the decode must make them, so the golden text is the same
/// either way (the decode's checks are then skipped, with a note).
/// </summary>
public static class PictureSubtitleTests
{
    private const int CanvasWidth = 640, CanvasHeight = 360;

    // Palette 1 white, 2 black, 3 white at half opacity (Y, Cr, Cb, alpha; BT.601 for a canvas this small).
    private static readonly byte[][] Colours = [[0, 0, 0, 0], [255, 255, 255, 255], [0, 0, 0, 255], [255, 255, 255, 128]];

    /// <summary>A picture of the file: when it is shown, where on the canvas, and its fill (a 2-pixel black border round it).</summary>
    private sealed record Shot(double From, double To, int X, int Y, int Width, int Height, byte Fill);

    private static readonly Shot[] Shots =
    [
        new(0.5, 1.5, 100, 280, 400, 40, 1),
        new(2.0, 3.0, 60, 20, 200, 30, 3),
        new(3.2, 3.8, 220, 300, 200, 40, 1),
    ];

    public static GraphResult Run()
    {
        var text = new StringBuilder("# Picture subtitles in the GPU preview: a PGS file made here, its cues, its pictures as HandPeg's FFmpeg decodes them, and GpuScenePlan's stills at sampled moments.\n");
        var problems = new List<string>();
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests", "picture-subtitles-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(folder);
            var sup = Path.Combine(folder, "pictures.sup");
            File.WriteAllBytes(sup, Sup(Shots));

            // The cues, as the import reads them.
            var cues = SubtitleFiles.Read(sup);
            text.Append(CultureInfo.InvariantCulture, $"\nfile: {cues.Count} cue(s): {string.Join(", ", cues.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Start:0.###}-{c.End:0.###}")))}\n");
            if (cues.Count != Shots.Length)
                problems.Add($"SubtitleFiles read {cues.Count} cues, not {Shots.Length}");

            var set = Decoded(sup, problems) ?? Made(sup);
            text.Append(CultureInfo.InvariantCulture, $"pictures: {set.Pictures.Count} on a {set.CanvasWidth} x {set.CanvasHeight} canvas\n");
            for (var i = 0; i < set.Pictures.Count; i++)
            {
                var p = set.Pictures[i];
                text.Append(CultureInfo.InvariantCulture, $"  picture {i + 1}: {p.Start:0.###}-{p.End:0.###} s at ({p.X}, {p.Y}) {p.Width} x {p.Height}\n");
            }

            // Paired with the cues: the second retimed a second later, all of them a quarter of a second (the offset).
            const double Offset = 0.25;
            var file = cues.Select(c => (c.Start, c.End)).ToList();
            var shown = cues.Select((c, i) => (c.Start + Offset + (i == 1 ? 1 : 0), c.End + Offset + (i == 1 ? 1 : 0))).ToList();
            var track = PictureSubtitleTrack.Match(set, file, shown);
            if (track is null)
            {
                problems.Add("no cue was paired with a picture");
                return new GraphResult("gpu-picture-subtitles", text.ToString(), problems, 0);
            }

            text.Append(CultureInfo.InvariantCulture, $"track (the second cue retimed by +1 s, every cue by the offset, +{Offset} s): {track.CanvasWidth} x {track.CanvasHeight}\n");
            foreach (var cue in track.Cues)
                text.Append(CultureInfo.InvariantCulture, $"  {cue.From:0.###}-{cue.To:0.###} s: picture {IndexOf(set, cue.Picture) + 1}\n");

            double[] moments = [0.5, 1.0, 2.5, 3.3, 3.6, 4.1, 4.5];
            Plan(text, problems, "plain 1280 x 720 frame, a 1280 x 720 source filling it", track,
                State(1280, 720, 1280, 720, (0, 0, 1280, 720), crop: null), moments);
            Plan(text, problems, "vertical 720 x 1280 frame, a 1920 x 1080 source cropped by a tenth at each side, across the middle", track,
                State(720, 1280, 1920, 1080, (0, 387, 720, 506), crop: (0.1, 0, 0.1, 0)), moments);

            // A cue is not paired with a picture the file shows elsewhere: a cue at a moment with none is left out.
            var stray = PictureSubtitleTrack.Match(set, [(10.0, 11.0)], [(10.0, 11.0)]);
            text.Append(CultureInfo.InvariantCulture, $"a cue at 10 s, where the file shows nothing: {(stray is null ? "no track" : $"{stray.Cues.Count} cue(s)")}\n");
            if (stray is not null)
                problems.Add("a cue where the file shows no picture was paired with one");
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new GraphResult("gpu-picture-subtitles", text.ToString(), problems, 0);
    }

    private static int IndexOf(PictureSubtitleSet set, PictureSubtitlePicture picture)
    {
        for (var i = 0; i < set.Pictures.Count; i++)
        {
            if (set.Pictures[i].Hash == picture.Hash)
                return i;
        }

        return -1;
    }

    // ----- The decode -----

    /// <summary>The file decoded by HandPeg's FFmpeg, checked against the shots (problems only, never the golden text); null without FFmpeg.</summary>
    private static PictureSubtitleSet? Decoded(string sup, List<string> problems)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): gpu-picture-subtitles uses pictures made here, not decoded");
            return null;
        }

        var clock = Stopwatch.StartNew();
        var set = Task.Run(() => PictureSubtitleDecoder.DecodeAsync(sup, 0, ffmpeg, null, CancellationToken.None)).GetAwaiter().GetResult();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        decoded {set?.Pictures.Count ?? 0} picture(s) in {clock.ElapsedMilliseconds} ms, {set?.PackedBytes ?? 0} bytes packed"));
        if (set is null)
        {
            problems.Add("FFmpeg's decode gave nothing (see the log)");
            return null;
        }

        if (!set.IsComplete || set.OverBudget || set.CanvasWidth != CanvasWidth || set.CanvasHeight != CanvasHeight)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the decode: canvas {set.CanvasWidth} x {set.CanvasHeight}, complete {set.IsComplete}, over budget {set.OverBudget}"));
        if (set.Pictures.Count != Shots.Length)
        {
            problems.Add($"the decode gave {set.Pictures.Count} pictures, not {Shots.Length}");
            return set;
        }

        for (var i = 0; i < Shots.Length; i++)
        {
            var (picture, shot) = (set.Pictures[i], Shots[i]);
            if (Math.Abs(picture.Start - shot.From) > 1e-3 || Math.Abs(picture.End - shot.To) > 1e-3
                || (picture.X, picture.Y, picture.Width, picture.Height) != (shot.X, shot.Y, shot.Width, shot.Height))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"picture {i + 1}: {picture.Start}-{picture.End} s at ({picture.X}, {picture.Y}) {picture.Width} x {picture.Height}, written {shot.From}-{shot.To} s at ({shot.X}, {shot.Y}) {shot.Width} x {shot.Height}"));
                continue;
            }

            var (pixels, wanted) = (picture.Pixels(), Pixels(shot));
            var wrong = 0;
            for (var k = 0; k < wanted.Length; k++)
                wrong += pixels[k] != wanted[k] ? 1 : 0;
            if (wrong > 0)
                problems.Add($"picture {i + 1}: {wrong} of {wanted.Length} bytes differ from the palette colours written");
        }

        return set;
    }

    /// <summary>The pictures as the decode must give them, made here (no FFmpeg).</summary>
    private static PictureSubtitleSet Made(string sup) =>
        new(sup, CanvasWidth, CanvasHeight,
            [.. Shots.Select((s, i) => PictureSubtitlePicture.Create(s.From, s.To, s.X, s.Y, s.Width, s.Height, Pixels(s), i + 1))], isComplete: true, overBudget: false);

    // ----- The plan -----

    private static void Plan(StringBuilder text, List<string> problems, string name, PictureSubtitleTrack track, SequenceExportState state, double[] moments)
    {
        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        var plan = GpuScenePlan.Create(state with { PictureSubtitles = track, IsPreview = true }, broker);
        var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var infos = new GpuLayerInfo[CompositorProtocol.MaxLayers];
        text.Append(CultureInfo.InvariantCulture, $"\n{name}: {plan.CanvasWidth} x {plan.CanvasHeight}, {plan.EntryCount} entries\n");

        // Nothing is handed over before the playhead comes near: the cue at 1 s is not drawn yet.
        var header = default(FrameSceneHeader);
        var before = plan.Evaluate(1.0, ref header, layers);
        var stillsBefore = 0;
        for (var i = 0; i < before; i++)
            stillsBefore += layers[i].SourceKind == (int)GpuSourceKind.Still ? 1 : 0;
        text.Append(CultureInfo.InvariantCulture, $"  at 1 s before the playhead came near: {stillsBefore} still(s)\n");
        if (stillsBefore != 0)
            problems.Add($"{name}: a picture was drawn before it was handed over");

        plan.PrepareAround(0);
        foreach (var moment in moments)
        {
            var count = plan.Evaluate(moment, ref header, layers);
            var described = plan.LayerInfoAt(moment, infos);
            text.Append(CultureInfo.InvariantCulture, $"  at {moment:0.###} s: {count} layer(s)\n");
            for (var i = 0; i < count; i++)
                text.Append("    ").Append(Describe(layers[i], i < described ? infos[i] : default)).Append('\n');

            // On exactly while a cue is.
            var stills = layers.AsSpan(0, count).ToArray().Count(d => d.SourceKind == (int)GpuSourceKind.Still);
            var up = track.Cues.Any(c => moment >= c.From && moment < c.To) ? 1 : 0;
            if (stills != up)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} at {moment} s: {stills} picture(s) drawn, {up} cue(s) up"));

            // Allocation-free, as the frame pump needs it.
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var k = 0; k < 1000; k++)
                plan.Evaluate(moment + k * 1e-6, ref header, layers);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (allocated > 0)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} at {moment} s: Evaluate allocated {allocated} bytes in 1000 calls"));
        }
    }

    private static string Describe(in GpuLayerDescriptor d, GpuLayerInfo info)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{(GpuSourceKind)d.SourceKind}");
        if (d.SourceKind == (int)GpuSourceKind.Media)
            line.Append(CultureInfo.InvariantCulture, $" source {d.SourceId} at {d.MediaSeconds:0.000}");
        if (d.StillResourceId >= 0)
            line.Append(CultureInfo.InvariantCulture, $" still {d.StillResourceId}");
        line.Append(CultureInfo.InvariantCulture, $" box ({d.X:0.0##}, {d.Y:0.0##}) {d.Width:0.0##} x {d.Height:0.0##}, crop ({d.CropLeft:0.000}, {d.CropTop:0.000})-({d.CropRight:0.000}, {d.CropBottom:0.000}), opacity {d.Opacity:0.000}, z {d.ZDepth}");
        if (info.Name is { Length: > 0 })
            line.Append(CultureInfo.InvariantCulture, $"  [{info.Name}]");
        return line.ToString();
    }

    private static SequenceExportState State(int width, int height, int sourceWidth, int sourceHeight, (int X, int Y, int Width, int Height) center,
        (double Left, double Top, double Right, double Bottom)? crop)
    {
        const string Main = "D:/media/film.mkv";
        var document = new TimelineDocument { Width = width, Height = height, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        document.Tracks.Add(track);
        var look = new LayerState
        {
            Kind = LayerKind.MainVideo, Name = "Main", PositionX = 0, PositionY = (double)center.Y / height, SizeWidth = 1, SourceWidth = 0.25, SourceHeight = 0.25,
            ImagePath = Main, ImageWidth = sourceWidth, ImageHeight = sourceHeight, MediaDuration = 6,
        };
        if (crop is { } cut)
            (look.CropLeft, look.CropTop, look.CropRight, look.CropBottom) = cut;
        track.Clips.Add(new TimelineClip { ContentKind = LayerKind.MainVideo, SourcePath = Main, Start = 0, End = 6, MediaOffset = 0, SourceDuration = 6, State = look });

        return new SequenceExportState
        {
            Timeline = document,
            SourceFrameRate = 30,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            RequestedSize = (width, height),
            OutputSize = (width, height),
            LocalMediaPath = Main,
            HasSource = true,
            MainMediaSeconds = 6,
            CenterRect = center,
            FrameEngine = true,
            HasCrop = crop is not null,
            CropFractions = crop ?? default,
            CaptionLayer = Layer.FromState(new LayerState
            {
                Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
                SourceWidth = 0.25, SourceHeight = 0.25,
            }, sourceWidth, sourceHeight, width, height),
            MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [Main] = 0 },
        };
    }

    // ----- A PGS file, written by hand -----

    /// <summary>A shot's pixels as BGRA8 (straight alpha): its fill inside a 2-pixel black border.</summary>
    private static byte[] Pixels(Shot shot)
    {
        var pixels = new byte[shot.Width * shot.Height * 4];
        for (var y = 0; y < shot.Height; y++)
        {
            for (var x = 0; x < shot.Width; x++)
                Colours[IndexAt(shot, x, y)].CopyTo(pixels, (y * shot.Width + x) * 4);
        }

        return pixels;
    }

    private static byte IndexAt(Shot shot, int x, int y) =>
        x < 2 || y < 2 || x >= shot.Width - 2 || y >= shot.Height - 2 ? (byte)2 : shot.Fill;

    /// <summary>
    /// The shots as a PGS file: for each, a display set that shows it (the presentation with one object in a window, the
    /// window, the palette, the object run-length coded, the end) and one that clears it at its end (no object).
    /// </summary>
    private static byte[] Sup(IReadOnlyList<Shot> shots)
    {
        var file = new List<byte>();
        var composition = 0;
        foreach (var shot in shots)
        {
            byte[] window = [1, 0, .. U16(shot.X), .. U16(shot.Y), .. U16(shot.Width), .. U16(shot.Height)];
            Segment(file, shot.From, 0x16, [.. U16(CanvasWidth), .. U16(CanvasHeight), 0x10, .. U16(composition++), 0x80, 0, 0, 1, .. U16(0), 0, 0, .. U16(shot.X), .. U16(shot.Y)]);
            Segment(file, shot.From, 0x17, window);
            Segment(file, shot.From, 0x14, [0, 0, 1, 235, 128, 128, 255, 2, 16, 128, 128, 255, 3, 235, 128, 128, 128]);
            var runs = Runs(shot);
            var length = runs.Length + 4;
            Segment(file, shot.From, 0x15, [.. U16(0), 0, 0xC0, (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. U16(shot.Width), .. U16(shot.Height), .. runs]);
            Segment(file, shot.From, 0x80, []);

            Segment(file, shot.To, 0x16, [.. U16(CanvasWidth), .. U16(CanvasHeight), 0x10, .. U16(composition++), 0x00, 0, 0, 0]);
            Segment(file, shot.To, 0x17, window);
            Segment(file, shot.To, 0x80, []);
        }

        return [.. file];
    }

    private static void Segment(List<byte> file, double at, byte kind, byte[] data)
    {
        var pts = (uint)Math.Round(at * 90000);
        file.AddRange([(byte)'P', (byte)'G', (byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts, 0, 0, 0, 0, kind, .. U16(data.Length)]);
        file.AddRange(data);
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    /// <summary>A shot's object data, PGS run-length coded row by row.</summary>
    private static byte[] Runs(Shot shot)
    {
        var data = new List<byte>();
        for (var y = 0; y < shot.Height; y++)
        {
            for (var x = 0; x < shot.Width;)
            {
                var colour = IndexAt(shot, x, y);
                var run = 1;
                while (x + run < shot.Width && IndexAt(shot, x + run, y) == colour && run < 16383)
                    run++;
                if (colour == 0)
                    data.AddRange(run < 64 ? [0, (byte)run] : [0, (byte)(0x40 | (run >> 8)), (byte)run]);
                else if (run < 3)
                    data.AddRange(Enumerable.Repeat(colour, run));
                else
                    data.AddRange(run < 64 ? [0, (byte)(0x80 | run), colour] : [0, (byte)(0xC0 | (run >> 8)), (byte)run, colour]);
                x += run;
            }

            data.AddRange([0, 0]);
        }

        return [.. data];
    }

    private static string? FindFfmpeg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[] { Path.Combine(local, "HandPeg-Data", "deps", "ffmpeg", "ffmpeg.exe"), Path.Combine(local, "HandPeg", "deps", "ffmpeg", "ffmpeg.exe") })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
