using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>One spoken word and when it is said, in seconds from the start of the analysed audio.</summary>
public readonly record struct CaptionWord(string Text, double Start, double End);

/// <summary>
/// whisper.cpp decoding and VAD options from the Auto-Captions settings. Defaults match whisper's; options at their default are
/// omitted from the command line.
/// </summary>
/// <param name="BeamSize">Beam width (--beam-size).</param>
/// <param name="BestOf">Sampling candidates (--best-of).</param>
/// <param name="Temperature">Sampling temperature 0-1 (--temperature).</param>
/// <param name="TemperatureFallback">Retry failed segments at higher temperature; false emits --no-fallback.</param>
/// <param name="VadThreshold">VAD speech probability threshold 0-1 (--vad-threshold).</param>
public readonly record struct WhisperOptions(
    int BeamSize = 5, int BestOf = 5, double Temperature = 0, bool TemperatureFallback = true,
    double VadThreshold = 0.5, int VadMinSpeechMs = 250, int VadMinSilenceMs = 100, int VadSpeechPadMs = 30)
{
    /// <summary>The options as the settings have them, each held to what whisper accepts.</summary>
    public static WhisperOptions FromSettings(AppSettings settings) => new(
        Math.Clamp(settings.WhisperBeamSize, 1, 16), Math.Clamp(settings.WhisperBestOf, 1, 16),
        Math.Clamp(settings.WhisperTemperature, 0, 1), settings.WhisperTemperatureFallback,
        Math.Clamp(settings.VadThreshold, 0.05, 0.95), Math.Clamp(settings.VadMinSpeechMs, 0, 5000),
        Math.Clamp(settings.VadMinSilenceMs, 0, 5000), Math.Clamp(settings.VadSpeechPadMs, 0, 2000));
}

/// <summary>
/// Pending transcription snapshot: audio source, whisper model/language/decoding/VAD settings, and output formatting, captured at
/// creation so queued jobs use the settings from queue time.
/// </summary>
/// <param name="Ranges">Audio ranges concatenated for transcription; empty for the whole track.</param>
/// <param name="UseVad">Skip silence when the VAD model is installed and supported by whisper.exe.</param>
/// <param name="Width">Caption box width in output pixels.</param>
/// <param name="TextScale">Multiplier for style sizes authored at 1080p.</param>
/// <param name="AssPath">Output caption file path.</param>
/// <param name="Spans">
/// Clip windows playing the audio at creation time; words in audio time are mapped through these (CaptionProjection.ProjectWords).
/// </param>
public sealed record CaptionJob(
    string AudioPath, int TrackIndex, IReadOnlyList<(double Start, double End)> Ranges,
    string ModelPath, string Language, string Prompt, bool Translate, bool UseVad, WhisperOptions Options,
    CaptionStyle Style, int Width, int Height, double TextScale, double TimeOffset, string AssPath,
    IReadOnlyList<CaptionSpan>? Spans = null);

/// <summary>
/// Auto-captions pipeline: extract audio to 16 kHz mono WAV, transcribe with whisper.cpp (word timings), write an animated ASS file.
/// </summary>
public static partial class CaptionGenerator
{
    // With VAD, whisper transcribes concatenated speech segments and logs each segment's original and compacted times:
    //   whisper_vad: vad_segment_info: orig_start: 18.08, orig_end: 20.83, vad_start: 4.74, vad_end: 7.49
    [GeneratedRegex(@"vad_segment_info: orig_start: ([\d.]+), orig_end: ([\d.]+), vad_start: ([\d.]+), vad_end: ([\d.]+)")]
    private static partial Regex VadSegmentRegex();

    [GeneratedRegex(@"progress\s*=\s*(\d+)\s*%")]
    private static partial Regex ProgressRegex();

    /// <summary>VAD segment: original audio range and compacted (silence-removed) range.</summary>
    private readonly record struct SpeechStretch(double OriginalStart, double OriginalEnd, double Start, double End);

    /// <summary>
    /// Inter-word silence threshold (s): longer gaps start a new caption and end the previous one at its last word; shorter gaps are
    /// bridged so captions do not flicker.
    /// </summary>
    public const double SilentGapSeconds = 0.35;

    // Sizes in the style are given for a frame this many pixels on its shorter side.
    private const double ReferenceSide = 1080;

