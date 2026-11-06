using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Shows one frame of the Sequence with the colour and sharpening settings applied, redrawn as the sliders or the position
/// change. Each redraw is a capture through the GPU compositor (5.2-Arm64AndNpu: GpuPreviewEngine.CaptureFrameAsync, the
/// whole finished frame as the export draws it, grade and all); FFmpeg makes no still for it any more.
///
/// Two rules keep those captures from ever overlapping. Changes are debounced: a capture is only asked for once the controls
/// have been still for a moment. And there is a single runner: a request that arrives while a capture is under way does not
/// ask for a second one, it only notes that another is wanted, which the runner asks for, with the values of that moment, as
/// soon as the current one has come back. The histogram is counted on the processor from each still's pixels, once, and
/// drawn as a small picture of its own over the frame's corner: nothing is drawn on the frame, and nothing runs per frame.
/// </summary>
public partial class FramePreviewDialog : Window
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The histogram's size in pixels: one column for each of the 256 levels, and as tall as it reads well.</summary>
    private const int HistogramWidth = 256, HistogramHeight = 100;

    private readonly MainViewModel _viewModel;
    private readonly Func<double, Task<(byte[] Bgra, int Width, int Height)?>> _capture;
    private readonly DispatcherTimer _debounce;
    private readonly CancellationTokenSource _closing = new();

    private bool _isRendering;
    private bool _renderWanted;

    // The levels of the still on show, counted as it came back (red, green, blue by 256 levels); null while there is none.
    private int[,]? _levels;

    /// <param name="capture">
    /// A moment of the Sequence drawn by the GPU engine and read back: the whole finished frame. Null when it gave none (the
    /// engine is not running, or the read-back failed): the dialog says so, and keeps the frame it showed last.
    /// </param>
    public FramePreviewDialog(MainViewModel viewModel, Func<double, Task<(byte[] Bgra, int Width, int Height)?>> capture)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _viewModel = viewModel;
        _capture = capture;
        DataContext = viewModel;

        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RenderAsync();
        };

        // Starts where the main timeline is.
        PositionSlider.Maximum = Math.Max(viewModel.DurationMs, 1);
        PositionSlider.Value = Math.Clamp(viewModel.PositionMs, 0, PositionSlider.Maximum);
        UpdatePositionText();

        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) => _ = RenderAsync();
        Closed += FramePreviewDialog_Closed;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every grading control: the Color... values, and the sharpening.
        if (e.PropertyName is nameof(MainViewModel.SharpenStrength)
            || (e.PropertyName?.StartsWith("Color", StringComparison.Ordinal) ?? false))
        {
            RequestRender();
        }
    }

    /// <summary>The histogram shown or hidden over the still on show: its levels were counted as it came, so nothing is captured again.</summary>
    private void HistogramBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ShowHistogram();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Raised once while the window is still being built, before there is anything to draw.
        if (!IsLoaded)
            return;

        UpdatePositionText();
        RequestRender();
    }

    private void UpdatePositionText() => PositionText.Text = TimeDisplay.Format(PositionSlider.Value / 1000);

    /// <summary>Every change restarts the wait, so a slider in motion asks for nothing until it rests.</summary>
    private void RequestRender()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// The single runner. While it is at work, further calls return at once after noting that another
    /// frame is wanted; the loop then goes round again. A capture is never asked for while one is under way.
    /// </summary>
    private async Task RenderAsync()
    {
        if (_isRendering)
        {
            _renderWanted = true;
            return;
        }

        _isRendering = true;
        try
        {
            do
            {
                _renderWanted = false;

                // Just short of the end at most: there is no frame to take at the very end of the Sequence.
                var seconds = Math.Clamp(PositionSlider.Value, 0, Math.Max(PositionSlider.Maximum - 200, 0)) / 1000;
                StatusText.Text = "Drawing...";
                try
                {
                    var shown = await ShowCapturedAsync(seconds);
                    if (_closing.IsCancellationRequested)
                        return;
                    StatusText.Text = shown
                        ? $"Frame at {TimeDisplay.Format(seconds)}"
                        : "The GPU engine could not draw the frame: it is not running yet, or it stopped (Reload Player starts it again).";
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    StatusText.Text = $"Could not draw the frame: {ex.Message}";
                }
            }
            while (_renderWanted && !_closing.IsCancellationRequested);
        }
        finally
        {
            _isRendering = false;
        }
    }

    /// <summary>The frame from the GPU engine, its levels counted for the histogram. False when it gave none.</summary>
    private async Task<bool> ShowCapturedAsync(double seconds)
    {
        if (await _capture(seconds) is not { } frame || frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length < (long)frame.Width * frame.Height * 4)
            return false;

        // Counted away from the window's thread, once for this still: a large frame is millions of pixels.
        var (bgra, width, height) = frame;
        var levels = await Task.Run(() => CountLevels(bgra));
        if (_closing.IsCancellationRequested)
            return false;

        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        picture.Freeze();
        FrameImage.Source = picture;
        _levels = levels;
        ShowHistogram();
        return true;
    }

    /// <summary>The histogram of the still on show over the frame's lower right corner, while Show Histogram is ticked.</summary>
    private void ShowHistogram()
    {
        var shown = HistogramBox.IsChecked == true && _levels is not null;
        HistogramImage.Source = shown ? HistogramPicture(_levels!) : null;
        HistogramImage.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A frame's red, green and blue levels (BGRA8, its alpha not read), counted: [channel, level], red first.</summary>
    internal static int[,] CountLevels(byte[] bgra)
    {
        var counts = new int[3, 256];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            counts[2, bgra[i]]++;
            counts[1, bgra[i + 1]]++;
            counts[0, bgra[i + 2]]++;
        }

        return counts;
    }

    /// <summary>
    /// Counted levels as a small picture (<see cref="HistogramWidth"/> by <see cref="HistogramHeight"/>, BGRA, frozen): the three
    /// curves laid over each other on a dark ground, each channel lit where its curve reaches, so that where they overlap is white.
    /// Scaled to the tallest level but black and white, which a letterbox or a white sky would leave everything else flat under.
    /// </summary>
    internal static BitmapSource HistogramPicture(int[,] levels)
    {
        var tallest = 1;
        for (var channel = 0; channel < 3; channel++)
        {
            for (var level = 1; level < 255; level++)
                tallest = Math.Max(tallest, levels[channel, level]);
        }

        var pixels = new byte[HistogramWidth * HistogramHeight * 4];
        var bars = new int[3];
        for (var x = 0; x < HistogramWidth; x++)
        {
            // Red, green and blue as the picture's own bytes order them (blue first).
            for (var channel = 0; channel < 3; channel++)
                bars[2 - channel] = (int)Math.Min((long)levels[channel, x] * HistogramHeight / tallest, HistogramHeight);

            for (var y = 0; y < HistogramHeight; y++)
            {
                var at = ((HistogramHeight - 1 - y) * HistogramWidth + x) * 4;
                for (var b = 0; b < 3; b++)
                    pixels[at + b] = (byte)(y < bars[b] ? 230 : 24);
                pixels[at + 3] = 200;
            }
        }

        var picture = BitmapSource.Create(HistogramWidth, HistogramHeight, 96, 96, PixelFormats.Bgra32, null, pixels, HistogramWidth * 4);
        picture.Freeze();
        return picture;
    }

    private void FramePreviewDialog_Closed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _debounce.Stop();

        // A capture still under way is let come back to nothing.
        _closing.Cancel();
    }
}
