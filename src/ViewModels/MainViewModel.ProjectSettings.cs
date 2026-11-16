using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Project Settings -----
// Explicit per-project settings (Models/Timeline/ProjectSettings.cs): natural frame and orientation, frame rate, colour space,
// pixel aspect, sample rate. The first video on an empty Sequence sets them (TakeProjectSettingsFrom) unless the New Project studio
// set them first (ApplyProjectSettings marks frame/rate explicit). Export's Match Project Settings follows them.
public partial class MainViewModel
{
    /// <summary>Current project settings: the document's, with orientation from Use Vertical Resolution.</summary>
    public ProjectSettings CurrentProjectSettings => Document.ProjectSettings with { Vertical = UseVerticalResolution };

    /// <summary>Whether the Sequence has clips (frame rate is then locked: clip edges sit on that rate's frames).</summary>
    public bool SequenceHasClips => Document.Tracks.Exists(t => t.Clips.Count > 0);

    /// <summary>
    /// Applies project settings (New Project studio, Project Settings). <paramref name="frameWidth"/> x <paramref name="frameHeight"/> is
    /// the output frame (portrait = transposed natural size with Use Vertical Resolution; Export Resolution resets to Match Project
    /// Settings); 0 leaves size unchanged. Null <paramref name="frameRate"/> leaves the rate unchanged. Given values are marked explicit
    /// (<see cref="TimelineDocument.ExplicitFrame"/>) so the first file keeps them. One undo step when <paramref name="undoable"/> and clips
    /// exist, including <see cref="ProjectSettingsFrame"/>. Returns false (status line) for a rate change with clips (no requantize).
    /// </summary>
    public bool ApplyProjectSettings(int frameWidth, int frameHeight, FrameRate? frameRate, ProjectColorSpace colorSpace,
        ProjectPixelAspect pixelAspect, int sampleRate, bool undoable = true)
    {
        var hasClips = SequenceHasClips;
        if (frameRate is { } wanted && hasClips && wanted != Document.FrameRate)
        {
            StatusText = "The frame rate cannot change once the Sequence has clips: start a New Project at the rate wanted.";
            return false;
        }

        if (IsBusy || IsRecording)
        {
            StatusText = IsRecording
                ? "Stop the voiceover recording before changing the Project Settings."
                : "Wait for the running operation to finish, or cancel it, before changing the Project Settings.";
            return false;
        }

        var framed = frameWidth >= SmallestFrameSide && frameHeight >= SmallestFrameSide;
        var vertical = framed && frameHeight > frameWidth;
        var (naturalWidth, naturalHeight) = vertical ? (frameHeight, frameWidth) : (frameWidth, frameHeight);
        var frameChanges = framed && (naturalWidth != Document.NaturalWidth || naturalHeight != Document.NaturalHeight
                                      || vertical != UseVerticalResolution || OutputWidth.Length > 0 || OutputHeight.Length > 0);
        var rateChanges = frameRate is { } rate && rate != Document.FrameRate;
        var settingsChange = colorSpace != Document.ColorSpace || pixelAspect != Document.PixelAspect
                             || (sampleRate is 44100 or 48000 or 96000 ? sampleRate : TimelineDocument.DefaultSampleRate) != Document.SampleRate;
        // Omitted frame/rate keep their explicit marks (Project Settings sends only changed values).
        var (explicitFrame, explicitRate) = (framed || Document.ExplicitFrame, frameRate.HasValue || Document.ExplicitFrameRate);
        if (!frameChanges && !rateChanges && !settingsChange && Document.ExplicitFrame == explicitFrame && Document.ExplicitFrameRate == explicitRate)
            return true;

        // Snapshot before any change (frame/rate are set outside the transaction), including the VM frame state so undo/redo restore
        // orientation and Resolution boxes too.
        var before = undoable && hasClips && _restoring == 0 ? CaptureSnapshot() with { Frame = CaptureProjectFrame() } : null;

        // Frame, rate, colour space, pixel aspect and sample rate in one transaction (non-undoable; the snapshot above is the step).
        var (frameWidthNow, frameHeightNow) = framed ? (naturalWidth, naturalHeight) : (Document.NaturalWidth, Document.NaturalHeight);
        Edit("change the Project Settings", edit =>
        {
            edit.SetFrameRate(frameRate ?? Document.FrameRate, explicitRate);
            edit.SetFrame(frameWidthNow, frameHeightNow, explicitFrame);
            if (settingsChange)
                edit.SetProjectSettings(colorSpace, pixelAspect, sampleRate);
        }, undoable: false);
        if (framed)
        {

            // Toggle orientation without swapping boxes, then blank them (Match Project Settings).
            _syncingDimensions = true;
            try
            {
                UseVerticalResolution = vertical;
            }
            finally
            {
                _syncingDimensions = false;
            }

            SetOutputSize("", "");
        }

        NoteDocumentShape();
        GenerateCommand();
        RaiseTimelineChanged();

        if (before is not null && (frameChanges || rateChanges || settingsChange))
            Checkpoint("change the Project Settings", before);

        StatusText = $"Project Settings: {Describe(CurrentProjectSettings)}.";
        return true;
    }

