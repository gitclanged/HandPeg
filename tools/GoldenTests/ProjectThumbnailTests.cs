using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ShellThumbnails;
using Microsoft.Win32;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.2D's baked project thumbnails: their size, their JPEG, where a .hproj and a .hpb carry them and how
/// they are read back without reading the rest, and the readback wait the player draws one through, ended by its stop
/// (project-thumbnail); Explorer's preview of them, the very handler class Explorer loads over a stream of each kind of file, and its
/// registration for the user, on a test key of its own (explorer-thumbnails). Every line says what it proved; sizes in bytes,
/// colours and wait times as measured go to the console.
/// </summary>
public static partial class ProjectThumbnailTests
{
    // The colours of the test frames (B, G, R), and how far a JPEG (and a scaling) may move them.
    private static readonly (int B, int G, int R) Orange = (40, 120, 200), Blue = (200, 60, 30), Green = (30, 160, 90);
    private const int Tolerance = 8;

    // The test's own registry key: what Register writes there is what it writes under HKCU\Software\Classes. Deleted at the end.
    private const string TestRoot = @"Software\HandPeg.GoldenTests";

    /// <summary>project-thumbnail: sizes, the JPEG, the .hproj's thumbnailJpeg and the .hpb's thumbnail.jpg, then the readback wait and the player's stop.</summary>
    public static GraphResult ProjectThumbnails() =>
        Case("project-thumbnail", "# Baked project thumbnails (5.2D): their size, their JPEG, where a .hproj and a .hpb carry them, read back quickly.",
            (folder, text, expect) =>
            {
                RunProjectThumbnails(folder, text, expect);
                RunReadbackAbort(text, expect);
            });

    /// <summary>explorer-thumbnails: the handler class over a .hproj and a .hpb, and its registration on a test key.</summary>
    public static GraphResult ExplorerPreviews() =>
        Case("explorer-thumbnails", "# Explorer's preview of a project (5.2D): the thumbnail handler over a .hproj and a .hpb, and its registration for the user.",
            RunExplorerPreviews);

