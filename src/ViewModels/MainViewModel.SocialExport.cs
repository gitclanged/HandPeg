using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// One card of Easy Social Export: a platform and how it is exported (Pass 4.2B-2). <see cref="SizesMb"/> are the
/// file-size ceilings it is offered at (each a button of its own); none for a card of one click.
/// </summary>
public sealed record SocialExportTarget(string Id, string Title, string Detail, string Glyph, IReadOnlyList<int> SizesMb);

// ----- Easy Social Export (Pass 4.2B-2) -----
// The Export tab's 1-click cards. A card sets the export's own settings (the same ones the Video and Audio sections
// show, saved with the project and in presets) and nothing else: no layer, look or caption is touched. Advanced Export
// shows those settings, as the card left them, to change by hand.
public partial class MainViewModel
{
    public const string SocialShortForm = "short-form", SocialYouTube = "youtube-master", SocialChat = "chat-share",
        SocialFeed = "feed-hd", SocialArchive = "archive";

    /// <summary>The cards, in the order the Export tab shows them.</summary>
    public IReadOnlyList<SocialExportTarget> SocialExportTargets { get; } =
    [
        new(SocialShortForm, "Short-Form Vertical", "9:16, 1080 x 1920, 60 fps. H.264, AAC 192k.", "", []),
        new(SocialYouTube, "YouTube Master", "The source's size and rate (4K60, 1440p60...). H.265 when the card has it, quality 18, AAC 320k.", "", []),
        new(SocialChat, "Chat & Discord Share", "Under the size a chat allows: the bitrate is worked out from the length. 720p under 10 MB, 1080p above.", "", [10, 25, 50]),
        new(SocialFeed, "Social Feed HD", "1080p, 60 fps, a fast encoder step. H.264, AAC 192k.", "", []),
        new(SocialArchive, "Lossless / Mezzanine Archive", "ProRes in .mov with PCM sound, at the source's size and rate: to edit or keep.", "", []),
    ];

    /// <summary>The card (and size) chosen last in this session, for the Export tab to mark; null when none was.</summary>
    public (string Id, int SizeMb)? SocialExportChoice { get; private set; }

