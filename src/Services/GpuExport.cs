using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>
/// A GPU export that could not be made (the compositor would not start, a video it cannot decode, a frame it would not
/// draw): nothing usable was written, and the FFmpeg path makes the export instead.
/// </summary>
public sealed class GpuExportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The GPU export (Pass 5.0-AudioAnd3D): the compositor that draws the player renders every frame of the export, and
/// FFmpeg encodes them, with the export's own command for everything else (<see cref="FilterGraphBuilder.BuildGpuExportCommand"/>:
/// the same inputs, the same sound graph and encoders, captions and burned subtitles by libass, chapters, soft subtitles).
/// <see cref="FilterGraphBuilder"/>'s graph stays the bit-exact CPU path and the golden reference; this is the fast one.
/// <list type="bullet">
/// <item><b>Its own compositor.</b> A worker of its own (and a standby beside it, so a TDR mid-export is taken over and
/// the frame drawn again), on the compositor's card, with its own tables (<see cref="GpuResourceBroker"/>, every texture made
/// at once) and its own plan of the export's state (<see cref="GpuScenePlan"/>, on the UI thread). The player's engine is
/// left alone.</item>
/// <item><b>Exact frames.</b> Each scene asks the worker for every media layer's exact frame
/// (<see cref="FrameSceneHeader.FlagExactFrames"/>), stretched over the export's frame (no letterbox), read back
/// (<see cref="FrameSceneHeader.FlagReadback"/>, BGRA) and checked: a frame drawn with a stand-in, more layers than a scene
/// holds, or a video the worker cannot decode (each is drawn once alone over magenta first) gives the export to FFmpeg.</item>
/// <item><b>Pipelined.</b> One thread renders (publish, wait, copy the readback out, publish the next at once); FFmpeg's
/// standard input is written from the other buffer meanwhile. Two buffers of one frame each; the canvas frames the
/// export's rate skips are never drawn, and the ones it repeats are drawn once (<see cref="GpuExportTiming"/>).</item>
/// <item><b>Never HandPeg's fault.</b> A frame not read back in <see cref="FrameTimeoutMs"/> is published again, up to
/// <see cref="TriesPerFrame"/> times; then the export gives up (<see cref="GpuExportException"/>, logged), and the view model
/// makes it with FFmpeg from the beginning. Nothing escapes the render thread.</item>
/// </list>
/// <para>Threads: <see cref="Prepare"/> on the UI thread (or another STA thread: text clips are drawn by WPF there);
/// <see cref="StartCompositor"/> off it (it waits for the worker); <see cref="RunAsync"/> from anywhere.</para>
/// </summary>
public sealed class GpuExport : IDisposable
{
    /// <summary>How long one frame may take to come back (the worker itself waits up to 20 s for an exact frame), ms.</summary>
    public const int FrameTimeoutMs = 25_000;

    /// <summary>How many times a frame that did not come back is published again before the export gives up.</summary>
    public const int TriesPerFrame = 3;

    /// <summary>How long the worker may take to start, ms.</summary>
    public const int StartTimeoutMs = 10_000;

    /// <summary>
    /// The export worker's frame cache: enough for its play-ahead (0.35 s of 4K) and the cut ahead, little enough to leave
    /// the card to the player's own worker and the encoder (Windows' budget for the process caps it besides).
    /// </summary>
    public const long CacheBudgetBytes = 768L << 20;

    /// <summary>
    /// How far into its frame each moment is drawn, seconds: the media frame a moment falls on is the one at or before it,
    /// and a container's time stamps are rounded (a millisecond in Matroska), so the moment is taken a hair in, as the
    /// differential test of the GPU scene takes it.
    /// </summary>
    public const double FrameHair = 0.0005;

    private const int Buffers = 2;

    // The clip key of a source's probe (each source's its own).
    private const long ProbeKey = 0x5052_4F42_0000_0000;

    private readonly CompositorClient _client;
    private readonly GpuResourceBroker _broker;
    private readonly GpuScenePlan _plan;
    private readonly byte[][] _buffers = new byte[Buffers][];
    private readonly int[] _repeats = new int[Buffers];
    private readonly SemaphoreSlim _free = new(Buffers, Buffers);
    private readonly SemaphoreSlim _filled = new(0, Buffers + 1);
    private readonly GpuSeekHint[] _hints = new GpuSeekHint[CompositorProtocol.MaxHints];
    private readonly GpuMediaHint[] _media = new GpuMediaHint[CompositorProtocol.MaxHints];
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _renderer;
    private CancellationTokenSource? _abort;
    private Exception? _fault;
    private volatile bool _done;
    private long _produced, _consumed, _piped;
    private long _approximateSeen;
    private readonly GpuHintWriter _hintWriter = new();
    private int _publishedTo;
    private bool _disposed;

