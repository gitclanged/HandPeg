using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.Views;

// ----- The preview and the sub-clip cutter (5.4C: the Filmstrip view, and the List view once the pane is wide) -----
// The file chosen in the list, shown large (MediaSourcePreview) over a compact strip of its frames (MediaFilmstripCutter). Scrubbing shows
// the strip's frame at once (the browser's cached strip), then the frame itself from the thumbnail compositor through a latest-wins
// SourceFrames: a key frame while the strip is dragged or coasting, the exact frame once it has rested ExactAfterMs. Space steps through
// the file at a few stills a second with no sound, and says so (a true playback with sound would need a second swapchain: not this pass).
// I and O mark a sub-clip (seconds of the file, its clips' MediaOffset clock); Insert or Enter puts it on the timeline at the playhead,
// and dragging the marked band carries it there as a sub-clip (SubClipFormat). Made at first need after the first frame (Law 1); holds
// one frame and the strip's frames, let go when the view hides it or the pane is released (Law 2).
public partial class MediaBrowserPane
{
    /// <summary>The drag format of a sub-clip dragged out of the cutter (beside the file itself and <see cref="DragFormat"/>): "in|out", invariant seconds.</summary>
    public const string SubClipFormat = "HandPeg.MediaBrowserSubClip";

    /// <summary>The stepping preview's rate, stills a second at most.</summary>
    private const double SteppingFps = 12;

