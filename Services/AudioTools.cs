using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <summary>Pictures of sound: the waveform images shown on the timeline and in the mixer.</summary>
public static partial class Waveforms
{
    // The sound is read at this rate, which is plenty for its outline, and for every so many samples only the
    // lowest and the highest are kept: twenty pairs to the second. An hour of sound is 72,000 pairs, 576 KB.
    private const int SampleRate = 4000, SamplesPerPeak = 200;

    /// <summary>How many pairs of peaks there are to a second of sound.</summary>
    public const double PeaksPerSecond = (double)SampleRate / SamplesPerPeak;

    // The picture made for places that want a picture has at most this many columns, however long the sound.
    private const int MostColumns = 4000;

    // The peaks behind each picture, for what draws a waveform itself at the size it is shown (the time bars).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, WaveformData> Peaks = [];

    /// <summary>The peaks a waveform picture was drawn from; null for a picture that is not one of these.</summary>
    public static WaveformData? DataOf(ImageSource? picture) => picture is not null && Peaks.TryGetValue(picture, out var data) ? data : null;

    /// <summary>
    /// Reads the whole of one audio track as peaks, and returns a picture of them made of geometry: the
    /// outline of its loudness, as one filled shape. FFmpeg decodes the sound to plain samples, which are
    /// read as they come and reduced; no image file is made and no bitmap is held. The peaks stay with the
    /// picture (see <see cref="DataOf"/>). Null when it could not be made.
    /// </summary>
    /// <param name="trackIndexes">Which of the file's audio tracks, counted from 0. Several are mixed together (amix) first.</param>
    /// <param name="imagePath">Not used any more: the picture is not a file. Kept for the callers that name one.</param>
    /// <param name="width">The shape of the picture: it is this many units wide for every <paramref name="height"/> high.</param>
    /// <param name="color">A colour as FFmpeg would be given it: a name such as gray, or 0xRRGGBB.</param>
    public static async Task<ImageSource?> RenderAsync(
        string mediaPath, IReadOnlyList<int> trackIndexes, string imagePath, int width, int height, string color, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath) || !File.Exists(mediaPath) || trackIndexes.Count == 0)
            return null;

        var inputs = string.Concat(trackIndexes.Select(index => $"[0:a:{index}]"));
        var mix = trackIndexes.Count > 1 ? $"amix=inputs={trackIndexes.Count}:normalize=0," : "";

        var peaks = new PeakReader();
        var (lows, highs) = (peaks.Lows, peaks.Highs);
        var samples = PipeTarget.Create(async (stream, token) =>
        {
            // Borrowed, and given back: what FFmpeg writes passes through the same room for every waveform.
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768);
            try
            {
                var held = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(held), token)) > 0)
                {
                    var have = held + read;
                    var whole = have & ~1;
                    peaks.Add(buffer.AsSpan(0, whole));

                    // Half a sample at the end of what was read waits for its other half.
                    held = have - whole;
                    if (held > 0)
                        buffer[0] = buffer[whole];
                }

                peaks.Finish();
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        });

        var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-loglevel", "error", "-i", mediaPath, "-filter_complex",
                $"{inputs}{mix}aresample={SampleRate},aformat=sample_fmts=s16:channel_layouts=mono[wave]", "-map", "[wave]", "-f", "s16le", "-"])
            .WithStandardOutputPipe(samples)
            .WithValidation(CommandResultValidation.None),
            cancellationToken);

        if (result.ExitCode != 0 || highs.Count == 0)
            return null;

        var data = new WaveformData(lows.ToArray(), highs.ToArray(), BrushOf(color));
        var picture = Draw(data, Math.Max(width, 2), Math.Max(height, 2));
        Peaks.AddOrUpdate(picture, data);
        return picture;
    }

    /// <summary>Reduces 16-bit samples, as they arrive, to the lowest and highest of every so many.</summary>
    private sealed class PeakReader
    {
        public readonly List<float> Lows = new(4096), Highs = new(4096);
        private int _count, _low, _high;

        public void Add(ReadOnlySpan<byte> bytes)
        {
            // Read as the numbers they are (little-endian, as this processor is) rather than put together byte by byte.
            foreach (var sample in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes))
            {
                if (sample < _low)
                    _low = sample;
                if (sample > _high)
                    _high = sample;
                if (++_count == SamplesPerPeak)
                    Finish();
            }
        }

        public void Finish()
        {
            if (_count == 0)
                return;

            Lows.Add(_low / 32768f);
            Highs.Add(_high / 32768f);
            (_count, _low, _high) = (0, 0, 0);
        }
    }

    /// <summary>
    /// A stretch of a waveform as a waveform of its own, between two fractions of its length: what is left of
    /// a sound once it has been trimmed. Null for a picture that is not one of these waveforms.
    /// </summary>
    public static ImageSource? Cut(ImageSource? picture, double from, double to, int width, int height)
    {
        if (DataOf(picture) is not { Count: > 0 } whole)
            return null;

        var data = whole.Slice(from, to);
        var cut = Draw(data, Math.Max(width, 2), Math.Max(height, 2));
        Peaks.AddOrUpdate(cut, data);
        return cut;
    }

    /// <summary>The peaks as a picture: one shape, a column for every so many pairs.</summary>
    private static ImageSource Draw(WaveformData data, double width, double height)
    {
        var columns = Math.Min(data.Count, MostColumns);
        var (step, middle) = (width / columns, height / 2);
        var pool = System.Buffers.ArrayPool<double>.Shared;
        var (tops, bottoms) = (pool.Rent(columns), pool.Rent(columns));
        for (var c = 0; c < columns; c++)
        {
            var (low, high) = data.Range((double)c / columns, (double)(c + 1) / columns);

            // Silence is still a line: the sound is there, and quiet.
            tops[c] = middle - Math.Max(high * middle, height / 200);
            bottoms[c] = middle - Math.Min(low * middle, -height / 200);
        }

        var outline = new StreamGeometry();
        using (var path = outline.Open())
        {
            path.BeginFigure(new Point(0, tops[0]), isFilled: true, isClosed: true);
            for (var c = 0; c < columns; c++)
                path.LineTo(new Point((c + 0.5) * step, tops[c]), false, false);
            path.LineTo(new Point(width, tops[columns - 1]), false, false);
            path.LineTo(new Point(width, bottoms[columns - 1]), false, false);
            for (var c = columns - 1; c >= 0; c--)
                path.LineTo(new Point((c + 0.5) * step, bottoms[c]), false, false);
            path.LineTo(new Point(0, bottoms[0]), false, false);
        }

        outline.Freeze();
        pool.Return(tops);
        pool.Return(bottoms);

        // The see-through rectangle gives the picture its full size: without it, it would be as tall as the loudest moment only.
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, width, height))));
        drawing.Children.Add(new GeometryDrawing(data.Fill, null, outline));
        drawing.Freeze();

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static Brush BrushOf(string color)
    {
        try
        {
            var name = color.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "#" + color[2..] : color;
            if (ColorConverter.ConvertFromString(name) is Color parsed)
            {
                var brush = new SolidColorBrush(parsed);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
            // Gray, then.
        }

        return Brushes.Gray;
    }

    /// <summary>Reads a picture completely into memory and lets go of the file.</summary>
    public static ImageSource? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Records a microphone to a wave file with FFmpeg's DirectShow input. A recording can be paused and
/// carried on: each stretch between Record and Pause is a file of its own, and Stop joins them into one.
/// </summary>
public sealed partial class VoiceRecorder
{
    // FFmpeg lists devices as:  [dshow @ 0000...] "Microphone (Realtek Audio)" (audio)
    [GeneratedRegex("\"([^\"]+)\"\\s+\\(audio\\)")]
    private static partial Regex AudioDeviceRegex();

    /// <summary>How long FFmpeg is given to open the device before the recording counts as started.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(900);

    private readonly List<string> _parts = [];
    private readonly System.Text.StringBuilder _errors = new();
    private Process? _process;
    private string _folder = "";

    /// <summary>A stretch is being recorded right now.</summary>
    public bool IsRecording => _process is { HasExited: false };

    /// <summary>A recording has been started and not yet stopped: it is running, or paused.</summary>
    public bool IsSessionOpen => IsRecording || _parts.Count > 0;

    /// <summary>The names of the audio capture devices DirectShow knows, as FFmpeg expects them in audio="...".</summary>
    public static async Task<List<string>> ListMicrophonesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            return [];

        // Listing devices is not an error to FFmpeg, but opening the "dummy" input afterwards is: the
        // exit code says nothing, the list is on stderr.
        var result = await ProcessPipes.RunBufferedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"])
            .WithValidation(CommandResultValidation.None), cancellationToken, System.Text.Encoding.UTF8);

        return AudioDeviceRegex().Matches(result.StandardError).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>
    /// Starts a new recording, or carries a paused one on, from the named microphone. Throws with FFmpeg's
    /// own words when the device cannot be opened (in use by another program, unplugged, renamed).
    /// </summary>
    /// <param name="folder">Where the stretches of the recording are kept until it is stopped.</param>
    public async Task StartAsync(string microphone, string folder)
    {
        if (IsRecording)
            throw new InvalidOperationException("A recording is already running.");
        if (!File.Exists(DependencyUpdater.FfmpegPath))
            throw new InvalidOperationException("FFmpeg is not installed. Install it from Settings first.");
        if (string.IsNullOrWhiteSpace(microphone))
            throw new InvalidOperationException("Choose a microphone first.");

        _folder = folder;
        Directory.CreateDirectory(folder);
        var part = Path.Combine(folder, $"voiceover_part{_parts.Count}.wav");

        var start = new ProcessStartInfo(DependencyUpdater.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // The way to stop FFmpeg so that it finishes the file properly is to type "q" at it.
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };

        // Each entry is one argument, quoted for the command line as needed: a device name with spaces or
        // brackets in it reaches FFmpeg whole, exactly as audio="Microphone (Realtek Audio)" would.
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "dshow", "-i", $"audio={microphone}", "-y", part })
            start.ArgumentList.Add(argument);

        _errors.Clear();
        var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (_errors)
                    _errors.AppendLine(e.Data.Trim());
            }
        };
        process.BeginErrorReadLine();
        ProcessPipes.Track(process.Id);
        _process = process;

        // A device that cannot be opened makes FFmpeg give up within moments. Waiting for that here turns
        // "nothing was recorded" into a message that says why.
        await Task.Delay(StartupGrace);
        if (process.HasExited)
        {
            ProcessPipes.Untrack(process.Id);
            process.Dispose();
            _process = null;

            string reason;
            lock (_errors)
                reason = _errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            throw new InvalidOperationException(reason.Length > 0 ? reason : "The microphone could not be opened.");
        }

        _parts.Add(part);
    }

    /// <summary>Ends the stretch being recorded, keeping the recording open to be carried on.</summary>
    public async Task PauseAsync()
    {
        if (_process is not { } process)
            return;

        _process = null;
        try
        {
            if (!process.HasExited)
            {
                // FFmpeg is asked to quit, which lets it write the length into the file's header; if it
                // does not go within a few seconds it is stopped the hard way.
                await process.StandardInput.WriteAsync('q');
                await process.StandardInput.FlushAsync();
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try
                {
                    await process.WaitForExitAsync(patience.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone: the file is whatever it managed to write.
        }
        finally
        {
            ProcessPipes.Untrack(process.Id);
            process.Dispose();
        }
    }

    /// <summary>Ends the recording and throws away everything recorded so far.</summary>
    public async Task DiscardAsync()
    {
        await PauseAsync();

        foreach (var part in _parts)
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the session folder, which goes when the application closes.
            }
        }

        _parts.Clear();
    }

    /// <summary>
    /// Ends the recording and writes it to one file: the single stretch as it is, or several joined end to
    /// end. Returns false when nothing usable was recorded.
    /// </summary>
    public async Task<bool> StopAsync(string outputPath, CancellationToken cancellationToken)
    {
        await PauseAsync();

        var parts = _parts.Where(p => File.Exists(p) && new FileInfo(p).Length > 1024).ToList();
        _parts.Clear();
        if (parts.Count == 0)
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            if (parts.Count == 1)
            {
                File.Move(parts[0], outputPath, overwrite: true);
                return true;
            }

            var ffmpeg = DependencyUpdater.IsFfmpegOverridden ? $"\"{DependencyUpdater.FfmpegPath}\"" : "ffmpeg";
            var inputs = string.Join(" ", parts.Select(p => $"-i \"{p}\""));
            await FfmpegRunner.RunAsync(
                $"{ffmpeg} -hide_banner -y {inputs} -filter_complex \"concat=n={parts.Count}:v=0:a=1[whole]\" -map \"[whole]\" -c:a pcm_s16le \"{outputPath}\"",
                new Progress<FfmpegProgress>(), cancellationToken);
            return true;
        }
        finally
        {
            foreach (var part in parts.Where(File.Exists))
                File.Delete(part);
        }
    }
}
/// <summary>
/// Windows notifications. They are sent through a notification-area icon, which Windows 10 and 11 show
/// as ordinary toasts and keep in the notification centre; no installer registration is needed for that.
/// </summary>
public static class Notifier
{
    private static System.Windows.Forms.NotifyIcon? _icon;

