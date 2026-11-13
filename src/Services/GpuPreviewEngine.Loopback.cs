namespace HandPegApp.Services;

/// <summary>
/// What the compositor's encode loopback is doing (ControlBlock's Loopback* fields, read as they are). 5.4.4-Final: whether the encoder is on
/// the card the export's family names (false: a stand-in on another card) and whether it encodes paused frames only (a software encoder).
/// </summary>
public readonly record struct ExportLoopbackStatus(ExportLoopbackState State, string EncoderName, float RoundTripMs, int FramesEncoded, int LateFrames, int HResult,
    bool OnAskedCard = true, bool PausedOnly = false);

// ----- The Export Quality Preview (Pass 5.4B): the player's frame through the export's encoder and back -----
// Off (null settings), nothing of it is written into a scene and the worker holds nothing for it (FrameSceneHeader.FlagLoopback is
// never set): the player's path is as it was. On, every scene the pump publishes asks the worker for it (ExportLoopbackPreview), with
// the export's codec and rate control, what is shown (the encoded picture, a split wipe, the difference x10), and a content stamp
// raised whenever the picture may have changed: a paused frame is encoded again only then, never for a wipe or mode moved.
public sealed partial class GpuPreviewEngine
{
    private ExportLoopbackSettings? _loopback;
    private int _loopbackMode = 1;
    private float _loopbackWipe = 0.5f;

    // The content stamp, and whether something that may change the picture happened since it was last written (Invalidate sets it).
    private long _loopbackStamp;
    private int _loopbackStale = 1;

    /// <summary>The difference's magnification (the roadmap's "x10").</summary>
    public const float LoopbackHeatGain = 10;

    // Raised by every change of the settings while the preview is on (5.4.4-Final, FrameSceneHeader.LoopbackSettingsStamp).
    private long _loopbackSettingsStamp;

    /// <summary>
    /// The Export Quality Preview on with these settings, or off (null). Any thread. 5.4.4-Final: turned on, the picture is drawn anew (the
    /// content stamp raised); new settings while it is on keep the content stamp (the worker encodes the scene it has already drawn again,
    /// no layer drawn for it: a paused frame updates as fast as the encoder answers) and raise the settings' own stamp.
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
    /// A software encoder's arguments into the control block (UTF-8, at most 255 bytes, cut at the last whole argument that fits), between an
    /// odd and an even <see cref="ControlBlock.LoopbackArgumentsVersion"/>: the worker never takes a half-written string. Pure in its packing
    /// (<see cref="PackArguments"/>, for the golden test).
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

