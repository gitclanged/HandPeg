using System.IO;
using System.Windows;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>What of a track the dialog sets.</summary>
public enum LayerTool
{
    /// <summary>Filters applied to this track alone.</summary>
    Filters,

    /// <summary>Where it is, how large, and how it is turned.</summary>
    Transform,

    /// <summary>The shape it is cut to.</summary>
    Mask,
}

/// <summary>
/// One of a track's three sets of modifiers, opened from its right-click menu: its own filters, its position and
/// transform, or its mask. Bound straight to the layer, so every change is seen at once; what is changed on
/// one clip of a track is changed on them all.
/// </summary>
public partial class LayerToolDialog : Window
{
    private readonly Layer _layer;
    private readonly MainViewModel _viewModel;
    private readonly LayerTool _tool;

    /// <summary>Set when the dialog was closed in order to mark the layer's area on the video again.</summary>
    public bool RedrawRequested { get; private set; }

    public LayerToolDialog(Layer layer, MainViewModel viewModel, LayerTool tool)
    {
        InitializeComponent();
        (_layer, _viewModel, _tool) = (layer, viewModel, tool);
        DataContext = layer;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        switch (tool)
        {
            case LayerTool.Filters:
                Title = $"Filters: {layer.Name}";
                IntroText.Text = "Filters for this track alone. The filters of the Filters tab are applied to the finished picture, after every track has been laid on it.";
                FiltersPanel.Visibility = Visibility.Visible;
                break;

            case LayerTool.Transform:
                Title = $"Position & Transform: {layer.Name}";
                IntroText.Text = "Where the track sits on the frame, how large it is, and how it is turned. It may lie partly or wholly outside the frame: what is outside is cut off, and the frame keeps its size.";
                TransformPanel.Visibility = Visibility.Visible;

                // The main video is placed about its middle, by the same numbers as the Center controls.
                if (layer.IsMainVideo)
                {
                    (PlacedPanel.Visibility, MainPanel.Visibility) = (Visibility.Collapsed, Visibility.Visible);
                    MainPanel.DataContext = viewModel;
                }

                break;

            default:
                Title = $"Mask: {layer.Name}";
                IntroText.Text = layer.IsVideo
                    ? "The shape this track is cut to. A mask is a black-and-white picture: one of the whole video frame, white over every HUD element, serves every piece cut from the video, each taking its own part of it. Crop cuts away edges of the layer, and the outline rounds or softens it."
                    : "The shape this track is cut to. A mask is a black-and-white picture stretched over the layer; crop cuts away its edges, and the outline rounds or softens the rectangle itself.";
                MaskPanel.Visibility = Visibility.Visible;
                RedrawButton.Visibility = layer.IsVideo ? Visibility.Visible : Visibility.Collapsed;

                // A mask of the whole frame belongs to a piece cut from the video: other layers have no place in the frame to take it from.
                MaskFrameBox.Visibility = FrameMaskButtons.Visibility = layer.IsVideo ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    private void BrowseLut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select a LUT for this layer", Filter = "3D LUT (*.cube)|*.cube|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
            _layer.FilterLut = dialog.FileName;
    }

    private void ClearLut_Click(object sender, RoutedEventArgs e) => _layer.FilterLut = "";

    private void BrowseMask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select a black-and-white mask", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            (_layer.MaskPath, _layer.CustomMask, _layer.MaskShapes) = (dialog.FileName, true, "");

            // A picture in the shape of the video is taken for a mask of the whole frame; anything else is stretched over the layer.
            if (_layer.IsVideo)
                _layer.MaskFrame = IsFrameShaped(dialog.FileName);
        }
    }

    // Whether a picture has the video's shape, to within a few percent: a mask made from a screenshot of it.
    private bool IsFrameShaped(string path)
    {
        if (ImageInfo.GetSize(path) is not ({ } width, { } height) || width <= 0 || height <= 0)
            return false;

        var (sourceWidth, sourceHeight) = _viewModel.SourceWidth > 0 && _viewModel.SourceHeight > 0 ? (_viewModel.SourceWidth, _viewModel.SourceHeight) : (16, 9);
        return Math.Abs((double)width / height / ((double)sourceWidth / sourceHeight) - 1) < 0.03;
    }

    // Draw Custom Shape: the outlines become a mask of the whole frame, drawn into the masks folder.
    private void DrawShape_Click(object sender, RoutedEventArgs e)
    {
        var window = new MaskDrawWindow(_viewModel, _layer) { Owner = this };
        if (window.ShowDialog() != true)
            return;

        var (width, height) = MaskStore.SizeFor(_viewModel.SourceWidth, _viewModel.SourceHeight);
        string? path;
        try
        {
            path = MaskStore.Render(window.Shapes, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.StatusText = $"The mask could not be drawn: {ex.Message}";
            return;
        }

        if (path is null)
            return;

        (_layer.CustomMask, _layer.MaskPath, _layer.MaskFrame, _layer.MaskShapes) = (true, path, true, window.Shapes);
        if (window.ForAllLayers)
            ShareMask();
    }

    private void ShareMask_Click(object sender, RoutedEventArgs e)
    {
        if (!_layer.CustomMask || _layer.MaskPath.Trim().Length == 0)
        {
            _viewModel.StatusText = "Choose or draw a mask of the whole frame first.";
            return;
        }

        _layer.MaskFrame = true;
        ShareMask();
    }

    private void ShareMask()
    {
        var count = _viewModel.ShareFrameMask(_layer);
        _viewModel.StatusText = count == 0
            ? "There are no other layers cut from the video to give the mask to."
            : $"The mask is now on {count + 1} layers cut from the video: each takes its own part of it.";
    }

    private void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || !Enum.TryParse<HudShape>(name, out var shape))
            return;

        try
        {
            if (HudLibrary.EnsureMask(shape) is { } mask)
                (_layer.MaskPath, _layer.CustomMask, _layer.MaskFrame, _layer.MaskShapes) = (mask, true, false, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.StatusText = $"The mask could not be made: {ex.Message}";
        }
    }

    private void NoMask_Click(object sender, RoutedEventArgs e) => (_layer.CustomMask, _layer.MaskPath, _layer.MaskFrame, _layer.MaskShapes) = (false, "", false, "");

    private void Redraw_Click(object sender, RoutedEventArgs e)
    {
        RedrawRequested = true;
        Close();
    }

    private void Center_Click(object sender, RoutedEventArgs e)
    {
        if (_layer.IsMainVideo)
        {
            (_viewModel.CenterOffsetX, _viewModel.CenterOffsetY) = (0, 0);
            return;
        }

        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
        var (_, _, width, height) = _layer.GetOutputRect(frameWidth, frameHeight, _viewModel.SourceWidth, _viewModel.SourceHeight);
        (_layer.PositionX, _layer.PositionY) = ((frameWidth - width) / 2.0 / frameWidth, (frameHeight - height) / 2.0 / frameHeight);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        switch (_tool)
        {
            case LayerTool.Filters:
                _layer.ResetFilters();
                break;

            case LayerTool.Transform:
                _layer.ResetTransform();
                if (_layer.IsMainVideo)
                    _viewModel.ResetCenterVideoCommand.Execute(null);
                break;

            default:
                _layer.ResetMask();
                break;
        }
    }
}
