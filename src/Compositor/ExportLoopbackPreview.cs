using System.Diagnostics;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.Native;

namespace HandPegApp.Compositor;

/// <summary>
/// Live Encoded Preview (<see cref="FrameSceneHeader.FlagLoopback"/>): the player frame as the export encoder would produce it. The scene is
/// rendered once at canvas size (export framing: stretched, no player zoom; the loupe is drawn over the compare instead, magnifying the
/// compare picture via DrawLoupeView), converted to NV12 on the GPU (PSRgbToNv12, as in export), encoded with the export codec, rate control
/// and bitrate (<see cref="MfEncoderMft"/>), decoded (<see cref="MfDecoderMft"/>), and composited into the player output letterboxed like
/// the canvas (Shaders/Loopback.hlsl: encoded picture, split wipe, or difference x10).
/// <para>Created on the first scene that requests it and released on the first that does not (Law 2: nothing exists while off). Decoded
/// frame waits are bounded (10 ms playing, 120 ms paused, and only in the paused scene that submitted it; wipe or loupe drags take what is
/// ready and never wait, and the worker <see cref="Poll"/>s for the rest) and never hold the output keyed mutex: a late frame shows the last
/// returned pair, never a source beside another frame's encoding. Only device loss throws: a missing or failing transform becomes a state
/// (<see cref="ControlBlock.LoopbackState"/>) with the plain picture.</para>
/// </summary>
internal sealed unsafe class ExportLoopbackPreview(GpuDevice gpu, SceneRenderer renderer) : IDisposable
{
    /// <summary>Bounded decoded-frame waits: playing (the frame's turn) and paused (a representative key frame).</summary>
    public const int PlayingWaitMs = 10, PausedWaitMs = 120;

    // Paused frames drain the encoder (and decoder) immediately after submission so they show at once.
    private const int DrainAfterMs = 0;

    // Give up on a paused frame still in flight after this long (stops the worker's 2 ms polling).
    private const int GiveUpMs = 2000;

    // Give up on an encoder that returns no picture this long after its first frame, and try the next one.
    private const int FirstPictureGiveUpMs = 5000;

    // Source planes of recently submitted frames, keyed by sample time, so each decoded frame is shown beside its own source.
    private sealed class Source(GpuTexture luma, GpuTexture chroma) : IDisposable
    {
        public GpuTexture Luma { get; } = luma;
        public GpuTexture Chroma { get; } = chroma;
        public long Time = -1;
        public long HandedAt;

        public void Dispose()
        {
            Luma.Dispose();
            Chroma.Dispose();
        }
    }

    // High-resolution waitable timer for 1 ms wait steps (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION; 0 when unsupported).
    private nint _tick = Kernel32.CreateWaitableTimerExW(0, 0, Kernel32.HighResolutionTimer, Kernel32.TimerAllAccess);

    private const int Sources = 4;
    private readonly Source?[] _sources = new Source?[Sources];
    private GpuTexture? _program;
    private nint _stagingLuma, _stagingChroma;
    private (int Width, int Height) _size;

    // Export-size textures when the export size differs from the canvas (e.g. 720p export of a 1080p Sequence): the scaled scene, its NV12
    // planes (encoder input; canvas-size source planes are the RAW side) and staging at that size.
    private GpuTexture? _scaled, _encodeLuma, _encodeChroma;
    private (int Width, int Height) _encodedSize;

    // Last rendered scene (content stamp and whether all its video frames were exact), reused as-is when a paused frame is re-encoded for new
    // settings; and the settings stamp of the last encode.
    private long _renderedStamp = long.MinValue;
    private bool _renderedExact;
    private long _encodedSettingsStamp = long.MinValue;

    // Draining the encoder or decoder ends the stream: the next submitted frame must be a key frame.
    private bool _forceKeyNext;

    // Software encoder arguments read from the control block, and the version read.
    private string _softwareArguments = "";
    private int _argumentsVersion = -1;

    private ILoopbackEncoder? _encoder;
    private MfDecoderMft? _decoder;
    private LoopbackConfig _config;
    private bool _configured, _bypass;
    private ExportLoopbackState _state;
    private int _hresult;
    private string _name = "";

    // Encoders that failed or returned nothing (not retried for this codec), first-frame submit time (give-up clock), last failure HRESULT,
    // and the state last written to HandPeg.log.
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private long _firstSubmit;
    private int _lastFailure;
    private (ExportLoopbackState State, int HResult, string Name) _logged;

