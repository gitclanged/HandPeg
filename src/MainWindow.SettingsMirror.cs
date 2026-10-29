using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The Settings screen's live mirror of the editor (Pass 4.4) -----
// The Settings screen shows, above its drawn pictures, the part of the editor the option looked at is about, as it is now
// (a VisualBrush of it: SettingsStagePreview.ShowMirror). Only a pane that is on show is offered: one that is closed,
// hidden, behind another tab or not made yet is never made or shown for it (that would cost the memory and the work of a
// pane the user did not open). The player's picture is drawn by mpv in a window of its own, which no brush can see: a
// still from the thumbnail sheet at the playhead stands in for it.
public partial class MainWindow
{
    // The still last cut for the mirror: the sheet it came from, its cell, and the picture, kept while the playhead stays in that cell.
    private (BitmapSource Sheet, int Index, ImageSource Still)? _mirrorStill;

    /// <summary>
    /// The element of the editor a region of the Settings stage mirrors ("Window", "Header", "Timeline", "Player",
    /// "Canvas", "Media", "Effects", "Export"), the part of it to show (its own bounds, in its own coordinates), and for the
    /// player a still standing in for the video with where the video lies in it; null when that part is not on show.
    /// </summary>
    internal (FrameworkElement Source, Rect View, ImageSource? Video, Rect VideoRect)? StageMirror(string region)
    {
        FrameworkElement? source = region switch
        {
            "Window" => ShownForMirror(ChromeRoot),
            "Header" => ShownForMirror(EditorHeader),
            // The timeline pane; the overview bar while the timeline is not on show.
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

    // An element that is on show, laid out and large enough to be worth a picture; else null. Nothing here realizes a pane.
    private static FrameworkElement? ShownForMirror(FrameworkElement element) =>
        element is { IsVisible: true, ActualWidth: >= 40, ActualHeight: >= 20 } ? element : null;

    /// <summary>
    /// The thumbnail sheet's picture nearest the playhead, as the timeline's strip cuts them (RebuildFilmstrip), or null
    /// while there is no sheet. The sheet is made from the main video's own time; the playhead is the sequence's, which
    /// is near enough for a stand-in.
    /// </summary>
    private ImageSource? StillAtPlayhead()
    {
        var (interval, duration) = (_viewModel.SpriteIntervalSeconds, _viewModel.DurationMs / 1000);
        if (_spriteSheet is not { } sheet || interval <= 0 || duration <= 0)
            return null;

        const int grid = FfmpegRunner.SpriteGridSize;
        var (cellWidth, cellHeight) = (sheet.PixelWidth / grid, sheet.PixelHeight / grid);
        if (cellWidth <= 0 || cellHeight <= 0)
            return null;

        var last = Math.Max(Math.Min(grid * grid, (int)Math.Ceiling(duration / interval)) - 1, 0);
        var index = Math.Clamp((int)(_viewModel.PositionMs / 1000 / interval), 0, last);
        if (_mirrorStill is { } kept && ReferenceEquals(kept.Sheet, sheet) && kept.Index == index)
            return kept.Still;

        var still = new CroppedBitmap(sheet, new Int32Rect(index % grid * cellWidth, index / grid * cellHeight, cellWidth, cellHeight));
        still.Freeze();
        _mirrorStill = (sheet, index, still);
        return still;
    }
}
