using HandPegApp.Models;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Edge loupe wiring -----
// Panes owning edge sliders report holds (MainViewModel.BeginEdgeTuning / EndEdgeTuning); this forwards them to the GPU engine's loupe
// (GpuPreviewEngine.SetLoupe), rendered by the worker (SceneRenderer.Loupe.cs). It targets the edge's clip, or for clip-less boxes
// (Crop boxes) the clip they edit (CropTarget).
public partial class MainWindow
{
    private void HookLoupe() => _viewModel.EdgeTuningChanged += SyncLoupe;

    /// <summary>Pushes the current edge-tuning target (clip key and edge kind, or none) to the GPU engine.</summary>
    private void SyncLoupe()
    {
        if (_gpu is not { } engine)
            return;

        long key = 0;
        var kind = _viewModel.EdgeTuning;
        if (_viewModel.EdgeTuningLayer is { } layer && kind != EdgeTuningKind.None)
        {
            var clip = layer.ClipId != Guid.Empty ? layer : _viewModel.CropTarget;
            if (clip is { ClipId: var id } && id != Guid.Empty)
                key = GpuHash.Of(id);
        }

        engine.SetLoupe(key, key == 0 ? 0 : (int)kind);
    }
}