    /// <summary>The ceiling a chat card aims under: a little below the limit, for the container's own bytes.</summary>
    private static string ChatTargetMegabytes(int limitMb) => (limitMb * 0.94).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Sets the export as a card says. Every value it sets is an ordinary export setting (Video and Audio sections,
    /// Advanced Export); the encoder is the best one this PC has for the card (a hardware H.264 / H.265 encoder when the
    /// probe found one, else the software one).
    /// </summary>
    public void ApplySocialExport(SocialExportTarget target, int sizeMb = 0)
    {
        if (target.SizesMb.Count > 0 && !target.SizesMb.Contains(sizeMb))
            sizeMb = target.SizesMb[0];

        using var whole = DeferCommand();
        var source = GetCroppedSourceSize();
        switch (target.Id)
        {
            case SocialShortForm:
                SetSocialPicture(vertical: true, "1080", "1920", "60");
                SetSocialEncoding(Hardware("h264") ?? Software("libx264"), "mp4", crf: 20, targetMb: null, "192k", ["fast", "p5", "balanced"]);
                break;

            case SocialYouTube:
                SetSocialPicture(vertical: false, "", "", SameAsSource);
                SetSocialEncoding(Hardware("hevc") ?? Hardware("h264") ?? Software("libx264"), "mp4", crf: 18, targetMb: null, "320k", ["slow", "p6", "quality"]);
                break;

            case SocialChat:
            {
                // The short side capped (720 under 10 MB, 1080 above), never above the source's own.
                var cap = sizeMb <= 10 ? 720 : 1080;
                if (source is { } size)
                    cap = Math.Min(cap, Even(Math.Min(size.Width, size.Height)));
                var side = cap.ToString(CultureInfo.InvariantCulture);
                if (UseVerticalResolution)
                    SetSocialPicture(vertical: true, side, "", sizeMb >= 50 ? SameAsSource : "30");
                else
                    SetSocialPicture(vertical: false, "", side, sizeMb >= 50 ? SameAsSource : "30");
                SetSocialEncoding(Hardware("h264") ?? Software("libx264"), "mp4", crf: 23, ChatTargetMegabytes(sizeMb), sizeMb <= 10 ? "96k" : "128k", ["medium", "p5", "balanced"]);
                break;
            }

            case SocialFeed:
                SetSocialPicture(vertical: false, "", "1080", "60");
                SetSocialEncoding(Hardware("h264") ?? Software("libx264"), "mp4", crf: 21, targetMb: null, "192k", ["faster", "p3", "speed"]);
                break;

            case SocialArchive:
                SetSocialPicture(vertical: UseVerticalResolution, OutputWidth, OutputHeight, SameAsSource);
                SetSocialEncoding(Software("prores_ks"), "mov", crf: Crf, targetMb: null, AudioBitrate, [], audioCodec: "pcm_s16le");
                break;

            default:
                return;
        }

        SocialExportChoice = (target.Id, sizeMb);
        OnPropertyChanged(nameof(SocialExportChoice));
        var title = target.SizesMb.Count > 0 ? $"{target.Title} (under {sizeMb} MB)" : target.Title;
        StatusText = $"Easy Social Export: {title}, with {VideoEncoder.DisplayName}. Advanced Export shows every setting it chose.{TooLongForSize(sizeMb)}";
        Log($"Easy Social Export: {title}");

        EncoderOption? Hardware(string codec) => VideoEncoders.FirstOrDefault(e => e.IsHardware && e.Name.StartsWith(codec + "_", StringComparison.Ordinal));
        EncoderOption Software(string name) => VideoEncoders.FirstOrDefault(e => e.Name == name) ?? DefaultVideoEncoder;
    }

    /// <summary>
    /// A size this long a timeline cannot meet (the video held at its lowest bitrate is still too much), said with the card:
    /// Easy Social Export shows no Target File Size box to carry that hint (TargetSizeHint). Empty when the size is met.
    /// </summary>
    private string TooLongForSize(int sizeMb)
    {
        var seconds = GetOutputDuration();
        if (sizeMb <= 0 || seconds <= 0 || TargetSizeHint.Length == 0 || !int.TryParse(TargetBitrate, out var videoKbps))
            return "";

        var tracks = GetOutputAudioTracks();
        var megabytes = (videoKbps + GetOutputAudioKbps(tracks, MergeAudioTracks && tracks.Count > 1)) * seconds / 8192;
        return string.Create(CultureInfo.InvariantCulture,
            $" This timeline is too long for under {sizeMb} MB: the file will be about {megabytes:0} MB. Cut it shorter, or pick a larger size.");
    }

    /// <summary>The frame: tall or wide, its size (a blank side follows the source's shape), and the frame rate.</summary>
    private void SetSocialPicture(bool vertical, string width, string height, string framerate)
    {
        if (UseVerticalResolution != vertical)
            UseVerticalResolution = vertical;
        KeepAspectRatio = true;
        SetOutputSize(width, height);
        if (string.IsNullOrWhiteSpace(width) != string.IsNullOrWhiteSpace(height))
            RelinkDimensions();
        Framerate = Framerates.Contains(framerate) ? framerate : SameAsSource;
    }

    /// <summary>
    /// The encoding: encoder and container, then the sound (its bitrate is part of a size's sum), then quality or a
    /// size. A size is aimed at with an average bitrate worked out from the output's length (Target File Size).
    /// </summary>
    private void SetSocialEncoding(EncoderOption encoder, string container, int crf, string? targetMb, string audioBitrate,
        string[] speedSteps, string audioCodec = "aac")
    {
        VideoEncoder = encoder;
        if (Containers.Contains(container))
            Container = container;
        if (speedSteps.FirstOrDefault(EncoderPresets.Contains) is { } step)
            EncoderPreset = step;
        if (encoder.Family != EncoderFamily.Intermediate)
            TenBit = false;

        // Tracks that follow the default take these; a track given a codec of its own on the Audio tab keeps it.
        AudioEncoder = AudioEncoders.Contains(audioCodec) ? audioCodec : "aac";
        AudioBitrate = AudioBitrates.Contains(audioBitrate) ? audioBitrate : "160k";

        TargetFileSize = "";
        if (targetMb is null)
        {
            RateControl = ConstantQuality;
            Crf = Math.Clamp(crf, 0, 51);
        }
        else
        {
            RateControl = AverageBitrate;
            TargetFileSize = targetMb;
        }
    }
}
