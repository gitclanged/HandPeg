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
/// Built in code and cheap to construct (Law 1): nothing is captured or drawn until shown. Pass 5.6.4B: the Histogram is the YRGB overlay
/// (HistogramView over YrgbHistogram) with YRGB | RGB | Luma pills in the title row, its mode shared with the Color Studio curves through
/// AppSettings.ColorHistogramMode.
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
    private readonly HistogramView _histogram = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _modePills = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };

    // Trailing throttle: a change arms it, the tick captures; intermediate changes coalesce into one capture.
    private readonly DispatcherTimer _throttle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };

    private bool _attached, _capturing, _wanted;

    // A held playhead scrub skipped its captures (ScrubGate.IsHeld); ScrubGate.Released captures once.
    private bool _captureAfterScrub;

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
        foreach (var mode in YrgbHistogram.Modes)
        {
            var label = YrgbHistogram.LabelOf(mode);
            var pill = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 0, 0, 0), ToolTip = $"Histogram: {label}" };
            AutomationProperties.SetName(pill, label + " Histogram");
            pill.Click += (_, _) =>
            {
                HistogramMode = mode;
                AppSettings.Update(settings => settings.ColorHistogramMode = label);
                YrgbHistogram.Announce(mode, this);
            };
            _modePills.Children.Add(pill);
        }

        title.Children.Add(_modePills);
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
        stage.Children.Add(_histogram);
        stage.Children.Add(_note);
        HistogramMode = YrgbHistogram.Parse(AppSettings.Current.ColorHistogramMode);

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

    /// <summary>The Histogram's overlay channels (YRGB | RGB | Luma); setting it updates the pills and redraws.</summary>
    internal HistogramMode HistogramMode
    {
        get => _histogram.Mode;
        set
        {
            _histogram.Mode = value;
            for (var i = 0; i < _modePills.Children.Count; i++)
                ((System.Windows.Controls.Primitives.ToggleButton)_modePills.Children[i]).IsChecked = YrgbHistogram.Modes[i] == value;
        }
    }

    /// <summary>The YRGB histogram last drawn (goldens).</summary>
    internal YrgbHistogram? ShownHistogram => _histogram.Painter.Histogram;

    private void Attach()
    {
        if (_attached)
            return;

        // The Color Studio may have changed the shared mode while this pane was away.
        HistogramMode = YrgbHistogram.Parse(AppSettings.Current.ColorHistogramMode);

        _attached = true;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.TimelineChanged += Want;
        _viewModel.PreviewStateInvalidated += Want;
        GradeGhost.Changed += GhostChanged;
        YrgbHistogram.ModeChanged += SharedMode_Changed;

        // Static event: held only while attached (Detach removes it), so it never roots an unloaded pane.
        ScrubGate.Released += ScrubGate_Released;
        GhostChanged();
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
        GradeGhost.Changed -= GhostChanged;
        YrgbHistogram.ModeChanged -= SharedMode_Changed;
        ScrubGate.Released -= ScrubGate_Released;
        _captureAfterScrub = false;
        _throttle.Stop();
    }

    // A pill click in the Color Studio: adopt the mode live (the setter never writes the setting, so nothing echoes back).
    private void SharedMode_Changed(HistogramMode mode, object? origin)
    {
        if (!ReferenceEquals(origin, this) && HistogramMode != mode)
            HistogramMode = mode;
    }

    // A Color Studio grading gesture: its pre-grade ghost over this Histogram until the gesture ends.
    private void GhostChanged() => _histogram.ShowGhost(GradeGhost.Shown);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Paused scrubs move the playhead many times per second: capture after it rests, since an exact-frame readback on the pump
            // would delay the scrub's own frames.
            // A held scrub defers every capture to its release (ScrubGate_Released): no readbacks compete with the scrub's frames.
            case nameof(MainViewModel.PositionMs) when ScrubGate.IsHeld:
                _captureAfterScrub = true;
                _throttle.Stop();
                break;
            case nameof(MainViewModel.PositionMs) when !_viewModel.IsPlaying:
                Want(afterRest: true);
                break;
            case nameof(MainViewModel.IsPlaying):
            case nameof(MainViewModel.HasSource):
                Want();
                break;
        }
    }

    // The scrub ended (after its last PositionMs commit): one capture for the frame it rests on.
    private void ScrubGate_Released()
    {
        if (!_captureAfterScrub)
            return;

        _captureAfterScrub = false;
        if (Dispatcher.CheckAccess())
            Want();
        else
            Dispatcher.InvokeAsync(Want);
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
                // No source: drop any draw still in flight and the last histogram, so nothing stale reappears over the note.
                _drawn++;
                _histogram.Show(null);
                (_histogram.Visibility, _modePills.Visibility) = (Visibility.Collapsed, Visibility.Collapsed);
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
        var histogram = kind == VideoScopeKind.Histogram;
        _modePills.Visibility = histogram ? Visibility.Visible : Visibility.Collapsed;
        if (histogram)
        {
            // The YRGB overlay, drawn in WPF over the counts (no bitmap).
            var counts = await Task.Run(() => YrgbHistogram.Of(frame.Bgra, frame.Width, frame.Height));
            if (turn != _drawn)
                return;
            _histogram.Show(counts);
            (_histogram.Visibility, _picture.Visibility) = (Visibility.Visible, Visibility.Collapsed);

            // The hidden picture drops the Vectorscope's Uniform: every scope but the Vectorscope fills the pane.
            _picture.Stretch = Stretch.Fill;
            ShowNote(_viewModel.IsPlaying ? "Updates when paused." : "");
            return;
        }

        var (pixels, width, height) = await Task.Run(() => VideoScopes.Render(kind, frame.Bgra, frame.Width, frame.Height));
        if (turn != _drawn)
            return;
        (_histogram.Visibility, _picture.Visibility) = (Visibility.Collapsed, Visibility.Visible);

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
        _note.VerticalAlignment = _picture.Source is null && _histogram.Visibility != Visibility.Visible ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        _note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
