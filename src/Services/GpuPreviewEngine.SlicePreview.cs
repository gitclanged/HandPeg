using System.Diagnostics;

namespace HandPegApp.Services;

/// <summary>
/// A Slice-Preview strip read back from the compositor (Pass 5.7.1): BGRA, rows packed, one cell of <paramref name="CellWidth"/> pixels
/// per slot of <paramref name="Request"/> side by side in slot order. <paramref name="Ticket"/> is the one
/// <see cref="GpuPreviewEngine.RequestSlicePreview"/> returned.
/// </summary>
public sealed record SlicePreviewStrip(int Ticket, SlicePreviewRequest Request, byte[] Pixels, int Width, int Height, int CellWidth);

// ----- Slice-Preview burst and strip (Pass 5.7.1) -----
// The ribbon (MainWindow.Scrub.cs) asks with RequestSlicePreview (UI thread: the file is resolved to its source-table entry there, as
// PrewarmTabs does). The pump posts the newest ask as the shared-memory slice burst (CompositorClient.PostSliceBurst) and, once the worker
// answers that every slice is in its frame cache (SliceBurstDone), draws the slots side by side in one small read-back scene: from cached
// frames, so the worker never waits, at most every SliceStripEveryMs and only paused with no seek pending and no player picture owed.
// The pump never waits for that readback: it polls it on later turns, after the ghost pause and the seek are applied (PollSliceStrip), and
// publishes the player's own scene again once it is read back, replaced or overdue. Dormant while playing (any burst is withdrawn) and in
// the 3D view (a flat scene would release its plane textures on small cards). The UI takes the strip with TakeSlicePreview from its frame
// hook: no dispatcher traffic, nothing queued.
public sealed partial class GpuPreviewEngine
{
    /// <summary>Longest time, ms, a strip's readback is polled for on later pump turns (its frames are cached; normally a few ms).</summary>
    internal const int SliceReadbackMs = 60;

    /// <summary>Least time, ms, between two strips drawn on the pump (each costs one small scene and the player's redraw).</summary>
    internal const int SliceStripEveryMs = 60;

    // Decoder affinity key for the strip's layers (one key: the slices of a burst are one file, read forward).
    private const long SliceClipKey = 0x5_11CE_5_7_1;

    // UI -> pump: the newest ask (null once taken); Request null withdraws. Cell sizes in pixels.
    private sealed record SliceAsk(int Ticket, SlicePreviewRequest? Request, int SourceId, long PathHash, int CellWidth, int CellHeight);

    private SliceAsk? _sliceAsk;
    private int _sliceTicket;

    // Pump's own: the burst posted (its version, 0 for none, and its ask), whether its strip was drawn, and when the last one was.
    private int _sliceVersion;
    private SliceAsk? _slicePosted;
    private bool _sliceDrawn;
    private long _sliceDrawnAt;

    // Pump's own: the strip scene published and not read back yet (its sequence, 0 for none), its ask, and when it was published.
    private long _sliceReadback;
    private SliceAsk? _sliceReading;
    private long _sliceReadbackAt;

    // Pump -> UI: the newest strip not yet taken.
    private SlicePreviewStrip? _sliceStrip;

    /// <summary>
    /// The ribbon's frames (UI thread): the request's file is resolved here and the pump posts its slices as the newest burst; null (or an
    /// invalid request) withdraws it. <paramref name="aspect"/> is the cells' width over height. Returns the ask's ticket (0 when
    /// withdrawn), matched by <see cref="SlicePreviewStrip.Ticket"/>.
    /// </summary>
    public int RequestSlicePreview(SlicePreviewRequest? request, double aspect = 16.0 / 9)
    {
        _uiOwner.Check();
        var ticket = ++_sliceTicket;
        var source = request is { IsValid: true } ? _broker.SourceFor(request.Path, request.Stream) : -1;
        if (source < 0)
        {
            Volatile.Write(ref _sliceAsk, new SliceAsk(ticket, null, -1, 0, 0, 0));
            WakePaused();
            return 0;
        }

        var (cellWidth, cellHeight) = SliceCellSize(request!.ShortSide, aspect);
        Volatile.Write(ref _sliceAsk, new SliceAsk(ticket, request, source, _broker.PathHashOf(source), cellWidth, cellHeight));
        WakePaused();
        return ticket;
    }

    /// <summary>The newest strip read back since the last call (UI thread, the ribbon's frame hook), or null.</summary>
    public SlicePreviewStrip? TakeSlicePreview() => Interlocked.Exchange(ref _sliceStrip, null);

