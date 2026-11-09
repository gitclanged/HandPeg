using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Export Quality Preview (Pass 5.4B, the encode loopback: MainWindow.LoopbackPreview.cs, ExportLoopbackPreview) -----
public partial class MainViewModel
{
    /// <summary>
    /// The player shows the frame as the export's encoder would leave it. Never saved: off at every start (Law 3). Never on for an Audio
    /// Only file (5.4C: there is no picture to encode): turned off when the container becomes one, refused while it is one.
    /// </summary>
    [ObservableProperty]
    private bool _isExportQualityPreview;

    partial void OnIsExportQualityPreviewChanged(bool value)
    {
        if (value && IsAudioOnlyExport)
        {
            IsExportQualityPreview = false;
            StatusText = "Live Encoded Preview shows a picture as the encoder leaves it: an Audio Only file has none.";
        }
    }

    /// <summary>0 the encoded picture, 1 a split wipe (source left, encoded right), 2 the difference x10.</summary>
    [ObservableProperty]
    private int _exportQualityPreviewMode = 1;

    /// <summary>Where the wipe stands, 0..1 of the picture's width.</summary>
    [ObservableProperty]
    private double _exportQualityPreviewWipe = 0.5;

    /// <summary>
    /// The preview's Magnifier (5.4D, Q10 b): a loupe that follows the pointer over the compare, with no edge slider held. Saved as a
    /// viewer preference (AppSettings.ExportQualityPreviewLoupe); it shows only while the preview itself is on.
    /// </summary>
    [ObservableProperty]
    private bool _exportQualityPreviewLoupe = AppSettings.Current.ExportQualityPreviewLoupe;

    partial void OnExportQualityPreviewLoupeChanged(bool value) => SaveView(s => s.ExportQualityPreviewLoupe = value);

    /// <summary>What the loopback is doing ("H.264 by ... , 6 ms"), or why it cannot.</summary>
    [ObservableProperty]
    private string _exportQualityPreviewStatus = "";

    /// <summary>
    /// The export's settings as the loopback takes them (the one place that reads the Export tab's members for it: the video encoder's
    /// name, the rate control, the bitrate in force, the CRF, the preset and the export's frame rate); null and why for a codec it
    /// cannot preview.
    /// </summary>
    public ExportLoopbackSettings? CurrentLoopbackSettings(out string? whyNot)
    {
        if (IsAudioOnlyExport)
        {
            whyNot = "an Audio Only file has no picture";
            return null;
        }

        var constantBitrate = RateControl?.Contains("CBR", StringComparison.Ordinal) == true;
        return ExportLoopbackSettings.From(VideoEncoder?.Name ?? "", IsConstantQuality, constantBitrate, EffectiveBitrateKbps, Crf,
            EncoderPreset ?? "", GetOutputFrameRate(), out whyNot);
    }
}
