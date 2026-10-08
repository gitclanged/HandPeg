using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CliWrap;
using CliWrap.Buffered;

namespace HandPegApp.Services;

/// <summary>Pictures of sound: the waveform images shown on the timeline and in the mixer.</summary>
public static partial class Waveforms
{
    /// <summary>
    /// Draws the whole of one audio track as a single picture with FFmpeg's showwavespic. Returns the
    /// picture, loaded into memory so the file is not held open, or null when it could not be made.
    /// </summary>
    /// <param name="trackIndexes">Which of the file's audio tracks, counted from 0. Several are mixed together (amix) first.</param>
    /// <param name="color">A colour FFmpeg understands: a name such as gray, or 0xRRGGBB.</param>
    public static async Task<ImageSource?> RenderAsync(
        string mediaPath, IReadOnlyList<int> trackIndexes, string imagePath, int width, int height, string color, CancellationToken cancellationToken)
    {
        if (!File.Exists(DependencyUpdater.FfmpegPath) || !File.Exists(mediaPath) || trackIndexes.Count == 0)
            return null;

        var inputs = string.Concat(trackIndexes.Select(index => $"[0:a:{index}]"));
        var mix = trackIndexes.Count > 1 ? $"amix=inputs={trackIndexes.Count}:normalize=0," : "";

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var result = await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-loglevel", "error", "-y", "-i", mediaPath,
                "-filter_complex", $"{inputs}{mix}showwavespic=s={width}x{height}:colors={color}", "-frames:v", "1", imagePath])
            .WithValidation(CommandResultValidation.None),
            cancellationToken);

        return result.ExitCode == 0 ? Load(imagePath) : null;
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

    // How long FFmpeg is given to open the device before the recording counts as started.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(900);

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
        var result = await Cli.Wrap(DependencyUpdater.FfmpegPath)
            .WithArguments(["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(System.Text.Encoding.UTF8, cancellationToken);

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
