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

    // A paused frame not back this soon is drained out of the encoder (a low-latency one has given it by then).
    private const int DrainAfterMs = 4;

    // A paused frame still in flight after this long is given up on (the worker stops looking every 2 ms).
    private const int GiveUpMs = 2000;

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

    private MfEncoderMft? _encoder;
    private MfDecoderMft? _decoder;
    private LoopbackConfig _config;
    private bool _configured, _bypass;
    private ExportLoopbackState _state;
    private int _hresult;
    private string _name = "";

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
    public bool Broken(in FrameSceneHeader header) => _failedSize.Width != 0 && _failedSize == EncodedSize(header);

    // The encoded size: the canvas's, even, at most 8192 a side.
    private static (int Width, int Height) EncodedSize(in FrameSceneHeader header)
    {
        var (width, height) = ((Math.Max(header.CanvasWidth, 2) + 1) & ~1, (Math.Max(header.CanvasHeight, 2) + 1) & ~1);
        return (Math.Min(width, 8192), Math.Min(height, 8192));
    }

    /// <summary>
    /// A paused frame is still awaited: the worker looks again in 2 ms, not 100, and <see cref="Poll"/>s for it. 5.4.1A: true until Poll
    /// or <see cref="Collect"/> gives it up (<see cref="InFlight"/>), so the worker always has the turn that gives it up and draws again.
    /// </summary>
    public bool Pending => _awaited >= 0 && !_playing && Find(_awaited) >= 0;

    // A paused frame still in flight: awaited, and handed over less than GiveUpMs ago.
    private bool InFlight => Pending && Find(_awaited) is var index and >= 0
        && Stopwatch.GetElapsedTime(_sources[index]!.HandedAt).TotalMilliseconds < GiveUpMs;

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
        var (width, height) = EncodedSize(header);
        _failedSize = (0, 0);
        Configure(header, width, height);

        var encode = _playing || _program is null || header.LoopbackContentStamp != _encodedStamp
                     || (!_encodedExact && decoders.FramesDecoded != _decodedAtRender);
        if (!encode)
        {
            Publish(ref control);
            return;
        }

        // The export's look: the canvas stretched over the encoded size, no player zoom, pan or loupe. Textures the card cannot give
        // (an 8K canvas on a small card) or a render that fails are a state, never a throw out of the worker: the textures are let go
        // and the player draws its plain picture (Broken) until the canvas's size changes.
        int index;
        Source source;
        try
        {
            EnsureTextures(width, height);
            var flat = header;
            flat.Flags &= ~(FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagLoopback);
            (flat.LoupeClipKey, flat.ViewScale, flat.ViewPanX, flat.ViewPanY) = (0, 1, 0, 0);

            // The count before the render: a frame a lane finishes during it (and wakes the worker for) is drawn on that wake.
            var decoded = decoders.FramesDecoded;
            renderer.Render(flat, layers, _program!.Rtv, width, height, raw: false);
            (_encodedStamp, _encodedExact, _decodedAtRender) = (header.LoopbackContentStamp, decoders.LastFrameExact, decoded);

            index = NextSource();
            source = _sources[index]!;
            _newest = index;
            renderer.ToNv12(_program, source.Luma, source.Chroma);
            source.Time = _frames * 10_000_000L * 1000 / FpsMilli(_config.Fps);
            source.HandedAt = Stopwatch.GetTimestamp();
            _frames++;
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            ReleaseTextures();
            (_shown, _newest, _shownDecoded) = (-1, -1, false);
            _failedSize = (width, height);
            Fail(ex.Code);
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
        else if (_encoder is not null && (_playing || _encodedExact))
        {
            try
            {
                _encoder.SetQualityVsSpeed(_playing ? Math.Min(header.LoopbackQualityVsSpeed, 33) : header.LoopbackQualityVsSpeed);
                _encoder.Submit(SampleOf(source), forceKey: !_playing);
                _awaited = source.Time;
                (_drained, _submitted) = (false, true);

                // The golden test's flag 2 (5.4.1A): the submitting scene does not wait either, so the frame comes back through Poll alone.
                if ((header.LoopbackTestFlags & 2) != 0)
                    _submitted = false;
            }
            catch (GpuException ex) when (!ex.IsDeviceLost)
            {
                Fail(ex.Code);
            }
        }

        // No codec (none on this PC, a codec without a loopback, a failure): the plain picture. A paused picture that still shows a
        // stand-in for a video frame is not encoded: it shows as it is, and is encoded once the exact frame is drawn.
        if (_encoder is null && !_bypass)
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
            Fail(ex.Code);
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
            Fail(ex.Code);
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
    private void Configure(in FrameSceneHeader header, int width, int height)
    {
        var codec = header.LoopbackCodec switch
        {
            1 => MfGuids.H264,
            2 => MfGuids.Hevc,
            3 => MfGuids.Av1,
            _ => Guid.Empty,
        };
        var fps = header.LoopbackFrameRate > 0 && float.IsFinite(header.LoopbackFrameRate) ? Math.Clamp(header.LoopbackFrameRate, 1, 1000) : 30;
        var config = new LoopbackConfig(codec, width, height, fps, header.LoopbackRateControl, Math.Max(header.LoopbackBitrateKbps, 100), header.LoopbackQuality);
        if (_configured && config == _config && _bypass == _bypassConfigured)
            return;

        CloseCodec();
        (_config, _configured, _bypassConfigured) = (config, true, _bypass);
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
            _encoder = MfEncoderMft.Create(gpu, config, header.LoopbackQualityVsSpeed, out _state, out _hresult);
            _name = _encoder?.Name ?? "";
        }
        catch (GpuException ex) when (!ex.IsDeviceLost)
        {
            Fail(ex.Code);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Fail(ex.HResult);
        }
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

        if (!_decoder.Decode(bitstream))
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

    // The source's planes into an NV12 sample in memory: Y rows, then the UV rows, each the encoded width (the card waited for here).
    private nint SampleOf(Source source)
    {
        var (width, height) = _size;
        gpu.Context.CopyResource(_stagingLuma, source.Luma.Texture);
        gpu.Context.CopyResource(_stagingChroma, source.Chroma.Texture);
        var bytes = (uint)(width * height * 3 / 2);
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
                        Buffer.MemoryCopy(luma.Data + (long)y * luma.RowPitch, data + (long)y * width, width, width);
                }
                finally
                {
                    gpu.Context.Unmap(_stagingLuma, 0);
                }

                Com.Check(gpu.Context.Map(_stagingChroma, 0, D3D11.MapRead, 0, out var chroma), "Map loopback (UV)");
                try
                {
                    var start = data + (long)width * height;
                    for (var y = 0; y < height / 2; y++)
                        Buffer.MemoryCopy(chroma.Data + (long)y * chroma.RowPitch, start + (long)y * width, width, width);
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
            _ = Mf.SetSampleTime(sample, source.Time);
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

    private static long FpsMilli(double fps) => Math.Max((long)Math.Round(fps * 1000), 1000);

    private void Fail(int hresult)
    {
        (_state, _hresult, _awaited) = (ExportLoopbackState.Failed, hresult, -1);
        CloseCodec();
    }

    private void CloseCodec()
    {
        _decoder?.Dispose();
        _encoder?.Dispose();
        (_decoder, _encoder) = (null, null);
        _shownDecoded = false;
    }

    // ----- Textures -----

    private void EnsureTextures(int width, int height)
    {
        if (_program is not null && _size == (width, height))
            return;
        ReleaseTextures();
        _size = (width, height);
        _program = gpu.CreateTexture(width, height, DxgiFormat.B8G8R8A8Unorm, renderTarget: true);
        for (var i = 0; i < Sources; i++)
            _sources[i] = new Source(gpu.CreateTexture(width, height, DxgiFormat.R8Unorm, renderTarget: true), gpu.CreateTexture(width / 2, height / 2, DxgiFormat.R8G8Unorm, renderTarget: true));
        _stagingLuma = gpu.CreateStaging(width, height, DxgiFormat.R8Unorm);
        _stagingChroma = gpu.CreateStaging(width / 2, height / 2, DxgiFormat.R8G8Unorm);
        (_shown, _newest, _shownDecoded, _awaited) = (-1, -1, false, -1);
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
        control.LoopbackState = (int)_state;
        control.LoopbackHResult = _hresult;
        control.LoopbackRoundTripMs = _roundTripMs;
        control.LoopbackFramesEncoded = _encodedCount;
        control.LoopbackLateFrames = _late;
        control.LoopbackCodecsAvailable = _state == ExportLoopbackState.Running && !_bypass ? _config.Codec == MfGuids.H264 ? 1 : _config.Codec == MfGuids.Hevc ? 2 : 4 : 0;
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
