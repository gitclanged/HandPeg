using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Native;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary>
/// HandPeg.exe --dpi-matrix (Pass 5.6.5, dispatched first by Program.Main on its STA thread): builds MainWindow, the player overlay
/// (VideoSurface.OverlayWindow), PlayerFullscreenWindow, MaskDrawWindow and FirstRunWindow at simulated per-window DPI scales (100-250 %)
/// and aspect ratios (16:9, 16:10, 3:2, 21:9), visible on the primary monitor, and checks their geometry with GetWindowRect and UIA
/// ElementFromPoint. Exit code: <see cref="Passed"/>, <see cref="Failed"/> (a geometry check failed) or <see cref="NotRun"/> (could not run).
/// <para>DPI is simulated per window, never by changing the display: a synthetic WM_DPICHANGED (wParam the DPI, lParam the rectangle for
/// the aspect ratio) makes WPF lay the window out at that DPI and move it there, with HandPeg's UI scale following that DPI (in memory
/// only). Everything the windows keep goes into a private data folder deleted at the end (under HANDPEG_DATA_DIR when set, so a portable
/// run leaves nothing in %LOCALAPPDATA%).</para>
/// <para>Options: --scales 1,1.5,2.5 --aspects 16:9,21:9 --quick (100/175/250 %) --out report.json. Lines go to standard output.</para>
/// </summary>
internal static partial class DpiMatrixRunner
{
    /// <summary>The command-line switch, as the first argument.</summary>
    public const string Argument = "--dpi-matrix";

    /// <summary>Process exit codes.</summary>
    public const int Passed = 0, Failed = 1, NotRun = 2;

    /// <summary>Simulated DPI scales (100-250 % in 25 % steps).</summary>
    public static readonly double[] Scales = [1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5];

    /// <summary>Quick run's scales.</summary>
    public static readonly double[] QuickScales = [1.0, 1.75, 2.5];

    /// <summary>Window aspect ratios.</summary>
    public static readonly (string Name, int Width, int Height)[] Aspects = [("16:9", 16, 9), ("16:10", 16, 10), ("3:2", 3, 2), ("21:9", 21, 9)];

    /// <summary>Rectangles compared with GetWindowRect may differ by this many pixels per edge.</summary>
    public const int TolerancePixels = 1;

    private const int WmDpiChanged = 0x02E0;

    // ----- Pure geometry (checked by goldens) -----