    private static GraphResult Case(string name, string title, Action<string, StringBuilder, Action<bool, string>> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.Thumbnails", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), name);
        try
        {
            Directory.CreateDirectory(folder);
            run(folder, text, Expect);
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

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // ----- project-thumbnail -----

    private static void RunProjectThumbnails(string folder, StringBuilder text, Action<bool, string> expect)
    {
        // The size: the short side 360 (the canvas's own when smaller), both sides even, the canvas's shape.
        var shapesKept = true;
        foreach (var (w, h) in new[] { (1080, 1920), (1920, 1080), (1080, 1080), (1001, 1777), (2560, 1081), (300, 200), (301, 199) })
        {
            var (width, height) = ProjectThumbnail.ThumbnailSize(w, h, ProjectThumbnail.ShortSide);
            text.Append(CultureInfo.InvariantCulture, $"size: {w} x {h} -> {width} x {height}\n");
            var (small, large, shortest, longest) = (Math.Min(width, height), Math.Max(width, height), Math.Min(w, h), Math.Max(w, h));
            shapesKept &= width % 2 == 0 && height % 2 == 0 && small == (Math.Min(ProjectThumbnail.ShortSide, shortest) & ~1)
                          && (w <= h) == (width <= height) && Math.Abs(large - (double)longest * small / shortest) <= 1;
        }

        expect(ProjectThumbnail.ThumbnailSize(1080, 1920, 360) == (360, 640), "a 9:16 canvas (1080 x 1920) has a 360 x 640 thumbnail");
        expect(ProjectThumbnail.ThumbnailSize(1920, 1080, 360) == (640, 360), "a 16:9 canvas (1920 x 1080) has a 640 x 360 one");
        expect(shapesKept, "every size is even on both sides, its short side 360 (the canvas's own, made even, when smaller), and the canvas's shape within a pixel");

        // The JPEG of a solid frame, and the picture it decodes to.
        var wide = ProjectThumbnail.EncodeJpeg(Solid(640, 360, Orange), 640, 360);
        expect(wide is { Length: > 2 } && wide[0] == 0xFF && wide[1] == 0xD8, "EncodeJpeg makes a JPEG of a 640 x 360 BGRA frame");
        if (wide is null)
            return;

        Console.WriteLine($"        a solid 640 x 360 frame: {wide.Length} bytes of JPEG");
        var decoded = ProjectThumbnail.Decode(wide, 360);
        expect(decoded is { PixelWidth: 640, PixelHeight: 360, IsFrozen: true }, "decoded at 360 pixels tall it is 640 x 360 again, and frozen (any thread may show it)");
        (int B, int G, int R) colour = decoded is null ? default : Centre(decoded);
        Console.WriteLine($"        decoded colour: B {colour.B}, G {colour.G}, R {colour.R} (made of B {Orange.B}, G {Orange.G}, R {Orange.R})");
        expect(Near(colour, Orange), $"its colour comes back within {Tolerance} levels");
        expect(ProjectThumbnail.Decode(wide, 72) is { PixelWidth: 128, PixelHeight: 72 }, "decoded at the Project Manager's 72 pixels it keeps its shape (128 x 72)");
        expect(ProjectThumbnail.Decode([1, 2, 3, 4], 72) is null, "bytes that are not a picture decode to none");

        // The .hproj: right after the header, PeekHeader none the wiser, read back by the quick reader and by opening.
        var state = new ProjectState { Name = "Thumbnail", ThumbnailJpeg = wide };
        var written = ProjectStore.Serialize(state);
        var names = JsonNode.Parse(written)!.AsObject().Select(p => p.Key).ToList();
        expect(names is ["format", "version", "writtenBy", "thumbnailJpeg", ..] && names[^1] == "timeline",
            $"the thumbnail is written right after the header, the timeline still last ({string.Join(", ", names.Take(5))} ...)");
        var path = Path.Combine(folder, "Thumbnail.hproj");
        ProjectStore.SaveTo(state, path);
        expect(ProjectStore.PeekHeader(path) is { Format: ProjectFile.FormatName, Version: ProjectFile.CurrentVersion, WrittenBy.Length: > 0 },
            "PeekHeader still reads the format, the version and the writer from the first 8 KB");
        expect(ProjectThumbnail.ReadFromProject(path) is { } quick && quick.AsSpan().SequenceEqual(wide), "ReadFromProject gives the same bytes");
        var loaded = ProjectStore.Load(path, unpackMasks: false);
        expect(loaded.ThumbnailJpeg is { } opened && opened.AsSpan().SequenceEqual(wide), "opened again, the project has the same thumbnail");
        expect(ProjectStore.Serialize(loaded) == written, "and written again, the file is the same (format 6 as it was)");

        // The quick reader stops at the thumbnail: a start of the file that ends after it is enough, one that ends inside it asks for more.
        var bytes = Encoding.UTF8.GetBytes(written);
        var after = written.IndexOf("\"name\"", StringComparison.Ordinal);
        var inside = written.IndexOf("\"thumbnailJpeg\"", StringComparison.Ordinal) + 100;
        var whole = ProjectThumbnail.ReadFromJson(bytes.AsSpan(0, after), isFinalBlock: false, out var cutAfter);
        var part = ProjectThumbnail.ReadFromJson(bytes.AsSpan(0, inside), isFinalBlock: false, out var cutInside);
        expect(whole is { } early && early.AsSpan().SequenceEqual(wide) && !cutAfter, "the text up to the property after it is all the reader needs");
        expect(part is null && cutInside, "a start that ends inside it says it was cut off (the file is read further)");

        // A project without one: no key, and the reader stops at the property after writtenBy.
        state.ThumbnailJpeg = null;
        var none = ProjectStore.Serialize(state);
        expect(!none.Contains("\"thumbnailJpeg\"", StringComparison.Ordinal), "a state without a thumbnail writes no thumbnailJpeg");
        var nonePath = Path.Combine(folder, "None.hproj");
        File.WriteAllText(nonePath, none);
        expect(ProjectThumbnail.ReadFromProject(nonePath) is null, "ReadFromProject finds none in it");
        var noneBytes = Encoding.UTF8.GetBytes(none);
        expect(ProjectThumbnail.ReadFromJson(noneBytes.AsSpan(0, none.IndexOf("\"name\"", StringComparison.Ordinal) + 8), isFinalBlock: false, out var cutNone) is null && !cutNone,
            "it knows there is none from the property after writtenBy, without reading on");
        expect(ProjectStore.Load(nonePath, unpackMasks: false).ThumbnailJpeg is null, "opened, it has none");

        // The .hpb: thumbnail.jpg stored right after project.hproj, named in the manifest, read back alone, left in the bundle on unpacking.
        var tall = ProjectThumbnail.EncodeJpeg(Solid(360, 640, Green), 360, 640);
        expect(tall is { Length: > 2 }, "a 360 x 640 frame makes a JPEG too");
        if (tall is null)
            return;

        state.ThumbnailJpeg = tall;
        var bundlePath = Path.Combine(folder, "Tall.hpb");
        ProjectBundle.Pack(state, bundlePath);
        using (var archive = ZipFile.OpenRead(bundlePath))
        {
            var entries = archive.Entries.Select(e => e.FullName).ToList();
            text.Append("bundle entries: ").AppendJoin(", ", entries).Append('\n');
            var entry = archive.GetEntry(ProjectThumbnail.BundleEntry);
            expect(entry is not null && entry.CompressedLength == entry.Length && entry.Length == tall.Length, "thumbnail.jpg is in the bundle, stored as it is");
            expect(entries.IndexOf(ProjectThumbnail.BundleEntry) == entries.IndexOf("project.hproj") + 1, "right after project.hproj");
            var manifest = JsonNode.Parse(new StreamReader(archive.GetEntry("bundle.json")!.Open()).ReadToEnd())!.AsObject();
            expect(manifest["thumbnail"]?.GetValue<string>() == ProjectThumbnail.BundleEntry, "the manifest names it (\"thumbnail\")");
            var bundled = new StreamReader(archive.GetEntry("project.hproj")!.Open()).ReadToEnd();
            expect(ProjectThumbnail.ReadFromJson(Encoding.UTF8.GetBytes(bundled), isFinalBlock: true, out _) is { } inProject && inProject.AsSpan().SequenceEqual(tall),
                "the bundled project carries it too");
        }

        expect(ProjectThumbnail.ReadFromBundle(bundlePath) is { } fromBundle && fromBundle.AsSpan().SequenceEqual(tall), "ReadFromBundle gives the same bytes");
        var unpacked = ProjectBundle.Unpack(bundlePath);
        var unpackedFolder = Path.GetDirectoryName(unpacked)!;
        expect(Directory.GetFiles(unpackedFolder, "*.jpg", SearchOption.AllDirectories).Length == 0, "unpacking leaves it in the bundle: no picture in the folder");
        expect(ProjectThumbnail.ReadFromProject(unpacked) is { } fromUnpacked && fromUnpacked.AsSpan().SequenceEqual(tall), "and the unpacked project still carries it");

        // A bundle of a project without one: no entry, no manifest field.
        state.ThumbnailJpeg = null;
        var plainPath = Path.Combine(folder, "Plain.hpb");
        ProjectBundle.Pack(state, plainPath);
        using (var archive = ZipFile.OpenRead(plainPath))
        {
            var manifest = JsonNode.Parse(new StreamReader(archive.GetEntry("bundle.json")!.Open()).ReadToEnd())!.AsObject();
            expect(archive.GetEntry(ProjectThumbnail.BundleEntry) is null && manifest["thumbnail"] is null, "a bundle of a project without one has no thumbnail.jpg, and its manifest names none");
        }

        expect(ProjectThumbnail.ReadFromBundle(plainPath) is null, "ReadFromBundle finds none in it");
    }

    // ----- project-thumbnail: the readback wait and the player's stop -----

    // How soon a stop must end the wait, ms: a slice and the scheduler's slack, far inside GpuPreviewEngine's 2 s join.
    private const int PromptMs = 250;

    /// <summary>
    /// The wait CompositorClient.TryReadback makes (WaitForSequence), driven without a worker: a count and a frame event of the
    /// test's own in place of the control plane's. The player passes its stop event as the abort (the thumbnail and Export Frame),
    /// so that a pump told to stop leaves within a slice and is joined before the compositor's memory is let go.
    /// </summary>
    private static void RunReadbackAbort(StringBuilder text, Action<bool, string> expect)
    {
        using var presented = new AutoResetEvent(false);
        using var stop = new ManualResetEvent(false);
        var count = new long[1];
        var clock = new Stopwatch();
        bool Readback(long target, WaitHandle? abort, int milliseconds, out double took)
        {
            clock.Restart();
            var got = CompositorClient.WaitForSequence(ref count[0], target, presented, abort, milliseconds);
            took = clock.Elapsed.TotalMilliseconds;
            return got;
        }

        text.Append(CultureInfo.InvariantCulture, $"readback wait: slices of {CompositorClient.ReadbackSliceMs} ms\n");

        // The frame there already, then read back during the wait (the stop given, not set).
        count[0] = 1;
        var ready = Readback(1, stop, 5000, out var readyMs);
        expect(ready && readyMs < PromptMs, "readback wait: a frame already read back is taken at once");
        var raise = Task.Run(() =>
        {
            Thread.Sleep(50);
            Volatile.Write(ref count[0], 2);
            presented.Set();
        });
        var arrived = Readback(2, stop, 5000, out var arrivedMs);
        raise.Wait();
        presented.Reset();
        expect(arrived && arrivedMs < 5000, "readback wait: a frame read back during the wait ends it with the frame");

        // The player's stop set before the wait, then during a long one (its Dispose): false, within a slice of it.
        stop.Set();
        var before = Readback(3, stop, 5000, out var beforeMs);
        expect(!before && beforeMs < PromptMs, "readback wait: the player's stop already set, it gives up at once (false)");
        stop.Reset();
        var stopSetAt = 0L;
        var stopper = Task.Run(() =>
        {
            Thread.Sleep(100);
            Volatile.Write(ref stopSetAt, Stopwatch.GetTimestamp());
            stop.Set();
        });
        var during = Readback(3, stop, 5000, out var duringMs);
        var leftAt = Stopwatch.GetTimestamp();
        stopper.Wait();
        var afterStopMs = Stopwatch.GetElapsedTime(Volatile.Read(ref stopSetAt), leftAt).TotalMilliseconds;
        expect(!during && afterStopMs >= 0 && afterStopMs < PromptMs,
            $"readback wait: the stop set 100 ms into a 5000 ms wait ends it (false) within {PromptMs} ms of the stop, inside the pump's 2 s join");

        // A stop that stays unset, and no abort at all (the export's and the filmstrips' calls): the timeout as before.
        stop.Reset();
        var unset = Readback(3, stop, 60, out var unsetMs);
        expect(!unset && unsetMs >= 55, "readback wait: a stop that stays unset changes nothing (false at its 60 ms timeout)");
        var plain = Readback(3, null, 60, out var plainMs);
        expect(!plain && plainMs >= 55, "readback wait: without an abort it times out as it always did (false at 60 ms)");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"        readback wait: ready {readyMs:0.0} ms, frame after {arrivedMs:0.0} ms, stop set before {beforeMs:0.0} ms, stop set during: left {afterStopMs:0.0} ms after it ({duringMs:0.0} ms in), timeouts {unsetMs:0.0} / {plainMs:0.0} ms"));
    }

