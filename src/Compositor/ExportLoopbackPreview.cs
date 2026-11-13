using System.Diagnostics;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegApp.Compositor;

/// <summary>
/// The Export Quality Preview (Pass 5.4B, <see cref="FrameSceneHeader.FlagLoopback"/>): the player's frame as the export's encoder
/// would leave it. The scene is rendered once at the canvas's size (the export's: stretched, without the player's zoom or loupe; 5.4C:
/// the loupe is laid over the compare instead, its view the compare picture magnified, DrawLoupeView),
/// made NV12 on the card (PSRgbToNv12, as an export's frames are), handed to a hardware encoder of the export's codec at its rate
/// control and bitrate (<see cref="MfEncoderMft"/>), decoded again (<see cref="MfDecoderMft"/>), and the two compared into the
/// player's output, letterboxed as the player shows the canvas (Shaders/Loopback.hlsl: the encoded picture, a split wipe, or the
/// difference x10).
/// <para>Made on the first scene that asks for it, let go on the first that does not (Law 2: nothing of it exists while it is off).
/// The wait for a decoded frame is bounded (10 ms playing, 120 ms paused, and only in the paused scene that handed it over: 5.4.1A, a
/// wipe or loupe dragged meanwhile takes what is back and never waits; the worker <see cref="Poll"/>s for the rest) and never holds the output's keyed mutex: a frame that is
/// late shows the last pair that came back, never a source beside another frame's encoding. Nothing is thrown out of it but a lost
/// device: a missing or failing transform is a state (<see cref="ControlBlock.LoopbackState"/>) and the plain picture.</para>
/// </summary>
internal sealed unsafe class ExportLoopbackPreview(GpuDevice gpu, SceneRenderer renderer) : IDisposable
{
    /// <summary>The bounded waits for the decoded frame: playing (the frame's turn) and paused (a representative key frame).</summary>
    public const int PlayingWaitMs = 10, PausedWaitMs = 120;

    // A paused frame is drained out of the encoder as soon as it has gone in (5.4.4-Final, the user's: both the encoder and the decoder are
    // drained on a paused frame, so it shows at once; before, 4 ms, and the decoder never).
    private const int DrainAfterMs = 0;

    // A paused frame still in flight after this long is given up on (the worker stops looking every 2 ms).
    private const int GiveUpMs = 2000;

    // An encoder that has given no picture back this long after its first frame is given up on, and the next one tried (5.4.4).
    private const int FirstPictureGiveUpMs = 5000;

    // The source's planes of the last frames handed over, by sample time: the decoded frame is shown beside its own.
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

    // The wait's millisecond step (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION; 0 on a Windows without it).
    private nint _tick = AudioNative.CreateWaitableTimerExW(0, 0, AudioNative.HighResolutionTimer, AudioNative.TimerAllAccess);

    private const int Sources = 4;
    private readonly Source?[] _sources = new Source?[Sources];
    private GpuTexture? _program;
    private nint _stagingLuma, _stagingChroma;
    private (int Width, int Height) _size;

    // 5.4.4-Final: the export's frame size when it is not the canvas's (a 720p export of a 1080p Sequence): the scene stretched to it, its NV12
    // planes (what the encoder is given; the source's own planes, at the canvas's size, are the RAW side), and the staging at that size.
    private GpuTexture? _scaled, _encodeLuma, _encodeChroma;
    private (int Width, int Height) _encodedSize;

    // The scene drawn last (its content stamp and whether every video frame in it was exact): a paused frame encoded again for new settings
    // takes it as it is (no layer drawn again), and the settings' stamp the last encode was made for.
    private long _renderedStamp = long.MinValue;
    private bool _renderedExact;
    private long _encodedSettingsStamp = long.MinValue;

    // A drain of the decoder or the encoder ends a stream: the next frame handed over is a key frame.
    private bool _forceKeyNext;

    // A software encoder's arguments as read from the control block, and the version they were read at.
    private string _softwareArguments = "";
    private int _argumentsVersion = -1;

