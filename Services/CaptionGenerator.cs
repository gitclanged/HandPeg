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
/// Where the caption box sits on the output frame, for a caption file written for the whole frame instead of
/// for the box: the one the player draws by itself over the picture. With it go the two parts of the caption
/// layer's look that a subtitle style can carry, its opacity and its drop shadow.
/// </summary>
/// <param name="Opacity">0 to 1.</param>
/// <param name="ShadowOffset">How far the shadow falls to the right and down, in output pixels; 0 for none.</param>
/// <param name="ShadowOpacity">0 to 1.</param>
public sealed record CaptionFramePlacement(int X, int Y, int FrameWidth, int FrameHeight, double Opacity, double ShadowOffset, double ShadowOpacity);

/// <summary>
/// How whisper.cpp is asked to decode, and how its silence detection listens: what the Auto-Captions tab of
/// the settings sets. The defaults are whisper's own, and an option left at its default is not written into
/// the command at all, so settings nobody touched run whisper exactly as it ran before there were any.
/// </summary>
/// <param name="BeamSize">How many candidate sentences are followed at once (--beam-size).</param>
/// <param name="BestOf">How many candidates are kept when sampling (--best-of).</param>
/// <param name="Temperature">0 to 1: how freely words are chosen (--temperature).</param>
/// <param name="TemperatureFallback">Try again at a higher temperature when a stretch fails to decode; off is --no-fallback.</param>
/// <param name="VadThreshold">0 to 1: how sure the detector has to be that a sound is speech (--vad-threshold).</param>
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
/// A transcription still to be done, and everything it takes to do it: what to listen to, how whisper is
/// asked to listen (the model, the language, and the decoding and silence settings as they were when this
/// was made), and how the words are then written. A queued job carries one, so that its captions are made
/// when the job runs, from what was set when it was queued, whatever the window shows by then.
/// </summary>
/// <param name="Ranges">The stretches of the audio that are heard, joined end to end; none for all of it.</param>
/// <param name="UseVad">Skip silence, when the detection model is installed and whisper.exe knows how.</param>
/// <param name="Width">The caption box on the output frame, in pixels: the canvas the file is written for.</param>
/// <param name="TextScale">What the style's sizes, given for a 1080-pixel frame, are multiplied by.</param>
/// <param name="AssPath">Where the caption file is written: the file the job's command draws.</param>
public sealed record CaptionJob(
    string AudioPath, int TrackIndex, IReadOnlyList<(double Start, double End)> Ranges,
    string ModelPath, string Language, string Prompt, bool Translate, bool UseVad, WhisperOptions Options,
    CaptionStyle Style, int Width, int Height, double TextScale, double TimeOffset, string AssPath);

/// <summary>
/// The auto-captions pipeline, step by step: cut the audio that will be heard into a small speech-quality
/// file, have whisper.cpp find the words and their times, and write them as an animated ASS subtitle file
/// for FFmpeg to draw.
/// </summary>
public static partial class CaptionGenerator
{
    // With voice activity detection, whisper listens to the stretches of speech joined end to end, and says
    // in its log where each of them was in the audio it was given:
    //   whisper_vad: vad_segment_info: orig_start: 18.08, orig_end: 20.83, vad_start: 4.74, vad_end: 7.49
    [GeneratedRegex(@"vad_segment_info: orig_start: ([\d.]+), orig_end: ([\d.]+), vad_start: ([\d.]+), vad_end: ([\d.]+)")]
    private static partial Regex VadSegmentRegex();

    [GeneratedRegex(@"progress\s*=\s*(\d+)\s*%")]
    private static partial Regex ProgressRegex();

    /// <summary>One stretch of speech: where it is in the audio, and where it is on the clock whisper counts by when it skips silence.</summary>
    private readonly record struct SpeechStretch(double OriginalStart, double OriginalEnd, double Start, double End);

    // A silence this long between two words starts a new caption, however few words the last one has.
    private const double PauseSeconds = 0.7;

