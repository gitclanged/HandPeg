using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CliWrap;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>One spoken word and when it is said, in seconds from the start of the analysed audio.</summary>
public sealed record CaptionWord(string Text, double Start, double End);

/// <summary>
/// The auto-captions pipeline, step by step: cut the audio that will be heard into a small speech-quality
/// file, have whisper.cpp find the words and their times, and write them as an animated ASS subtitle file
/// for FFmpeg to draw.
/// </summary>
public static class CaptionGenerator
{
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
    public static async Task<List<CaptionWord>> TranscribeAsync(
        string wavPath, string modelPath, string language, string prompt, bool translate, CancellationToken cancellationToken)
    {
        // -ojf: output JSON, full. -of: where to write it (whisper adds .json). -np: no progress chatter.
        var arguments = new List<string> { "-m", modelPath, "-f", wavPath, "-ojf", "-of", "", "-np" };
        if (!string.IsNullOrWhiteSpace(language))
            arguments.AddRange(["--language", language.Trim()]);
        if (!string.IsNullOrWhiteSpace(prompt))
            arguments.AddRange(["--prompt", prompt]);
        if (translate)
            arguments.Add("--translate");

        var outputBase = Path.Combine(Path.GetDirectoryName(wavPath)!, Path.GetFileNameWithoutExtension(wavPath));
        var jsonPath = outputBase + ".json";
        var lastLines = new Queue<string>();

        void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

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

            return ParseWhisperJson(File.ReadAllText(jsonPath));
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    /// <summary>
    /// Puts whisper's tokens back together into words. A token is a piece of a word; one that starts with a
    /// space starts a new word, and punctuation, having no space, stays on the word before it.
    /// </summary>
    public static List<CaptionWord> ParseWhisperJson(string json)
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
    public static void WriteAss(
        IReadOnlyList<CaptionWord> words, CaptionStyle style, int width, int height, double scale, string path, double timeOffset = 0)
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
        ass.AppendLine($"PlayResX: {width}");
        ass.AppendLine($"PlayResY: {height}");
        ass.AppendLine("WrapStyle: 2");
        ass.AppendLine("ScaledBorderAndShadow: yes");
        ass.AppendLine();
        ass.AppendLine("[V4+ Styles]");
        ass.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, "
                       + "StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");

        // Centred at the bottom of the box, a little in from its edges.
        var font = string.IsNullOrWhiteSpace(style.FontName) ? "Arial" : style.FontName.Replace(",", " ").Trim();
        ass.AppendLine($"Style: Caption,{font},{fontSize},&H00{first},&H00{second},&H00{AssColor(style.OutlineColor, "000000")},&H80000000,"
                       + $"-1,0,0,0,100,100,0,0,1,{outline.ToString(CultureInfo.InvariantCulture)},0,2,{width / 40},{width / 40},{height / 12},1");
        ass.AppendLine();
        ass.AppendLine("[Events]");
        ass.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var captions = Chunk(words, Math.Clamp(style.MaxWordsPerLine, 2, 5));
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
