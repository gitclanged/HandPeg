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
using HandPegApp.Services;

namespace HandPegApp;

// ----- Live Encoded Preview (Pass 5.4B, the Export Quality Preview): the player-bar toggle, the wipe, the Magnifier and the engine told -----
// The view model's IsExportQualityPreview (the player bar's toggle here, the Export tab's in its card) turns the GPU engine's encode
// loopback on with the export's settings (MainViewModel.CurrentLoopbackSettings -> GpuPreviewEngine.SetLoopback): the worker shows the
// frame as this PC's hardware encoder of the export's codec leaves it (ExportLoopbackPreview). 5.4D: the bar shows the toggle by
// default (PlaybackControlSet), so it is made in the first layout, before Frame 1: two buttons and an empty popup, its flyout made on
// first opening. The wipe's handle and the status timer exist only while the preview is on, and the Magnifier's pointer hook
// (VideoSurface.PointerMoved) only while the preview and the Magnifier are both on. The preview is turned off when the 3D view opens,
// when the player goes away and when HandPeg closes; it is never saved on.
public partial class MainWindow
{
    private ToggleButton? _loopbackToggle;
    private Popup? _loopbackFlyout;
    private RadioButton[]? _loopbackModes;
    private Canvas? _loopbackWipeLayer;

    // The player's zoom and pan undone for the wipe's layer (5.4.1A): PlayerOverlay carries the player's view matrix (ApplyPlayerView)
    // for the boxes drawn in the picture's own coordinates, while the wipe is placed in the player's (PictureSpan already has the zoom
    // and pan in it); without this the zoom moved the handle twice, and scaled it.
    private readonly MatrixTransform _loopbackWipeUnzoom = new();

    // The two sides named on the picture (5.4.1-M1): RAW left of the line, ENCODED right of it, never taking the pointer.
    private Border? _loopbackRawPill, _loopbackEncodedPill;

    // The wipe's divider, drawn here too (5.4.4): the worker draws it only once an encoded picture is back, so with an encoder that
    // failed or never answered the handle and pills stood over a picture with no line. Over the worker's own line, pixel for pixel.
    private Border? _loopbackDivider;

    // 5.5A: the pills never vanish. They sit in a layer of their own (a 0x0 Canvas, the handle and divider outside it), clipped to the
    // picture as the player shows it, so each slides off the picture's edge with the line; the ENCODED pill keeps its whole words
    // while they fit right of the line and its first word ("ENCODED") when they do not. 16: a pill's padding and border.
    private const double WipePillGap = 10, WipePillTop = 14, WipePillEdge = 4, WipePillChrome = 16;
    private Canvas? _loopbackPillLayer;

    // The ENCODED pill's whole words (SetEncodedPill), and their pill's width at the display scale it was measured at (never the
    // label's current text: once it can show the short words, that would flip between the two forms).
    private string _loopbackEncodedText = "ENCODED";
    private double _loopbackEncodedWidth = double.NaN, _loopbackEncodedWidthScale;
    private Border? _loopbackWipeHandle;
    private DispatcherTimer? _loopbackTimer;
    private GpuPreviewEngine? _loopbackEngine;
    private ExportLoopbackSettings? _loopbackSettings;
    private string? _loopbackWhyNot;
    private int _loopbackTicks;

    // The settings whose change sends the preview new ones (5.4.4-Final: MainViewModel.LoopbackSettingNames, every Export tab member that
    // shapes the encode), gathered for 25 ms (a box typed in, a slider dragged) and then sent at once: the paused frame is encoded again from
    // the scene the worker has, not drawn again (GpuPreviewEngine.SetLoopback).
    private DispatcherTimer? _loopbackSettingsTimer;
    private const int LoopbackSettingsGatherMs = 25;