    /// <summary>
    /// Clears explicit frame/rate marks on an empty Sequence (frame boxes cleared, or Auto from First Clip re-selected) so the first file
    /// sets them. No-op with clips; no undo step (as <see cref="ApplyProjectSettings"/> on an empty Sequence).
    /// </summary>
    public void LeaveToFirstClip(bool frame, bool rate)
    {
        if (SequenceHasClips || (!frame || !Document.ExplicitFrame) && (!rate || !Document.ExplicitFrameRate))
            return;

        Edit("leave the frame to the first clip", edit =>
        {
            edit.SetFrame(Document.NaturalWidth, Document.NaturalHeight, Document.ExplicitFrame && !frame);
            edit.SetFrameRate(Document.FrameRate, Document.ExplicitFrameRate && !rate);
        }, undoable: false);
    }

    // ----- VM frame state in Project Settings undo steps -----

    /// <summary>Current orientation and Export Resolution boxes, captured in Project Settings steps.</summary>
    private ProjectSettingsFrame CaptureProjectFrame() => new(UseVerticalResolution, OutputWidth, OutputHeight);

    /// <summary>Restores a Project Settings step's frame state (RestoreTimeline): orientation without box swap, then exact box values.</summary>
    private void RestoreProjectFrame(ProjectSettingsFrame frame)
    {
        _syncingDimensions = true;
        try
        {
            UseVerticalResolution = frame.Vertical;
        }
        finally
        {
            _syncingDimensions = false;
        }

        SetOutputSize(frame.OutputWidth ?? "", frame.OutputHeight ?? "");
    }

    /// <summary>
    /// HDR export note under Match Project Settings: kept HDR (10-bit BT.2020 with its transfer) or tone-mapped to BT.709 with the reason
    /// (<see cref="HdrPassthroughBlocker"/>); "" for SDR projects or a non-matching Colorspace choice.
    /// </summary>
    public string ProjectColorNote => Colorspace == MatchProject && ProjectSettings.IsHdr(Document.ColorSpace)
        ? HdrPassthroughBlocker is { } why
            ? $"{ProjectSettings.Label(Document.ColorSpace)} project: exported as BT.709 SDR, tone-mapped ({why})."
            : $"{ProjectSettings.Label(Document.ColorSpace)} project: exported as {(Document.ColorSpace == ProjectColorSpace.Hlg ? "HLG" : "HDR10 (PQ)")}, 10-bit BT.2020. "
              + "No mastering display or MaxCLL metadata is written."
        : "";

    // ----- HDR passthrough -----
    // An HDR project (PQ/HLG) stays HDR under Match Project Settings when 10-bit is enabled on a capable HEVC/AV1 encoder (x265,
    // SVT-AV1, or 10-bit hardware HEVC/AV1) and every picture is HDR with the project transfer: frames read back as P010 (BT.2020nc,
    // limited) and the encode is tagged BT.2020 + transfer (EncoderCommand). Otherwise tone-map to BT.709 and explain why: SDR
    // clips, images, text or captions in a PQ canvas would be wrong (SDR white is not PQ 1.0).

    /// <summary>HDR transfer tag: "smpte2084" (PQ) or "arib-std-b67" (HLG); null for SDR.</summary>
    internal static string? HdrTransferOf(ProjectColorSpace colorSpace) => colorSpace switch
    {
        ProjectColorSpace.HdrPq => "smpte2084",
        ProjectColorSpace.Hlg => "arib-std-b67",
        _ => null,
    };

    /// <summary>Whether an encoder produces HDR-capable 10-bit HEVC/AV1 (x265, SVT-AV1, hevc_*/av1_* hardware).</summary>
    internal static bool IsTenBitHdrCodec(EncoderOption encoder) =>
        encoder.Name is "libx265" or "libsvtav1" || encoder.Name.StartsWith("hevc_", StringComparison.Ordinal) || encoder.Name.StartsWith("av1_", StringComparison.Ordinal);

