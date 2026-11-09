using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Project Settings (5.4A) -----
// Every project has explicit settings (Models/Timeline/ProjectSettings.cs): its natural frame and whether it stands on its side,
// its frame rate, colour space, pixel aspect and sample rate. The first video opened onto an empty Sequence sets them
// (TakeProjectSettingsFrom), unless the New Project studio chose them first (ApplyProjectSettings, which marks the frame and the
// rate as chosen on purpose so the first file keeps them). Export's "Match Project Settings" follows them.
public partial class MainViewModel
{
    /// <summary>The open project's settings: the document's, with the frame's turn to tall as the view model holds it (Use Vertical Resolution).</summary>
    public ProjectSettings CurrentProjectSettings => Document.ProjectSettings with { Vertical = UseVerticalResolution };

    /// <summary>Whether any clip lies on the Sequence (then its frame rate is fixed: its edges lie on that rate's frames).</summary>
    public bool SequenceHasClips => Document.Tracks.Exists(t => t.Clips.Count > 0);

    /// <summary>
    /// Gives the project its settings (the New Project studio, the Project Manager's Project Settings). <paramref name="frameWidth"/> x
    /// <paramref name="frameHeight"/> is the output frame wanted (1080 x 1920 for 9:16): a taller than wide frame is a natural size
    /// turned on its side with Use Vertical Resolution, and Export's Resolution goes back to "Match Project Settings"; 0 leaves the
    /// size as it is (to the first clip on an empty Sequence), and Export's Resolution boxes with it. A null <paramref name="frameRate"/>
    /// leaves the rate as it is (the first clip's own on an empty Sequence). A frame or rate given is kept by the first file opened onto
    /// the empty Sequence (<see cref="TimelineDocument.ExplicitFrame"/>); one not given leaves that mark as it was. One undo step when
    /// <paramref name="undoable"/> and the Sequence has clips, which puts back the frame as the view model holds it too (its turn to
    /// tall and the Resolution boxes, <see cref="ProjectSettingsFrame"/>). False (and a status line) when refused: a frame-rate change
    /// on a Sequence that has clips, whose edges lie on the old rate's frames (there is no requantize).
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
        // A frame or rate not given leaves its "chosen on purpose" mark as it was (5.4A: Project Settings sends only what was changed).
        var (explicitFrame, explicitRate) = (framed || Document.ExplicitFrame, frameRate.HasValue || Document.ExplicitFrameRate);
        if (!frameChanges && !rateChanges && !settingsChange && Document.ExplicitFrame == explicitFrame && Document.ExplicitFrameRate == explicitRate)
            return true;

        // The step is of the timeline before any of it (the frame and rate are the document's own values, set outside the transaction),
        // with the frame as the view model holds it, which no other step carries: Undo and Redo put the turn and the boxes back too.
        var before = undoable && hasClips && _restoring == 0 ? CaptureSnapshot() with { Frame = CaptureProjectFrame() } : null;

        (Document.ExplicitFrame, Document.ExplicitFrameRate) = (explicitFrame, explicitRate);
        if (frameRate is { } given)
            Document.FrameRate = given;
        if (framed)
        {
            (Document.NaturalWidth, Document.NaturalHeight) = (naturalWidth, naturalHeight);

            // Turned without the boxes swapping on the way, then blank: "Match Project Settings", the Sequence's own size.
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

        // The colour space, pixel aspect and sample rate through a transaction of their own (no step: the one taken above is the step).
        if (settingsChange)
            Edit("change the Project Settings", edit => edit.SetProjectSettings(colorSpace, pixelAspect, sampleRate), undoable: false);

        NoteDocumentShape();
        GenerateCommand();
        TimelineChanged?.Invoke();

        if (before is not null && (frameChanges || rateChanges || settingsChange))
            Checkpoint("change the Project Settings", before);

        StatusText = $"Project Settings: {Describe(CurrentProjectSettings)}.";
        return true;
    }

    /// <summary>
    /// Clears what chose the frame or the rate on purpose while the Sequence is empty (Project Settings: the frame boxes emptied, or Auto
    /// from First Clip chosen again), so the first file opened gives its own. Nothing once the Sequence has clips. No undo step: an empty
    /// Sequence takes none (as <see cref="ApplyProjectSettings"/>).
    /// </summary>
    public void LeaveToFirstClip(bool frame, bool rate)
    {
        if (SequenceHasClips)
            return;

        Document.ExplicitFrame &= !frame;
        Document.ExplicitFrameRate &= !rate;
    }

