using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// The Cutout Studio (5.1-CleanupStudioAndFfmpegPurge; Draw Custom Shape before it): a still of the video to trace a game's HUD on by
/// hand, corner by corner with the polygon pen, zoomed in to 64 screen pixels a source pixel and panned at any moment, a shape half drawn or
/// not. The shapes belong to named Cutouts; the Cutouts become clips, or are saved as a Game Preset (.hpstyle) for the drop overlay and
/// the Game Presets list, laid out on a tall frame beside the drawing. Opened from a clip it can also be that clip's mask. Built in code.
/// </summary>
public sealed partial class MaskDrawWindow : Window
{
    /// <summary>The most a view magnifies: 64 screen pixels to a source pixel.</summary>
    public const double MostZoom = 64;

    /// <summary>From here up the still's pixels are drawn as hard squares.</summary>
    public const double PixelEdgesFrom = 4;

    /// <summary>From here up each source pixel gets a cell of a thin grid, and points land on its corners.</summary>
    public const double PixelGridFrom = 16;

    // The wheel's zoom for one notch (120), and how fast the view catches up with it (the time constant, seconds).
    private const double ZoomPerNotch = 1.25, ZoomEase = 0.045;

    // WASD's pan, in screen pixels a second; Shift three times that.
    private const double KeyPanSpeed = 900;

    // A notch of Shift + the wheel or of the horizontal wheel (a touchpad's sideways scroll), in screen pixels; how far a right press
    // moves before it is a pan rather than a click (5.3.1).
    private const double WheelPan = 96, RightDragFrom = 4;

    private const int WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E, MkShift = 0x0004, MkControl = 0x0008;

    // How long after a touchpad's scroll a wheel message is still taken for the same gesture (Pass 5.5.1): a precision touchpad's scroll
    // now and then sends a whole notch (120), which must not zoom in the middle of a pan.
    internal const long TouchpadStreamMs = 250;

    // When the wheel last came from a touchpad (Environment.TickCount64); long ago at first.
    private long _touchpadWheelAt = long.MinValue / 2;

    private static readonly string[] PieceNames = ["Hero Portrait & HP", "Abilities & Weapon", "Killfeed", "Minimap", "Objective"];

    private readonly MainViewModel _viewModel;
    private readonly Layer _layer;
    private readonly bool _forLayer;

    // The file the still is taken from (5.2-Audit, the user's Q4): the clip's own, for a clip of any file (5.2-AuditB: on the video
    // alone, the video's at the playhead); the file last opened's otherwise. And whether it is the clip's, read on its clock.
    private readonly string _stillSource;
    private readonly bool _stillOnClip;