    public static void Show(string title, string message)
    {
        try
        {
            _icon ??= new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application,
                Text = "HandPeg",
            };

            // Only in the notification area while there is something to say.
            _icon.Visible = true;
            _icon.ShowBalloonTip(5000, title, message, System.Windows.Forms.ToolTipIcon.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or IOException)
        {
            // A notification that cannot be shown is not worth interrupting anything for.
        }
    }

    /// <summary>Takes the icon out of the notification area. Called as the application closes.</summary>
    public static void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}

/// <summary>
/// A sound reduced to peaks: for each twentieth of a second, the lowest and the highest sample (-1 to 1).
/// Whatever draws it asks for the range over a stretch of it, as wide a stretch as one of its pixels covers:
/// zoomed out, many pairs fall into one pixel and are merged into one; zoomed in, each pair is its own.
/// </summary>
public sealed class WaveformData(float[] lows, float[] highs, Brush fill)
{
    public int Count => highs.Length;

    /// <summary>The colour it is drawn in.</summary>
    public Brush Fill { get; } = fill;

    /// <summary>The peaks between two fractions of its length (0 to 1), at least one of them.</summary>
    public WaveformData Slice(double from, double to)
    {
        var first = Math.Clamp((int)(from * highs.Length), 0, highs.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling(to * highs.Length), first + 1, highs.Length);
        return new WaveformData(lows[first..last], highs[first..last], Fill);
    }

    /// <summary>The lowest and the highest sample between two points of the sound, each a fraction of its length (0 to 1).</summary>
    public (float Low, float High) Range(double from, double to)
    {
        if (highs.Length == 0 || to <= 0 || from >= 1)
            return (0, 0);

        var first = Math.Clamp((int)(from * highs.Length), 0, highs.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling(to * highs.Length) - 1, first, highs.Length - 1);
        var (low, high) = (lows[first], highs[first]);
        for (var i = first + 1; i <= last; i++)
        {
            if (lows[i] < low)
                low = lows[i];
            if (highs[i] > high)
                high = highs[i];
        }

        return (low, high);
    }
}
