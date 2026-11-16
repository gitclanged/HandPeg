using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using CliWrap;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>
/// Export failure (compositor start, undecodable video, render failure, table overflow); nothing usable was written; message explains.
/// </summary>
public sealed class GpuExportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// GPU export pipeline: the compositor renders every frame (fades, captions, burned subtitles) exactly as the preview; the audio
/// engine renders every track (ExportSoundPlan); FFmpeg only encodes and muxes (EncoderCommand never writes a filter graph), or
/// GifEncoder writes GIFs.
/// <list type="bullet">
/// <item><b>Dedicated compositor.</b> Own worker plus standby (TDR mid-export is taken over and the frame re-rendered) on the export
/// adapter, with its own tables (<see cref="GpuResourceBroker"/>) and plan (<see cref="GpuScenePlan"/>, built on the UI thread).
/// The player engine is untouched.</item>
/// <item><b>Exact frames.</b> Scenes request exact frames (<see cref="FrameSceneHeader.FlagExactFrames"/>), stretched without letterbox,
/// read back as GPU-converted NV12 (<see cref="FrameSceneHeader.FlagReadback"/>) or P010 for HDR (<see cref="FrameSceneHeader.FlagReadbackP010"/>)
/// and validated: stand-in frames, layer overflow or undecodable videos (each pre-rendered alone over magenta) abort with a message.</item>
/// <item><b>Pipelined.</b> The render thread publishes, waits, copies out and publishes the next; the pump feeds FFmpeg stdin (or the
/// GIF encoder) from the other buffer, converting NV12 to I420. Double-buffered; skipped canvas frames are never rendered, repeated
/// ones rendered once (<see cref="GpuExportTiming"/>).</item>
/// <item><b>Bounded retries.</b> A frame not read back within <see cref="FrameTimeoutMs"/> is republished up to <see cref="TriesPerFrame"/>
/// times, then the export fails (<see cref="GpuExportException"/>). If the compositor cannot start, WARP is used. Nothing escapes the render thread.</item>
/// </list>
/// <para>Threads: <see cref="Prepare"/> on an STA thread (WPF text rendering); <see cref="StartCompositor"/> off the UI thread (blocks on the
/// worker); <see cref="RunAsync"/> from any thread.</para>
/// </summary>
public sealed class GpuExport : IDisposable
{
    /// <summary>Per-frame readback timeout (ms); the worker itself waits up to 20 s for an exact frame.</summary>
    public const int FrameTimeoutMs = 25_000;

    /// <summary>How many times a frame that did not come back is published again before the export gives up.</summary>
    public const int TriesPerFrame = 3;

    /// <summary>How long the worker may take to start, ms.</summary>
    public const int StartTimeoutMs = 10_000;

    /// <summary>
    /// Export worker cache: enough for play-ahead (0.35 s of 4K) and the next cut, small enough to leave VRAM for the player worker and
    /// encoder (also capped by the OS budget).
    /// </summary>
    public const long CacheBudgetBytes = 768L << 20;

    /// <summary>
    /// Sampling offset into each frame (s): container timestamps are rounded (1 ms in Matroska) and lookup takes the frame at or
    /// before the time, so sampling slightly inside avoids picking the previous frame (matches the scene differential test).
    /// </summary>
    public const double FrameHair = 0.0005;

    private const int Buffers = 2;

    /// <summary>Caption pre-render lookahead (s); passed captions are evicted as the table fills.</summary>
    private const double CaptionLookaheadSeconds = 2;

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

    // Per-frame audio band map for audio-reactive effects; null when none pulse.
    private readonly AudioEnergyMap? _energies;

    private GpuExport(CompositorClient client, GpuResourceBroker broker, GpuScenePlan plan, GpuExportCommand command, AudioEnergyMap? energies)
    {
        (_client, _broker, _plan, Command, _energies) = (client, broker, plan, command, energies);
        for (var i = 0; i < Buffers; i++)
            _buffers[i] = GC.AllocateUninitializedArray<byte>((int)command.FrameBytes);
    }

    /// <summary>The FFmpeg command the frames are piped to.</summary>
    public GpuExportCommand Command { get; }

    /// <summary>How many frames have been handed to FFmpeg so far.</summary>
    public long FramesPiped => Interlocked.Read(ref _piped);