    // ----- The frame as the view model holds it, in a Project Settings step (5.4A) -----

    /// <summary>The turn to tall and Export's Resolution boxes as they are now: what a Project Settings step puts back with the document.</summary>
    private ProjectSettingsFrame CaptureProjectFrame() => new(UseVerticalResolution, OutputWidth, OutputHeight);

    /// <summary>
    /// Puts a Project Settings step's frame back (RestoreTimeline): the turn to tall without the boxes swapping on the way, then the
    /// boxes exactly as they were.
    /// </summary>
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
    /// A line about how an HDR project exports under "Match Project Settings": kept HDR, 10-bit BT.2020 with its transfer (5.4B, the
    /// passthrough rule), or tone-mapped to BT.709 SDR and why (<see cref="HdrPassthroughBlocker"/>); "" for an SDR project, or while
    /// Colorspace asks for something else.
    /// </summary>
    public string ProjectColorNote => Colorspace == MatchProject && ProjectSettings.IsHdr(Document.ColorSpace)
        ? HdrPassthroughBlocker is { } why
            ? $"{ProjectSettings.Label(Document.ColorSpace)} project: exported as BT.709 SDR, tone-mapped ({why})."
            : $"{ProjectSettings.Label(Document.ColorSpace)} project: exported as {(Document.ColorSpace == ProjectColorSpace.Hlg ? "HLG" : "HDR10 (PQ)")}, 10-bit BT.2020. "
              + "No mastering display or MaxCLL metadata is written."
        : "";

    // ----- HDR passthrough (5.4B, Q1: map-export D6) -----
    // An HDR project (PQ or HLG) keeps its HDR when it is exported under "Match Project Settings" with 10-bit ticked on an encoder that
    // can write it (HEVC or AV1: x265, SVT-AV1, a hardware HEVC/AV1 encoder that does 10-bit on this card), and every picture on the
    // Sequence is HDR of the project's own transfer: the compositor then reads the frames back as 10-bit P010 (BT.2020nc, limited range)
    // and the encode is tagged BT.2020 with that transfer (EncoderCommand). Anything else is tone-mapped to BT.709 SDR as before, and the
    // colour note says why: an SDR clip, an image, text or captions drawn into a PQ canvas would be wrong (SDR white is not PQ's 1.0).

    /// <summary>The transfer an HDR export is tagged with: "smpte2084" (PQ) or "arib-std-b67" (HLG); null for SDR.</summary>
    internal static string? HdrTransferOf(ProjectColorSpace colorSpace) => colorSpace switch
    {
        ProjectColorSpace.HdrPq => "smpte2084",
        ProjectColorSpace.Hlg => "arib-std-b67",
        _ => null,
    };

    /// <summary>Whether an encoder writes HDR's 10-bit codecs: HEVC or AV1 (x265, SVT-AV1, hevc_* and av1_* hardware).</summary>
    internal static bool IsTenBitHdrCodec(EncoderOption encoder) =>
        encoder.Name is "libx265" or "libsvtav1" || encoder.Name.StartsWith("hevc_", StringComparison.Ordinal) || encoder.Name.StartsWith("av1_", StringComparison.Ordinal);

    /// <summary>
    /// Why an HDR project's export cannot keep HDR, in words for the colour note, or null when it can (the rule above, in order: the
    /// project and Colorspace, an animated output, Copy, the codec, 10-bit, the card, Extra Options' own pixel format, then the pictures).
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

    /// <summary>Why this export cannot keep the project's HDR (<see cref="HdrBlocker"/>), or null when it does.</summary>
    public string? HdrPassthroughBlocker => HdrBlocker(Document.ColorSpace, Colorspace == MatchProject, IsAnimatedOutput, VideoEncoder, TenBit,
        CanTenBit, ExtraVideoArguments.Contains("-pix_fmt", StringComparison.Ordinal), ProjectSettings.IsHdr(Document.ColorSpace) ? SdrPicture() : null);

    /// <summary>The export keeps the project's HDR: 10-bit BT.2020, tagged with its transfer (CompileExportState: HdrTransfer).</summary>
    public bool IsHdrPassthrough => HdrPassthroughBlocker is null;

