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

// ----- Source preview and sub-clip cutter (Filmstrip view, and List view when the pane is wide) -----
// The selected file shown large (MediaSourcePreview) above a compact frame strip (MediaFilmstripCutter). Scrubbing shows the cached strip
// frame immediately, then a frame from the thumbnail compositor via latest-wins SourceFrames: a keyframe while dragging or coasting, the
// exact frame after ExactAfterMs at rest. Space steps through the file at a few silent stills per second and labels it as such (true
// playback with audio would need a second swapchain). I and O mark a sub-clip in file seconds (the clip MediaOffset clock); Insert or
// Enter places it at the playhead, and dragging the marked band carries it as a sub-clip (SubClipFormat). Created lazily after Frame 1
// (Law 1); holds one frame plus the strip frames, released when hidden or when the pane is released (Law 2).
public partial class MediaBrowserPane
{
    /// <summary>Drag format for a cutter sub-clip (alongside FileDrop and <see cref="DragFormat"/>): "in|out" in invariant seconds.</summary>
    public const string SubClipFormat = "HandPeg.MediaBrowserSubClip";

    /// <summary>Maximum stepping-preview rate in stills per second.</summary>
    private const double SteppingFps = 12;

    /// <summary>Formats a sub-clip drag payload, e.g. "12.5|31.25" (invariant seconds, In then Out).</summary>
    public static string FormatSubClip(double inSeconds, double outSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{inSeconds:0.######}|{outSeconds:0.######}");

    /// <summary>Parses a sub-clip drag payload; null unless it holds two finite, non-negative times with Out after In.</summary>
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

    /// <summary>Insert/Enter requested placing a sub-clip at the playhead: path, In, Out, and whether it is audio.</summary>
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

    // Time since the last decoded still was shown; while stills arrive promptly, scrubs skip the coarse strip frames.
    private readonly Stopwatch _sinceStill = new();

    /// <summary>
    /// True when the item is the shown file, compared by path (re-listing Project Media creates new entries for the same files, and
    /// in-flight reads for the old entry still apply).
    /// </summary>
    private bool IsShown(MediaBrowserItem item) => _shown is { } shown && string.Equals(shown.Path, item.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>View toggled the preview area: create it (after Frame 1) and show the selection, or release it.</summary>
    private void PreviewShownChanged(bool shown)
    {
        if (!shown)
        {
            // Release the frames and return their memory once idle.
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

    /// <summary>Creates the preview, its buttons and the cutter once, then shows the selected file.</summary>
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

        // In, Out, Clear, Step left; Insert right. In narrow previews (down to 120 DIPs at minimum Filmstrip width) the four wrap and
        // Insert moves below, shortened if needed (FitPreviewBar), never clipped.
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

        // Clicking the picture focuses the cutter so I, O and Space work immediately.
        _preview.MouseLeftButtonDown += (_, _) => _cutter?.Focus();
        PreviewHost.Content = panel;
        ShowChosen();
    }

    // Insert button labels: full, and short for a too-narrow own line (FitPreviewBar).
    private const string InsertWords = "Insert at Playhead", InsertWordsShort = "Insert";

    /// <summary>
    /// Lays out the preview buttons for the bar width: one line when they fit (marks left, Insert right); otherwise marks wrap on top and
    /// Insert sits below, labelled "Insert" when "Insert at Playhead" does not fit.
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

    /// <summary>Shows the first selected file; reselecting the same file keeps its playhead and sub-clip.</summary>
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

    /// <summary>Loads a file: poster immediately, then details, the frame at 0 and the cutter's strip frames.</summary>
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

            // Cutter frames: the browser's cached strip (or built now on the Interactive lane), sliced at the row height.
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
            AppLog.Write($"Media browser: the preview of {item.Name}", ex);
        }
    }

    // Requested still height in device pixels: the displayed height, clamped 90..1080 (Law 2: one frame held).
    private int PreviewPixelHeight() =>
        (int)Math.Clamp(Math.Ceiling((_preview?.ActualHeight ?? 270) * VisualTreeHelper.GetDpi(this).DpiScaleY), 90, 1080);

    /// <summary>Nearest strip frame to a time: the coarse image shown while the decoded still is pending.</summary>
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

        // Show the strip frame immediately (unless stills are arriving within 300 ms), then request a keyframe while moving and the
        // exact frame once settled.
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
    /// Requests the shown file's frame at a time; displayed on arrival unless a newer request already displayed. SourceFrames decodes
    /// one at a time and replaces queued requests, so each still arriving mid-drag is the latest.
    /// </summary>
    private void AskFrame(double seconds, bool fast) => Faults.Forget(AskFrameAsync(seconds, fast), nameof(AskFrame));

    private async Task AskFrameAsync(double seconds, bool fast)
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

    // ----- Stepping preview (Space; J, K, L) -----

    private void Shuttle(int direction)
    {
        if (direction == 2)
            direction = _stepDirection != 0 ? 0 : 1;
        if (direction == 0 || _cutter is not { DurationSeconds: > 0 } cutter || _shown is not { Kind: not MediaBrowserKind.Image })
        {
            StopStepping();
            return;
        }

        // Wrap: forward from the end restarts at In (or 0); backward from the start restarts at the end.
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

    // ----- Sub-clip to timeline -----

    /// <summary>Requests placing the marked range at the timeline playhead (whole file when unmarked: NaN for both ends).</summary>
    private void InsertRange()
    {
        if (_shown is not { IsFile: true, Kind: not MediaBrowserKind.Image } item || _cutter is not { DurationSeconds: > 0 } cutter)
            return;
        if (HasVideo?.Invoke() != true)
        {
            StatusText.Text = "Open a video or a project first: the sub-clip is put on its timeline at the playhead.";
            return;
        }

        // Unmarked: the whole file as a drop places it (NaN = no range), never stretched to the container duration (the cutter span),
        // which would add trailing black frames.
        var (start, end) = cutter.HasRange ? (cutter.InSeconds, cutter.OutSeconds) : (double.NaN, double.NaN);
        AddRangeRequested?.Invoke(item.Path, start, end, item.Kind == MediaBrowserKind.Audio);
    }

    /// <summary>
    /// Drags the marked band out as FileDrop (so any file target accepts it), tagged with <see cref="DragFormat"/> and, when marked, the
    /// sub-clip (<see cref="SubClipFormat"/>) the timeline places as that range. Background work is held during the drag.
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
        // Internal drags pre-warm nothing before the drop; the drop opens what it needs (MainWindow.IsInternalDrag).
        StopStripOnDemand();
        using (WorkScheduler.HoldBackground())
            DragDrop.DoDragDrop(cutter, data, DragDropEffects.Copy);
    }

    /// <summary>Releases the preview frame, cutter frames and any pending still (view hidden or pane released).</summary>
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