    private GpuExport(CompositorClient client, GpuResourceBroker broker, GpuScenePlan plan, GpuExportCommand command)
    {
        (_client, _broker, _plan, Command) = (client, broker, plan, command);
        for (var i = 0; i < Buffers; i++)
            _buffers[i] = GC.AllocateUninitializedArray<byte>((int)command.FrameBytes);
    }

    /// <summary>The FFmpeg command the frames are piped to.</summary>
    public GpuExportCommand Command { get; }

    /// <summary>How many frames have been handed to FFmpeg so far.</summary>
    public long FramesPiped => Interlocked.Read(ref _piped);

    // ----- Whether, and getting ready -----

    /// <summary>
    /// Why an export cannot be drawn by the GPU compositor, in plain words (for the log); null when it can. The graph's
    /// reasons (<see cref="FilterGraphBuilder.BuildGpuExportCommand"/>) and the plan's: an effect without a shader, a second
    /// LUT or Curves card on a layer (the descriptor has one of each), a kind of layer the plan does not draw, the colour
    /// sliders' channel balance, more files or textures than the compositor's tables hold.
    /// </summary>
    public static string? WhyNot(SequenceExportState state)
    {
        if (FilterGraphBuilder.BuildGpuExportCommand(state, out var reason) is null)
            return reason;

        var lowered = FilterGraphBuilder.LowerForGpu(state);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textures = state.LutPath.Trim().Trim('"').Length > 0 ? 1 : 0;
        foreach (var layer in lowered.Stack)
        {
            // (A twin is its video's blurred copy; a hidden or unusable layer is drawn by neither.)
            if (lowered.BlurTwins.Contains(layer) || (layer.IsMainVideo ? layer.IsHidden : !layer.IsUsable))
                continue;
            if (layer.Kind is not (LayerKind.MainVideo or LayerKind.Video or LayerKind.VideoFile or LayerKind.Image or LayerKind.Text or LayerKind.Adjustment))
                return $"a {layer.Kind} layer ({layer.Name}) is not drawn by the compositor";
            if (!layer.EffectsBypassed && CardsWhyNot(layer.Name, layer.Effects, ref textures) is { } cards)
                return cards;
            if (layer.IsVideoFile)
                files.Add(layer.ImagePath.Trim().Trim('"'));
            if (layer.IsImage || layer.IsText)
                textures++;
            if (layer.CustomMask)
                textures++;
        }

        if (CardsWhyNot("the Sequence Output", lowered.SequenceEffects, ref textures) is { } output)
            return output;

        // The blurred background's own cards (5.0-Release), judged as a clip's are.
        if (CardsWhyNot("the blurred background", state.BackgroundEffects.Select(EffectInstance.FromState), ref textures) is { } background)
            return background;
        foreach (var filter in state.ColorFilters)
        {
            if (ChannelBalanced(filter))
                return "the colour sliders' red, green and blue balance is FFmpeg's (the compositor has none yet)";
        }

        if (files.Count + 1 > CompositorProtocol.MaxSources)
            return string.Create(CultureInfo.InvariantCulture, $"{files.Count} video files are more than the compositor's source table holds ({CompositorProtocol.MaxSources - 1})");
        if (textures > CompositorProtocol.MaxResources - 4)
            return string.Create(CultureInfo.InvariantCulture, $"some {textures} pictures, titles, masks and colour tables are more than the compositor's resource table holds");
        return null;
    }

    /// <summary>A stack of effect cards the compositor cannot draw as FFmpeg does: a card with no shader, two LUTs or two Curves. Counts the textures it needs.</summary>
    private static string? CardsWhyNot(string owner, IEnumerable<EffectInstance> cards, ref int textures)
    {
        var (luts, curves) = (0, 0);
        foreach (var card in cards)
        {
            if (!card.Enabled)
                continue;
            if (card.Definition is not { } definition)
                return $"{owner} has an effect HandPeg does not know ({card.Type})";
            if (definition.GpuSlot == GpuShaderSlot.None)
                return $"{owner}'s {definition.Name} effect has no shader in the compositor yet";
            if ((definition.GpuSlot == GpuShaderSlot.Lut3D && ++luts > 1) || (definition.GpuSlot == GpuShaderSlot.ToneCurve && ++curves > 1))
                return $"{owner} has two LUT or two Curves cards (the compositor draws one of each)";
        }

        textures += luts + curves;
        return null;
    }