    /// <summary>
    /// First reason HDR passthrough is blocked, for the colour note; null when allowed. Checks in order: project/Colorspace, animated
    /// output, Copy, codec, 10-bit, GPU support, Extra Options pixel format, then pictures.
    /// </summary>
    internal static string? HdrBlocker(ProjectColorSpace project, bool matchProject, bool animated, EncoderOption encoder, bool tenBit, bool canTenBit,
        bool ownPixelFormat, string? sdrPicture)
    {
        if (!ProjectSettings.IsHdr(project))
            return "the project is SDR";
        if (!matchProject)
            return "Colorspace is HDR to SDR";
        if (animated)
            return "an animated GIF or WebP is 8-bit SDR";
        if (encoder.Family == EncoderFamily.Copy)
            return "Copy keeps the file as it is";
        if (!IsTenBitHdrCodec(encoder))
            return "choose an HEVC or AV1 encoder and tick 10-bit to keep HDR";
        if (!tenBit)
            return "tick 10-bit to keep HDR";
        if (!canTenBit)
            return $"{encoder.DisplayName} has no 10-bit on this card";
        if (ownPixelFormat)
            return "Extra Options set a pixel format of their own";
        return sdrPicture is { } picture ? $"{picture}, which an HDR export would show too bright" : null;
    }

    /// <summary>HDR passthrough blocker for this export (<see cref="HdrBlocker"/>), or null.</summary>
    public string? HdrPassthroughBlocker => HdrBlocker(Document.ColorSpace, Colorspace == MatchProject, IsAnimatedOutput, VideoEncoder, TenBit,
        CanTenBit, ExtraVideoArguments.Contains("-pix_fmt", StringComparison.Ordinal), ProjectSettings.IsHdr(Document.ColorSpace) ? SdrPicture() : null);

    /// <summary>Whether the export keeps HDR (10-bit BT.2020 with transfer; CompileExportState.HdrTransfer).</summary>
    public bool IsHdrPassthrough => HdrPassthroughBlocker is null;

    /// <summary>
    /// Describes the first visible picture that is not HDR in the project transfer (image, text, captions, burned subtitles, or a video
    /// of another or unprobed transfer); null when all match.
    /// </summary>
    private string? SdrPicture()
    {
        // Auto-Captions render from text cues or export-time transcription alike (CommandUsesCaptions).
        if (AutoCaptions)
            return "the captions are SDR";
        if (SubtitleTracks.Any(t => t.Action == SubtitleTrack.HardSub))
            return "burned subtitles are SDR";

        foreach (var track in Document.Tracks)
        {
            if (track.IsHidden || track.Kind is TimelineTrackKind.Audio or TimelineTrackKind.Adjustment)
                continue;
            foreach (var clip in track.Clips)
            {
                switch (clip.ContentKind)
                {
                    case LayerKind.Image:
                        return "an image clip is SDR";
                    case LayerKind.Text or LayerKind.Captions:
                        return "a text clip is SDR";
                    case LayerKind.VideoFile or LayerKind.MainVideo or LayerKind.Video when clip.SourcePath is { Length: > 0 } path:
                        var name = System.IO.Path.GetFileName(path);
                        if (MediaProbe.KnownInfo(path)?.Video is not { } video)
                            return $"{name} is not inspected yet";
                        if (ProjectSettings.ColorSpaceOf(video.ColorTransfer) != Document.ColorSpace)
                            return $"{name} is {ProjectSettings.Label(ProjectSettings.ColorSpaceOf(video.ColorTransfer))}";
                        break;
                }
            }
        }

        return null;
    }

