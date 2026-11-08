using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
/// (project-thumbnail); Explorer's preview of them, the very handler Explorer loads (its code compiled in here, called through its
/// exports and COM tables as Explorer's surrogate calls the NativeAOT DLL; and that DLL itself where a Release build has published it)
/// over a stream of each kind of file, and its registration for the user, on a test key of its own (explorer-thumbnails). Every line
/// says what it proved; sizes in bytes, colours and wait times as measured go to the console.
/// </summary>
public static unsafe partial class ProjectThumbnailTests
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

    // The interfaces as Windows declares them (unknwn.h, shobjidl.h, thumbcache.h): what Explorer's surrogate asks a handler for.
    private static readonly Guid UnknownIid = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid ClassFactoryIid = new("00000001-0000-0000-c000-000000000046");
    private static readonly Guid InitializeWithStreamIid = new("b824b49d-22ac-4161-ac8a-9916e8fa3f7f");
    private static readonly Guid InitializeWithFileIid = new("b7d14566-0509-4cce-a71f-0a554233bd9b");
    private static readonly Guid ThumbnailProviderIid = new("e357fccd-a995-4576-b01f-234630154e96");

    // S_FALSE, E_NOINTERFACE, CLASS_E_NOAGGREGATION, CLASS_E_CLASSNOTAVAILABLE.
    private const int SFalse = 1, NoInterface = unchecked((int)0x80004002), NoAggregation = unchecked((int)0x80040110), ClassNotAvailable = unchecked((int)0x80040111);

    // STGM_READ | STGM_SHARE_DENY_WRITE: the file opened as Explorer's surrogate opens one for a thumbnail handler.
    private const uint ReadDenyWrite = 0x20;

    private static void RunExplorerPreviews(string folder, StringBuilder text, Action<bool, string> expect)
    {
        var registered = Guid.Parse(ExplorerThumbnails.HandlerClsid);
        expect(ComIds.Provider == registered && Guid.Parse(ProjectThumbnailProvider.ClassId) == registered, "the handler class's CLSID is the one HandPeg registers");
        expect(ComIds.ThumbnailProvider == Guid.Parse(ExplorerThumbnails.ThumbnailProviderKey) && ComIds.ThumbnailProvider == ThumbnailProviderIid,
            "and the ShellEx key it is registered under is IThumbnailProvider's");
        expect(ComIds.Unknown == UnknownIid && ComIds.ClassFactory == ClassFactoryIid && ComIds.InitializeWithStream == InitializeWithStreamIid,
            "its objects answer to Windows' own IUnknown, IClassFactory and IInitializeWithStream");

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
        var broken = Path.Combine(folder, "Broken.hproj");
        ProjectStore.SaveTo(new ProjectState { Name = "Broken", ThumbnailJpeg = Enumerable.Range(0, 2048).Select(i => (byte)(i * 13 % 251)).ToArray() }, broken);

        // The handler's code compiled into this program, called as Explorer's surrogate calls the DLL: its export by address, then its
        // objects through their tables, over Windows' own stream of each file; a bitmap that fits 256 x 256.
        delegate* unmanaged<Guid*, Guid*, void**, int> getClassObject = &ShellExports.DllGetClassObject;
        foreach (var (path, kind, width, height, colour) in new[] { (project, ".hproj 16:9", 256, 144, Blue), (bundle, ".hpb 9:16", 144, 256, Green) })
        {
            var preview = Preview(getClassObject, path, 256);
            text.Append(CultureInfo.InvariantCulture, $"{kind}: HRESULT 0x{preview.Result:X8}, {preview.Width} x {preview.Height}, {preview.BitsPerPixel} bits, top-down {Near(preview.Upper, colour) && Near(preview.Lower, Orange)}, alpha type {preview.Alpha}\n");
            Console.WriteLine($"        {kind}: first rows B {preview.Upper.B}, G {preview.Upper.G}, R {preview.Upper.R}; last rows B {preview.Lower.B}, G {preview.Lower.G}, R {preview.Lower.R}");
            expect(preview.Result == ProjectThumbnailProvider.Ok && preview.Width == width && preview.Height == height,
                $"a {kind} gives a {width} x {height} bitmap: it fits 256 x 256 in the thumbnail's shape");
            expect(preview.BitsPerPixel == 32 && preview.Alpha == ProjectThumbnailProvider.AlphaRgb, $"a 32-bit DIB section, its alpha meaning nothing (WTSAT_RGB) ({kind})");
            expect(Near(preview.Upper, colour) && Near(preview.Lower, Orange), $"top-down, in the thumbnail's colours: its first rows the top's, within {Tolerance} levels ({kind})");
        }

        var small = Preview(getClassObject, project, 96);
        expect(small.Result == ProjectThumbnailProvider.Ok && small.Width == 96 && small.Height == 54, "asked for 96 it draws 96 x 54");
        var none = Preview(getClassObject, bare, 256);
        expect(none.Result == ProjectThumbnailProvider.Fail && !none.HadBitmap, "a project without a thumbnail gets E_FAIL and no bitmap (Explorer shows its icon)");
        var noise = Preview(getClassObject, notProject, 256);
        expect(noise.Result == ProjectThumbnailProvider.Fail && !noise.HadBitmap, "a file that is not a project gets E_FAIL too");
        var damaged = Preview(getClassObject, broken, 256);
        expect(damaged.Result == ProjectThumbnailProvider.Fail && !damaged.HadBitmap, "and so does a project whose thumbnail is not a picture (nothing leaves the handler as an exception)");

        Objects(getClassObject, &ShellExports.DllCanUnloadNow, project, expect);
        Published(project, expect);

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
            expect(Value(classes, clsid + @"\InprocServer32", "") == Path.Combine(handler, ExplorerThumbnails.HandlerFile)
                   && Value(classes, clsid + @"\InprocServer32", "ThreadingModel") == "Apartment", "its server is the handler's own DLL in its folder (no .NET host), Apartment-threaded");
            expect(Value(classes, $@".hproj\ShellEx\{ExplorerThumbnails.ThumbnailProviderKey}", "") == ExplorerThumbnails.HandlerClsid
                   && Value(classes, $@".hpb\ShellEx\{ExplorerThumbnails.ThumbnailProviderKey}", "") == ExplorerThumbnails.HandlerClsid,
                ".hproj and .hpb name it as their thumbnail handler");
            expect(!ExplorerThumbnails.Register(classes, handler), "a second Register writes nothing (and Explorer is told nothing)");
            const string moved = @"X:\HandPeg-Data\shell\fedcba9876543210";
            expect(ExplorerThumbnails.Register(classes, moved) && Value(classes, clsid + @"\InprocServer32", "") == Path.Combine(moved, ExplorerThumbnails.HandlerFile),
                "a handler in another folder (a new version): its server's path is written again");
            using (var key = classes.CreateSubKey(clsid + @"\InprocServer32"))
                key.SetValue("", Path.Combine(moved, "HandPeg.ShellThumbnails.comhost.dll"));
            expect(ExplorerThumbnails.Register(classes, moved) && Value(classes, clsid + @"\InprocServer32", "") == Path.Combine(moved, ExplorerThumbnails.HandlerFile),
                "5.3-Stage1's registration (its .NET COM host) is written over with the DLL at the next start");

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

    private static PreviewResult Preview(delegate* unmanaged<Guid*, Guid*, void**, int> getClassObject, string path, uint cx)
    {
        var (result, bitmap, alpha) = Thumbnail(getClassObject, path, cx);
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

    /// <summary>
    /// A file's thumbnail asked for as Explorer's surrogate asks a handler DLL: DllGetClassObject (the registered CLSID, IClassFactory),
    /// CreateInstance (IInitializeWithStream), Initialize with Windows' own stream of the file, QueryInterface (IThumbnailProvider),
    /// GetThumbnail, then every reference released. The HRESULT, and the bitmap the caller now owns (0 for none).
    /// </summary>
    private static (int Result, nint Bitmap, uint Alpha) Thumbnail(delegate* unmanaged<Guid*, Guid*, void**, int> getClassObject, string path, uint cx)
    {
        void* factory = null;
        var answer = GetClassObject(getClassObject, Guid.Parse(ExplorerThumbnails.HandlerClsid), &factory);
        if (answer != 0 || factory is null)
            return (answer, 0, 0);

        void* initialize = null, thumbnail = null;
        try
        {
            answer = CreateInstance(factory, null, InitializeWithStreamIid, &initialize);
            if (answer != 0 || initialize is null)
                return (answer, 0, 0);

            var stream = OpenStream(path);
            answer = Initialize(initialize, stream);
            Release(stream);
            if (answer != 0)
                return (answer, 0, 0);

            answer = Query(initialize, ThumbnailProviderIid, &thumbnail);
            if (answer != 0 || thumbnail is null)
                return (answer, 0, 0);

            nint bitmap = 0;
            var alpha = 0;
            answer = GetThumbnail(thumbnail, cx, &bitmap, &alpha);
            return (answer, bitmap, (uint)alpha);
        }
        finally
        {
            if (thumbnail is not null)
                Release(thumbnail);
            if (initialize is not null)
                Release(initialize);
            Release(factory);
        }
    }

    /// <summary>
    /// One object as Explorer's surrogate holds it: refused a second file, one identity whichever interface it is asked from, the file let go
    /// of once its thumbnail is read (or with the object, when it never is), freed by its last Release; and what the library refuses
    /// (another class, an aggregate, a handler of paths) and that it is never unloaded.
    /// </summary>
    private static void Objects(delegate* unmanaged<Guid*, Guid*, void**, int> getClassObject, delegate* unmanaged<int> canUnloadNow, string project, Action<bool, string> expect)
    {
        void* factory = null;
        if (GetClassObject(getClassObject, Guid.Parse(ExplorerThumbnails.HandlerClsid), &factory) != 0 || factory is null)
        {
            expect(false, "DllGetClassObject gives the handler's class factory");
            return;
        }

        try
        {
            void* other = null;
            expect(GetClassObject(getClassObject, new Guid("00000000-0000-0000-0000-000000000001"), &other) == ClassNotAvailable && other is null,
                "another class: DllGetClassObject says CLASS_E_CLASSNOTAVAILABLE and gives nothing");
            expect(canUnloadNow() == SFalse, "DllCanUnloadNow says S_FALSE: a NativeAOT library is never unloaded, so Explorer's surrogate keeps it");

            void* initialize = null;
            if (CreateInstance(factory, null, InitializeWithStreamIid, &initialize) != 0 || initialize is null)
            {
                expect(false, "CreateInstance gives an IInitializeWithStream");
                return;
            }

            var stream = OpenStream(project);
            var first = Initialize(initialize, stream);
            var second = Initialize(initialize, stream);
            Release(stream);
            expect(first == ProjectThumbnailProvider.Ok && second != ProjectThumbnailProvider.Ok, "a second Initialize is refused (the handler is for one file)");

            void* thumbnail = null, fromInitialize = null, fromThumbnail = null;
            Query(initialize, ThumbnailProviderIid, &thumbnail);
            Query(initialize, UnknownIid, &fromInitialize);
            if (thumbnail is not null)
                Query(thumbnail, UnknownIid, &fromThumbnail);
            expect(thumbnail is not null && fromInitialize == initialize && fromThumbnail == initialize,
                "QueryInterface finds IThumbnailProvider, and IUnknown is the same object from either interface (COM's identity rule)");

            nint bitmap = 0;
            var alpha = 0;
            var drawn = thumbnail is not null ? GetThumbnail(thumbnail, 256, &bitmap, &alpha) : ProjectThumbnailProvider.Fail;
            if (bitmap != 0)
                DeleteObject(bitmap);
            expect(drawn == ProjectThumbnailProvider.Ok && FileIsFree(project), "the file is let go of once its thumbnail is read, while Explorer still holds the object");

            if (fromThumbnail is not null)
                Release(fromThumbnail);
            if (fromInitialize is not null)
                Release(fromInitialize);
            if (thumbnail is not null)
                Release(thumbnail);
            expect(Release(initialize) == 0, "the last Release frees the object (its count back to 0)");

            void* unread = null;
            var heldWhileAlive = false;
            if (CreateInstance(factory, null, InitializeWithStreamIid, &unread) == 0 && unread is not null)
            {
                var again = OpenStream(project);
                Initialize(unread, again);
                Release(again);
                heldWhileAlive = !FileIsFree(project);
                Release(unread);
            }

            expect(heldWhileAlive && FileIsFree(project), "a file never read is held while its object lives, and let go of by the object's last Release");

            void* aggregate = null;
            expect(CreateInstance(factory, factory, InitializeWithStreamIid, &aggregate) == NoAggregation && aggregate is null,
                "an aggregating outer object is refused (CLASS_E_NOAGGREGATION)");
            void* byPath = null;
            expect(CreateInstance(factory, null, InitializeWithFileIid, &byPath) == NoInterface && byPath is null,
                "it takes a stream only (as a handler in Explorer's isolated surrogate should): asked for IInitializeWithFile, E_NOINTERFACE");
        }
        finally
        {
            Release(factory);
        }
    }

    /// <summary>
    /// The handler as HandPeg's Release build publishes it for shell\ (src\ShellThumbnails\bin\Release\net10.0-windows\&lt;rid&gt;\publish;
    /// Release.ps1 makes both), where one has been made: each a native DLL of its processor with the two exports, and the one of this
    /// process's processor loaded as Explorer's surrogate loads it, drawing what the compiled-in code draws. A Debug build makes none, so
    /// the golden text is the same either way; what was found goes to the console.
    /// </summary>
    private static void Published(string project, Action<bool, string> expect)
    {
        var root = PortableBootstrapTests.RepositoryRoot() ?? "";
        var own = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (ushort)0xAA64 : (ushort)0x8664;
        var wrong = new List<string>();
        string? loadable = null;
        foreach (var (rid, machine) in new[] { ("win-x64", (ushort)0x8664), ("win-arm64", (ushort)0xAA64) })
        {
            var dll = Path.GetFullPath(Path.Combine(root, "src", "ShellThumbnails", "bin", "Release", "net10.0-windows", rid, "publish", ExplorerThumbnails.HandlerFile));
            if (!File.Exists(dll))
            {
                Console.WriteLine($"        {rid} handler not published here ({dll}): checked by Release.ps1, and here once a Release build has made it");
                continue;
            }

            var pe = PortableBootstrapTests.ReadPe(dll);
            var size = new FileInfo(dll).Length;
            Console.WriteLine($"        {rid} handler: machine 0x{pe.Machine:X4}, DLL {pe.IsDll}, .NET header {pe.HasClrHeader}, exports {string.Join(" ", pe.Exports)}, CET-compatible {pe.CetCompatible}, {size / 1024} KB");
            if (pe.Machine != machine || !pe.IsDll || pe.HasClrHeader || !pe.Exports.Contains("DllGetClassObject") || !pe.Exports.Contains("DllCanUnloadNow")
                || (machine == 0x8664 && !pe.CetCompatible) || size > 4 << 20)
                wrong.Add(rid);
            else if (machine == own)
                loadable = dll;
        }

        expect(wrong.Count == 0, "each published handler is a native DLL of its processor (x64 0x8664, ARM64 0xAA64) with no .NET header, exporting DllGetClassObject and "
            + "DllCanUnloadNow, CET-compatible on x64 (Explorer's surrogate may enforce it), under 4 MB" + (wrong.Count > 0 ? $": not {string.Join(", ", wrong)}" : ""));

        var drawn = true;
        if (loadable is not null)
        {
            // Never freed: a NativeAOT library cannot be unloaded (this process holds it until it ends).
            var library = NativeLibrary.Load(loadable);
            var published = (delegate* unmanaged<Guid*, Guid*, void**, int>)NativeLibrary.GetExport(library, "DllGetClassObject");
            var preview = Preview(published, project, 256);
            Console.WriteLine($"        the published handler of this processor: HRESULT 0x{preview.Result:X8}, {preview.Width} x {preview.Height}, {preview.BitsPerPixel} bits, alpha type {preview.Alpha}; first rows B {preview.Upper.B}, G {preview.Upper.G}, R {preview.Upper.R}");
            drawn = preview.Result == ProjectThumbnailProvider.Ok && preview.Width == 256 && preview.Height == 144 && preview.BitsPerPixel == 32
                    && preview.Alpha == ProjectThumbnailProvider.AlphaRgb && Near(preview.Upper, Blue) && Near(preview.Lower, Orange);
        }

        expect(drawn, "and the one of this processor, loaded as Explorer's surrogate loads it (the DLL, then its export), draws the .hproj at 256 x 144, top-down, as the code compiled in here does");
    }

    // Whether nothing holds the file open any more: Windows' stream of it lets no one else write while it is open.
    private static bool FileIsFree(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ----- COM by hand: a pointer's first field is its table of methods (IUnknown's three first, then the interface's own) -----

    private static void* Slot(void* com, int slot) => (*(void***)com)[slot];

    private static uint Release(void* com) => ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);

    private static int Query(void* com, Guid iid, void** result) => ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(com, 0))(com, &iid, result);

    private static int GetClassObject(delegate* unmanaged<Guid*, Guid*, void**, int> getClassObject, Guid clsid, void** factory)
    {
        var iid = ClassFactoryIid;
        return getClassObject(&clsid, &iid, factory);
    }

    // IClassFactory::CreateInstance(outer, iid, result), slot 3.
    private static int CreateInstance(void* factory, void* outer, Guid iid, void** result) =>
        ((delegate* unmanaged<void*, void*, Guid*, void**, int>)Slot(factory, 3))(factory, outer, &iid, result);

    // IInitializeWithStream::Initialize(stream, mode), slot 3.
    private static int Initialize(void* initialize, void* stream) => ((delegate* unmanaged<void*, void*, uint, int>)Slot(initialize, 3))(initialize, stream, 0);

    // IThumbnailProvider::GetThumbnail(cx, bitmap, alpha), slot 3.
    private static int GetThumbnail(void* thumbnail, uint cx, nint* bitmap, int* alpha) =>
        ((delegate* unmanaged<void*, uint, nint*, int*, int>)Slot(thumbnail, 3))(thumbnail, cx, bitmap, alpha);

    // The file as a stream, opened as Explorer's surrogate opens one for a handler (shlwapi's file stream: read, others may not write).
    private static void* OpenStream(string path)
    {
        void* stream = null;
        int answer;
        fixed (char* name = path)
            answer = SHCreateStreamOnFileEx(name, ReadDenyWrite, 0, 0, null, &stream);
        if (answer != 0 || stream is null)
            throw new IOException($"SHCreateStreamOnFileEx said 0x{answer:X8} for {path}");
        return stream;
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

    // A file stream (shlwapi): the file, its STGM mode, its attributes, whether to create it (no), no template, the IStream.
    [LibraryImport("shlwapi.dll")]
    private static partial int SHCreateStreamOnFileEx(char* file, uint mode, uint attributes, int create, void* template, void** stream);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(nint handle, int size, out DibSection section);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}
