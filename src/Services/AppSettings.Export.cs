namespace HandPegApp.Services;

// ----- What renders an export's picture (Pass 5.0-AudioAnd3D, GPU export) -----
//
// Settings > Export & Hardware > Hardware, and the Export tab's own Render choice (MainWindow.Export.cs). Read when an
// export starts (MainViewModel.RunExportAsync); saved settings need no compatibility in v5.0 (the user's call): this is
// new, and an old file simply has none of it.

/// <summary>One of the export renderers as a list shows it: its saved value and its name.</summary>
public sealed record ExportRendererChoice(string Value, string Display);

public sealed partial class AppSettings
{
    public const string ExportRendererGpu = "Gpu", ExportRendererFfmpeg = "Ffmpeg";

    /// <summary>
    /// What draws an export's frames: <see cref="ExportRendererGpu"/> (the GPU compositor renders every frame and pipes it
    /// to FFmpeg's encoder, the default) or <see cref="ExportRendererFfmpeg"/> (FFmpeg's own filter graph, the bit-exact
    /// reference). The sound, the captions and the subtitles are FFmpeg's either way. A Copy export, a GIF or WebP, a
    /// hand-edited command, and anything the compositor cannot draw yet always go through FFmpeg; so does an export whose
    /// GPU render fails (the compositor does not start, or gives up), started again from the beginning.
    /// </summary>
    public string ExportRenderer { get; set; } = ExportRendererGpu;

    /// <summary>Whether exports are to be drawn by the GPU compositor where they can be (the setting, before any fallback).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WantsGpuExport => !string.Equals(ExportRenderer, ExportRendererFfmpeg, StringComparison.OrdinalIgnoreCase);

    /// <summary>The renderers, in the order the lists show them.</summary>
    public static IReadOnlyList<ExportRendererChoice> ExportRenderers { get; } =
    [
        new(ExportRendererGpu, "GPU Compositor"),
        new(ExportRendererFfmpeg, "FFmpeg Filter Graph"),
    ];
}
