using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- The magnifier loupe's wiring (Pass 5.0-Verify, the user's bug 6) -----
// The panes that own the edge sliders say when one is held (MainViewModel.BeginEdgeTuning / EndEdgeTuning); here that
// becomes the GPU engine's loupe (GpuPreviewEngine.SetLoupe), which the worker draws (SceneRenderer.Loupe.cs). The main
// video's Center controls stand for one of its clips (CenterTarget): the loupe looks at that clip.
public partial class MainWindow
{
    private void HookLoupe() => _viewModel.EdgeTuningChanged += SyncLoupe;

    /// <summary>The edge tuned now to the GPU engine: its clip's key and kind, or none.</summary>
    private void SyncLoupe()
    {
        if (_gpu is not { } engine)
            return;

        long key = 0;
        var kind = _viewModel.EdgeTuning;
        if (_viewModel.EdgeTuningLayer is { } layer && kind != EdgeTuningKind.None)
        {
            var clip = layer.ClipId != Guid.Empty ? layer : layer.IsMainVideo ? _viewModel.CenterTarget : null;
            if (clip is { ClipId: var id } && id != Guid.Empty)
                key = GpuHash.Of(id);
        }

        engine.SetLoupe(key, key == 0 ? 0 : (int)kind);
    }
}
