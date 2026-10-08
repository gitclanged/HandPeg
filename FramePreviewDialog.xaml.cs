using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Shows one frame of the source with the colour and sharpening settings applied, redrawn as the sliders
/// or the position change. Each redraw is an FFmpeg run that writes the frame to a picture file.
///
/// Two rules keep those runs from ever overlapping. Changes are debounced: a run is only asked for once
/// the controls have been still for a moment. And there is a single runner: a request that arrives while
/// FFmpeg is working does not start a second process, it only notes that another run is wanted, which the
/// runner makes, with the values of that moment, as soon as the current one has finished.
/// </summary>
public partial class FramePreviewDialog : Window
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _debounce;
    private readonly CancellationTokenSource _closing = new();

    // One file for the life of the dialog, overwritten by every run.
    private readonly string _framePath = Path.Combine(SessionPaths.Previews, $"frame_{Guid.NewGuid():N}.jpg");

    private bool _isRendering;
    private bool _renderWanted;

    public FramePreviewDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
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

    private void HistogramBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            RequestRender();
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
    /// frame is wanted; the loop then goes round again. FFmpeg is never started while FFmpeg is running.
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

                // Just short of the end at most: there is no frame to take at the very end of a file.
                var seconds = Math.Clamp(PositionSlider.Value, 0, Math.Max(PositionSlider.Maximum - 200, 0)) / 1000;
                var command = _viewModel.BuildFramePreviewCommand(seconds, _framePath, HistogramBox.IsChecked == true);
                StatusText.Text = "Rendering...";

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_framePath)!);
                    await Task.Run(() => FfmpegRunner.RunAsync(command, new Progress<FfmpegProgress>(), _closing.Token), _closing.Token);
                    ShowFrame();
                    StatusText.Text = $"Frame at {TimeDisplay.Format(seconds)}";
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    StatusText.Text = $"Could not render the frame: {ex.Message}";
                }
            }
            while (_renderWanted && !_closing.IsCancellationRequested);
        }
        finally
        {
            _isRendering = false;
        }
    }

    /// <summary>
    /// Reads the picture completely into memory and lets go of the file, so that the next run can
    /// overwrite it while this frame is still on screen.
    /// </summary>
    private void ShowFrame()
    {
        using var stream = File.OpenRead(_framePath);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        FrameImage.Source = image;
    }

    private void FramePreviewDialog_Closed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _debounce.Stop();

        // Stops a run in progress by ending its process.
        _closing.Cancel();

        try
        {
            File.Delete(_framePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still being let go of; the session folder is removed when the application closes.
        }
    }
}
