using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Settings screen live mirror of the editor -----
// The Settings stage shows the editor region the focused option affects, live, via a VisualBrush (SettingsStagePreview.ShowMirror).
// Only visible panes are offered; closed, hidden, background-tab or unrealized panes are never realized for it (that would cost memory
// and work for a pane the user did not open). The player video renders in a separate GPU child window invisible to brushes, so a
// thumbnail-sheet still at the playhead stands in for it.
public partial class MainWindow
{
    // Cached mirror still (source sheet, cell index, image), reused while the playhead stays in that cell.
    private (BitmapSource Sheet, int Index, ImageSource Still)? _mirrorStill;

    /// <summary>
    /// Mirror source for a Settings stage region ("Window", "Header", "Timeline", "Player", "Canvas", "Media", "Effects", "Export"):
    /// the element, its view rect (local bounds), and for the player a stand-in video still with its rect; null when not visible.
    /// </summary>
    internal (FrameworkElement Source, Rect View, ImageSource? Video, Rect VideoRect)? StageMirror(string region)
    {
        FrameworkElement? source = region switch
        {
            "Window" => ShownForMirror(ChromeRoot),
            "Header" => ShownForMirror(EditorHeader),
            // Timeline pane, falling back to the Overview Scrubber when the Timeline is not visible.
            "Timeline" => ShownForMirror(UnifiedTimelinePanel) ?? ShownForMirror(TimelinePane),
            "Player" => ShownForMirror(PlayerPane),
            "Canvas" => ShownForMirror(LayoutPane),
            "Media" => ShownForMirror(MediaBrowserPanel),
            "Effects" => ShownForMirror(EffectsPanePanel),
            "Export" => ShownForMirror(ExportPanel),
            _ => null,
        };

        if (source is null)
            return null;

        var (still, videoRect) = (default(ImageSource), Rect.Empty);
        if (ReferenceEquals(source, PlayerPane) && VideoView.IsVisible && VideoView.IsDescendantOf(PlayerPane))
        {
            videoRect = VideoView.TransformToAncestor(PlayerPane).TransformBounds(new Rect(VideoView.RenderSize));
            still = StillAtPlayhead();
        }

        return (source, new Rect(0, 0, source.ActualWidth, source.ActualHeight), still, videoRect);
    }

    // Returns the element if visible, laid out and at least 40 x 20 DIPs; else null (including unrealized template parts such as the
    // Export tab body). Never realizes a pane.
    private static FrameworkElement? ShownForMirror(FrameworkElement? element) =>
        element is { IsVisible: true, ActualWidth: >= 40, ActualHeight: >= 20 } ? element : null;

    /// <summary>
    /// Overview Scrubber thumbnail nearest the playhead, sliced as RebuildFilmstrip does; null without a sheet. Uses the GPU-rendered
    /// sequence sheet, else the source file's keyframe sheet (close enough in time to stand in).
    /// </summary>
    private ImageSource? StillAtPlayhead()
    {
        var duration = _viewModel.DurationMs / 1000;
        if (OverviewSheet is not { } sheet || duration <= 0)
            return null;

        var index = Math.Min(sheet.IndexAt(_viewModel.PositionMs / 1000), LastPicture(sheet, duration));
        if (_mirrorStill is { } kept && ReferenceEquals(kept.Sheet, sheet.Picture) && kept.Index == index)
            return kept.Still;

        var still = sheet.Cell(index);
        _mirrorStill = (sheet.Picture, index, still);
        return still;
    }
}