    // Sample clock (frames submitted), newest submitted frame not yet returned, displayed pair, and the paused frame's key.
    private long _frames;
    private long _awaited = -1;
    private int _shown = -1, _newest = -1;
    private bool _shownDecoded, _bypassConfigured;
    private long _encodedStamp = long.MinValue;
    private bool _encodedExact, _playing;
    private int _late, _encodedCount;
    private float _roundTripMs;
    private bool _drained;

    // Whether this scene submitted a frame (only that scene waits in Collect), and the decoded-frame count at last render (a picture missing
    // an exact video frame re-renders only after more frames decode).
    private bool _submitted;
    private int _decodedAtRender = -1;

    /// <summary>True when a scene requests the loopback where supported: flat, letterboxed player picture without readback.</summary>
    public static bool Wants(in FrameSceneHeader header) =>
        (header.Flags & FrameSceneHeader.FlagLoopback) != 0 && (header.Flags & FrameSceneHeader.FlagLetterbox) != 0
        && (header.Flags & FrameSceneHeader.FlagReadback) == 0 && header.ViewMode == 0;

    // Size whose textures or render failed (0 x 0 when none): plain picture until the size changes.
    private (int Width, int Height) _failedSize;

    /// <summary>
    /// True when textures or render failed at this scene's size: the worker draws the plain player picture and keeps this instance only for
    /// its Failed state. A different canvas size retries.
    /// </summary>
    public bool Broken(in FrameSceneHeader header) => _failedSize.Width != 0 && _failedSize == RawSize(header);

    // Scene (RAW side) size: the canvas size, even, at most 8192 per side.
    private static (int Width, int Height) RawSize(in FrameSceneHeader header)
    {
        var (width, height) = ((Math.Max(header.CanvasWidth, 2) + 1) & ~1, (Math.Max(header.CanvasHeight, 2) + 1) & ~1);
        return (Math.Min(width, 8192), Math.Min(height, 8192));
    }

    // Encoded size: the export frame size, keeping the canvas aspect (ExportLoopbackSettings.EncodedSizeOf).
    private static (int Width, int Height) EncodedSize(in FrameSceneHeader header) =>
        ExportLoopbackSettings.EncodedSizeOf(header.CanvasWidth, header.CanvasHeight, header.LoopbackEncodedWidth, header.LoopbackEncodedHeight);

    /// <summary>
    /// True while a paused frame is awaited: the worker re-checks in 2 ms instead of 100 and <see cref="Poll"/>s. Stays true until Poll or
    /// <see cref="Collect"/> gives it up (<see cref="InFlight"/>), so the worker always gets the turn that gives up and redraws.
    /// </summary>
    public bool Pending => _awaited >= 0 && !_playing && Find(_awaited) >= 0;

    // A paused frame still in flight: awaited and submitted within the encoder's give-up time (2 s; 30 s for software encoders).
    private bool InFlight => Pending && Find(_awaited) is var index and >= 0
        && Stopwatch.GetElapsedTime(_sources[index]!.HandedAt).TotalMilliseconds < (_encoder?.GiveUpMs ?? GiveUpMs);