    private void HookLoopback()
    {
        // Shown as the bar's settings say (PlaybackControlSet: a default since 5.4D); its content is made the first time it shows.
        ExportQualityPreviewBarSlot.SetBinding(VisibilityProperty, new Binding("PlaybackControls[ExportQualityPreview]") { Converter = (IValueConverter)FindResource("BoolToVisibility") });
        ExportQualityPreviewBarSlot.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                BuildLoopbackToggle();
        };
        _viewModel.PropertyChanged += LoopbackViewModel_PropertyChanged;
        ExplodedToggle.Checked += (_, _) =>
        {
            if (_viewModel.IsExportQualityPreview)
            {
                _viewModel.IsExportQualityPreview = false;
                _viewModel.StatusText = "Live Encoded Preview is off: the 3D Exploded View shows the layers, not the export.";
            }
        };
        VideoView.Unloaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // A dock laid out again moves the player (it is loaded again at once): only a player that stays gone turns the preview off.
            if (!VideoView.IsLoaded && _viewModel.IsExportQualityPreview)
                _viewModel.IsExportQualityPreview = false;
        });
        Closing += (_, _) => _viewModel.IsExportQualityPreview = false;
    }

    private void LoopbackViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModels.MainViewModel.IsExportQualityPreview):
                ApplyLoopback();
                break;
            case nameof(ViewModels.MainViewModel.ExportQualityPreviewMode):
            case nameof(ViewModels.MainViewModel.ExportQualityPreviewWipe):
                if (_viewModel.IsExportQualityPreview)
                {
                    _gpu?.SetLoopbackView(_viewModel.ExportQualityPreviewMode, _viewModel.ExportQualityPreviewWipe);
                    SyncLoopbackModes();
                    PlaceLoopbackWipe();
                }

                break;
            case nameof(ViewModels.MainViewModel.ExportQualityPreviewLoupe):
                SyncLoopbackLoupe();
                break;
            case nameof(ViewModels.MainViewModel.IsPlaying):
                // 5.5A: the ENCODED pill's words follow play and pause at once (a paused-only encoder's RAW WHILE PLAYING, its encoder
                // again when paused), not up to a second later on the status tick. Only the pill: the flyout's status keeps its tick.
                if (_viewModel.IsExportQualityPreview && _loopbackSettings is { } playSettings && _gpu is { } playEngine)
                    SetEncodedPill(EncodedPillText(playSettings, playEngine.LoopbackStatus, _viewModel.IsPlaying));
                break;
            case { } name when ViewModels.MainViewModel.LoopbackSettingNames.Contains(name):
                if (_viewModel.IsExportQualityPreview)
                    GatherLoopbackSettings();
                break;
        }
    }

    // A setting changed: sent 25 ms after the last of a run of changes (5.4.4-Final), never more than once a gather.
    private void GatherLoopbackSettings()
    {
        if (_loopbackSettingsTimer is null)
        {
            _loopbackSettingsTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(LoopbackSettingsGatherMs) };
            _loopbackSettingsTimer.Tick += (_, _) =>
            {
                _loopbackSettingsTimer.Stop();
                PushLoopbackSettings();
            };
        }

        _loopbackSettingsTimer.Stop();
        _loopbackSettingsTimer.Start();
    }

    /// <summary>
    /// The Export tab's settings as they are now, to the engine (5.4.4-Final): the worker encodes the paused frame again from the scene it drew
    /// (no layer drawn again), and the status and ENCODED pill say the new encoder and rate at once.
    /// </summary>
    private void PushLoopbackSettings()
    {
        if (!_viewModel.IsExportQualityPreview || _explodedUi)
            return;
        _loopbackSettings = _viewModel.CurrentLoopbackSettings(out _loopbackWhyNot);
        _gpu?.SetLoopback(_loopbackSettings);
        UpdateLoopbackStatus();
    }

    /// <summary>The preview's state to the engine: its settings (or off), what it shows, the wipe's handle and the status timer.</summary>
    private void ApplyLoopback()
    {
        if (!_viewModel.IsExportQualityPreview)
        {
            _gpu?.SetLoopback(null);
            (_loopbackEngine, _loopbackSettings, _loopbackWhyNot) = (null, null, null);
            _loopbackTimer?.Stop();
            if (_loopbackWipeLayer is not null)
                _loopbackWipeLayer.Visibility = Visibility.Collapsed;
            _viewModel.ExportQualityPreviewStatus = "";
            SyncLoopbackLoupe();
            return;
        }

        if (_explodedUi)
        {
            _viewModel.IsExportQualityPreview = false;
            _viewModel.StatusText = "Live Encoded Preview needs the flat picture: leave the 3D Exploded View first.";
            return;
        }

        _loopbackSettings = _viewModel.CurrentLoopbackSettings(out _loopbackWhyNot);
        _loopbackEngine = _gpu;
        _gpu?.SetLoopback(_loopbackSettings);
        _gpu?.SetLoopbackView(_viewModel.ExportQualityPreviewMode, _viewModel.ExportQualityPreviewWipe);
        EnsureLoopbackWipe();
        if (_loopbackTimer is null)
        {
            _loopbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _loopbackTimer.Tick += (_, _) => LoopbackTick();
        }

        _loopbackTicks = 0;
        _loopbackTimer.Start();
        PlaceLoopbackWipe();
        UpdateLoopbackStatus();
        SyncLoopbackLoupe();
    }

    // A quarter second: the handle follows the player's zoom and pan; once a second the status is read again. An engine started again
    // (a failure, Reload Player) is told the settings anew.
    private void LoopbackTick()
    {
        if (!_viewModel.IsExportQualityPreview)
        {
            _loopbackTimer?.Stop();
            return;
        }

        if (!ReferenceEquals(_loopbackEngine, _gpu))
        {
            _loopbackEngine = _gpu;
            _gpu?.SetLoopback(_loopbackSettings);
            _gpu?.SetLoopbackView(_viewModel.ExportQualityPreviewMode, _viewModel.ExportQualityPreviewWipe);
            _gpu?.SetLoopbackLoupe(null, null); // the next move over the player places it again
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
        else if (_gpu is not { } engine)
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
        SetEncodedPill(settings is null ? "NO LOOPBACK" : _gpu is not { } shown ? "ENCODED" : EncodedPillText(settings, shown.LoopbackStatus, _viewModel.IsPlaying));
    }

    /// <summary>
    /// The ENCODED pill's words (5.4.4-Final, the user's: the encoder and its rate, "ENCODED \u00B7 HEVC QSV \u00B7 12 Mbps", "ENCODED \u00B7 x264 veryslow \u00B7 CRF 22");
    /// a stand-in on another card says so; a software encoder while playing says the picture is the plain one; else the state. Pure, for the golden test.
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

    // The ENCODED pill names a failure too (5.4.4): what the flyout's status says, where the encoded picture should be. 5.5A: compared
    // with the whole words kept (the label may show the short ones), and placed again at once, so new words never run past the picture
    // for the 250 ms until the next placing. With no wipe yet, EnsureLoopbackWipe makes the pill with these words.
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

        // 5.4D: the eye (View), the preview's own glyph (5.4.1A: the one canonical encode preview; Render Preview is retired). 5.4.4: the
        // row's own toggle look (TransportRow's BarToggle: Segoe MDL2 13, as tall as the Timeline's tools), it and its arrow one pair.
        var barToggle = (Style)TransportRow.Resources["BarToggle"];
        _loopbackToggle = new ToggleButton
        {
            Style = barToggle,
            Content = LoopbackGlyph,
            Padding = new Thickness(7, 4, 5, 4),
            Margin = new Thickness(3, 0, 0, 0),
            ToolTip = LoopbackName + ": the picture as the export's encoder would leave it, live (this PC's hardware encoder of the export's codec, at its rate control and bitrate). The arrow beside chooses Encoded, Split Wipe or the Artifact Heatmap x10, and the Magnifier.",
        };
        AutomationProperties.SetName(_loopbackToggle, LoopbackName);
        _loopbackToggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ViewModels.MainViewModel.IsExportQualityPreview)) { Mode = BindingMode.TwoWay });

        // An Audio Only file (5.4C) has no picture to preview: greyed out, as the Export tab's toggle is.
        _loopbackToggle.SetBinding(IsEnabledProperty, new Binding(nameof(ViewModels.MainViewModel.IsVideoExport)));
        ToolTipService.SetShowOnDisabled(_loopbackToggle, true);

        // The arrow: a smaller glyph, padded to the toggle's height (9 + 6 + 6 = 13 + 4 + 4).
        var more = new ToggleButton
        {
            Style = barToggle,
            Content = "\uE70E",
            FontSize = 9,
            Padding = new Thickness(4, 6, 4, 6),
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = LoopbackName + " options: what is shown, the Magnifier, and what the preview is doing.",
        };
        AutomationProperties.SetName(more, LoopbackName + " Options");

        // Law 1 (5.4D): the popup is made empty; its flyout is made the first time it opens.
        var flyout = _loopbackFlyout = new Popup { StaysOpen = false, AllowsTransparency = true };
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
        ExportQualityPreviewBarSlot.Content = row;
        AutomationProperties.SetName(ExportQualityPreviewBarSlot, LoopbackName);

        // In the bar's More menu when the row has no room for it.
        var before = TransportCenter.MenuEntryFor;
        TransportCenter.MenuEntryFor = child => ReferenceEquals(child, ExportQualityPreviewBarSlot)
            ? (_viewModel.IsExportQualityPreview ? LoopbackName + " (On)" : LoopbackName, () => _viewModel.IsExportQualityPreview = !_viewModel.IsExportQualityPreview)
            : before?.Invoke(child);
    }

    /// <summary>The preview's name wherever it is shown (5.4D: the user's "Live Encoded Preview"), and its glyph, Segoe MDL2's View.</summary>
    internal const string LoopbackName = "Live Encoded Preview", LoopbackGlyph = "\uE890";

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

        // 5.4D (Q10 b): the loupe follows the pointer over the picture, as it does while an edge slider is held.
        var magnifier = new CheckBox
        {
            Content = "Magnifier (Loupe)",
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "A loupe at 400 % in the picture's far corner, round the point under the pointer: the encoded picture (or the difference) close up. Kept for next time.",
        };
        magnifier.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ViewModels.MainViewModel.ExportQualityPreviewLoupe)) { Mode = BindingMode.TwoWay });
        panel.Children.Add(magnifier);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(ViewModels.MainViewModel.ExportQualityPreviewStatus)));
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
        if (TryFindResource("FlyoutChrome") is Style style)
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

        // A grip in the middle of the line the worker draws; the line itself is in the picture. The layer takes nothing but the handle.
        var grip = new Border
        {
            Width = 18,
            Height = 34,
            CornerRadius = new CornerRadius(4),
            Background = Frozen(Color.FromArgb(0xD8, 0x1E, 0x1E, 0x22)),
            BorderBrush = Frozen(Color.FromArgb(0xC0, 0xF2, 0xF2, 0xF2)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = "\uE76F", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _loopbackWipeHandle = new Border { Width = 18, Background = Frozen(Color.FromArgb(0x01, 0, 0, 0)), Cursor = Cursors.SizeWE, Child = grip, ToolTip = "Drag: the source on the left, the encoded picture on the right." };
        AutomationProperties.SetName(_loopbackWipeHandle, LoopbackName + " Wipe");
        _loopbackWipeHandle.MouseLeftButtonDown += (_, e) =>
        {
            _loopbackWipeHandle.CaptureMouse();
            _gpu?.SetLoopbackLoupe(null, null); // no Magnifier while the wipe is dragged
            e.Handled = true;
        };
        _loopbackWipeHandle.MouseMove += (_, e) =>
        {
            // The same span and formula as the handle's place and the worker's line (5.4.1A, GpuPreviewEngine.WipeAt / WipeLineX).
            var (viewWidth, viewHeight) = LoopbackViewSize();
            if (!_loopbackWipeHandle.IsMouseCaptured || _gpu is not { } engine || viewWidth < 1)
                return;
            var span = engine.PictureSpan(viewWidth, viewHeight);
            if (span.Width > 1 && _loopbackWipeLayer is not null)
                _viewModel.ExportQualityPreviewWipe = GpuPreviewEngine.WipeAt(e.GetPosition(_loopbackWipeLayer).X, span);
        };
        _loopbackWipeHandle.MouseLeftButtonUp += (_, e) =>
        {
            _loopbackWipeHandle.ReleaseMouseCapture();
            e.Handled = true;
        };

        _loopbackWipeLayer = new Canvas { ClipToBounds = true, Visibility = Visibility.Collapsed, RenderTransform = _loopbackWipeUnzoom };
        _loopbackRawPill = WipePill("RAW");
        _loopbackEncodedPill = WipePill(_loopbackEncodedText);

        // 5.5A: the pills' own layer, a 0x0 Canvas at the layer's origin: never ClipToBounds (that would clip everything), only the
        // picture's Clip (ApplyWipePills), so the handle and the divider are not clipped with them. Z-order: divider, pills, handle.
        _loopbackPillLayer = new Canvas { IsHitTestVisible = false };
        _loopbackPillLayer.Children.Add(_loopbackRawPill);
        _loopbackPillLayer.Children.Add(_loopbackEncodedPill);
        _loopbackDivider = new Border { IsHitTestVisible = false, Background = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2)) };
        _loopbackWipeLayer.Children.Add(_loopbackDivider);
        _loopbackWipeLayer.Children.Add(_loopbackPillLayer);
        _loopbackWipeLayer.Children.Add(_loopbackWipeHandle);
        PlayerOverlay.Children.Add(_loopbackWipeLayer);
        PlayerOverlay.SizeChanged += (_, _) => PlaceLoopbackWipe();
    }

    // The handle over the line: where the wipe stands across the picture as the player shows it; only in the wipe's mode. 5.4.1A: the
    // line's place is the worker's own formula (WipeLineX over PictureSpan, which is SceneRenderer.Letterbox), and the handle's edge is
    // rounded to a device pixel, not a DIP (1.5 pixels at 150 %), so its middle stays on the 2-pixel line.
    private void PlaceLoopbackWipe()
    {
        if (_loopbackWipeLayer is null || _loopbackWipeHandle is null)
            return;
        var (viewWidth, viewHeight) = LoopbackViewSize();
        var shown = _viewModel.IsExportQualityPreview && _viewModel.ExportQualityPreviewMode == 1 && _loopbackSettings is not null && _gpu is not null
            && viewWidth > 1 && viewHeight > 1;
        _loopbackWipeLayer.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
            return;

        var view = PlayerOverlay.RenderTransform?.Value ?? Matrix.Identity;
        if (view.HasInverse)
            view.Invert();
        else
            view = Matrix.Identity;
        _loopbackWipeUnzoom.Matrix = view;

        var x = GpuPreviewEngine.WipeLineX(_viewModel.ExportQualityPreviewWipe, _gpu!.PictureSpan(viewWidth, viewHeight));
        var scale = VisualTreeHelper.GetDpi(PlayerOverlay).DpiScaleX;
        scale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        _loopbackWipeHandle.Height = PlayerOverlay.ActualHeight;
        Canvas.SetLeft(_loopbackWipeHandle, Math.Round((x - _loopbackWipeHandle.Width / 2) * scale) / scale);
        Canvas.SetTop(_loopbackWipeHandle, 0);

        // The pills: 10 DIPs either side of the line, 14 below the picture's top (or the player's, the picture zoomed past it).
        var picture = _gpu.PictureRect(viewWidth, viewHeight);
        var (_, top, _, height) = picture;

        // The divider (5.4.4): always with the handle, 2 device pixels wide on the worker's line, the picture's height within the player.
        if (_loopbackDivider is { } divider)
        {
            var (from, to) = (Math.Max(top, 0), Math.Min(top + height, viewHeight));
            divider.Width = 2 / scale;
            divider.Height = Math.Max(to - from, 0);
            Canvas.SetLeft(divider, (Math.Round(x * scale) - 1) / scale);
            Canvas.SetTop(divider, from);
        }
        // 5.5A: both pills always shown with the layer, slid off the picture's edge by its clip (the picture within the player); the
        // ENCODED pill's whole words measured from the words kept (5.4.4-Final: the encoder and its rate), shortened where they would
        // pass the picture's visible right edge.
        if (_loopbackRawPill is { } raw && _loopbackEncodedPill is { Child: TextBlock words } encoded && _loopbackPillLayer is { } pills)
        {
            raw.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (double.IsNaN(_loopbackEncodedWidth) || _loopbackEncodedWidthScale != scale)
                (_loopbackEncodedWidth, _loopbackEncodedWidthScale) = (PillWidth(words, _loopbackEncodedText, scale), scale);
            ApplyWipePills(pills, raw, encoded, PlanWipePills(x, picture, viewWidth, viewHeight, raw.DesiredSize.Width, _loopbackEncodedWidth, scale), _loopbackEncodedText);
        }
    }

    // A small dark pill naming one side of the wipe (5.4.1-M1): semi-bold caps, no hit testing. Internal for the golden (5.5A).
    internal static Border WipePill(string text) => new()
    {
        IsHitTestVisible = false,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 2, 7, 2),
        BorderThickness = new Thickness(1),
        Background = Frozen(Color.FromArgb(0xCC, 0x18, 0x18, 0x1C)),
        BorderBrush = Frozen(Color.FromRgb(0x38, 0x38, 0x42)),
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2)) },
    };

    /// <summary>Where the wipe's pills go (5.5A): RAW's and ENCODED's left edges, their top, whether ENCODED shows its short words, and
    /// the pills' clip (the picture within the player). Every value is on a device pixel.</summary>
    internal readonly record struct WipePillPlan(double RawLeft, double EncodedLeft, double Top, bool EncodedShort, Rect Clip);

    /// <summary>
    /// The pills placed for a wipe line at <paramref name="lineX"/> (5.5A). Pure, for the golden. <paramref name="picture"/> is
    /// GpuPreviewEngine.PictureRect (the overlay's un-zoomed DIPs, the zoom and pan in it); RAW ends <see cref="WipePillGap"/> left of the
    /// line and ENCODED starts as far right of it, both always placed (never hidden): the clip, the picture within the player, slides
    /// them off its edges. ENCODED keeps its whole words while they fit between the line and the picture's visible right edge (the
    /// player's, the picture zoomed past it), less <see cref="WipePillEdge"/>; else the short ones.
    /// </summary>
    internal static WipePillPlan PlanWipePills(double lineX, (double Left, double Top, double Width, double Height) picture,
        double viewWidth, double viewHeight, double rawWidth, double encodedFullWidth, double scale)
    {
        scale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        double Snap(double value) => Math.Round(value * scale) / scale;
        var (left, top) = (Math.Max(picture.Left, 0), Math.Max(picture.Top, 0));
        var (right, bottom) = (Math.Min(picture.Left + picture.Width, viewWidth), Math.Min(picture.Top + picture.Height, viewHeight));

        // The Point/Point form: a picture wholly off the player would give a negative size, which Rect refuses.
        var clip = right > left && bottom > top ? new Rect(new Point(Snap(left), Snap(top)), new Point(Snap(right), Snap(bottom))) : new Rect(0, 0, 0, 0);
        var room = right - WipePillEdge - (lineX + WipePillGap);
        return new(Snap(lineX - WipePillGap - rawWidth), Snap(lineX + WipePillGap), Snap(top + WipePillTop), !(encodedFullWidth <= room), clip);
    }

    /// <summary>
    /// The ENCODED pill's words where the whole ones do not fit (5.5A): the words before the first " \u00B7 " ("ENCODED \u00B7 HEVC QSV \u00B7
    /// 12 Mbps" -> "ENCODED", "RAW WHILE PLAYING \u00B7 x264 veryslow" -> "RAW WHILE PLAYING"), "ENCODER FAILED" without its code, the others
    /// ("NO ENCODER", "NO DECODER", "NO LOOPBACK", "ENCODED") unchanged. 5.5A review: an encoder still starting shortens to "STARTING", not
    /// "ENCODED" (nothing is encoded yet: the right side is the plain picture). The flyout and the Export tab's status keep the whole story. Pure.
    /// </summary>
    internal static string ShortEncodedPillText(string text)
    {
        if (text.EndsWith(" \u00B7 STARTING", StringComparison.Ordinal))
            return "STARTING";
        var cut = text.IndexOf(" \u00B7 ", StringComparison.Ordinal);
        return cut > 0 ? text[..cut] : text.StartsWith("ENCODER FAILED", StringComparison.Ordinal) ? "ENCODER FAILED" : text;
    }

    /// <summary>A pill's width for these words (5.5A): its TextBlock's own font at a display scale, plus the pill's padding and border.</summary>
    internal static double PillWidth(TextBlock words, string text, double pixelsPerDip) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(words.FontFamily, words.FontStyle, words.FontWeight, words.FontStretch), words.FontSize, Brushes.White, null,
            TextOptions.GetTextFormattingMode(words), pixelsPerDip > 0 && double.IsFinite(pixelsPerDip) ? pixelsPerDip : 1).WidthIncludingTrailingWhitespace + WipePillChrome;

    /// <summary>
    /// The plan applied to the pills (5.5A; static, for a golden on real elements): their places, ENCODED's whole or short words, and
    /// the pills' layer clipped to the plan's rectangle (a frozen geometry, made again only when the rectangle moves: a zoom, a resize).
    /// Never touches Visibility: the pills show whenever the wipe's layer does.
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

    // The player's size for the wipe and the Magnifier alike (5.4.1A: one element for both): the layer over the video (PlayerOverlay),
    // whose window is the native window's pixels exactly, so its DIPs are the worker's output pixels over the display's scale, as the
    // pointer's are (VideoSurface.PointerMoved); the surface's own layout size is those pixels before rounding (1601 DIPs at 125 % are
    // 2001 pixels, not 2001.25), which a zoomed picture would carry to well over a pixel.
    private (double Width, double Height) LoopbackViewSize() => (PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);

    // ----- The Magnifier (5.4D, Q10 b): the loupe round the point under the pointer -----

    private bool _loopbackPointerHooked;

    /// <summary>
    /// The pointer hook on while the preview and its Magnifier are both on (nothing is listened to otherwise, and nothing before Frame 1:
    /// both start off but for the saved Magnifier, and the preview is never on at a start); off, the engine's loupe put away.
    /// </summary>
    private void SyncLoopbackLoupe()
    {
        var wanted = _viewModel.IsExportQualityPreview && _viewModel.ExportQualityPreviewLoupe;
        if (wanted != _loopbackPointerHooked)
        {
            _loopbackPointerHooked = wanted;
            if (wanted)
                VideoView.PointerMoved += LoopbackPointerMoved;
            else
                VideoView.PointerMoved -= LoopbackPointerMoved;
        }

        if (!wanted)
            _gpu?.SetLoopbackLoupe(null, null);
    }

    // The pointer over the player (VideoView's DIPs, null: it left) to the engine as fractions of the output, through the picture's
    // rectangle as the player shows it (its zoom and pan): none off the picture, in the 3D view, or while the wipe is dragged.
    private void LoopbackPointerMoved(Point? at)
    {
        if (_gpu is not { } engine)
            return;
        if (at is not { } point || _explodedUi || _loopbackWipeHandle?.IsMouseCaptured == true)
        {
            engine.SetLoopbackLoupe(null, null);
            return;
        }

        var (width, height) = LoopbackViewSize();
        var fraction = GpuPreviewEngine.LoupePointOf(point.X, point.Y, width, height, engine.PictureRect(width, height));
        engine.SetLoopbackLoupe(fraction?.X, fraction?.Y);
    }
}