    // ----- explorer-thumbnails -----

    private static void RunExplorerPreviews(string folder, StringBuilder text, Action<bool, string> expect)
    {
        expect(Guid.Parse(ProjectThumbnailProvider.ClassId) == Guid.Parse(ExplorerThumbnails.HandlerClsid) && typeof(ProjectThumbnailProvider).GUID == Guid.Parse(ProjectThumbnailProvider.ClassId),
            "the handler class's CLSID is the one HandPeg registers");
        expect(typeof(IThumbnailProvider).GUID == Guid.Parse(ExplorerThumbnails.ThumbnailProviderKey), "and the ShellEx key it is registered under is IThumbnailProvider's");

        // Two-tone (the top half its colour, the bottom half orange), so the bitmap's rows say which way up they are: GetObject reports
        // a DIB section's height as positive whichever way its rows run, so the order is read from the pixels themselves.
        var wide = ProjectThumbnail.EncodeJpeg(TwoTone(640, 360, Blue, Orange), 640, 360);
        var tall = ProjectThumbnail.EncodeJpeg(TwoTone(360, 640, Green, Orange), 360, 640);
        expect(wide is not null && tall is not null, "the two test thumbnails are made (640 x 360, 360 x 640)");
        if (wide is null || tall is null)
            return;

        var project = Path.Combine(folder, "Wide.hproj");
        ProjectStore.SaveTo(new ProjectState { Name = "Wide", ThumbnailJpeg = wide }, project);
        var bundle = Path.Combine(folder, "Tall.hpb");
        ProjectBundle.Pack(new ProjectState { Name = "Tall", ThumbnailJpeg = tall }, bundle);
        var bare = Path.Combine(folder, "Bare.hproj");
        ProjectStore.SaveTo(new ProjectState { Name = "Bare" }, bare);
        var notProject = Path.Combine(folder, "Noise.hproj");
        File.WriteAllBytes(notProject, Enumerable.Range(0, 4096).Select(i => (byte)(i * 37 % 251)).ToArray());

        // The handler over each, as Explorer calls it: a stream, then a bitmap that fits 256 x 256.
        foreach (var (path, kind, width, height, colour) in new[] { (project, ".hproj 16:9", 256, 144, Blue), (bundle, ".hpb 9:16", 144, 256, Green) })
        {
            var preview = Preview(path, 256);
            text.Append(CultureInfo.InvariantCulture, $"{kind}: HRESULT 0x{preview.Result:X8}, {preview.Width} x {preview.Height}, {preview.BitsPerPixel} bits, top-down {Near(preview.Upper, colour) && Near(preview.Lower, Orange)}, alpha type {preview.Alpha}\n");
            Console.WriteLine($"        {kind}: first rows B {preview.Upper.B}, G {preview.Upper.G}, R {preview.Upper.R}; last rows B {preview.Lower.B}, G {preview.Lower.G}, R {preview.Lower.R}");
            expect(preview.Result == ProjectThumbnailProvider.Ok && preview.Width == width && preview.Height == height,
                $"a {kind} gives a {width} x {height} bitmap: it fits 256 x 256 in the thumbnail's shape");
            expect(preview.BitsPerPixel == 32 && preview.Alpha == ProjectThumbnailProvider.AlphaRgb, $"a 32-bit DIB section, its alpha meaning nothing (WTSAT_RGB) ({kind})");
            expect(Near(preview.Upper, colour) && Near(preview.Lower, Orange), $"top-down, in the thumbnail's colours: its first rows the top's, within {Tolerance} levels ({kind})");
        }

        var small = Preview(project, 96);
        expect(small.Result == ProjectThumbnailProvider.Ok && small.Width == 96 && small.Height == 54, "asked for 96 it draws 96 x 54");
        var none = Preview(bare, 256);
        expect(none.Result == ProjectThumbnailProvider.Fail && !none.HadBitmap, "a project without a thumbnail gets E_FAIL and no bitmap (Explorer shows its icon)");
        var noise = Preview(notProject, 256);
        expect(noise.Result == ProjectThumbnailProvider.Fail && !noise.HadBitmap, "a file that is not a project gets E_FAIL too");

        var twice = new ProjectThumbnailProvider();
        using (var file = File.OpenRead(project))
        {
            var stream = new ManagedStream(file);
            expect(twice.Initialize(stream, 0) == ProjectThumbnailProvider.Ok && twice.Initialize(stream, 0) != ProjectThumbnailProvider.Ok,
                "a second Initialize is refused (the handler is for one file)");
        }

        // The registration, on the test's own key: what differs is written, a second time nothing, and only HandPeg's is taken out.
        try
        {
            // Left over from a run that did not finish, it would make the first Register look like a second one.
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
            using var classes = Registry.CurrentUser.CreateSubKey(TestRoot + @"\Classes");
            const string handler = @"X:\HandPeg-Data\shell\0123456789abcdef";
            expect(ExplorerThumbnails.Register(classes, handler), "Register writes the handler's keys");
            Describe(classes, "", text);
            var clsid = @"CLSID\" + ExplorerThumbnails.HandlerClsid;
            expect(Value(classes, clsid, "") == ExplorerThumbnails.HandlerName, "its class is named");
            expect(Value(classes, clsid + @"\InprocServer32", "") == Path.Combine(handler, ExplorerThumbnails.ComHostFile)
                   && Value(classes, clsid + @"\InprocServer32", "ThreadingModel") == "Apartment", "its server is the COM host in the handler's folder, Apartment-threaded");
            expect(Value(classes, $@".hproj\ShellEx\{ExplorerThumbnails.ThumbnailProviderKey}", "") == ExplorerThumbnails.HandlerClsid
                   && Value(classes, $@".hpb\ShellEx\{ExplorerThumbnails.ThumbnailProviderKey}", "") == ExplorerThumbnails.HandlerClsid,
                ".hproj and .hpb name it as their thumbnail handler");
            expect(!ExplorerThumbnails.Register(classes, handler), "a second Register writes nothing (and Explorer is told nothing)");
            const string moved = @"X:\HandPeg-Data\shell\fedcba9876543210";
            expect(ExplorerThumbnails.Register(classes, moved) && Value(classes, clsid + @"\InprocServer32", "") == Path.Combine(moved, ExplorerThumbnails.ComHostFile),
                "a handler in another folder (a new version): its server's path is written again");

            // Another program's handler for .hpb is not HandPeg's to take out.
            const string other = "{00000000-0000-0000-0000-000000000001}";
            using (var key = classes.CreateSubKey($@".hpb\ShellEx\{ExplorerThumbnails.ThumbnailProviderKey}"))
                key.SetValue("", other);
            expect(ExplorerThumbnails.Unregister(classes), "Unregister takes the handler out");
            Describe(classes, "", text);
            expect(classes.OpenSubKey(clsid) is null && classes.OpenSubKey(".hproj") is null, "its class is gone, and so is the .hproj key it made (left empty)");
            expect(Value(classes, $@".hpb\ShellEx\{ExplorerThumbnails.ThumbnailProviderKey}", "") == other, "another class's handler for .hpb stays");
            expect(!ExplorerThumbnails.Unregister(classes), "a second Unregister finds nothing of HandPeg's");

            // The open command (5.2-Release): .hproj and .hpb name HandPeg's ProgIDs, whose command starts the installed HandPeg.exe with the file.
            const string exe = @"X:\HandPeg\current\HandPeg.exe";
            expect(ExplorerThumbnails.Associate(classes, exe), "Associate writes the open command");
            Describe(classes, "", text);
            expect(Value(classes, ".hproj", "") == ExplorerThumbnails.ProjectProgId && Value(classes, ".hpb", "") == ExplorerThumbnails.BundleProgId,
                ".hproj and .hpb open with HandPeg's ProgIDs");
            expect(Value(classes, ExplorerThumbnails.ProjectProgId + @"\shell\open\command", "") == "\"" + exe + "\" \"%1\""
                   && Value(classes, ExplorerThumbnails.BundleProgId + @"\shell\open\command", "") == "\"" + exe + "\" \"%1\"",
                "each ProgID's open command starts HandPeg.exe with the file");
            expect(Value(classes, ExplorerThumbnails.ProjectProgId + @"\DefaultIcon", "") == "\"" + exe + "\",0", "its icon is HandPeg.exe's own");
            expect(Value(classes, @".hproj\OpenWithProgids", ExplorerThumbnails.ProjectProgId) == "" && Value(classes, @".hpb\OpenWithProgids", ExplorerThumbnails.BundleProgId) == "",
                "each extension lists its ProgID in its Open With list");
            expect(!ExplorerThumbnails.Associate(classes, exe), "a second Associate writes nothing (and Explorer is told nothing)");
            expect(classes.OpenSubKey(".mp4") is null && classes.OpenSubKey(".mkv") is null, "no media extension is touched");

            // Another program's claim on .hpb is not HandPeg's to take: it stays, and HandPeg is only in .hpb's Open With list.
            using (var key = classes.CreateSubKey(".hpb"))
                key.SetValue("", "Other.Bundle");
            expect(!ExplorerThumbnails.Associate(classes, exe) && Value(classes, ".hpb", "") == "Other.Bundle", "another program's claim on .hpb stays");
            expect(ExplorerThumbnails.Dissociate(classes), "Dissociate takes the open command out");
            Describe(classes, "", text);
            expect(classes.OpenSubKey(ExplorerThumbnails.ProjectProgId) is null && classes.OpenSubKey(ExplorerThumbnails.BundleProgId) is null
                   && classes.OpenSubKey(".hproj") is null, "HandPeg's ProgIDs are gone, and so is the .hproj key (left empty)");
            expect(Value(classes, ".hpb", "") == "Other.Bundle" && classes.OpenSubKey(@".hpb\OpenWithProgids") is null,
                "the other program's claim on .hpb stays; HandPeg's Open With entry is gone");
            expect(!ExplorerThumbnails.Dissociate(classes), "a second Dissociate finds nothing of HandPeg's");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        }
    }

