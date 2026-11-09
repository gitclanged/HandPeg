namespace HandPegApp.Services;

// ----- The GPU roles (Pass 5.0-Core, micro-step 5.0-0): which graphics card does what -----
//
// Four roles, each a DXGI adapter index (the number GpuAdapters lists and FFmpeg's -hwaccel_device takes), -1 for Auto.
// Settings > Export & Hardware > GPU & NPU Roles shows them; Test & Auto-Tune Hardware (HardwareBenchmarkService) sets all four
// from what it measured, each still the user's to change. The fourth, Background, is BackgroundGpu (AppSettings.cs).

public sealed partial class AppSettings
{
    /// <summary>
    /// Viewport &amp; HLSL Shader Compositor GPU: the card the GPU compositor draws the player and renders exports' frames on.
    /// -1 is Auto: the high-performance card (the one Windows gives demanding programs).
    /// </summary>
    public int CompositorGpuAdapter { get; set; } = -1;

    /// <summary>
    /// Timeline Clip Hardware Decode GPU: the card that decodes the timeline's video clips for the compositor. -1 is Auto:
    /// the compositor's own card, so decoded frames never leave its memory (zero-copy). Another card copies each frame across.
    /// </summary>
    public int DecodeGpuAdapter { get; set; } = -1;

    /// <summary>
    /// Export GPU: the card an export and a Render Preview are composited on, their clips decoded there too (5.4C,
    /// GpuExport.ExportAdapter); -1 (Auto), or a card that is gone, is the <see cref="CompositorGpuAdapter"/>. Frames reach the encoder
    /// through system memory, so this need not be the card whose hardware encoder is chosen (that one is picked by the encoder's name).
    /// The same setting as <see cref="HardwareDecodeAdapter"/>, which keeps its saved name: this is only the role's name for it.
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
    /// The most Instant Seek's frame cache may hold, MB (Settings' "Maximum Frame Cache (GB)", 5.4D-TabPerf, the user's U3). The cache
    /// is adaptive: it starts at about 2 GB (less on unified memory with little of it, 512 MB on WARP or a small card) and grows a
    /// gigabyte at a time when a project needs more, up to this; a tab put behind or a project closed brings it back to its start.
    /// 0 Auto: the hardware's ceiling (a card with more than 4 GB of its own, half of what is free on it, at most 12 GB, a margin
    /// always left, an eighth of the card at most 3 GB while a game uses it; unified memory or a card of 4 GB or less, at most 4 GB,
    /// and an eighth of the computer's memory below 32 GB: Compositor.MemoryTier). N: the lower of N and that ceiling. -1 off (every
    /// frame decoded when shown). Windows' own budget for the process and its commit always cap it. Read when the player's compositor
    /// starts, and put into effect in the running one when Settings is saved (<see cref="CompositorClient.FrameCacheBudget"/>).
    /// </summary>
    public int GpuFrameCacheMb { get; set; }

    /// <summary>
    /// How much memory the project tabs kept live in the editor (5.4D-TabPerf: each tab brought to the front once keeps its
    /// Sequence, history, still and pictures, so a switch is a swap) may hold together, MB, before the tab used least recently goes
    /// back to its file on disk. The two tabs used last always stay.
    /// </summary>
    public int HydratedTabBudgetMb { get; set; } = 1024;

    /// <summary>
    /// Decode threads of the compositor (0 Auto: two, as most cards decode two streams at full speed at once; at most 4).
    /// Read when the player's compositor starts: a change applies after a restart.
    /// </summary>
    public int GpuDecodeThreads { get; set; }
}
