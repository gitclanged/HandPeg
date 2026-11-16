namespace HandPegApp.Services;

/// <summary>
/// Compositor encode-loopback status (raw ControlBlock Loopback* fields), including whether the encoder runs on the export family's
/// adapter (false: a substitute adapter) and whether it encodes paused frames only (software encoders).
/// </summary>
public readonly record struct ExportLoopbackStatus(ExportLoopbackState State, string EncoderName, float RoundTripMs, int FramesEncoded, int LateFrames, int HResult,
    bool OnAskedCard = true, bool PausedOnly = false);

// ----- Live Encoded Preview: the player frame round-tripped through the export encoder -----
// Off (null settings): no scene data and no worker resources (FrameSceneHeader.FlagLoopback never set). On: every published scene
// requests it (ExportLoopbackPreview) with codec, rate control, view mode (encoded, split wipe, difference x10) and a content stamp
// raised when the picture may change; paused frames re-encode only on a stamp change, never for wipe or mode moves.
public sealed partial class GpuPreviewEngine
{
    private ExportLoopbackSettings? _loopback;
    private int _loopbackMode = 1;
    private float _loopbackWipe = 0.5f;

    // Content stamp and a dirty flag (set by Invalidate) for picture-affecting changes since the last write.
    private long _loopbackStamp;
    private int _loopbackStale = 1;

    /// <summary>The difference's magnification (the roadmap's "x10").</summary>
    public const float LoopbackHeatGain = 10;

    // Incremented on every settings change while on (FrameSceneHeader.LoopbackSettingsStamp).
    private long _loopbackSettingsStamp;

    /// <summary>
    /// Enables the preview with settings, or disables it (null). Any thread. Enabling raises the content stamp (fresh render); changing
    /// settings while on raises only the settings stamp, so the worker re-encodes its existing scene without re-rendering layers.
    /// </summary>
    public void SetLoopback(ExportLoopbackSettings? settings)
    {
        var was = Volatile.Read(ref _loopback);
        if (Equals(was, settings))
            return;
        if (settings is not null)
            WriteSoftwareArguments(settings.SoftwareArguments);
        Interlocked.Increment(ref _loopbackSettingsStamp);
        Volatile.Write(ref _loopback, settings);
        if (was is null || settings is null)
        {
            Invalidate();
            return;
        }

        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
    }

    /// <summary>
    /// Writes software encoder arguments to the control block (UTF-8, max 255 bytes, truncated at a whole argument) bracketed by odd/even
    /// <see cref="ControlBlock.LoopbackArgumentsVersion"/> so the worker never reads a torn string. Packing is pure (<see cref="PackArguments"/>).
    /// </summary>
    private void WriteSoftwareArguments(string arguments)
    {
        ref var control = ref _client.Memory.Control;
        var bytes = PackArguments(arguments);
        lock (_loopbackArgumentsLock)
        {
            Interlocked.Increment(ref control.LoopbackArgumentsVersion);
            Span<byte> field = control.LoopbackSoftwareArguments;
            field.Clear();
            bytes.CopyTo(field);
            Interlocked.Increment(ref control.LoopbackArgumentsVersion);
        }
    }

    private readonly object _loopbackArgumentsLock = new();