    /// <summary>A sub-clip as its drag carries it: "12.5|31.25" (invariant seconds, In then Out).</summary>
    public static string FormatSubClip(double inSeconds, double outSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{inSeconds:0.######}|{outSeconds:0.######}");

    /// <summary>A sub-clip read from its drag's text; null for none, or text that is not two finite times with Out after In.</summary>
    public static (double In, double Out)? ParseSubClip(object? data)
    {
        if (data is not string text)
            return null;

        var parts = text.Split('|');
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
               && double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start
            ? (start, end)
            : null;
    }

    /// <summary>A sub-clip was asked to be put on the timeline at the playhead (Insert, Enter): the file, In, Out, and whether it is a sound.</summary>
    public event Action<string, double, double, bool>? AddRangeRequested;

    private MediaSourcePreview? _preview;
    private MediaFilmstripCutter? _cutter;
    private Button? _insertButton, _playButton;
    private SourceFrames? _sourceFrames;
    private MediaBrowserItem? _shown;
    private CancellationTokenSource? _showing;
    private DispatcherTimer? _exactTimer, _stepTimer;
    private readonly Stopwatch _stepClock = new();
    private double _stepFrom;
    private int _stepDirection, _frameAsked, _frameShown;

    // When a still last came back and was shown: while they keep coming, a scrub shows them rather than the strip's coarse frames.
    private readonly Stopwatch _sinceStill = new();

    /// <summary>
    /// Whether a file is the one shown (by its path: Project Media listed again makes new entries for the same files, and what was
    /// being read for the old entry still belongs to the file shown).
    /// </summary>
    private bool IsShown(MediaBrowserItem item) => _shown is { } shown && string.Equals(shown.Path, item.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>The preview's part was shown or hidden by the view: made (after the first frame) and given the file chosen, or let go.</summary>
    private void PreviewShownChanged(bool shown)
    {
        if (!shown)
        {
            // Its frames let go, and the memory they took given back once idle.
            if (_shown is not null)
            {
                ReleasePreview();
                GiveBackMemory();
            }

            return;
        }

        if (_preview is null)
        {
            if (_started)
                Dispatcher.BeginInvoke(EnsurePreview, DispatcherPriority.ContextIdle);
            return;
        }

        ShowChosen();
    }

    /// <summary>Makes the preview, its buttons and the cutter (once), and shows the file chosen.</summary>
    private void EnsurePreview()
    {
        if (_preview is not null || PickerMode)
        {
            if (_preview is not null && PreviewHost.Visibility == Visibility.Visible)
                ShowChosen();
            return;
        }

        if (PreviewHost.Visibility != Visibility.Visible)
            return;

        _preview = new MediaSourcePreview();
        _cutter = new MediaFilmstripCutter { Margin = new Thickness(0, 4, 0, 0) };
        _cutter.Scrubbed += Cutter_Scrubbed;
        _cutter.RangeChanged += (start, end) => _preview?.ShowRange(start, end, _cutter.HasRange);
        _cutter.Shuttle += Shuttle;
        _cutter.InsertRequested += InsertRange;
        _cutter.DragOutRequested += DragRangeOut;

        Button Action(string label, string about, string automation, Action act)
        {
            var button = new Button { Content = label, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), Focusable = false, ToolTip = about };
            System.Windows.Automation.AutomationProperties.SetName(button, automation);
            button.Click += (_, _) => act();
            return button;
        }

        // In, Out, Clear and Step at the left, Insert at the right; in a narrow preview (5.4D: the Media pane at its least in the Filmstrip
        // view leaves it 120 DIPs) the four wrap and Insert goes under them, said shorter if need be (FitPreviewBar), never cut off.
        var bar = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = false };
        var marks = new WrapPanel();
        marks.Children.Add(Action("In", "Mark In at the playhead (I).", "Mark In", () => _cutter?.MarkIn()));
        marks.Children.Add(Action("Out", "Mark Out at the playhead (O).", "Mark Out", () => _cutter?.MarkOut()));
        marks.Children.Add(Action("Clear", "Mark the whole file again.", "Clear In and Out", () => _cutter?.ClearRange()));
        _playButton = Action("Step", "Step through the file at a few stills a second, with no sound (Space; J back, K stop, L forward).", "Step Preview", () => Shuttle(2));
        marks.Children.Add(_playButton);
        _insertButton = Action("Insert at Playhead", "Puts the marked part (the whole file when nothing is marked) on the timeline at its playhead (Enter). Or drag the marked band onto the timeline.",
            "Insert Sub-Clip at Playhead", InsertRange);
        _insertButton.Margin = new Thickness(0);
        DockPanel.SetDock(marks, Dock.Left);
        DockPanel.SetDock(_insertButton, Dock.Right);
        bar.Children.Add(marks);
        bar.Children.Add(_insertButton);
        bar.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                FitPreviewBar(bar, marks);
        };

        var panel = new DockPanel { Margin = new Thickness(0, 0, 4, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        DockPanel.SetDock(_cutter, Dock.Bottom);
        panel.Children.Add(bar);
        panel.Children.Add(_cutter);
        panel.Children.Add(_preview);

        // A click on the picture gives the cutter the keyboard, so I, O and Space work at once.
        _preview.MouseLeftButtonDown += (_, _) => _cutter?.Focus();
        PreviewHost.Content = panel;
        ShowChosen();
    }

    // The Insert button's words, and the shorter ones it takes on a line of its own too narrow for them (FitPreviewBar).
    private const string InsertWords = "Insert at Playhead", InsertWordsShort = "Insert";

    /// <summary>
    /// The preview's buttons laid out for its width (5.4D): on one line while they fit (In, Out, Clear and Step at the left, Insert at the
    /// right); else the four at the top, wrapping, and Insert on a line under them, "Insert" alone where "Insert at Playhead" would not fit.
    /// </summary>
    private void FitPreviewBar(DockPanel bar, Panel marks)
    {
        if (_insertButton is not { } insert)
            return;

        static double Natural(UIElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Width;
        }

        insert.Content = InsertWords;
        var room = bar.ActualWidth;
        var oneLine = marks.Children.Cast<UIElement>().Sum(Natural) + Natural(insert) <= room;
        DockPanel.SetDock(marks, oneLine ? Dock.Left : Dock.Top);
        DockPanel.SetDock(insert, oneLine ? Dock.Right : Dock.Top);
        insert.HorizontalAlignment = oneLine ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        insert.Margin = oneLine ? new Thickness(0) : new Thickness(0, 4, 0, 0);
        if (!oneLine && Natural(insert) > room)
            insert.Content = InsertWordsShort;
    }

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_preview is not null && PreviewHost.Visibility == Visibility.Visible)
            ShowChosen();
    }

