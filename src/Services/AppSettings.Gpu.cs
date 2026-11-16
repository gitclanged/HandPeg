namespace HandPegApp.Services;

// ----- GPU roles -----
//
// Four roles, each a DXGI adapter index (as listed by GpuAdapters and accepted by FFmpeg -hwaccel_device), -1 for Auto. Shown in
// Settings > Export & Hardware > GPU & NPU Roles; Test & Auto-Tune Hardware (HardwareBenchmarkService) sets all four (user-editable).
// The fourth, Background, is BackgroundGpu (AppSettings.cs).

public sealed partial class AppSettings
{
    /// <summary>
    /// Viewport &amp; HLSL Shader Compositor GPU: renders the player and export frames. -1 Auto = Windows' high-performance GPU.
    /// </summary>
    public int CompositorGpuAdapter { get; set; } = -1;

    /// <summary>
    /// Timeline Clip Hardware Decode GPU. -1 Auto = the compositor adapter (zero-copy); another adapter copies each frame across.
    /// </summary>
    public int DecodeGpuAdapter { get; set; } = -1;

    /// <summary>
    /// Export GPU: composites and decodes exports (GpuExport.ExportAdapter); -1 Auto or a missing adapter falls back to
    /// <see cref="CompositorGpuAdapter"/>. Frames reach the encoder via system memory, so it need not host the chosen hardware encoder.
    /// Alias of <see cref="HardwareDecodeAdapter"/> (which keeps the saved name).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ExportGpuAdapter
    {
        get => HardwareDecodeAdapter;
        set => HardwareDecodeAdapter = value;
    }

    // ----- Preview engine -----
    // The player is the GPU compositor with Instant Seek and the WASAPI mixer (GpuPreviewEngine); Timeline and Media Browser strips
    // use a compositor too (GpuFilmstrips, Background GPU). Settings > Export &amp; Hardware > GPU Preview Engine (SettingsWindow.GpuEngine.cs).

    /// <summary>
    /// Instant Seek frame cache cap in MB (Settings "Maximum Frame Cache (GB)"). Adaptive: starts near 2 GB (less on small unified
    /// memory; 512 MB on WARP or small GPUs), grows 1 GB at a time up to this, and resets when a tab is backgrounded or closed. 0 Auto =
    /// hardware ceiling (Compositor.MemoryTier); N = min(N, ceiling); -1 disables caching. Always capped by the OS process budget and
    /// commit. Read at compositor start and applied live on Settings save (<see cref="CompositorClient.FrameCacheBudget"/>).
    /// </summary>
    public int GpuFrameCacheMb { get; set; }

    /// <summary>
    /// Compositor decode threads (0 Auto = 2, since most GPUs decode two streams at full speed; max 4). Applied at compositor start.
    /// </summary>
    public int GpuDecodeThreads { get; set; }

    /// <summary>
    /// Dynamic Preview Scaling (Settings > Timeline &amp; Player > Player): during play/scrub the picture may render smaller and upscale.
    /// On battery or unified memory with multiple tabs it starts at "1:1 DIP" (player size at monitor scale, short side &lt;= 720 px),
    /// else full; late frames step it down (GpuPreviewEngine.Scaling.cs). Always full when paused. Read every frame.
    /// </summary>
    public bool DynamicPreviewScaling { get; set; } = true;

    /// <summary>
    /// Smooth Slow-Mo frame interpolation engine (Speed &amp; Time card): "auto" (vendor optical-flow hardware when usable, else shader),
    /// "shader" (built-in HLSL optical flow) or "vendor". Per machine, not per clip.
    /// </summary>
    public string FrameInterpolationEngine { get; set; } = "auto";
}