    // ----- Whether, and getting ready -----

    /// <summary>
    /// Reason the state cannot be exported (frame/timing via <see cref="EncoderCommand.WhyNot"/>, or table overflow); null when exportable.
    /// </summary>
    public static string? WhyNot(SequenceExportState state)
    {
        if (EncoderCommand.WhyNot(state) is { } reason)
            return reason;

        var lowered = TimelineLowering.LowerForGpu(state);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textures = state.LutPath.Trim().Trim('"').Length > 0 ? 1 : 0;
        foreach (var layer in lowered.Stack)
        {
            // (A twin is its video's blurred copy, of its file; a hidden or unusable layer is drawn by neither.)
            if (lowered.BlurTwins.Contains(layer) || !layer.IsUsable)
                continue;
            if (!layer.EffectsBypassed)
                textures += EffectTextures(layer.Effects);
            if (layer.IsVideoFile)
                files.Add(layer.ImagePath.Trim().Trim('"'));
            if (layer.IsImage || layer.IsText)
                textures++;
            if (layer.CustomMask)
                textures++;
        }

        textures += EffectTextures(lowered.SequenceEffects) + EffectTextures(state.BackgroundEffects.Select(EffectInstance.FromState));

        // Every clip references its own file; there is no primary source.
        if (files.Count > CompositorProtocol.MaxSources)
            return string.Create(CultureInfo.InvariantCulture, $"{files.Count} video files are more than the compositor's source table holds ({CompositorProtocol.MaxSources})");
        if (textures > CompositorProtocol.MaxResources - 4)
            return string.Create(CultureInfo.InvariantCulture, $"some {textures} pictures, titles, masks and colour tables are more than the compositor's resource table holds");
        return null;
    }

    /// <summary>
    /// Names of features the export will omit (the compositor is the only renderer): unknown or shaderless effects, a second LUT or
    /// Curves on one clip, unsupported clip kinds, unreadable captions. Empty when complete.
    /// </summary>
    public static IReadOnlyList<string> LeftOut(SequenceExportState state)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Look(string owner, IEnumerable<EffectInstance> effects)
        {
            var (luts, curves) = (0, 0);
            foreach (var effect in effects)
            {
                if (!effect.Enabled)
                    continue;
                if (effect.Definition is not { } definition)
                    names.Add($"{owner}'s unknown effect ({effect.Type})");
                else if (definition.GpuSlot == GpuShaderSlot.None)
                    names.Add($"{owner}'s {definition.Name}");
                else
                {
                    // Count LUT and curve tables separately (Color Grade can use both) so a dropped LUT cannot mask a dropped Curves card.
                    // A Color Grade card only loses its table and is reported as such.
                    var lostLut = HoldsLut(effect, definition) && ++luts > 1;
                    var lostCurve = HoldsCurves(effect, definition) && ++curves > 1;
                    if (lostLut || lostCurve)
                        names.Add(definition.GpuSlot == GpuShaderSlot.ColorStudio
                            ? $"{owner}'s Color Grade {(lostLut && lostCurve ? "LUT and curves" : lostLut ? "LUT" : "curves")}"
                            : $"{owner}'s second {definition.Name}");
                }
            }
        }

        var lowered = TimelineLowering.LowerForGpu(state);
        foreach (var layer in lowered.Stack)
        {
            if (lowered.BlurTwins.Contains(layer) || !layer.IsUsable)
                continue;
            if (layer.Kind is not (LayerKind.VideoFile or LayerKind.Image or LayerKind.Text or LayerKind.Adjustment))
                names.Add($"{layer.Name} (a {layer.Kind} clip)");
            else if (!layer.EffectsBypassed)
                Look(layer.Name, layer.Effects);
        }