    /// <summary>
    /// The first picture on the Sequence (a shown picture track) that is not HDR of the project's transfer, in words: an image, text,
    /// the captions or burned subtitles, or a video file of another transfer (or not inspected yet); null when every picture is.
    /// </summary>
    private string? SdrPicture()
    {
        // Auto-Captions are drawn whether they come from text cues or are transcribed at export (CommandUsesCaptions).
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

    /// <summary>A project's settings in a line: "1920 x 1080, 60 fps, BT.709 SDR, Square Pixels (1:1), 48 kHz".</summary>
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
    /// The first video opened onto an empty Sequence gives the project its colour space (its transfer: PQ, HLG, else BT.709 SDR), unless
    /// the frame was chosen on purpose (<see cref="TimelineDocument.ExplicitFrame"/>): outside any transaction, as the rate and the
    /// natural size are. The pixel aspect and sample rate are never the clip's: a new project starts at square pixels and 48 kHz, and
    /// what Project Settings chose before the first clip stays. (The user's decision, 5.4A: an HDR first clip makes the project HDR, and
    /// "Match Project Settings" then exports it tone-mapped to BT.709 SDR, the only output the pipeline writes.)
    /// </summary>
    private void TakeProjectSettingsFrom(MediaInfo info)
    {
        if (Document.ExplicitFrame)
            return;

        Document.ColorSpace = info.Video is { } video ? ProjectSettings.ColorSpaceOf(video.ColorTransfer) : ProjectColorSpace.Sdr709;
    }

    // ----- A New Project from the studio (MainWindow.CreateProjectAsync) -----

    /// <summary>
    /// Gives the fresh, empty project the New Project studio's settings: its name and folder kept for the first save, the Game Preset
    /// chosen (when no file is to be opened, its layout then waits for the first one), then the frame, rate and colour space, which
    /// win over the preset's own turn to tall or wide. No undo step and nothing to save: a project made is not an edit. False (and a
    /// status line) when the settings were refused.
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

    /// <summary>
    /// After the New Project's files are open: the frame chosen in the studio again, where opening them (a Game Preset's layout turning
    /// the frame tall or wide) changed it. No undo step.
    /// </summary>
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
    /// The name the New Project studio gave the project, which its first save offers ("" for none, or once it is saved). Told when it
    /// changes (Pass 5.4D-TabPerf: the project tab in front takes its name from it at once).
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

    /// <summary>The folder the New Project studio chose for it (null: the Projects folder, where every save goes).</summary>
    public string? NewProjectFolder { get; private set; }

    /// <summary>Keeps the name and folder a New Project was given, for its first save (cleared by the next New Project or a save).</summary>
    public void KeepNewProjectName(string name, string? folder)
    {
        NewProjectName = name.Trim();
        NewProjectFolder = string.IsNullOrWhiteSpace(folder) || SamePath(folder, AppPaths.Projects) ? null : folder;
    }

    /// <summary>Whether two paths name the same file or folder (as full paths, case aside, a trailing separator aside); false for a path that is none.</summary>
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

    /// <summary>A project's name as a file name, as ProjectStore.Save makes it: the characters a file name cannot hold as "_", "Project" for none.</summary>
    private static string SafeFileName(string name)
    {
        var fileName = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return fileName.Length == 0 ? "Project" : fileName;
    }

    /// <summary>Where the first save of a project named in the New Project studio goes when it chose a folder of its own; null otherwise.</summary>
    private string? NewProjectPathFor(string name) =>
        CurrentProjectPath.Length > 0 || NewProjectFolder is not { } folder ? null : Path.Combine(folder, SafeFileName(name) + ProjectStore.Extension);

    /// <summary>The file <see cref="SaveProject"/> writes for a name: a portable project's in its folder, the New Project's own folder's, or Projects'.</summary>
    private string SavedProjectPathFor(string name) =>
        AppSettings.Current.FullyPortableProjectSave
            ? PortableProject.PathFor(name.Trim())
            : NewProjectPathFor(name) ?? Path.Combine(ProjectStore.Folder, SafeFileName(name) + ProjectStore.Extension);

    /// <summary>
    /// The name the New Project studio offers (5.4A): "New Project", or the first of "New Project 2", "New Project 3"... that no project in
    /// the folder (blank: Projects) has yet, as a file or as a portable project's folder.
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
/// The frame as the view model holds it (5.4A): Use Vertical Resolution and Export's Resolution boxes, which are not the document's.
/// Carried by a Project Settings undo step alone (<see cref="TimelineSnapshot.Frame"/>), so its Undo and Redo put them back.
/// </summary>
public sealed record ProjectSettingsFrame(bool Vertical, string OutputWidth, string OutputHeight);