    /// <summary>
    /// Pre-output half of the frame: renders the scene at encoded size (only when it may have changed: playing, a new content stamp, or a
    /// paused frame that lacked an exact video frame after more frames decoded), keeps its NV12 planes and submits them to the encoder.
    /// Runs inside the worker's drawing window (decode lanes hold back).
    /// </summary>
    public void Render(ref ControlBlock control, in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, DecoderPool decoders)
    {
        _playing = header.IsPlaying != 0;
        _bypass = (header.LoopbackTestFlags & 1) != 0;
        _submitted = false;
        var (width, height) = RawSize(header);
        var (encodedWidth, encodedHeight) = EncodedSize(header);
        _failedSize = (0, 0);
        Configure(ref control, header, encodedWidth, encodedHeight);

        // An encoder that accepted frames but returned none: give up and try the codec's next encoder.
        if (_encoder is not null && _encodedCount == 0 && _firstSubmit != 0
            && Stopwatch.GetElapsedTime(_firstSubmit).TotalMilliseconds > Math.Max(FirstPictureGiveUpMs, _encoder.GiveUpMs))
        {
            Fail(unchecked((int)0x800705B4), $"waiting {Math.Max(FirstPictureGiveUpMs, _encoder.GiveUpMs)} ms for its first picture", tryNext: true);
            Configure(ref control, header, encodedWidth, encodedHeight);
        }

        // Re-encode when the picture or the export settings may have changed (a new stamp of either).
        var encode = _playing || _program is null || header.LoopbackContentStamp != _encodedStamp || header.LoopbackSettingsStamp != _encodedSettingsStamp
                     || (!_encodedExact && decoders.FramesDecoded != _decodedAtRender);
        if (!encode)
        {
            Publish(ref control);
            return;
        }

        // Export framing: canvas stretched to the scene size, no player zoom, pan or loupe. Texture allocation failures (e.g. an 8K canvas
        // on a low-VRAM GPU) or render failures become a state, never a throw: textures are released and the plain picture is drawn
        // (Broken) until the canvas size changes.
        int index;
        Source source;
        try
        {
            var made = EnsureTextures(width, height, encodedWidth, encodedHeight);

            // A paused frame re-encoded only for new export settings reuses the last rendered scene (no layer redrawn, so new settings show
            // as fast as the encoder responds). Playing, or a picture that may have changed, is redrawn.
            var redraw = made || _playing || header.LoopbackContentStamp != _renderedStamp
                         || (!_renderedExact && decoders.FramesDecoded != _decodedAtRender);
            if (redraw)
            {
                var flat = header;
                flat.Flags &= ~(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagLoopback);
                (flat.LoupeClipKey, flat.ViewScale, flat.ViewPanX, flat.ViewPanY) = (0, 1, 0, 0);

                // Count taken before rendering: a frame a lane finishes meanwhile (waking the worker) is drawn on that wake.
                var decoded = decoders.FramesDecoded;
                renderer.Render(flat, layers, _program!.Rtv, width, height, raw: false);
                (_renderedStamp, _renderedExact, _decodedAtRender) = (header.LoopbackContentStamp, decoders.LastFrameExact, decoded);
            }

            (_encodedStamp, _encodedExact, _encodedSettingsStamp) = (header.LoopbackContentStamp, _renderedExact, header.LoopbackSettingsStamp);
            index = NextSource();
            source = _sources[index]!;
            _newest = index;
            renderer.ToNv12(_program!, source.Luma, source.Chroma);

            // Export size differs: scale the scene and convert to NV12 at that size for the encoder only.
            if (_scaled is not null)
            {
                renderer.Stretch(_program!, _scaled);
                renderer.ToNv12(_scaled, _encodeLuma!, _encodeChroma!);
            }

            source.Time = _frames * 10_000_000L * 1000 / FpsMilli(_config.Fps);
            source.HandedAt = Stopwatch.GetTimestamp();
            _frames++;
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            ReleaseTextures();
            (_shown, _newest, _shownDecoded) = (-1, -1, false);
            _failedSize = (width, height);
            Fail(ex.Code, "the compare's textures or render");
            Publish(ref control);
            return;
        }

        if (_bypass)
        {
            // Golden-test codec bypass: the "decoded" picture is the source NV12 itself.
            (_shown, _shownDecoded, _awaited) = (index, true, -1);
            _state = ExportLoopbackState.Running;
            _encodedCount++;
        }
        else if (_encoder is not null && (_playing || _encodedExact) && !(_playing && _encoder.PausedOnly))
        {
            try
            {
                _encoder.SetQualityVsSpeed(_playing ? Math.Min(header.LoopbackQualityVsSpeed, 33) : header.LoopbackQualityVsSpeed);
                var (luma, chroma) = _scaled is not null ? (_encodeLuma!, _encodeChroma!) : (source.Luma, source.Chroma);
                _encoder.Submit(SampleOf(luma, chroma, source.Time, _encoder.TakesP010), forceKey: !_playing || _forceKeyNext);
                _forceKeyNext = false;
                if (_firstSubmit == 0)
                    _firstSubmit = Stopwatch.GetTimestamp();
                _awaited = source.Time;
                (_drained, _submitted) = (false, true);

                // Golden-test flag 2: the submitting scene does not wait either, so the frame returns only via Poll.
                if ((header.LoopbackTestFlags & 2) != 0)
                    _submitted = false;
            }
            catch (GpuException ex) when (!ex.IsDeviceLost)
            {
                Fail(ex.Code, "handing a frame to the encoder (ProcessInput)", tryNext: true);
            }
        }

        // No codec (unavailable, no loopback support, or failed), or a software encoder while playing (paused frames only): plain picture. A
        // paused picture still showing a stand-in frame is not encoded until the exact frame is drawn.
        if ((_encoder is null || (_playing && _encoder.PausedOnly)) && !_bypass)
            (_shown, _shownDecoded) = (index, false);
        Publish(ref control);
    }

