using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

// ----- Drawing ahead while playing (5.4.4-Final, the user's Q2 a) -----
// On the XPS (unified memory) a playing turn drew its frame and waited for it inside the refresh it was to be shown on: a turn that took a little
// longer than a refresh (the worker's draw, a decode lane's turn on the card) let the next timeline frame pass unpublished (5 to 16 skipped
// frames in an 11 s play). Drawing ahead, each turn first shows the frame the worker drew during the refresh before (waiting only for what is
// left of it), then hands the worker the next frame at once: the worker draws while the frame before is on the screen, and a turn is a present
// and a publish. The next frame is drawn for its own moment on the screen: the swap chain's measured lead from the turn that handed it over
// (GpuViewportControl.DisplayLeadSeconds, which then holds the refresh it waits), at least two refreshes until that is known. Off unless
// HANDPEG_DRAW_AHEAD=1 (DrawsAhead: the bench did not show it helping); without it every turn draws its own frame, as before.
public sealed partial class GpuPreviewEngine
{
    // The frame handed over on the last turn (its scene's sequence, 0: none) and that turn's QPC (its origin for the measured lead).
    private long _aheadSequence;
    private long _aheadOrigin;

    // The timeline frame of the frame handed over (what the bench and a trace name a presented frame by).
    private long _aheadFrame = long.MinValue;

    /// <summary>
    /// Whether playing draws ahead: only when HANDPEG_DRAW_AHEAD=1 asks (5.4.4-Final). The XPS play bench (two rounds each way, same build) did not
    /// show it skipping fewer frames than drawing in the turn (S2: 62 against 57 % on time and 96 against 90 %, S1: 78 against 94 %, the
    /// skipped frames alike), so it is off until it does: the user's call (USERRESPONSE Q2).
    /// </summary>
    private bool DrawsAhead => _drawsAhead ??= Environment.GetEnvironmentVariable("HANDPEG_DRAW_AHEAD") == "1";

    private bool? _drawsAhead;

    /// <summary>
    /// The lead a frame drawn ahead is drawn for (pure, for the golden test): the measured one when known and within reason, at least two
    /// refreshes (its turn's, and the one it waits in the worker's hands); two refreshes until it is known.
    /// </summary>
    internal static double AheadLead(double refresh, double measured) => GpuViewportControl.PlayingLead(2 * refresh, measured);

    /// <summary>
    /// One playing turn while drawing ahead: the frame drawn during the last refresh presented, then the next one handed over. <paramref name="now"/>
    /// is the turn's lead origin (its QPC, or 0 for a stalled turn: GpuViewportControl.LeadOrigin).
    /// </summary>
    private void DrawAheadTurn(GpuScenePlan plan, long now, Span<GpuSeekHint> hints, ref bool owedTurn, ref bool presentedLast)
    {
        // 1. On the screen now: the frame handed over on the last turn (waited for at most a refresh: the worker had the refresh between),
        // else the frame shown last again (the swap chain keeps its beat).
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
        var at = _audio.Position + AheadLead(_refresh, _viewport.DisplayLeadSeconds) * Math.Max(_rate, 0);
        if (_rate >= 0 && at >= plan.SequenceSeconds - 1e-3 || _rate < 0 && at <= 0)
        {
            // Its moment is past the end: the end is reached on the next turn's clock, as without drawing ahead.
            var heard = _audio.Position;
            if (_rate >= 0 && heard >= plan.SequenceSeconds - 1e-3 || _rate < 0 && heard <= 0)
            {
                Pause();
                Volatile.Write(ref _pausedAt, _rate >= 0 ? plan.SequenceSeconds : 0);
                Ended?.Invoke();
            }

            return;
        }

        // The same timeline frame as the one handed over last, nothing else changed: nothing new to draw (the next turn shows the last again).
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
