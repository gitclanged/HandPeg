using HandPegApp.Models;

namespace HandPegApp.ViewModels;

// ----- Edge tuning and player magnifier loupe -----
// While an edge-shaping slider is held (feather, mask grow/offset, matte choke/smooth, chroma key tolerance/softness), the GPU player
// draws a 400 % corner loupe of that layer's edge for sub-pixel inspection. Owning panes (Effects cards, inspector mask/feather rows)
// signal drag start/end; the window forwards to the GPU engine (MainWindow.Loupe.cs). Transient view state: never saved or undone.
public partial class MainViewModel
{
    /// <summary>Layer whose edge is being tuned (null when none) and the tuning kind.</summary>
    public Layer? EdgeTuningLayer { get; private set; }

    public EdgeTuningKind EdgeTuning { get; private set; }

    /// <summary>Raised when edge tuning starts, changes kind/layer, or ends.</summary>
    public event Action? EdgeTuningChanged;

    /// <summary>Begins edge tuning on <paramref name="layer"/> until <see cref="EndEdgeTuning"/>.</summary>
    public void BeginEdgeTuning(Layer? layer, EdgeTuningKind kind)
    {
        if (layer is null || kind == EdgeTuningKind.None)
        {
            EndEdgeTuning();
            return;
        }

        if (ReferenceEquals(EdgeTuningLayer, layer) && EdgeTuning == kind)
            return;
        (EdgeTuningLayer, EdgeTuning) = (layer, kind);
        EdgeTuningChanged?.Invoke();
    }

    /// <summary>Ends edge tuning (slider released or pane lost capture).</summary>
    public void EndEdgeTuning()
    {
        if (EdgeTuningLayer is null && EdgeTuning == EdgeTuningKind.None)
            return;
        (EdgeTuningLayer, EdgeTuning) = (null, EdgeTuningKind.None);
        EdgeTuningChanged?.Invoke();
    }
}