    /// <summary>
    /// FFmpeg command extracting the given ranges of an audio track, concatenated, to 16 kHz mono WAV for whisper.cpp. Empty = whole track.
    /// </summary>
    /// <param name="trackIndex">Zero-based audio track index.</param>
    public static string BuildExtractCommand(string audioPath, int trackIndex, IReadOnlyList<(double Start, double End)> ranges, string wavPath)
    {
        var track = $"0:a:{Math.Max(trackIndex, 0)}";
        var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? $"\"{DependencyUpdater.FfmpegPath}\"" : "ffmpeg";
        var output = $"-ar 16000 -ac 1 -c:a pcm_s16le \"{wavPath}\"";

        if (ranges.Count == 0)
            return $"{ffmpeg} -hide_banner -y -i \"{audioPath}\" -map {track} -vn {output}";

        // The same trim-and-join the encode itself uses, so the words land where they will be heard.
        var graph = new StringBuilder();
        for (var i = 0; i < ranges.Count; i++)
            graph.Append($"[{track}]atrim=start={Number(ranges[i].Start)}:end={Number(ranges[i].End)},asetpts=PTS-STARTPTS[a{i}];");
        for (var i = 0; i < ranges.Count; i++)
            graph.Append($"[a{i}]");
        graph.Append($"concat=n={ranges.Count}:v=0:a=1[speech]");

        return $"{ffmpeg} -hide_banner -y -i \"{audioPath}\" -filter_complex \"{graph}\" -map \"[speech]\" {output}";
    }

    /// <summary>
    /// Runs whisper.cpp on a WAV and returns words; requests full JSON output for per-token timing.
    /// </summary>
    /// <param name="language">whisper language code (en, es, ...) or auto.</param>
    /// <param name="prompt">Initial prompt with expected words and names; blank for none.</param>
    /// <param name="translate">Translate to English.</param>
    /// <param name="vadModelPath">
    /// VAD model path, or null to transcribe everything; VAD prevents hallucinated words in silence.
    /// </param>
    public static Task<List<CaptionWord>> TranscribeAsync(
        string wavPath, string modelPath, string language, string prompt, bool translate, string? vadModelPath, CancellationToken cancellationToken,
        IProgress<double>? progress = null, WhisperOptions? options = null) =>
        RunWhisperAsync(wavPath, modelPath, language, prompt, translate, vadModelPath, cancellationToken, progress, options, onProcessor: false);