    /// <summary>Whether one of the colour sliders' filters (eq) sets a channel's own gamma, which the compositor's grade does not have.</summary>
    private static bool ChannelBalanced(string filter)
    {
        if (!filter.StartsWith("eq=", StringComparison.Ordinal))
            return false;
        foreach (var option in filter[3..].Split(':'))
        {
            var at = option.IndexOf('=');
            if (at > 0 && option[..at] is "gamma_r" or "gamma_g" or "gamma_b"
                && double.TryParse(option[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && Math.Abs(value - 1) > 0.005)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts a compositor for an export, on the compositor's card (AppSettings.CompositorGpuAdapter), with a standby, and
    /// waits until it is ready: off the UI thread. Null (and why) when it does not start.
    /// </summary>
    public static CompositorClient? StartCompositor(out string whyNot)
    {
        var settings = AppSettings.Current;
        CompositorClient? client = null;
        try
        {
            client = new CompositorClient(settings.CompositorGpuAdapter, keepStandby: true, CacheBudgetBytes, settings.GpuDecodeThreads, announcesRecovery: false);
            var clock = Stopwatch.StartNew();
            while (client.State == CompositorWorkerState.Starting && clock.ElapsedMilliseconds < StartTimeoutMs)
                Thread.Sleep(10);
            if (client.State != CompositorWorkerState.Ready)
            {
                whyNot = $"the GPU compositor did not start ({client.State}: {client.Message})";
                client.Dispose();
                return null;
            }

            whyNot = "";
            return client;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            whyNot = $"the GPU compositor could not be started: {ex.Message}";
            client?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Plans the export on a compositor (the UI thread: WPF draws the text clips): its command, its tables, its scene
    /// (fades and captions left to FFmpeg, which draws them on the piped frames as the graph does), and the readback
    /// section. Takes <paramref name="client"/> over when it returns an export; null (and why) when it cannot.
    /// </summary>
    public static GpuExport? Prepare(SequenceExportState state, CompositorClient client, out string whyNot) =>
        FilterGraphBuilder.BuildGpuExportCommand(state, out whyNot) is { } command ? Prepare(state, command, client, out whyNot) : null;

    /// <summary>
    /// <see cref="Prepare(SequenceExportState, CompositorClient, out string)"/> for a command already made: the export's, or
    /// Render Preview's (<see cref="FilterGraphBuilder.BuildGpuPreviewCommand"/>), whose timing is a stretch of the sequence.
    /// </summary>
    public static GpuExport? Prepare(SequenceExportState state, GpuExportCommand command, CompositorClient client, out string whyNot)
    {
        GpuResourceBroker? broker = null;
        try
        {
            broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
            var plan = GpuScenePlan.Create(state with { FadeIn = false, FadeOut = false, AutoCaptions = false }, broker);
            if (ReferenceEquals(plan, GpuScenePlan.Empty))
            {
                whyNot = "the GPU scene could not be planned (the log says why)";
                broker.Dispose();
                return null;
            }

            if (plan.CanvasWidth != command.Width || plan.CanvasHeight != command.Height)
            {
                whyNot = string.Create(CultureInfo.InvariantCulture, $"the GPU scene's frame ({plan.CanvasWidth} x {plan.CanvasHeight}) is not the export's ({command.Width} x {command.Height})");
                broker.Dispose();
                return null;
            }

            client.EnsureReadback(command.FrameBytes);
            whyNot = "";
            return new GpuExport(client, broker, plan, command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            whyNot = $"the GPU export could not be prepared: {ex.Message}";
            broker?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The export on the GPU compositor, for the view model (from the UI thread): false when the export is not one it can
    /// draw, or the compositor did not start (nothing was done; the log says why); true when it was made. Throws
    /// <see cref="OperationCanceledException"/> when cancelled, <see cref="GpuExportException"/> when the render failed and
    /// <see cref="InvalidOperationException"/> when FFmpeg did: the caller then makes it with FFmpeg's graph.
    /// </summary>
    public static async Task<bool> TryRunAsync(SequenceExportState state, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (WhyNot(state) is { } reason)
        {
            AppLog.Write($"GPU export: FFmpeg's graph renders this export ({reason}).");
            return false;
        }

        progress.Report(new FfmpegProgress("Starting the GPU compositor...", null, null));
        var (client, why) = await Task.Run(() => (StartCompositor(out var whyNot), whyNot), cancellationToken);
        if (client is null)
        {
            AppLog.Write($"GPU export: FFmpeg's graph renders this export ({why}).");
            return false;
        }

        // Back on the caller's thread (the UI's): the scene is planned there.
        var export = Prepare(state, client, out why);
        if (export is null)
        {
            client.Dispose();
            AppLog.Write($"GPU export: FFmpeg's graph renders this export ({why}).");
            return false;
        }

        using (export)
        {
            var timing = export.Command.Timing;
            AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                $"GPU export: {export.Command.Width} x {export.Command.Height} at {timing.OutputRate}, {timing.OutputFrames} frames ({timing.CanvasFrames} on the canvas at {timing.CanvasNumerator}/{timing.CanvasDenominator}), on {client.Message}"));
            progress.Report(new FfmpegProgress($"Rendering on the GPU compositor ({client.Message})...", null, null));
            var clock = Stopwatch.StartNew();
            await export.RunAsync(progress, cancellationToken);
            AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                $"GPU export: {export.FramesPiped} frames in {clock.Elapsed.TotalSeconds:0.0} s ({export.FramesPiped / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0.0} a second)"));
        }

        return true;
    }

    /// <summary>
    /// Render Preview on the GPU compositor (5.0-Verify Q5), for the view model (from the UI thread): the stretch from
    /// <paramref name="fromSeconds"/> to <paramref name="toSeconds"/> drawn as the export draws its frames, its sound the sound
    /// engine's render of the same program (AudioMixer.Render, from the first frame drawn), into BuildPreviewCommand's small
    /// fast x264. False when the GPU cannot draw it or its compositor did not start (nothing was done; the log says why);
    /// true when it was made. Throws as <see cref="TryRunAsync"/> does: the caller then makes it with FFmpeg's graph.
    /// </summary>
    /// <param name="captionsPath">The stretch's auto-caption file (timed from its start), or null.</param>
    public static async Task<bool> TryRunPreviewAsync(SequenceExportState state, string outputPath, double fromSeconds, double toSeconds, int percent,
        string? captionsPath, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (WhyNot(state) is { } reason
            || FilterGraphBuilder.BuildGpuPreviewCommand(state, outputPath, fromSeconds, toSeconds, percent, captionsPath, null, out reason) is not { } bare)
        {
            AppLog.Write($"GPU preview: FFmpeg's graph renders this preview ({reason}).");
            return false;
        }

        // The sound first, off this thread (it decodes): exactly as long as the frames piped, from the first of them.
        var timing = bare.Timing;
        var start = timing.CanvasSeconds(timing.FirstCanvasFrame);
        var frames = (long)Math.Round(timing.OutputSeconds(timing.OutputFrames) * SoundRate);
        var program = AudioProgram.Build(state);
        var soundPath = program.Strips.Count > 0 && frames > 0 ? outputPath + ".sound.f32" : null;
        try
        {
            progress.Report(new FfmpegProgress("Rendering the preview's sound...", null, null));
            if (soundPath is not null)
                await Task.Run(() => WriteSound(program, start, frames, soundPath), cancellationToken);

            var command = soundPath is null ? bare
                : FilterGraphBuilder.BuildGpuPreviewCommand(state, outputPath, fromSeconds, toSeconds, percent, captionsPath, soundPath, out reason);
            if (command is null)
            {
                AppLog.Write($"GPU preview: FFmpeg's graph renders this preview ({reason}).");
                return false;
            }

            progress.Report(new FfmpegProgress("Starting the GPU compositor...", null, null));
            var (client, why) = await Task.Run(() => (StartCompositor(out var whyNot), whyNot), cancellationToken);
            if (client is null)
            {
                AppLog.Write($"GPU preview: FFmpeg's graph renders this preview ({why}).");
                return false;
            }

            // Back on the caller's thread (the UI's): the scene is planned there.
            var export = Prepare(state, command, client, out why);
            if (export is null)
            {
                client.Dispose();
                AppLog.Write($"GPU preview: FFmpeg's graph renders this preview ({why}).");
                return false;
            }

            using (export)
            {
                progress.Report(new FfmpegProgress($"Rendering the preview on the GPU compositor ({client.Message})...", null, null));
                var clock = Stopwatch.StartNew();
                await export.RunAsync(progress, cancellationToken);
                AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                    $"GPU preview: {export.FramesPiped} frames from {start:0.000} s in {clock.Elapsed.TotalSeconds:0.0} s, at {percent} % size"));
            }

            return true;
        }
        finally
        {
            if (soundPath is not null)
            {
                try
                {
                    File.Delete(soundPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open: it is in the session's previews folder, which goes when HandPeg closes.
                }
            }
        }
    }

    /// <summary>The rate Render Preview's sound is rendered at and given to FFmpeg (as its f32le input says).</summary>
    public const int SoundRate = 48000;

    // A program's sound from a moment, interleaved stereo float, into the file as f32le reads it (little-endian, as the processor keeps it).
    private static void WriteSound(AudioProgram program, double fromSeconds, long frames, string path)
    {
        var sound = new float[checked((int)frames * 2)];
        AudioMixer.Render(program, fromSeconds, sound, SoundRate);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        file.Write(MemoryMarshal.AsBytes(sound.AsSpan()));
    }

    // ----- Running -----

    /// <summary>
    /// Renders the export and encodes it: the render thread is started, its first frame waited for (every source drawn once
    /// first: nothing is written before the compositor has shown it can draw them), then FFmpeg runs with the frames piped to
    /// it. Throws <see cref="OperationCanceledException"/> when cancelled, <see cref="GpuExportException"/> when the render
    /// failed, <see cref="InvalidOperationException"/> when FFmpeg did.
    /// </summary>
    public async Task RunAsync(IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer is not null)
            throw new InvalidOperationException("A GPU export runs once.");

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _abort = abort;
        _renderer = new Thread(Render) { Name = "GPU export", IsBackground = true };
        _renderer.Start();
        try
        {
            await _firstFrame.Task.WaitAsync(cancellationToken);
            await RunFfmpegAsync(progress, abort.Token);
            if (_fault is { } late)
                throw late as GpuExportException ?? new GpuExportException(late.Message, late);
            if (Interlocked.Read(ref _piped) < Command.Timing.OutputFrames)
            {
                AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                    $"GPU export: FFmpeg ended after {Interlocked.Read(ref _piped)} of {Command.Timing.OutputFrames} frames (its own options stopped it)."));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _fault is { } fault)
        {
            // Stopped by the render thread's failure, not by the user.
            throw fault as GpuExportException ?? new GpuExportException(fault.Message, fault);
        }
        finally
        {
            abort.Cancel();
            if (_renderer.IsAlive && !_renderer.Join(10_000))
                AppLog.Write("GPU export: the render thread did not stop; its compositor is left to end with HandPeg.");
            _abort = null;
        }
    }

    /// <summary>FFmpeg, as FfmpegRunner runs an export (graphs in files, the arguments checked to arrive as meant), its standard input the frames.</summary>
    private async Task RunFfmpegAsync(IProgress<FfmpegProgress> progress, CancellationToken token)
    {
        var executable = Command.Executable;
        var isConfigured = DependencyUpdater.IsFfmpegOverridden && executable.Equals(DependencyUpdater.FfmpegPath, StringComparison.OrdinalIgnoreCase);
        if (!isConfigured && !Path.GetFileNameWithoutExtension(executable).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The command has to start with ffmpeg.");
        if (Path.GetFileName(executable) == executable && File.Exists(DependencyUpdater.FfmpegPath))
            executable = DependencyUpdater.FfmpegPath;

        var graphFiles = new List<string>();
        try
        {
            var form = await FfmpegRunner.GraphFileFormAsync(executable, token);
            var arguments = FfmpegRunner.WithGraphFiles(Command.Arguments, always: true, form, graph =>
            {
                Directory.CreateDirectory(SessionPaths.Graphs);
                var path = Path.Combine(SessionPaths.Graphs, $"graph_{Guid.NewGuid():N}.txt");
                File.WriteAllText(path, graph, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                graphFiles.Add(path);
                return path;
            });

            var recent = new Queue<string>();
            void OnLine(string line)
            {
                var text = line.Trim();
                if (text.Length == 0)
                    return;
                if (!text.StartsWith("frame=", StringComparison.Ordinal) && !text.Contains("time=", StringComparison.Ordinal))
                {
                    lock (recent)
                    {
                        recent.Enqueue(text);
                        if (recent.Count > 40)
                            recent.Dequeue();
                    }
                }

                progress.Report(new FfmpegProgress(text, PipedTime(), null));
            }

            var command = Cli.Wrap(executable).WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardInputPipe(PipeSource.Create((stdin, pipeToken) => PipeFramesAsync(stdin, progress, pipeToken)))
                .WithStandardOutputPipe(ProcessPipes.Lines(OnLine))
                .WithStandardErrorPipe(ProcessPipes.Lines(OnLine));
            if (!FfmpegRunner.SplitArguments(command.Arguments).SequenceEqual(arguments, StringComparer.Ordinal))
                throw new FfmpegCommandException("The GPU export's FFmpeg command could not be passed on unchanged, so it was not run.");
            var length = executable.Length + 3 + command.Arguments.Length;
            if (length > FfmpegRunner.MaxCommandLineLength)
                throw new FfmpegCommandException(string.Create(CultureInfo.InvariantCulture, $"The GPU export's FFmpeg command is {length:N0} characters long, and Windows starts no program from more than {FfmpegRunner.MaxCommandLineLength:N0}."));

            var result = await ProcessPipes.RunAsync(command, token);
            if (result.ExitCode != 0)
            {
                List<string> lines;
                lock (recent)
                    lines = [.. recent];
                var problems = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                                                || l.Contains("could not", StringComparison.OrdinalIgnoreCase) || l.Contains("not supported", StringComparison.OrdinalIgnoreCase)).TakeLast(3).ToList();
                var detail = problems.Count > 0 ? string.Join(" | ", problems) : lines.Count > 0 ? string.Join(" | ", lines.TakeLast(3)) : "no output";
                throw new InvalidOperationException($"FFmpeg exited with code {result.ExitCode}: {detail}");
            }
        }
        finally
        {
            foreach (var file in graphFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open: it is in the session folder, which goes when HandPeg closes.
                }
            }
        }
    }

    /// <summary>How far into the export the frames handed to FFmpeg reach (the progress bar's position).</summary>
    private TimeSpan PipedTime() => TimeSpan.FromSeconds(Command.Timing.OutputSeconds(Interlocked.Read(ref _piped)));

    /// <summary>
    /// FFmpeg's standard input: each frame the render thread hands over, as many times as the export shows it, then the
    /// buffer is given back. FFmpeg that stops reading (it failed, or its own options ended it) ends this quietly: its
    /// exit says why.
    /// </summary>
    private async Task PipeFramesAsync(Stream stdin, IProgress<FfmpegProgress> progress, CancellationToken token)
    {
        var bytes = (int)Command.FrameBytes;
        var reported = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                await _filled.WaitAsync(token);
                if (Interlocked.Read(ref _consumed) >= Interlocked.Read(ref _produced))
                {
                    if (_done)
                        break;
                    continue;
                }

                var buffer = (int)(_consumed % Buffers);
                for (var i = 0; i < _repeats[buffer]; i++)
                {
                    await stdin.WriteAsync(_buffers[buffer].AsMemory(0, bytes), token);
                    Interlocked.Increment(ref _piped);
                }

                Interlocked.Increment(ref _consumed);
                _free.Release();
                if (reported.ElapsedMilliseconds >= 250)
                {
                    progress.Report(new FfmpegProgress("", PipedTime(), null));
                    reported.Restart();
                }
            }

            await stdin.FlushAsync(token);
        }
        catch (IOException)
        {
            // FFmpeg is no longer reading: it failed (its exit code and its words say why) or its own options stopped it.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // FFmpeg ended while a frame was awaited (its exit says why), or the run was cancelled (the run says so).
        }
    }

