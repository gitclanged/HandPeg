using HandPegApp.Models;

namespace HandPegApp.ViewModels;

// ----- Edge tuning and the player's magnifier loupe (5.0.0) -----
// While a slider that shapes a picture's edge is held (a feather, a mask's grow or offset, a matte's choke or smooth, a chroma
// key's tolerance and softness), the GPU player draws a 400 % loupe of that layer's edge in a corner of the picture, so the
// sub-pixel edge can be seen while it changes. The panes that own those sliders (the Effects pane's cards, the inspector's
// mask and feather rows) say when such a drag starts and ends; the window passes it to the GPU engine (MainWindow.Loupe.cs).
// Nothing here is saved or undone: it only says what is being looked at.
public partial class MainViewModel
{
    /// <summary>The layer whose edge is being tuned now (null: none) and how.</summary>
    public Layer? EdgeTuningLayer { get; private set; }

    public EdgeTuningKind EdgeTuning { get; private set; }

    /// <summary>Raised when an edge's tuning starts, changes kind or layer, or ends.</summary>
    public event Action? EdgeTuningChanged;

    /// <summary>A slider shaping <paramref name="layer"/>'s edge is held from now on (until <see cref="EndEdgeTuning"/>).</summary>
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

    /// <summary>The slider is let go (or the pane lost it): no edge is tuned now.</summary>
    public void EndEdgeTuning()
    {
        if (EdgeTuningLayer is null && EdgeTuning == EdgeTuningKind.None)
            return;
        (EdgeTuningLayer, EdgeTuning) = (null, EdgeTuningKind.None);
        EdgeTuningChanged?.Invoke();
    }
}
