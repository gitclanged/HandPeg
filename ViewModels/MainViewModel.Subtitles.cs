using System.Collections.ObjectModel;
using System.IO;
using CliWrap;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The subtitle track: cues imported from a file (.srt, .vtt; or the timings of a .sup or VobSub), made from the
// auto-captions, or typed in the Subtitle Editor. There is one. While it has text cues, the captions are made
// of them instead of being transcribed: they are drawn in the caption style, in the caption box, by the same
// steps (the player's own subtitle track for the preview, the caption overlay for the export).
public partial class MainViewModel
{
    public ObservableCollection<SubtitleCue> SubtitleCues { get; } = [];

    /// <summary>The file the track was imported from; empty for one made in HandPeg.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleTrackSummary))]
    private string _subtitleSourcePath = "";

    /// <summary>The track is pictures (PGS, VobSub): its cues are when the file's pictures are shown, and have no words to edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextCues))]
    [NotifyPropertyChangedFor(nameof(SubtitleTrackSummary))]
    private bool _subtitlesAreImages;

    /// <summary>Milliseconds every cue is shifted by: later when positive, earlier when negative.</summary>
    [ObservableProperty] private double _subtitleOffsetMs;

    /// <summary>The Subtitle Editor is open: it draws the subtitles in a player of its own, and the main player leaves them out meanwhile.</summary>
    [ObservableProperty] private bool _isSubtitleEditorOpen;

    public bool HasSubtitleCues => SubtitleCues.Count > 0;

    /// <summary>The track has words, which the captions are then made of.</summary>
    public bool HasTextCues => HasSubtitleCues && !SubtitlesAreImages;

    public string SubtitleTrackSummary
    {
        get
        {
            if (!HasSubtitleCues)
                return "No subtitle track: import a file, or make one from the auto-captions with Edit Subtitles.";

            var from = SubtitleSourcePath.Length > 0 ? Path.GetFileName(SubtitleSourcePath) : "made in HandPeg";
            var count = $"{SubtitleCues.Count} cue{(SubtitleCues.Count == 1 ? "" : "s")}";
            return SubtitlesAreImages
                ? $"{from}  ·  {count}, image-based: shown in the player and retimed in the Subtitle Editor, but not written into the export"
                : $"{from}  ·  {count}, drawn as captions in the caption style";
        }
    }

    private double SubtitleOffsetSeconds => double.IsFinite(SubtitleOffsetMs) ? SubtitleOffsetMs / 1000 : 0;

    // A picture track as it was read from its file, cue for cue: what the edited times are measured against.
    private List<SubtitleCue> _subtitleOriginal = [];
    private string _imageSubtitleFile = "";
    private int _imageSubtitleRun;

    /// <summary>Reads a subtitle file in as the subtitle track, replacing the one there was. Returns false, with the reason in the status bar, when it cannot be read.</summary>
    public bool ImportSubtitles(string path)
    {
        path = path.Trim().Trim('"');
        List<SubtitleCue> cues;
        try
        {
            cues = SubtitleFiles.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            StatusText = $"The subtitles could not be read: {ex.Message}";
            return false;
        }

        if (cues.Count == 0)
        {
            StatusText = $"No subtitles were found in {Path.GetFileName(path)}.";
            return false;
        }

        var images = SubtitleFiles.IsImageFile(path);
        SetSubtitleTrack(cues, path, images, 0);
        Log($"Imported {cues.Count} subtitle cue{(cues.Count == 1 ? "" : "s")} from {Path.GetFileName(path)}{(images ? " (image-based: timing only)" : "")}");

        // Words are drawn as captions, so captions are switched on for them; and shown, where there is a player to show them.
        if (!images && !AutoCaptions)
            AutoCaptions = true;
        if (HasSource && !_isBackgroundWorker)
        {
            // Pictures are handed to the player as they are; words are shown by the subtitles preview, switched on for them.
            if (!images && !PreviewSubtitles)
                PreviewSubtitles = true;
            else
                RefreshSubtitlePreview();
        }

        return true;
    }

    [RelayCommand]
    private void ClearSubtitles()
    {
        if (!HasSubtitleCues)
            return;

        SetSubtitleTrack([], "", false, 0);
        RefreshSubtitlePreview();
        StatusText = "Subtitle track removed.";
    }

