using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Player context menu -----
// A stationary right-click on the picture (within the system drag distance) opens frame and view actions: Export Frame as Image...,
// Copy Frame to Clipboard, Ping-to-Mark Highlight Here (at the click point), then Full Screen Preview (F11), 3D Exploded View, Live
// Encoded Preview, Safe Margins and Reset Player Zoom & Pan, then the layer items (Pass 5.6.5): Find Layer on Timeline (the clip under
// the pointer) and Social Safe Zones. PlayerOverlay (with PlayerHitBrush) receives the input, so this is a standard WPF menu built on
// each open (nothing before Frame 1). The Find Layer X-Ray pill, the Social Safe Zones guide and in-place text editing live here too.
public partial class MainWindow
{
    // Overlay position of a pending right press; null when none.
    private Point? _playerRightPressAt;

    private void HookPlayerContextMenu()
    {
        PlayerOverlay.PreviewMouseRightButtonDown += (_, e) => _playerRightPressAt = e.GetPosition(PlayerOverlay);
        PlayerOverlay.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (_playerRightPressAt is not { } pressed)
                return;
            _playerRightPressAt = null;
            var at = e.GetPosition(PlayerOverlay);
            if (!IsStationaryClick(pressed, at, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
                return;
            e.Handled = true;
            OpenPlayerContextMenu(at);
        };
    }

    /// <summary>True when press and release stay within the drag distance on both axes (a click, not a drag). Pure; golden-tested.</summary>
    internal static bool IsStationaryClick(Point pressed, Point released, double dragX, double dragY) =>
        Math.Abs(released.X - pressed.X) <= dragX && Math.Abs(released.Y - pressed.Y) <= dragY;

    /// <summary>Player context menu labels in order (null = separator); read by the golden test.</summary>
    internal static readonly string?[] PlayerMenuItems =
    [
        "Export Frame as Image...", "Copy Frame to Clipboard", "Ping-to-Mark Highlight Here", null,
        "Full Screen Preview", "3D Exploded View", "Live Encoded Preview", "Safe Margins", "Reset Player Zoom & Pan",
    ];

    private void OpenPlayerContextMenu(Point at)
    {
        var menu = new ContextMenu { PlacementTarget = PlayerOverlay, Placement = PlacementMode.MousePoint };
        var hasPicture = _gpu is not null && _viewModel.HasSource;
        var focal = PictureFocalAt(at);
        MenuItem Add(string header, Action action, bool enabled = true, bool? check = null, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
            if (check is { } on)
                (item.IsCheckable, item.IsChecked) = (true, on);
            item.Click += (_, _) => RunGuarded(header, action);
            menu.Items.Add(item);
            return item;
        }

        Add(PlayerMenuItems[0]!, ExportFrame, hasPicture);
        Add(PlayerMenuItems[1]!, CopyFrameToClipboard, hasPicture);
        Add(PlayerMenuItems[2]!, () => _viewModel.PingToMark(focal!.Value.X, focal.Value.Y), hasPicture && focal is not null && !_explodedUi);
        menu.Items.Add(new Separator());
        Add(PlayerMenuItems[4]!, _fullscreen.Toggle, _fullscreen.IsOpen || VideoView.IsVisible, check: _fullscreen.IsOpen, gesture: "F11");
        Add(PlayerMenuItems[5]!, () => ExplodedToggle.IsChecked = ExplodedToggle.IsChecked != true, ExplodedToggle.IsEnabled && _gpu is not null, check: ExplodedToggle.IsChecked == true);
        Add(PlayerMenuItems[6]!, () => _viewModel.IsExportQualityPreview = !_viewModel.IsExportQualityPreview, _gpu is not null, check: _viewModel.IsExportQualityPreview);
        Add(PlayerMenuItems[7]!, () => _viewModel.ShowSafeMargins = !_viewModel.ShowSafeMargins, check: _viewModel.ShowSafeMargins);
        Add(PlayerMenuItems[8]!, ResetPlayerView, _playerZoom != 0 || _playerPanX != 0 || _playerPanY != 0);
        menu.Items.Add(new Separator());
        var found = _explodedUi ? null : LayerUnder(at);
        Add(PlayerLayerMenuItems[0], () => FindLayerOnTimeline(found!), found is not null);
        Add(PlayerLayerMenuItems[1], ToggleSocialGuide, _viewModel.FrameWidth > 0, check: _socialGuideShown);
        menu.IsOpen = true;
    }

    /// <summary>Resets player zoom and pan to fit-centred; the Canvas view is untouched.</summary>
    private void ResetPlayerView()
    {
        (_playerZoom, _playerPanX, _playerPanY) = (0, 0, 0);
        if (_playerView is { } player)
            player.Matrix = Matrix.Identity;
        _gpu?.SetView(0, 0, 0);
        _loopback.PlaceLoopbackWipe();
        _viewModel.StatusText = "Player zoom and pan reset.";
    }

    /// <summary>Copies the displayed composite frame (as Export Frame saves it) to the clipboard as an image.</summary>
    private void CopyFrameToClipboard() => Faults.Forget(CopyFrameToClipboardAsync(), nameof(CopyFrameToClipboard));

    private async Task CopyFrameToClipboardAsync()
    {
        if (_gpu is not { } engine)
            return;
        if (await engine.CaptureFrameAsync() is not { } frame || frame.Width <= 0 || frame.Height <= 0)
        {
            _viewModel.StatusText = "The frame could not be read from the GPU engine.";
            return;
        }

        var (bgra, width, height) = frame;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        bitmap.Freeze();
        try
        {
            Clipboard.SetImage(bitmap);
            _viewModel.StatusText = $"Frame copied to the clipboard ({width} x {height}).";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard locked by another process (CLIPBRD_E_CANT_OPEN): report via status, never throw.
            AppLog.Write("Copy Frame to Clipboard", ex);
            _viewModel.StatusText = "The clipboard is busy (another program has it open): try Copy Frame again.";
        }
    }

    // ----- Viewport layer items (Pass 5.6.5) -----

    /// <summary>Layer items after the view items, in order (Find Layer on Timeline, Social Safe Zones); read by the golden test.</summary>
    internal static readonly string[] PlayerLayerMenuItems = ["Find Layer on Timeline", "Social Safe Zones"];

    /// <summary>
    /// The topmost drawn picture clip under an overlay point: by the player's hit boxes (masks honoured) while they are up, else by the
    /// clips' boxes on the frame; null on empty picture.
    /// </summary>
    private Layer? LayerUnder(Point at)
    {
        for (var i = _playerBoxes.Count - 1; i >= 0; i--)
        {
            var (box, clip, _) = _playerBoxes[i];
            var point = PlayerOverlay.TranslatePoint(at, box);
            if (!clip.IsCaptions && LayerBox.Holds(box, point) && IsDrawnNow(clip) && LayerBox.Shows(box, point))
                return clip;
        }

        if (PictureFocalAt(at) is not { } focal || _viewModel.FrameWidth <= 0 || _viewModel.FrameHeight <= 0)
            return null;

        var clips = PlayerClips();
        for (var i = clips.Count - 1; i >= 0; i--)
        {
            if (clips[i].IsCaptions)
                continue;
            var visible = VisibleRectOf(clips[i], (double)TrackLayerRect(clips[i]).X / _viewModel.FrameWidth, (double)TrackLayerRect(clips[i]).Y / _viewModel.FrameHeight);
            if (new Rect(visible.X, visible.Y, visible.Width, visible.Height).Contains(focal))
                return clips[i];
        }

        return null;
    }

    /// <summary>
    /// Viewport right-click Find Layer on Timeline: selects the clip under the pointer (the Timeline follows its selection) and, when the
    /// playhead is off the clip, puts the playhead on its first frame.
    /// </summary>
    private void FindLayerOnTimeline(Layer layer)
    {
        SelectClip(layer);
        var at = _viewModel.PositionMs / 1000;
        var (start, end) = layer.GetSpan(_viewModel.SequenceSeconds);
        if (at < start - 0.0005 || at >= end)
            _viewModel.PositionMs = Math.Max(start, 0) * 1000;
        _viewModel.StatusText = $"{layer.Name} found on the Timeline.";
    }

    // ----- Social Safe Zones: the 9:16 Social UI Ghost Guide (Pass 5.6.5) -----
    // Toggled from the player menu (this session); drawn over the picture (zooming with it) and on the Canvas pane. Made on first use.

    private bool _socialGuideShown;
    private SocialGhostGuide? _playerSocialGuide;

    private void ToggleSocialGuide()
    {
        _socialGuideShown = !_socialGuideShown;
        PlaceSocialGuide();
        if (_viewModel.IsArrangeActive)
            RedrawLayout();
        _viewModel.StatusText = _socialGuideShown ? "Social Safe Zones shown: keep HUD Cutouts out of the shaded areas." : "Social Safe Zones hidden.";
    }

    /// <summary>Shows the player's ghost guide over the letterboxed output frame (hidden when off); made and hooked on first show.</summary>
    private void PlaceSocialGuide()
    {
        if (!_socialGuideShown && _playerSocialGuide is null)
            return;

        if (_playerSocialGuide is null)
        {
            _playerSocialGuide = new SocialGhostGuide();
            var after = PlayerOverlay.Children.IndexOf(SafeMarginsCanvas);
            PlayerOverlay.Children.Insert(after < 0 ? PlayerOverlay.Children.Count : after + 1, _playerSocialGuide);
            PlayerOverlay.SizeChanged += (_, _) => PlaceSocialGuide();
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.FrameWidth) or nameof(MainViewModel.FrameHeight))
                    PlaceSocialGuide();
            };
        }

        _playerSocialGuide.Visibility = _socialGuideShown ? Visibility.Visible : Visibility.Collapsed;
        var (width, height) = (PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
        var (frameWidth, frameHeight) = ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight);
        if (width <= 0 || height <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            _playerSocialGuide.Frame = Rect.Empty;
            return;
        }

        // The output frame as the GPU engine fits it (as PictureFocalAt and the player's boxes).
        var scale = Math.Min(width / frameWidth, height / frameHeight);
        _playerSocialGuide.Frame = new Rect((width - frameWidth * scale) / 2, (height - frameHeight * scale) / 2, frameWidth * scale, frameHeight * scale);
    }

    // ----- Find Layer X-Ray pill (Pass 5.6.5) -----
    // "Why can't I see or hear this?": while paused on the flat player, an explicitly selected picture clip that does not show (off the
    // playhead, hidden, 0% opacity, off the frame, covered by an opaque picture above) or is muted gets a one-click pill at the top of the
    // player naming the issue, with its fix (LayerXRay). Made on first need, never before Frame 1.

    private Border? _xrayPill;
    private TextBlock? _xrayText;
    private Button? _xrayFix;
    private LayerIssue _xrayIssue;
    private Layer? _xrayCover, _xrayWatched;

    /// <summary>Re-diagnoses the selected clip and shows, updates or hides the pill.</summary>
    private void UpdateLayerXRay()
    {
        var layer = !_viewModel.IsPlaying && !_explodedUi && _viewModel.HasSource && _viewModel.FrameWidth > 0 && _viewModel.FrameHeight > 0
            && _selectedClip is Layer { IsCaptions: false, IsAudio: false, IsDeleted: false } picked && picked.IsPicture ? picked : null;
        WatchXRay(layer);
        (_xrayIssue, _xrayCover) = layer is null ? (LayerIssue.None, null) : DiagnoseLayer(layer);
        if (_xrayIssue == LayerIssue.None)
        {
            if (_xrayPill is not null)
                _xrayPill.Visibility = Visibility.Collapsed;
            return;
        }

        var (text, fix) = LayerXRay.Words(_xrayIssue);
        MakeXRayPill();
        (_xrayText!.Text, _xrayFix!.Content) = (text, fix);
        _xrayFix.ToolTip = $"{fix} [{layer!.Name}]";
        _xrayPill!.Visibility = Visibility.Visible;
    }

    // Follows the diagnosed clip's own changes (opacity, hiding, place) while it is the one diagnosed.
    private void WatchXRay(Layer? layer)
    {
        if (ReferenceEquals(layer, _xrayWatched))
            return;
        if (_xrayWatched is { } old)
            old.PropertyChanged -= OnXRayLayerChanged;
        _xrayWatched = layer;
        if (layer is not null)
            layer.PropertyChanged += OnXRayLayerChanged;
    }

    // One re-diagnosis per burst of changes (a drag writes many), after them.
    private bool _xrayQueued;

    private void OnXRayLayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_xrayQueued)
            return;
        _xrayQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _xrayQueued = false;
            UpdateLayerXRay();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>The selected clip's first issue at the playhead, and the opaque picture covering it (topmost) when that is the issue.</summary>
    private (LayerIssue Issue, Layer? Cover) DiagnoseLayer(Layer layer)
    {
        var at = _viewModel.PositionMs / 1000;
        var (start, end) = layer.GetSpan(_viewModel.SequenceSeconds);
        var atPlayhead = at >= start - 0.0005 && at < end;
        var hidden = layer.IsHidden || _viewModel.IsTrackHidden(layer);
        var (x, y, _, _) = TrackLayerRect(layer);
        var visible = VisibleRectOf(layer, (double)x / _viewModel.FrameWidth, (double)y / _viewModel.FrameHeight);
        var box = new Rect(visible.X, visible.Y, Math.Max(visible.Width, 0), Math.Max(visible.Height, 0));
        var cover = atPlayhead && !hidden && LayerXRay.OnFrame(box) ? CoverOf(layer, box, at) : null;

        // Keyed opacity is the user's animation, not an accident: only a still 0% is an issue.
        var opacity = layer.AnimatesOpacity ? 100 : layer.Opacity;
        return (LayerXRay.Diagnose(atPlayhead, hidden, opacity, box, cover is not null, layer.HasAudio, layer.IsSoundMuted), cover);
    }

    /// <summary>
    /// The topmost picture above <paramref name="layer"/> on another track that is drawn now and certainly opaque over all of its visible box:
    /// a plain video (no mask, key, effect or opacity change), flat and unrotated, whose box covers the clip's.
    /// </summary>
    private Layer? CoverOf(Layer layer, Rect box, double at)
    {
        var (frameWidth, frameHeight) = ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight);
        var above = false;
        Layer? cover = null;
        foreach (var upper in _viewModel.Layers)
        {
            if (ReferenceEquals(upper, layer))
            {
                above = true;
                continue;
            }

            if (!above || upper.TrackId == layer.TrackId || !upper.IsVideoFile || upper.IsHidden || upper.IsDeleted || upper.CustomMask || upper.ChromaKey
                || upper.Opacity < 100 || upper.AnimatesOpacity || upper.Effects.Count > 0 || DrawnTilted(upper)
                || (upper.HasFilters && Math.Abs(upper.Rotation % 360) >= 0.05) || !_viewModel.IsPictureDrawn(upper, at))
                continue;

            var (x, y, width, height) = TrackLayerRect(upper);
            if (LayerXRay.Covers(new Rect(x / frameWidth, y / frameHeight, width / frameWidth, height / frameHeight), box))
                cover = upper;
        }

        return cover;
    }

    // The pill: issue text and its fix, top centre of the player (not zoomed), on the inspector card surface.
    private void MakeXRayPill()
    {
        if (_xrayPill is not null)
            return;

        var icon = new TextBlock { Text = char.ConvertFromUtf32(0xE7BA), FontFamily = Glyphs.Font, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush");
        _xrayText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _xrayText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _xrayFix = new Button { Padding = new Thickness(10, 2, 10, 3), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _xrayFix.Click += (_, _) => RunGuarded("Find Layer", FixLayerIssue);
        System.Windows.Automation.AutomationProperties.SetName(_xrayFix, "Fix Layer Issue");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, _xrayText, _xrayFix } };
        _xrayPill = new Border
        {
            Child = row, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(12, 4, 4, 4),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _xrayPill.SetResourceReference(Border.BackgroundProperty, "InspectorCardBrush");
        _xrayPill.SetResourceReference(Border.BorderBrushProperty, "InspectorCardBorderBrush");
        System.Windows.Automation.AutomationProperties.SetName(_xrayPill, "Find Layer X-Ray");
        PlayerHud.Children.Add(_xrayPill);
    }

    /// <summary>The pill's one-click fix for the selected clip's issue (one Undo step for a change).</summary>
    private void FixLayerIssue()
    {
        if (_selectedClip is not Layer layer)
            return;

        switch (_xrayIssue)
        {
            case LayerIssue.NotAtPlayhead:
                _viewModel.PositionMs = Math.Max(layer.GetSpan(_viewModel.SequenceSeconds).Start, 0) * 1000;
                break;
            case LayerIssue.Hidden:
                _viewModel.ToggleHidden(layer);
                break;
            case LayerIssue.Transparent:
                _viewModel.Checkpoint($"set {layer.Name} to 100% opacity");
                layer.Opacity = 100;
                break;
            case LayerIssue.OffCanvas:
                _viewModel.Checkpoint($"center {layer.Name}");
                CenterTrackLayer(layer);
                break;
            case LayerIssue.Covered when _xrayCover is { } cover:
                SelectClip(cover);
                break;
            case LayerIssue.Muted:
                _viewModel.SetClipsMuted([layer], false);
                break;
        }

        UpdateLayerXRay();
    }

    // ----- In-place viewport text edit (Pass 5.6.5) -----
    // Double-clicking a text clip on the player edits its words where they are, in a popup editor centred on its box (the overlay window
    // never takes the keyboard). Enter commits (Shift+Enter: new line), Esc cancels, clicking away commits; one Undo step.

    private (Popup Popup, TextBox Editor, Layer Clip)? _textEdit;

    private void BeginTextEdit(FrameworkElement box, Layer clip)
    {
        EndTextEdit(commit: true);
        var editor = new TextBox
        {
            Text = clip.TextContent, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, Padding = new Thickness(6, 4, 6, 4),
            MinWidth = Math.Clamp(box.ActualWidth, 120, 900), MaxWidth = 900, MinHeight = 30,
        };
        System.Windows.Automation.AutomationProperties.SetName(editor, $"Text of {clip.Name}");
        var popup = new Popup { Child = editor, PlacementTarget = box, Placement = PlacementMode.Center, StaysOpen = false, AllowsTransparency = true };
        _textEdit = (popup, editor, clip);
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                EndTextEdit(commit: false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                EndTextEdit(commit: true);
                e.Handled = true;
            }
        };
        editor.Loaded += (_, _) =>
        {
            editor.Focus();
            Keyboard.Focus(editor);
            editor.SelectAll();
        };
        popup.Closed += (_, _) => EndTextEdit(commit: true);
        popup.IsOpen = true;
    }

    /// <summary>Closes the in-place editor, writing its text to the clip (one Undo step) when committing and changed.</summary>
    private void EndTextEdit(bool commit)
    {
        if (_textEdit is not { } edit)
            return;

        _textEdit = null;
        edit.Popup.IsOpen = false;
        var text = edit.Editor.Text;
        if (!commit || text == edit.Clip.TextContent)
            return;

        _viewModel.Checkpoint($"edit the text of {edit.Clip.Name}");
        edit.Clip.TextContent = text;
    }
}
