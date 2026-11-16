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
/// Video Scopes pane: Histogram, Luma Waveform, RGB Parade or Vectorscope (kept square) of the player frame, selected in the title row.
/// Fed by a small paused-player capture (GpuPreviewEngine.CaptureThumbnailAsync; never during playback, so no frame waits on it: Law 3),
/// requested only while visible and paused after frame, timeline or preview changes, at most ~8 Hz. Binning and rendering run off the UI
/// thread (Services/VideoScopes.cs) into one reused bitmap. During playback the last image stays with an "updates when paused" note.
/// Built in code and cheap to construct (Law 1): nothing is captured or drawn until shown.
/// </summary>
public sealed class VideoScopesPane : UserControl
{
    /// <summary>Scope names for the title-row selector, in <see cref="VideoScopeKind"/> order.</summary>
    internal static readonly string[] KindNames = ["Histogram", "Luma Waveform", "RGB Parade", "Vectorscope"];

    /// <summary>Minimum interval between captures, ms (~8 Hz).</summary>
    internal const int ThrottleMs = 125;

    private readonly MainViewModel _viewModel;
    private readonly Func<Task<(byte[] Bgra, int Width, int Height)?>> _capture;
    private readonly ComboBox _kindBox;
    private readonly Image _picture;
    private readonly TextBlock _note;

    // Trailing throttle: a change arms it, the tick captures; intermediate changes coalesce into one capture.
    private readonly DispatcherTimer _throttle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };

    private bool _attached, _capturing, _wanted;

    // Last captured frame (so switching scopes redraws immediately) and the single reused output bitmap.
    private (byte[] Bgra, int Width, int Height)? _frame;
    private WriteableBitmap? _bitmap;
    private int _drawn;

    public VideoScopesPane(MainViewModel viewModel, Func<Task<(byte[] Bgra, int Width, int Height)?>> capture)
    {
        _viewModel = viewModel;
        _capture = capture;
        AutomationProperties.SetName(this, "Video Scopes Pane");

        _kindBox = new ComboBox { MinWidth = 120, Margin = new Thickness(6, 0, 0, 0), ItemsSource = KindNames, SelectedIndex = (int)VideoScopeKind.LumaWaveform };
        // Short tooltip per the microcopy rule (12 words max); each scope's name describes it.
        _kindBox.ToolTip = "The scope drawn from the paused frame";
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

        // Dock rebuilds detach and re-attach the pane, so subscribe only while loaded.
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

    /// <summary>Currently selected scope.</summary>
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
            // Paused scrubs move the playhead many times per second: capture after it rests, since an exact-frame readback on the pump
            // would delay the scrub's own frames.
            case nameof(MainViewModel.PositionMs) when !_viewModel.IsPlaying:
                Want(afterRest: true);
                break;
            case nameof(MainViewModel.IsPlaying):
            case nameof(MainViewModel.HasSource):
                Want();
                break;
        }
    }

    /// <summary>Frame may have changed: schedule a throttled recapture (never during playback; a note is shown instead).</summary>
    private void Want() => Want(afterRest: false);

    /// <param name="afterRest">Restart the throttle from now (trailing debounce) instead of keeping the first request's schedule.</param>
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
            AppLog.Write("Video Scopes: the frame could not be read", ex);
        }
        finally
        {
            _capturing = false;
        }

        // Changes that arrived during the capture trigger another one.
        if (_wanted && _attached && IsVisible && !_viewModel.IsPlaying)
            _throttle.Start();
    }

    /// <summary>Renders the selected scope from the last frame off the UI thread, then writes it into the reused bitmap (stale results dropped).</summary>
    private async Task DrawAsync()
    {
        if (_frame is not { } frame)
            return;

        var kind = Kind;
        var turn = ++_drawn;
        var (pixels, width, height) = await Task.Run(() => VideoScopes.Render(kind, frame.Bgra, frame.Width, frame.Height));
        if (turn != _drawn)
            return;

        // Vectorscope uses Uniform to keep its circle round; other scopes fill the pane.
        _picture.Stretch = kind == VideoScopeKind.Vectorscope ? Stretch.Uniform : Stretch.Fill;

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        _picture.Source = _bitmap;
        ShowNote(_viewModel.IsPlaying ? "Updates when paused." : "");
    }

    private void ShowNote(string text)
    {
        // With an image shown, bottom-align the note so the scope stays readable.
        _note.Text = text;
        _note.VerticalAlignment = _picture.Source is null ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        _note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