    /// <summary>One-line summary: "1920 x 1080, 60 fps, BT.709 SDR, Square Pixels (1:1), 48 kHz".</summary>
    public static string Describe(ProjectSettings settings)
    {
        var size = settings.NaturalWidth > 0 && settings.NaturalHeight > 0
            ? settings.Vertical ? $"{settings.NaturalHeight} x {settings.NaturalWidth}" : $"{settings.NaturalWidth} x {settings.NaturalHeight}"
            : "size from the first clip";
        var fps = settings.FrameRate.Fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var khz = (settings.SampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return $"{size}, {fps} fps, {ProjectSettings.Label(settings.ColorSpace)}, {ProjectSettings.Label(settings.PixelAspect)}, {khz} kHz";
    }

    /// <summary>
    /// Sets the colour space from the first video on an empty Sequence (PQ, HLG, else BT.709 SDR) unless the frame is explicit
    /// (<see cref="TimelineDocument.ExplicitFrame"/>); outside any transaction, like rate and natural size. Pixel aspect and sample rate
    /// never come from clips (defaults: square, 48 kHz). An HDR first clip makes the project HDR; export then follows the passthrough rule.
    /// </summary>
    private void TakeProjectSettingsFrom(MediaInfo info)
    {
        if (Document.ExplicitFrame)
            return;

        var colorSpace = info.Video is { } video ? ProjectSettings.ColorSpaceOf(video.ColorTransfer) : ProjectColorSpace.Sdr709;
        Edit("take the first clip's colour space", edit => edit.SetProjectSettings(colorSpace, Document.PixelAspect, Document.SampleRate), undoable: false);
    }

    // ----- A New Project from the studio (MainWindow.CreateProjectAsync) -----

    /// <summary>
    /// Applies New Project studio choices to a fresh project: name and folder for the first save, Game Preset (pending if no file),
    /// then frame, rate and colour space (overriding the preset's orientation). No undo step, not dirty. False (status line) if refused.
    /// </summary>
    public bool TakeNewProject(NewProjectRequest request)
    {
        KeepNewProjectName(request.Name, request.Folder);
        if (request.Files.Count == 0 && request.StylePath is { Length: > 0 } stylePath && ReadStyle(stylePath) is { } style)
            ApplyStyle(style, layout: true, color: true, blur: true, subtitles: true);

        var applied = ApplyProjectSettings(request.FrameWidth, request.FrameHeight, request.FrameRate, request.ColorSpace,
            ProjectPixelAspect.Square, TimelineDocument.DefaultSampleRate, undoable: false);
        ForgetHistory();
        MarkSaved();
        return applied;
    }

    /// <summary>Re-applies the studio frame after New Project files open (a preset layout may have changed orientation). No undo step.</summary>
    public void KeepNewProjectFrame(NewProjectRequest request)
    {
        if (request.FrameWidth < SmallestFrameSide || request.FrameHeight < SmallestFrameSide)
            return;

        var vertical = request.FrameHeight > request.FrameWidth;
        if (vertical != UseVerticalResolution || OutputWidth.Length > 0 || OutputHeight.Length > 0)
            ApplyProjectSettings(request.FrameWidth, request.FrameHeight, Document.ExplicitFrameRate ? Document.FrameRate : null,
                Document.ColorSpace, Document.PixelAspect, Document.SampleRate, undoable: false);
    }

    // ----- The name a New Project was given, kept for its first save -----

    /// <summary>
    /// Studio-given project name offered by the first save ("" when none or after saving); change notification renames the active tab.
    /// </summary>
    public string NewProjectName
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            OnPropertyChanged();
        }
    } = "";

    /// <summary>Studio-chosen folder (null = the Projects folder).</summary>
    public string? NewProjectFolder { get; private set; }

    /// <summary>Stores the studio name and folder for the first save (cleared by the next New Project or a save).</summary>
    public void KeepNewProjectName(string name, string? folder)
    {
        NewProjectName = name.Trim();
        NewProjectFolder = string.IsNullOrWhiteSpace(folder) || SamePath(folder, AppPaths.Projects) ? null : folder;
    }

    /// <summary>Whether two paths are the same (full-path, case-insensitive, trailing separator ignored); false for invalid paths.</summary>
    private static bool SamePath(string one, string other)
    {
        if (one.Length == 0 || other.Length == 0)
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(one).TrimEnd('\\', '/'), Path.GetFullPath(other).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Project name as a file name (as ProjectStore.Save): invalid characters become "_", empty becomes "Project".</summary>
    private static string SafeFileName(string name)
    {
        var fileName = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return fileName.Length == 0 ? "Project" : fileName;
    }

    /// <summary>First-save path in the studio-chosen folder; null when no folder was chosen.</summary>
    private string? NewProjectPathFor(string name) =>
        CurrentProjectPath.Length > 0 || NewProjectFolder is not { } folder ? null : Path.Combine(folder, SafeFileName(name) + ProjectStore.Extension);

    /// <summary>Target file for <see cref="SaveProject"/>: portable folder, studio folder, or Projects.</summary>
    private string SavedProjectPathFor(string name) =>
        AppSettings.Current.FullyPortableProjectSave
            ? PortableProject.PathFor(name.Trim())
            : NewProjectPathFor(name) ?? Path.Combine(ProjectStore.Folder, SafeFileName(name) + ProjectStore.Extension);

    /// <summary>
    /// Default studio name: "New Project", or the first unused "New Project N" in the folder (blank = Projects), checking files and
    /// portable project folders.
    /// </summary>
    public static string FreeProjectName(string? folder)
    {
        const string Stem = "New Project";
        var where = string.IsNullOrWhiteSpace(folder) ? AppPaths.Projects : folder.Trim().Trim('"');
        for (var number = 1; number < 10000; number++)
        {
            var name = number == 1 ? Stem : $"{Stem} {number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (!Taken(name))
                return name;
        }

        return Stem;

        bool Taken(string name)
        {
            try
            {
                return File.Exists(Path.Combine(where, name + ProjectStore.Extension)) || Directory.Exists(Path.Combine(where, name));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// VM-held frame state (Use Vertical Resolution and Export Resolution boxes, not in the document), carried only by Project Settings
/// undo steps (<see cref="TimelineSnapshot.Frame"/>).
/// </summary>
public sealed record ProjectSettingsFrame(bool Vertical, string OutputWidth, string OutputHeight);