    /// <summary>
    /// Bounded wait for the last submitted frame's decoded picture, paired with its source; the output is not held. When paused, only the
    /// submitting scene waits: others (wipe or loupe moved, same picture) take what is ready without waiting or draining; the worker's
    /// <see cref="Poll"/> draws the frame when it arrives.
    /// </summary>
    public void Collect(ref ControlBlock control)
    {
        if (_encoder is null || _awaited < 0)
            return;

        var started = Stopwatch.GetTimestamp();
        var budget = _playing ? PlayingWaitMs : _submitted ? PausedWaitMs : 0;
        var spins = 0;
        try
        {
            while (true)
            {
                var bitstream = _encoder.TryTakeOutput();
                if (bitstream != 0)
                {
                    try
                    {
                        Decode(bitstream);
                    }
                    finally
                    {
                        Com.Release(ref bitstream);
                    }

                    if (_awaited < 0 || _decoder is null)
                        break;
                    continue;
                }

                var waited = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (waited >= budget)
                    break;

                // Drain encoders that hold paused frames for look-ahead (Qualcomm's holds several).
                if (!_playing && !_drained && waited >= DrainAfterMs)
                    _drained = _encoder.Drain();

                // No hot spin: yield, then sleep 1 ms steps once the wait is long, on the high-resolution timer (the worker never raises
                // the system timer rate, so Sleep(1) would wake on the 15.6 ms tick, past a playing frame's 10 ms budget).
                if (++spins < 50)
                {
                    Thread.Yield();
                }
                else if (_tick != 0)
                {
                    var due = -(long)(Math.Clamp(budget - waited, 0.1, 1.0) * 10_000);
                    _ = Kernel32.SetWaitableTimer(_tick, &due, 0, 0, 0, 0);
                    _ = Kernel32.WaitForSingleObject(_tick, 2);
                }
                else if (_playing)
                {
                    Thread.Yield();
                }
                else
                {
                    Thread.Sleep(waited < budget - 2 ? 1 : 0);
                }
            }

            if (_awaited >= 0 && _playing)
            {
                // Deadline passed: keep the last pair on screen; the frame is still collected later and shown only if newest.
                _late++;
                _awaited = -1;
            }
            else if (_awaited >= 0 && !InFlight)
            {
                _awaited = -1;
            }
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            Fail(ex.Code, "taking the encoded frame back", tryNext: true);
        }

        Publish(ref control);
    }

    /// <summary>
    /// Between-scene check for a paused frame still in the encoder (worker thread, outside the drawing window, like <see cref="Collect"/>):
    /// decodes whatever the encoder has output without waiting. True when the displayed picture changed (newer decoded pair) or the frame was
    /// given up, so the scene is redrawn; false otherwise and nothing is drawn (avoids full redraws every 2 ms while a frame is in flight).
    /// </summary>
    public bool Poll()
    {
        if (_awaited < 0)
            return false;

        var before = (_shown, _shownDecoded, _decoder?.LastTime ?? -1, _state);
        try
        {
            // Look-ahead encoders release a paused frame only when drained: retry here if the submitting scene's Collect found it not
            // yet in the encoder; never blocks.
            if (!_playing && !_drained && _encoder is not null && Find(_awaited) is var awaited and >= 0
                && Stopwatch.GetElapsedTime(_sources[awaited]!.HandedAt).TotalMilliseconds >= DrainAfterMs)
                _drained = _encoder.Drain();

            while (_encoder?.TryTakeOutput() is { } bitstream and not 0)
            {
                try
                {
                    Decode(bitstream);
                }
                finally
                {
                    Com.Release(ref bitstream);
                }

                if (_awaited < 0 || _decoder is null)
                    break;
            }
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            Fail(ex.Code, "polling the encoder for its frame", tryNext: true);
            return true;
        }

        // Given up (or no codec left): draw once more and stop polling.
        if (_awaited >= 0 && (_encoder is null || !InFlight))
        {
            _awaited = -1;
            return true;
        }

        // Newer pair or new state (a decoder creation failure surfaces here and is published by the redraw).
        return (_shown, _shownDecoded, _decoder?.LastTime ?? -1, _state) != before;
    }

    /// <summary>Draws the compare into the output (caller holds its keyed mutex), placed like the player's canvas.</summary>
    public void Draw(in FrameSceneHeader header, nint target, int width, int height)
    {
        var (x, y, scale) = SceneRenderer.Letterbox(header, width, height);
        var picture = (x, y, Math.Max(header.CanvasWidth, 1) * scale, Math.Max(header.CanvasHeight, 1) * scale);
        if (Pair() is not { } pair)
        {
            gpu.Context.ClearRenderTargetView(target, 0, 0, 0, 1);
            return;
        }

        var mode = pair.Decoded ? Math.Clamp(header.LoopbackMode, 0, 2) : 0;
        var gain = header.LoopbackHeatGain > 0 ? header.LoopbackHeatGain : 10;
        renderer.DrawLoopbackCompare(pair.Luma, pair.Chroma, pair.DecodedLuma, pair.DecodedChroma, target, width, height, picture, mode, Math.Clamp(header.LoopbackWipeX, 0, 1), gain, 1);
    }