    /// <summary>The arguments as the control block holds them: UTF-8, at most 255 bytes, cut at a space so no argument is half there.</summary>
    internal static byte[] PackArguments(string arguments)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes((arguments ?? "").Trim());
        if (bytes.Length <= 255)
            return bytes;
        var cut = Array.LastIndexOf(bytes, (byte)' ', 254);
        return bytes[..Math.Max(cut, 0)];
    }

    /// <summary>Whether the preview is on (settings given).</summary>
    public bool LoopbackOn => Volatile.Read(ref _loopback) is not null;

    /// <summary>
    /// What the preview shows: <paramref name="mode"/> 0 the encoded picture, 1 a split wipe at <paramref name="wipe"/> (0..1 of the
    /// picture's width, source on the left), 2 the difference x10. Drawn again from what the worker has, not encoded again.
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

            // A clip's source shown alone carries no loopback (the worker lets it go and says Off): it is not in this view, not waiting.
            var state = (ExportLoopbackState)Volatile.Read(ref control.LoopbackState);
            if (_sourceView && Volatile.Read(ref _loopback) is not null && state == ExportLoopbackState.Off)
                state = ExportLoopbackState.Unavailable;
            return new ExportLoopbackStatus(state, CompositorSharedMemory.ReadString(control.LoopbackEncoderName),
                control.LoopbackRoundTripMs, control.LoopbackFramesEncoded, control.LoopbackLateFrames, control.LoopbackHResult,
                Volatile.Read(ref control.LoopbackOnAskedCard) != 0, Volatile.Read(ref control.LoopbackPausedOnly) != 0);
        }
    }

    /// <summary>
    /// Where the picture lies across the player, in the units of <paramref name="viewportWidth"/> (DIPs or pixels: the letterbox scales
    /// alike): its left edge and its width, as the worker places it (SceneRenderer.Letterbox: fitted, zoomed, panned). For the wipe's handle.
    /// </summary>
    public (double Left, double Width) PictureSpan(double viewportWidth, double viewportHeight)
    {
        var (left, _, width, _) = PictureRect(viewportWidth, viewportHeight);
        return (left, width);
    }

    /// <summary>
    /// The split wipe under a pointer at <paramref name="pointerX"/> (5.4.1A): its fraction of the picture's width (<paramref name="span"/>,
    /// <see cref="PictureSpan"/> in the same units), 0..1; 0.5 for a picture of no width. The handle's drag and the worker's line use the
    /// same span, so the line is where the handle is. Pure, for the golden tests.
    /// </summary>
    internal static double WipeAt(double pointerX, (double Left, double Width) span) =>
        span.Width > 1 && double.IsFinite(pointerX) && double.IsFinite(span.Left) ? Math.Max(0, Math.Min((pointerX - span.Left) / span.Width, 1)) : 0.5;

    /// <summary>
    /// Where the split wipe's line lies across the player (5.4.1A): <paramref name="wipe"/> (0..1) of the picture's width from its left edge,
    /// in the units of <paramref name="span"/>, as the worker draws it (Loopback.hlsl: the picture's left plus the wipe times its width).
    /// The inverse of <see cref="WipeAt"/>. Pure, for the golden tests.
    /// </summary>
    internal static double WipeLineX(double wipe, (double Left, double Width) span) =>
        span.Left + Math.Max(0, Math.Min(double.IsFinite(wipe) ? wipe : 0.5, 1)) * span.Width;

    /// <summary>
    /// Where the picture lies on the player, in the units of <paramref name="viewportWidth"/> (5.4D: <see cref="PictureSpan"/> with its
    /// top and height, for the Magnifier): fitted, zoomed and panned as the worker places it.
    /// </summary>
    public (double Left, double Top, double Width, double Height) PictureRect(double viewportWidth, double viewportHeight)
    {
        var plan = Volatile.Read(ref _plan);
        var (canvasWidth, canvasHeight) = plan.CanvasWidth > 0 && plan.CanvasHeight > 0 ? (plan.CanvasWidth, plan.CanvasHeight) : (1920, 1080);
        return PictureRectOf(canvasWidth, canvasHeight, _viewScale, _viewPanX, _viewPanY, viewportWidth, viewportHeight);
    }

    /// <summary>
    /// SceneRenderer.Letterbox in the client's units: a canvas fitted into the viewport and centred, zoomed by <paramref name="viewScale"/>
    /// about the middle and moved by the pan (fractions of the zoomed picture). Pure, for the golden tests.
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
    /// The Magnifier's point (5.4D): a pointer at <paramref name="pointerX"/>, <paramref name="pointerY"/> on a player of
    /// <paramref name="viewportWidth"/> x <paramref name="viewportHeight"/> (the units of <paramref name="picture"/>) as fractions of the
    /// player's output, which the worker multiplies by its own output's size; null off the picture (the bars, or the player's zoom
    /// leaving the pointer over nothing) or on a player with no size.
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
    /// The Export Quality Preview's Magnifier (5.4D) at a point of the output (fractions of its width and height: <see cref="LoupePointOf"/>),
    /// or put away (null). Drawn again from what the worker has, never encoded again, as <see cref="SetLoopbackView"/>. Any thread.
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

        // 5.4.4-Final: the exact encoder the Export tab chose and every setting it has.
        header.LoopbackFamily = settings.Family;
        (header.LoopbackEncodedWidth, header.LoopbackEncodedHeight) = (Math.Max(settings.EncodedWidth, 0), Math.Max(settings.EncodedHeight, 0));
        header.LoopbackTenBit = settings.TenBit ? 1 : 0;
        header.LoopbackCrf = settings.Crf;
        header.LoopbackSettingsStamp = Interlocked.Read(ref _loopbackSettingsStamp);

        // The Magnifier (5.4D): only in the flat view this is written for (never in source view, which writes no loopback).
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