    private void SetSubtitleTrack(IEnumerable<SubtitleCue> cues, string path, bool images, double offsetMs)
    {
        SubtitleCues.Clear();
        foreach (var cue in cues)
            SubtitleCues.Add(cue);

        _subtitleOriginal = images ? SubtitleCues.Select(c => c.Clone()).ToList() : [];
        (SubtitleSourcePath, SubtitlesAreImages, SubtitleOffsetMs) = (path, images, offsetMs);
        NotifySubtitlesChanged();
    }

    private void NotifySubtitlesChanged()
    {
        OnPropertyChanged(nameof(HasSubtitleCues));
        OnPropertyChanged(nameof(HasTextCues));
        OnPropertyChanged(nameof(SubtitleTrackSummary));
        OnPropertyChanged(nameof(CaptionHint));
        TimelineChanged?.Invoke();
    }

    /// <summary>
    /// Called when cues were edited by hand (in the Subtitle Editor): they are put in order and numbered again,
    /// the timeline shows them as they are now, and the main player's subtitles are written again.
    /// </summary>
    public void CommitSubtitleEdits()
    {
        // A picture track's cues stay in the file's order: that is how they are matched to its pictures.
        if (!SubtitlesAreImages)
        {
            var ordered = SubtitleFiles.Numbered(SubtitleCues.ToList());
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(SubtitleCues[i], ordered[i]))
                    SubtitleCues.Move(SubtitleCues.IndexOf(ordered[i]), i);
            }
        }

        NotifySubtitlesChanged();
        RefreshSubtitlePreview();
        GenerateCommand();
    }

    partial void OnSubtitleOffsetMsChanged(double value)
    {
        if (HasSubtitleCues)
        {
            TimelineChanged?.Invoke();
            RefreshSubtitlePreview();
        }
    }

    partial void OnIsSubtitleEditorOpenChanged(bool value)
    {
        // Closed: the main player draws them again, as they now are. Open: it lets go of them.
        if (!value)
            RefreshSubtitlePreview();
        if (!_isBackgroundWorker)
            LiveFilterInvalidated?.Invoke();
    }

    /// <summary>Has the main player show the subtitle track as it is now.</summary>
    private void RefreshSubtitlePreview()
    {
        if (_isBackgroundWorker)
            return;

        if (SubtitlesAreImages || !HasSubtitleCues)
        {
            _imageSubtitleFile = HasSubtitleCues ? WriteImageSubtitles() ?? "" : "";
            LiveFilterInvalidated?.Invoke();
        }

        if (PreviewSubtitles && !IsSubtitleEditorOpen)
            RefreshLiveCaptionsSoon();
    }

    /// <summary>The picture track as a file a player can be given: the imported file itself while nothing was moved, a retimed copy of it otherwise.</summary>
    private string? WriteImageSubtitles()
    {
        if (!SubtitlesAreImages || !File.Exists(SubtitleSourcePath))
            return null;

        var moved = Math.Abs(SubtitleOffsetSeconds) > 0.0005 || _subtitleOriginal.Count != SubtitleCues.Count
            || SubtitleCues.Where((cue, i) => Math.Abs(cue.Start - _subtitleOriginal[i].Start) > 0.0005 || Math.Abs(cue.End - _subtitleOriginal[i].End) > 0.0005).Any();
        if (!moved)
            return SubtitleFiles.IdxPathOf(SubtitleSourcePath) is var idx && Path.GetExtension(SubtitleSourcePath).ToLowerInvariant() is ".sub" or ".vobsub" ? idx : SubtitleSourcePath;

        try
        {
            // A new name each time: the player only loads a file again when its name has changed.
            return SubtitleFiles.WriteRetimed(SubtitleSourcePath, _subtitleOriginal, SubtitleCues, SubtitleOffsetSeconds, _workFolder, $"subtitles_retimed_{++_imageSubtitleRun}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"The retimed subtitles could not be written: {ex.Message}";
            return null;
        }
    }

    /// <summary>The picture track's file for the main player; empty when there is none, or while the Subtitle Editor shows it instead.</summary>
    private string LiveImageSubtitlePath => SubtitlesAreImages && HasSubtitleCues && !IsSubtitleEditorOpen ? _imageSubtitleFile : "";

    /// <summary>
    /// Writes the subtitle track as a file for a player to draw over a blank picture: the Subtitle Editor's
    /// own. Text is written as captions for the whole frame, in the caption style; a picture track is its
    /// file, retimed. Returns the file, or null when there is nothing to show.
    /// </summary>
    public string? WriteSubtitlePreview(string name)
    {
        if (!HasSubtitleCues)
            return null;
        if (SubtitlesAreImages)
            return WriteImageSubtitles();

        // Written for the caption box alone, not the frame: the editor's player shows a picture of the box's
        // shape, so the words fill it and are large enough to read, however small the box is on the frame.
        var path = Path.Combine(_workFolder, name + ".ass");
        var lines = GetCueCaptions([]);
        WriteCaptionFile(lines.SelectMany(l => l).ToList(), lines, path, 0, forPlayer: false);
        return path;
    }

    /// <summary>
    /// Makes the blank video the Subtitle Editor's player plays: black, the shape of the output frame, as long
    /// as the timeline. It is a real file, encoded once and kept for the session, because a player can go to
    /// any moment of a file at once; a picture generated as it is played (lavfi's "color") can only be played
    /// forwards, and every seek into it breaks it. It is tiny (the subtitles are drawn at the size of the
    /// window, not of the video), so it is made in a moment and is a few hundred kilobytes for hours.
    /// Returns the file, or null when it could not be made.
    /// </summary>
    public async Task<string?> PrepareSubtitleCanvasAsync(double seconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return null;

        var (frameWidth, frameHeight) = SubtitleFrameSize;
        const int height = 72;
        var width = Math.Clamp((int)Math.Round(height * (double)frameWidth / Math.Max(frameHeight, 1) / 2) * 2, 16, 1024);
        var length = (int)Math.Clamp(Math.Ceiling(seconds), 1, 12 * 3600);
        var path = Path.Combine(_workFolder, $"subtitle_canvas_{width}x{height}_{length}.mkv");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        Directory.CreateDirectory(_workFolder);
        var making = Path.Combine(_workFolder, $"subtitle_canvas_{Guid.NewGuid():N}.mkv");
        try
        {
            // An I-frame every second, so that any moment is a short step from one.
            var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                .WithArguments(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c=black:s={width}x{height}:r=10",
                    "-t", length.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-tune", "stillimage",
                    "-g", "10", "-pix_fmt", "yuv420p", making])
                .WithValidation(CommandResultValidation.None), cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(making))
            {
                AppLog.Write($"The Subtitle Editor's blank video could not be made (exit code {result.ExitCode}): {result.StandardError.Trim()}");
                return null;
            }

            File.Move(making, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write("The Subtitle Editor's blank video could not be made", ex);
            return null;
        }
    }

    /// <summary>Where the import row of the Subtitles tab is shown: always in Encoder Mode, and in Editor Mode under the legacy settings.</summary>
    public bool ShowSubtitleImportRow => IsEncoderMode || !ShowAutoCaptions;

    /// <summary>The size of the frame the subtitles are placed on, for a player that shows them on a blank picture of that shape.</summary>
    public (int Width, int Height) SubtitleFrameSize
    {
        get
        {
            var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);

            // Pictures are placed on the whole frame by their own file. Words are shown close up: the caption box and nothing around it.
            if (SubtitlesAreImages)
                return (frameWidth, frameHeight);
            var (_, _, width, height) = GetCaptionRect(frameWidth, frameHeight);
            return (Math.Max(width, 16), Math.Max(height, 16));
        }
    }

    /// <summary>
    /// The text cues as captions: one per cue, its words spread over its time by their length. With ranges
    /// (the kept segments of a cut encode, which are played end to end) each cue is moved to where its moment
    /// falls in the output, and one that is cut out is left out.
    /// </summary>
    private List<List<CaptionWord>> GetCueCaptions(IReadOnlyList<(double Start, double End)> ranges)
    {
        var captions = new List<List<CaptionWord>>();
        foreach (var cue in SubtitleCues.OrderBy(c => c.Start))
        {
            var (start, end) = (cue.Start + SubtitleOffsetSeconds, cue.End + SubtitleOffsetSeconds);
            if (ranges.Count > 0)
            {
                // Where the part of the cue that is kept begins and ends, on the output's clock.
                double? from = null, to = null;
                var before = 0.0;
                foreach (var range in ranges)
                {
                    var (a, b) = (Math.Max(start, range.Start), Math.Min(end, range.End));
                    if (b - a > 0.01)
                    {
                        from ??= before + a - range.Start;
                        to = before + b - range.Start;
                    }

                    before += range.End - range.Start;
                }

                if (from is null || to is null)
                    continue;
                (start, end) = (from.Value, to.Value);
            }

            start = Math.Max(start, 0);
            var words = cue.GetWords();
            if (words.Length == 0 || end - start < 0.02)
                continue;

            // Each word from its boundary to the next: the ones set in the Subtitle Editor, or its share by length.
            var breaks = cue.GetBreaks();
            var caption = new List<CaptionWord>(words.Length);
            for (var i = 0; i < words.Length; i++)
            {
                var (from, to) = (i == 0 ? 0 : breaks[i - 1], i < breaks.Length ? breaks[i] : 1);
                caption.Add(new CaptionWord(words[i], start + (end - start) * from, start + (end - start) * to));
            }

            captions.Add(caption);
        }

        return captions;
    }

    /// <summary>What is drawn on the caption layer's time bar: the cues, or failing those the captions last transcribed.</summary>
    public IReadOnlyList<(double Start, double End, string Text, SubtitleCue? Cue)> GetSubtitleSegments()
    {
        if (HasSubtitleCues)
            return SubtitleCues.Select(c => (c.Start + SubtitleOffsetSeconds, c.End + SubtitleOffsetSeconds, c.Text.ReplaceLineEndings(" "), (SubtitleCue?)c)).ToList();

        return AutoCaptions
            ? _generatedCaptions.Select(c => (c[0].Start, c[^1].End, string.Join(" ", c.Select(w => w.Text)), (SubtitleCue?)null)).ToList()
            : [];
    }

    // The captions last transcribed for the preview, line by line, on the timeline's clock.
    private List<List<CaptionWord>> _generatedCaptions = [];

    /// <summary>
    /// Makes the subtitle track from the auto-captions: transcribes what they listen to (or takes the words
    /// already transcribed) and turns each caption into a cue, which can then be edited. From then on the
    /// captions are made of the cues, and nothing is transcribed again until the track is removed.
    /// </summary>
    public Task ConvertCaptionsToCuesAsync() => RunOperationAsync(async cancellationToken =>
    {
        var (words, problem) = await GetCaptionWordsAsync([], cancellationToken);
        if (words is null)
            return problem;

        var offset = CaptionUseVoiceover ? VoiceoverStartSeconds : 0;
        var cues = CaptionGenerator.Lines(words, CaptionStyle).Select(line =>
        {
            var (start, end) = (line[0].Start, Math.Max(line[^1].End, line[0].Start + 0.1));
            var cue = new SubtitleCue { Start = start + offset, End = end + offset, Text = string.Join(" ", line.Select(w => w.Text.Trim())) };

            // The words keep the moments they were heard at: each boundary is where the next word was heard to begin.
            if (line.Count > 1)
                cue.WordBreaks = line.Skip(1).Select(w => (w.Start - start) / (end - start)).ToArray();
            return cue;
        }).ToList();
        if (cues.Count == 0)
            return "No speech was found to make subtitles of.";

        SetSubtitleTrack(SubtitleFiles.Numbered(cues), "", false, 0);
        return $"Made {cues.Count} subtitle cues from the auto-captions: they can now be edited, and the captions are drawn from them.";
    });

    /// <summary>Adds a cue by hand. The first one makes the track; its words are drawn as captions, so captions are switched on for them.</summary>
    public SubtitleCue AddSubtitleCue(double start, double end, string text)
    {
        var cue = new SubtitleCue { Index = SubtitleCues.Count + 1, Start = Math.Max(start, 0), End = Math.Max(end, start + 0.1), Text = text };
        SubtitleCues.Add(cue);
        if (!AutoCaptions)
            AutoCaptions = true;
        NotifySubtitlesChanged();
        return cue;
    }

    public void RemoveSubtitleCue(SubtitleCue cue)
    {
        if (SubtitleCues.Remove(cue))
            NotifySubtitlesChanged();
    }

    /// <summary>
    /// Cuts a cue in two at a moment inside it (seconds on the cue's own clock, before the offset). The words
    /// said before the cut stay, the rest go to a new cue that follows, and each word keeps when it is said.
    /// A cue of one word becomes two cues of that word. Returns the new cue, or null when the moment is not inside the cue.
    /// </summary>
    public SubtitleCue? SplitSubtitleCue(SubtitleCue cue, double at)
    {
        if (SubtitlesAreImages || !SubtitleCues.Contains(cue) || at <= cue.Start + 0.05 || at >= cue.End - 0.05)
            return null;

        var (words, breaks) = (cue.GetWords(), cue.GetBreaks());
        var share = (at - cue.Start) / (cue.End - cue.Start);
        var second = new SubtitleCue { Start = Math.Round(at, 3), End = cue.End, Text = cue.Text };
        if (words.Length > 1)
        {
            // How many words are said before the cut; at least one stays and at least one goes.
            var kept = Math.Clamp(breaks.Count(b => b < share) + 1, 1, words.Length - 1);
            second.Text = string.Join(" ", words[kept..]);
            second.WordBreaks = breaks[kept..].Select(b => (b - share) / (1 - share)).ToArray();
            cue.Text = string.Join(" ", words[..kept]);
            cue.WordBreaks = breaks[..(kept - 1)].Select(b => b / share).ToArray();
        }

        cue.End = Math.Round(at, 3);
        SubtitleCues.Insert(SubtitleCues.IndexOf(cue) + 1, second);
        NotifySubtitlesChanged();
        return second;
    }

    /// <summary>Takes one word out of a cue; the words beside it share its time. False for a cue's only word.</summary>
    public bool RemoveSubtitleWord(SubtitleCue cue, int word)
    {
        var (words, breaks) = (cue.GetWords(), cue.GetBreaks().ToList());
        if (SubtitlesAreImages || words.Length < 2 || word < 0 || word >= words.Length)
            return false;

        // The boundary that goes is the one after the word, or before it when it is the last.
        breaks.RemoveAt(Math.Min(word, breaks.Count - 1));
        cue.Text = string.Join(" ", words.Where((_, i) => i != word));
        cue.WordBreaks = breaks.Count > 0 ? [.. breaks] : null;
        NotifySubtitlesChanged();
        return true;
    }

    /// <summary>The traditional view of the Subtitles tab (the source's own subtitle tracks), for the button that opens it in Editor Mode.</summary>
    public bool ShowLegacySubtitles
    {
        get => !ShowAutoCaptions;
        set => ShowAutoCaptions = !value;
    }

    private SubtitleTrackData? CaptureSubtitles() => !HasSubtitleCues ? null : new SubtitleTrackData
    {
        SourcePath = SubtitleSourcePath,
        IsImage = SubtitlesAreImages,
        OffsetMs = SubtitleOffsetMs,
        Cues = SubtitleCues.Select(c => new SubtitleCueState(c.Start, c.End, c.Text, c.WordBreaks)).ToList(),
    };

    private void ApplySubtitles(SubtitleTrackData? data)
    {
        var cues = (data?.Cues ?? []).Where(c => double.IsFinite(c.Start) && double.IsFinite(c.End) && c.End > c.Start)
            .Select((c, i) => new SubtitleCue { Index = i + 1, Start = c.Start, End = c.End, Text = c.Text ?? "", WordBreaks = c.Breaks }).ToList();
        var images = data?.IsImage == true && cues.Count > 0;
        SetSubtitleTrack(cues, cues.Count > 0 ? data!.SourcePath ?? "" : "", images, cues.Count > 0 ? data!.OffsetMs : 0);

        // A picture track is measured against its file as it is on disk: read again, when it is still there and still the same.
        if (images)
        {
            try
            {
                var read = SubtitleFiles.Read(SubtitleSourcePath);
                _subtitleOriginal = read.Count == cues.Count ? read : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _subtitleOriginal = [];
            }

            _imageSubtitleFile = WriteImageSubtitles() ?? "";
        }
    }
}