    /// <summary>
    /// Loupe view over the compare: the compare picture around the loupe point at <paramref name="scale"/> view pixels per canvas pixel,
    /// point-sampled (Loopback.hlsl, negative Params.w) so encoder blocking and ringing are magnified. Shows the encoded picture, or the
    /// gained difference in heatmap mode (split wipe shows the encoded side). False when no picture yet. Draws into the renderer's own view
    /// outside the keyed mutex (SceneRenderer.LoupeFocus).
    /// </summary>
    public bool DrawLoupeView(in FrameSceneHeader header, nint target, int side, float pointX, float pointY, float scale)
    {
        if (Pair() is not { } pair || side <= 0 || !(scale > 0))
            return false;

        var picture = LoupePicture(side, pointX, pointY, scale, header.CanvasWidth, header.CanvasHeight);
        var mode = pair.Decoded && header.LoopbackMode == 2 ? 2 : 0;
        var gain = header.LoopbackHeatGain > 0 ? header.LoopbackHeatGain : 10;
        renderer.DrawLoopbackCompare(pair.Luma, pair.Chroma, pair.DecodedLuma, pair.DecodedChroma, target, side, side, picture, mode, 0, gain, -1);
        return true;
    }

    /// <summary>
    /// Picture rectangle in a <paramref name="side"/>-pixel loupe view, scaled by <paramref name="scale"/> about the point (canvas pixels)
    /// centred in the view. Pure; used by goldens.
    /// </summary>
    internal static (float X, float Y, float W, float H) LoupePicture(int side, float pointX, float pointY, float scale, int canvasWidth, int canvasHeight) =>
        (side / 2f - pointX * scale, side / 2f - pointY * scale, Math.Max(canvasWidth, 1) * scale, Math.Max(canvasHeight, 1) * scale);

    // Pair to display: paused, a new picture shows unencoded until its encoding returns (a seek never shows the previous frame); playing,
    // the last returned pair stays (never a source beside another frame's encoding). Null when none yet.
    private (nint Luma, nint Chroma, nint DecodedLuma, nint DecodedChroma, bool Decoded)? Pair()
    {
        var decoded = _shownDecoded && (_playing || _shown == _newest) && _shown >= 0 && _sources[_shown] is { } shown
            && (_bypass || _decoder?.LastTime == shown.Time);
        var index = decoded ? _shown : _newest;
        if (index < 0 || _sources[index] is not { } source)
            return null;

        var (luma, chroma) = (source.Luma.Srv, source.Chroma.Srv);
        var (decodedLuma, decodedChroma) = decoded && !_bypass && _decoder is not null ? (_decoder.LumaSrv, _decoder.ChromaSrv) : (luma, chroma);
        return (luma, chroma, decodedLuma, decodedChroma, decoded);
    }

    // ----- Codec -----

