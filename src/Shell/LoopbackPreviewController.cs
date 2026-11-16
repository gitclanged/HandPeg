using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Controls;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary>
/// Live Encoded Preview controller: player bar toggle and flyout, split-wipe handle/divider/pills, Magnifier pointer hook, and
/// pushing export settings (<see cref="MainViewModel.CurrentLoopbackSettings"/> to <see cref="GpuPreviewEngine.SetLoopback"/>) so the
/// worker shows frames as the export encoder produces them (D-02).
/// </summary>
/// <remarks>
/// UI thread only. The toggle is built with the bar's first layout (popup empty, flyout built on first open: Law 1); the wipe and
/// status timer exist only while on, the Magnifier hook only while both are on. Turns off on 3D view, player unload and window
/// close; never persisted as on.
/// </remarks>
internal sealed class LoopbackPreviewController
{
    /// <summary>Display name and glyph (Segoe MDL2 View).</summary>
    internal const string LoopbackName = "Live Encoded Preview", LoopbackGlyph = Glyphs.View;

    // Pill gap from the line, top offset, right-edge margin, and pill padding/border.
    internal const double WipePillGap = 10, WipePillTop = 14, WipePillEdge = 4, WipePillChrome = 16;

    // Debounce for setting changes before one SetLoopback (which re-encodes the paused frame).
    private const int LoopbackSettingsGatherMs = 25;

    private readonly MainViewModel _viewModel;
    private readonly Window _window;
    private readonly ContentControl _barSlot;
    private readonly FrameworkElement _transportRow;
    private readonly OverflowStrip _transportCenter;
    private readonly Grid _overlay;
    private readonly VideoSurface _video;
    private readonly Func<GpuPreviewEngine?> _engine;
    private readonly Func<bool> _exploded;
    private readonly TimerScope _timers = new();
    private readonly Debounce _settingsGather;

    private ToggleButton? _loopbackToggle;
    private RadioButton[]? _loopbackModes;
    private Canvas? _loopbackWipeLayer;

    // Inverse of the player view transform for the wipe layer: PlayerOverlay applies the view matrix (ApplyPlayerView) for picture-space
    // boxes, but the wipe is positioned in player space (PictureSpan already includes zoom/pan); without it zoom applied twice.
    private readonly MatrixTransform _loopbackWipeUnzoom = new();

    // Side labels: RAW left of the line, ENCODED right; not hit-testable.
    private Border? _loopbackRawPill, _loopbackEncodedPill;

    // Divider drawn here as well, exactly over the worker's line: the worker only draws it once an encoded frame returns, so a failed
    // encoder would otherwise leave handle and pills with no line.
    private Border? _loopbackDivider;

    // Pills are never hidden: they live in their own 0x0 Canvas (handle and divider outside it) clipped to the visible picture, so they
    // slide off its edge with the line; ENCODED shows full text when it fits right of the line, else its first word.
    private Canvas? _loopbackPillLayer;

    // Full ENCODED text (SetEncodedPill) and its measured width at a display scale; never derived from the label's current text, which
    // would oscillate between full and short forms.
    private string _loopbackEncodedText = "ENCODED";
    private double _loopbackEncodedWidth = double.NaN, _loopbackEncodedWidthScale;
    private Border? _loopbackWipeHandle;
    private DispatcherTimer? _loopbackTimer;
    private GpuPreviewEngine? _loopbackEngine;
    private ExportLoopbackSettings? _loopbackSettings;
    private string? _loopbackWhyNot;
    private int _loopbackTicks;
    private bool _loopbackPointerHooked;