    private ILoopbackEncoder? _encoder;
    private MfDecoderMft? _decoder;
    private LoopbackConfig _config;
    private bool _configured, _bypass;
    private ExportLoopbackState _state;
    private int _hresult;
    private string _name = "";

    // 5.4.4 (the Desktop's missing encoded picture): encoders that failed or gave nothing back are not tried again for this codec, the
    // first frame's hand-over time (the give-up's clock), the last failure's HRESULT, and the state last written to HandPeg.log.
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private long _firstSubmit;
    private int _lastFailure;
    private (ExportLoopbackState State, int HResult, string Name) _logged;

    // The sample clock (frames handed over), the newest frame handed over and not yet back, the pair shown, and the paused frame's key.
    private long _frames;
    private long _awaited = -1;
    private int _shown = -1, _newest = -1;
    private bool _shownDecoded, _bypassConfigured;
    private long _encodedStamp = long.MinValue;
    private bool _encodedExact, _playing;
    private int _late, _encodedCount;
    private float _roundTripMs;
    private bool _drained;

    // Whether this scene handed a frame to the encoder (5.4.1A: only that scene waits for it in Collect), and the decoders' count when
    // the picture was last rendered (a picture that lacked an exact video frame is rendered again only once more frames were decoded).
    private bool _submitted;
    private int _decodedAtRender = -1;

    /// <summary>Whether a scene asks for the loopback where it can be: the player's flat, letterboxed picture, no readback.</summary>
    public static bool Wants(in FrameSceneHeader header) =>
        (header.Flags & FrameSceneHeader.FlagLoopback) != 0 && (header.Flags & FrameSceneHeader.FlagLetterbox) != 0
        && (header.Flags & FrameSceneHeader.FlagReadback) == 0 && header.ViewMode == 0;

    // The encoded size whose textures or render failed (0 x 0 when none): the scene draws its plain picture until the size changes.
    private (int Width, int Height) _failedSize;

    /// <summary>
    /// The loopback's textures or render failed at this scene's size: the worker draws the player's plain picture and keeps this
    /// instance only for its state (Failed). A canvas of another size tries again.
    /// </summary>
    public bool Broken(in FrameSceneHeader header) => _failedSize.Width != 0 && _failedSize == RawSize(header);

    // The scene's size (the RAW side's): the canvas's, even, at most 8192 a side.
    private static (int Width, int Height) RawSize(in FrameSceneHeader header)
    {
        var (width, height) = ((Math.Max(header.CanvasWidth, 2) + 1) & ~1, (Math.Max(header.CanvasHeight, 2) + 1) & ~1);
        return (Math.Min(width, 8192), Math.Min(height, 8192));
    }

    // The encoded size (5.4.4-Final): the export's frame size, the canvas's shape kept (ExportLoopbackSettings.EncodedSizeOf).
    private static (int Width, int Height) EncodedSize(in FrameSceneHeader header) =>
        ExportLoopbackSettings.EncodedSizeOf(header.CanvasWidth, header.CanvasHeight, header.LoopbackEncodedWidth, header.LoopbackEncodedHeight);

    /// <summary>
    /// A paused frame is still awaited: the worker looks again in 2 ms, not 100, and <see cref="Poll"/>s for it. 5.4.1A: true until Poll
    /// or <see cref="Collect"/> gives it up (<see cref="InFlight"/>), so the worker always has the turn that gives it up and draws again.
    /// </summary>
    public bool Pending => _awaited >= 0 && !_playing && Find(_awaited) >= 0;

    // A paused frame still in flight: awaited, and handed over less than the encoder's give-up time ago (2 s; a software encoder's, 30 s).
    private bool InFlight => Pending && Find(_awaited) is var index and >= 0
        && Stopwatch.GetElapsedTime(_sources[index]!.HandedAt).TotalMilliseconds < (_encoder?.GiveUpMs ?? GiveUpMs);

    /// <summary>
    /// The frame's half before the output is taken: the scene rendered at the encoded size (only when the picture may have changed:
    /// playing, a new content stamp, or a paused frame that lacked an exact video frame and more frames were decoded since, 5.4.1A),
    /// its NV12 planes kept and handed to the encoder. Inside the worker's drawing window (the decode lanes hold back).
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