    // How long a caption stays after its last word, when nothing follows at once.
    private const double HoldSeconds = 0.2;

    // Sizes in the style are given for a frame this many pixels on its shorter side.
    private const double ReferenceSide = 1080;

    /// <summary>
    /// The FFmpeg command that writes the given stretches of an audio track, joined end to end, as the
    /// 16 kHz mono wave file whisper.cpp reads. No ranges means the whole track.
    /// </summary>
    /// <param name="trackIndex">Which of the file's audio tracks, counted from 0.</param>
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
    /// Runs whisper.cpp on a wave file and returns the words it heard. Its full JSON output is asked for,
    /// which is what carries a time for every token.
    /// </summary>
    /// <param name="language">The spoken language as whisper names it (en, es...), or auto.</param>
    /// <param name="prompt">Words and names to expect; blank for none.</param>
    /// <param name="translate">Write English whatever is spoken.</param>
    /// <param name="vadModelPath">
    /// The voice activity detection model, or null to listen to everything. With it, whisper is given only
    /// the stretches that have speech in them, which stops it writing words into the silences.
    /// </param>
    public static async Task<List<CaptionWord>> TranscribeAsync(
        string wavPath, string modelPath, string language, string prompt, bool translate, string? vadModelPath, CancellationToken cancellationToken,
        IProgress<double>? progress = null, WhisperOptions? options = null)
    {
        var useVad = !string.IsNullOrWhiteSpace(vadModelPath);
        var said = 0;

        // -ojf: output JSON, full. -of: where to write it (whisper adds .json). -np: no progress chatter;
        // left out with voice activity detection, whose log is what says where the speech was.
        // -pp: say how far along it is, which is what the progress bar shows. Its log is read as well: it says
        // which processor or graphics card whisper is running on.
        var arguments = new List<string> { "-m", modelPath, "-f", wavPath, "-ojf", "-of", "", "-pp" };
        if (!string.IsNullOrWhiteSpace(language))
            arguments.AddRange(["--language", language.Trim()]);
        if (!string.IsNullOrWhiteSpace(prompt))
            arguments.AddRange(["--prompt", prompt]);
        if (translate)
            arguments.Add("--translate");
        if (useVad)
            arguments.AddRange(["--vad", "--vad-model", vadModelPath!]);

        // Only what was moved off whisper's own default is said, and only to a whisper.exe that knows the option:
        // an older build of the user's own is run as before rather than refused.
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

            // What it runs on, for the log: a build with graphics card support says which card it found, or that it found none.
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

            if (result.ExitCode != 0 || !File.Exists(jsonPath))
                throw new InvalidOperationException($"whisper.exe exited with code {result.ExitCode}: {string.Join(" | ", lastLines.TakeLast(3))}");

            if (!useVad)
                return ParseWhisperJson(File.ReadAllText(jsonPath));

            // The word times are on the shortened clock; without knowing where the speech was they cannot
            // be put back, and captions at the wrong moments are worse than captions in a silence.
            if (speech.Count > 0)
                return ParseWhisperJson(File.ReadAllText(jsonPath), time => ToOriginalTime(speech, time));

            AppLog.Write("whisper.exe did not say where the speech was, so the audio was transcribed again without silence detection.");
        }
        finally
        {
            File.Delete(jsonPath);
        }