    // Encoder and decoder for the scene's settings: recreated when codec, rate control, bitrate, quality, frame rate or size change (a failed
    // setup is not retried until one does).
    private void Configure(ref ControlBlock control, in FrameSceneHeader header, int width, int height)
    {
        var codec = header.LoopbackCodec switch
        {
            1 => MfGuids.H264,
            2 => MfGuids.Hevc,
            3 => MfGuids.Av1,
            4 => MfGuids.Vp9,
            _ => Guid.Empty,
        };
        var fps = header.LoopbackFrameRate > 0 && float.IsFinite(header.LoopbackFrameRate) ? Math.Clamp(header.LoopbackFrameRate, 1, 1000) : 30;

        // Also preset (quality vs. speed), 10-bit, encoder family (which vendor's adapter) and software encoder arguments.
        var software = header.LoopbackFamily == ExportLoopbackSettings.FamilySoftware;
        var config = new LoopbackConfig(codec, width, height, fps, header.LoopbackRateControl, Math.Max(header.LoopbackBitrateKbps, 100), header.LoopbackQuality,
            Math.Clamp(header.LoopbackQualityVsSpeed, 0, 100), header.LoopbackTenBit != 0, header.LoopbackFamily, software ? SoftwareArguments(ref control) : "");
        if (_configured && config == _config && _bypass == _bypassConfigured)
            return;

        CloseCodec();
        // New codec, family or software command: clear the refused list (the changed setting may be what failed).
        if (config.Codec != _config.Codec || config.Family != _config.Family || config.SoftwareArguments != _config.SoftwareArguments)
            _refused.Clear();
        _forceKeyNext = false;
        (_config, _configured, _bypassConfigured, _firstSubmit) = (config, true, _bypass, 0);
        (_frames, _awaited, _shown, _newest, _shownDecoded, _encodedStamp, _hresult, _late, _encodedCount, _roundTripMs) = (0, -1, -1, -1, false, long.MinValue, 0, 0, 0, 0);
        foreach (var slot in _sources)
        {
            if (slot is not null)
                slot.Time = -1;
        }

        if (_bypass)
        {
            (_state, _name) = (ExportLoopbackState.Running, "the test's bypass");
            return;
        }

        if (codec == Guid.Empty)
        {
            (_state, _name) = (ExportLoopbackState.Unsupported, "");
            return;
        }

        try
        {
            // The export's selected encoder: software via FFmpeg, else the hardware MFT on its family's adapter first.
            _encoder = software
                ? _refused.Count == 0 ? FfmpegLoopbackEncoder.Create(config, out _state) : null
                : MfEncoderMft.Create(gpu, config, header.LoopbackQualityVsSpeed, _refused, out _state, out _hresult);
            _name = _encoder?.Name ?? "";

            // All of the codec's encoders already refused: keep the last failure (not "no encoder").
            if (_encoder is null && _refused.Count > 0 && _state == ExportLoopbackState.NoEncoder)
                (_state, _hresult) = (ExportLoopbackState.Failed, _lastFailure);
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            Fail(ex.Code, "setting the encoder up");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Fail(ex.HResult, "loading Media Foundation");
        }
    }