    // ----- The render thread -----

    /// <summary>A scene to publish: canvas frame <see cref="Frame"/> of the export, or (a <see cref="Source"/> of 0 or more) that source alone at <see cref="Media"/>.</summary>
    private readonly record struct Wanted(long Frame, int Source, double Media);

    private void Render()
    {
        var token = _abort!.Token;
        try
        {
            WarmUp();
            ProbeSources(token);
            _approximateSeen = Volatile.Read(ref _client.Memory.Control.ApproximatePresents);

            var timing = Command.Timing;
            var slot = 0L;
            var frame = timing.CanvasFrameOf(0);
            var repeat = RepeatOf(slot, frame);
            var wanted = new Wanted(frame, -1, 0);
            var sequence = Publish(wanted);
            while (true)
            {
                _free.Wait(token);
                var buffer = (int)(Interlocked.Read(ref _produced) % Buffers);
                sequence = Read(sequence, wanted, _buffers[buffer], token);
                CheckExact();
                _repeats[buffer] = repeat;

                // The next frame is published before this one is handed over: the worker draws it while FFmpeg takes this.
                slot += repeat;
                var more = slot < timing.OutputFrames;
                if (more)
                {
                    frame = timing.CanvasFrameOf(slot);
                    repeat = RepeatOf(slot, frame);
                    wanted = new Wanted(frame, -1, 0);
                    sequence = Publish(wanted);
                }
                else
                {
                    // The last frame's own count is written after its readback: one more scene, and it is there.
                    var closing = Publish(wanted with { Source = -2 });
                    if (!_client.WaitForFrame(closing, FrameTimeoutMs))
                        throw new GpuExportException($"The GPU compositor did not finish the export's last frame ({_client.State}: {_client.Message}).");
                    CheckExact();
                }

                Interlocked.Increment(ref _produced);
                _filled.Release();
                _firstFrame.TrySetResult();
                if (!more)
                    break;
            }

            _done = true;
            _filled.Release();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _firstFrame.TrySetCanceled(token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never out of this thread: the export is given up, FFmpeg stopped, and the view model makes it with FFmpeg's graph.
            var fault = ex as GpuExportException ?? new GpuExportException($"The GPU export stopped: {ex.Message}", ex);
            AppLog.Write("GPU export", fault);
            _fault = fault;
            _firstFrame.TrySetException(fault);
            try
            {
                _abort?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>How many output frames, from <paramref name="slot"/> on, show canvas frame <paramref name="frame"/> (more than one where the export's rate is above the canvas's).</summary>
    private int RepeatOf(long slot, long frame)
    {
        var timing = Command.Timing;
        var repeat = 1;
        while (slot + repeat < timing.OutputFrames && timing.CanvasFrameOf(slot + repeat) == frame && repeat < 1000)
            repeat++;
        return repeat;
    }

    /// <summary>Writes and publishes a scene: an export frame (exact frames, read back, stretched over the export's frame), or a source's probe.</summary>
    private long Publish(in Wanted wanted)
    {
        _publishedTo = _client.WorkerProcessId;
        _client.ClearScene();
        ref var header = ref _client.Scene;
        if (wanted.Source >= 0)
        {
            WriteProbe(ref header, wanted.Source, wanted.Media);
            return _client.Publish();
        }

        var layers = MemoryMarshal.CreateSpan(ref _client.Layer(0), CompositorProtocol.MaxLayers);
        var seconds = Command.Timing.CanvasSeconds(wanted.Frame);
        var count = _plan.Evaluate(seconds + FrameHair, ref header, layers);
        if (count >= CompositorProtocol.MaxLayers)
            throw new GpuExportException(string.Create(CultureInfo.InvariantCulture, $"At {seconds:0.000} s more layers are on at once than a GPU scene holds ({CompositorProtocol.MaxLayers})."));
        for (var i = 0; i < count; i++)
        {
            if (layers[i].EffectCount >= CompositorProtocol.MaxEffectsPerLayer)
                throw new GpuExportException(string.Create(CultureInfo.InvariantCulture, $"At {seconds:0.000} s a layer has more effect passes than a GPU scene holds ({CompositorProtocol.MaxEffectsPerLayer})."));
        }

        header.IsPlaying = 1; // the worker decodes ahead of a playing scene (its play-ahead): the export moves on as playback does
        (header.OutputWidth, header.OutputHeight) = (Command.Width, Command.Height);
        header.Flags |= FrameSceneHeader.FlagExactFrames | (wanted.Source == -2 ? 0 : FrameSceneHeader.FlagReadback);
        LeaveHints(seconds);
        return _client.Publish();
    }

    /// <summary>
    /// The first frames of every cut within the lookahead horizon, decoded ahead as the player's are (the plan's cut index,
    /// GpuHintWriter; 5.0-Release): written only when the cuts in the horizon change.
    /// </summary>
    private void LeaveHints(double seconds)
    {
        if (_hintWriter.Write(_plan, seconds, 1, _hints, out var count))
            _client.SetHints(_hints.AsSpan(0, count));
    }

    /// <summary>
    /// Waits for a scene's readback and copies it out, publishing it again when it does not come back in time (a worker
    /// that was taken over draws the newest scene by itself; this is for one that lost it). A scene a new worker drew
    /// (the standby after a crash or a TDR) is drawn again: a worker reads the source table only as it draws its first
    /// scene, so that scene can lack its videos. Returns the sequence read.
    /// </summary>
    private long Read(long sequence, in Wanted wanted, byte[] into, CancellationToken token)
    {
        var takenOver = 0;
        for (var attempt = 1; ; attempt++)
        {
            var since = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(since).TotalMilliseconds < FrameTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                if (!_client.TryReadback(sequence, into, out var width, out var height, 250))
                    continue;
                if (width != Command.Width || height != Command.Height)
                    throw new GpuExportException(string.Create(CultureInfo.InvariantCulture, $"The GPU compositor read back {width} x {height}, not {Command.Width} x {Command.Height}."));
                if (_client.WorkerProcessId == _publishedTo)
                    return sequence;

                if (++takenOver > 2 * TriesPerFrame)
                    throw new GpuExportException($"The GPU compositor kept failing ({_client.State}: {_client.Message}).");
                AppLog.Write("GPU export: the compositor was taken over; the frame is drawn again.");
                sequence = Publish(wanted);
                since = Stopwatch.GetTimestamp();
            }

            var what = wanted.Source >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"source {wanted.Source} at {wanted.Media:0.000} s")
                : string.Create(CultureInfo.InvariantCulture, $"the frame at {Command.Timing.CanvasSeconds(wanted.Frame):0.000} s");
            if (attempt >= TriesPerFrame)
                throw new GpuExportException($"The GPU compositor did not draw {what} in {TriesPerFrame} tries ({_client.State}: {_client.Message}).");
            AppLog.Write($"GPU export: {what} did not come back in {FrameTimeoutMs / 1000} s ({_client.State}); published again.");
            sequence = Publish(wanted);
        }
    }

    /// <summary>
    /// An empty scene, waited for, before anything else: the worker reads the source table as it draws (its first scene
    /// would draw no video), and makes its output textures at the export's size.
    /// </summary>
    private void WarmUp()
    {
        _client.ClearScene();
        ref var header = ref _client.Scene;
        (header.CanvasWidth, header.CanvasHeight) = (_plan.CanvasWidth, _plan.CanvasHeight);
        (header.OutputWidth, header.OutputHeight) = (Command.Width, Command.Height);
        if (!_client.WaitForFrame(_client.Publish(), StartTimeoutMs))
            throw new GpuExportException($"The GPU compositor drew nothing ({_client.State}: {_client.Message}).");
    }

    /// <summary>
    /// Every frame drawn must have shown each media layer's exact frame: the worker counts those it drew with a stand-in
    /// (its exact wait ran out), after the frame's readback, so a count that grew is seen by the next read at the latest.
    /// </summary>
    private void CheckExact()
    {
        var approximate = Volatile.Read(ref _client.Memory.Control.ApproximatePresents);
        if (approximate > _approximateSeen)
            throw new GpuExportException("The GPU compositor drew a frame without its exact video frame (its decoder could not reach it in 20 s).");
        _approximateSeen = approximate; // a new worker may count from nothing again
    }

    // ----- The sources, each drawn once first -----

    /// <summary>
    /// Every video the export shows, drawn once alone (at its first moment in the export, over the whole frame, on a
    /// magenta background) before anything is written: a file the worker's decoder cannot open is left out of its frames
    /// without a word (they still count as exact), which an export must not be.
    /// </summary>
    private void ProbeSources(CancellationToken token)
    {
        var timing = Command.Timing;
        var seen = new bool[CompositorProtocol.MaxSources];
        var probes = new List<GpuMediaHint>();
        var last = -1L;
        for (var slot = 0L; slot < timing.OutputFrames; slot++)
        {
            if ((slot & 4095) == 0)
                token.ThrowIfCancellationRequested();
            var frame = timing.CanvasFrameOf(slot);
            if (frame == last)
                continue;
            last = frame;
            var found = _plan.MediaAt(timing.CanvasSeconds(frame) + FrameHair, _media);
            for (var i = 0; i < found; i++)
            {
                if (_media[i].SourceId is >= 0 and < CompositorProtocol.MaxSources && !seen[_media[i].SourceId])
                {
                    seen[_media[i].SourceId] = true;
                    probes.Add(_media[i]);
                }
            }
        }

        foreach (var probe in probes)
        {
            var wanted = new Wanted(0, probe.SourceId, probe.MediaSeconds);
            Read(Publish(wanted), wanted, _buffers[0], token);
            if (Magenta(_buffers[0], Command.Width, Command.Height))
            {
                throw new GpuExportException(string.Create(CultureInfo.InvariantCulture,
                    $"The GPU compositor cannot decode a video of the export (source {probe.SourceId} at {probe.MediaSeconds:0.000} s: {_client.Message})."));
            }
        }
    }

    /// <summary>A probe's scene: the source alone, its whole picture over the whole frame, opaque, on magenta.</summary>
    private void WriteProbe(ref FrameSceneHeader header, int source, double media)
    {
        (header.CanvasWidth, header.CanvasHeight) = (_plan.CanvasWidth, _plan.CanvasHeight);
        (header.OutputWidth, header.OutputHeight) = (Command.Width, Command.Height);
        (header.BackgroundR, header.BackgroundG, header.BackgroundB) = (1, 0, 1);
        header.Flags = FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback;
        header.ActiveLayerCount = 1;

        ref var layer = ref _client.Layer(0);
        layer.ClipKey = ProbeKey + source;
        (layer.SourceId, layer.SourceKind, layer.MediaSeconds) = (source, (int)GpuSourceKind.Media, media);
        (layer.CropLeft, layer.CropTop, layer.CropRight, layer.CropBottom) = (0, 0, 1, 1);
        (layer.X, layer.Y, layer.Width, layer.Height) = (0, 0, _plan.CanvasWidth, _plan.CanvasHeight);
        (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
        (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
        layer.Flags = GpuLayerDescriptor.FlagVisible;
    }

    /// <summary>Whether a probe's frame is the magenta background everywhere it was sampled (8 x 8 points): its video was not drawn.</summary>
    private static bool Magenta(byte[] bgra, int width, int height)
    {
        for (var gy = 0; gy < 8; gy++)
        {
            for (var gx = 0; gx < 8; gx++)
            {
                var (x, y) = ((2 * gx + 1) * width / 16, (2 * gy + 1) * height / 16);
                var at = ((long)y * width + x) * 4;
                if (bgra[at] < 252 || bgra[at + 1] > 3 || bgra[at + 2] < 252)
                    return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // The render thread reads the control plane: the compositor goes only once it has stopped.
        if (_renderer is { IsAlive: true })
        {
            AppLog.Write("GPU export: its compositor is left running (the render thread is still in it); it ends with HandPeg.");
            return;
        }

        _broker.Dispose();
        _client.Dispose();
        _free.Dispose();
        _filled.Dispose();
    }
}