    /// <summary>Encodes arguments as UTF-8 (max 255 bytes), truncated at a space so no argument is split.</summary>
    internal static byte[] PackArguments(string arguments)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes((arguments ?? "").Trim());
        if (bytes.Length <= 255)
            return bytes;
        var cut = Array.LastIndexOf(bytes, (byte)' ', 254);
        return bytes[..Math.Max(cut, 0)];
    }

    /// <summary>True when the preview is enabled.</summary>
    public bool LoopbackOn => Volatile.Read(ref _loopback) is not null;

    /// <summary>
    /// View mode: 0 encoded, 1 split wipe at <paramref name="wipe"/> (0..1 of picture width, source left), 2 difference x10. Redrawn from
    /// worker state without re-encoding.
    /// </summary>
    public void SetLoopbackView(int mode, double wipe)
    {
        var (m, w) = (Math.Clamp(mode, 0, 2), (float)Math.Clamp(double.IsFinite(wipe) ? wipe : 0.5, 0, 1));
        var changed = Interlocked.Exchange(ref _loopbackMode, m) != m;
        changed |= Interlocked.Exchange(ref _loopbackWipe, w) != w;
        if (!changed || Volatile.Read(ref _loopback) is null)
            return;

        // Not Invalidate: the content stamp stays, so the worker redraws its compare without a new encode.
        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
    }

    /// <summary>What the worker's loopback is doing now (its control block).</summary>
    public ExportLoopbackStatus LoopbackStatus
    {
        get
        {
            ref var control = ref _client.Memory.Control;

            // Source view carries no loopback (the worker releases it and reports Off), so it is absent, not pending.
            var state = (ExportLoopbackState)Volatile.Read(ref control.LoopbackState);
            if (_sourceView && Volatile.Read(ref _loopback) is not null && state == ExportLoopbackState.Off)
                state = ExportLoopbackState.Unavailable;
            return new ExportLoopbackStatus(state, CompositorSharedMemory.ReadString(control.LoopbackEncoderName),
                control.LoopbackRoundTripMs, control.LoopbackFramesEncoded, control.LoopbackLateFrames, control.LoopbackHResult,
                Volatile.Read(ref control.LoopbackOnAskedCard) != 0, Volatile.Read(ref control.LoopbackPausedOnly) != 0);
        }
    }

    /// <summary>
    /// Horizontal picture extent (left, width) in <paramref name="viewportWidth"/> units (DIPs or pixels), matching SceneRenderer.Letterbox
    /// (fit, zoom, pan). Used for the wipe handle.
    /// </summary>
    public (double Left, double Width) PictureSpan(double viewportWidth, double viewportHeight)
    {
        var (left, _, width, _) = PictureRect(viewportWidth, viewportHeight);
        return (left, width);
    }

    /// <summary>
    /// Wipe fraction (0..1) under <paramref name="pointerX"/> within <paramref name="span"/> (<see cref="PictureSpan"/>, same units); 0.5
    /// for zero width. Handle and worker share the span so they align. Pure (golden-tested).
    /// </summary>
    internal static double WipeAt(double pointerX, (double Left, double Width) span) =>
        span.Width > 1 && double.IsFinite(pointerX) && double.IsFinite(span.Left) ? Math.Max(0, Math.Min((pointerX - span.Left) / span.Width, 1)) : 0.5;

    /// <summary>
    /// Wipe line position: span left + <paramref name="wipe"/> x width, in <paramref name="span"/> units, matching Loopback.hlsl. Inverse
    /// of <see cref="WipeAt"/>. Pure (golden-tested).
    /// </summary>
    internal static double WipeLineX(double wipe, (double Left, double Width) span) =>
        span.Left + Math.Max(0, Math.Min(double.IsFinite(wipe) ? wipe : 0.5, 1)) * span.Width;

    /// <summary>
    /// Full picture rectangle in <paramref name="viewportWidth"/> units (<see cref="PictureSpan"/> plus top and height, for the
    /// Magnifier), fitted, zoomed and panned like the worker.
    /// </summary>
    public (double Left, double Top, double Width, double Height) PictureRect(double viewportWidth, double viewportHeight)
    {
        var plan = Volatile.Read(ref _plan);
        var (canvasWidth, canvasHeight) = plan.CanvasWidth > 0 && plan.CanvasHeight > 0 ? (plan.CanvasWidth, plan.CanvasHeight) : (1920, 1080);
        return PictureRectOf(canvasWidth, canvasHeight, _viewScale, _viewPanX, _viewPanY, viewportWidth, viewportHeight);
    }

    /// <summary>
    /// Client-side SceneRenderer.Letterbox: fit and centre, zoom by <paramref name="viewScale"/> about the centre, then pan (fractions of
    /// the zoomed picture). Pure (golden-tested).
    /// </summary>
    internal static (double Left, double Top, double Width, double Height) PictureRectOf(int canvasWidth, int canvasHeight, double viewScale, double panX, double panY,
        double viewportWidth, double viewportHeight)
    {
        (canvasWidth, canvasHeight) = (Math.Max(canvasWidth, 1), Math.Max(canvasHeight, 1));
        var fit = Math.Min(viewportWidth / canvasWidth, viewportHeight / canvasHeight);
        var zoom = Math.Clamp(viewScale > 0 ? viewScale : 1, 0.05, 64);
        var scale = fit * zoom;
        return (viewportWidth / 2 + panX * canvasWidth * scale - canvasWidth * scale / 2, viewportHeight / 2 + panY * canvasHeight * scale - canvasHeight * scale / 2,
            canvasWidth * scale, canvasHeight * scale);
    }

    /// <summary>
    /// Magnifier point: pointer (<paramref name="pointerX"/>, <paramref name="pointerY"/>) in a <paramref name="viewportWidth"/> x
    /// <paramref name="viewportHeight"/> player (units of <paramref name="picture"/>) as output fractions (scaled by the worker); null
    /// outside the picture or for a zero-size player.
    /// </summary>
    internal static (double X, double Y)? LoupePointOf(double pointerX, double pointerY, double viewportWidth, double viewportHeight,
        (double Left, double Top, double Width, double Height) picture)
    {
        if (!(viewportWidth >= 1 && viewportHeight >= 1) || !double.IsFinite(pointerX) || !double.IsFinite(pointerY))
            return null;
        var left = Math.Max(picture.Left, 0);
        var top = Math.Max(picture.Top, 0);
        var right = Math.Min(picture.Left + picture.Width, viewportWidth);
        var bottom = Math.Min(picture.Top + picture.Height, viewportHeight);
        if (pointerX < left || pointerX >= right || pointerY < top || pointerY >= bottom)
            return null;
        return (pointerX / viewportWidth, pointerY / viewportHeight);
    }

    // The Magnifier's point, fractions of the output; NaN: none.
    private float _loopbackLoupeX = float.NaN, _loopbackLoupeY = float.NaN;

    /// <summary>
    /// Shows the preview Magnifier at an output point (fractions, <see cref="LoupePointOf"/>) or hides it (null). Redrawn from worker
    /// state without re-encoding, like <see cref="SetLoopbackView"/>. Any thread.
    /// </summary>
    public void SetLoopbackLoupe(double? outputFractionX, double? outputFractionY)
    {
        var (x, y) = outputFractionX is { } fx && outputFractionY is { } fy && double.IsFinite(fx) && double.IsFinite(fy)
            ? ((float)Math.Max(0, Math.Min(fx, 1)), (float)Math.Max(0, Math.Min(fy, 1)))
            : (float.NaN, float.NaN);
        var changed = !Interlocked.Exchange(ref _loopbackLoupeX, x).Equals(x);
        changed |= !Interlocked.Exchange(ref _loopbackLoupeY, y).Equals(y);
        if (!changed || Volatile.Read(ref _loopback) is null)
            return;

        // Not Invalidate: the content stamp stays (a paused frame is not encoded again for a loupe moved).
        Volatile.Write(ref _sceneDirty, 1);
        _dirty.Set();
    }

    // Into the scene the pump publishes (PublishAt, after the camera: the worker leaves the 3D view and a readback alone).
    private void WriteLoopback(ref FrameSceneHeader header)
    {
        if (Volatile.Read(ref _loopback) is not { } settings)
            return;

        if (Interlocked.Exchange(ref _loopbackStale, 0) != 0 || header.IsPlaying != 0)
            _loopbackStamp++;
        header.Flags |= FrameSceneHeader.FlagLoopback;
        header.LoopbackCodec = settings.Codec;
        header.LoopbackRateControl = settings.RateControl;
        header.LoopbackBitrateKbps = settings.BitrateKbps;
        header.LoopbackQuality = settings.Quality;
        header.LoopbackQualityVsSpeed = settings.QualityVsSpeed;
        header.LoopbackFrameRate = (float)settings.FrameRate;
        header.LoopbackMode = Volatile.Read(ref _loopbackMode);
        header.LoopbackWipeX = Volatile.Read(ref _loopbackWipe);
        header.LoopbackHeatGain = LoopbackHeatGain;
        header.LoopbackContentStamp = _loopbackStamp;

        // The exact Export tab encoder and all of its settings.
        header.LoopbackFamily = settings.Family;
        (header.LoopbackEncodedWidth, header.LoopbackEncodedHeight) = (Math.Max(settings.EncodedWidth, 0), Math.Max(settings.EncodedHeight, 0));
        header.LoopbackTenBit = settings.TenBit ? 1 : 0;
        header.LoopbackCrf = settings.Crf;
        header.LoopbackSettingsStamp = Interlocked.Read(ref _loopbackSettingsStamp);

        // Magnifier: flat view only (source view writes no loopback).
        var (loupeX, loupeY) = (Volatile.Read(ref _loopbackLoupeX), Volatile.Read(ref _loopbackLoupeY));
        if (float.IsFinite(loupeX) && float.IsFinite(loupeY))
        {
            header.Flags |= FrameSceneHeader.FlagLoopbackLoupe;
            (header.LoopbackLoupeX, header.LoopbackLoupeY) = (loupeX, loupeY);
            if (header.LoupeZoom <= 0)
                header.LoupeZoom = LoupeZoom;
        }
    }
}