    // What the handler gave for a file: its HRESULT, and the bitmap as GDI describes it (then deleted).
    // Upper and Lower: the middle column's pixel a quarter and three quarters of the way through the bitmap's memory.
    private readonly record struct PreviewResult(int Result, bool HadBitmap, int Width, int Height, int BitsPerPixel, uint Alpha, (int B, int G, int R) Upper, (int B, int G, int R) Lower);

    private static PreviewResult Preview(string path, uint cx)
    {
        var provider = new ProjectThumbnailProvider();
        using var file = File.OpenRead(path);
        var initialized = provider.Initialize(new ManagedStream(file), 0);
        if (initialized != ProjectThumbnailProvider.Ok)
            return new PreviewResult(initialized, false, 0, 0, 0, 0, default, default);

        var result = provider.GetThumbnail(cx, out var bitmap, out var alpha);
        if (bitmap == 0)
            return new PreviewResult(result, false, 0, 0, 0, alpha, default, default);

        try
        {
            if (GetObject(bitmap, Marshal.SizeOf<DibSection>(), out var dib) <= 0 || dib.Bits == 0)
                return new PreviewResult(result, true, 0, 0, 0, alpha, default, default);

            var (width, height) = (dib.Width, Math.Abs(dib.Height));
            (int B, int G, int R) At(int row)
            {
                var pixel = Marshal.ReadInt32(dib.Bits + (nint)((long)row * dib.WidthBytes + (width / 2) * 4));
                return (pixel & 0xFF, (pixel >> 8) & 0xFF, (pixel >> 16) & 0xFF);
            }

            return new PreviewResult(result, true, width, height, dib.BitCount, alpha, At(height / 4), At(height * 3 / 4));
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    // Every key and value under a key, sorted: what the golden shows of the registration.
    private static void Describe(RegistryKey key, string path, StringBuilder text)
    {
        foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            text.Append("  value: ").Append(path.Length > 0 ? path : "(root)").Append(" [").Append(name.Length > 0 ? name : "(default)").Append("] = ").Append(key.GetValue(name)).Append('\n');
        foreach (var name in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            using var sub = key.OpenSubKey(name)!;
            var subPath = path.Length > 0 ? path + @"\" + name : name;
            if (sub.ValueCount == 0 && sub.SubKeyCount == 0)
                text.Append("  key: ").Append(subPath).Append('\n');
            Describe(sub, subPath, text);
        }
    }

    private static string? Value(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    // ----- Pictures -----

    private static byte[] TwoTone(int width, int height, (int B, int G, int R) top, (int B, int G, int R) bottom)
    {
        var pixels = Solid(width, height, bottom);
        for (var i = 0; i < width * (height / 2) * 4; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2]) = ((byte)top.B, (byte)top.G, (byte)top.R);
        return pixels;
    }

    private static byte[] Solid(int width, int height, (int B, int G, int R) colour)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)colour.B, (byte)colour.G, (byte)colour.R, 255);
        return pixels;
    }

    private static (int B, int G, int R) Centre(BitmapSource picture)
    {
        var converted = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(converted.PixelWidth / 2, converted.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2]);
    }

    private static bool Near((int B, int G, int R) a, (int B, int G, int R) b) =>
        Math.Abs(a.B - b.B) <= Tolerance && Math.Abs(a.G - b.G) <= Tolerance && Math.Abs(a.R - b.R) <= Tolerance;

    // ----- What Explorer hands the handler, and what GDI says of what it hands back -----

    /// <summary>An IStream over a file, as Explorer gives a thumbnail handler one: read and seek, its size from Stat.</summary>
    private sealed class ManagedStream(Stream inner) : IStream
    {
        public void Read(byte[] pv, int cb, nint pcbRead)
        {
            var read = inner.Read(pv, 0, cb);
            if (pcbRead != 0)
                Marshal.WriteInt32(pcbRead, read);
        }

        public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
        {
            var at = inner.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != 0)
                Marshal.WriteInt64(plibNewPosition, at);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG { cbSize = inner.Length, type = 2 };

        public void Clone(out IStream ppstm) => throw new NotSupportedException();

        public void Commit(int grfCommitFlags)
        {
        }

        public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten) => throw new NotSupportedException();

        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void Revert()
        {
        }

        public void SetSize(long libNewSize) => throw new NotSupportedException();

        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void Write(byte[] pv, int cb, nint pcbWritten) => throw new NotSupportedException();
    }

    /// <summary>DIBSECTION: the BITMAP (its pixels' address), its BITMAPINFOHEADER (a negative height: rows top first), and the rest.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
        public uint HeaderSize;
        public int HeaderWidth;
        public int HeaderHeight;
        public ushort HeaderPlanes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
        public uint Field0;
        public uint Field1;
        public uint Field2;
        public nint Section;
        public uint Offset;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(nint handle, int size, out DibSection section);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}
