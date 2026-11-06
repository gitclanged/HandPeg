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
/// An export that could not be made (the compositor would not start, a video it cannot decode, a frame it would not draw, more
/// than its tables hold): nothing usable was written, and the message says why.
/// </summary>
public sealed class GpuExportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The export (Pass 5.0-AudioAnd3D; since 5.2-Audit the only one): the compositor that draws the player renders every frame,
/// fades, captions and the source's burned subtitles included, where the preview draws them; the sound engine renders every
/// sound track (ExportSoundPlan); FFmpeg encodes and muxes, nothing else (EncoderCommand: no filter graph is ever written), or
/// HandPeg's own GIF encoder does (GifEncoder).
/// <list type="bullet">
/// <item><b>Its own compositor.</b> A worker of its own (and a standby beside it, so a TDR mid-export is taken over and
/// the frame drawn again), on the compositor's card, with its own tables (<see cref="GpuResourceBroker"/>, every texture made
/// at once) and its own plan of the export's state (<see cref="GpuScenePlan"/>, on the UI thread). The player's engine is
/// left alone.</item>
/// <item><b>Exact frames.</b> Each scene asks the worker for every media layer's exact frame
/// (<see cref="FrameSceneHeader.FlagExactFrames"/>), stretched over the export's frame (no letterbox), read back
/// (<see cref="FrameSceneHeader.FlagReadback"/>, NV12 converted on the card) and checked: a frame drawn with a stand-in, more layers than a scene
/// holds, or a video the worker cannot decode (each is drawn once alone over magenta first) stops the export, which says so.</item>
/// <item><b>Pipelined.</b> One thread renders (publish, wait, copy the readback out, publish the next at once); FFmpeg's
/// standard input (or the GIF encoder) is fed from the other buffer meanwhile, I420 made from the NV12 readback as it goes. Two buffers of one frame each; the canvas frames the
/// export's rate skips are never drawn, and the ones it repeats are drawn once (<see cref="GpuExportTiming"/>).</item>
/// <item><b>Never HandPeg's fault.</b> A frame not read back in <see cref="FrameTimeoutMs"/> is published again, up to
/// <see cref="TriesPerFrame"/> times; then the export gives up (<see cref="GpuExportException"/>, logged, said aloud). A card whose
/// compositor will not start gives the export to the processor's software renderer (WARP). Nothing escapes the render thread.</item>
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

    /// <summary>How far ahead of each frame its captions are drawn (5.1-Polish), seconds: those behind are let go as the table fills.</summary>
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

    // The bands of the export's sound, frame by frame, for the effects that pulse with it (5.2B); null when none does.
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
    /// Why an export of the state cannot be made at all, in plain words; null when it can. The frame and its timing
    /// (<see cref="EncoderCommand.WhyNot"/>), and the compositor's tables: more files or textures than they hold.
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

        // (Every clip reads its own file, 5.2-AuditB: no source is kept for a "main" one.)
        if (files.Count > CompositorProtocol.MaxSources)
            return string.Create(CultureInfo.InvariantCulture, $"{files.Count} video files are more than the compositor's source table holds ({CompositorProtocol.MaxSources})");
        if (textures > CompositorProtocol.MaxResources - 4)
            return string.Create(CultureInfo.InvariantCulture, $"some {textures} pictures, titles, masks and colour tables are more than the compositor's resource table holds");
        return null;
    }

    /// <summary>
    /// What an export of the state leaves out, by name, to be said aloud (5.2-Audit: the compositor is the only renderer, so what it
    /// cannot draw is not drawn at all): an effect HandPeg does not know or that has no shader yet, a second LUT or Curves effect on
    /// one clip (it draws one of each), a clip of a kind it does not draw, captions whose words cannot be read. Empty for nothing.
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
                else if ((definition.GpuSlot == GpuShaderSlot.Lut3D && ++luts > 1) || (definition.GpuSlot == GpuShaderSlot.ToneCurve && ++curves > 1))
                    names.Add($"{owner}'s second {definition.Name}");
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
                (luts, curves) = (luts + (definition.GpuSlot == GpuShaderSlot.Lut3D ? 1 : 0), curves + (definition.GpuSlot == GpuShaderSlot.ToneCurve ? 1 : 0));
        }

        return Math.Min(luts, 1) + Math.Min(curves, 1);
    }

    /// <summary>
    /// The encode an export of the state runs, as the command box shows it (its sound tracks' renders named by tokens until they
    /// are made); null, and why, when the export cannot be made.
    /// </summary>
    public static GpuExportCommand? CommandFor(SequenceExportState state, out string whyNot)
    {
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
    /// Starts a compositor for an export, on the compositor's card (AppSettings.CompositorGpuAdapter), with a standby, and
    /// waits until it is ready: off the UI thread. When the card's will not start, the processor's software renderer (WARP)
    /// is tried: slower, never wrong. Null (and why) when neither starts.
    /// </summary>
    public static CompositorClient? StartCompositor(out string whyNot)
    {
        var settings = AppSettings.Current;
        if (Start(settings.CompositorGpuAdapter, out whyNot) is { } client)
            return client;
        if (GpuAdapters.WarpIndex is not { } warp || warp == settings.CompositorGpuAdapter)
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
                whyNot = $"the GPU compositor could not be started: {ex.Message}";
                client?.Dispose();
                return null;
            }
        }
    }

    /// <summary>The words an export's captions are drawn from (beside its caption file, CaptionGenerator.ReadWords); null for none, or none to read.</summary>
    private static CaptionGenerator.CaptionWords? CaptionsOf(SequenceExportState state) =>
        state.AutoCaptions ? CaptionGenerator.ReadWords(state.CaptionsFilePath) : null;

    /// <summary>
    /// Plans an export on a compositor (the UI thread, or another STA thread: WPF draws the text clips): its tables, its scene
    /// (fades and captions drawn by the compositor, the captions from <paramref name="captions"/>, their words on their file's
    /// clock, which is <paramref name="captionsShift"/> seconds before the sequence's: a Render Preview's stretch's start), and the
    /// readback section. Takes <paramref name="client"/> over when it returns an export; null (and why) when it cannot.
    /// <paramref name="energies"/> (5.2B) are the bands of its sound each frame's header carries (AudioReactive.MapOf); null for none.
    /// </summary>
    public static GpuExport? Prepare(SequenceExportState state, GpuExportCommand command, CompositorClient client, out string whyNot,
        CaptionGenerator.CaptionWords? captions = null, double captionsShift = 0, AudioEnergyMap? energies = null)
    {
        GpuResourceBroker? broker = null;
        try
        {
            broker = new GpuResourceBroker(client, bakeOnCallingThread: true);
            var drawsCaptions = command.CaptionsOnGpu && captions is not null && state.AutoCaptions;
            if (drawsCaptions)
            {
                var lines = captions!.Lines.Select(line => (IReadOnlyList<CaptionWord>)line.Select(w => w with { Start = w.Start + captionsShift, End = w.End + captionsShift }).ToList()).ToList();
                broker.Text.SetCaptions(lines, captions.Style, captions.TextScale);
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
            whyNot = $"the GPU export could not be prepared: {ex.Message}";
            broker?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The export, for the view model (from the UI thread): the source's burned subtitles and the sound tracks made first (off this
    /// thread), the compositor started, the frames drawn and piped to the encode. <paramref name="arguments"/> are the command box's
    /// own arguments when it was edited by hand (their sound tokens filled in as the state's are): the frames are still the state's.
    /// Throws <see cref="GpuExportException"/> when it cannot be made or the render failed (the message says why: nothing usable was
    /// written), <see cref="InvalidOperationException"/> when FFmpeg failed, <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static async Task RunAsync(SequenceExportState state, IReadOnlyList<string>? arguments, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (CommandFor(state, out var why) is not { } command)
            throw new GpuExportException($"This export cannot be made: {why}.");
        if (arguments is not null)
            command = command with { Arguments = arguments };

        // The stream source's subtitles to burn in, where its clips put their moments, as the plan draws a picture subtitle track.
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

            // The bands of the sound for the effects that pulse with it (5.2B), frame by frame, as the player's paused frames have them.
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
    /// The export's sound tracks rendered by the sound engine (ExportSoundPlan), each exactly as long as the frames piped; the copies
    /// left to FFmpeg as they are. Only the tracks the command reads are made.
    /// </summary>
    private static async Task<IReadOnlyList<ExportSoundTrack>> RenderSoundAsync(SequenceExportState state, GpuExportCommand command, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        if (command.Sound.Count == 0 || command.Sound.All(t => t.IsCopy))
            return command.Sound;

        var plan = ExportSoundPlan.For(state, EncoderCommand.IsPictureOnly(state));
        var frames = (long)Math.Round(command.Timing.OutputSeconds(command.Timing.OutputFrames) * plan.SampleRate);
        progress.Report(new FfmpegProgress("Rendering the sound...", null, null));
        var clock = Stopwatch.StartNew();
        var rendered = await Task.Run(() => plan.Render(state, 0, frames, null, cancellationToken), cancellationToken);
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"GPU export: {rendered.Count(t => !t.IsCopy)} sound track(s), {frames / (double)plan.SampleRate:0.0} s each, rendered in {clock.Elapsed.TotalSeconds:0.0} s."));
        return rendered;
    }

    /// <summary>
    /// The bands of a state's sound over a stretch of the Sequence (5.2B, AudioReactive.MapOf), made off this thread; null when no effect
    /// pulses with it (nothing is made then). A map that could not be made is none: the effects then do not pulse, and the log says why.
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

    /// <summary>
    /// Render Preview (for the view model, from the UI thread): the stretch from <paramref name="fromSeconds"/> to
    /// <paramref name="toSeconds"/> drawn as the export draws its frames, at <paramref name="percent"/> of the frame's size, its sound
    /// the engine's render of the same program (from the first frame drawn), into a small fast x264 MP4. Throws as
    /// <see cref="RunAsync"/> does.
    /// </summary>
    /// <param name="captionsPath">The stretch's auto-caption file (timed from its start), or null.</param>
    public static async Task RunPreviewAsync(SequenceExportState state, string outputPath, double fromSeconds, double toSeconds, int percent,
        string? captionsPath, IProgress<FfmpegProgress> progress, CancellationToken cancellationToken)
    {
        var captions = captionsPath is not null ? CaptionGenerator.ReadWords(captionsPath) : null;
        if (EncoderCommand.WhyNot(state) is { } reason)
            throw new GpuExportException($"The preview cannot be made: {reason}.");
        var output = EncoderCommand.ParseFrameRate(state.TargetFramerate);
        var length = TimelineLowering.Lowering(state).SequenceSeconds;
        var timing = GpuExportTiming.Window(fromSeconds, Math.Min(toSeconds, length), state.Timeline.FrameRate, output);
        if (timing.OutputFrames <= 0)
            throw new GpuExportException("The preview cannot be made: the stretch holds no frame.");

        // The stream source's subtitles to burn in, as the export burns them.
        if (state.BurnedSubtitleStreams.Count > 0)
            state = state with { PictureSubtitles = await BurnedSubtitles.PrepareAsync(state, cancellationToken) };

        // The sound first, off this thread (it decodes): exactly as long as the frames piped, from the first of them; every track
        // heard, as the export hears them, whatever is soloed.
        var start = timing.CanvasSeconds(timing.FirstCanvasFrame);
        var frames = (long)Math.Round(timing.OutputSeconds(timing.OutputFrames) * SoundRate);
        var program = AudioProgram.Build(state, new AudioBuildOptions { IgnoresSolo = true });
        var soundPath = program.Strips.Count > 0 && frames > 0 ? outputPath + ".sound.f32" : null;
        var command = EncoderCommand.BuildPreview(state with { AutoCaptions = captions is not null }, outputPath, timing, percent, soundPath is not null, captions is not null);
        try
        {
            progress.Report(new FfmpegProgress("Rendering the preview's sound...", null, null));
            if (soundPath is not null)
            {
                await Task.Run(() => WriteSound(program, start, frames, soundPath), cancellationToken);
                command = command with { Sound = [new ExportSoundTrack("", "aac", "128k", 2, [0]) { RenderPath = soundPath }] };
            }

            // The bands of its sound (5.2B), from a second before the stretch (what the bands' followers remember of it) to its end.
            var energies = await EnergiesAsync(state, Math.Max(start - 1, 0), start + timing.OutputSeconds(timing.OutputFrames) + 0.1, progress, cancellationToken);
            progress.Report(new FfmpegProgress("Starting the GPU compositor...", null, null));
            var (client, why) = await Task.Run(() => (StartCompositor(out var whyNot), whyNot), cancellationToken);
            if (client is null)
                throw new GpuExportException($"The preview could not be made: {why}.");

            // Back on the caller's thread (the UI's): the scene is planned there. The preview's captions were made for the stretch from its start.
            if (Prepare(state with { AutoCaptions = captions is not null }, command, client, out why, captions, fromSeconds, energies) is not { } export)
            {
                client.Dispose();
                throw new GpuExportException($"The preview could not be made: {why}.");
            }

            using (export)
            {
                progress.Report(new FfmpegProgress($"Rendering the preview on the GPU compositor ({client.Message})...", null, null));
                var clock = Stopwatch.StartNew();
                await export.RunAsync(progress, cancellationToken);
                AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                    $"GPU preview: {export.FramesPiped} frames from {start:0.000} s in {clock.Elapsed.TotalSeconds:0.0} s, at {percent} % size"));
            }
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
        // STA (5.1-Polish): the captions are drawn by WPF on this thread as the export reaches them, the broker's from here on.
        _renderer = new Thread(Render) { Name = "GPU export", IsBackground = true };
        _renderer.SetApartmentState(ApartmentState.STA);
        _renderer.Start();
        try
        {
            await _firstFrame.Task.WaitAsync(cancellationToken);
            if (Command.Encoder == ExportEncoder.Gif)
                await RunGifAsync(progress, abort.Token);
            else
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

    /// <summary>
    /// FFmpeg, as FfmpegRunner runs a command (the arguments checked to arrive as meant), its standard input the frames. Its
    /// arguments are EncoderCommand's, which writes no graph; a hand-edited command, or the Extra options of the Video tab, may
    /// still carry one the user typed (a -vf, say): it reaches FFmpeg on the command line as it was typed (5.2-Arm64AndNpu: no
    /// graph goes in a file any more), held to what Windows starts a program with.
    /// </summary>
    private async Task RunFfmpegAsync(IProgress<FfmpegProgress> progress, CancellationToken token)
    {
        var executable = Command.Executable;
        var isConfigured = DependencyUpdater.IsFfmpegOverridden && executable.Equals(DependencyUpdater.FfmpegPath, StringComparison.OrdinalIgnoreCase);
        if (!isConfigured && !Path.GetFileNameWithoutExtension(executable).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The command has to start with ffmpeg.");
        if (Path.GetFileName(executable) == executable && File.Exists(DependencyUpdater.FfmpegPath))
            executable = DependencyUpdater.FfmpegPath;

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
    /// HandPeg's GIF encoder (5.2-Audit): every frame counted into one palette and kept, then the GIF written from them. The
    /// export's whole picture, at its rate, looping for ever.
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
    /// The frames' sink (FFmpeg's standard input, or the GIF encoder): each frame the render thread hands over, as many times as
    /// the export shows it (I420 made from the NV12 readback first, for an encoder that takes yuv420p), then the buffer is given
    /// back. FFmpeg that stops reading (it failed, or its own options ended it) ends this quietly: its exit says why.
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
        finally
        {
            // WPF made a dispatcher for this thread if it drew captions here: it ends with the thread.
            System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
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

        // The captions the next moments show, drawn before the frame is (5.1-Polish): a caption that cannot be drawn (no room
        // in the resource table, or no words) gives the export to FFmpeg rather than leave it out of a frame.
        if (_plan.HasCaptions && !_plan.PrepareCaptions(seconds + FrameHair, seconds + CaptionLookaheadSeconds))
            throw new GpuExportException(string.Create(CultureInfo.InvariantCulture, $"The caption at {seconds:0.000} s could not be drawn for the GPU export."));

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

        // The sound's bands at the frame's moment (5.2B), for the effects that pulse with it; 0 where none are known.
        WriteBands(ref header, _energies, seconds);
        header.Flags |= FrameSceneHeader.FlagExactFrames | (wanted.Source == -2 ? 0 : FrameSceneHeader.FlagReadback | (Command.PipesNv12 ? FrameSceneHeader.FlagReadbackNv12 : 0));
        LeaveHints(seconds);
        return _client.Publish();
    }

    /// <summary>
    /// A sound's bands at a frame's moment into its header (5.2B, the audio-reactive shader bus): the export's frames and the player's
    /// (GpuPreviewEngine) look them up alike, a hair into the frame as the frame itself is drawn. Nothing is written where none are known.
    /// </summary>
    internal static bool WriteBands(ref FrameSceneHeader header, AudioEnergyMap? energies, double seconds)
    {
        if (energies is not { } map || !map.TryAt(seconds + FrameHair, out var bass, out var mid, out var high))
            return false;

        (header.AudioBass, header.AudioMid, header.AudioHigh) = (bass, mid, high);
        return true;
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
