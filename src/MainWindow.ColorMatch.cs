using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models.Effects;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegApp;

// ----- Color Match host: the window side of the standalone ColorMatchBar (src/Controls/ColorMatchBar.cs) -----
// The bar reaches the window only through IColorMatchHost (explicit members below): the view model, the Color Studio, the picture state,
// one frame capture, the player overlay as the pick surface and the ColorMatchHost canvas (MainWindow.xaml, topmost over the picture) as its
// loupe layer. Pick mode ends itself on playback, seeks, edits, source views and Esc; switching project tabs ends it here (EndColorMatchPicks).
public partial class MainWindow : IColorMatchHost
{
    /// <summary>
    /// Creates the Color Studio's Color Match bar; <paramref name="target"/> is evaluated at use time.
    /// </summary>
    internal FrameworkElement? CreateColorMatchBar(Func<ColorGradeTarget> target)
    {
        _viewerTools.ColorMatchBars.RemoveAll(bar => !bar.TryGetTarget(out _));
        var created = new ColorMatchBar(this, target);
        _viewerTools.ColorMatchBars.Add(new WeakReference<ColorMatchBar>(created));
        return created;
    }

    /// <summary>Ends any Color Match pick in progress (project tab switch: its captured frame belongs to the tab being left).</summary>
    private void EndColorMatchPicks()
    {
        foreach (var weak in _viewerTools.ColorMatchBars)
        {
            if (weak.TryGetTarget(out var bar))
                bar.CancelPick();
        }
    }

    MainViewModel IColorMatchHost.ViewModel => _viewModel;

    ColorStudioPane? IColorMatchHost.Studio => _colorStudio;

    bool IColorMatchHost.IsExploded => _explodedUi;

    bool IColorMatchHost.HasEngine => _gpu is not null;

    bool IColorMatchHost.IsPlaying => PlayerIsPlaying;

    void IColorMatchHost.Pause() => PlayerSetPause(true);

    Task<(byte[] Bgra, int Width, int Height)?> IColorMatchHost.CaptureFrameAsync() =>
        _gpu is { } engine ? engine.CaptureFrameAsync() : Task.FromResult<(byte[] Bgra, int Width, int Height)?>(null);

    Point? IColorMatchHost.PictureFocalAt(Point surfacePoint) => PictureFocalAt(surfacePoint);

    FrameworkElement IColorMatchHost.PickSurface => PlayerOverlay;

    Panel IColorMatchHost.PickLayer => ColorMatchHost;

    UIElement IColorMatchHost.KeyScope => this;
}