    // The still and the shapes are laid out in the source's own pixels; the view scales and moves them.
    private readonly double _width, _height;
    private readonly Image _still = new() { Stretch = Stretch.Fill };
    private readonly ShapeSurface _surface;
    private readonly Canvas _viewport = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Grid _picture;
    private readonly MatrixTransform _view = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, Margin = new Thickness(8, 0, 8, 0), TextAlignment = TextAlignment.Right };
    private readonly string _stillPath = Path.Combine(SessionPaths.Previews, $"mask_still_{Guid.NewGuid():N}.png");
    private readonly CancellationTokenSource _closing = new();
    private readonly Button _undoButton, _redoButton;
    private readonly StackPanel _pieceRows = new();
    private readonly CutoutStage _stage;
    private readonly SourceNavigator _navigator;

    // The drawing view's scroll bars (5.3.1): over its right and bottom edges, shown while the picture is larger than the view that way.
    private readonly ScrollBar _horizontalBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly ScrollBar _verticalBar = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    private readonly Slider _zoomSlider = new() { Minimum = 1, Maximum = 2.5, Value = MainViewModel.CutoutDefaultZoom, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _presetName = new() { Margin = new Thickness(0, 4, 0, 0) };

    // The named pieces, by the id their shapes carry; the ids of those that were moved by hand on the tall frame.
    private readonly Dictionary<int, string> _names = [];
    private readonly Dictionary<int, CutoutStage.Placed> _placed = [];
    private readonly HashSet<int> _movedByHand = [];
    private int _nextPiece;

    // The view: screen pixels to a source pixel, and where the picture's corner lies in the viewport. A wheel moves the goal and the
    // view eases to it about the point under the pointer.
    private double _zoom = 1, _panX, _panY, _zoomGoal = 1;
    private Point _zoomPoint;
    private bool _fitted = true, _ticking;
    private long _lastTick;
    private readonly HashSet<Key> _panKeys = [];
    private bool _spaceHeld;
    private (Point From, double PanX, double PanY)? _panDrag;

    // A right press in the view, until it is a click (the button up where it went down) or a pan (5.3.1).
    private (Point From, double PanX, double PanY)? _rightPress;

    private static readonly FontFamily Glyphs = new("Segoe MDL2 Assets");

    // Segoe MDL2 Assets: Undo (E7A7), Redo (E7A6), Delete (E74D).
    private static readonly string UndoGlyph = char.ConvertFromUtf32(0xE7A7), RedoGlyph = char.ConvertFromUtf32(0xE7A6), DeleteGlyph = char.ConvertFromUtf32(0xE74D);

    /// <summary>The finished shapes as a mask's text (see <see cref="MaskStore"/>), once the window was confirmed for a clip.</summary>
    public string Shapes { get; private set; } = "";

    /// <summary>Whether the shapes are for every layer cut from the video, not only the one the window was opened from.</summary>
    public bool ForAllLayers { get; private set; }

    /// <summary>
    /// A picture of the still's file near its moment, decoded ahead (5.4.3, MainWindow.HeatLamp.cs): shown on the window's first frame while
    /// the exact still is taken, when it is of the same file and moment (<see cref="StillTarget"/>).
    /// </summary>
    public sealed record WarmStill(string Source, double Seconds, BitmapSource Picture);

    private readonly WarmStill? _warm;

    /// <param name="layer">The clip it is asked to mask; null opens it on the video alone, for Cutouts and Game Presets.</param>
    /// <param name="warm">A picture decoded ahead (the heat lamp), used only when it is of this still's file and moment.</param>
    public MaskDrawWindow(MainViewModel viewModel, Layer? layer, WarmStill? warm = null)
    {
        (_viewModel, _layer, _forLayer) = (viewModel, layer ?? viewModel.PlayheadVideo, layer is not null);

        // The still and its natural size are the clip's being edited (on the video alone, the video at the playhead): a clip is
        // drawn on over its own file's picture.
        var target = layer ?? _layer;
        var ownFile = OwnFileOf(target);
        (_stillSource, _stillOnClip) = (ownFile ?? viewModel.LocalMediaPath, ownFile is not null);
        if (warm is not null && string.Equals(warm.Source, _stillSource, StringComparison.OrdinalIgnoreCase) && Math.Abs(warm.Seconds - StillSeconds()) < 0.001)
            _warm = warm;
        (_width, _height) = ownFile is not null
            ? MediaProbe.KnownInfo(ownFile)?.Video is { Width: > 0, Height: > 0 } video ? (video.Width, video.Height)
            : target.ImageWidth > 0 && target.ImageHeight > 0 ? (target.ImageWidth, target.ImageHeight) : (1920.0, 1080.0)
            : viewModel.SourceWidth > 0 && viewModel.SourceHeight > 0 ? (viewModel.SourceWidth, viewModel.SourceHeight) : (1920.0, 1080.0);

        Title = layer is null ? "Cutout Studio" : $"Cutout Studio: {layer.Name}";
        Width = 1320;
        Height = 860;
        MinWidth = 900;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WheelMessage);
        };

        // The old regions of the opened file (LayerKind.Video, a legacy kind) are shown over its still alone.
        var regions = viewModel.Layers.Where(l => ownFile is null && l.IsVideo && !l.IsDeleted && !(l.UsesFrameMask && l.MaskShapes.Length > 0))
            .Select(l => (new Rect(l.SourceX * _width, l.SourceY * _height, l.SourceWidth * _width, l.SourceHeight * _height), l.Name, ReferenceEquals(l, layer)))
            .ToList();
        _surface = new ShapeSurface(_width, _height, regions, StartingShapes());
        _surface.CurrentPiece = _names.Count > 0 ? _names.Keys.First() : NewPiece();
        _surface.Changed += ShowHelp;
        _surface.Changed += RefreshPieces;

        _picture = new Grid { Width = _width, Height = _height, Background = Brushes.Black, RenderTransform = _view, RenderTransformOrigin = new Point(0, 0) };
        _picture.Children.Add(_still);
        _picture.Children.Add(_surface);
        _viewport.Children.Add(_picture);
        _viewport.SizeChanged += (_, _) =>
        {
            if (_fitted)
                Fit();
            else
                ApplyView();
        };
        _viewport.PreviewMouseWheel += Viewport_MouseWheel;
        _viewport.PreviewMouseDown += Viewport_MouseDown;
        _viewport.PreviewMouseMove += Viewport_MouseMove;
        _viewport.PreviewMouseUp += Viewport_MouseUp;
        _viewport.LostMouseCapture += (_, _) => (_panDrag, _rightPress) = (null, null);
        RenderOptions.SetBitmapScalingMode(_still, BitmapScalingMode.HighQuality);
        _horizontalBar.Scroll += (_, _) => PanTo(-_horizontalBar.Value, _panY);
        _verticalBar.Scroll += (_, _) => PanTo(_panX, -_verticalBar.Value);
        _navigator = new SourceNavigator(_width, _height, _surface) { Margin = new Thickness(0, 2, 0, 0) };
        _navigator.Navigate += CenterOn;

        // Undo and redo, over the shapes (ShapeSurface keeps the steps): enabled while there is something to undo or redo.
        _undoButton = GlyphButton(UndoGlyph, "Undo (Ctrl+Z)", "Undo");
        _undoButton.Click += (_, _) => _surface.Undo();
        _redoButton = GlyphButton(RedoGlyph, "Redo (Ctrl+Y)", "Redo");
        _redoButton.Margin = new Thickness(4, 0, 10, 0);
        _redoButton.Click += (_, _) => _surface.Redo();
        _surface.Changed += UpdateUndoButtons;

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        foreach (var (tool, name, tip) in new (CutoutTool, string, string)[]
                 {
                     (CutoutTool.Pen, "Pen", "The polygon pen: click corner by corner."),
                     (CutoutTool.Rectangle, "Rectangle", "Drag out a rectangle: four ordinary points."),
                     (CutoutTool.Ellipse, "Ellipse", "Drag out an ellipse: round points to drag like any others. For a round portrait or an ultimate meter."),
                     (CutoutTool.Slant, "Slant", "Drag out a bar leaning to the right, as slanted health bars do."),
                 })
        {
            var choice = new RadioButton { Content = name, ToolTip = tip, GroupName = "CutoutTool", IsChecked = tool == CutoutTool.Pen, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            choice.Checked += (_, _) => _surface.Tool = tool;
            tools.Children.Add(choice);
        }

        var undoShape = new Button { Content = "Remove Last Shape", Padding = new Thickness(12, 4, 12, 4) };
        undoShape.Click += (_, _) => _surface.RemoveLastShape();
        var clear = new Button { Content = "Clear All", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => _surface.Clear();
        var fit = new Button { Content = "Fit", Padding = new Thickness(12, 4, 12, 4), ToolTip = "The whole picture in the window." };
        fit.Click += (_, _) => Fit();
        var actualSize = new Button { Content = "100%", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 0, 0), ToolTip = "One screen pixel to a source pixel." };
        actualSize.Click += (_, _) => ZoomTo(1, new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2));
        var view = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        view.Children.Add(fit);
        view.Children.Add(actualSize);

        // Pass 5.5.1 (the user's): the shortcuts are a card behind a '?' beside Fit and 100%, no longer four lines of words over the view.
        view.Children.Add(_shortcutsButton = ShortcutsButton());
        view.Children.Add(_shortcutsCard);
        view.Children.Add(_zoomText);

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(view, Dock.Right);
        top.Children.Add(view);
        top.Children.Add(tools);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = _forLayer ? "Cancel" : "Close", MinWidth = 88, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        var addLayers = new Button
        {
            Content = "Add Cutouts to Timeline",
            Padding = new Thickness(14, 4, 14, 4),
            ToolTip = "Lays the video out tall with its blur fill and adds each named Cutout over it, as drawn here. Cutouts added before are replaced. One step of Undo; every Cutout stays an ordinary clip.",
        };
        addLayers.Click += (_, _) =>
        {
            _surface.CloseShape();
            if (Pieces() is { Count: > 0 } pieces)
            {
                _viewModel.AddCutouts(pieces, _zoomSlider.Value);
                DialogResult = false;
            }
            else
            {
                _status.Text = "Draw at least one shape first.";
            }
        };
        right.Children.Add(addLayers);
        if (_forLayer)
        {
            var applyOne = new Button { Content = "Apply to This Clip", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0) };
            applyOne.Click += (_, _) => Finish(forAll: false);
            right.Children.Add(applyOne);
            if (regions.Count > 1)
            {
                var applyAll = new Button
                {
                    Content = "Apply to All Cutouts",
                    Padding = new Thickness(14, 4, 14, 4),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "Every Cutout of the video gets this mask of the whole frame, and takes the part of it under its own area.",
                };
                applyAll.Click += (_, _) => Finish(forAll: true);
                right.Children.Add(applyAll);
            }
        }

        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(undoShape);
        left.Children.Add(clear);
        DockPanel.SetDock(left, Dock.Left);
        _status.Margin = new Thickness(12, 0, 12, 0);
        buttons.Children.Add(right);
        buttons.Children.Add(left);
        buttons.Children.Add(_status);

        _stage = new CutoutStage(_width, _height) { MinHeight = 160, Margin = new Thickness(0, 4, 0, 6) };
        _stage.Moved += PieceMoved;
        _zoomSlider.ValueChanged += (_, _) => RefreshPieces();
        ToolTipService.SetToolTip(_zoomSlider, "How far the video is zoomed on the tall frame. Its sides are what the Cutouts bring back.");
        var pane = PiecesPane();
        DockPanel.SetDock(pane, Dock.Right);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(pane);
        var viewHost = new Grid();
        viewHost.Children.Add(_viewport);
        viewHost.Children.Add(_horizontalBar);
        viewHost.Children.Add(_verticalBar);
        root.Children.Add(new Border { Child = viewHost, Background = Brushes.Black });
        Content = root;

        PreviewKeyDown += Window_PreviewKeyDown;
        PreviewKeyUp += Window_PreviewKeyUp;
        PreviewMouseDown += (_, _) => CloseShortcutsUnlessOnButton();
        Deactivated += (_, _) =>
        {
            LetGoOfKeys();
            CloseShortcuts();
        };
        Loaded += async (_, _) =>
        {
            Fit();
            _surface.Focus();
            await LoadStillAsync();
        };
        Closed += (_, _) =>
        {
            _closing.Cancel();
            StopTicking();
            SourceStill.Delete(_stillPath);
        };
        ShowHelp();
        RefreshPieces();
    }

    private void LetGoOfKeys()
    {
        _panKeys.Clear();
        _spaceHeld = false;
    }

    // ----- The pieces -----

    // What it starts from. From a layer: its own drawn shapes, or failing that those of another piece of the same video (one mask of the
    // frame). Opened on the video alone: the pieces the Studio made earlier, each with its name.
    private List<(int Owner, List<Point> Points)> StartingShapes()
    {
        var start = new List<(int, List<Point>)>();
        void Take(int piece, string text) =>
            start.AddRange(MaskStore.ParseShapes(text).Select(s => (piece, s.Select(p => new Point(p.X * _width, p.Y * _height)).ToList())));

        if (_forLayer)
        {
            var piece = _viewModel.IsCutout(_layer);
            var text = (_layer.UsesFrameMask || (piece && _layer.CustomMask)) && _layer.MaskShapes.Length > 0 ? _layer.MaskShapes
                : piece ? _viewModel.CutoutLayers.FirstOrDefault()?.MaskShapes ?? "" : "";
            if (text.Length > 0)
                Take(NewPiece(_layer.Name), text);
            return start;
        }

        foreach (var group in _viewModel.CutoutLayers.GroupBy(l => l.MaskShapes))
        {
            var piece = NewPiece(group.First().Name);
            Take(piece, group.Key);
            _placed[piece] = new CutoutStage.Placed
            {
                Id = piece,
                Source = default,
                Outlines = [],
                X = group.First().PositionX,
                Y = group.First().PositionY,
                Width = group.First().SizeWidth,
            };
            _movedByHand.Add(piece);
        }

        return start;
    }

    private int NewPiece(string? name = null)
    {
        var piece = ++_nextPiece;
        _names[piece] = name ?? (piece <= PieceNames.Length ? PieceNames[piece - 1] : $"Cutout {piece}");
        return piece;
    }

    // The column at the right: the Cutouts, the Video Zoom, the Source Navigator, the Output Stage (taking the height left) and the
    // Game Preset's name and Save at its foot.
    private DockPanel PiecesPane()
    {
        var pane = new DockPanel { Width = 270, Margin = new Thickness(10, 0, 0, 0) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        pane.Children.Add(top);
        var foot = new StackPanel();
        DockPanel.SetDock(foot, Dock.Bottom);
        pane.Children.Add(foot);
        top.Children.Add(new TextBlock { Text = "Cutouts", FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock { Text = "New shapes go to the Cutout selected. Each one becomes a clip of its own.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        top.Children.Add(hint);
        top.Children.Add(new ScrollViewer { Content = _pieceRows, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var add = new Button { Content = "New Cutout", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            _surface.CloseShape();
            _surface.CurrentPiece = NewPiece();
            RefreshPieces();
        };
        top.Children.Add(add);

        var zoomRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var zoomLabel = new TextBlock { Text = "Video Zoom", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(zoomLabel, Dock.Left);
        var zoomValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, TextAlignment = TextAlignment.Right };
        void ShowZoom() => zoomValue.Text = System.FormattableString.Invariant($"{_zoomSlider.Value * 100:0} %");
        _zoomSlider.ValueChanged += (_, _) => ShowZoom();
        ShowZoom();
        DockPanel.SetDock(zoomValue, Dock.Right);
        zoomRow.Children.Add(zoomLabel);
        zoomRow.Children.Add(zoomValue);
        zoomRow.Children.Add(_zoomSlider);
        top.Children.Add(zoomRow);

        // The whole frame being cut from, with the view's box (5.3.1); under it the Output Stage, labelled for what a drag there does.
        top.Children.Add(new TextBlock { Text = "Source Navigator", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        top.Children.Add(_navigator);
        top.Children.Add(new TextBlock { Text = "Output Stage \u2014 drag a finished Cutout to position it", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });

        foot.Children.Add(new TextBlock { Text = "Game Preset Name", Margin = new Thickness(0, 4, 0, 0) });
        foot.Children.Add(_presetName);
        var save = new Button { Content = "Save as Game Preset", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 6, 0, 0) };
        save.ToolTip = "Saves the Cutouts as a Game Preset in the Styles folder, its masks inside it: it shows on the drag-and-drop overlay and in the Game Presets. Nothing on the timeline changes.";
        save.Click += (_, _) =>
        {
            _surface.CloseShape();
            var problem = _viewModel.SaveCutoutPreset(_presetName.Text, Pieces(), _zoomSlider.Value);
            _status.Text = problem ?? $"Game Preset \"{_presetName.Text.Trim()}\" saved: drop a video on it, or pick it from the Game Presets.";
        };
        foot.Children.Add(save);
        pane.Children.Add(_stage);
        return pane;
    }

    // A piece moved on the tall frame: the stage holds the very object it moves, so nothing is made again under the drag. A double-click
    // (no X left) gives it back to the automatic layout.
    private void PieceMoved(int piece)
    {
        if (_placed.TryGetValue(piece, out var placed) && double.IsNaN(placed.X))
        {
            _movedByHand.Remove(piece);
            RefreshPieces();
        }
        else
        {
            _movedByHand.Add(piece);
        }
    }

    // The pieces with shapes, in the order they were made, as the layout and the view model want them. Their place on the tall frame:
    // laid out above and below the video by HudLibrary, but for those moved by hand.
    private List<CutoutPiece> Pieces()
    {
        var pieces = new List<(int Id, Rect Source, List<List<Point>> Outlines)>();
        foreach (var id in _names.Keys)
        {
            var outlines = new List<List<Point>>();
            for (var s = 0; s < _surface.Finished.Count; s++)
            {
                if (_surface.Owners[s] == id)
                    outlines.Add(_surface.Finished[s].Select(p => new Point(p.X / _width, p.Y / _height)).ToList());
            }

            if (outlines.Count == 0)
                continue;

            var (left, top) = (outlines.Min(o => o.Min(p => p.X)), outlines.Min(o => o.Min(p => p.Y)));
            var (right, bottom) = (outlines.Max(o => o.Max(p => p.X)), outlines.Max(o => o.Max(p => p.Y)));
            pieces.Add((id, new Rect(left, top, Math.Max(right - left, 1e-4), Math.Max(bottom - top, 1e-4)), outlines));
        }

        var key = System.Globalization.CultureInfo.InvariantCulture;
        var automatic = pieces.Where(p => !_movedByHand.Contains(p.Id))
            .Select(p => new LayerState { Name = p.Id.ToString(key), SourceX = p.Source.X, SourceY = p.Source.Y, SourceWidth = p.Source.Width, SourceHeight = p.Source.Height }).ToList();
        HudLibrary.LayOutVertically(automatic, _zoomSlider.Value);

        var result = new List<CutoutPiece>(pieces.Count);
        var seen = new List<CutoutStage.Placed>(pieces.Count);
        foreach (var (id, source, outlines) in pieces)
        {
            var (x, y, width) = (0.0, 0.0, 0.25);
            if (automatic.FirstOrDefault(a => a.Name == id.ToString(key)) is { } laid)
                (x, y, width) = (laid.PositionX, laid.PositionY, laid.SizeWidth);
            else if (_placed.TryGetValue(id, out var kept))
                (x, y, width) = (kept.X, kept.Y, kept.Width);

            var placed = new CutoutStage.Placed { Id = id, Source = source, Outlines = outlines, X = x, Y = y, Width = width };
            _placed[id] = placed;
            seen.Add(placed);
            result.Add(new CutoutPiece(_names[id].Trim().Length > 0 ? _names[id].Trim() : $"Cutout {id}", MaskStore.FormatShapes(outlines), source, x, y, width));
        }

        _stage.Show(seen, _still.Source as BitmapSource, _zoomSlider.Value);
        return result;
    }

    // The rows of the piece list and the tall frame. Rows come and go with the pieces; a name being typed keeps its box.
    private void RefreshPieces()
    {
        // An undo can bring back the shapes of a piece that was removed.
        foreach (var id in _surface.Owners)
            _names.TryAdd(id, $"Cutout {id}");
        Pieces();
        _stage.Selected = _surface.CurrentPiece;

        var shown = _pieceRows.Children.OfType<FrameworkElement>().Select(row => (int)row.Tag).ToList();
        if (!shown.SequenceEqual(_names.Keys))
        {
            _pieceRows.Children.Clear();
            foreach (var (id, name) in _names)
                _pieceRows.Children.Add(PieceRow(id, name));
        }

        foreach (var row in _pieceRows.Children.OfType<Border>())
        {
            var id = (int)row.Tag;
            row.BorderBrush = id == _surface.CurrentPiece ? ShapeSurface.LineOf(id) : Brushes.Transparent;
            if (row.Child is DockPanel panel && panel.Children[0] is TextBlock count)
            {
                var shapes = _surface.Owners.Count(o => o == id);
                count.Text = shapes == 1 ? "1 shape" : $"{shapes} shapes";
            }
        }
    }

    private Border PieceRow(int id, string name)
    {
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), MinWidth = 46, FontSize = 11 };
        count.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(count, Dock.Right);
        var remove = new Button { Content = DeleteGlyph, FontFamily = Glyphs, FontSize = 11, Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove this Cutout and its shapes. Ctrl+Z brings the shapes back." };
        remove.Click += (_, _) =>
        {
            _surface.RemovePiece(id);
            _names.Remove(id);
            if (_surface.CurrentPiece == id)
                _surface.CurrentPiece = _names.Count > 0 ? _names.Keys.Last() : NewPiece();
            RefreshPieces();
        };
        DockPanel.SetDock(remove, Dock.Right);
        var swatch = new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Fill = ShapeSurface.LineOf(id), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(swatch, Dock.Left);
        var named = new TextBox { Text = name, MinWidth = 90, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "This Cutout's name: the clip's, on the Timeline and in the Game Preset." };
        System.Windows.Automation.AutomationProperties.SetName(named, "Cutout Name");
        named.TextChanged += (_, _) => _names[id] = named.Text;
        named.GotKeyboardFocus += (_, _) => Select(id);

        var panel = new DockPanel();
        panel.Children.Add(count);
        panel.Children.Add(remove);
        panel.Children.Add(swatch);
        panel.Children.Add(named);
        var row = new Border { Tag = id, Child = panel, BorderThickness = new Thickness(1), Padding = new Thickness(3), Margin = new Thickness(0, 1, 0, 1), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent };
        row.PreviewMouseLeftButtonDown += (_, _) => Select(id);
        return row;

        void Select(int piece)
        {
            if (_surface.CurrentPiece == piece)
                return;

            _surface.CloseShape();
            _surface.CurrentPiece = piece;
            _surface.InvalidateVisual();
            RefreshPieces();
        }
    }

    // ----- The view -----

    private double Fitted => Math.Min(_viewport.ActualWidth / _width, _viewport.ActualHeight / _height);

    // The least a view magnifies: the whole picture, or a screen pixel to a source pixel for a video smaller than the window.
    private double LeastZoom => Math.Min(Fitted > 0 ? Fitted : 1, 1);

    private void Fit()
    {
        if (_viewport.ActualWidth <= 0 || _viewport.ActualHeight <= 0)
            return;

        _fitted = true;
        StopTicking();
        _zoom = _zoomGoal = Fitted;
        ApplyView();
    }

    private void ZoomTo(double zoom, Point at)
    {
        _fitted = false;
        _zoomGoal = Math.Clamp(zoom, LeastZoom, MostZoom);
        _zoomPoint = at;
        if (AppSettings.Current.EnableExtraAnimations)
        {
            StartTicking();
        }
        else
        {
            SetZoom(_zoomGoal);
            ApplyView();
        }
    }

    // The zoom set, with the point of the picture under the zoom's point staying under it.
    private void SetZoom(double zoom)
    {
        var (sourceX, sourceY) = ((_zoomPoint.X - _panX) / _zoom, (_zoomPoint.Y - _panY) / _zoom);
        _zoom = zoom;
        (_panX, _panY) = (_zoomPoint.X - sourceX * zoom, _zoomPoint.Y - sourceY * zoom);
    }

    private void ApplyView()
    {
        // The picture is never lost: smaller than the viewport it sits in the middle, larger it always covers it.
        var (wide, tall) = (_width * _zoom, _height * _zoom);
        var (viewWidth, viewHeight) = (_viewport.ActualWidth, _viewport.ActualHeight);
        _panX = wide <= viewWidth ? (viewWidth - wide) / 2 : Math.Clamp(_panX, viewWidth - wide, 0);
        _panY = tall <= viewHeight ? (viewHeight - tall) / 2 : Math.Clamp(_panY, viewHeight - tall, 0);
        _view.Matrix = new Matrix(_zoom, 0, 0, _zoom, _panX, _panY);

        RenderOptions.SetBitmapScalingMode(_still, _zoom >= PixelEdgesFrom ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        var seen = new Rect(-_panX / _zoom, -_panY / _zoom, Math.Max(viewWidth, 0) / _zoom, Math.Max(viewHeight, 0) / _zoom);
        _surface.ViewChanged(_zoom >= PixelGridFrom ? 1 : null, seen);
        _zoomText.Text = System.FormattableString.Invariant($"{_zoom:0.##}x");
        _navigator.Seen = seen;
        ShowBar(_horizontalBar, wide, viewWidth, _panX);
        ShowBar(_verticalBar, tall, viewHeight, _panY);
    }

    // A scroll bar over the view: shown while the picture is larger than the view that way, its thumb the part seen. Setting its value
    // here raises no Scroll, so only the user's own moves of it pan.
    private static void ShowBar(ScrollBar bar, double picture, double view, double pan)
    {
        var shown = picture > view + 0.5;
        bar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
            return;

        (bar.Minimum, bar.Maximum, bar.ViewportSize) = (0, picture - view, view);
        (bar.SmallChange, bar.LargeChange, bar.Value) = (WheelPan, view * 0.9, -pan);
    }

    // The picture's corner put here (ApplyView keeps it covering the view). A view showing the whole picture stays fitted.
    private void PanTo(double panX, double panY)
    {
        if (_width * _zoom > _viewport.ActualWidth + 0.5 || _height * _zoom > _viewport.ActualHeight + 0.5)
            _fitted = false;
        (_panX, _panY) = (panX, panY);
        ApplyView();
    }

    // The Source Navigator's press: the view centred on that point of the source, at the zoom it has.
    internal void CenterOn(Point source) =>
        PanTo(_viewport.ActualWidth / 2 - source.X * _zoom, _viewport.ActualHeight / 2 - source.Y * _zoom);

    private void StartTicking()
    {
        if (_ticking)
            return;

        _lastTick = Stopwatch.GetTimestamp();
        _ticking = true;
        CompositionTarget.Rendering += Tick;
    }

    private void StopTicking()
    {
        if (!_ticking)
            return;

        _ticking = false;
        CompositionTarget.Rendering -= Tick;
    }

    // Each refresh while the view is moving: the wheel's zoom eased in, WASD's pan.
    private void Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Min((now - _lastTick) / (double)Stopwatch.Frequency, 0.1);
        _lastTick = now;

        var moved = false;
        if (Math.Abs(_zoom / _zoomGoal - 1) > 0.001)
        {
            SetZoom(_zoom * Math.Pow(_zoomGoal / _zoom, 1 - Math.Exp(-seconds / ZoomEase)));
            moved = true;
        }
        else if (_zoom != _zoomGoal)
        {
            SetZoom(_zoomGoal);
            moved = true;
        }

        if (_panKeys.Count > 0)
        {
            var step = KeyPanSpeed * seconds * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 3 : 1);
            _panX += (_panKeys.Contains(Key.A) ? step : 0) - (_panKeys.Contains(Key.D) ? step : 0);
            _panY += (_panKeys.Contains(Key.W) ? step : 0) - (_panKeys.Contains(Key.S) ? step : 0);
            _fitted = false;
            moved = true;
        }

        if (moved)
            ApplyView();
        else
            StopTicking();
    }

    // The view's pointer, from WPF's events (and the window's own messages) into the point-based steps below, which the golden tests
    // drive with the points and keys they choose.
    // A wheel WPF raises (the window's own messages answer the real ones first, WheelMessage): one sent by a test, or one the hook let by.
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Wheel(e.Delta, e.GetPosition(_viewport), Keyboard.Modifiers, TouchpadNow(e.Delta));
        e.Handled = true;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = Press(e.ChangedButton, e.GetPosition(_viewport), Keyboard.Modifiers);

    // A right press waits for the right button's own moves: the move WPF raises as the press takes the pointer is not one of them.
    private void Viewport_MouseMove(object sender, MouseEventArgs e) =>
        e.Handled = (_rightPress is null || _panDrag is not null || e.RightButton == MouseButtonState.Pressed) && MoveTo(e.GetPosition(_viewport));

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e) => e.Handled = Release(e.ChangedButton);

    /// <summary>
    /// The wheels over the view, from the window's own messages, before WPF sees them (Pass 5.5.1, the user's: a touchpad's two-finger
    /// scroll up and down zoomed). The horizontal wheel (a touchpad's sideways scroll, a tilted wheel), which WPF raises nothing for, pans
    /// sideways, answered TRUE or Windows sends it on as an old tilt wheel's clicks. The vertical wheel is told apart here, while its
    /// message is the one being handled (<see cref="TouchpadNow"/>), with its keys as the message carries them (a precision touchpad's
    /// pinch is Ctrl + the wheel), and done here (<see cref="Wheel"/>): handled, so WPF raises no MouseWheel for it and nothing is done twice.
    /// </summary>
    private IntPtr WheelMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WmMouseWheel or WmMouseHWheel) || !_viewport.IsMouseOver)
            return IntPtr.Zero;

        var word = wParam.ToInt64();
        var delta = (short)((word >> 16) & 0xFFFF);
        handled = true;
        if (msg == WmMouseHWheel)
        {
            SidewaysWheel(delta);
            return new IntPtr(1);
        }

        var keys = Keyboard.Modifiers & ModifierKeys.Alt;
        if ((word & MkShift) != 0)
            keys |= ModifierKeys.Shift;
        if ((word & MkControl) != 0)
            keys |= ModifierKeys.Control;

        // The pointer as the message has it (screen pixels), in the view's DIPs: the point a zoom keeps under it.
        var (x, y) = ((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        Wheel(delta, _viewport.PointFromScreen(new Point(x, y)), keys, TouchpadNow(delta));
        return IntPtr.Zero;
    }

    /// <summary>
    /// Whether the wheel being handled now is a touchpad's (<see cref="IsTouchpadWheel"/>, with what Windows says of the message and of the
    /// wheel just before it); a touchpad's is remembered, so the rest of its gesture is one too.
    /// </summary>
    private bool TouchpadNow(int delta)
    {
        var now = Environment.TickCount64;
        var touchpad = IsTouchpadWheel(delta, MainWindow.FromTouchpad(), now - _touchpadWheelAt <= TouchpadStreamMs);
        if (touchpad)
            _touchpadWheelAt = now;
        return touchpad;
    }

    /// <summary>
    /// Whether a vertical wheel is a touchpad's two-finger scroll (pure, for cutout-studio-wheel): Windows says its message came from a
    /// touchpad (GetCurrentInputMessageSource, IMDT_TOUCHPAD), or it is no whole number of notches (a mouse's notched wheel turns 120 at a
    /// time; a precision touchpad's scroll sends what the fingers moved), or a touchpad's scroll came just before it (the same gesture).
    /// </summary>
    internal static bool IsTouchpadWheel(int delta, bool sourceIsTouchpad, bool touchpadJustBefore) =>
        sourceIsTouchpad || touchpadJustBefore || delta % 120 != 0;

    /// <summary>What a turn of the vertical wheel does in the view.</summary>
    internal enum WheelAction { Zoom, PanSideways, PanUpDown }

    /// <summary>
    /// What the vertical wheel does in the view (pure, for cutout-studio-wheel): Shift pans sideways; a touchpad's two-finger scroll with no
    /// key pans up and down; anything else zooms about the pointer: a mouse's wheel, Ctrl + either, and a touchpad's pinch (Ctrl + the wheel).
    /// </summary>
    internal static WheelAction WheelDoes(ModifierKeys keys, bool touchpad) =>
        keys == ModifierKeys.Shift ? WheelAction.PanSideways
        : touchpad && keys == ModifierKeys.None ? WheelAction.PanUpDown
        : WheelAction.Zoom;

    /// <summary>
    /// The wheel at a point of the view (<see cref="WheelDoes"/>): a zoom about it; with Shift a pan sideways (a notch down moves the view
    /// right); a touchpad's two-finger scroll with no key (<paramref name="touchpad"/>) a pan up and down, the picture moving with the fingers
    /// as a page scrolls; its pinch (Ctrl) still zooms.
    /// </summary>
    internal void Wheel(int delta, Point at, ModifierKeys keys, bool touchpad = false)
    {
        switch (WheelDoes(keys, touchpad))
        {
            case WheelAction.PanSideways:
                PanTo(_panX + delta / 120.0 * WheelPan, _panY);
                break;
            case WheelAction.PanUpDown:
                PanTo(_panX, _panY + delta / 120.0 * WheelPan);
                break;
            default:
                ZoomTo(_zoomGoal * Math.Pow(ZoomPerNotch, delta / 120.0), at);
                break;
        }
    }

    /// <summary>The horizontal wheel: a notch to the right moves the view right.</summary>
    internal void SidewaysWheel(int delta) => PanTo(_panX - delta / 120.0 * WheelPan, _panY);

    /// <summary>
    /// A press at a point of the view. A pan takes it (true: it never reaches the shapes, so a shape being drawn is left as it is) for
    /// the middle button, Space + the left, Alt + the left away from an edge (on one it puts a point in), and the right button, which
    /// becomes a pan once it moves and a right-click on the shapes if it comes up where it went down (5.3.1).
    /// </summary>
    internal bool Press(MouseButton button, Point at, ModifierKeys keys)
    {
        if (button == MouseButton.Right)
        {
            _rightPress = (at, _panX, _panY);
            _viewport.CaptureMouse();
            return true;
        }

        var altPan = button == MouseButton.Left && keys.HasFlag(ModifierKeys.Alt) && !_surface.HasEdgeAt(_viewport.TranslatePoint(at, _surface));
        if (button == MouseButton.Middle || (button == MouseButton.Left && (_spaceHeld || altPan)))
        {
            StartPan(at, _panX, _panY);
            return true;
        }

        return false;
    }

    private void StartPan(Point from, double panX, double panY)
    {
        _panDrag = (from, panX, panY);
        _fitted = false;
        _viewport.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    /// <summary>The pointer moved to a point of the view: a pan follows it; a right press becomes one once it is a few pixels away.</summary>
    internal bool MoveTo(Point at)
    {
        if (_rightPress is { } press && _panDrag is null)
        {
            if ((at - press.From).Length < RightDragFrom)
                return false;
            StartPan(press.From, press.PanX, press.PanY);
        }

        if (_panDrag is not { } drag)
            return false;

        (_panX, _panY) = (drag.PanX + at.X - drag.From.X, drag.PanY + at.Y - drag.From.Y);
        ApplyView();
        return true;
    }

    /// <summary>A button came up: a pan ends; a right press that never moved is a right-click on the shapes, where it went down.</summary>
    internal bool Release(MouseButton button)
    {
        if (button == MouseButton.Right && _rightPress is { } press && _panDrag is null)
        {
            _rightPress = null;
            _viewport.ReleaseMouseCapture();
            _surface.RightClick(_viewport.TranslatePoint(press.From, _surface));
            return true;
        }

        _rightPress = null;
        if (_panDrag is null)
            return false;

        _panDrag = null;
        _viewport.ReleaseMouseCapture();
        Cursor = null;
        return true;
    }

    /// <summary>The view now: screen pixels to a source pixel, and where the picture's corner lies in the view.</summary>
    internal (double Zoom, double PanX, double PanY) View => (_zoom, _panX, _panY);

    // ----- Keys -----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A name being typed keeps its letters, its Space and its own Ctrl+Z; Enter or Escape goes back to drawing.
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                _surface.Focus();
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            // Undo: Ctrl+Z. Redo: Ctrl+Y or Ctrl+Shift+Z.
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                _surface.Undo();
                e.Handled = true;
                break;
            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control:
            case Key.Z when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                _surface.Redo();
                e.Handled = true;
                break;
            case Key.Enter when _surface.IsDrawing:
                _surface.CloseShape();
                e.Handled = true;
                break;
            case Key.Back:
                _surface.TakeBackPoint();
                e.Handled = true;
                break;
            case Key.Escape when _surface.IsDrawing:
                _surface.DropShape();
                e.Handled = true;
                break;
            case Key.Space:
                _spaceHeld = true;
                e.Handled = true;
                break;
            case Key.W or Key.A or Key.S or Key.D when (Keyboard.Modifiers & ~ModifierKeys.Shift) == 0:
                _panKeys.Add(e.Key);
                StartTicking();
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
            _spaceHeld = false;
        else
            _panKeys.Remove(e.Key);
    }

    private void UpdateUndoButtons() => (_undoButton.IsEnabled, _redoButton.IsEnabled) = (_surface.CanUndo, _surface.CanRedo);

    private static Button GlyphButton(string glyph, string tip, string name)
    {
        var button = new Button { Content = glyph, FontFamily = Glyphs, FontSize = 13, Padding = new Thickness(10, 4, 10, 4), ToolTip = tip, IsEnabled = false };
        ToolTipService.SetShowOnDisabled(button, true);
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    private void ShowHelp() => _status.Text = _surface.IsDrawing
        ? $"Drawing: {_surface.PointsInShape} point{(_surface.PointsInShape == 1 ? "" : "s")}. Click the glowing first point to close the shape."
        : $"{_surface.ShapeCount} shape{(_surface.ShapeCount == 1 ? "" : "s")}.";

    /// <summary>
    /// The frame at the playhead, as it is in the file (the areas layers cut out are measured on it): of the clip's own file
    /// where it is one of another file (its moment there: the playhead held to the clip), else of the file opened last.
    /// </summary>
    private async Task LoadStillAsync()
    {
        var source = _stillSource;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            _status.Text = "There is no video file to take a still from: shapes can still be drawn on the blank frame.";
            return;
        }

        _status.Text = "Taking a still of the video at the playhead...";
        if (_warm is not null)
        {
            _still.Source = _warm.Picture;
            _navigator.Show(_warm.Picture);
        }

        try
        {
            _still.Source = await SourceStill.TakeAsync(source, StillSeconds(), _stillPath, _closing.Token);
            _navigator.Show(_still.Source as BitmapSource);
            ShowHelp();
            RefreshPieces();
        }
        catch (OperationCanceledException)
        {
            // Closed before the still came.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Cutout Studio: the still could not be taken", ex);
            _status.Text = $"The still could not be taken ({ex.Message}): shapes can still be drawn on the blank frame.";
        }
    }

    // Where the still is taken in its file: the playhead's moment in the clip (held to the clip's span), or (the file last opened)
    // where a video at the playhead shows it.
    private double StillSeconds() => StillSecondsOf(_viewModel, _layer, _stillOnClip);

    private static double StillSecondsOf(MainViewModel viewModel, Layer? layer, bool onClip)
    {
        var at = viewModel.PositionMs / 1000;
        if (onClip && layer is { IsVideoFile: true })
        {
            var end = layer.Duration > 0.001 ? layer.StartTime + layer.Duration : double.PositiveInfinity;
            var inside = Math.Clamp(at, layer.StartTime, Math.Max(layer.StartTime, end - 0.001));
            return Math.Max(layer.MediaOffset + (inside - layer.StartTime) * layer.Speed, 0);
        }

        return viewModel.FileMomentAt(at)?.Seconds ?? at;
    }

    // A video clip's own file (its picture is drawn on over that file's); null for anything else.
    private static string? OwnFileOf(Layer? target) =>
        target is { IsVideoFile: true } && target.ImagePath.Trim().Trim('"') is { Length: > 0 } path ? path : null;

    /// <summary>
    /// The file and moment the window would take its still of, opened for <paramref name="layer"/> (null: the video at the playhead) now:
    /// what the heat lamp decodes ahead (5.4.3). Null when there is no file.
    /// </summary>
    internal static (string Source, double Seconds)? StillTarget(MainViewModel viewModel, Layer? layer)
    {
        var target = layer ?? viewModel.PlayheadVideo;
        var ownFile = OwnFileOf(target);
        var source = ownFile ?? viewModel.LocalMediaPath;
        return string.IsNullOrWhiteSpace(source) ? null : (source, StillSecondsOf(viewModel, target, ownFile is not null));
    }

    private void Finish(bool forAll)
    {
        _surface.CloseShape();
        if (_surface.ShapeCount == 0)
        {
            _status.Text = "Draw at least one shape first, or press Cancel.";
            return;
        }

        Shapes = MaskStore.FormatShapes(_surface.Finished.Select(s => (IReadOnlyList<Point>)s.Select(p => new Point(p.X / _width, p.Y / _height)).ToList()));
        ForAllLayers = forAll;
        DialogResult = true;
    }
}