    /// <param name="viewModel">Window VM (preview switches, modes, export settings).</param>
    /// <param name="window">Owner window (resources, Closing, dispatcher).</param>
    /// <param name="barSlot">Player bar slot the toggle is built into on first show.</param>
    /// <param name="transportRow">Player bar row (BarToggle style source).</param>
    /// <param name="transportCenter">Bar overflow strip (More menu entry).</param>
    /// <param name="explodedToggle">3D view toggle; enabling it disables the preview.</param>
    /// <param name="overlay">PlayerOverlay: wipe parent and player size in DIPs.</param>
    /// <param name="video">Video surface (Magnifier pointer source; permanent unload disables the preview).</param>
    /// <param name="engine">Current GPU engine accessor (null when absent; replaced after failure or reload).</param>
    /// <param name="exploded">Whether the 3D Exploded View is shown.</param>
    public LoopbackPreviewController(MainViewModel viewModel, Window window, ContentControl barSlot, FrameworkElement transportRow, OverflowStrip transportCenter,
        ToggleButton explodedToggle, Grid overlay, VideoSurface video, Func<GpuPreviewEngine?> engine, Func<bool> exploded)
    {
        (_viewModel, _window, _barSlot, _transportRow, _transportCenter, _overlay, _video, _engine, _exploded) =
            (viewModel, window, barSlot, transportRow, transportCenter, overlay, video, engine, exploded);
        _settingsGather = new Debounce(TimeSpan.FromMilliseconds(LoopbackSettingsGatherMs), PushLoopbackSettings, DispatcherPriority.Input, _timers);

        // Visibility per PlaybackControlSet (on by default); content built on first show.
        barSlot.SetBinding(UIElement.VisibilityProperty, new Binding("PlaybackControls[ExportQualityPreview]") { Converter = (IValueConverter)window.FindResource("BoolToVisibility") });
        barSlot.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                BuildLoopbackToggle();
        };
        viewModel.PropertyChanged += LoopbackViewModel_PropertyChanged;
        explodedToggle.Checked += (_, _) =>
        {
            if (_viewModel.IsExportQualityPreview)
            {
                _viewModel.IsExportQualityPreview = false;
                _viewModel.StatusText = "Live Encoded Preview is off: the 3D Exploded View shows the layers, not the export.";
            }
        };
        video.Unloaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // Dock relayouts unload/reload the player immediately; only a player that stays unloaded disables the preview.
            if (!_video.IsLoaded && _viewModel.IsExportQualityPreview)
                _viewModel.IsExportQualityPreview = false;
        });
        window.Closing += (_, _) => _viewModel.IsExportQualityPreview = false;
    }

    private void LoopbackViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsExportQualityPreview):
                ApplyLoopback();
                break;
            case nameof(MainViewModel.ExportQualityPreviewMode):
            case nameof(MainViewModel.ExportQualityPreviewWipe):
                if (_viewModel.IsExportQualityPreview)
                {
                    _engine()?.SetLoopbackView(_viewModel.ExportQualityPreviewMode, _viewModel.ExportQualityPreviewWipe);
                    SyncLoopbackModes();
                    PlaceLoopbackWipe();
                }

                break;
            case nameof(MainViewModel.ExportQualityPreviewLoupe):
                SyncLoopbackLoupe();
                break;
            case nameof(MainViewModel.IsPlaying):
                // Update the ENCODED pill immediately on play/pause (e.g. RAW WHILE PLAYING for paused-only encoders) rather than on
                // the 1 s status tick; the flyout status keeps the tick.
                if (_viewModel.IsExportQualityPreview && _loopbackSettings is { } playSettings && _engine() is { } playEngine)
                    SetEncodedPill(EncodedPillText(playSettings, playEngine.LoopbackStatus, _viewModel.IsPlaying));
                break;
            case { } name when MainViewModel.LoopbackSettingNames.Contains(name):
                if (_viewModel.IsExportQualityPreview)
                    GatherLoopbackSettings();
                break;
        }
    }

    // Setting change: send 25 ms after the last change in a burst, once per burst.
    private void GatherLoopbackSettings() => _settingsGather.Poke();

    /// <summary>
    /// Pushes current Export settings to the engine; the worker re-encodes the paused frame from its cached scene (no re-render) and
    /// the status and ENCODED pill update immediately.
    /// </summary>
    private void PushLoopbackSettings()
    {
        if (!_viewModel.IsExportQualityPreview || _exploded())
            return;
        _loopbackSettings = _viewModel.CurrentLoopbackSettings(out _loopbackWhyNot);
        _engine()?.SetLoopback(_loopbackSettings);
        UpdateLoopbackStatus();
    }

    /// <summary>Applies preview state: engine settings (or off), display mode, wipe handle and status timer.</summary>
    private void ApplyLoopback()
    {
        var gpu = _engine();
        if (!_viewModel.IsExportQualityPreview)
        {
            gpu?.SetLoopback(null);
            (_loopbackEngine, _loopbackSettings, _loopbackWhyNot) = (null, null, null);
            _loopbackTimer?.Stop();
            _settingsGather.Cancel();
            if (_loopbackWipeLayer is not null)
                _loopbackWipeLayer.Visibility = Visibility.Collapsed;
            _viewModel.ExportQualityPreviewStatus = "";
            SyncLoopbackLoupe();
            return;
        }

        if (_exploded())
        {
            _viewModel.IsExportQualityPreview = false;
            _viewModel.StatusText = "Live Encoded Preview needs the flat picture: leave the 3D Exploded View first.";
            return;
        }

        _loopbackSettings = _viewModel.CurrentLoopbackSettings(out _loopbackWhyNot);
        _loopbackEngine = gpu;
        gpu?.SetLoopback(_loopbackSettings);
        gpu?.SetLoopbackView(_viewModel.ExportQualityPreviewMode, _viewModel.ExportQualityPreviewWipe);
        EnsureLoopbackWipe();
        _loopbackTimer ??= _timers.Make(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => LoopbackTick());
        _loopbackTicks = 0;
        _loopbackTimer.Start();
        PlaceLoopbackWipe();
        UpdateLoopbackStatus();
        SyncLoopbackLoupe();
    }

    // 250 ms tick: handle tracks zoom/pan; status re-read each second; a restarted engine (failure, Reload Player) gets settings again.
    private void LoopbackTick()
    {
        if (!_viewModel.IsExportQualityPreview)
        {
            _loopbackTimer?.Stop();
            return;
        }

        var gpu = _engine();
        if (!ReferenceEquals(_loopbackEngine, gpu))
        {
            _loopbackEngine = gpu;
            gpu?.SetLoopback(_loopbackSettings);
            gpu?.SetLoopbackView(_viewModel.ExportQualityPreviewMode, _viewModel.ExportQualityPreviewWipe);
            gpu?.SetLoopbackLoupe(null, null); // re-placed on the next pointer move over the player
        }

        PlaceLoopbackWipe();
        if (++_loopbackTicks % 4 == 0)
            UpdateLoopbackStatus();
    }

    private void UpdateLoopbackStatus()
    {
        var settings = _loopbackSettings;
        string text;
        if (settings is null)
        {
            text = "Nothing to preview: " + (_loopbackWhyNot ?? "this export's codec has no hardware encoder loopback") + ".";
        }
        else if (_engine() is not { } engine)
        {
            text = "Starts with the GPU preview: open a video first.";
        }
        else
        {
            var codec = ExportLoopbackSettings.CodecName(settings.Codec);
            var status = engine.LoopbackStatus;
            var where = status.OnAskedCard ? "" : $" (no {ExportLoopbackSettings.FamilyShort(settings.Family)} encoder here: a stand-in)";
            var paused = status.PausedOnly ? " Paused frames only: the plain picture while playing." : "";
            text = status.State switch
            {
                ExportLoopbackState.Running => string.Create(CultureInfo.InvariantCulture,
                    $"{settings.ShortName}, {settings.RateControlName}{(settings.TenBit ? ", 10-bit" : "")}: {status.EncoderName}{where}, {status.RoundTripMs:0} ms a round trip{(status.LateFrames > 0 ? $", {status.LateFrames} frames late" : "")}.{paused}"),
                ExportLoopbackState.Starting => $"Starting {(status.EncoderName.Length > 0 ? status.EncoderName : "the " + codec + " encoder")}...",
                ExportLoopbackState.NoEncoder => $"No {codec} hardware encoder on this PC: the plain picture is shown.",
                ExportLoopbackState.NoDecoder => settings.Codec == 1
                    ? $"No {codec} decoder on this PC: the plain picture is shown."
                    : $"No {codec} decoder on this PC (the {codec} Video Extension from the Microsoft Store adds one): the plain picture is shown.",
                ExportLoopbackState.Unsupported => "This export's codec has no hardware encoder loopback: the plain picture is shown.",
                ExportLoopbackState.Failed => string.Create(CultureInfo.InvariantCulture, $"The {codec} encoder or decoder failed (0x{status.HResult:X8}): the plain picture is shown."),
                ExportLoopbackState.Unavailable => "Not in this view: the flat picture of the Sequence only.",
                _ => "Waiting for the player's next frame...",
            };
        }

        if (_viewModel.ExportQualityPreviewStatus != text)
            _viewModel.ExportQualityPreviewStatus = text;
        SetEncodedPill(settings is null ? "NO LOOPBACK" : _engine() is not { } shown ? "ENCODED" : EncodedPillText(settings, shown.LoopbackStatus, _viewModel.IsPlaying));
    }

    /// <summary>
    /// ENCODED pill text: encoder and rate, e.g. "ENCODED \u00B7 HEVC QSV \u00B7 12 Mbps", "ENCODED \u00B7 x264 veryslow \u00B7 CRF 22");
    /// a cross-GPU stand-in is labelled; software encoders while playing show the raw picture; else the state. Pure (golden-tested).
    /// </summary>
    internal static string EncodedPillText(ExportLoopbackSettings settings, ExportLoopbackStatus status, bool playing) => status switch
    {
        { State: ExportLoopbackState.Running, PausedOnly: true } when playing => $"RAW WHILE PLAYING \u00B7 {settings.ShortName}",
        { State: ExportLoopbackState.Running, OnAskedCard: false } => $"ENCODED \u00B7 {ExportLoopbackSettings.CodecName(settings.Codec)} \u00B7 STAND-IN \u00B7 {settings.RateShort}",
        { State: ExportLoopbackState.Running } => settings.PillText,
        { State: ExportLoopbackState.Starting } => $"ENCODED \u00B7 {settings.ShortName} \u00B7 STARTING",
        { State: ExportLoopbackState.NoEncoder } => "NO ENCODER",
        { State: ExportLoopbackState.NoDecoder } => "NO DECODER",
        { State: ExportLoopbackState.Unsupported } => "NO LOOPBACK",
        { State: ExportLoopbackState.Failed } failed => string.Create(CultureInfo.InvariantCulture, $"ENCODER FAILED 0x{failed.HResult:X8}"),
        _ => "ENCODED",
    };

    // ENCODED also reports failures (mirroring the flyout status). Compare against the stored full text (the label may be short) and
    // re-place immediately so new text never overflows for a 250 ms tick. Without a wipe, EnsureLoopbackWipe creates it with this text.
    private void SetEncodedPill(string text)
    {
        if (text == _loopbackEncodedText)
            return;
        (_loopbackEncodedText, _loopbackEncodedWidth) = (text, double.NaN);
        PlaceLoopbackWipe();
    }

    // ----- The player bar's toggle and its flyout -----

    private void BuildLoopbackToggle()
    {
        if (_loopbackToggle is not null)
            return;

        // Eye glyph (View) in the row's BarToggle style (Segoe MDL2 13, Timeline-tool height), paired with its dropdown arrow.
        var barToggle = (Style)_transportRow.Resources["BarToggle"];
        _loopbackToggle = new ToggleButton
        {
            Style = barToggle,
            Content = LoopbackGlyph,
            Padding = new Thickness(7, 4, 5, 4),
            Margin = new Thickness(3, 0, 0, 0),
            ToolTip = LoopbackName + ": the picture as the export's encoder would leave it, live (this PC's hardware encoder of the export's codec, at its rate control and bitrate). The arrow beside chooses Encoded, Split Wipe or the Artifact Heatmap x10, and the Magnifier.",
        };
        AutomationProperties.SetName(_loopbackToggle, LoopbackName);
        _loopbackToggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsExportQualityPreview)) { Mode = BindingMode.TwoWay });

        // Disabled for Audio Only output, matching the Export tab toggle.
        _loopbackToggle.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(MainViewModel.IsVideoExport)));
        ToolTipService.SetShowOnDisabled(_loopbackToggle, true);

        // The arrow: a smaller glyph, padded to the toggle's height (9 + 6 + 6 = 13 + 4 + 4).
        var more = new ToggleButton
        {
            Style = barToggle,
            Content = Glyphs.ChevronUp,
            FontSize = 9,
            Padding = new Thickness(4, 6, 4, 6),
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = LoopbackName + " options: what is shown, the Magnifier, and what the preview is doing.",
        };
        AutomationProperties.SetName(more, LoopbackName + " Options");

        // Law 1: create the popup empty; build its flyout on first open.
        var flyout = new Popup { StaysOpen = false, AllowsTransparency = true };
        flyout.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = more, Mode = BindingMode.TwoWay });
        more.Checked += (_, _) =>
        {
            flyout.Child ??= BuildLoopbackFlyout();
            UpdateLoopbackStatus();
            UiScale.PlaceAgainst(flyout, more, UiScale.FlyoutSide.AboveStart, 4);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_loopbackToggle);
        row.Children.Add(more);
        row.Children.Add(flyout);
        _barSlot.Content = row;
        AutomationProperties.SetName(_barSlot, LoopbackName);

        // Overflow entry in the bar's More menu.
        var before = _transportCenter.MenuEntryFor;
        _transportCenter.MenuEntryFor = child => ReferenceEquals(child, _barSlot)
            ? (_viewModel.IsExportQualityPreview ? LoopbackName + " (On)" : LoopbackName, () => _viewModel.IsExportQualityPreview = !_viewModel.IsExportQualityPreview)
            : before?.Invoke(child);
    }

    private FrameworkElement BuildLoopbackFlyout()
    {
        var panel = new StackPanel { MinWidth = 260, MaxWidth = 340 };
        panel.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        panel.Children.Add(new TextBlock { Text = LoopbackName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

        string[] labels = ["Encoded", "Split Wipe (drag the line on the picture)", "Artifact Heatmap x10"];
        _loopbackModes = new RadioButton[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var mode = i;
            var choice = new RadioButton { Content = labels[i], GroupName = "ExportQualityPreviewMode", Margin = new Thickness(0, 2, 0, 2), IsChecked = _viewModel.ExportQualityPreviewMode == i };
            choice.Checked += (_, _) => _viewModel.ExportQualityPreviewMode = mode;
            _loopbackModes[i] = choice;
            panel.Children.Add(choice);
        }

        // The loupe follows the pointer over the picture (as during edge-slider drags).
        var magnifier = new CheckBox
        {
            Content = "Magnifier (Loupe)",
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "A loupe at 400 % in the picture's far corner, round the point under the pointer: the encoded picture (or the difference) close up. Kept for next time.",
        };
        magnifier.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.ExportQualityPreviewLoupe)) { Mode = BindingMode.TwoWay });
        panel.Children.Add(magnifier);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.ExportQualityPreviewStatus)));
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock
        {
            Text = "An approximation: the export's own encoder may differ, and a paused frame is encoded as a key frame.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var chrome = new ButtonChrome { Padding = new Thickness(12, 10, 12, 10), Child = panel, DataContext = _viewModel };
        if (_window.TryFindResource("FlyoutChrome") is Style style)
            chrome.Style = style;
        return chrome;
    }

    private void SyncLoopbackModes()
    {
        if (_loopbackModes is null)
            return;
        for (var i = 0; i < _loopbackModes.Length; i++)
        {
            if (_loopbackModes[i].IsChecked != (i == _viewModel.ExportQualityPreviewMode))
                _loopbackModes[i].IsChecked = i == _viewModel.ExportQualityPreviewMode;
        }
    }

    // ----- The split wipe's handle on the player -----

    private void EnsureLoopbackWipe()
    {
        if (_loopbackWipeLayer is not null)
            return;

        // Grip centred on the worker-drawn line; only the handle is hit-testable on this layer.
        var handle = _loopbackWipeHandle = UiBrushes.WipeHandle("Drag: the source on the left, the encoded picture on the right.", LoopbackName + " Wipe");
        handle.MouseLeftButtonDown += (_, e) =>
        {
            handle.CaptureMouse();
            _engine()?.SetLoopbackLoupe(null, null); // hide the Magnifier during wipe drags
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            // Same span and formula as the handle and the worker line (GpuPreviewEngine.WipeAt / WipeLineX).
            var (viewWidth, viewHeight) = LoopbackViewSize();
            if (!handle.IsMouseCaptured || _engine() is not { } engine || viewWidth < 1)
                return;
            var span = engine.PictureSpan(viewWidth, viewHeight);
            if (span.Width > 1 && _loopbackWipeLayer is not null)
                _viewModel.ExportQualityPreviewWipe = GpuPreviewEngine.WipeAt(e.GetPosition(_loopbackWipeLayer).X, span);
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            handle.ReleaseMouseCapture();
            e.Handled = true;
        };

        _loopbackWipeLayer = new Canvas { ClipToBounds = true, Visibility = Visibility.Collapsed, RenderTransform = _loopbackWipeUnzoom };
        _loopbackRawPill = WipePill("RAW");
        _loopbackEncodedPill = WipePill(_loopbackEncodedText);

        // Pill layer: 0x0 Canvas at the origin, clipped by the picture Clip (ApplyWipePills), not ClipToBounds (which would clip all),
        // so handle and divider stay unclipped. Z-order: divider, pills, handle.
        _loopbackPillLayer = new Canvas { IsHitTestVisible = false };
        _loopbackPillLayer.Children.Add(_loopbackRawPill);
        _loopbackPillLayer.Children.Add(_loopbackEncodedPill);
        _loopbackDivider = UiBrushes.WipeDivider();
        _loopbackWipeLayer.Children.Add(_loopbackDivider);
        _loopbackWipeLayer.Children.Add(_loopbackPillLayer);
        _loopbackWipeLayer.Children.Add(_loopbackWipeHandle);
        _overlay.Children.Add(_loopbackWipeLayer);
        _overlay.SizeChanged += (_, _) => PlaceLoopbackWipe();
    }

    /// <summary>
    /// Positions the wipe handle (wipe mode only) using the worker's formula (WipeLineX over PictureSpan = SceneRenderer.Letterbox),
    /// rounding edges to device pixels (not DIPs; 1.5 px at 150 %) so it stays centred on the 2 px line. Called on player zoom, pan or resize.
    /// </summary>
    public void PlaceLoopbackWipe()
    {
        if (_loopbackWipeLayer is null || _loopbackWipeHandle is null)
            return;
        var (viewWidth, viewHeight) = LoopbackViewSize();
        var gpu = _engine();
        var shown = _viewModel.IsExportQualityPreview && _viewModel.ExportQualityPreviewMode == 1 && _loopbackSettings is not null && gpu is not null
            && viewWidth > 1 && viewHeight > 1;
        _loopbackWipeLayer.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
            return;

        var view = _overlay.RenderTransform?.Value ?? Matrix.Identity;
        if (view.HasInverse)
            view.Invert();
        else
            view = Matrix.Identity;
        _loopbackWipeUnzoom.Matrix = view;

        var x = GpuPreviewEngine.WipeLineX(_viewModel.ExportQualityPreviewWipe, gpu!.PictureSpan(viewWidth, viewHeight));
        var scale = VisualTreeHelper.GetDpi(_overlay).DpiScaleX;
        scale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        _loopbackWipeHandle.Height = _overlay.ActualHeight;
        Canvas.SetLeft(_loopbackWipeHandle, Math.Round((x - _loopbackWipeHandle.Width / 2) * scale) / scale);
        Canvas.SetTop(_loopbackWipeHandle, 0);

        // Pills: 10 DIPs either side of the line, 14 below the visible picture top.
        var picture = gpu.PictureRect(viewWidth, viewHeight);
        var (_, top, _, height) = picture;

        // Divider: shown with the handle, 2 device px on the worker line, spanning the visible picture height.
        if (_loopbackDivider is { } divider)
        {
            var (from, to) = (Math.Max(top, 0), Math.Min(top + height, viewHeight));
            divider.Width = 2 / scale;
            divider.Height = Math.Max(to - from, 0);
            Canvas.SetLeft(divider, (Math.Round(x * scale) - 1) / scale);
            Canvas.SetTop(divider, from);
        }
        // Both pills always visible with the layer, clipped at the picture edge; ENCODED width measured from its full text, shortened
        // when it would pass the visible right edge.
        if (_loopbackRawPill is { } raw && _loopbackEncodedPill is { Child: TextBlock words } encoded && _loopbackPillLayer is { } pills)
        {
            raw.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (double.IsNaN(_loopbackEncodedWidth) || _loopbackEncodedWidthScale != scale)
                (_loopbackEncodedWidth, _loopbackEncodedWidthScale) = (PillWidth(words, _loopbackEncodedText, scale), scale);
            ApplyWipePills(pills, raw, encoded, PlanWipePills(x, picture, viewWidth, viewHeight, raw.DesiredSize.Width, _loopbackEncodedWidth, scale), _loopbackEncodedText);
        }
    }

    private static readonly SolidColorBrush PillFill = UiBrushes.Frozen(0xCC, 0x18, 0x18, 0x1C), PillEdge = UiBrushes.Frozen(0x38, 0x38, 0x42);

    // Dark side-label pill: semi-bold caps, not hit-testable. Internal for goldens.
    internal static Border WipePill(string text) => new()
    {
        IsHitTestVisible = false,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 2, 7, 2),
        BorderThickness = new Thickness(1),
        Background = PillFill,
        BorderBrush = PillEdge,
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = UiBrushes.PictureInk },
    };

    /// <summary>Pill placement: RAW and ENCODED left edges, top, whether ENCODED is shortened, and the clip rect (visible picture);
    /// all device-pixel aligned.</summary>
    internal readonly record struct WipePillPlan(double RawLeft, double EncodedLeft, double Top, bool EncodedShort, Rect Clip);

    /// <summary>
    /// Pure pill layout for a line at <paramref name="lineX"/> (golden-tested). <paramref name="picture"/> is GpuPreviewEngine.PictureRect
    /// (overlay DIPs with zoom/pan applied). RAW ends <see cref="WipePillGap"/> left of the line, ENCODED starts that far right; both are
    /// always placed and clipped by the visible picture. ENCODED uses full text if it fits before the visible right edge minus
    /// <see cref="WipePillEdge"/>, else short text.
    /// </summary>
    internal static WipePillPlan PlanWipePills(double lineX, (double Left, double Top, double Width, double Height) picture,
        double viewWidth, double viewHeight, double rawWidth, double encodedFullWidth, double scale)
    {
        scale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        double Snap(double value) => Math.Round(value * scale) / scale;
        var (left, top) = (Math.Max(picture.Left, 0), Math.Max(picture.Top, 0));
        var (right, bottom) = (Math.Min(picture.Left + picture.Width, viewWidth), Math.Min(picture.Top + picture.Height, viewHeight));

        // Point/Point constructor: a picture fully off-player would yield a negative size, which Rect(x, y, w, h) rejects.
        var clip = right > left && bottom > top ? new Rect(new Point(Snap(left), Snap(top)), new Point(Snap(right), Snap(bottom))) : new Rect(0, 0, 0, 0);
        var room = right - WipePillEdge - (lineX + WipePillGap);
        return new(Snap(lineX - WipePillGap - rawWidth), Snap(lineX + WipePillGap), Snap(top + WipePillTop), !(encodedFullWidth <= room), clip);
    }

    /// <summary>
    /// Short ENCODED pill text when the full text does not fit: the part before the first " \u00B7 " ("ENCODED \u00B7 HEVC QSV \u00B7
    /// 12 Mbps" -> "ENCODED", "RAW WHILE PLAYING \u00B7 x264 veryslow" -> "RAW WHILE PLAYING"), "ENCODER FAILED" without its code, the others
    /// ("NO ENCODER", "NO DECODER", "NO LOOPBACK", "ENCODED") unchanged; a starting encoder shortens to "STARTING" (nothing encoded yet).
    /// The flyout and Export tab status keep the full text. Pure.
    /// </summary>
    internal static string ShortEncodedPillText(string text)
    {
        if (text.EndsWith(" \u00B7 STARTING", StringComparison.Ordinal))
            return "STARTING";
        var cut = text.IndexOf(" \u00B7 ", StringComparison.Ordinal);
        return cut > 0 ? text[..cut] : text.StartsWith("ENCODER FAILED", StringComparison.Ordinal) ? "ENCODER FAILED" : text;
    }

    /// <summary>Pill width for a text: TextBlock font metrics at a display scale plus padding and border.</summary>
    internal static double PillWidth(TextBlock words, string text, double pixelsPerDip) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(words.FontFamily, words.FontStyle, words.FontWeight, words.FontStretch), words.FontSize, Brushes.White, null,
            TextOptions.GetTextFormattingMode(words), pixelsPerDip > 0 && double.IsFinite(pixelsPerDip) ? pixelsPerDip : 1).WidthIncludingTrailingWhitespace + WipePillChrome;

    /// <summary>
    /// Applies a pill plan to real elements (static for goldens): positions, ENCODED full/short text, and the layer clip (frozen
    /// geometry rebuilt only when the rect changes). Never changes Visibility.
    /// </summary>
    internal static void ApplyWipePills(Canvas pills, Border raw, Border encoded, WipePillPlan plan, string encodedText)
    {
        var shown = plan.EncodedShort ? ShortEncodedPillText(encodedText) : encodedText;
        if (encoded.Child is TextBlock words && words.Text != shown)
            words.Text = shown;
        Canvas.SetLeft(raw, plan.RawLeft);
        Canvas.SetTop(raw, plan.Top);
        Canvas.SetLeft(encoded, plan.EncodedLeft);
        Canvas.SetTop(encoded, plan.Top);
        if (pills.Clip is not RectangleGeometry current || current.Rect != plan.Clip)
        {
            var clip = new RectangleGeometry(plan.Clip);
            clip.Freeze();
            pills.Clip = clip;
        }
    }

    /// <summary>
    /// Player size for wipe, Magnifier and Color Studio split: PlayerOverlay, whose DIPs map exactly to the native window's pixels (as
    /// VideoSurface.PointerMoved does). The surface's layout size is pre-rounding (1601 DIPs at 125 % = 2001 px, not 2001.25), an error
    /// that zoom would amplify past a pixel.
    /// </summary>
    public (double Width, double Height) LoopbackViewSize() => (_overlay.ActualWidth, _overlay.ActualHeight);

    // ----- Magnifier (pointer loupe) -----

    /// <summary>
    /// Attaches the pointer hook only while both preview and Magnifier are on (never before Frame 1; the preview always starts off);
    /// otherwise detaches and hides the engine loupe.
    /// </summary>
    private void SyncLoopbackLoupe()
    {
        var wanted = _viewModel.IsExportQualityPreview && _viewModel.ExportQualityPreviewLoupe;
        if (wanted != _loopbackPointerHooked)
        {
            _loopbackPointerHooked = wanted;
            if (wanted)
                _video.PointerMoved += LoopbackPointerMoved;
            else
                _video.PointerMoved -= LoopbackPointerMoved;
        }

        if (!wanted)
            _engine()?.SetLoopbackLoupe(null, null);
    }

    // Forwards the pointer (VideoView DIPs; null on leave) as output fractions via the zoomed picture rect; none when off-picture, in
    // 3D view, or during a wipe drag.
    private void LoopbackPointerMoved(Point? at)
    {
        if (_engine() is not { } engine)
            return;
        if (at is not { } point || _exploded() || _loopbackWipeHandle?.IsMouseCaptured == true)
        {
            engine.SetLoopbackLoupe(null, null);
            return;
        }

        var (width, height) = LoopbackViewSize();
        var fraction = GpuPreviewEngine.LoupePointOf(point.X, point.Y, width, height, engine.PictureRect(width, height));
        engine.SetLoopbackLoupe(fraction?.X, fraction?.Y);
    }
}