    /// <summary>DPI for a scale (96 at 100 %).</summary>
    public static int DpiOf(double scale) => (int)Math.Round(96 * scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The largest rectangle of <paramref name="aspectWidth"/>:<paramref name="aspectHeight"/> inside 90 % of <paramref name="work"/>,
    /// centred on it (physical pixels).
    /// </summary>
    public static Int32Rect FitAspect(Int32Rect work, int aspectWidth, int aspectHeight)
    {
        var (room, roomHeight) = (work.Width * 0.9, work.Height * 0.9);
        var width = room;
        var height = width * aspectHeight / aspectWidth;
        if (height > roomHeight)
            (height, width) = (roomHeight, roomHeight * aspectWidth / aspectHeight);
        var (w, h) = ((int)Math.Floor(width), (int)Math.Floor(height));
        return new Int32Rect(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
    }

    /// <summary>True when every edge of <paramref name="a"/> lies within <paramref name="tolerance"/> pixels of <paramref name="b"/>'s.</summary>
    public static bool Matches(Int32Rect? a, Int32Rect? b, int tolerance = TolerancePixels) =>
        a is { } x && b is { } y && Math.Abs(x.X - y.X) <= tolerance && Math.Abs(x.Y - y.Y) <= tolerance
        && Math.Abs(x.X + x.Width - (y.X + y.Width)) <= tolerance && Math.Abs(x.Y + x.Height - (y.Y + y.Height)) <= tolerance;

    /// <summary>True when <paramref name="inner"/> lies wholly inside <paramref name="outer"/>.</summary>
    public static bool Inside(Int32Rect inner, Int32Rect outer) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>Parses "--scales 1,1.5" (percent or factor) into factors held to 1-2.5; null when absent or empty.</summary>
    public static double[]? ParseScales(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var scales = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => double.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (v > 10 ? v / 100 : v) : double.NaN)
            .Where(double.IsFinite).Select(v => Math.Clamp(v, 1, 2.5)).Distinct().ToArray();
        return scales.Length == 0 ? null : scales;
    }

    /// <summary>Parses "--aspects 16:9,21:9" against <see cref="Aspects"/>; null when absent or none known.</summary>
    public static (string Name, int Width, int Height)[]? ParseAspects(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var names = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var picked = Aspects.Where(a => names.Contains(a.Name, StringComparer.Ordinal)).ToArray();
        return picked.Length == 0 ? null : picked;
    }

    // ----- Run -----

    /// <summary>One configuration's verdicts.</summary>
    internal sealed record Cell(string Window, double Scale, string Aspect, bool Passed, string Detail);

    /// <summary>Runs the matrix and returns its exit code.</summary>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? Option(string name) => Array.IndexOf(args, name) is var at && at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        // A private data folder for whatever the windows keep (settings, session, logs), deleted at the end.
        var parent = Environment.GetEnvironmentVariable(AppPaths.DataFolderVariable) is { Length: > 0 } given ? given : Path.GetTempPath();
        var data = Path.Combine(parent, "dpi-matrix-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(data, "Settings"));
        // First run done and FFmpeg named (a set path counts as present unchecked), so App.OnStartup opens no first-run window.
        var seed = new Dictionary<string, object>
        {
            ["FirstRunComplete"] = true, ["QuickStartTourDone"] = true, ["FollowWindowsDpiScaling"] = true, ["UiScalePercent"] = 100,
            ["FfmpegPath"] = Environment.GetEnvironmentVariable("HANDPEG_FFMPEG") is { Length: > 0 } ffmpeg ? ffmpeg : "ffmpeg.exe",
        };
        File.WriteAllText(Path.Combine(data, "Settings", "appsettings.json"), JsonSerializer.Serialize(seed));
        Environment.SetEnvironmentVariable(AppPaths.DataFolderVariable, data);

        var scales = ParseScales(Option("--scales")) ?? (args.Contains("--quick") ? QuickScales : Scales);
        var aspects = ParseAspects(Option("--aspects")) ?? Aspects;
        var cells = new List<Cell>();
        var minutes = double.TryParse(Option("--timeout-minutes"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 15;
        StartWatchdog(TimeSpan.FromMinutes(minutes), output, data);
        int exit;
        try
        {
            exit = Matrix(scales, aspects, cells, output);
        }
        catch (Exception ex)
        {
            AppLog.Write("dpi-matrix: could not run", ex);
            output.WriteLine("dpi-matrix: could not run: " + ex.GetType().Name + ": " + ex.Message);
            exit = NotRun;
        }

        if (Option("--out") is { } outPath)
        {
            var report = new
            {
                Verdict = exit switch { Passed => "pass", Failed => "fail", _ => "not run" },
                Cells = cells,
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        var failed = cells.Count(c => !c.Passed);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"dpi-matrix: {(exit == Passed ? "PASS" : exit == Failed ? "FAIL" : "NOT RUN")}: {cells.Count - failed} of {cells.Count} checks passed ({scales.Length} scales x {aspects.Length} aspect ratios)"));
        Volatile.Write(ref _finished, true);
        DeleteData(data);
        return exit;
    }

    // What the matrix is doing (named by the watchdog when it hangs), and whether it ended.
    private static string _stage = "starting";
    private static bool _finished;

    /// <summary>
    /// Ends the process (exit <see cref="NotRun"/>) when the matrix runs longer than <paramref name="limit"/>, naming the stage it stopped
    /// in, after deleting what it can of its data folder.
    /// </summary>
    private static void StartWatchdog(TimeSpan limit, StreamWriter output, string data)
    {
        var watchdog = new Thread(() =>
        {
            var until = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < until)
            {
                if (Volatile.Read(ref _finished))
                    return;
                Thread.Sleep(250);
            }

            output.WriteLine($"dpi-matrix: NOT RUN: no progress past '{Volatile.Read(ref _stage)}' within {limit.TotalMinutes:0} min");
            CrashReporter.MarkCleanExit();
            DeleteData(data);
            Environment.Exit(NotRun);
        }) { IsBackground = true, Name = "dpi-matrix watchdog" };
        watchdog.Start();
    }

    /// <summary>
    /// Deletes the private data folder: the binding-error log this process holds is let go first, and workers that held a file get a few
    /// seconds to exit.
    /// </summary>
    private static void DeleteData(string data)
    {
        foreach (var listener in PresentationTraceSources.DataBindingSource.Listeners.OfType<System.Diagnostics.TraceListener>().ToList())
        {
            listener.Flush();
            listener.Close();
        }

        PresentationTraceSources.DataBindingSource.Listeners.Clear();
        for (var attempt = 0; attempt < 40 && Directory.Exists(data); attempt++)
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static int Matrix(double[] scales, (string Name, int Width, int Height)[] aspects, List<Cell> cells, StreamWriter output)
    {
        // The App as a normal start makes it: its resources, then (on the dispatcher's first turns) its OnStartup (work lanes, UI scale,
        // theme) and its StartupUri main window, which the matrix takes over.
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (app.MainWindow is not MainWindow { IsLoaded: true } && waited.Elapsed.TotalSeconds < 30)
            Pump(50);
        if (app.MainWindow is not MainWindow main)
            return NotRun;
        var settings = AppSettings.Current;
        (settings.FirstRunComplete, settings.QuickStartTourDone, settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (true, true, true, 100);

        if (!User32.TryMonitorInfo(User32.MonitorFromPoint(default, User32.MonitorDefaultToPrimary), out var info))
            return NotRun;
        var monitor = Rect32(info.Monitor);
        var work = Rect32(info.Work);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dpi-matrix: primary monitor {monitor.Width}x{monitor.Height} (work area {work.Width}x{work.Height})"));

        // Another HandPeg's window above the matrix's would take its hits (UIA names the covering process): said up front.
        var others = Process.GetProcessesByName("HandPeg").Count(p => p.Id != Environment.ProcessId && p.MainWindowHandle != 0);
        if (others > 0)
            output.WriteLine($"dpi-matrix: note: {others} other HandPeg window(s) open; a hit on another process's window fails its button");

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dpi-matrix: main window loaded in {waited.ElapsedMilliseconds} ms"));
        void Add(Cell cell)
        {
            cells.Add(cell);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {(cell.Passed ? "ok  " : "FAIL")} {cell.Window,-12} {cell.Scale * 100,4:0} % {cell.Aspect,-5} {cell.Detail}"));
        }

        try
        {
            if (main.WindowState != WindowState.Normal)
                main.WindowState = WindowState.Normal;
            Pump(600);
            foreach (var scale in scales)
            {
                foreach (var aspect in aspects)
                {
                    var target = FitAspect(work, aspect.Width, aspect.Height);
                    CheckMain(main, scale, aspect.Name, target, monitor, Add);
                    CheckFullscreen(main, scale, aspect.Name, target, monitor, Add);
                    CheckMask(main, scale, aspect.Name, target, monitor, Add);
                    CheckFirstRun(main, scale, aspect.Name, target, monitor, Add);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _stage, "closing");
            main.Close();
            Pump(300);

            // No crash report for this exit: the sentinel watching since the main window's first frame is told it is a clean one.
            CrashReporter.MarkCleanExit();
            app.Dispatcher.InvokeShutdown();
        }

        return cells.Count == 0 ? NotRun : cells.All(c => c.Passed) ? Passed : Failed;
    }

    // ----- Windows -----

    private static void CheckMain(MainWindow main, double scale, string aspect, Int32Rect target, Int32Rect monitor, Action<Cell> add)
    {
        Stage("main", scale, aspect);
        ClosePopups();
        var handle = new WindowInteropHelper(main).Handle;

        // A start may restore the window maximized (after Frame 1): the matrix sizes a normal window.
        if (main.WindowState != WindowState.Normal)
        {
            main.WindowState = WindowState.Normal;
            Pump(150);
        }

        Simulate(handle, scale, target);

        // The player shown as with media loaded (its native video window and the overlay over it).
        main.VideoView.Visibility = Visibility.Visible;
        Pump(250);
        main.VideoView.PlaceOverlay();
        Settle(main);

        var window = WindowRect(handle);
        var perDip = PixelsPerDip(main.TransportRow);
        add(new Cell("main", scale, aspect, window is { } w && w.Width > 0 && Math.Abs(perDip - scale) <= 0.03 * scale,
            string.Create(CultureInfo.InvariantCulture, $"window {Describe(window)}, content at {perDip:0.00} px/DIP (simulated {scale:0.00})")));

        var surface = main.VideoView.SurfaceRectangle();
        var overlay = main.VideoView.OverlayWindow is { } shown ? WindowRect(new WindowInteropHelper(shown).Handle) : null;
        add(new Cell("overlay", scale, aspect, surface is not null && Matches(overlay, surface),
            $"overlay {Describe(overlay)} over video window {Describe(surface)}"));

        var (buttons, missed) = HitTestSettled(main, () => Buttons(main.TransportRow, monitor).Concat(Buttons(main.EffectsView, monitor)));
        add(new Cell("buttons", scale, aspect, missed.Count == 0,
            $"{buttons.Count - missed.Count} of {buttons.Count} transport/inspector buttons hit by UIA ElementFromPoint{(missed.Count == 0 ? "" : ": missed " + string.Join(", ", missed.Take(6)))}"));
    }

    private static void CheckFullscreen(MainWindow main, double scale, string aspect, Int32Rect target, Int32Rect monitor, Action<Cell> add)
    {
        Stage("fullscreen", scale, aspect);
        ClosePopups();
        var fullscreen = new PlayerFullscreenWindow(PlayerFullscreenWindow.MonitorAround(new Rect(monitor.X + 10, monitor.Y + 10, 10, 10)))
        {
            Owner = main, ShowActivated = false,
        };
        try
        {
            fullscreen.Show();
            Pump(150);
            var handle = new WindowInteropHelper(fullscreen).Handle;

            // The DPI change proposes the aspect's rectangle; the window must snap back to its whole monitor.
            Simulate(handle, scale, target);
            Pump(120);
            var rect = WindowRect(handle);
            add(new Cell("fullscreen", scale, aspect, Matches(rect, monitor, 0), $"window {Describe(rect)}, monitor {Describe(monitor)}"));
        }
        finally
        {
            fullscreen.LetClose();
            fullscreen.Close();
        }
    }

    private static void CheckMask(MainWindow main, double scale, string aspect, Int32Rect target, Int32Rect monitor, Action<Cell> add)
    {
        Stage("mask", scale, aspect);
        ClosePopups();
        if (main.DataContext is not MainViewModel viewModel)
            return;
        var mask = new MaskDrawWindow(viewModel, null) { Owner = main, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            mask.Show();
            Pump(200);
            var handle = new WindowInteropHelper(mask).Handle;
            Simulate(handle, scale, target);
            Settle(mask);
            var rect = WindowRect(handle);

            // A minimum size larger than the aspect's rectangle at this DPI holds the window at it (grown right and down).
            var (minWidth, minHeight) = ((int)Math.Floor(mask.MinWidth * scale) - TolerancePixels, (int)Math.Floor(mask.MinHeight * scale) - TolerancePixels);
            var held = target.Width < minWidth || target.Height < minHeight;
            var placed = held
                ? rect is { } r && Math.Abs(r.X - target.X) <= TolerancePixels && Math.Abs(r.Y - target.Y) <= TolerancePixels && r.Width >= Math.Min(target.Width, minWidth) && r.Height >= Math.Min(target.Height, minHeight)
                : Matches(rect, target);
            add(new Cell("mask", scale, aspect, placed, $"window {Describe(rect)} for {Describe(target)}{(held ? " (held at its minimum size)" : "")}"));

            var (buttons, missed) = HitTestSettled(mask, () => Buttons(mask, monitor));
            add(new Cell("mask-buttons", scale, aspect, missed.Count == 0,
                $"{buttons.Count - missed.Count} of {buttons.Count} Cutout Studio buttons hit{(missed.Count == 0 ? "" : ": missed " + string.Join(", ", missed.Take(6)))}"));
        }
        finally
        {
            mask.Close();
            Pump(80);
        }
    }

    private static void CheckFirstRun(Window owner, double scale, string aspect, Int32Rect target, Int32Rect monitor, Action<Cell> add)
    {
        Stage("first-run", scale, aspect);
        ClosePopups();
        // live: false starts no download, probe or profile.
        // Owned by the main window (as Settings opens it), so a late activation of the main window never covers it.
        var first = new FirstRunWindow(live: false) { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        try
        {
            first.Show();
            Pump(200);
            var handle = new WindowInteropHelper(first).Handle;
            Simulate(handle, scale, target);
            Settle(first);
            var rect = WindowRect(handle);
            var again = (Pump(150), WindowRect(handle)).Item2;
            add(new Cell("first-run", scale, aspect, rect is { } r && r.Width > 0 && Matches(rect, again, 0),
                $"window {Describe(rect)}, steady after layout"));

            var (buttons, missed) = HitTestSettled(first, () => Buttons(first, monitor));
            add(new Cell("first-run-buttons", scale, aspect, missed.Count == 0,
                $"{buttons.Count - missed.Count} of {buttons.Count} first-run buttons hit{(missed.Count == 0 ? "" : ": missed " + string.Join(", ", missed.Take(6)))}"));
        }
        finally
        {
            first.Close();
            Pump(80);
        }
    }

    private static void Stage(string window, double scale, string aspect) =>
        Volatile.Write(ref _stage, string.Create(CultureInfo.InvariantCulture, $"{window} at {scale * 100:0} % {aspect}"));

    /// <summary>
    /// Closes every open popup of this process (a hint card or tour step offered after Frame 1 sits above every window, taking the hits
    /// meant for the window under test).
    /// </summary>
    private static void ClosePopups()
    {
        foreach (var source in PresentationSource.CurrentSources.OfType<PresentationSource>().ToList())
        {
            if (source.RootVisual is FrameworkElement { Parent: System.Windows.Controls.Primitives.Popup { IsOpen: true } popup })
                popup.IsOpen = false;
        }
    }

    // ----- DPI simulation -----

    /// <summary>Sends WM_DPICHANGED for <paramref name="scale"/> with <paramref name="rect"/> as the suggested window, and lays out.</summary>
    private static void Simulate(nint handle, double scale, Int32Rect rect)
    {
        if (handle == 0)
            return;
        var dpi = DpiOf(scale);
        var suggested = new NativeRect { Left = rect.X, Top = rect.Y, Right = rect.X + rect.Width, Bottom = rect.Y + rect.Height };
        _ = SendMessageW(handle, WmDpiChanged, (nint)((dpi << 16) | dpi), ref suggested);

        // WPF moves the window only when the DPI differs from the one it has; the same DPI again (the monitor's own) moves it here, as a
        // DPI change would (through the window's own WM_WINDOWPOSCHANGING, so a window that keeps its place still can).
        _ = User32.SetWindowPos(handle, 0, rect.X, rect.Y, rect.Width, rect.Height, User32.SwpNoZOrder | User32.SwpNoActivate);
        Pump(250);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessageW(nint window, int message, nint wParam, ref NativeRect lParam);

    /// <summary>
    /// Pumps until <paramref name="window"/> stops moving and laying out (two quiet turns in a row), at most 3 s: a window that fits itself
    /// to the work area or re-fits a row after Frame 1 is measured once it is done.
    /// </summary>
    private static void Settle(Window window)
    {
        var layouts = 0;
        void Count(object? sender, EventArgs e) => layouts++;
        window.LayoutUpdated += Count;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var (quiet, last, seen) = (0, WindowRect(handle), -1);
            var until = DateTime.UtcNow.AddSeconds(3);
            while (quiet < 2 && DateTime.UtcNow < until)
            {
                Pump(100);
                var now = WindowRect(handle);
                quiet = now == last && layouts == seen ? quiet + 1 : 0;
                (last, seen) = (now, layouts);
            }
        }
        finally
        {
            window.LayoutUpdated -= Count;
        }
    }

    /// <summary>Hits the buttons <paramref name="collect"/> finds; a miss is measured again once the window settles, and hit once more.</summary>
    private static (List<Target> Buttons, List<string> Missed) HitTestSettled(Window window, Func<IEnumerable<Target>> collect)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var buttons = collect().ToList();
        var missed = HitTest(buttons, handle);
        if (missed.Count == 0)
            return (buttons, missed);
        Settle(window);
        buttons = collect().ToList();
        return (buttons, HitTest(buttons, handle));
    }

    // ----- Hit testing -----

    /// <summary>A button to hit: its name (or type and caption), its screen rectangle and centre (physical pixels).</summary>
    private sealed record Target(string Name, string AutomationId, Int32Rect Screen, Point Centre);

    /// <summary>
    /// Visible, enabled-or-not buttons under <paramref name="root"/> laid out wholly on <paramref name="monitor"/> and not clipped away by
    /// an ancestor (scrolled out of view or cut by a pane edge).
    /// </summary>
    private static IEnumerable<Target> Buttons(DependencyObject root, Int32Rect monitor)
    {
        foreach (var button in Descendants(root).OfType<ButtonBase>())
        {
            if (!button.IsVisible || button.ActualWidth < 4 || button.ActualHeight < 4 || PresentationSource.FromVisual(button) is null)
                continue;
            var screen = Physical(button);
            if (!Inside(screen, monitor) || !Unclipped(button, screen))
                continue;
            var name = button.Name is { Length: > 0 } n ? n : button.GetType().Name + (button.Content is string text ? " '" + text + "'" : "");
            yield return new Target(name, button.Name ?? "", screen, new Point(screen.X + screen.Width / 2.0, screen.Y + screen.Height / 2.0));
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node is not Visual and not System.Windows.Media.Media3D.Visual3D)
                continue;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    // The button and every ancestor up to the window hold its whole rectangle: no layout slot of no size (a row's overflow parks a control
    // there), no layout clip cutting it (a pane edge), no ancestor that does not contain it (a scroll viewport).
    private static bool Unclipped(FrameworkElement element, Int32Rect screen)
    {
        var inner = Inflate(screen, -1);
        for (DependencyObject? node = element; node is FrameworkElement current; node = VisualTreeHelper.GetParent(current))
        {
            if (current is Window)
                return true;
            if (current.ActualWidth <= 0 || current.ActualHeight <= 0)
                return false;
            var slot = LayoutInformation.GetLayoutSlot(current);
            if (slot.Width < 1 || slot.Height < 1)
                return false;
            if (LayoutInformation.GetLayoutClip(current) is { Bounds: var clip } && !clip.IsEmpty
                && !Inside(inner, Inflate(Physical(current, clip), 1)))
                return false;
            if (!ReferenceEquals(current, element) && !Inside(inner, Inflate(Physical(current), 1)))
                return false;
        }

        return true;
    }

    private static Int32Rect Inflate(Int32Rect r, int by) => new(r.X - by, r.Y - by, Math.Max(0, r.Width + 2 * by), Math.Max(0, r.Height + 2 * by));

    /// <summary>Whether <paramref name="element"/> is inside <paramref name="window"/>: the nearest native window at or above it is that HWND.</summary>
    private static bool InWindow(TreeWalker walker, AutomationElement element, IntPtr window)
    {
        for (AutomationElement? node = element; node is not null; node = walker.GetParent(node))
        {
            if (node.Current.NativeWindowHandle is not 0 and var handle)
                return new IntPtr(handle) == window;
        }

        return false;
    }

    /// <summary>
    /// UIA ElementFromPoint at each button's centre, from an MTA thread while this thread pumps (WPF answers UIA on its own thread). A hit
    /// is the button itself or a part of it, in this process and inside <paramref name="window"/>'s HWND: an element at most four levels up
    /// with the button's rectangle and (when the button has one) its AutomationId. Returns the names of buttons missed.
    /// </summary>
    private static List<string> HitTest(List<Target> targets, IntPtr window)
    {
        if (targets.Count == 0)
            return [];
        var missed = new List<string>();
        var done = false;
        var process = Environment.ProcessId;
        var worker = new Thread(() =>
        {
            try
            {
                var walker = TreeWalker.RawViewWalker;
                foreach (var target in targets)
                {
                    var hit = false;
                    var found = "";
                    try
                    {
                        var element = AutomationElement.FromPoint(target.Centre);
                        if (element is not null)
                            found = string.Create(CultureInfo.InvariantCulture, $" (hit {element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal)} '{element.Current.AutomationId}{element.Current.Name}' {element.Current.ClassName} pid {element.Current.ProcessId} {element.Current.BoundingRectangle}, wanted {Describe(target.Screen)})");
                        // Only this process's element inside the window under test counts: another window at the same place does not.
                        if (element is not null && (element.Current.ProcessId != process || !InWindow(walker, element, window)))
                            element = null;
                        for (var level = 0; element is not null && level < 5 && !hit; level++, element = walker.GetParent(element))
                        {
                            var current = element.Current;
                            var r = current.BoundingRectangle;
                            hit = (target.AutomationId.Length == 0 || current.AutomationId == target.AutomationId)
                                  && !r.IsEmpty && Matches(new Int32Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height)), target.Screen, 2);
                        }
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
                    {
                    }

                    if (!hit)
                    {
                        lock (missed)
                            missed.Add(target.Name + found);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref done, true);
            }
        }) { IsBackground = true, Name = "dpi-matrix UIA" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!Volatile.Read(ref done) && DateTime.UtcNow < until)
            Pump(20);
        lock (missed)
            return Volatile.Read(ref done) ? [.. missed] : [.. missed, "(UIA timed out)"];
    }

    // ----- Helpers -----

    private static Int32Rect Physical(FrameworkElement element) => Physical(element, new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static Int32Rect Physical(FrameworkElement element, Rect local)
    {
        var (a, b) = (element.PointToScreen(local.TopLeft), element.PointToScreen(local.BottomRight));
        var (left, top) = ((int)Math.Round(Math.Min(a.X, b.X)), (int)Math.Round(Math.Min(a.Y, b.Y)));
        return new Int32Rect(left, top, (int)Math.Round(Math.Abs(b.X - a.X)), (int)Math.Round(Math.Abs(b.Y - a.Y)));
    }

    private static double PixelsPerDip(FrameworkElement element) =>
        element.ActualWidth > 0 && PresentationSource.FromVisual(element) is not null
            ? Math.Abs(element.PointToScreen(new Point(element.ActualWidth, 0)).X - element.PointToScreen(new Point(0, 0)).X) / element.ActualWidth
            : double.NaN;

    private static Int32Rect? WindowRect(nint handle) =>
        handle != 0 && User32.GetWindowRect(handle, out var r) ? Rect32(r) : null;

    private static Int32Rect Rect32(NativeRect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    private static string Describe(Int32Rect? r) =>
        r is { } x ? string.Create(CultureInfo.InvariantCulture, $"{x.X},{x.Y} {x.Width}x{x.Height}") : "(none)";

    /// <summary>
    /// Runs the dispatcher for <paramref name="ms"/> milliseconds (layout, renders, placements queued by moves). A timer ends the frame, so
    /// a window that keeps the dispatcher busy (animations, renders) cannot hold the run.
    /// </summary>
    private static bool Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(Math.Max(ms, 1)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        return true;
    }
}