    /// <param name="onProcessor">Force CPU (GPU retry after a crash on this audio).</param>
    private static async Task<List<CaptionWord>> RunWhisperAsync(
        string wavPath, string modelPath, string language, string prompt, bool translate, string? vadModelPath, CancellationToken cancellationToken,
        IProgress<double>? progress, WhisperOptions? options, bool onProcessor)
    {
        var useVad = !string.IsNullOrWhiteSpace(vadModelPath);
        var said = 0;

        // -ojf: full JSON; -of: output base (whisper appends .json); -np: quiet, omitted with VAD because its log carries segment times;
        // -pp: progress for the progress bar. The log also reports the CPU/GPU in use.
        var arguments = new List<string> { "-m", modelPath, "-f", wavPath, "-ojf", "-of", "", "-pp" };
        if (!string.IsNullOrWhiteSpace(language))
            arguments.AddRange(["--language", language.Trim()]);
        if (!string.IsNullOrWhiteSpace(prompt))
            arguments.AddRange(["--prompt", prompt]);
        if (translate)
            arguments.Add("--translate");
        if (useVad)
            arguments.AddRange(["--vad", "--vad-model", vadModelPath!]);

        // Emit only non-default options, and only those the whisper.exe supports, so older custom builds still run.
        var (set, standard, help) = (options ?? new WhisperOptions(), new WhisperOptions(), await HelpAsync(cancellationToken));
        void Option(string name, string value)
        {
            if (help.Contains(name, StringComparison.Ordinal))
                arguments.AddRange([name, value]);
        }

        if (set.BeamSize != standard.BeamSize)
            Option("--beam-size", set.BeamSize.ToString(CultureInfo.InvariantCulture));
        if (set.BestOf != standard.BestOf)
            Option("--best-of", set.BestOf.ToString(CultureInfo.InvariantCulture));
        if (set.Temperature != standard.Temperature)
            Option("--temperature", Number(set.Temperature));
        if (!set.TemperatureFallback && help.Contains("--no-fallback", StringComparison.Ordinal))
            arguments.Add("--no-fallback");

        // Whisper threads setting; Auto leaves whisper's default (max 4). Affects speed only.
        if (AppSettings.Current.WhisperThreads > 0)
            Option("--threads", Math.Clamp(AppSettings.Current.WhisperThreads, 1, 64).ToString(CultureInfo.InvariantCulture));

        // Device: whisper uses its GPU backend unless --no-gpu; VAD runs on the same device. Affects speed only.
        var plan = onProcessor
            ? new SpeechPlan(AppSettings.SpeechOnCpu, true, "On the processor: whisper.exe fell over on the graphics card, so the audio was transcribed again.")
            : PlanSpeech();
        if (plan.NoGpu && help.Contains("--no-gpu", StringComparison.Ordinal))
            arguments.Add("--no-gpu");
        AppLog.Write($"Auto-Captions: {plan.Why}");
        var fellOver = false;
        if (useVad)
        {
            if (set.VadThreshold != standard.VadThreshold)
                Option("--vad-threshold", Number(set.VadThreshold));
            if (set.VadMinSpeechMs != standard.VadMinSpeechMs)
                Option("--vad-min-speech-duration-ms", set.VadMinSpeechMs.ToString(CultureInfo.InvariantCulture));
            if (set.VadMinSilenceMs != standard.VadMinSilenceMs)
                Option("--vad-min-silence-duration-ms", set.VadMinSilenceMs.ToString(CultureInfo.InvariantCulture));
            if (set.VadSpeechPadMs != standard.VadSpeechPadMs)
                Option("--vad-speech-pad-ms", set.VadSpeechPadMs.ToString(CultureInfo.InvariantCulture));
        }

        var outputBase = Path.Combine(Path.GetDirectoryName(wavPath)!, Path.GetFileNameWithoutExtension(wavPath));
        var jsonPath = outputBase + ".json";
        var lastLines = new Queue<string>();
        var speech = new List<SpeechStretch>();

        void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            // "whisper_print_progress_callback: progress =  35%"
            if (ProgressRegex().Match(line) is { Success: true } percent)
            {
                progress?.Report(Math.Clamp(int.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100));
                return;
            }

            // Log the device reported by GPU-capable builds (adapter name or none found).
            if (said < 6 && (line.Contains("gpu", StringComparison.OrdinalIgnoreCase) || line.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
                             || line.Contains("cuda", StringComparison.OrdinalIgnoreCase) || line.Contains("system_info", StringComparison.OrdinalIgnoreCase)))
            {
                said++;
                AppLog.Write($"whisper: {line.Trim()}");
            }

            if (useVad && VadSegmentRegex().Match(line) is { Success: true } stretch)
            {
                speech.Add(new SpeechStretch(Seconds(stretch.Groups[1]), Seconds(stretch.Groups[2]), Seconds(stretch.Groups[3]), Seconds(stretch.Groups[4])));
                return;
            }

            lastLines.Enqueue(line.Trim());
            if (lastLines.Count > 6)
                lastLines.Dequeue();
        }

