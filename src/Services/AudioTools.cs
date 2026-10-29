using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <summary>One waveform to be made of a file: of one of its audio tracks, or of several mixed together.</summary>
/// <param name="Width">The shape of the picture: it is this many units wide for every <paramref name="Height"/> high.</param>
/// <param name="Color">A colour as FFmpeg would be given it: a name such as gray, or 0xRRGGBB.</param>
public sealed record WaveformRequest(IReadOnlyList<int> Tracks, int Width, int Height, string Color)
{
    /// <summary>
    /// What its peaks are kept under in the cache: the rate with the tracks ("peaks80-0", "peaks80-0-1"). A .peaks file holds
    /// only how many pairs it has, so peaks kept at another rate (before 4.4-BugHunt "wave0", "wave0-1"..., twenty a second)
    /// are never found under it, and never read as a sound of a quarter of its length; the cache lets them go as it trims.
    /// </summary>
    public string CacheKind => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"peaks{Waveforms.PeaksPerSecond:0}-{string.Join("-", Tracks)}");
}

/// <summary>Pictures of sound: the waveform images shown on the timeline and in the mixer.</summary>
public static partial class Waveforms
{
    // The sound is read at this rate, which is plenty for its outline (it holds what lies below 4 kHz), and for every so
    // many samples only the lowest and the highest are kept: eighty pairs to the second (4.4-BugHunt, twenty before), so
    // that a timeline zoomed in on a word still shows its syllables. An hour of sound is 288,000 pairs, 2.3 MB (and about
    // a seventh more for the folds, WaveformData).
    private const int SampleRate = 8000, SamplesPerPeak = 100;

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
        string mediaPath, IReadOnlyList<int> trackIndexes, string imagePath, int width, int height, string color, CancellationToken cancellationToken) =>
        (await RenderManyAsync(mediaPath, [new WaveformRequest(trackIndexes, width, height, color)], cancellationToken))[0];

    /// <summary>
    /// Several waveforms of one file in one reading of it: each track asked for, and each mix of tracks, decoded
    /// by one FFmpeg (the tracks split where several waveforms want them, and every waveform a channel of one
    /// stream), where one FFmpeg per waveform would read the whole file once for each. A recording with four
    /// tracks and their mix was five readings of the file. Those kept in the cache are not made again.
    /// </summary>
    /// <returns>One picture for each request, in order; null for one that could not be made.</returns>
    public static async Task<ImageSource?[]> RenderManyAsync(string mediaPath, IReadOnlyList<WaveformRequest> requests, CancellationToken cancellationToken)
    {
        var pictures = new ImageSource?[requests.Count];
        if (!File.Exists(DependencyUpdater.FfmpegPath) || !File.Exists(mediaPath))
            return pictures;

        // Read before, for this file as it is and these tracks: the peaks are taken from the cache.
        var wanted = new List<int>();
        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Tracks.Count == 0)
                continue;
            if (ReadPeaks(PreviewCache.Timeline.Find(mediaPath, requests[i].CacheKind, ".peaks")) is { } kept)
                pictures[i] = Picture(requests[i], new WaveformData(kept.Lows, kept.Highs, BrushOf(requests[i].Color)));
            else
                wanted.Add(i);
        }

        if (wanted.Count == 0)
            return pictures;

        // The reading itself is background work (see WorkScheduler): asked for twice, it is done once.
        var key = $"wave|{mediaPath}|{string.Join(",", wanted.Select(i => requests[i].CacheKind))}";
        return await WorkScheduler.Run(new WorkSpec($"Waveforms of {Path.GetFileName(mediaPath)}", WorkLane.Background) { Key = key },
            work => DecodeAsync(work.Token), cancellationToken);

        async Task<ImageSource?[]> DecodeAsync(CancellationToken cancellationToken)
        {
            // Each track goes to as many waveforms as want it: split where that is more than one.
            var uses = wanted.SelectMany(i => requests[i].Tracks).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            var taken = uses.Keys.ToDictionary(t => t, _ => 0);
            var graph = new System.Text.StringBuilder();
            foreach (var (track, count) in uses.Where(u => u.Value > 1))
            {
                graph.Append($"[0:a:{track}]asplit={count}");
                for (var k = 0; k < count; k++)
                    graph.Append($"[t{track}_{k}]");
                graph.Append(';');
            }

            string Input(int track) => uses[track] > 1 ? $"[t{track}_{taken[track]++}]" : $"[0:a:{track}]";
            for (var w = 0; w < wanted.Count; w++)
            {
                var tracks = requests[wanted[w]].Tracks;
                graph.Append(string.Concat(tracks.Select(Input)));
                if (tracks.Count > 1)
                    graph.Append($"amix=inputs={tracks.Count}:normalize=0,");
                graph.Append($"aresample={SampleRate},aformat=sample_fmts=s16:channel_layouts=mono[w{w}];");
            }

            // Every waveform a channel of the one stream that comes out.
            if (wanted.Count > 1)
                graph.Append(string.Concat(Enumerable.Range(0, wanted.Count).Select(w => $"[w{w}]"))).Append($"amerge=inputs={wanted.Count}[wave]");
            else
                graph.Append("[w0]anull[wave]");

            var readers = wanted.Select(_ => new PeakReader()).ToArray();
            var samples = PipeTarget.Create(async (stream, token) =>
            {
                // Borrowed, and given back: what FFmpeg writes passes through the same room for every waveform.
                var frame = 2 * readers.Length;
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768 / frame * frame + frame);
                try
                {
                    var held = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(held, buffer.Length / frame * frame - held), token)) > 0)
                    {
                        var have = held + read;
                        var whole = have / frame * frame;
                        Deal(buffer, whole, readers);

                        // Part of a frame at the end of what was read waits for the rest of it.
                        held = have - whole;
                        if (held > 0)
                            buffer.AsSpan(whole, held).CopyTo(buffer);
                    }

                    foreach (var reader in readers)
                        reader.Finish();
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            });

            try
            {
                var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                    .WithArguments(["-hide_banner", "-loglevel", "error", "-i", mediaPath, "-filter_complex", graph.ToString(), "-map", "[wave]", "-f", "s16le", "-"])
                    .WithStandardOutputPipe(samples)
                    .WithValidation(CommandResultValidation.None),
                    cancellationToken);

                if (result.ExitCode != 0)
                    return pictures;

                for (var w = 0; w < wanted.Count; w++)
                {
                    var request = requests[wanted[w]];
                    if (readers[w].TakePeaks() is not var (lows, highs))
                        continue;

                    PreviewCache.Timeline.Write(mediaPath, request.CacheKind, ".peaks", stream => WritePeaks(stream, lows, highs));
                    pictures[wanted[w]] = Picture(request, new WaveformData(lows, highs, BrushOf(request.Color)));
                }

                return pictures;
            }
            finally
            {
                foreach (var reader in readers)
                    reader.Dispose();
            }
        }

        static ImageSource Picture(WaveformRequest request, WaveformData data)
        {
            var picture = Draw(data, Math.Max(request.Width, 2), Math.Max(request.Height, 2));
            Peaks.AddOrUpdate(picture, data);
            return picture;
        }
    }

    /// <summary>Hands each waveform its own samples, out of frames of one sample for each (16-bit, little-endian, as this processor is).</summary>
    private static void Deal(byte[] buffer, int length, PeakReader[] readers)
    {
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, length));
        for (var at = 0; at < values.Length; at += readers.Length)
        {
            for (var c = 0; c < readers.Length; c++)
                readers[c].Add(values[at + c]);
        }
    }

    // Peaks as they are kept on disk: how many pairs, then the lows, then the highs, as 32-bit floats. Written
    // and read in place, from and into the arrays the waveform keeps: no copy of the whole is made either way.
    private static void WritePeaks(Stream stream, float[] lows, float[] highs)
    {
        Span<byte> count = stackalloc byte[4];
        BitConverter.TryWriteBytes(count, highs.Length);
        stream.Write(count);
        stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(lows.AsSpan()));
        stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(highs.AsSpan()));
    }

    private static (float[] Lows, float[] Highs)? ReadPeaks(string? path)
    {
        if (path is null)
            return null;

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = RandomAccess.GetLength(handle);
            Span<byte> head = stackalloc byte[4];
            if (length < 4 || RandomAccess.Read(handle, head, 0) != 4)
                return null;

            var count = BitConverter.ToInt32(head);
            if (count <= 0 || length != 4 + count * 8L)
                return null;

            var (lows, highs) = (new float[count], new float[count]);
            var lowBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(lows.AsSpan());
            var highBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(highs.AsSpan());
            return RandomAccess.Read(handle, lowBytes, 4) == lowBytes.Length && RandomAccess.Read(handle, highBytes, 4 + lowBytes.Length) == highBytes.Length
                ? (lows, highs)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reduces 16-bit samples, as they arrive, to the lowest and highest of every so many. The pairs gather in
    /// borrowed room that grows as it needs to, and are copied out once, at their final size, when it is done.
    /// </summary>
    private sealed class PeakReader : IDisposable
    {
        private float[] _lows = System.Buffers.ArrayPool<float>.Shared.Rent(4096), _highs = System.Buffers.ArrayPool<float>.Shared.Rent(4096);
        private int _pairs, _count, _low, _high;

        public void Add(short sample)
        {
            if (sample < _low)
                _low = sample;
            if (sample > _high)
                _high = sample;
            if (++_count == SamplesPerPeak)
                Finish();
        }

        public void Finish()
        {
            if (_count == 0)
                return;

            if (_pairs == _lows.Length)
            {
                Grow(ref _lows);
                Grow(ref _highs);
            }

            _lows[_pairs] = _low / 32768f;
            _highs[_pairs++] = _high / 32768f;
            (_count, _low, _high) = (0, 0, 0);
        }

        /// <summary>The peaks, in arrays of their own exact size; null when there were none.</summary>
        public (float[] Lows, float[] Highs)? TakePeaks() => _pairs == 0 ? null : (_lows.AsSpan(0, _pairs).ToArray(), _highs.AsSpan(0, _pairs).ToArray());

        private void Grow(ref float[] room)
        {
            var larger = System.Buffers.ArrayPool<float>.Shared.Rent(room.Length * 2);
            room.AsSpan(0, _pairs).CopyTo(larger);
            System.Buffers.ArrayPool<float>.Shared.Return(room);
            room = larger;
        }

        public void Dispose()
        {
            System.Buffers.ArrayPool<float>.Shared.Return(_lows);
            System.Buffers.ArrayPool<float>.Shared.Return(_highs);
            (_lows, _highs) = ([], []);
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

    /// <summary>The peaks as a picture: one shape, a column for every so many pairs (one sweep of the peaks, WaveformData.Envelope).</summary>
    private static ImageSource Draw(WaveformData data, double width, double height)
    {
        var columns = Math.Max(Math.Min(data.Count, MostColumns), 1);
        var (step, middle) = (width / columns, height / 2);
        var pool = System.Buffers.ArrayPool<double>.Shared;
        var floats = System.Buffers.ArrayPool<float>.Shared;
        var (tops, bottoms) = (pool.Rent(columns), pool.Rent(columns));
        var (lows, highs) = (floats.Rent(columns), floats.Rent(columns));
        data.Envelope(0, data.Seconds, lows.AsSpan(0, columns), highs.AsSpan(0, columns));
        for (var c = 0; c < columns; c++)
        {
            // Silence is still a line: the sound is there, and quiet.
            tops[c] = middle - Math.Max(highs[c] * middle, height / 200);
            bottoms[c] = middle - Math.Min(lows[c] * middle, -height / 200);
        }

        floats.Return(lows);
        floats.Return(highs);

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
/// A sound reduced to peaks: for each eightieth of a second, the lowest and the highest sample (-1 to 1).
/// Whatever draws it asks for the range over a stretch of it, as wide a stretch as one of its pixels covers:
/// zoomed out, many pairs fall into one pixel and are merged into one; zoomed in, each pair is its own.
/// Since 4.4 the peaks are also kept folded (a mip pyramid: every 8 pairs as one, every 64, every 512), made once
/// when the peaks are, so that a stretch of any length is answered by reading a few dozen numbers, not every pair in it.
/// The folds are never written to disk: the .peaks file holds the pairs alone, as before.
/// </summary>
public sealed class WaveformData
{
    /// <summary>How many pairs of one level are folded into one pair of the next; the levels are 1:1, 1:8, 1:64 and 1:512.</summary>
    public const int MipFactor = 8, MipLevels = 4;

    // The pairs of each level, [level][i]: level 0 is the arrays given, each level above folds 8 of the one below
    // (its last pair folding what is left over, so nothing is padded).
    private readonly float[][] _lows, _highs;

    public WaveformData(float[] lows, float[] highs, Brush fill, double pairsPerSecond = Waveforms.PeaksPerSecond)
    {
        Fill = fill;
        PairsPerSecond = pairsPerSecond > 0 ? pairsPerSecond : Waveforms.PeaksPerSecond;
        (_lows, _highs) = (new float[MipLevels][], new float[MipLevels][]);
        (_lows[0], _highs[0]) = (lows, highs);
        for (var level = 1; level < MipLevels; level++)
        {
            var (belowLows, belowHighs) = (_lows[level - 1], _highs[level - 1]);
            var count = Math.Min(belowLows.Length, belowHighs.Length);
            var folded = (count + MipFactor - 1) / MipFactor;
            var (levelLows, levelHighs) = (new float[folded], new float[folded]);
            for (var j = 0; j < folded; j++)
            {
                var (start, end) = (j * MipFactor, Math.Min(j * MipFactor + MipFactor, count));
                var (low, high) = (belowLows[start], belowHighs[start]);
                for (var i = start + 1; i < end; i++)
                {
                    if (belowLows[i] < low)
                        low = belowLows[i];
                    if (belowHighs[i] > high)
                        high = belowHighs[i];
                }

                (levelLows[j], levelHighs[j]) = (low, high);
            }

            (_lows[level], _highs[level]) = (levelLows, levelHighs);
        }
    }

    public int Count => _highs[0].Length;

    /// <summary>The colour it is drawn in.</summary>
    public Brush Fill { get; }

    /// <summary>How many pairs there are to a second of the sound (80 since 4.4-BugHunt, 20 before).</summary>
    public double PairsPerSecond { get; }

    /// <summary>How long the sound is, in its own seconds: what the pairs cover (not the container's duration, which may differ).</summary>
    public double Seconds => Count / PairsPerSecond;

    /// <summary>How many pairs a level has.</summary>
    public int CountAt(int level) => _highs[level].Length;

    public ReadOnlySpan<float> LowsAt(int level) => _lows[level];

    public ReadOnlySpan<float> HighsAt(int level) => _highs[level];

    /// <summary>The level whose pairs are no wider than a column: 0 below 8 pairs a column, then 1, 2 and 3.</summary>
    public static int LevelFor(double pairsPerColumn) =>
        pairsPerColumn >= 512 ? 3 : pairsPerColumn >= 64 ? 2 : pairsPerColumn >= 8 ? 1 : 0;

    /// <summary>The peaks between two fractions of its length (0 to 1), at least one of them.</summary>
    public WaveformData Slice(double from, double to)
    {
        var (lows, highs) = (_lows[0], _highs[0]);
        var first = Math.Clamp((int)(from * highs.Length), 0, highs.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling(to * highs.Length), first + 1, highs.Length);
        return new WaveformData(lows[first..last], highs[first..last], Fill, PairsPerSecond);
    }

    /// <summary>The lowest and the highest sample between two points of the sound, each a fraction of its length (0 to 1).</summary>
    public (float Low, float High) Range(double from, double to)
    {
        var count = Count;
        if (count == 0 || to <= 0 || from >= 1)
            return (0, 0);

        var first = Math.Clamp((int)(from * count), 0, count - 1);
        var last = Math.Clamp((int)Math.Ceiling(to * count) - 1, first, count - 1);
        return RangeOf(first, last + 1);
    }

    /// <summary>
    /// The lowest and the highest sample of the pairs from <paramref name="first"/> up to (not including)
    /// <paramref name="end"/>, exactly: the ragged ends are read pair by pair, and what lies between them a level up, as
    /// whole folds, climbing until little is left. (0, 0) for no pairs.
    /// </summary>
    public (float Low, float High) RangeOf(int first, int end)
    {
        first = Math.Max(first, 0);
        end = Math.Min(end, Count);
        if (end <= first)
            return (0, 0);

        var (low, high) = (float.PositiveInfinity, float.NegativeInfinity);
        for (var level = 0; ; level++)
        {
            var (lows, highs) = (_lows[level], _highs[level]);
            if (level == MipLevels - 1 || end - first <= 2 * MipFactor)
            {
                for (var i = first; i < end; i++)
                    Merge(lows[i], highs[i]);
                break;
            }

            // The start up to a whole fold, pair by pair.
            while (first % MipFactor != 0 && first < end)
            {
                Merge(lows[first], highs[first]);
                first++;
            }

            // The end down to a whole fold, unless it is the level's end: its last fold covers exactly what is left.
            var whole = end == lows.Length;
            if (!whole)
            {
                while (end % MipFactor != 0 && end > first)
                {
                    end--;
                    Merge(lows[end], highs[end]);
                }
            }

            if (end <= first)
                break;

            first /= MipFactor;
            end = whole ? (end + MipFactor - 1) / MipFactor : end / MipFactor;
        }

        return (low, high);

        void Merge(float pairLow, float pairHigh)
        {
            if (pairLow < low)
                low = pairLow;
            if (pairHigh > high)
                high = pairHigh;
        }
    }

    /// <summary>
    /// One low and one high for each column of a stretch of the sound, from <paramref name="fromSeconds"/> to
    /// <paramref name="toSeconds"/> (its own seconds), as many columns as the spans are long. One sweep over the level
    /// whose folds are no wider than a column: each fold goes into the column its middle falls in, and a column no fold
    /// falls in (zoomed in, or past the end of the sound) takes the fold under its own middle, or silence. So a column
    /// may show a sound up to a fold away from it, never less than what lies wholly inside it.
    /// </summary>
    public void Envelope(double fromSeconds, double toSeconds, Span<float> lows, Span<float> highs)
    {
        var columns = Math.Min(lows.Length, highs.Length);
        if (columns == 0)
            return;

        if (Count == 0 || !(toSeconds > fromSeconds))
        {
            lows[..columns].Clear();
            highs[..columns].Clear();
            return;
        }

        var (pairsFrom, pairsTo) = (fromSeconds * PairsPerSecond, toSeconds * PairsPerSecond);
        var perColumn = (pairsTo - pairsFrom) / columns;
        var level = LevelFor(perColumn);
        var fold = 1 << (3 * level);
        var (levelLows, levelHighs) = (_lows[level], _highs[level]);
        lows[..columns].Fill(float.PositiveInfinity);
        highs[..columns].Fill(float.NegativeInfinity);

        var firstFold = (int)Math.Clamp(Math.Floor(pairsFrom / fold), 0, levelLows.Length);
        var endFold = (int)Math.Clamp(Math.Ceiling(pairsTo / fold), firstFold, levelLows.Length);
        for (var i = firstFold; i < endFold; i++)
        {
            var c = (int)Math.Clamp(((i + 0.5) * fold - pairsFrom) / perColumn, 0, columns - 1);
            if (levelLows[i] < lows[c])
                lows[c] = levelLows[i];
            if (levelHighs[i] > highs[c])
                highs[c] = levelHighs[i];
        }

        for (var c = 0; c < columns; c++)
        {
            if (!float.IsPositiveInfinity(lows[c]))
                continue;

            var under = (int)Math.Floor((pairsFrom + (c + 0.5) * perColumn) / fold);
            (lows[c], highs[c]) = under >= 0 && under < levelLows.Length ? (levelLows[under], levelHighs[under]) : (0f, 0f);
        }
    }
}
