using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Layers from a Mask: a mask of the whole frame, white over a game's HUD, taken apart into one layer per HUD
/// element. Every patch the mask shows is found; patches closer together than the distance set are one
/// element, so a row of separately traced ability icons becomes one layer and not one each. The pieces are
/// shown over a still of the video as they will be cut, numbered, before anything is added; a click on one
/// leaves it out or takes it back. Pieces that a layer already cuts out start left out, so the same window
/// adds the pieces a preset did not (an ultimate meter, say). Built in code.
/// </summary>
public sealed class MaskLayersWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly List<Rect> _islands;
    private readonly List<Rect> _existing;

    // Where pieces were clicked: a piece with an odd number of clicks in it is the other way round from how it started.
    private readonly List<Point> _toggles = [];
    private List<Rect> _all = [];
    private List<bool> _included = [];
    private readonly double _width, _height;
    private readonly Image _still = new() { Stretch = Stretch.Fill };
    private readonly PieceSurface _surface;
    private readonly Slider _join = new() { Minimum = 0, Maximum = 300, Width = 240, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 2 };
    private readonly TextBlock _joinText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 50 };
    private readonly CheckBox _vertical = new() { Content = "Lay out for vertical video", Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _replace = new() { Content = "Remove the layers already cut from the video", Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _add = new() { MinWidth = 120, Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
    private readonly string _stillPath = Path.Combine(SessionPaths.Previews, $"mask_still_{Guid.NewGuid():N}.png");
    private readonly CancellationTokenSource _closing = new();

    /// <summary>The pieces, as fractions of the frame, once the window was confirmed.</summary>
    public List<Rect> Pieces { get; private set; } = [];

    public bool Vertical => _vertical.IsChecked == true;
    public bool ReplaceExisting => _replace.IsChecked == true;

    /// <summary>Null when the mask cannot be read, or shows nothing: the window is then not shown.</summary>
    /// <param name="existing">The areas layers already cut out of the video, as fractions of the frame.</param>
    public static MaskLayersWindow? Open(MainViewModel viewModel, string maskPath, List<Rect> existing) =>
        MaskStore.FindIslands(maskPath) is { Count: > 0 } islands ? new MaskLayersWindow(viewModel, maskPath, islands, existing) : null;

    private MaskLayersWindow(MainViewModel viewModel, string maskPath, List<Rect> islands, List<Rect> existing)
    {
        (_viewModel, _islands, _existing) = (viewModel, islands, existing);
        (_width, _height) = viewModel.SourceWidth > 0 && viewModel.SourceHeight > 0 ? (viewModel.SourceWidth, viewModel.SourceHeight) : (1920.0, 1080.0);

        Title = $"Layers from a Mask: {Path.GetFileName(maskPath)}";
        Width = 1180;
        Height = 820;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        // The mask itself, faintly over the still: what shows of it is where the HUD elements were found.
        var mask = new Image { Stretch = Stretch.Fill, Opacity = 0.45, IsHitTestVisible = false };
        try
        {
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.UriSource = new Uri(maskPath);
            picture.EndInit();
            picture.Freeze();
            mask.Source = picture;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            // The pieces are still drawn.
        }

        _surface = new PieceSurface(_width, _height);
        _surface.Clicked += at =>
        {
            _toggles.Add(at);
            Regroup();
        };
        var frame = new Grid { Width = _width, Height = _height, Background = Brushes.Black };
        frame.Children.Add(_still);
        frame.Children.Add(mask);
        frame.Children.Add(_surface);
        var view = new Viewbox { Stretch = Stretch.Uniform, Child = frame };

        var help = new TextBlock
        {
            Text = "Every patch the mask shows is found, and those closer together than the distance below are one HUD element: one layer each, all sharing this mask. "
                   + "Raise the distance until each element is one numbered box (a row of ability icons traced one by one, say), and lower it if two elements have run together. "
                   + "Click a box to leave it out, or to take it back: pieces a layer already cuts out start left out.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var joinRow = new StackPanel { Orientation = Orientation.Horizontal };
        joinRow.Children.Add(new TextBlock { Text = "Join patches closer than:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        joinRow.Children.Add(_join);
        joinRow.Children.Add(_joinText);
        _join.ToolTip = "In pixels of a 2560-wide frame. Patches nearer each other than this, across or down, are one HUD element and one layer.";
        _join.Value = 40;
        _join.ValueChanged += (_, _) => Regroup();

        _vertical.IsChecked = viewModel.FrameHeight > viewModel.FrameWidth;
        _vertical.ToolTip = "Makes the frame tall, with the video across its middle and the HUD elements enlarged above and below it. Cleared, each element is laid back where it was cut from.";
        _replace.ToolTip = "Takes away the layers already cut from the video (into the recycle bin, so Undo brings them back), for a mask that is to replace them. Every piece then starts included.";
        _replace.Checked += (_, _) => Regroup();
        _replace.Unchecked += (_, _) => Regroup();

        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        _add.Click += (_, _) => DialogResult = Pieces.Count > 0;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_add);
        buttons.Children.Add(cancel);

        var options = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        options.Children.Add(joinRow);
        options.Children.Add(_vertical);
        options.Children.Add(_replace);
        options.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(help, Dock.Top);
        DockPanel.SetDock(options, Dock.Bottom);
        root.Children.Add(help);
        root.Children.Add(options);
        root.Children.Add(new Border { Child = view, Background = Brushes.Black });
        Content = root;

        Regroup();
        SizeChanged += (_, _) => _surface.InvalidateVisual();
        Loaded += async (_, _) => await LoadStillAsync();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            SourceStill.Delete(_stillPath);
        };
    }

    private void Regroup()
    {
        _all = MaskStore.GroupIslands(_islands, _join.Value / 2560, _width / _height);
        _included = _all.Select(piece =>
        {
            // Left out to begin with when a layer already cuts most of it out (and its layers are not being replaced).
            var covered = !ReplaceExisting && _existing.Any(area => Rect.Intersect(area, piece) is { IsEmpty: false } overlap
                                                                     && overlap.Width * overlap.Height > 0.5 * piece.Width * piece.Height);
            var clicks = _toggles.Count(point => piece.Contains(point));
            return covered == (clicks % 2 == 1);
        }).ToList();

        Pieces = _all.Where((_, i) => _included[i]).ToList();
        _joinText.Text = $"{_join.Value:0} px";
        _add.Content = Pieces.Count == 1 ? "Add 1 Layer" : $"Add {Pieces.Count} Layers";
        _add.IsEnabled = Pieces.Count > 0;
        _surface.Show(_all, _included);
    }

    private async Task LoadStillAsync()
    {
        var source = _viewModel.LocalMediaPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;

        try
        {
            _still.Source = await SourceStill.TakeAsync(source, _viewModel.PositionMs / 1000, _stillPath, _closing.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Layers from a Mask: the still could not be taken", ex);
        }
    }

    /// <summary>
    /// The pieces over the still, as numbered boxes (solid when they are to be added, grey and dashed when left
    /// out), in the source's pixels; lines are sized to stay thin when scaled. A click is passed on as a point
    /// in fractions of the frame.
    /// </summary>
    private sealed class PieceSurface(double width, double height) : FrameworkElement
    {
        private static readonly Brush Line = Frozen(Color.FromRgb(0xFF, 0xD0, 0x40));
        private static readonly Brush Skipped = Frozen(Color.FromArgb(0xC0, 0xA0, 0xA0, 0xA0));
        private static readonly Brush LabelBack = Frozen(Color.FromArgb(0xD0, 0, 0, 0));
        private List<Rect> _pieces = [];
        private List<bool> _included = [];

        public event Action<Point>? Clicked;

        public void Show(List<Rect> pieces, List<bool> included)
        {
            (_pieces, _included, Width, Height) = (pieces, included, width, height);
            Cursor = System.Windows.Input.Cursors.Hand;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
        {
            var at = e.GetPosition(this);
            Clicked?.Invoke(new Point(at.X / width, at.Y / height));
        }

        protected override void OnRender(DrawingContext dc)
        {
            var scale = Window.GetWindow(this) is { } window && window.IsAncestorOf(this)
                ? TransformToAncestor(window).TransformBounds(new Rect(0, 0, 1, 1)).Width
                : 1;
            var px = scale > 0 ? 1 / scale : 1;
            // Something to click on everywhere: a click in a box toggles it.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            var pen = new Pen(Line, 2 * px);
            var skippedPen = new Pen(Skipped, 1.5 * px) { DashStyle = DashStyles.Dash };
            for (int i = 0, number = 0; i < _pieces.Count; i++)
            {
                var included = i < _included.Count && _included[i];
                var box = new Rect(_pieces[i].X * width, _pieces[i].Y * height, _pieces[i].Width * width, _pieces[i].Height * height);
                dc.DrawRectangle(null, included ? pen : skippedPen, box);
                var text = included ? $"{++number}  {MaskStore.PlaceName(_pieces[i])}" : "left out";
                var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 12 * px, included ? Brushes.White : Skipped, 1);
                var at = new Point(box.X, Math.Max(box.Y - label.Height - 2 * px, 0));
                dc.DrawRectangle(LabelBack, null, new Rect(at.X, at.Y, label.Width + 6 * px, label.Height));
                dc.DrawText(label, new Point(at.X + 3 * px, at.Y));
            }
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