        return await TranscribeAsync(wavPath, modelPath, language, prompt, translate, vadModelPath: null, cancellationToken, progress, options);
    }

    private static double Seconds(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// A time on the clock whisper counts by when it skips silence, as a time in the audio it was given. A time
    /// that falls between two stretches of speech (in the short gap whisper leaves there) is the start of the later one.
    /// </summary>
    private static double ToOriginalTime(List<SpeechStretch> speech, double time)
    {
        foreach (var stretch in speech)
        {
            if (time < stretch.Start)
                return stretch.OriginalStart;
            if (time <= stretch.End)
                return stretch.OriginalStart + (time - stretch.Start);
        }

        return speech[^1].OriginalEnd;
    }

    // What the whisper.exe in use says it understands (its --help), by the program asked: older builds know fewer options.
    private static (string Program, string Help)? _help;

    /// <summary>Whether the whisper.exe in use can skip silence with a voice activity detection model. Asked of it once.</summary>
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
    /// Puts whisper's tokens back together into words. A token is a piece of a word; one that starts with a
    /// space starts a new word, and punctuation, having no space, stays on the word before it.
    /// </summary>
    /// <param name="mapTime">Turns whisper's token times into times in the audio, when the two differ; null when they are the same.</param>
    public static List<CaptionWord> ParseWhisperJson(string json, Func<double, double>? mapTime = null)
    {
        var words = new List<CaptionWord>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("transcription", out var segments))
            return words;

        var text = new StringBuilder();
        double start = 0, end = 0;

        void Finish()
        {
            var word = text.ToString().Trim();
            text.Clear();

            // Notes such as [BLANK_AUDIO] or (music) are not speech.
            if (word.Length == 0 || word[0] is '[' or '(' || !word.Any(char.IsLetterOrDigit))
                return;

            // Never before the word that precedes it, and never too brief to be seen.
            var from = Math.Max(start, words.Count > 0 ? words[^1].Start : 0);
            words.Add(new CaptionWord(word, from, Math.Max(end, from + 0.05)));
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
                if (mapTime is not null)
                    (from, to) = (mapTime(from), mapTime(to));

                if (piece.StartsWith(' ') || text.Length == 0)
                {
                    Finish();
                    start = from;
                }

                text.Append(piece);
                end = to;
            }

            // A word never runs on from one segment into the next.
            Finish();
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
    /// <param name="frame">
    /// Null for a file whose canvas is the caption box, which FFmpeg draws and then lays on the frame. Given, the
    /// canvas is the whole frame and the margins put the words where the box is: a file a player can draw as it is.
    /// </param>
    public static void WriteAss(
        IReadOnlyList<CaptionWord> words, CaptionStyle style, int width, int height, double scale, string path, double timeOffset = 0,
        CaptionFramePlacement? frame = null, IReadOnlyList<List<CaptionWord>>? lines = null)
    {
        var fontSize = Math.Max((int)Math.Round(Math.Clamp(style.FontSize, 8, 400) * scale), 6);
        var outline = Math.Round(Math.Clamp(style.OutlineThickness, 0, 40) * scale, 1);
        var isKaraoke = style.Animation != CaptionStyle.TikTokPop;

        var primary = AssColor(style.PrimaryColor, "FFFFFF");
        var highlight = AssColor(style.HighlightColor, "FFE600");

        // In a karaoke line the words start in the secondary colour and turn into the primary one as they
        // are sung, so there the highlight is the primary colour.
        var (first, second) = isKaraoke ? (highlight, primary) : (primary, highlight);

        var ass = new StringBuilder();
        ass.AppendLine("[Script Info]");
        ass.AppendLine("; Generated by HandPeg from a whisper.cpp transcription");
        ass.AppendLine("ScriptType: v4.00+");
        ass.AppendLine($"PlayResX: {frame?.FrameWidth ?? width}");
        ass.AppendLine($"PlayResY: {frame?.FrameHeight ?? height}");
        ass.AppendLine("WrapStyle: 2");
        ass.AppendLine("ScaledBorderAndShadow: yes");
        ass.AppendLine();
        ass.AppendLine("[V4+ Styles]");
        ass.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, "
                       + "StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");

        // Centred at the bottom of the box, a little in from its edges. On a canvas that is the whole frame, the
        // margins also take up what lies between the box and the edges of the frame.
        var (marginLeft, marginRight, marginBottom) = (width / 40, width / 40, height / 12);
        if (frame is not null)
        {
            marginLeft += frame.X;
            marginRight += Math.Max(frame.FrameWidth - frame.X - width, 0);
            marginBottom += Math.Max(frame.FrameHeight - frame.Y - height, 0);
        }

        // ASS writes transparency, not opacity: 00 is solid.
        static string Alpha(double opacity) => ((int)Math.Round((1 - Math.Clamp(opacity, 0, 1)) * 255)).ToString("X2", CultureInfo.InvariantCulture);
        var alpha = Alpha(frame?.Opacity ?? 1);
        var shadow = Math.Round(Math.Max(frame?.ShadowOffset ?? 0, 0), 1);
        var back = frame is null ? "&H80000000" : $"&H{Alpha(frame.Opacity * frame.ShadowOpacity)}000000";

        var font = string.IsNullOrWhiteSpace(style.FontName) ? "Arial" : style.FontName.Replace(",", " ").Trim();
        ass.AppendLine($"Style: Caption,{font},{fontSize},&H{alpha}{first},&H{alpha}{second},&H{alpha}{AssColor(style.OutlineColor, "000000")},{back},"
                       + $"-1,0,0,0,100,100,0,0,1,{outline.ToString(CultureInfo.InvariantCulture)},{shadow.ToString(CultureInfo.InvariantCulture)},2,{marginLeft},{marginRight},{marginBottom},1");
        ass.AppendLine();
        ass.AppendLine("[Events]");
        ass.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // Lines that are given are kept as they are (the cues of a subtitle track); words alone are grouped here.
        IReadOnlyList<List<CaptionWord>> captions = lines ?? Lines(words, style);
        for (var c = 0; c < captions.Count; c++)
        {
            var caption = captions[c];
            var captionStart = caption[0].Start;

            // Held a moment after the last word, but cleared before the next caption begins.
            var captionEnd = caption[^1].End + HoldSeconds;
            if (c + 1 < captions.Count)
                captionEnd = Math.Min(captionEnd, captions[c + 1][0].Start);
            captionEnd = Math.Max(captionEnd, captionStart + 0.1);

            if (isKaraoke)
            {
                // Each word's sweep lasts until the next word starts, so the timing never drifts across a line.
                var text = new StringBuilder();
                for (var w = 0; w < caption.Count; w++)
                {
                    var until = w + 1 < caption.Count ? caption[w + 1].Start : captionEnd;
                    var centiseconds = Math.Max((int)Math.Round((until - caption[w].Start) * 100), 1);
                    text.Append($"{{\\kf{centiseconds}}}{Escape(caption[w].Text)}{(w + 1 < caption.Count ? " " : "")}");
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
    }

    /// <summary>The words as the captions they are shown as, in a style: as many to a line as the style allows.</summary>
    public static List<List<CaptionWord>> Lines(IReadOnlyList<CaptionWord> words, CaptionStyle style) => Chunk(words, Math.Clamp(style.MaxWordsPerLine, 2, 5));

    /// <summary>Groups the words into captions: so many at most, a new one after a pause or the end of a sentence.</summary>
    private static List<List<CaptionWord>> Chunk(IReadOnlyList<CaptionWord> words, int maxWords)
    {
        var captions = new List<List<CaptionWord>>();
        foreach (var word in words)
        {
            var current = captions.Count > 0 ? captions[^1] : null;
            var startsNew = current is null
                            || current.Count >= maxWords
                            || word.Start - current[^1].End > PauseSeconds
                            || current[^1].Text[^1] is '.' or '!' or '?';
            if (startsNew)
                captions.Add(current = []);
            current!.Add(word);
        }

        return captions;
    }

    private static string Event(double start, double end, string text, double offset) =>
        $"Dialogue: 0,{Time(start + offset)},{Time(end + offset)},Caption,,0,0,0,,{text}";

    /// <summary>ASS times are H:MM:SS.cc.</summary>
    private static string Time(double seconds)
    {
        var centiseconds = (long)Math.Round(Math.Max(seconds, 0) * 100);
        return $"{centiseconds / 360000}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}";
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