    /// <summary>The file chosen in the list (the first chosen file), shown; the same file again keeps its playhead and sub-clip.</summary>
    private void ShowChosen()
    {
        var item = Items.SelectedItem is MediaBrowserItem { IsFile: true } first ? first : Selected().FirstOrDefault(i => i.IsFile);
        if (item is null && Relisting)
            return;
        if (item is not null && _shown is not null && string.Equals(item.Path, _shown.Path, StringComparison.OrdinalIgnoreCase))
        {
            _shown = item;
            return;
        }

        _ = ShowAsync(item);
    }

    /// <summary>Takes up a file: its poster at once, then its details, the strip's frames for the cutter, and its frame at the start.</summary>
    private async Task ShowAsync(MediaBrowserItem? item)
    {
        if (_preview is not { } preview || _cutter is not { } cutter)
            return;

        StopStepping();
        _showing?.Cancel();
        _sourceFrames?.Cancel();
        _exactTimer?.Stop();
        _sinceStill.Reset();
        _shown = item;
        cutter.Load(item?.Seconds ?? 0, item?.FrameRate ?? 0);
        preview.ShowRange(0, 0, false);
        preview.ShowMoment(0, item?.Seconds ?? 0);
        preview.Frame = item?.Poster;
        if (_insertButton is not null)
            _insertButton.IsEnabled = item is { Kind: not MediaBrowserKind.Image };
        if (item is null)
        {
            preview.Message = "Choose a file in the list to see it here.";
            return;
        }

        preview.Message = item.Info is "missing" ? $"{item.Name} is missing: it is not where the project had it."
            : item.Info is "cannot be read" ? $"{item.Name} cannot be read."
            : item.Kind == MediaBrowserKind.Audio ? $"{item.Name}: a sound, with no picture. Mark In and Out on the strip to cut it."
            : "";
        var showing = _showing = new CancellationTokenSource();
        var token = showing.Token;
        try
        {
            var height = PreviewPixelHeight();
            if (item.Kind == MediaBrowserKind.Image)
            {
                var path = item.Path;
                preview.Frame = await Task.Run(() => MediaBrowserFrames.Picture(path, height), token) ?? item.Poster;
                return;
            }

            while (WorkScheduler.IsHeld)
                await Task.Delay(HeldPollMilliseconds, token);
            if (await MediaBrowserFrames.DetailsAsync(item.Path, token, WorkLane.Interactive) is not { } details)
            {
                if (!token.IsCancellationRequested)
                    preview.Message = $"{item.Name} cannot be read.";
                return;
            }

            if (token.IsCancellationRequested || !IsShown(item))
                return;
            item.TakeDetails(details.Seconds, details.Width, details.Height, details.FrameRate, details.Codec, details.AudioStreams);
            if (Math.Abs(cutter.DurationSeconds - details.Seconds) > 0.001)
                cutter.Load(details.Seconds, details.FrameRate);
            else
                cutter.FrameRate = details.FrameRate > 0 ? details.FrameRate : 30;
            preview.ShowMoment(cutter.PositionSeconds, details.Seconds);
            if (item.Kind != MediaBrowserKind.Video || details.Width <= 0 || details.Height <= 0)
                return;

            AskFrame(0, fast: false);

            // The cutter's frames: the browser's strip of the file (kept, or made now in the Interactive lane), cut at the row's height.
            var settings = AppSettings.Current;
            var (frames, frameHeight) = (Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080));
            var strip = item.StripPath ?? await MediaBrowserFrames.StripAsync(item.Path, details.Seconds, details.Width, details.Height, frames, frameHeight, token, WorkLane.Interactive);
            if (strip is null || token.IsCancellationRequested || !IsShown(item))
                return;

            item.StripPath ??= strip;
            var (shape, rowHeight) = ((double)details.Width / details.Height, (int)Math.Ceiling(MediaFilmstripCutter.FrameRowHeight * VisualTreeHelper.GetDpi(this).DpiScaleY));
            var cut = await Task.Run(() => MediaBrowserFrames.Frames(strip, shape, rowHeight), token);
            if (cut is not null && !token.IsCancellationRequested && IsShown(item))
            {
                cutter.Frames = [.. cut];
                if (preview.Frame is null)
                    preview.Frame = StripFrameAt(cutter.PositionSeconds);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Media browser: the preview of {item.Name}: {ex.Message}");
        }
    }

