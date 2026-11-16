using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

// ----- Speed & Time card per-clip options -----
// Look properties (not ClipFields), flushed into State with the look, serialized only when non-default (Layer.Serialization.cs), and
// preserved over track looks by TimelineLowering.Merged.
public sealed partial class Layer
{
    /// <summary>Smooth Slow-Mo: slower than 1x, the picture is drawn as optical-flow in-betweens instead of repeated frames.</summary>
    [ObservableProperty] private bool _smoothSlowMo;

    /// <summary>Keep Pitch: a sped-up or slowed-down sound keeps its pitch (on, as it always did); off, it plays like tape.</summary>
    [ObservableProperty] private bool _keepPitch = true;

    /// <summary>Spectral Audio: a stretched sound goes through the phase vocoder instead of WSOLA (smooth slow-motion sound).</summary>
    [ObservableProperty] private bool _spectralAudio;

    /// <summary>Takes the speed options of a saved layer (null: the default), as its look (ApplyLook).</summary>
    private void TakeSpeedOptions(LayerState state)
    {
        SmoothSlowMo = state.SmoothSlowMo == true;
        KeepPitch = state.KeepPitch != false;
        SpectralAudio = state.SpectralAudio == true;
    }
}