        // An encoder that took frames but gave none back (5.4.4): given up on, and the next one of the codec tried.
        if (_encoder is not null && _encodedCount == 0 && _firstSubmit != 0
            && Stopwatch.GetElapsedTime(_firstSubmit).TotalMilliseconds > Math.Max(FirstPictureGiveUpMs, _encoder.GiveUpMs))
        {
            Fail(unchecked((int)0x800705B4), $"waiting {Math.Max(FirstPictureGiveUpMs, _encoder.GiveUpMs)} ms for its first picture", tryNext: true);
            Configure(ref control, header, encodedWidth, encodedHeight);
        }

        // Encoded again when the picture may have changed, or (5.4.4-Final) when the export's settings did: a new stamp of either.
        var encode = _playing || _program is null || header.LoopbackContentStamp != _encodedStamp || header.LoopbackSettingsStamp != _encodedSettingsStamp
                     || (!_encodedExact && decoders.FramesDecoded != _decodedAtRender);
        if (!encode)
        {
            Publish(ref control);
            return;
        }

        // The export's look: the canvas stretched over the scene's size, no player zoom, pan or loupe. Textures the card cannot give
        // (an 8K canvas on a small card) or a render that fails are a state, never a throw out of the worker: the textures are let go
        // and the player draws its plain picture (Broken) until the canvas's size changes.
        int index;
        Source source;
        try
        {
            var made = EnsureTextures(width, height, encodedWidth, encodedHeight);

            // 5.4.4-Final: a paused frame encoded again because only the export's settings changed takes the scene drawn last as it is: not one
            // layer is drawn again (the new settings show as fast as the encoder answers). Playing, or a picture that may have changed, is drawn.
            var redraw = made || _playing || header.LoopbackContentStamp != _renderedStamp
                         || (!_renderedExact && decoders.FramesDecoded != _decodedAtRender);
            if (redraw)
            {
                var flat = header;
                flat.Flags &= ~(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagLoopback);
                (flat.LoupeClipKey, flat.ViewScale, flat.ViewPanX, flat.ViewPanY) = (0, 1, 0, 0);

                // The count before the render: a frame a lane finishes during it (and wakes the worker for) is drawn on that wake.
                var decoded = decoders.FramesDecoded;
                renderer.Render(flat, layers, _program!.Rtv, width, height, raw: false);
                (_renderedStamp, _renderedExact, _decodedAtRender) = (header.LoopbackContentStamp, decoders.LastFrameExact, decoded);
            }

            (_encodedStamp, _encodedExact, _encodedSettingsStamp) = (header.LoopbackContentStamp, _renderedExact, header.LoopbackSettingsStamp);
            index = NextSource();
            source = _sources[index]!;
            _newest = index;
            renderer.ToNv12(_program!, source.Luma, source.Chroma);

            // A smaller (or larger) export: the scene stretched to its size, made NV12 there, for the encoder alone.
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
            // The golden test's compare without a codec: the decoded picture is the source's own NV12.
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

                // The golden test's flag 2 (5.4.1A): the submitting scene does not wait either, so the frame comes back through Poll alone.
                if ((header.LoopbackTestFlags & 2) != 0)
                    _submitted = false;
            }
            catch (GpuException ex) when (!ex.IsDeviceLost)
            {
                Fail(ex.Code, "handing a frame to the encoder (ProcessInput)", tryNext: true);
            }
        }

        // No codec (none on this PC, a codec without a loopback, a failure), or a software encoder while playing (5.4.4-Final: paused frames
        // only): the plain picture. A paused picture that still shows a stand-in for a video frame is not encoded: it shows as it is, and is
        // encoded once the exact frame is drawn.
        if ((_encoder is null || (_playing && _encoder.PausedOnly)) && !_bypass)
            (_shown, _shownDecoded) = (index, false);
        Publish(ref control);
    }

    /// <summary>
    /// The bounded wait for the frame handed over last: its decoded picture, paired with its own source. The output is not held. Paused,
    /// only the scene that handed the frame over waits (5.4.1A): any other (a wipe or loupe moved, the same picture) takes what is back
    /// without waiting and never drains the encoder; the worker's <see cref="Poll"/> draws the frame when it comes.
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

                // A driver that keeps a paused frame back for its look-ahead (Qualcomm's holds several) is told to give it out.
                if (!_playing && !_drained && waited >= DrainAfterMs)
                    _drained = _encoder.Drain();

                // Never a hot spin: the processor is given away, a millisecond at a time once the wait is long. A millisecond on the
                // high-resolution timer: the worker never raises the timer's rate, so a Sleep(1) would wake on the 15.6 ms tick, past
                // the 10 ms a playing frame has.
                if (++spins < 50)
                {
                    Thread.Yield();
                }
                else if (_tick != 0)
                {
                    var due = -(long)(Math.Clamp(budget - waited, 0.1, 1.0) * 10_000);
                    _ = AudioNative.SetWaitableTimer(_tick, &due, 0, 0, 0, 0);
                    _ = AudioNative.WaitForSingleObject(_tick, 2);
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
                // Its turn passed: the last pair stays on screen; the frame is still taken when it comes, and shown only if newest.
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
    /// The worker's look, between scenes, for a paused frame still in the encoder (5.4.1A; on the worker's thread, outside its drawing
    /// window, as <see cref="Collect"/>): what the encoder has given out is decoded, never waited for. True when the picture to show
    /// changed (a newer decoded pair) or the frame was given up: the scene is then drawn again. False otherwise, and the worker draws
    /// nothing (before 5.4.1A it drew the whole scene every 2 ms, each with Collect's wait, while a frame was in flight).
    /// </summary>
    public bool Poll()
    {
        if (_awaited < 0)
            return false;

        var before = (_shown, _shownDecoded, _decoder?.LastTime ?? -1, _state);
        try
        {
            // An encoder that holds frames back (look-ahead) gives a paused frame out only once drained: tried again here while the
            // first try (in the submitting scene's Collect) found it not yet in the encoder; never blocks.
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

        // Given up (or no codec left to give it): the worker draws once more and stops looking.
        if (_awaited >= 0 && (_encoder is null || !InFlight))
        {
            _awaited = -1;
            return true;
        }

        // A newer pair, or a new state (a decoder that could not be made fails here, and is published by the redraw).
        return (_shown, _shownDecoded, _decoder?.LastTime ?? -1, _state) != before;
    }

    /// <summary>The compare into the output (its keyed mutex held by the caller), placed as the player places the canvas.</summary>
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
    /// The loupe's view over the compare (5.4C): the compare picture itself round the loupe's point, <paramref name="scale"/> view pixels
    /// to a canvas pixel, point sampled (Loopback.hlsl, a negative Params.w), so the encoder's blocks and ringing are what is magnified.
    /// The encoded picture, or the difference x the gain in the heatmap's mode (the split wipe's loupe shows the encoded side). False
    /// when there is no picture yet. Outside the keyed mutex: it draws into the renderer's own view (SceneRenderer.LoupeFocus).
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
    /// Where the whole picture lies in a loupe's view of <paramref name="side"/> pixels: scaled by <paramref name="scale"/> about the
    /// point (canvas pixels), which lands in the view's middle. Pure, for the golden tests.
    /// </summary>
    internal static (float X, float Y, float W, float H) LoupePicture(int side, float pointX, float pointY, float scale, int canvasWidth, int canvasHeight) =>
        (side / 2f - pointX * scale, side / 2f - pointY * scale, Math.Max(canvasWidth, 1) * scale, Math.Max(canvasHeight, 1) * scale);

    // The pair shown: paused, a new picture shows as it is until its encoding comes back (a seek never shows the frame before it);
    // playing, the last pair that came back stays (never a source beside another frame's encoding). Null when there is none yet.
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

    // ----- The codec -----

    // The encoder and decoder for the scene's settings: made anew when the codec, the rate control, the bitrate, the quality, the rate
    // or the size changed (a failed set-up is not tried again until one of them does).
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

        // 5.4.4-Final: the preset (quality against speed), 10-bit, the family (whose card's encoder) and a software encoder's own arguments too.
        var software = header.LoopbackFamily == ExportLoopbackSettings.FamilySoftware;
        var config = new LoopbackConfig(codec, width, height, fps, header.LoopbackRateControl, Math.Max(header.LoopbackBitrateKbps, 100), header.LoopbackQuality,
            Math.Clamp(header.LoopbackQualityVsSpeed, 0, 100), header.LoopbackTenBit != 0, header.LoopbackFamily, software ? SoftwareArguments(ref control) : "");
        if (_configured && config == _config && _bypass == _bypassConfigured)
            return;

        CloseCodec();
        // Another codec, family or software command: every encoder may be tried again (a setting fixed may be what it failed on).
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
            // The export's own encoder (5.4.4-Final): a software one through FFmpeg, else the hardware transform on its family's card first.
            _encoder = software
                ? _refused.Count == 0 ? FfmpegLoopbackEncoder.Create(config, out _state) : null
                : MfEncoderMft.Create(gpu, config, header.LoopbackQualityVsSpeed, _refused, out _state, out _hresult);
            _name = _encoder?.Name ?? "";

            // Every encoder of the codec already given up on: the last failure stands (not "no encoder on this PC").
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

    // A software encoder's arguments from the control block (5.4.4-Final), read between two even versions (never half written); the last
    // ones read stand while the client writes.
    private string SoftwareArguments(ref ControlBlock control)
    {
        var version = Volatile.Read(ref control.LoopbackArgumentsVersion);
        if (version == _argumentsVersion || (version & 1) != 0)
            return _softwareArguments;
        ReadOnlySpan<byte> field = control.LoopbackSoftwareArguments;
        var end = field.IndexOf((byte)0);
        var text = System.Text.Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
        if (Volatile.Read(ref control.LoopbackArgumentsVersion) != version)
            return _softwareArguments;
        (_softwareArguments, _argumentsVersion) = (text, version);
        return text;
    }

    // One bitstream sample through the decoder (made on the first, with the encoder's sequence header where it has one).
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

        // 5.4.4-Final (the user's): paused, a decoder that keeps the frame back (its reorder buffer of one picture) is drained at once, so the
        // paused frame shows without waiting for a scrub or a play; the next frame handed over is then a key frame.
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

        // Shown only when it is newer than the pair on screen (a late frame that came back after a newer one is passed over).
        if (_shown < 0 || !_shownDecoded || _sources[_shown] is not { } shown || shown.Time <= time)
            (_shown, _shownDecoded) = (index, true);
        if (time == _awaited)
            _awaited = -1;
    }

    // The encoder's planes into an NV12 sample in memory: Y rows, then the UV rows, each the encoded width (the card waited for here). 5.4.4-Final:
    // P010 for an encoder that took 10-bit, each 8-bit value at the top of its 16 bits (the scene is drawn in 8 bits: nothing is lost or made up).
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

    /// <summary>A row of 8-bit samples into the sample's memory: as they are, or (P010) each at the top of 16 bits. Pure, for the golden test.</summary>
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

    // A failure: the plain picture, said in HandPeg.log (5.4.4). With tryNext, the encoder that failed is not tried again for this codec
    // and the next scene sets the codec up anew with the next one (until none is left: then the failure stands).
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

    // The scene's textures at its size, and (5.4.4-Final) the export's own when it is another size: true when anything was made anew (the
    // scene is then drawn again, its picture gone).
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

    // The slot for the next frame: never the pair on screen, else the oldest.
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

    // The slot holding the frame of this sample time; -1 when it is gone.
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
        // Each new state in HandPeg.log and the breadcrumbs (5.4.4): what the ENCODED pill says, and why.
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

    /// <summary>Everything let go: the transforms, the textures (nothing of the loopback survives its being turned off).</summary>
    public void Dispose()
    {
        CloseCodec();
        ReleaseTextures();
        _configured = false;
        if (_tick != 0)
        {
            _ = AudioNative.CloseHandle(_tick);
            _tick = 0;
        }
    }
}
