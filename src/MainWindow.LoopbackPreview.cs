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
    private Border? _loopbackWipeHandle;
    private DispatcherTimer? _loopbackTimer;
    private GpuPreviewEngine? _loopbackEngine;
    private ExportLoopbackSettings? _loopbackSettings;
    private string? _loopbackWhyNot;
    private int _loopbackTicks;

    // The settings whose change sends the preview new ones (the Export tab's video members, EffectiveBitrateKbps the bitrate in force).
    private static readonly HashSet<string> LoopbackSettingNames = new(StringComparer.Ordinal)
    {
        "VideoEncoder", "RateControl", "TargetBitrate", "EffectiveBitrateKbps", "Crf", "EncoderPreset", "Framerate", "CustomFramerate", "TargetFileSize",
    };

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
            case { } name when LoopbackSettingNames.Contains(name):
                if (_viewModel.IsExportQualityPreview)
                    ApplyLoopback();
                break;
        }
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
            text = status.State switch
            {
                ExportLoopbackState.Running => string.Create(CultureInfo.InvariantCulture,
                    $"{codec}, {settings.RateControlName}: an approximation by {status.EncoderName}, {status.RoundTripMs:0} ms a round trip{(status.LateFrames > 0 ? $", {status.LateFrames} frames late" : "")}."),
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
    }

    // ----- The player bar's toggle and its flyout -----

    private void BuildLoopbackToggle()
    {
        if (_loopbackToggle is not null)
            return;

        // 5.4D: the eye (View), so it no longer shares Render Preview's glyph.
        _loopbackToggle = new ToggleButton
        {
            Content = LoopbackGlyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Padding = new Thickness(12, 7, 8, 7),
            Margin = new Thickness(3, 0, 0, 0),
            ToolTip = LoopbackName + ": the picture as the export's encoder would leave it, live (this PC's hardware encoder of the export's codec, at its rate control and bitrate). The arrow beside chooses Encoded, Split Wipe or the Artifact Heatmap x10, and the Magnifier.",
        };
        AutomationProperties.SetName(_loopbackToggle, LoopbackName);
        _loopbackToggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ViewModels.MainViewModel.IsExportQualityPreview)) { Mode = BindingMode.TwoWay });

        // An Audio Only file (5.4C) has no picture to preview: greyed out, as the Export tab's toggle is.
        _loopbackToggle.SetBinding(IsEnabledProperty, new Binding(nameof(ViewModels.MainViewModel.IsVideoExport)));
        ToolTipService.SetShowOnDisabled(_loopbackToggle, true);

        var more = new ToggleButton
        {
            Content = "\uE70E",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            Padding = new Thickness(5, 7, 5, 7),
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
            if (!_loopbackWipeHandle.IsMouseCaptured || _gpu is not { } engine || PlayerOverlay.ActualWidth < 1)
                return;
            var (left, width) = engine.PictureSpan(PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
            if (width > 1)
                _viewModel.ExportQualityPreviewWipe = Math.Clamp((e.GetPosition(PlayerOverlay).X - left) / width, 0, 1);
        };
        _loopbackWipeHandle.MouseLeftButtonUp += (_, e) =>
        {
            _loopbackWipeHandle.ReleaseMouseCapture();
            e.Handled = true;
        };

        _loopbackWipeLayer = new Canvas { ClipToBounds = true, Visibility = Visibility.Collapsed };
        _loopbackWipeLayer.Children.Add(_loopbackWipeHandle);
        PlayerOverlay.Children.Add(_loopbackWipeLayer);
        PlayerOverlay.SizeChanged += (_, _) => PlaceLoopbackWipe();
    }

    // The handle over the line: where the wipe stands across the picture as the player shows it; only in the wipe's mode.
    private void PlaceLoopbackWipe()
    {
        if (_loopbackWipeLayer is null || _loopbackWipeHandle is null)
            return;
        var shown = _viewModel.IsExportQualityPreview && _viewModel.ExportQualityPreviewMode == 1 && _loopbackSettings is not null && _gpu is not null
            && PlayerOverlay.ActualWidth > 1 && PlayerOverlay.ActualHeight > 1;
        _loopbackWipeLayer.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
            return;

        var (left, width) = _gpu!.PictureSpan(PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
        var x = left + Math.Clamp(_viewModel.ExportQualityPreviewWipe, 0, 1) * width;
        _loopbackWipeHandle.Height = PlayerOverlay.ActualHeight;
        Canvas.SetLeft(_loopbackWipeHandle, Math.Round(x - _loopbackWipeHandle.Width / 2));
        Canvas.SetTop(_loopbackWipeHandle, 0);
    }

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

        var (width, height) = (VideoView.ActualWidth, VideoView.ActualHeight);
        var fraction = GpuPreviewEngine.LoupePointOf(point.X, point.Y, width, height, engine.PictureRect(width, height));
        engine.SetLoopbackLoupe(fraction?.X, fraction?.Y);
    }
}