    // The preview's height in real pixels, for the stills asked for: as tall as it is shown, at most 1080 (Law 2: one frame held).
    private int PreviewPixelHeight() =>
        (int)Math.Clamp(Math.Ceiling((_preview?.ActualHeight ?? 270) * VisualTreeHelper.GetDpi(this).DpiScaleY), 90, 1080);

    /// <summary>The strip's frame nearest a moment: the coarse picture shown at once while the still is made.</summary>
    private ImageSource? StripFrameAt(double seconds) =>
        _cutter is { Frames: { Count: > 0 } frames, DurationSeconds: > 0 } cutter
            ? frames[Math.Clamp((int)(seconds / cutter.DurationSeconds * frames.Count), 0, frames.Count - 1)]
            : null;

    private void Cutter_Scrubbed(double seconds, bool settled)
    {
        if (_preview is not { } preview || _shown is not { } item)
            return;

        preview.ShowMoment(seconds, _cutter?.DurationSeconds ?? 0);
        if (item.Kind != MediaBrowserKind.Video)
            return;

        // The strip's frame at once (unless stills are coming back quickly); the still behind it: a key frame while moving, the exact
        // one once it rests.
        if (!settled && StripFrameAt(seconds) is { } coarse && (!_sinceStill.IsRunning || _sinceStill.ElapsedMilliseconds > 300))
            preview.Frame = coarse;
        if (settled)
        {
            _exactTimer?.Stop();
            AskFrame(seconds, fast: false);
            return;
        }

        AskFrame(seconds, fast: true);
        if (_exactTimer is null)
        {
            _exactTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(SourceFrames.ExactAfterMs) };
            _exactTimer.Tick += (_, _) =>
            {
                _exactTimer.Stop();
                if (_cutter is { } cutter)
                    AskFrame(cutter.PositionSeconds, fast: false);
            };
        }