    // Software encoder arguments from the control block, read between two equal even versions (seqlock; never torn); the previous value
    // stands while the client is writing.
    private string SoftwareArguments(ref ControlBlock control)
    {
        var version = Volatile.Read(ref control.LoopbackArgumentsVersion);
        if (version == _argumentsVersion || (version & 1) != 0)
            return _softwareArguments;
        ReadOnlySpan<byte> field = control.LoopbackSoftwareArguments;
        var end = field.IndexOf((byte)0);
        var text = System.Text.Encoding.UTF8.GetString(end < 0 ? field : field[..end]);

        // Data loads must complete before the version re-check (ARM64 can reorder them past a plain acquire read).
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref control.LoopbackArgumentsVersion) != version)
            return _softwareArguments;
        (_softwareArguments, _argumentsVersion) = (text, version);
        return text;
    }

    // Feeds one bitstream sample to the decoder (created on first use, with the encoder's sequence header when available).
    private void Decode(nint bitstream)
    {
        if (_decoder is null)
        {
            _decoder = MfDecoderMft.Create(gpu, _config.Codec, _config.Width, _config.Height, _config.Fps, _encoder!.SequenceHeader(), out var state, out var hresult);
            if (_decoder is null)
            {
                (_state, _hresult, _awaited) = (state, hresult, -1);
                CloseCodec();
                return;
            }
        }

        var got = _decoder.Decode(bitstream);

        // Paused: drain a decoder holding the frame in its reorder buffer so it shows without waiting for a scrub or play; the next
        // submitted frame is then a key frame.
        if (!_playing && _awaited >= 0 && _decoder.LastTime != _awaited)
        {
            got |= _decoder.DrainHeld();
            _forceKeyNext = true;
        }

        if (!got)
            return;

        var time = _decoder.LastTime;
        var index = Find(time);
        if (index < 0 || _sources[index] is not { } source)
            return;

        _roundTripMs = (float)Stopwatch.GetElapsedTime(source.HandedAt).TotalMilliseconds;
        _encodedCount++;
        _state = ExportLoopbackState.Running;

        // Show only if newer than the displayed pair (late frames returning after a newer one are skipped).
        if (_shown < 0 || !_shownDecoded || _sources[_shown] is not { } shown || shown.Time <= time)
            (_shown, _shownDecoded) = (index, true);
        if (time == _awaited)
            _awaited = -1;
    }

    // Copies the encoder planes into a system-memory NV12 sample: Y rows then UV rows at encoded width (waits on the GPU here). For 10-bit
    // encoders, P010 with each 8-bit value in the top bits of 16 (the scene is 8-bit, so nothing is lost or invented).
    private nint SampleOf(GpuTexture lumaPlane, GpuTexture chromaPlane, long time, bool p010)
    {
        var (width, height) = _encodedSize;
        gpu.Context.CopyResource(_stagingLuma, lumaPlane.Texture);
        gpu.Context.CopyResource(_stagingChroma, chromaPlane.Texture);
        var sampleSize = p010 ? 2 : 1;
        var bytes = (uint)(width * height * 3 / 2 * sampleSize);
        var sample = MfEncoderMft.Sample(bytes);
        nint buffer = 0;
        try
        {
            MfEncoderMft.Check(Mf.GetBufferByIndex(sample, 0, out buffer), "IMFSample::GetBufferByIndex");
            MfEncoderMft.Check(Mf.Lock(buffer, out var data, out _), "IMFMediaBuffer::Lock");
            try
            {
                Com.Check(gpu.Context.Map(_stagingLuma, 0, D3D11.MapRead, 0, out var luma), "Map loopback (Y)");
                try
                {
                    for (var y = 0; y < height; y++)
                        CopyRow(luma.Data + (long)y * luma.RowPitch, data + (long)y * width * sampleSize, width, p010);
                }
                finally
                {
                    gpu.Context.Unmap(_stagingLuma, 0);
                }

                Com.Check(gpu.Context.Map(_stagingChroma, 0, D3D11.MapRead, 0, out var chroma), "Map loopback (UV)");
                try
                {
                    var start = data + (long)width * height * sampleSize;
                    for (var y = 0; y < height / 2; y++)
                        CopyRow(chroma.Data + (long)y * chroma.RowPitch, start + (long)y * width * sampleSize, width, p010);
                }
                finally
                {
                    gpu.Context.Unmap(_stagingChroma, 0);
                }
            }
            finally
            {
                _ = Mf.Unlock(buffer);
            }

            MfEncoderMft.Check(Mf.SetCurrentLength(buffer, bytes), "IMFMediaBuffer::SetCurrentLength");
            var duration = 10_000_000L * 1000 / FpsMilli(_config.Fps);
            _ = Mf.SetSampleTime(sample, time);
            _ = Mf.SetSampleDuration(sample, duration);
            return sample;
        }
        catch
        {
            Com.Release(ref sample);
            throw;
        }
        finally
        {
            Com.Release(ref buffer);
        }
    }

    /// <summary>Copies a row of 8-bit samples as-is, or (P010) shifted into the top of 16 bits. Pure; used by goldens.</summary>
    internal static void CopyRow(byte* from, byte* to, int count, bool p010)
    {
        if (!p010)
        {
            Buffer.MemoryCopy(from, to, count, count);
            return;
        }

        var wide = (ushort*)to;
        for (var i = 0; i < count; i++)
            wide[i] = (ushort)(from[i] << 8);
    }

    private static long FpsMilli(double fps) => Math.Max((long)Math.Round(fps * 1000), 1000);

    // Failure: plain picture, logged to HandPeg.log. With tryNext the failed encoder is refused for this codec and the next scene sets up the
    // next one (when none remain, the failure stands).
    private void Fail(int hresult, string stage, bool tryNext = false)
    {
        var encoder = _name;
        (_state, _hresult, _awaited, _lastFailure) = (ExportLoopbackState.Failed, hresult, -1, hresult);
        CloseCodec();
        var next = tryNext && encoder.Length > 0 && _refused.Add(encoder);
        if (next)
            _configured = false;
        AppLog.Write(FormattableString.Invariant(
            $"Live Encoded Preview: {stage} failed (0x{hresult:X8}) with {(encoder.Length > 0 ? encoder : "no encoder")}{(next ? "; the next encoder of the codec is tried" : "")}."));
    }

    private void CloseCodec()
    {
        _decoder?.Dispose();
        _encoder?.Dispose();
        (_decoder, _encoder, _firstSubmit) = (null, null, 0);
        _shownDecoded = false;
    }

    // ----- Textures -----

    // Scene textures at its size, plus export-size textures when different; true when anything was recreated (the scene must be redrawn).
    private bool EnsureTextures(int width, int height, int encodedWidth, int encodedHeight)
    {
        if (_program is not null && _size == (width, height) && _encodedSize == (encodedWidth, encodedHeight))
            return false;
        ReleaseTextures();
        (_size, _encodedSize) = ((width, height), (encodedWidth, encodedHeight));
        _program = gpu.CreateTexture(width, height, DxgiFormat.B8G8R8A8Unorm, renderTarget: true);
        for (var i = 0; i < Sources; i++)
            _sources[i] = new Source(gpu.CreateTexture(width, height, DxgiFormat.R8Unorm, renderTarget: true), gpu.CreateTexture(width / 2, height / 2, DxgiFormat.R8G8Unorm, renderTarget: true));
        if ((encodedWidth, encodedHeight) != (width, height))
        {
            _scaled = gpu.CreateTexture(encodedWidth, encodedHeight, DxgiFormat.B8G8R8A8Unorm, renderTarget: true);
            _encodeLuma = gpu.CreateTexture(encodedWidth, encodedHeight, DxgiFormat.R8Unorm, renderTarget: true);
            _encodeChroma = gpu.CreateTexture(encodedWidth / 2, encodedHeight / 2, DxgiFormat.R8G8Unorm, renderTarget: true);
        }

        _stagingLuma = gpu.CreateStaging(encodedWidth, encodedHeight, DxgiFormat.R8Unorm);
        _stagingChroma = gpu.CreateStaging(encodedWidth / 2, encodedHeight / 2, DxgiFormat.R8G8Unorm);
        (_shown, _newest, _shownDecoded, _awaited) = (-1, -1, false, -1);
        _renderedStamp = long.MinValue;
        return true;
    }

    // Slot for the next frame: the oldest, never the displayed pair.
    private int NextSource()
    {
        var best = -1;
        for (var i = 0; i < Sources; i++)
        {
            if (i == _shown)
                continue;
            if (best < 0 || _sources[i]!.Time < _sources[best]!.Time)
                best = i;
        }

        return best;
    }

    // Slot holding this sample time's frame; -1 when evicted.
    private int Find(long time)
    {
        for (var i = 0; i < Sources; i++)
        {
            if (time >= 0 && _sources[i] is { } source && source.Time == time)
                return i;
        }

        return -1;
    }

    private void ReleaseTextures()
    {
        _program?.Dispose();
        _program = null;
        _scaled?.Dispose();
        _encodeLuma?.Dispose();
        _encodeChroma?.Dispose();
        (_scaled, _encodeLuma, _encodeChroma) = (null, null, null);
        _renderedStamp = long.MinValue;
        for (var i = 0; i < Sources; i++)
        {
            _sources[i]?.Dispose();
            _sources[i] = null;
        }

        Com.Release(ref _stagingLuma);
        Com.Release(ref _stagingChroma);
    }

    // ----- Status -----

    private void Publish(ref ControlBlock control)
    {
        // Log each new state to HandPeg.log and breadcrumbs: what the ENCODED pill shows, and why.
        if ((_state, _hresult, _name) != _logged)
        {
            _logged = (_state, _hresult, _name);
            var codec = _config.Codec == MfGuids.H264 ? "H.264" : _config.Codec == MfGuids.Hevc ? "HEVC" : _config.Codec == MfGuids.Av1 ? "AV1"
                : _config.Codec == MfGuids.Vp9 ? "VP9" : "no codec";
            var line = FormattableString.Invariant(
                $"Live Encoded Preview: {_state}{(_hresult != 0 ? $" (0x{_hresult:X8})" : "")}, {codec} {_config.Width}x{_config.Height}{(_config.TenBit ? " 10-bit" : "")} ({ExportLoopbackSettings.FamilyShort(_config.Family)}), encoder {(_name.Length > 0 ? _name : "none")}{(_encoder is { OnAskedCard: false } ? " (a stand-in)" : "")}, compositor LUID 0x{gpu.AdapterLuid:X}.");
            AppLog.Write(line);
            GpuNativeBreadcrumbs.Mark("Loopback", line);
        }

        control.LoopbackOnAskedCard = _encoder is { OnAskedCard: false } ? 0 : 1;
        control.LoopbackPausedOnly = _encoder is { PausedOnly: true } ? 1 : 0;
        control.LoopbackState = (int)_state;
        control.LoopbackHResult = _hresult;
        control.LoopbackRoundTripMs = _roundTripMs;
        control.LoopbackFramesEncoded = _encodedCount;
        control.LoopbackLateFrames = _late;
        control.LoopbackCodecsAvailable = _state == ExportLoopbackState.Running && !_bypass
            ? _config.Codec == MfGuids.H264 ? 1 : _config.Codec == MfGuids.Hevc ? 2 : _config.Codec == MfGuids.Vp9 ? 8 : 4 : 0;
        if (!CompositorSharedMemory.ReadString(control.LoopbackEncoderName).Equals(_name, StringComparison.Ordinal))
            CompositorSharedMemory.WriteString(control.LoopbackEncoderName, _name);
    }

    /// <summary>Releases transforms and textures (nothing of the loopback survives being turned off).</summary>
    public void Dispose()
    {
        CloseCodec();
        ReleaseTextures();
        _configured = false;
        if (_tick != 0)
        {
            _ = Kernel32.CloseHandle(_tick);
            _tick = 0;
        }
    }
}