        try
        {
            arguments[6] = outputBase;
            var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.WhisperPath)
                .WithArguments(arguments)
                .WithWorkingDirectory(Path.GetDirectoryName(DependencyUpdater.WhisperPath) ?? DependencyUpdater.ToolFolder(DependencyUpdater.Whisper))
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.Null)
                .WithStandardErrorPipe(ProcessPipes.Lines(OnLine)),
                cancellationToken);

            // GPU crash (NTSTATUS exit): retry on CPU; Auto then stays on CPU for this build/driver (AppSettings.WhisperGpuFailedOn).
            fellOver = result.ExitCode < 0 && !plan.NoGpu && plan.Device == AppSettings.SpeechOnGpu;
            if (!fellOver && (result.ExitCode != 0 || !File.Exists(jsonPath)))
                throw new InvalidOperationException($"whisper.exe exited with code {result.ExitCode}: {string.Join(" | ", lastLines.TakeLast(3))}");

            if (fellOver)
            {
                AppLog.Write(FormattableString.Invariant($"whisper.exe fell over on the graphics card (exit code 0x{result.ExitCode:X8}): {string.Join(" | ", lastLines.TakeLast(3))}"));
            }
            else if (!useVad)
            {
                return ParseWhisperJson(File.ReadAllText(jsonPath));
            }
            else if (speech.Count > 0)
            {
                // Without VAD segment info, compacted word times cannot be mapped back; mistimed captions are worse than none.
                return ParseWhisperJson(File.ReadAllText(jsonPath), (from, to) => ToOriginalWord(speech, from, to));
            }
            else
            {
                AppLog.Write("whisper.exe did not say where the speech was, so the audio was transcribed again without silence detection.");
            }
        }
        finally
        {
            File.Delete(jsonPath);
        }

        if (fellOver)
        {
            // Blacklist the GPU only after the CPU succeeds; a CPU crash too implicates the audio or model.
            var words = await RunWhisperAsync(wavPath, modelPath, language, prompt, translate, vadModelPath, cancellationToken, progress, options, onProcessor: true);
            NoteGpuFellOver();
            return words;
        }

        return await RunWhisperAsync(wavPath, modelPath, language, prompt, translate, vadModelPath: null, cancellationToken, progress, options, onProcessor);
    }

    // ----- Transcription device selection -----

    /// <summary>
    /// Transcription device: <c>Device</c> "npu", "gpu" or "cpu" (AppSettings SpeechOn names), whether to pass --no-gpu, and a
    /// one-sentence reason (log, Settings, Auto-Captions window).
    /// </summary>
    public readonly record struct SpeechPlan(string Device, bool NoGpu, string Why);

    /// <summary>
    /// Resolves the device from <see cref="AppSettings.CaptionAccelerator"/> and installed builds. Auto: NPU if an NPU build exists
    /// (<see cref="DependencyUpdater.NpuSpeechBuildInstalled"/>, currently never), else GPU if the build has a backend
    /// (<see cref="DependencyUpdater.WhisperGpuBackend"/>) that has not crashed here, else CPU. NPU without a build falls back to Auto.
    /// --no-gpu is passed only when the build has an external GPU backend or CPU was chosen. Pure (hardware-tier golden).
    /// </summary>
    public static SpeechPlan ChooseSpeechAccelerator(string choice, bool npuBuild, string gpuBackend, bool gpuFellOverHere)
    {
        var gpu = gpuBackend.Length > 0;
        if (choice == AppSettings.SpeechOnCpu)
            return new(AppSettings.SpeechOnCpu, true, "On the processor, as chosen.");
        if (choice == AppSettings.SpeechOnGpu)
        {
            return gpu
                ? new(AppSettings.SpeechOnGpu, false, $"On the graphics card ({gpuBackend}), as chosen.")
                : new(AppSettings.SpeechOnCpu, false, "On the processor: the whisper.cpp build in use shows no graphics back end (it decides for itself).");
        }

        if (npuBuild)
            return new(AppSettings.SpeechOnNpu, true, choice == AppSettings.SpeechOnNpu ? "On the NPU, as chosen." : "On the NPU (Auto).");

        // Auto, or the NPU chosen with no build for it: the graphics card, else the processor.
        var npu = choice == AppSettings.SpeechOnNpu ? " (no speech build for the NPU is installed yet)" : " (Auto)";
        return gpu && !gpuFellOverHere ? new(AppSettings.SpeechOnGpu, false, $"On the graphics card, whisper's {gpuBackend} back end{npu}.")
            : gpu ? new(AppSettings.SpeechOnCpu, true, $"On the processor{npu}: whisper's {gpuBackend} back end fell over on this computer before.")
            : new(AppSettings.SpeechOnCpu, false, $"On the processor{npu}: the whisper.cpp build in use has no graphics back end.");
    }

    /// <summary>
    /// Device a transcription would use now for <paramref name="choice"/> (current setting when null). File I/O and adapter enumeration:
    /// off the UI thread.
    /// </summary>
    public static SpeechPlan PlanSpeech(string? choice = null) =>
        ChooseSpeechAccelerator(choice ?? AppSettings.Current.CaptionAccelerator, DependencyUpdater.NpuSpeechBuildInstalled, DependencyUpdater.WhisperGpuBackend,
            AppSettings.Current.WhisperGpuFailedOn.Length > 0 && AppSettings.Current.WhisperGpuFailedOn == GpuStamp());

    // The whisper.exe in use and the cards and drivers there are: what a fall on the graphics card is remembered against.
    private static string GpuStamp()
    {
        var path = DependencyUpdater.WhisperPath;
        long written;
        try
        {
            written = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            written = 0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{path}|{written}|{GpuAdapters.Fingerprint}");
    }

    /// <summary>Persists a GPU crash for this build/driver so Auto uses the CPU.</summary>
    private static void NoteGpuFellOver()
    {
        var stamp = GpuStamp();
        AppSettings.Update(settings => settings.WhisperGpuFailedOn = stamp);
    }

    private static double Seconds(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps a word's compacted start/end to original audio time. A start in an inter-segment gap snaps to the later segment's start;
    /// an end snaps to the earlier segment's end (no word spans a pause). Ends are capped at the end of the start's segment.
    /// </summary>
    private static (double Start, double End) ToOriginalWord(List<SpeechStretch> speech, double from, double to)
    {
        var (start, stretch) = ToOriginalTime(speech, from, isEnd: false);
        var (end, _) = ToOriginalTime(speech, to, isEnd: true);
        return (start, Math.Min(Math.Max(end, start), speech[stretch].OriginalEnd));
    }

    /// <summary>
    /// Builds the compacted-to-original word mapper from VAD segments (original start/end, compacted start/end) for ParseWhisperJson.
    /// Public for goldens.
    /// </summary>
    public static Func<double, double, (double Start, double End)> SpeechWordMap(
        IReadOnlyList<(double OriginalStart, double OriginalEnd, double Start, double End)> stretches)
    {
        var speech = stretches.Select(s => new SpeechStretch(s.OriginalStart, s.OriginalEnd, s.Start, s.End)).ToList();
        return (from, to) => ToOriginalWord(speech, from, to);
    }

    /// <summary>Maps a compacted time to original time and returns its segment.</summary>
    /// <param name="isEnd">Word end: in a gap, resolve to the earlier segment's end rather than the later one's start.</param>
    private static (double Time, int Stretch) ToOriginalTime(List<SpeechStretch> speech, double time, bool isEnd)
    {
        for (var i = 0; i < speech.Count; i++)
        {
            var stretch = speech[i];
            if (time < stretch.Start)
                return isEnd && i > 0 ? (speech[i - 1].OriginalEnd, i - 1) : (stretch.OriginalStart, i);
            if (time <= stretch.End)
                return (stretch.OriginalStart + (time - stretch.Start), i);
        }

        return (speech[^1].OriginalEnd, speech.Count - 1);
    }

    /// <summary>
    /// Maps a time on the concatenated-ranges clock (BuildExtractCommand output) to source time; clamps past the last range.
    /// </summary>
    public static double JoinedToSource(IReadOnlyList<(double Start, double End)> ranges, double time) =>
        ranges.Count == 0 ? time : ranges[JoinedRange(ranges, time, out var into)].Start + into;

    /// <summary>Maps words from the concatenated <paramref name="ranges"/> clock to source time, capping each end at its range end.</summary>
    public static List<CaptionWord> JoinedToSource(IReadOnlyList<(double Start, double End)> ranges, IReadOnlyList<CaptionWord> words)
    {
        var placed = new List<CaptionWord>(words.Count);
        foreach (var word in words)
        {
            if (ranges.Count == 0)
            {
                placed.Add(word);
                continue;
            }

            var range = ranges[JoinedRange(ranges, word.Start, out var into)];
            var start = range.Start + into;
            var end = Math.Min(start + Math.Max(word.End - word.Start, 0), range.End);
            placed.Add(word with { Start = start, End = Math.Max(end, start) });
        }

        return placed;
    }

    /// <summary>Which stretch a moment of the joined clock is in, and how far into it.</summary>
    private static int JoinedRange(IReadOnlyList<(double Start, double End)> ranges, double time, out double into)
    {
        var before = 0.0;
        for (var i = 0; i < ranges.Count; i++)
        {
            var length = Math.Max(ranges[i].End - ranges[i].Start, 0);
            if (time < before + length || i == ranges.Count - 1)
            {
                into = Math.Clamp(time - before, 0, length);
                return i;
            }

            before += length;
        }

        into = 0;
        return 0;
    }

    /// <summary>
    /// Max duration for a segment's last word (whisper extends it to the segment end, into silence): ~0.25 s for short words, scaled by
    /// length, capped at 1.5 s.
    /// </summary>
    public static double LongestLastWord(string word) => Math.Clamp(0.25 + 0.08 * word.Count(char.IsLetterOrDigit), 0.35, 1.5);

    // What the whisper.exe in use says it understands (its --help), by the program asked: older builds know fewer options.
    private static (string Program, string Help)? _help;

    /// <summary>True when the active whisper.exe supports VAD; probed once.</summary>
    public static async Task<bool> SupportsVadAsync(CancellationToken cancellationToken) =>
        (await HelpAsync(cancellationToken)).Contains("--vad-model", StringComparison.Ordinal);

    /// <summary>The options the whisper.exe in use lists, asked of it once; empty when it could not be asked.</summary>
    private static async Task<string> HelpAsync(CancellationToken cancellationToken)
    {
        var path = DependencyUpdater.WhisperPath;
        if (!File.Exists(path))
            return "";

        var program = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (_help is { } known && known.Program == program)
            return known.Help;

        try
        {
            var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(path)
                .WithArguments(["--help"])
                .WithWorkingDirectory(Path.GetDirectoryName(path) ?? DependencyUpdater.ToolFolder(DependencyUpdater.Whisper))
                .WithValidation(CommandResultValidation.None), cancellationToken);
            var help = result.StandardOutput + result.StandardError;
            _help = (program, help);
            return help;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Reassembles whisper tokens into words: a leading space starts a new word; punctuation attaches to the previous word.
    /// </summary>
    /// <param name="mapWord">
    /// Maps compacted start/end to audio time when VAD was used (null otherwise); takes both so the end can be bounded by the start's segment.
    /// </param>
    public static List<CaptionWord> ParseWhisperJson(string json, Func<double, double, (double Start, double End)>? mapWord = null)
    {
        var words = new List<CaptionWord>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("transcription", out var segments))
            return words;

        var text = new StringBuilder();
        double start = 0, end = 0;

        // endsSegment: last word of a whisper segment, whose end whisper stretches to the segment end.
        void Finish(bool endsSegment = false)
        {
            var word = text.ToString().Trim();
            text.Clear();

            // Notes such as [BLANK_AUDIO] or (music) are not speech.
            if (word.Length == 0 || word[0] is '[' or '(' || !word.Any(char.IsLetterOrDigit))
                return;

            var (said, done) = mapWord is null ? (start, end) : mapWord(start, end);
            if (endsSegment)
                done = Math.Min(done, said + LongestLastWord(word));

            // Never before the word that precedes it, and never too brief to be seen.
            var from = Math.Max(said, words.Count > 0 ? words[^1].Start : 0);
            words.Add(new CaptionWord(word, from, Math.Max(done, from + 0.05)));
        }

        foreach (var segment in segments.EnumerateArray())
        {
            if (!segment.TryGetProperty("tokens", out var tokens))
                continue;

            foreach (var token in tokens.EnumerateArray())
            {
                var piece = token.GetProperty("text").GetString() ?? "";

                // Markers of whisper's own, such as [_BEG_] and [_TT_150].
                if (piece.StartsWith("[_", StringComparison.Ordinal) || !token.TryGetProperty("offsets", out var offsets))
                    continue;

                var from = offsets.GetProperty("from").GetDouble() / 1000;
                var to = offsets.GetProperty("to").GetDouble() / 1000;

                if (piece.StartsWith(' ') || text.Length == 0)
                {
                    Finish();
                    start = from;
                }

                text.Append(piece);
                end = to;
            }

            // A word never runs on from one segment into the next.
            Finish(endsSegment: true);
        }

        return words;
    }

    /// <summary>
    /// Writes the words as an ASS subtitle file sized for a frame of the given dimensions.
    /// Karaoke Sweep: one line per caption, the highlight colour sweeping across each word as it is spoken (\kf).
    /// TikTok Pop: one line per word, showing the whole caption with that word in the highlight colour and enlarged.
    /// </summary>
    /// <param name="width">Width of the canvas the captions are drawn on: the caption box.</param>
    /// <param name="scale">What the style's sizes, given for a 1080-pixel frame, are multiplied by.</param>
    /// <param name="timeOffset">Added to every time, for a video whose own clock does not start at zero.</param>
    public static void WriteAss(
        IReadOnlyList<CaptionWord> words, CaptionStyle style, int width, int height, double scale, string path, double timeOffset = 0,
        IReadOnlyList<List<CaptionWord>>? lines = null)
    {
        var fontSize = Math.Max((int)Math.Round(Math.Clamp(style.FontSize, 8, 400) * scale), 6);
        var outline = Math.Round(Math.Clamp(style.OutlineThickness, 0, 40) * scale, 1);
        var isKaraoke = style.Animation != CaptionStyle.TikTokPop;

        var primary = AssColor(style.PrimaryColor, "FFFFFF");
        var highlight = AssColor(style.HighlightColor, "FFE600");

        // Karaoke lines sweep from secondary to primary colour, so the highlight is the primary colour.
        var (first, second) = isKaraoke ? (highlight, primary) : (primary, highlight);

        var ass = new StringBuilder();
        ass.AppendLine("[Script Info]");
        ass.AppendLine("; Generated by HandPeg from a whisper.cpp transcription");
        ass.AppendLine("ScriptType: v4.00+");
        ass.AppendLine(string.Create(CultureInfo.InvariantCulture, $"PlayResX: {width}"));
        ass.AppendLine(string.Create(CultureInfo.InvariantCulture, $"PlayResY: {height}"));
        ass.AppendLine("WrapStyle: 2");
        ass.AppendLine("ScaledBorderAndShadow: yes");
        ass.AppendLine();
        ass.AppendLine("[V4+ Styles]");
        ass.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, "
                       + "StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");

        // Bottom-centre with margins, opaque (ASS alpha 00), no own shadow (layer opacity and drop shadow apply at composite).
        var (marginLeft, marginRight, marginBottom) = (width / 40, width / 40, height / 12);
        var font = string.IsNullOrWhiteSpace(style.FontName) ? "Arial" : style.FontName.Replace(",", " ").Trim();
        ass.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Style: Caption,{font},{fontSize},&H00{first},&H00{second},&H00{AssColor(style.OutlineColor, "000000")},&H80000000,"
            + $"-1,0,0,0,100,100,0,0,1,{outline},0,2,{marginLeft},{marginRight},{marginBottom},1"));
        ass.AppendLine();
        ass.AppendLine("[Events]");
        ass.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // Lines that are given are kept as they are (the cues of a subtitle track); words alone are grouped here.
        IReadOnlyList<List<CaptionWord>> captions = lines ?? Lines(words, style);
        for (var c = 0; c < captions.Count; c++)
        {
            var caption = captions[c];
            var captionStart = caption[0].Start;

            // End at the last word unless the next caption follows within the gap threshold; then hold until replaced (no flicker).
            var captionEnd = caption[^1].End;
            if (c + 1 < captions.Count)
            {
                var next = captions[c + 1][0].Start;
                captionEnd = next - captionEnd <= SilentGapSeconds ? next : captionEnd;
            }

            captionEnd = Math.Max(captionEnd, captionStart + 0.1);

            if (isKaraoke)
            {
                // Each word's sweep lasts until the next word starts, so the timing never drifts across a line.
                var text = new StringBuilder();
                for (var w = 0; w < caption.Count; w++)
                {
                    var until = w + 1 < caption.Count ? caption[w + 1].Start : captionEnd;
                    var centiseconds = Math.Max((int)Math.Round((until - caption[w].Start) * 100), 1);
                    text.Append(CultureInfo.InvariantCulture, $"{{\\kf{centiseconds}}}{Escape(caption[w].Text)}{(w + 1 < caption.Count ? " " : "")}");
                }

                ass.AppendLine(Event(captionStart, captionEnd, text.ToString(), timeOffset));
            }
            else
            {
                for (var w = 0; w < caption.Count; w++)
                {
                    var until = w + 1 < caption.Count ? caption[w + 1].Start : captionEnd;
                    var text = string.Join(" ", caption.Select((word, index) => index == w
                        ? $"{{\\c&H{second}&\\fscx115\\fscy115}}{Escape(word.Text)}{{\\r}}"
                        : Escape(word.Text)));
                    ass.AppendLine(Event(caption[w].Start, until, text, timeOffset));
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // UTF-8 with a byte order mark, which is how libass recognises the encoding.
        File.WriteAllText(path, ass.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteWordsBeside(path, captions, style, scale, timeOffset);
    }

    // ----- Word sidecar beside the caption file -----

    /// <summary>
    /// Caption source data for GPU rendering (TextRasterizer.SetCaptions): per-line words on the file clock, style and text scale.
    /// Written beside each caption file (<see cref="WordsPath"/>) so GPU exports render identical captions.
    /// </summary>
    public sealed record CaptionWords(List<List<CaptionWord>> Lines, CaptionStyle Style, double TextScale);

    /// <summary>Where the words of a caption file are written: beside it.</summary>
    public static string WordsPath(string assPath) => assPath + ".words.json";

    private static void WriteWordsBeside(string assPath, IReadOnlyList<List<CaptionWord>> lines, CaptionStyle style, double scale, double timeOffset)
    {
        var moved = lines.Select(line => line.Select(w => w with { Start = w.Start + timeOffset, End = w.End + timeOffset }).ToList()).ToList();
        try
        {
            File.WriteAllText(WordsPath(assPath), JsonSerializer.Serialize(new CaptionWords(moved, style.Clone(), scale)), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without it a GPU export leaves the captions to FFmpeg's libass, as before.
            AppLog.Write("The caption file's words could not be written beside it", ex);
        }
    }

    /// <summary>
    /// Reads the <see cref="CaptionWords"/> sidecar; null when missing, older than the caption file, or unreadable.
    /// </summary>
    public static CaptionWords? ReadWords(string assPath)
    {
        try
        {
            var words = WordsPath(assPath);
            if (string.IsNullOrWhiteSpace(assPath) || !File.Exists(assPath) || !File.Exists(words)
                || File.GetLastWriteTimeUtc(words) < File.GetLastWriteTimeUtc(assPath))
                return null;
            return JsonSerializer.Deserialize<CaptionWords>(File.ReadAllText(words)) is { Lines: not null, Style: not null } read ? read : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AppLog.Write("The caption file's words could not be read", ex);
            return null;
        }
    }

    /// <summary>The words as the captions they are shown as, in a style: as many to a line as the style allows.</summary>
    public static List<List<CaptionWord>> Lines(IReadOnlyList<CaptionWord> words, CaptionStyle style) => Chunk(words, Math.Clamp(style.MaxWordsPerLine, 2, 5));

    /// <summary>
    /// Groups words into captions with a max word count, breaking after a silent gap (<see cref="SilentGapSeconds"/>) or sentence end.
    /// </summary>
    private static List<List<CaptionWord>> Chunk(IReadOnlyList<CaptionWord> words, int maxWords)
    {
        var captions = new List<List<CaptionWord>>();
        foreach (var word in words)
        {
            var current = captions.Count > 0 ? captions[^1] : null;
            var startsNew = current is null
                            || current.Count >= maxWords
                            || word.Start - current[^1].End > SilentGapSeconds
                            || current[^1].Text[^1] is '.' or '!' or '?';
            if (startsNew)
                captions.Add(current = []);
            current!.Add(word);
        }

        return captions;
    }

    private static string Event(double start, double end, string text, double offset) =>
        $"Dialogue: 0,{Time(start + offset)},{Time(end + offset)},Caption,,0,0,0,,{text}";

    /// <summary>ASS times are H:MM:SS.cc, written the same whatever the culture.</summary>
    private static string Time(double seconds)
    {
        var centiseconds = (long)Math.Round(Math.Max(seconds, 0) * 100);
        return string.Create(CultureInfo.InvariantCulture,
            $"{centiseconds / 360000}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}");
    }

    /// <summary>#RRGGBB as ASS writes colours: BBGGRR. Anything unreadable becomes the fallback.</summary>
    private static string AssColor(string color, string fallback)
    {
        var hex = (color ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            hex = fallback;
        return (hex[4..6] + hex[2..4] + hex[0..2]).ToUpperInvariant();
    }

    // Braces would be read as the start of an override block.
    private static string Escape(string text) => text.Replace("{", "(").Replace("}", ")").Replace("\\", "/");

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
