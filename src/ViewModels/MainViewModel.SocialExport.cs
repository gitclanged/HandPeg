using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Export Presets -----
// One list (Presets: ExportPresetCatalog built-ins, then custom), one selector grouped "Platform & Social", "Studio & Archive",
// "My Presets" (PresetsView), and one apply path touching export settings only (ApplyExportPreset). Game Presets own layout, looks
// and captions; project settings and Smart Rule presets still apply whole (ApplyPreset). Also: VBR auto bitrate
// (EffectiveBitrateKbps) and the Normalize loudness target.
public partial class MainViewModel
{
    private ListCollectionView? _presetsView;

    /// <summary>
    /// Export Presets selector view: <see cref="Presets"/> grouped by <see cref="EncodingPreset.Group"/> (built-in groups, then "My
    /// Presets"). Created lazily (the Export tab is built at idle, Law 1).
    /// </summary>
    public ICollectionView PresetsView
    {
        get
        {
            if (_presetsView is null)
            {
                _presetsView = new ListCollectionView(Presets);
                _presetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EncodingPreset.Group)));
            }

            return _presetsView;
        }
    }

    /// <summary>
    /// Applies an Export Preset to export settings only: size ("p" = Sequence short side; custom presets as saved; chat short-side cap,
    /// never above the Sequence), frame rate, encoder (preferred hardware codec first), speed preset, quality/bitrate, audio, container
    /// and Web Optimized. Clips, looks, crops and captions are untouched.
    /// </summary>
    public void ApplyExportPreset(EncodingPreset preset)
    {
        using var whole = DeferCommand();
        ApplyPresetPicture(preset);
        PixelAspectRatio = Pick(PixelAspectRatios, preset.PixelAspectRatio, MatchProject);
        CustomPixelAspectRatio = preset.CustomPixelAspectRatio;
        var found = ApplyEncodingPart(preset);

        // Target size comes only from the preset; none leaves a typed or automatic bitrate.
        if (string.IsNullOrWhiteSpace(preset.TargetFileSize) && preset.IsBuiltIn)
            TargetFileSize = "";
        var sizeMb = double.TryParse(preset.TargetFileSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? mb : 0;
        StatusText = $"Export Preset \"{preset.Name}\": {VideoEncoder.DisplayName}, {FrameWidth} x {FrameHeight}."
                     + (found ? "" : $" {preset.VideoEncoder} is not available here.") + TooLongForSize(sizeMb);
        Log($"Applied Export Preset \"{preset.Name}\"");
    }

    /// <summary>
    /// Resolves a preset's encoder: first available hardware encoder for <see cref="EncodingPreset.PreferredCodec"/> ("hevc,h264"),
    /// else its named encoder, else the current one (Found = false).
    /// </summary>
    private (EncoderOption Encoder, bool Found) ResolveEncoder(EncodingPreset preset)
    {
        foreach (var codec in (preset.PreferredCodec ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (VideoEncoders.FirstOrDefault(e => e.IsHardware && e.Name.StartsWith(codec + "_", StringComparison.Ordinal)) is { } hardware)
                return (hardware, true);
        }

        return VideoEncoders.FirstOrDefault(e => e.Name == preset.VideoEncoder) is { } named ? (named, true) : (VideoEncoder, false);
    }

    /// <summary>
    /// Preset output size: both sides = exact (orientation via Use Vertical Resolution relative to the Sequence); one side = "p" size on
    /// the Sequence short side (rotated only by the preset's flag); none = Sequence size; then the chat short-side cap. Custom presets
    /// apply as saved (Keep Aspect Ratio, Use Vertical Resolution, stored sides; a blank side derived from the aspect).
    /// </summary>
    private void ApplyPresetPicture(EncodingPreset preset)
    {
        if (!preset.IsBuiltIn)
        {
            _syncingDimensions = true;
            try
            {
                KeepAspectRatio = preset.KeepAspectRatio;
                TurnForPreset(preset.UseVerticalResolution ?? false);
                SetOutputSize(preset.OutputWidth, preset.OutputHeight);
            }
            finally
            {
                _syncingDimensions = false;
            }

            if (string.IsNullOrWhiteSpace(preset.OutputWidth) || string.IsNullOrWhiteSpace(preset.OutputHeight))
                RelinkDimensions();
            CapShortSide(preset.ShortSideCap);
            OnPropertyChanged(nameof(FrameWidth));
            OnPropertyChanged(nameof(FrameHeight));
            return;
        }

        KeepAspectRatio = true;
        var (width, height) = (preset.OutputWidth.Trim(), preset.OutputHeight.Trim());
        if (width.Length > 0 && height.Length > 0 && int.TryParse(width, out var w) && int.TryParse(height, out var h))
        {
            var natural = GetSequenceSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
            var turned = h > w != natural.Height > natural.Width;
            _syncingDimensions = true;
            try
            {
                TurnForPreset(turned);
            }
            finally
            {
                _syncingDimensions = false;
            }

            SetOutputSize(width, height);
        }
        else
        {
            // Apply this preset's orientation and clear the size boxes first so the frame read below never inherits an earlier pick.
            _syncingDimensions = true;
            try
            {
                // Orientation-neutral presets (Match Project, "p" sizes) undo an earlier preset's rotation but never the project's own
                // (defaulting to false used to turn vertical projects sideways).
                if (preset.UseVerticalResolution is { } shape)
                    TurnForPreset(shape);
                else if (_turnBeforePreset is { } own)
                    TurnForPreset(own);
            }
            finally
            {
                _syncingDimensions = false;
            }

            SetOutputSize("", "");
            if (width.Length > 0 || height.Length > 0)
            {
                // "1080p" is the short side in either orientation.
                var side = width.Length > 0 ? width : height;
                if (FrameHeight > FrameWidth)
                    SetOutputSize(side, "");
                else
                    SetOutputSize("", side);
                RelinkDimensions();
            }
        }

        CapShortSide(preset.ShortSideCap);
        OnPropertyChanged(nameof(FrameWidth));
        OnPropertyChanged(nameof(FrameHeight));
    }

    // Sequence orientation before a preset rotated it, restored by orientation-neutral presets. Cleared on manual rotation and on any
    // Sequence swap (tab switch, open, New Project).
    private bool? _turnBeforePreset;

    /// <summary>Applies a preset's orientation, saving the Sequence's own on the first preset change.</summary>
    private void TurnForPreset(bool vertical)
    {
        if (vertical != UseVerticalResolution)
            _turnBeforePreset ??= UseVerticalResolution;
        UseVerticalResolution = vertical;
        if (_turnBeforePreset == vertical)
            _turnBeforePreset = null;
    }

    /// <summary>Caps the short side at <paramref name="cap"/> px (never above the Sequence's); 0 = no cap.</summary>
    private void CapShortSide(int cap)
    {
        if (cap <= 0)
            return;
        if (GetSequenceSize() is { } natural)
            cap = Math.Min(cap, Even(Math.Min(natural.Width, natural.Height)));
        if (Math.Min(FrameWidth, FrameHeight) <= cap)
            return;

        var side = cap.ToString(CultureInfo.InvariantCulture);
        if (FrameHeight > FrameWidth)
            SetOutputSize(side, "");
        else
            SetOutputSize("", side);
        RelinkDimensions();
    }

    /// <summary>
    /// Warning when the preset's target size is unreachable for this duration even at the minimum bitrate; empty when achievable or
    /// unset.
    /// </summary>
    private string TooLongForSize(double sizeMb)
    {
        var seconds = GetOutputDuration();
        if (sizeMb <= 0 || seconds <= 0 || TargetSizeHint.Length == 0 || !int.TryParse(TargetBitrate, out var videoKbps))
            return "";

        var tracks = GetOutputAudioTracks();
        var megabytes = (videoKbps + GetOutputAudioKbps(tracks, MergeAudioTracks && tracks.Count > 1)) * seconds / 8192;
        return string.Create(CultureInfo.InvariantCulture,
            $" This timeline is too long for under {sizeMb:0.#} MB: the file will be about {megabytes:0} MB. Cut it shorter, or pick a larger size.");
    }

    // ----- Automatic bitrate -----

    /// <summary>
    /// Effective video kbps: typed (or derived from a typed target size), else <see cref="AutoBitrateKbps"/> from output size and fps.
    /// Used for VBR/CBR encodes and the Export Quality Preview.
    /// </summary>
    public int EffectiveBitrateKbps =>
        int.TryParse(TargetBitrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed) && typed > 0 ? typed : AutoBitrate();

    /// <summary>Hint under an empty Target Bitrate box in bitrate modes: the auto bitrate and its inputs.</summary>
    public string AutoBitrateText =>
        IsBitrateMode && TargetBitrate.Trim().Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Automatic: {AutoBitrate():N0} kbps for {FrameWidth} x {FrameHeight} at {FormatNumber(AutoBitrateRate())} fps. Type a bitrate or a size to set your own.")
            : "";

    private int AutoBitrate() => AutoBitrateKbps(FrameWidth, FrameHeight, AutoBitrateRate(), IsHdrPassthrough);

    private double AutoBitrateRate() => GetOutputFrameRate() is > 0 and var fps ? fps : Document.FrameRate.IsValid ? Document.FrameRate.Fps : 30;

    /// <summary>
    /// VBR target from platform upload guidance: 5/8/16/35 Mbps at 720p/1080p/1440p/2160p30, interpolated by pixel count; x1.5 above
    /// 30 fps (12 Mbps 1080p60, 53 Mbps 2160p60), x2 above 60 fps, x1.25 for HDR. Rounded to 500 kbps.
    /// </summary>
    internal static int AutoBitrateKbps(int width, int height, double fps, bool hdr)
    {
        (double Pixels, double Kbps)[] anchors = [(0, 0), (1280 * 720, 5000), (1920 * 1080, 8000), (2560 * 1440, 16000), (3840 * 2160, 35000)];
        var pixels = (double)Math.Max(width, 2) * Math.Max(height, 2);
        double kbps;
        if (pixels >= anchors[^1].Pixels)
        {
            kbps = anchors[^1].Kbps * pixels / anchors[^1].Pixels;
        }
        else
        {
            var i = 1;
            while (pixels > anchors[i].Pixels)
                i++;
            var (a, b) = (anchors[i - 1], anchors[i]);
            kbps = a.Kbps + (b.Kbps - a.Kbps) * (pixels - a.Pixels) / (b.Pixels - a.Pixels);
        }

        kbps *= fps > 61 ? 2 : fps > 31 ? 1.5 : 1;
        if (hdr)
            kbps *= 1.25;
        return (int)Math.Clamp(Math.Round(kbps / 500) * 500, 500, 400_000);
    }

    // ----- Normalize loudness target -----

    /// <summary>Normalize target in LUFS: -16 (default, EBU R128 web) or -14 (streaming platforms).</summary>
    [ObservableProperty] private int _loudnessTarget = (int)Services.Audio.ExportSoundPlan.TargetLufs;

    /// <summary>Offered targets: LUFS value and label.</summary>
    public IReadOnlyList<LabeledChoice<int>> LoudnessTargets { get; } =
        [new(-16, "-16 LUFS (default)"), new(-14, "-14 LUFS (streaming platforms)")];

    /// <summary>Normalizes a stored target (0 = default) to one of the offered values.</summary>
    private static int NormalizeLoudness(int value) => value == -14 ? -14 : -16;
}
