using System.IO;
using System.Windows;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>What of a clip the dialog sets.</summary>
public enum LayerTool
{
    /// <summary>Filters applied to this clip alone.</summary>
    Filters,

    /// <summary>The shape it is cut to.</summary>
    Mask,
}

/// <summary>
/// A clip's filters or its mask, opened from its right-click menu (where it sits on the frame is the Timeline's inspector strip, the Canvas and
/// the menu's Transform, Sizing & Position). Bound straight to the clip, so every change is seen at once; what is changed is this clip's alone.
/// </summary>
public partial class LayerToolDialog : Window
{
    private readonly Layer _layer;
    private readonly MainViewModel _viewModel;
    private readonly LayerTool _tool;

    /// <summary>Set when the dialog was closed in order to mark the clip's area on the video again.</summary>
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
                IntroText.Text = "Filters for this clip alone. The filters of the Filters tab are applied to the finished picture, after every track has been laid on it.";
                FiltersPanel.Visibility = Visibility.Visible;
                break;

            default:
                Title = $"Mask: {layer.Name}";
                IntroText.Text = layer.IsVideo
                    ? "The shape this clip is cut to. A mask is a black-and-white picture: one of the whole video frame, white over every HUD element, serves every Cutout of the video, each taking its own part of it. Crop cuts away edges of the clip, and the outline rounds or softens it."
                    : "The shape this clip is cut to. A mask is a black-and-white picture stretched over the clip; crop cuts away its edges, and the outline rounds or softens the rectangle itself.";
                MaskPanel.Visibility = Visibility.Visible;
                RedrawButton.Visibility = layer.IsVideo || viewModel.IsCutout(layer) ? Visibility.Visible : Visibility.Collapsed;

                // A mask of the whole frame belongs to a Cutout of the video: other clips have no place in the frame to take it from.
                MaskFrameBox.Visibility = FrameMaskButtons.Visibility = MaskLayersButton.Visibility = layer.IsVideo ? Visibility.Visible : Visibility.Collapsed;
                break;
        }

        // The dialog's own Undo (4.3E): each press of the pointer or a key that changes what the shown controls are bound to
        // (and the drawn mask's shapes, which buttons set) is a step. The whole visit is one step of the main window's Undo,
        // taken as the dialog opens (MainWindow.OpenLayerTool).
        _undo = new BoundUndo(this, () => _tool == LayerTool.Mask ? [(_layer, nameof(Layer.MaskShapes))] : []);
        _undo.Changed += () => (UndoButton.IsEnabled, RedoButton.IsEnabled) = (_undo.CanUndo, _undo.CanRedo);
    }

    private readonly BoundUndo _undo;

    private void Undo_Click(object sender, RoutedEventArgs e) => _undo.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => _undo.Redo();

    private void BrowseLut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select a LUT for this clip", Filter = "3D LUT (*.cube)|*.cube|All files|*.*" };
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

            // A picture in the shape of the video is taken for a mask of the whole frame; anything else is stretched over the clip.
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

    // Draw Custom Shape: the outlines become a mask of the whole frame, kept as the outlines alone (no picture file: 5.1-Release).
    private void DrawShape_Click(object sender, RoutedEventArgs e)
    {
        var window = new MaskDrawWindow(_viewModel, _layer) { Owner = this };
        if (window.ShowDialog() != true || MaskStore.DrawnKey(window.Shapes).Length == 0)
            return;

        (_layer.CustomMask, _layer.MaskPath, _layer.MaskFrame, _layer.MaskShapes) = (true, "", true, window.Shapes);
        if (window.ForAllLayers)
            ShareMask();
    }

    private void MaskLayers_Click(object sender, RoutedEventArgs e)
    {
        var path = _layer.MaskPath.Trim().Trim('"');
        if (!_layer.CustomMask || !File.Exists(path))
        {
            _viewModel.StatusText = "Choose or draw a mask of the whole frame first.";
            return;
        }

        if (Owner is MainWindow main)
        {
            main.AddLayersFromMask(path, this);
            _undo.Forget();
        }
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
        _undo.Forget();
        var count = _viewModel.ShareFrameMask(_layer);
        _viewModel.StatusText = count == 0
            ? "There are no other Cutouts of the video to give the mask to."
            : $"The mask is now on {count + 1} Cutouts of the video: each takes its own part of it.";
    }

    private void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || !Enum.TryParse<HudShape>(name, out var shape))
            return;

        // Outlines only (5.2-Audit): nothing is written to the Masks folder. The shape is soft-edged, as its picture was.
        if (HudLibrary.Outline(shape) is not { } outline)
            return;
        (_layer.MaskPath, _layer.CustomMask, _layer.MaskFrame, _layer.MaskShapes) = ("", true, false, outline);
        if (!_layer.Feather)
            (_layer.Feather, _layer.FeatherRadius) = (true, HudLibrary.ShapeFeatherRadius);
    }

    private void NoMask_Click(object sender, RoutedEventArgs e) => (_layer.CustomMask, _layer.MaskPath, _layer.MaskFrame, _layer.MaskShapes) = (false, "", false, "");

    private void Redraw_Click(object sender, RoutedEventArgs e)
    {
        RedrawRequested = true;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        switch (_tool)
        {
            case LayerTool.Filters:
                _layer.ResetFilters();
                break;

            default:
                _layer.ResetMask();
                break;
        }
    }
}
