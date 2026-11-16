using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Export Quality Preview (encode loopback: MainWindow.LoopbackPreview.cs, ExportLoopbackPreview) -----
public partial class MainViewModel
{
    /// <summary>
    /// Player shows frames as the export encoder would output them. Not persisted (off at start, Law 3). Unavailable for Audio Only
    /// containers: turned off on switch and refused while selected.
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

    /// <summary>View mode: 0 encoded, 1 split wipe (source left, encoded right), 2 difference x10.</summary>
    [ObservableProperty]
    private int _exportQualityPreviewMode = 1;

    /// <summary>Wipe position, 0..1 of picture width.</summary>
    [ObservableProperty]
    private double _exportQualityPreviewWipe = 0.5;

    /// <summary>
    /// Magnifier loupe following the pointer over the compare (when no edge slider is held). Persisted viewer preference
    /// (AppSettings.ExportQualityPreviewLoupe); visible only while the preview is on.
    /// </summary>
    [ObservableProperty]
    private bool _exportQualityPreviewLoupe = AppSettings.Current.ExportQualityPreviewLoupe;

    partial void OnExportQualityPreviewLoupeChanged(bool value) => SaveView(s => s.ExportQualityPreviewLoupe = value);

    /// <summary>Loopback status ("H.264 by ..., 6 ms") or the reason it is unavailable.</summary>
    [ObservableProperty]
    private string _exportQualityPreviewStatus = "";

    /// <summary>
    /// Loopback settings from the Export tab (sole reader): encoder, rate control, effective bitrate, CRF, preset, frame rate, frame
    /// size, 10-bit, and for software encoders the export's video arguments. Null with a reason for unpreviewable codecs.
    /// </summary>
    public ExportLoopbackSettings? CurrentLoopbackSettings(out string? whyNot)
    {
        if (IsAudioOnlyExport)
        {
            whyNot = "an Audio Only file has no picture";
            return null;
        }

        var constantBitrate = RateControl?.Contains("CBR", StringComparison.Ordinal) == true;
        if (ExportLoopbackSettings.From(VideoEncoder?.Name ?? "", IsConstantQuality, constantBitrate, EffectiveBitrateKbps, Crf,
                EncoderPreset ?? "", GetOutputFrameRate(), out whyNot) is not { } settings)
            return null;

        return settings with
        {
            EncodedWidth = FrameWidth,
            EncodedHeight = FrameHeight,
            TenBit = TenBit && HasTenBit && (VideoEncoder is null || !VideoEncoder.IsHardware || CanTenBit),
            SoftwareArguments = settings.Family == ExportLoopbackSettings.FamilySoftware ? BuildVideoCodecArguments() : "",
        };
    }

    /// <summary>
    /// Export tab properties that push new settings to the Live Encoded Preview: encoder, speed preset, rate control, typed or derived
    /// bitrate, target size, CRF, 10-bit, resolution/frame size, orientation, Export Preset, frame rate, tune/profile/level, extra args.
    /// </summary>
    public static readonly IReadOnlySet<string> LoopbackSettingNames = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(VideoEncoder), nameof(EncoderPreset), nameof(RateControl), nameof(TargetBitrate), nameof(EffectiveBitrateKbps), nameof(TargetFileSize),
        nameof(Crf), nameof(TenBit), nameof(ResolutionPreset), nameof(OutputWidth), nameof(OutputHeight), nameof(UseVerticalResolution),
        nameof(SelectedPreset), nameof(FrameWidth), nameof(FrameHeight), nameof(Framerate), nameof(CustomFramerate), nameof(Tune), nameof(Profile),
        nameof(Level), nameof(ExtraVideoArguments),
    };
}