        _exactTimer.Stop();
        _exactTimer.Start();
    }

    /// <summary>
    /// Asks for the shown file's frame at a moment. It is shown when it comes unless a later one was shown first (SourceFrames makes one
    /// at a time and lets the waiting ones be replaced, so while the strip is dragged each still that comes is the newest there is).
    /// </summary>
    private async void AskFrame(double seconds, bool fast)
    {
        if (_shown is not { IsVideo: true } item || _showing is not { } showing)
            return;

        _sourceFrames ??= new SourceFrames();
        var asked = ++_frameAsked;
        var frame = await _sourceFrames.FrameAsync(item.Path, seconds, PreviewPixelHeight(), fast, showing.Token);
        if (frame is null || asked <= _frameShown || !IsShown(item) || _preview is not { } preview)
            return;

        _frameShown = asked;
        preview.Frame = frame;
        _sinceStill.Restart();
    }

    // ----- Stepping through the file (Space; J, K, L) -----

    private void Shuttle(int direction)
    {
        if (direction == 2)
            direction = _stepDirection != 0 ? 0 : 1;
        if (direction == 0 || _cutter is not { DurationSeconds: > 0 } cutter || _shown is not { Kind: not MediaBrowserKind.Image })
        {
            StopStepping();
            return;
        }

        // From the start again when it stood at the end (going forward), or at the start (going back).
        if (direction > 0 && cutter.PositionSeconds >= cutter.DurationSeconds - 0.001)
            cutter.PositionSeconds = cutter.HasRange ? cutter.InSeconds : 0;
        else if (direction < 0 && cutter.PositionSeconds <= 0.001)
            cutter.PositionSeconds = cutter.DurationSeconds;

        _stepDirection = direction;
        _stepFrom = cutter.PositionSeconds;
        _stepClock.Restart();
        if (_stepTimer is null)
        {
            _stepTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / SteppingFps) };
            _stepTimer.Tick += Step_Tick;
        }

        _stepTimer.Start();
        if (_preview is not null)
            _preview.Stepping = true;
        if (_playButton is not null)
            _playButton.Content = "Stop";
    }

    private void Step_Tick(object? sender, EventArgs e)
    {
        if (_cutter is not { } cutter || !IsVisible)
        {
            StopStepping();
            return;
        }

        var at = Math.Clamp(_stepFrom + _stepDirection * _stepClock.Elapsed.TotalSeconds, 0, cutter.DurationSeconds);
        cutter.PositionSeconds = at;
        _preview?.ShowMoment(at, cutter.DurationSeconds);
        if (_shown is { IsVideo: true })
        {
            if (StripFrameAt(at) is { } coarse && _preview is { } preview && preview.Frame is null)
                preview.Frame = coarse;
            AskFrame(at, fast: false);
        }

        if (at <= 0 || at >= cutter.DurationSeconds)
            StopStepping();
    }

    private void StopStepping()
    {
        if (_stepDirection == 0)
            return;

        _stepDirection = 0;
        _stepTimer?.Stop();
        _stepClock.Reset();
        if (_preview is not null)
            _preview.Stepping = false;
        if (_playButton is not null)
            _playButton.Content = "Step";
    }

    // ----- The sub-clip onto the timeline -----

    /// <summary>The marked part (the whole file when nothing is marked: NaN for both ends) asked to be put on the timeline at its playhead.</summary>
    private void InsertRange()
    {
        if (_shown is not { IsFile: true, Kind: not MediaBrowserKind.Image } item || _cutter is not { DurationSeconds: > 0 } cutter)
            return;
        if (HasVideo?.Invoke() != true)
        {
            StatusText.Text = "Open a video or a project first: the sub-clip is put on its timeline at the playhead.";
            return;
        }

        // Nothing marked: the whole file, as a drop places it (NaN: no range), never stretched to the container's length (5.4A's
        // no-trailing-black-frame rule; the cutter's whole span is the container's).
        var (start, end) = cutter.HasRange ? (cutter.InSeconds, cutter.OutSeconds) : (double.NaN, double.NaN);
        AddRangeRequested?.Invoke(item.Path, start, end, item.Kind == MediaBrowserKind.Audio);
    }

    /// <summary>
    /// The marked band dragged out of the cutter: the file as Explorer drags it (so whatever takes files takes it), marked as the browser's,
    /// with the sub-clip (<see cref="SubClipFormat"/>), which the timeline places as the part marked. Background work is held meanwhile.
    /// </summary>
    private void DragRangeOut()
    {
        if (_shown is not { IsFile: true, Kind: not MediaBrowserKind.Image } item || _cutter is not { DurationSeconds: > 0 } cutter)
            return;

        StopStepping();
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        data.SetData(DragFormat, true);
        if (cutter.HasRange)
            data.SetData(SubClipFormat, FormatSubClip(cutter.InSeconds, cutter.OutSeconds));
        // (5.4.4: an internal drag makes nothing ready ahead of its drop: the drop opens what it needs, MainWindow.IsInternalDrag.)
        StopStripOnDemand();
        using (WorkScheduler.HoldBackground())
            DragDrop.DoDragDrop(cutter, data, DragDropEffects.Copy);
    }

    /// <summary>Lets go of the preview's frame, the cutter's frames and any still on its way (the view hid it, or the pane is released).</summary>
    private void ReleasePreview()
    {
        StopStepping();
        _exactTimer?.Stop();
        _showing?.Cancel();
        _showing = null;
        _sourceFrames?.Cancel();
        _cutter?.StopCoasting();
        if (_cutter is not null)
            _cutter.Frames = null;
        if (_preview is not null)
            _preview.Frame = null;
        _shown = null;
    }
}
