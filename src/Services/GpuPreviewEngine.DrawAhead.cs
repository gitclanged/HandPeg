using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

// ----- Draw-ahead playback (experimental) -----
// Default turns draw and wait within the target refresh; on unified-memory systems a slightly long turn skips a timeline frame. Draw
// ahead pipelines it: each turn presents the frame drawn during the previous refresh (waiting only for its remainder), then hands the
// worker the next frame, timed for its display moment via GpuViewport.DisplayLeadSeconds (at least two refreshes until measured).
// Off unless HANDPEG_DRAW_AHEAD=1 (DrawsAhead), since benchmarks showed no improvement.
public sealed partial class GpuPreviewEngine
{
    // Scene sequence handed over last turn (0 = none) and that turn's QPC (lead-measurement origin).
    private long _aheadSequence;
    private long _aheadOrigin;

    // The timeline frame of the frame handed over (what the bench and a trace name a presented frame by).
    private long _aheadFrame = long.MinValue;

    /// <summary>
    /// True only with HANDPEG_DRAW_AHEAD=1. A/B play benchmarks showed no reduction in skipped frames versus in-turn drawing, so it stays
    /// off by default.
    /// </summary>
    private bool DrawsAhead => _drawsAhead ??= Environment.GetEnvironmentVariable("HANDPEG_DRAW_AHEAD") == "1";

    private bool? _drawsAhead;

    /// <summary>
    /// Draw-ahead lead (pure, golden-tested): the measured lead when known and sane, floored at two refreshes; two refreshes until known.
    /// </summary>
    internal static double AheadLead(double refresh, double measured) => GpuViewport.PlayingLead(2 * refresh, measured);

    /// <summary>
    /// One draw-ahead turn: present the frame drawn last refresh, then hand over the next. <paramref name="now"/> is the lead origin (QPC,
    /// or 0 for a stalled turn: GpuViewport.LeadOrigin).
    /// </summary>
    private void DrawAheadTurn(GpuScenePlan plan, long now, Span<GpuSeekHint> hints, ref bool owedTurn, ref bool presentedLast)
    {
        // 1. Present last turn's frame (wait at most one refresh), else re-present the previous frame to keep cadence.
        var (shownFrame, shownSequence) = (_aheadFrame, _aheadSequence);
        if (shownSequence > 0)
        {
            CountFrame(late: NoteFrame(!_client.WaitForFrame(shownSequence, Math.Max(4, (int)(_refresh * 1000)))));
            _viewport.FrameOrigin = _aheadOrigin;
            owedTurn = !(_viewport.PresentNewest(vsync: true) || _viewport.PresentNewest(vsync: true, force: true));
            _aheadSequence = 0;
        }
        else
        {
            _viewport.FrameOrigin = 0;
            owedTurn = !_viewport.PresentNewest(vsync: true, force: true);
        }

        presentedLast = !owedTurn;
        PresentMirrors();

        // 2. The next frame, for its own moment on the screen, handed to the worker now: drawn while this one is shown.
        var serial = Volatile.Read(ref _transportSerial);
        var at = _audio.Position + AheadLead(_refresh, _viewport.DisplayLeadSeconds) * Math.Max(_rate, 0);
        if (_rate >= 0 && at >= plan.SequenceSeconds - 1e-3 || _rate < 0 && at <= 0)
        {
            // Its moment is past the end: the end is reached on the next turn's clock, as without drawing ahead.
            var heard = _audio.Position;
            if (_rate >= 0 && heard >= plan.SequenceSeconds - 1e-3 || _rate < 0 && heard <= 0)
                PauseAtEnd(serial, _rate >= 0 ? plan.SequenceSeconds : 0);

            return;
        }

        // Same timeline frame and nothing changed: skip drawing (next turn re-presents).
        var frame = CanvasFrameOf(at);
        var moved = false;
        CameraMoving(ref moved);
        if (frame == _lastPlayedFrame && frame != long.MinValue && Volatile.Read(ref _sceneDirty) == 0 && !moved)
            return;

        Volatile.Write(ref _sceneDirty, 0);
        _lastPlayedFrame = frame;
        var sequence = PublishAt(plan, at, playing: true);
        if (_primingSince != 0 && _primingScene == 0)
            _primingScene = sequence;
        LeaveHints(plan, at, playing: true, hints);
        (_aheadSequence, _aheadOrigin, _aheadFrame) = (sequence > 0 ? sequence : 0, now, frame);
    }
}