        Look("the Sequence Output", lowered.SequenceEffects);
        Look("the blur fill", state.BackgroundEffects.Select(EffectInstance.FromState));
        if (state.AutoCaptions && !EncoderCommand.IsPictureOnly(state) && CaptionsOf(state) is null && File.Exists(state.CaptionsFilePath))
            names.Add("the captions (their words could not be read: make them again)");
        return [.. names];
    }

    // The colour tables a stack of effects needs: one LUT and one Curves at most (the descriptor holds one of each).
    private static int EffectTextures(IEnumerable<EffectInstance> effects)
    {
        var (luts, curves) = (0, 0);
        foreach (var effect in effects)
        {
            if (effect is { Enabled: true, Definition: { } definition })
                (luts, curves) = (luts + (HoldsLut(effect, definition) ? 1 : 0), curves + (HoldsCurves(effect, definition) ? 1 : 0));
        }

        return Math.Min(luts, 1) + Math.Min(curves, 1);
    }

    // True when a card needs the layer's LUT or curve table (LUT, Curves, or Color Grade with a LUT file or curve).
    private static bool HoldsLut(EffectInstance effect, EffectDefinition definition) => definition.GpuSlot == GpuShaderSlot.Lut3D
        || definition.GpuSlot == GpuShaderSlot.ColorStudio && effect.GetText(ColorGradeKeys.Lut).Trim().Trim('"').Length > 0;

    private static bool HoldsCurves(EffectInstance effect, EffectDefinition definition) => definition.GpuSlot == GpuShaderSlot.ToneCurve
        || definition.GpuSlot == GpuShaderSlot.ColorStudio && ColorGradeMath.CurvesActive(effect);

    /// <summary>
    /// Encode command for the command box (audio renders as tokens until produced); null with a reason when not exportable.
    /// </summary>
    public static GpuExportCommand? CommandFor(SequenceExportState state, out string whyNot)
    {
        // Audio Only: Sequence-length audio on the canvas clock; no frames, so frame size and compositor tables are irrelevant.
        if (EncoderCommand.IsAudioOnly(state))
        {
            var plan = ExportSoundPlan.For(state, pictureOnly: false, audioOnly: true);
            var silent = EncoderCommand.WhyNotAudio(state, plan);
            if (silent is not null || EncoderCommand.Timing(state with { TargetFramerate = null }) is not { } length)
            {
                whyNot = silent ?? "the sequence's length cannot be followed";
                return null;
            }

            whyNot = "";
            return EncoderCommand.BuildAudioOnly(state, plan, length);
        }

        if (WhyNot(state) is { } reason || EncoderCommand.Timing(state) is not { } timing)
        {
            whyNot = WhyNot(state) ?? "the export's frame rate cannot be followed";
            return null;
        }

        whyNot = "";
        var sound = ExportSoundPlan.For(state, EncoderCommand.IsPictureOnly(state));
        return EncoderCommand.Build(state, sound, timing, captionsOnGpu: state.AutoCaptions && CaptionsOf(state) is not null);
    }

    /// <summary>
    /// Export adapter: <see cref="AppSettings.ExportGpuAdapter"/> if set and present in <paramref name="cards"/>, else the Compositor GPU
    /// (<see cref="AppSettings.CompositorGpuAdapter"/>, -1 = Auto). Off the UI thread only (DXGI enumeration, never before Frame 1).
    /// </summary>
    internal static int ExportAdapter(AppSettings settings, IReadOnlyList<GpuAdapter> cards) =>
        settings.ExportGpuAdapter >= 0 && cards.Any(c => c.Index == settings.ExportGpuAdapter) ? settings.ExportGpuAdapter : settings.CompositorGpuAdapter;

    /// <summary>
    /// Starts an export compositor with standby on <see cref="ExportAdapter"/> and blocks until ready (off the UI thread). Falls back to
    /// the Compositor GPU, then WARP. Returns null with a reason when none starts.
    /// </summary>
    public static CompositorClient? StartCompositor(out string whyNot)
    {
        var settings = AppSettings.Current;
        var adapter = ExportAdapter(settings, GpuAdapters.All);
        if (Start(adapter, out whyNot) is { } client)
            return client;
        if (adapter != settings.CompositorGpuAdapter)
        {
            AppLog.Write($"GPU export: on the Export GPU {whyNot}; the Compositor GPU draws it instead.");
            if (Start(settings.CompositorGpuAdapter, out var onCompositor) is { } fallback)
                return fallback;
            whyNot = $"{whyNot}, and on the Compositor GPU too ({onCompositor})";
            adapter = settings.CompositorGpuAdapter;
        }

        if (GpuAdapters.WarpIndex is not { } warp || warp == adapter)
            return null;

        AppLog.Write($"GPU export: {whyNot}; the processor's software renderer (WARP) draws it instead.");
        var first = whyNot;
        client = Start(warp, out whyNot);
        if (client is null)
            whyNot = $"{first}, and the software renderer did not start either ({whyNot})";
        return client;

        static CompositorClient? Start(int adapter, out string whyNot)
        {
            var settings = AppSettings.Current;
            CompositorClient? client = null;
            try
            {
                client = new CompositorClient(adapter, keepStandby: true, CacheBudgetBytes, settings.GpuDecodeThreads, announcesRecovery: false);
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
                AppLog.Write("GPU export: the compositor could not be started", ex);
                whyNot = $"the GPU compositor could not be started: {ex.Message}";
                client?.Dispose();
                return null;
            }
        }
    }

    /// <summary>Caption word timings from beside the caption file (CaptionGenerator.ReadWords); null when absent.</summary>
    private static CaptionGenerator.CaptionWords? CaptionsOf(SequenceExportState state) =>
        state.AutoCaptions ? CaptionGenerator.ReadWords(state.CaptionsFilePath) : null;

    /// <summary>
    /// Plans an export on an STA thread (WPF text): tables, scene plan (fades and <paramref name="captions"/> on the Sequence clock)
    /// and readback section. Takes ownership of <paramref name="client"/> on success; null with a reason otherwise.
    /// <paramref name="energies"/> is the per-frame audio band map (AudioReactive.MapOf), or null.
    /// </summary>
    public static GpuExport? Prepare(SequenceExportState state, GpuExportCommand command, CompositorClient client, out string whyNot,
        CaptionGenerator.CaptionWords? captions = null, AudioEnergyMap? energies = null)
    {
        GpuResourceBroker? broker = null;
        try
        {
            broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
            var drawsCaptions = command.CaptionsOnGpu && captions is not null && state.AutoCaptions;
            if (drawsCaptions)
            {
                broker.Text.SetCaptions(captions!.Lines, captions.Style, captions.TextScale);
            }

            var plan = GpuScenePlan.Create(state with { AutoCaptions = drawsCaptions }, broker);
            if (ReferenceEquals(plan, GpuScenePlan.Empty))
            {
                whyNot = "the GPU scene could not be planned (the log says why)";
                broker.Dispose();
                return null;
            }

            if (plan.CanvasWidth != command.CanvasWidth || plan.CanvasHeight != command.CanvasHeight)
            {
                whyNot = string.Create(CultureInfo.InvariantCulture, $"the GPU scene's frame ({plan.CanvasWidth} x {plan.CanvasHeight}) is not the export's ({command.CanvasWidth} x {command.CanvasHeight})");
                broker.Dispose();
                return null;
            }

            // Room for BGRA whatever is piped: each source's probe comes back in it.
            client.EnsureReadback((long)Math.Max(command.Width, command.CanvasWidth) * Math.Max(command.Height, command.CanvasHeight) * 4);
            whyNot = "";
            return new GpuExport(client, broker, plan, command, energies);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU export: it could not be prepared", ex);
            whyNot = $"the GPU export could not be prepared: {ex.Message}";
            broker?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Full export from the UI thread: prepares burned subtitles and audio renders (off-thread), starts the compositor, renders and
    /// pipes frames. <paramref name="arguments"/> overrides the command when hand-edited (audio tokens substituted); frames still come
    /// from the state. Throws <see cref="GpuExportException"/> (render/plan failure), <see cref="InvalidOperationException"/> (FFmpeg) or
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task RunAsync(SequenceExportState state, IReadOnlyList<string>? arguments, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (CommandFor(state, out var why) is not { } command)
            throw new GpuExportException($"This export cannot be made: {why}.");
        if (command.Encoder == ExportEncoder.Sound)
        {
            // Audio Only: no compositor, no frames.
            await RunAudioOnlyAsync(state, arguments, progress, cancellationToken);
            return;
        }

        if (arguments is not null)
            command = command with { Arguments = arguments };

        // Source subtitles to burn in, mapped to clip times like a picture subtitle track.
        if (state.BurnedSubtitleStreams.Count > 0 && !EncoderCommand.IsPictureOnly(state))
        {
            progress.Report(new FfmpegProgress("Reading the subtitles to burn in...", null, null));
            var burned = await BurnedSubtitles.PrepareAsync(state, cancellationToken);
            state = state with { PictureSubtitles = burned };
        }

        // The sound first, by the engine that plays it (FFmpeg only encodes it): off this thread, it decodes.
        var rendered = await RenderSoundAsync(state, command, progress, cancellationToken);
        try
        {
            command = command with { Sound = rendered };

            // Per-frame audio bands for audio-reactive effects, matching the player's paused frames.
            var energies = await EnergiesAsync(state, 0, double.PositiveInfinity, progress, cancellationToken);
            progress.Report(new FfmpegProgress("Starting the GPU compositor...", null, null));
            var (client, started) = await Task.Run(() => (StartCompositor(out var whyNot), whyNot), cancellationToken);
            if (client is null)
                throw new GpuExportException($"The export could not be made: {started}.");

            // Back on the caller's thread (the UI's): the scene is planned there.
            if (Prepare(state, command, client, out why, CaptionsOf(state), energies: energies) is not { } export)
            {
                client.Dispose();
                throw new GpuExportException($"The export could not be made: {why}.");
            }

            await RunPreparedAsync(export, client, progress, cancellationToken);
        }
        finally
        {
            ExportSoundPlan.DeleteRenders(rendered);
        }
    }

    /// <summary>
    /// Audio Only export (FLAC, WAV, MP3): renders the mix off-thread, then FFmpeg encodes it with no stdin; no compositor or subtitles.
    /// <paramref name="arguments"/> overrides the command when hand-edited. Throws <see cref="GpuExportException"/>,
    /// <see cref="InvalidOperationException"/> (FFmpeg) or <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task RunAudioOnlyAsync(SequenceExportState state, IReadOnlyList<string>? arguments, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (CommandFor(state, out var why) is not { Encoder: ExportEncoder.Sound } command)
            throw new GpuExportException($"This export cannot be made: {(why.Length > 0 ? why : "it is not an audio-only file")}.");
        if (arguments is not null)
            command = command with { Arguments = arguments };

        var rendered = await RenderSoundAsync(state, command, progress, cancellationToken);
        try
        {
            // Run like a Copy export: resolved executable, validated arguments, progress from time= lines.
            var line = string.Join(" ", EncoderCommand.WithRenders(command.Arguments, rendered).Prepend(command.Executable).Select(EncoderCommand.QuoteArgument));
            progress.Report(new FfmpegProgress("Encoding the sound...", null, null));
            var clock = Stopwatch.StartNew();
            await FfmpegRunner.RunAsync(line, progress, cancellationToken);
            AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Audio Only export: \"{Path.GetFileName(command.OutputPath)}\" encoded in {clock.Elapsed.TotalSeconds:0.0} s, no compositor started."));
        }
        finally
        {
            ExportSoundPlan.DeleteRenders(rendered);
        }
    }

    // The export itself, once it is planned.
    private static async Task RunPreparedAsync(GpuExport export, CompositorClient client, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
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
    }

    /// <summary>
    /// Renders the command's referenced audio tracks (ExportSoundPlan), each exactly the video length; stream copies are left to FFmpeg.
    /// </summary>
    private static async Task<IReadOnlyList<ExportSoundTrack>> RenderSoundAsync(SequenceExportState state, GpuExportCommand command, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (command.Sound.Count == 0 || command.Sound.All(t => t.IsCopy))
            return command.Sound;

        var plan = ExportSoundPlan.For(state, EncoderCommand.IsPictureOnly(state), command.Encoder == ExportEncoder.Sound);
        var frames = (long)Math.Round(command.Timing.OutputSeconds(command.Timing.OutputFrames) * plan.SampleRate);
        progress.Report(new FfmpegProgress("Rendering the sound...", null, null));
        var clock = Stopwatch.StartNew();
        var rendered = await Task.Run(() => plan.Render(state, 0, frames, null, cancellationToken), cancellationToken);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"GPU export: {rendered.Count(t => !t.IsCopy)} sound track(s), {frames / (double)plan.SampleRate:0.0} s each, rendered in {clock.Elapsed.TotalSeconds:0.0} s."));
        return rendered;
    }

    /// <summary>
    /// Audio band map over a Sequence range (AudioReactive.MapOf), built off-thread; null when nothing pulses or on failure (logged).
    /// </summary>
    private static async Task<AudioEnergyMap?> EnergiesAsync(SequenceExportState state, double fromSeconds, double toSeconds, IProgress<FfmpegProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!AudioReactive.Pulses(state))
            return null;

        progress.Report(new FfmpegProgress("Listening to the sound for the audio-reactive effects...", null, null));
        try
        {
            return await Task.Run(() => AudioReactive.MapOf(state, fromSeconds, toSeconds, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            AppLog.Write("GPU export: the sound's bands could not be measured; the audio-reactive effects do not pulse", ex);
            return null;
        }
    }

    // ----- Running -----

    /// <summary>
    /// Renders and encodes: starts the render thread, waits for the first frame (after every source is validated), then runs FFmpeg
    /// with piped frames. Throws <see cref="OperationCanceledException"/>, <see cref="GpuExportException"/> or <see cref="InvalidOperationException"/> (FFmpeg).
    /// </summary>
    public async Task RunAsync(IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer is not null)
            throw new InvalidOperationException("A GPU export runs once.");

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _abort = abort;
        // STA: WPF renders captions on this thread as the export reaches them; it owns the broker from here on.
        _renderer = new Thread(Render) { Name = "GPU export", IsBackground = true };
        _renderer.SetApartmentState(ApartmentState.STA);
        _renderer.Start();
        try
        {
            // The pump (FFmpeg pipes, I420 deinterleave, GIF quantizer) must run on the pool, never the UI dispatcher: Task.Run rather than
            // ConfigureAwait(false), since a synchronously completed first-frame wait would stay on the caller. Progress<T> still marshals
            // to the UI; OnLine locks its queue because stdout and stderr may arrive on two pool threads.
            await Task.Run(async () =>
            {
                await _firstFrame.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (Command.Encoder == ExportEncoder.Gif)
                    await RunGifAsync(progress, abort.Token).ConfigureAwait(false);
                else
                    await RunFfmpegAsync(progress, abort.Token).ConfigureAwait(false);
            }, CancellationToken.None);
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
            // Join off the UI thread (frame waits honour abort, so this takes ms unless the compositor hangs). A thread that fails to stop
            // keeps its compositor until process exit (ThreadStop logs it).
            if (_renderer.IsAlive)
            {
                var renderer = _renderer;
                _ = await Task.Run(() => ThreadStop.Join(renderer, 10_000, "GPU export render thread"));
            }

            _abort = null;
        }
    }

    /// <summary>
    /// Runs FFmpeg like FfmpegRunner (validated arguments) with frames on stdin. EncoderCommand writes no filter graph; a hand-edited
    /// command or Extra options may include user-typed filters, passed verbatim on the command line within Windows' length limit.
    /// </summary>
    private async Task RunFfmpegAsync(IProgress<FfmpegProgress> progress, CancellationToken token)
    {
        var executable = FfmpegRunner.ResolveExecutable(Command.Executable);

        // Each sound track's token gives way to its render.
        var arguments = EncoderCommand.WithRenders(Command.Arguments, Command.Sound);
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
            .WithStandardInputPipe(PipeSource.Create((stdin, pipeToken) => PipeFramesAsync((frame, t) => stdin.WriteAsync(frame, t), progress, pipeToken, stdin)))
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

    /// <summary>How far into the export the frames handed to FFmpeg reach (the progress bar's position).</summary>
    private TimeSpan PipedTime() => TimeSpan.FromSeconds(Command.Timing.OutputSeconds(Interlocked.Read(ref _piped)));

    /// <summary>
    /// Built-in GIF encoder: accumulates all frames into one global palette, then writes a looping GIF at the export rate.
    /// </summary>
    private async Task RunGifAsync(IProgress<FfmpegProgress> progress, CancellationToken token)
    {
        using var gif = new GifEncoder(Command.Width, Command.Height, SessionPaths.Previews);
        await PipeFramesAsync((frame, _) =>
        {
            gif.Add(frame.Span);
            return ValueTask.CompletedTask;
        }, progress, token);
        token.ThrowIfCancellationRequested();

        progress.Report(new FfmpegProgress($"Writing the GIF ({gif.FrameCount} frames)...", null, null));
        var path = Command.OutputPath;
        var part = path + ".part";
        await Task.Run(() => gif.Write(part, Command.Timing.OutputFramesPerSecond, null, token), token);
        File.Move(part, path, overwrite: true);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"GPU export: a GIF of {gif.FrameCount} frames, {new FileInfo(path).Length / 1024.0:0} KB."));
    }

    /// <summary>
    /// Frame sink (FFmpeg stdin or GIF encoder): writes each handed-over frame its repeat count times (NV12 to I420 for yuv420p encoders),
    /// then returns the buffer. If FFmpeg stops reading, this ends quietly; FFmpeg's exit status explains why.
    /// </summary>
    private async Task PipeFramesAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, IProgress<FfmpegProgress> progress, CancellationToken token, Stream? flush = null)
    {
        var bytes = (int)Command.FrameBytes;
        var planar = Command.PipeFormat == EncoderCommand.PipeI420 ? GC.AllocateUninitializedArray<byte>(bytes) : null;
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
                var frame = _buffers[buffer].AsMemory(0, bytes);
                if (planar is not null)
                {
                    ToI420(_buffers[buffer], planar, Command.Width, Command.Height);
                    frame = planar.AsMemory(0, bytes);
                }

                for (var i = 0; i < _repeats[buffer]; i++)
                {
                    await write(frame, token);
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

            if (flush is not null)
                await flush.FlushAsync(token);
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

    /// <summary>NV12 (a luma plane, then the chroma interleaved) as I420 (the same luma, then each chroma plane on its own).</summary>
    internal static void ToI420(byte[] nv12, byte[] i420, int width, int height)
    {
        var luma = width * height;
        var quarter = (width / 2) * (height / 2);
        Buffer.BlockCopy(nv12, 0, i420, 0, luma);
        var chroma = nv12.AsSpan(luma, 2 * quarter);
        var u = i420.AsSpan(luma, quarter);
        var v = i420.AsSpan(luma + quarter, quarter);
        for (var i = 0; i < quarter; i++)
        {
            u[i] = chroma[2 * i];
            v[i] = chroma[2 * i + 1];
        }
    }

    // ----- The render thread -----

    /// <summary>Scene request: canvas frame <see cref="Frame"/>, or (with <see cref="Source"/> >= 0) a single-source probe at <see cref="Media"/>.</summary>
    private readonly record struct Wanted(long Frame, int Source, double Media);

    private void Render()
    {
        var token = _abort!.Token;
        try
        {
            WarmUp(token);
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
                    if (!_client.WaitForFrame(closing, FrameTimeoutMs, token.WaitHandle))
                    {
                        token.ThrowIfCancellationRequested();
                        throw new GpuExportException($"The GPU compositor did not finish the export's last frame ({_client.State}: {_client.Message}).");
                    }

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
            // Never propagate: fail the export and stop FFmpeg; the caller reports the error.
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
        finally
        {
            // WPF made a dispatcher for this thread if it drew captions here: it ends with the thread.
            System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
    }

    /// <summary>Output frames from <paramref name="slot"/> showing canvas frame <paramref name="frame"/> (more than one when export rate exceeds canvas rate).</summary>
    private int RepeatOf(long slot, long frame)
    {
        var timing = Command.Timing;
        var repeat = 1;
        while (slot + repeat < timing.OutputFrames && timing.CanvasFrameOf(slot + repeat) == frame && repeat < 1000)
            repeat++;
        return repeat;
    }

    /// <summary>Writes and publishes a scene: an export frame (exact, read back, stretched) or a source probe.</summary>
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

        // Sample slightly inside the frame but never past the last clip's end (an off-grid Sequence end otherwise rendered black).
        var at = Command.Timing.SampleSeconds(wanted.Frame, FrameHair);

        // Pre-render upcoming captions; an unrenderable caption (table full, no words) fails the export rather than silently dropping it.
        if (_plan.HasCaptions && !_plan.PrepareCaptions(at, seconds + CaptionLookaheadSeconds))
            throw new GpuExportException(string.Create(CultureInfo.InvariantCulture, $"The caption at {seconds:0.000} s could not be drawn for the GPU export."));

        var count = _plan.Evaluate(at, ref header, layers);
        if (count >= CompositorProtocol.MaxLayers)
            throw new GpuExportException(string.Create(CultureInfo.InvariantCulture, $"At {seconds:0.000} s more layers are on at once than a GPU scene holds ({CompositorProtocol.MaxLayers})."));
        for (var i = 0; i < count; i++)
        {
            if (layers[i].EffectCount >= CompositorProtocol.MaxEffectsPerLayer)
                throw new GpuExportException(string.Create(CultureInfo.InvariantCulture, $"At {seconds:0.000} s a layer has more effect passes than a GPU scene holds ({CompositorProtocol.MaxEffectsPerLayer})."));
        }

        header.IsPlaying = 1; // enables worker play-ahead decoding, since the export advances like playback
        (header.OutputWidth, header.OutputHeight) = (Command.Width, Command.Height);

        // Audio bands at the frame time for audio-reactive effects; 0 when unknown.
        WriteBands(ref header, _energies, at - FrameHair);
        header.Flags |= FrameSceneHeader.FlagExactFrames | (wanted.Source == -2 ? 0 : FrameSceneHeader.FlagReadback
            | (Command.PipesP010 ? FrameSceneHeader.FlagReadbackP010 : Command.PipesNv12 ? FrameSceneHeader.FlagReadbackNv12 : 0));
        LeaveHints(seconds);
        return _client.Publish();
    }

    /// <summary>
    /// Writes audio band values at the frame time into the header (shared lookup with GpuPreviewEngine, same sampling offset); unchanged when unknown.
    /// </summary>
    internal static bool WriteBands(ref FrameSceneHeader header, AudioEnergyMap? energies, double seconds)
    {
        if (energies is not { } map || !map.TryAt(seconds + FrameHair, out var bass, out var mid, out var high))
            return false;

        (header.AudioBass, header.AudioMid, header.AudioHigh) = (bass, mid, high);
        return true;
    }

    /// <summary>
    /// Writes cut pre-decode hints within the lookahead horizon (cut index, GpuHintWriter), only when the set changes.
    /// </summary>
    private void LeaveHints(double seconds)
    {
        if (_hintWriter.Write(_plan, seconds, 1, _hints, out var count))
            _client.SetHints(_hints.AsSpan(0, count));
    }

    /// <summary>
    /// Waits for a scene readback and copies it out, republishing on timeout. A scene rendered by a fresh worker (standby after crash/TDR)
    /// is re-rendered because its first scene may lack videos (source table read on first draw). Returns the sequence read.
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
    /// Publishes and awaits an empty warm-up scene so the worker loads the source table and creates output textures at export size.
    /// </summary>
    private void WarmUp(CancellationToken token)
    {
        _client.ClearScene();
        ref var header = ref _client.Scene;
        (header.CanvasWidth, header.CanvasHeight) = (_plan.CanvasWidth, _plan.CanvasHeight);
        (header.OutputWidth, header.OutputHeight) = (Command.Width, Command.Height);
        if (!_client.WaitForFrame(_client.Publish(), StartTimeoutMs, token.WaitHandle))
        {
            token.ThrowIfCancellationRequested();
            throw new GpuExportException($"The GPU compositor drew nothing ({_client.State}: {_client.Message}).");
        }
    }

    /// <summary>
    /// Verifies no stand-in frames were used: the worker's stand-in counter is updated after readback, so growth is seen by the next read at the latest.
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
    /// Pre-renders each exported video alone over magenta at its first use before writing anything: an undecodable file would
    /// otherwise be silently omitted (frames still report exact).
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
            var found = _plan.MediaAt(timing.SampleSeconds(frame, FrameHair), _media);
            for (var i = 0; i < found; i++)
            {
                if (_media[i].SourceId is >= 0 and < CompositorProtocol.MaxSources && !seen[_media[i].SourceId])
                {
                    seen[_media[i].SourceId] = true;
                    probes.Add(_media[i]);
                }
            }
        }

        byte[]? looked = null;
        foreach (var probe in probes)
        {
            var wanted = new Wanted(0, probe.SourceId, probe.MediaSeconds);
            // BGRA, whatever is piped: a probe is looked at, not encoded.
            looked ??= GC.AllocateUninitializedArray<byte>(Command.Width * Command.Height * 4);
            Read(Publish(wanted), wanted, looked, token);
            if (Magenta(looked, Command.Width, Command.Height))
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
