using System.Diagnostics;
using HandPegApp.Compositor;
using HandPegApp;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// W3-REV-3 (Pass 5.6.4B-Release): large-file scrub lag. A visible frame's seek-landing key frame wakes the renderer at once; an active paused
/// scrub defers the GOP chase until the pointer dwells; pre-roll waits for a quiet playhead and scrub-ahead never shares the decode engine
/// with a visible frame; background waveform reads yield during holds. Exact-frame scenes and playback are never affected.
/// </summary>
public static class ScrubDecodeTests
{
    private static bool Has(string text, string part) => text.Contains(part, StringComparison.Ordinal);

    public static void Case(Action<bool, string> expect)
    {
        const long f = 10_000_000;
        static long Ms(int ms) => ms * f / 1000;
        var t0 = 100 * f;

        // ----- Pure decisions -----
        expect(DecoderPool.NextScrubMoves(0, t0, 0, f) == 1 && DecoderPool.NextScrubMoves(1, t0 + Ms(30), t0, f) == 2
               && DecoderPool.NextScrubMoves(5, t0 + Ms(200), t0, f) == 1,
            "paused moves within 120 ms extend the scrub run; a slower step starts a new one");
        expect(DecoderPool.ScrubbingAt(false, false, t0 + Ms(30), t0, 2, f) && !DecoderPool.ScrubbingAt(false, false, t0 + Ms(30), t0, 1, f)
               && !DecoderPool.ScrubbingAt(false, false, t0 + Ms(80), t0, 5, f) && !DecoderPool.ScrubbingAt(false, false, t0, 0, 5, f),
            "an active scrub is two rapid paused moves, the last within 70 ms");
        expect(!DecoderPool.ScrubbingAt(true, false, t0 + Ms(10), t0, 5, f) && !DecoderPool.ScrubbingAt(false, true, t0 + Ms(10), t0, 5, f),
            "never an active scrub in exact-frame scenes (export, filmstrips) or while playing");
        expect(DecoderPool.ChaseWaits(false, false, true, true, t0 + Ms(20), t0, 3, f),
            "an active scrub's visible landing key frame is shown and the GOP chase waits");
        expect(!DecoderPool.ChaseWaits(false, false, true, true, t0 + Ms(DecoderPool.ScrubDwellMs + 1), t0, 3, f),
            "the exact frame is chased once the pointer dwells 70 ms");
        expect(!DecoderPool.ChaseWaits(false, false, false, true, t0 + Ms(20), t0, 3, f) && !DecoderPool.ChaseWaits(false, false, true, false, t0 + Ms(20), t0, 3, f),
            "only a visible frame's landing waits (pre-roll, hints and frames mid-GOP read on)");
        expect(!DecoderPool.ChaseWaits(true, false, true, true, t0 + Ms(20), t0, 3, f) && !DecoderPool.ChaseWaits(false, true, true, true, t0 + Ms(20), t0, 3, f)
               && !DecoderPool.ChaseWaits(false, false, true, true, t0 + Ms(20), t0, 1, f),
            "exports, filmstrips, playback and single steps always decode the exact frame at once");
        expect(DecoderPool.PreRollMayRun(t0, 0, f) && !DecoderPool.PreRollMayRun(t0 + Ms(100), t0, f) && !DecoderPool.PreRollMayRun(t0 + Ms(249), t0, f)
               && DecoderPool.PreRollMayRun(t0 + Ms(250), t0, f),
            "pre-roll waits 250 ms past the last paused playhead move");
        expect(!DecoderPool.ScrubAheadMayRun(true, false, 0) && DecoderPool.ScrubAheadMayRun(false, false, 3) && DecoderPool.ScrubAheadMayRun(false, true, 0)
               && !DecoderPool.ScrubAheadMayRun(false, true, 1),
            "scrub-ahead never beside a visible decode, and one read at most during an active scrub");

        // ----- Where the lanes use them -----
        var pool = SourceProbe.Src("Compositor", "DecoderPool.cs");
        var lanes = SourceProbe.Src("Compositor", "DecoderPool.Lanes.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(lanes, "private void WorkOn("), "Pace(demand, decoder);",
                   "if (!_exactOnly && demand.Kind == DemandKind.Visible && decoder.LastWasLanding)", "_arrived.Set();", "DwellOnLanding(demand, source, target);",
                   "if (decoder.LastWasLanding && lateTries < 3)"),
            "a visible frame's landing key frame wakes the renderer at once; the chase dwells only during an active scrub");
        var dwell = SourceProbe.Member(lanes, "private void DwellOnLanding(");
        expect(Has(dwell, "_waitingExact) == 0 && ChaseWaits(_exactOnly, _lastWasPlaying") && Has(dwell, "demand.Cancelled || source.Retired || Math.Abs(demand.Target - target) > 1e-6")
               && Has(dwell, "_stopping.WaitOne(DwellPollMs)"),
            "the dwell ends when the pointer settles, play starts, anyone waits for exact frames, or the demand moves or is withdrawn");
        expect(SourceProbe.InOrder(SourceProbe.Member(lanes, "private Demand? MostUrgent(long pick)"), "&& VisibleBeingDecoded())",
                   "demand.Kind == DemandKind.PreRoll && !PreRollMayRun(", "Volatile.Read(ref _scrubHeld)))", "demand.HintKind == GpuSeekHintKind.ScrubAhead &&",
                   "!ScrubAheadMayRun(VisibleBeingDecoded(), scrubbing ??= ScrubbingNow(), ScrubAheadBusy(), Volatile.Read(ref _scrubHeld)))",
                   "BackgroundBusy() >= BackgroundLanes"),
            "MostUrgent holds pre-roll until the playhead is quiet and scrub-ahead off a visible decode");
        expect(SourceProbe.InOrder(SourceProbe.Member(pool, "public void EndFrame("), "var held = Volatile.Read(ref _scrubHeld);",
                   "if (!PreRollMayRun(Stopwatch.GetTimestamp(), Volatile.Read(ref _lastMoveAt), Stopwatch.Frequency, held))",
                   "busy.Kind == DemandKind.PreRoll || held && busy.HintKind == GpuSeekHintKind.ScrubAhead", "busy.Cancelled = true;", "if (!HasPreRoll(clipKey, source, to))")
               && Has(SourceProbe.Member(lanes, "private bool HasPreRoll("), "d.Kind == DemandKind.PreRoll && !d.Cancelled"),
            "a moving playhead stops the pre-roll under way; its range is queued again for the quiet");
        expect(SourceProbe.InOrder(SourceProbe.Member(pool, "public void BeginFrame("), "if (!exactOnly && !playing && !double.IsNaN(_lastPlayhead)", "NextScrubMoves(",
                   "Volatile.Write(ref _lastMoveAt, now);", "LastFrameExact = true;"),
            "BeginFrame records each paused move (never an exact-frame scene's or a playing frame's)");

        // ----- Held drag (FrameSceneHeader.FlagScrubbing) -----
        expect(DecoderPool.ScrubbingAt(false, false, t0 + Ms(100), t0, 1, f, held: true)
               && !DecoderPool.ScrubbingAt(false, false, t0 + Ms(DecoderPool.HeldScrubDwellMs + 1), t0, 1, f, held: true),
            "a held drag scrubs from its first move until it dwells 140 ms");
        expect(!DecoderPool.ScrubbingAt(false, false, t0 + Ms(10), t0, 5, f, released: true)
               && !DecoderPool.ChaseWaits(false, false, true, true, t0 + Ms(10), t0, 5, f, released: true),
            "a released drag chases the exact frame at once");
        expect(!DecoderPool.ScrubbingAt(true, false, t0 + Ms(10), t0, 1, f, held: true) && !DecoderPool.ScrubbingAt(false, true, t0 + Ms(10), t0, 1, f, held: true),
            "the held flag never scrubs exact-frame scenes or playback");
        expect(!DecoderPool.PreRollMayRun(t0 + Ms(1000), t0, f, held: true) && !DecoderPool.PreRollMayRun(t0, 0, f, held: true)
               && !DecoderPool.ScrubAheadMayRun(false, false, 0, held: true),
            "a held drag suppresses pre-roll and scrub-ahead outright");
        expect(SourceProbe.InOrder(SourceProbe.Member(pool, "public void BeginFrame("), "var held = scrubbing && !exactOnly && !playing;",
                   "if (Volatile.Read(ref _scrubHeld) && !held)", "Volatile.Write(ref _scrubReleased, true);", "Volatile.Write(ref _scrubReleased, false);",
                   "Volatile.Write(ref _scrubHeld, held);")
               && Has(SourceProbe.Src("Compositor", "SceneRenderer.cs"), "scrubbing: (header.Flags & FrameSceneHeader.FlagScrubbing) != 0, ghost:"),
            "BeginFrame reads the editor's held-drag flag and marks its release");

        // ----- Paused-scrub audio priming -----
        expect(WasapiAudioEngine.DefersPrime(false, false) && !WasapiAudioEngine.DefersPrime(true, false) && !WasapiAudioEngine.DefersPrime(false, true),
            "a paused seek defers its voice requests unless play is asked");
        var due = t0 + Ms(WasapiAudioEngine.PausedPrimeQuietMs);
        expect(WasapiAudioEngine.PrimesNow(false, 0, t0) && !WasapiAudioEngine.PrimesNow(false, due, due - 1) && WasapiAudioEngine.PrimesNow(false, due, due)
               && WasapiAudioEngine.PrimesNow(true, due, t0),
            "a deferred prime runs once the playhead rests 200 ms, or at once while playing");
        var engine = SourceProbe.Src("Services", "Audio", "WasapiAudioEngine.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(engine, "private void Commands("), "var defer = DefersPrime(_playing, Volatile.Read(ref _wantPlaying) != 0);",
                   "prime: !defer);", "_primeDue = Stopwatch.GetTimestamp() + PrimeQuietMs(")
               && SourceProbe.InOrder(SourceProbe.Member(engine, "private void Turn("), "if (PrimesNow(_playing, _primeDue, Stopwatch.GetTimestamp()))", "_primeDue = 0;", "_mixer?.Prime(_written);")
               && SourceProbe.InOrder(SourceProbe.Member(engine, "private void StartPlaying("), "_primeDue = 0;", "_mixer?.Prime(_written);")
               && SourceProbe.InOrder(SourceProbe.Member(SourceProbe.Src("Services", "Audio", "AudioMixer.cs"), "internal void SetLine("), "if (prime)", "Prime(frame);"),
            "paused seeks move the line at once; the voices are primed on rest or at play");
        expect(AudioDecoder.Superseding(5, 4) && !AudioDecoder.Superseding(4, 4) && !AudioDecoder.Superseding(5, 0),
            "a live decoder read is superseded once the voice's request serial moves on");
        expect(SourceProbe.InOrder(SourceProbe.Src("Services", "Audio", "AudioDecoders.cs"), "internal sealed unsafe class MfAudioDecoder",
                   "if (produced == 0 && !_ended && Superseded)", "return 0;", "if (_ended || !Decode())")
               && SourceProbe.InOrder(SourceProbe.Src("Services", "Audio", "AudioVoices.cs"), "_decoder?.AbortWhenSuperseded(exact ? null : state, serial);",
                   "while (_produced < target && state.RequestSerial == serial)", "decoder.AbortWhenSuperseded(null, 0);", "pool.Return(decoder);"),
            "a newer seek abandons a post-seek skip (live only); a returned decoder forgets its owner");

        // ----- Ruler seek drag and the background hold -----
        expect(TimelineRuler.NotesPointer(false) && !TimelineRuler.NotesPointer(true), "a seek drag never reports the pointer time");
        var parts = SourceProbe.Src("Controls", "TimelineParts.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(parts, "protected override void OnMouseLeftButtonDown("), "_seeking = true;", "NotePointer(double.NaN);")
               && SourceProbe.InOrder(SourceProbe.Member(parts, "protected override void OnMouseLeftButtonUp("), "SeekEnded?.Invoke();", "if (IsMouseOver)",
                   "NotePointer(PointerTimeAt(")
               && SourceProbe.InOrder(SourceProbe.Member(parts, "protected override void OnMouseMove("), "if (NotesPointer(_seeking))", "NotePointer(PointerTimeAt(at));"),
            "the ruler's skim line steps aside for a seek drag and returns at release");
        expect(GpuPreviewEngine.ScrubHoldDue(t0, 0, f) && !GpuPreviewEngine.ScrubHoldDue(t0 + Ms(49), t0, f)
               && GpuPreviewEngine.ScrubHoldDue(t0 + Ms(GpuPreviewEngine.ScrubHoldEveryMs), t0, f),
            "a scrub extends the background hold at most every 50 ms");
        expect(SourceProbe.InOrder(SourceProbe.Member(SourceProbe.Src("Services", "GpuPreviewEngine.cs"), "public void Seek(double sequenceSeconds)"),
                   "if (holdBackground && ScrubHoldDue(now, _scrubHeldAt, Stopwatch.Frequency))", "_scrubHeldAt = now;", "WorkScheduler.HoldBackground(ScrubHold);"),
            "Seek extends the background hold only when due");

        // ----- Background reads yield during holds -----
        var paused = 1;
        var next = 0L;
        var clock = Stopwatch.StartNew();
        var releasedAt = 0L;
        var release = new Thread(() =>
        {
            Thread.Sleep(60);
            Interlocked.Exchange(ref releasedAt, clock.ElapsedMilliseconds);
            Volatile.Write(ref paused, 0);
        }) { IsBackground = true };
        release.Start();
        Waveforms.YieldWhilePaused(() => Volatile.Read(ref paused) != 0, CancellationToken.None, ref next);
        var resumed = clock.ElapsedMilliseconds;
        release.Join();
        expect(resumed >= Interlocked.Read(ref releasedAt) && resumed - Interlocked.Read(ref releasedAt) < 250 && next > 0,
            "a held waveform read waits at its pause point and resumes promptly on release");

        var asked = 0;
        var later = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        Waveforms.YieldWhilePaused(() => ++asked > 0, CancellationToken.None, ref later);
        expect(asked == 0, "between checks (25 ms) a read never asks the pause");

        using var cancel = new CancellationTokenSource(30);
        var none = 0L;
        var threw = false;
        try
        {
            Waveforms.YieldWhilePaused(static () => true, cancel.Token, ref none);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        expect(threw, "cancellation ends a held read at once");

        var native = SourceProbe.Src("Services", "Waveforms.Native.cs");
        expect(SourceProbe.InOrder(native, "token.ThrowIfCancellationRequested();", "YieldWhilePaused(pause, token, ref nextPauseCheck);", "Mf.ReadSampleAny(")
               && SourceProbe.InOrder(SourceProbe.Src("Services", "AudioTools.cs"), "var pause = WorkScheduler.PausePoint();", "ReadPeaksNatively(mediaPath, distinct, native, cancellationToken, pause)"),
            "the multi-track MF waveform scan checks the background pause between reads");
        expect(SourceProbe.InOrder(SourceProbe.Src("Services", "GpuProgramStrip.cs"), "if (!MayGoOn(job))", "job.Held = !job.Token.IsCancellationRequested;", "client.Publish()"),
            "the program strip pauses before each cell it draws");
    }
}
