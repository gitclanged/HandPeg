using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Docking;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

/// <summary>
/// The Video Scopes pane (5.4C): the Histogram, the Luma Waveform or the RGB Parade of the frame the player shows, chosen in its title
/// row. Fed by a small capture of the paused player (GpuPreviewEngine.CaptureThumbnailAsync: never while it plays, so no played frame
/// waits for one, Law 3), asked for only while the pane is in sight and paused, after the frame, the timeline or the preview changed,
/// at most about eight times a second; the counting and drawing run off the interface's thread (Services/VideoScopes.cs) into one
/// bitmap, reused. While the player plays the pane keeps its last picture and says it updates when paused. Built in code and light
/// to make (Law 1): nothing is captured or drawn until it is shown.
/// </summary>
public sealed class VideoScopesPane : UserControl
{
    /// <summary>The scopes as the title row's box lists them, in <see cref="VideoScopeKind"/>'s order.</summary>
    internal static readonly string[] KindNames = ["Histogram", "Luma Waveform", "RGB Parade"];

    /// <summary>The least time between two captures, milliseconds (about 8 Hz).</summary>
    internal const int ThrottleMs = 125;

    private readonly MainViewModel _viewModel;
    private readonly Func<Task<(byte[] Bgra, int Width, int Height)?>> _capture;
    private readonly ComboBox _kindBox;
    private readonly Image _picture;
    private readonly TextBlock _note;

    // A trailing throttle: a change starts it, its tick captures; changes meanwhile are one capture.
    private readonly DispatcherTimer _throttle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };

    private bool _attached, _capturing, _wanted;

    // The last frame captured, so another scope is drawn from it at once; and the picture's one bitmap.
    private (byte[] Bgra, int Width, int Height)? _frame;
    private WriteableBitmap? _bitmap;
    private int _drawn;

    public VideoScopesPane(MainViewModel viewModel, Func<Task<(byte[] Bgra, int Width, int Height)?>> capture)
    {
        _viewModel = viewModel;
        _capture = capture;
        AutomationProperties.SetName(this, "Video Scopes Pane");

        _kindBox = new ComboBox { MinWidth = 120, Margin = new Thickness(6, 0, 0, 0), ItemsSource = KindNames, SelectedIndex = (int)VideoScopeKind.LumaWaveform };
        _kindBox.ToolTip = "Histogram: how many pixels are at each level, red, green, blue and the luma. Luma Waveform: the brightness of each column of the "
                           + "picture, black at the bottom, white at the top. RGB Parade: the same for red, green and blue side by side.";
        AutomationProperties.SetName(_kindBox, "Scope");
        _kindBox.SelectionChanged += (_, _) => _ = DrawAsync();

        var title = new PaneTitleRow { Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(new PaneTitleSlot { PaneId = MainWindow.VideoScopesId });
        title.Children.Add(_kindBox);
        DockPanel.SetDock(title, Dock.Top);

        _picture = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.Linear);
        AutomationProperties.SetName(_picture, "Scope Picture");
        _note = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(_note, "Scope Status");

        var stage = new Grid { ClipToBounds = true };
        stage.SetResourceReference(BackgroundProperty, "InsetBrush");
        stage.Children.Add(_picture);
        stage.Children.Add(_note);

        var root = new DockPanel();
        root.Children.Add(title);
        root.Children.Add(stage);
        Content = root;
        ShowNote("Pause the player to read the frame.");

        _throttle.Tick += (_, _) => _ = CaptureAsync();

        // Every dock rebuild takes the pane out and puts it back: it listens only while it is in a window.
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                Want();
            else
                _throttle.Stop();
        };
    }

    /// <summary>The scope shown.</summary>
    internal VideoScopeKind Kind
    {
        get => (VideoScopeKind)Math.Max(_kindBox.SelectedIndex, 0);
        set => _kindBox.SelectedIndex = (int)value;
    }

    private void Attach()
    {
        if (_attached)
            return;

        _attached = true;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.TimelineChanged += Want;
        _viewModel.PreviewStateInvalidated += Want;
        Want();
    }

    private void Detach()
    {
        if (!_attached)
            return;

        _attached = false;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.TimelineChanged -= Want;
        _viewModel.PreviewStateInvalidated -= Want;
        _throttle.Stop();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // A paused scrub moves the playhead many times a second: captured once it has rested (5.4C review: a capture is an exact
            // frame's readback on the pump, which a scrub's own frames would wait behind).
            case nameof(MainViewModel.PositionMs) when !_viewModel.IsPlaying:
                Want(afterRest: true);
                break;
            case nameof(MainViewModel.IsPlaying):
            case nameof(MainViewModel.HasSource):
                Want();
                break;
        }
    }

    /// <summary>The frame may have changed: captured again once the throttle lets it (never while playing: the note says so instead).</summary>
    private void Want() => Want(afterRest: false);

    /// <param name="afterRest">The throttle starts again from now (a trailing debounce), rather than running on from the first ask.</param>
    private void Want(bool afterRest)
    {
        if (!_attached || !IsVisible)
            return;

        if (_viewModel.IsPlaying)
        {
            _throttle.Stop();
            ShowNote(_frame is null ? "Updates when paused." : "Updates when paused: this is the last paused frame.");
            return;
        }

        _wanted = true;
        if (afterRest)
            _throttle.Stop();
        if (!_throttle.IsEnabled && !_capturing)
            _throttle.Start();
    }

    private async Task CaptureAsync()
    {
        _throttle.Stop();
        if (_capturing || !_attached || !IsVisible || _viewModel.IsPlaying)
            return;

        _capturing = true;
        _wanted = false;
        try
        {
            var frame = await _capture();
            if (frame is { Width: > 0, Height: > 0 } captured)
            {
                _frame = captured;
                await DrawAsync();
            }
            else if (!_viewModel.HasSource)
            {
                (_frame, _picture.Source) = (null, null);
                ShowNote("Open a video to read its frame.");
            }
            else if (_viewModel.IsPlaying)
            {
                ShowNote("Updates when paused.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            AppLog.Write($"Video Scopes: the frame could not be read: {ex.Message}");
        }
        finally
        {
            _capturing = false;
        }

        // What changed while the capture was out is captured next.
        if (_wanted && _attached && IsVisible && !_viewModel.IsPlaying)
            _throttle.Start();
    }

    /// <summary>The scope chosen drawn from the last frame: counted and drawn off the interface's thread, then written into the one bitmap.</summary>
    private async Task DrawAsync()
    {
        if (_frame is not { } frame)
            return;

        var kind = Kind;
        var turn = ++_drawn;
        var (pixels, width, height) = await Task.Run(() => VideoScopes.Render(kind, frame.Bgra, frame.Width, frame.Height));
        if (turn != _drawn)
            return;

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        _picture.Source = _bitmap;
        ShowNote(_viewModel.IsPlaying ? "Updates when paused." : "");
    }

    private void ShowNote(string text)
    {
        // Over a picture the note goes to the bottom edge, so the scope stays readable under it.
        _note.Text = text;
        _note.VerticalAlignment = _picture.Source is null ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        _note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
