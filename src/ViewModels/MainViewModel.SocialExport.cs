using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Export Presets (Pass 5.4B: the Encoding Presets and the Easy Social Export cards of 4.2B-2, one system) -----
// One list (Presets: ExportPresetCatalog's built-ins, then the user's own), one selector grouped "Platform & Social", "Studio & Archive",
// "My Presets" (PresetsView), and one way of applying a pick: its export settings only (ApplyExportPreset). Game Presets own the
// layout, the looks and the captions; a project's settings and a Smart Rule's preset are still applied whole (ApplyPreset).
// Also here: the automatic bitrate VBR starts with (EffectiveBitrateKbps) and the loudness Normalize aims at.
public partial class MainViewModel
{
    private ListCollectionView? _presetsView;

    /// <summary>
    /// The Export Presets selector's list: <see cref="Presets"/> grouped by <see cref="EncodingPreset.Group"/> (the built-ins' two groups
    /// first, as they come first, then "My Presets"). Made when first asked for (the Export tab is made at idle, Law 1).
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
    /// Sets the export as an Export Preset says, and nothing else (D-Q2): the picture's size (a "p" size on the short side of the
    /// Sequence's frame, a user's own preset as it was saved, a chat's short side capped, never above the Sequence's), the frame rate,
    /// the encoder (a codec it prefers on this PC's hardware first), its step, the quality or bitrate, the sound, the container and Web Optimized. No clip, look, crop or caption is touched.
    /// </summary>
    public void ApplyExportPreset(EncodingPreset preset)
    {
        using var whole = DeferCommand();
        ApplyPresetPicture(preset);
        PixelAspectRatio = Pick(PixelAspectRatios, preset.PixelAspectRatio, MatchProject);
        CustomPixelAspectRatio = preset.CustomPixelAspectRatio;
        var found = ApplyEncodingPart(preset);

        // A target size is the preset's alone: one that names none leaves the bitrate to be typed, or the automatic one.
        if (string.IsNullOrWhiteSpace(preset.TargetFileSize) && preset.IsBuiltIn)
            TargetFileSize = "";
        var sizeMb = double.TryParse(preset.TargetFileSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? mb : 0;
        StatusText = $"Export Preset \"{preset.Name}\": {VideoEncoder.DisplayName}, {FrameWidth} x {FrameHeight}."
                     + (found ? "" : $" {preset.VideoEncoder} is not available here.") + TooLongForSize(sizeMb);
        Log($"Applied Export Preset \"{preset.Name}\"");
    }

    /// <summary>
    /// The encoder a preset asks for on this PC: a hardware encoder of the first codec it prefers that this PC has
    /// (<see cref="EncodingPreset.PreferredCodec"/>, "hevc,h264"), else the encoder it names, else the one selected (Found false then).
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
    /// An Export Preset's picture: both sides named, exactly that size (turned so its shape is the frame's: a 9:16 preset on a 16:9
    /// Sequence is Use Vertical Resolution, on a 9:16 one it is not); one side named, a "p" size on the short side of the Sequence's
    /// frame (turned only by the preset's own Use Vertical Resolution); none, the Sequence's own size. Then a chat's short-side cap. A user's own preset is applied as it was saved (5.4A's rule):
    /// its Keep Aspect Ratio and Use Vertical Resolution, its sides as stored, a blank one completed from the shape.
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
            // The preset's own shape first, and the boxes cleared, so the frame read below is the Sequence's as this preset stands it,
            // never one an earlier pick left turned or sized.
            _syncingDimensions = true;
            try
            {
                // 5.4D: a preset that names no shape (Match Project — Master Quality, the "p" sizes) keeps the Sequence's: the turn an
                // earlier preset made is undone, the project's own (a vertical project) never (it used to fall to false and lay a
                // vertical project on its side).
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
                // "1080p" is the short side, whichever way the frame stands.
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

    // The Sequence's turn as it was before an Export Preset turned it (5.4D): what a preset that names no shape goes back to. Forgotten
    // when the user turns it by hand, and whenever a Sequence is put in place (another tab, an open, a New Project).
    private bool? _turnBeforePreset;

    /// <summary>Turns the frame as a preset asks, remembering the Sequence's own turn the first time a preset changes it.</summary>
    private void TurnForPreset(bool vertical)
    {
        if (vertical != UseVerticalResolution)
            _turnBeforePreset ??= UseVerticalResolution;
        UseVerticalResolution = vertical;
        if (_turnBeforePreset == vertical)
            _turnBeforePreset = null;
    }

    /// <summary>The short side of the frame held at <paramref name="cap"/> pixels (never above the Sequence's own short side); 0 for no cap.</summary>
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
    /// A size this long a timeline cannot meet (the video held at its lowest bitrate is still too much), said with the preset that asked
    /// for it. Empty when the size is met, or none was asked for.
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

    // ----- The automatic bitrate (5.4B, D-Q1) -----

    /// <summary>
    /// The video bitrate in force, in kbps: the one typed (or worked out from a typed target size), else the automatic one from the output's
    /// size and frame rate (<see cref="AutoBitrateKbps"/>). What the encoder is given in VBR and CBR, and what the Export Quality Preview reads.
    /// </summary>
    public int EffectiveBitrateKbps =>
        int.TryParse(TargetBitrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed) && typed > 0 ? typed : AutoBitrate();

    /// <summary>Under the Target Bitrate box while it is empty and a bitrate mode is chosen: the automatic bitrate and what it is worked out from.</summary>
    public string AutoBitrateText =>
        IsBitrateMode && TargetBitrate.Trim().Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Automatic: {AutoBitrate():N0} kbps for {FrameWidth} x {FrameHeight} at {FormatNumber(AutoBitrateRate())} fps. Type a bitrate or a size to set your own.")
            : "";

    private int AutoBitrate() => AutoBitrateKbps(FrameWidth, FrameHeight, AutoBitrateRate(), IsHdrPassthrough);

    private double AutoBitrateRate() => GetOutputFrameRate() is > 0 and var fps ? fps : Document.FrameRate.IsValid ? Document.FrameRate.Fps : 30;

    /// <summary>
    /// A VBR target that keeps a picture of this size and rate clean (the platforms' upload advice): 5 Mbps at 720p30, 8 at 1080p30, 16 at
    /// 1440p30, 35 at 2160p30, in between by the number of pixels; half as much again above 30 fps (12 Mbps at 1080p60, 53 at 2160p60),
    /// twice as much above 60; a quarter more for HDR. Rounded to 500 kbps.
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

    // ----- Normalize's target (5.4B, D9) -----

    /// <summary>The loudness Normalize aims at, in LUFS: -16 (the default, EBU R128 for the web) or -14 (the streaming platforms').</summary>
    [ObservableProperty] private int _loudnessTarget = (int)Services.Audio.ExportSoundPlan.TargetLufs;

    /// <summary>The two targets offered: their LUFS value and their words.</summary>
    public IReadOnlyList<LabeledChoice<int>> LoudnessTargets { get; } =
        [new(-16, "-16 LUFS (default)"), new(-14, "-14 LUFS (streaming platforms)")];

    /// <summary>A target as a preset or project stores it (0 for the default) made one of the two offered.</summary>
    private static int NormalizeLoudness(int value) => value == -14 ? -14 : -16;
}
