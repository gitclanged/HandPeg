namespace HandPegApp.Services;

// ----- The GPU roles (Pass 5.0-Core, micro-step 5.0-0): which graphics card does what -----
//
// Four roles, each a DXGI adapter index (the number GpuAdapters lists and FFmpeg's -hwaccel_device takes), -1 for Auto.
// Settings > Export & Hardware > GPU Roles shows them; Test & Auto-Tune Hardware (HardwareBenchmarkService) sets all four
// from what it measured, each still the user's to change. The fourth, Background, is BackgroundGpu (AppSettings.cs).

public sealed partial class AppSettings
{
    /// <summary>
    /// Viewport &amp; HLSL Shader Compositor GPU: the card the GPU compositor draws the player and renders exports' frames on.
    /// -1 is Auto: the high-performance card (the one Windows gives demanding programs).
    /// </summary>
    public int CompositorGpuAdapter { get; set; } = -1;

    /// <summary>
    /// Timeline Layer Hardware Decode GPU: the card that decodes the timeline's video layers for the compositor. -1 is Auto:
    /// the compositor's own card, so decoded frames never leave its memory (zero-copy). Another card copies each frame across.
    /// </summary>
    public int DecodeGpuAdapter { get; set; } = -1;

    /// <summary>
    /// Hardware Encode Export GPU: the card an export decodes (and encodes) on. The same setting as
    /// <see cref="HardwareDecodeAdapter"/>, which keeps its saved name: this is only the role's name for it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ExportGpuAdapter
    {
        get => HardwareDecodeAdapter;
        set => HardwareDecodeAdapter = value;
    }

    // ----- The preview engine (Pass 5.0-AudioAnd3D; the only player since 5.0-Verify) -----
    // The player is the GPU compositor with Instant Seek and the WASAPI mixer (GpuPreviewEngine); timeline and media
    // browser strips are drawn by a compositor too (GpuFilmstrips, on the Background GPU). Settings > Export &amp; Hardware >
    // GPU Preview Engine (SettingsWindow.GpuEngine.cs).

    /// <summary>
    /// Instant Seek's frame cache in video memory, MB: 0 Auto (an eighth of the compositor card's own memory, at most
    /// 3 GB), -1 off (every frame decoded when shown). Windows' own budget for the process always caps it. Read when the
    /// player's compositor starts, and put into effect in the running one when Settings is saved
    /// (<see cref="CompositorClient.FrameCacheBudget"/>).
    /// </summary>
    public int GpuFrameCacheMb { get; set; }

    /// <summary>
    /// Decode threads of the compositor (0 Auto: two, as most cards decode two streams at full speed at once; at most 4).
    /// Read when the player's compositor starts: a change applies after a restart.
    /// </summary>
    public int GpuDecodeThreads { get; set; }
}