    /// <summary>
    /// A strip cell's size in pixels for a thumbnail short side and a width-over-height shape: even sides (the read-back scene's chroma),
    /// the short side 16-256, the long one at most four times it. Pure; goldens.
    /// </summary>
    internal static (int Width, int Height) SliceCellSize(int shortSide, double aspect)
    {
        var side = Math.Clamp(shortSide, 16, 256) / 2 * 2;
        var shape = double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, 0.25, 4) : 16.0 / 9;
        var other = Math.Max((int)Math.Round(side * Math.Max(shape, 1 / shape) / 2) * 2, side);
        return shape >= 1 ? (other, side) : (side, other);
    }

    /// <summary>
    /// Whether the pump draws a posted burst's strip now: not yet drawn, answered done, paused with no seek pending, flat view, and
    /// <see cref="SliceStripEveryMs"/> since the last strip. Pure; goldens.
    /// </summary>
    internal static bool SliceStripDue(bool posted, bool drawn, bool done, bool seekPending, bool exploded, long now, long lastDrawnAt, long frequency) =>
        posted && !drawn && done && !seekPending && !exploded && (lastDrawnAt == 0 || (now - lastDrawnAt) * 1000 >= SliceStripEveryMs * frequency);

    /// <summary>Where a published strip's readback is (<see cref="SliceReadbackOf"/>).</summary>
    internal enum SliceReadback
    {
        Waiting,
        Ready,
        Replaced,
        Overdue,
    }

    /// <summary>
    /// Strip scene <paramref name="sequence"/>'s readback, looked at without waiting: ready once the worker's newest readback is that scene;
    /// replaced by a later readback, by a later scene drawn without it, or by a picture the player owes now (a seek or an edit arrived:
    /// its scene goes first); overdue <see cref="SliceReadbackMs"/> after publishing (a held or refused frame); else waiting. Pure; goldens.
    /// </summary>
    internal static SliceReadback SliceReadbackOf(long sequence, long readBack, long presented, bool playerOwes, long now, long publishedAt, long frequency) =>
        readBack == sequence ? SliceReadback.Ready
        : readBack > sequence || presented > sequence || playerOwes ? SliceReadback.Replaced
        : (now - publishedAt) * 1000 >= SliceReadbackMs * frequency ? SliceReadback.Overdue
        : SliceReadback.Waiting;

    private void WakePaused()
    {
        if (!_playing)
            _dirty.Set();
    }

    partial void SliceBurstTurn(bool playing)
    {
        var ask = Interlocked.Exchange(ref _sliceAsk, null);

        // Playing or in the 3D view: the ribbon is dormant, a strip in flight is dropped, and any burst is let go (the worker answers
        // nothing more).
        if (playing || IsExplodedForSlices)
        {
            if (_sliceReadback != 0)
                EndSliceReadback(retry: false);
            if (_slicePosted is not null)
                WithdrawSlices();
            return;
        }

        // A strip published on an earlier turn: polled, never waited on (this turn's ghost pause and seek were applied first).
        if (_sliceReadback != 0)
            PollSliceStrip();

        if (ask is { Request: null })
            WithdrawSlices();
        else if (ask is not null)
            PostSlices(ask);

        // A new strip only with none in flight and no seek or player picture owed (the player's scene would replace it at once).
        if (_sliceReadback == 0 && _slicePosted is { } posted
            && SliceStripDue(true, _sliceDrawn, _client.SliceBurstDone(_sliceVersion), PlayerOwesPicture(), false,
                Stopwatch.GetTimestamp(), _sliceDrawnAt, Stopwatch.Frequency))
        {
            DrawSliceStrip(posted);
        }
    }

    // A seek not yet applied, or a player scene marked dirty (a seek, an edit, a resize) and not yet published.
    private bool PlayerOwesPicture() => SeekMailSlot.IsPending(ref _pendingSeekBits) || Volatile.Read(ref _sceneDirty) != 0;

    // The 3D view's planes are cached per plane (SceneRenderer.Exploded.cs): a flat strip scene would let small cards release them.
    private bool IsExplodedForSlices => _drawingExploded;

    private void PostSlices(SliceAsk ask)
    {
        var slots = ask.Request!.Slots;
        Span<double> times = stackalloc double[CompositorProtocol.MaxSliceSlots];
        var count = Math.Min(slots.Count, times.Length);
        for (var i = 0; i < count; i++)
            times[i] = slots[i].MediaSeconds;

        _sliceVersion = _client.PostSliceBurst(ask.SourceId, ask.PathHash, times[..count], ask.CellHeight);
        (_slicePosted, _sliceDrawn) = (_sliceVersion > 0 ? ask : null, false);
    }

    private void WithdrawSlices()
    {
        if (_slicePosted is not null || _sliceVersion != 0)
            _client.PostSliceBurst(-1, 0, [], 0);
        (_sliceVersion, _slicePosted, _sliceDrawn) = (0, null, false);
    }

    /// <summary>
    /// Publishes the posted burst's slots side by side as one read-back scene (pump thread; its frames are cached, so no exact-frame wait).
    /// Its readback is polled on later turns (<see cref="PollSliceStrip"/>), never waited on here, so no seek or picture queues behind it.
    /// </summary>
    private void DrawSliceStrip(SliceAsk ask)
    {
        var request = ask.Request!;
        var count = Math.Min(request.Slots.Count, CompositorProtocol.MaxSliceSlots);
        var (cellWidth, cellHeight) = (ask.CellWidth, ask.CellHeight);
        var (width, height) = (cellWidth * count, cellHeight);
        (_sliceReading, _sliceReadbackAt) = (ask, Stopwatch.GetTimestamp());
        try
        {
            var bytes = (long)width * height * 4;
            _client.EnsureReadback(bytes);
            _client.ClearScene();
            ref var scene = ref _client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.OutputWidth, scene.OutputHeight) = (width, height, width, height);
            (scene.BackgroundR, scene.BackgroundG, scene.BackgroundB) = (0.17f, 0.17f, 0.19f);
            scene.ViewScale = 1;

            // The worker's clock stays at the player's playhead (the playhead's slot, else the middle one): no jump in its pre-roll.
            var centre = request.Slots[count / 2].SequenceSeconds;
            for (var i = 0; i < count; i++)
            {
                if (request.Slots[i].Offset == 0)
                    centre = request.Slots[i].SequenceSeconds;
            }

            scene.PlayheadSeconds = centre;
            scene.Flags = FrameSceneHeader.FlagReadback;
            scene.ActiveLayerCount = count;
            for (var i = 0; i < count; i++)
            {
                ref var layer = ref _client.Layer(i);
                layer.ClipKey = SliceClipKey;
                (layer.SourceKind, layer.SourceId, layer.SourcePathHash, layer.MediaSeconds) = ((int)GpuSourceKind.Media, ask.SourceId, ask.PathHash, request.Slots[i].MediaSeconds);
                (layer.CropLeft, layer.CropTop, layer.CropRight, layer.CropBottom) = (0, 0, 1, 1);
                (layer.X, layer.Y, layer.Width, layer.Height) = (i * cellWidth, 0, cellWidth, cellHeight);
                (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
                (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
                layer.Flags = GpuLayerDescriptor.FlagVisible;
            }

            _sliceReadback = _client.Publish();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU engine: the Slice-Preview strip could not be drawn", ex);
            EndSliceReadback(retry: false);
        }
    }

    /// <summary>
    /// The published strip's readback, looked at without waiting (pump thread, after the turn's ghost pause and seek): taken once read back;
    /// let go and tried again later once replaced (a seek arriving mid-readback re-dirties the player's scene at once); given up once
    /// overdue.
    /// </summary>
    private void PollSliceStrip()
    {
        ref var control = ref _client.Memory.Control;
        var sequence = _sliceReadback;
        var readBack = Volatile.Read(ref control.ReadbackSequence);
        switch (SliceReadbackOf(sequence, readBack, Volatile.Read(ref control.PresentedSequence), PlayerOwesPicture(), Stopwatch.GetTimestamp(),
            _sliceReadbackAt, Stopwatch.Frequency))
        {
            case SliceReadback.Ready:
                try
                {
                    TakeSliceStrip(sequence);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLog.Write("GPU engine: the Slice-Preview strip could not be read back", ex);
                }
                finally
                {
                    EndSliceReadback(retry: false);
                }

                break;
            case SliceReadback.Replaced:
                EndSliceReadback(retry: true);
                break;
            case SliceReadback.Overdue:
                EndSliceReadback(retry: false);
                break;
        }
    }

    // Copies the strip read back (the worker's newest readback, so the copy waits for nothing) and hands it to the UI.
    private void TakeSliceStrip(long sequence)
    {
        var ask = _sliceReading!;
        var request = ask.Request!;
        var count = Math.Min(request.Slots.Count, CompositorProtocol.MaxSliceSlots);
        var (width, height) = (ask.CellWidth * count, ask.CellHeight);
        var pixels = new byte[(long)width * height * 4];
        if (_client.TryReadback(sequence, pixels, out var gotWidth, out var gotHeight, 0) && gotWidth == width && gotHeight == height)
            Volatile.Write(ref _sliceStrip, new SlicePreviewStrip(ask.Ticket, request, pixels, width, height, ask.CellWidth));
    }

    /// <summary>
    /// Ends the strip in flight (read back, replaced, overdue or failed): the burst it was drawn for counts as drawn unless it is tried again
    /// (<paramref name="retry"/>, after <see cref="SliceStripEveryMs"/>), and the player's own scene is published again.
    /// </summary>
    private void EndSliceReadback(bool retry)
    {
        var reading = _sliceReading;
        (_sliceReadback, _sliceReading, _sliceDrawnAt) = (0, null, Stopwatch.GetTimestamp());

        // A newer burst posted meanwhile is still owed its own strip.
        if (ReferenceEquals(reading, _slicePosted))
            _sliceDrawn = !retry;

        // The player's own picture again (the strip scene is the worker's newest, or was replaced by a scene the player may still owe).
        InvalidateView();
    }
}
