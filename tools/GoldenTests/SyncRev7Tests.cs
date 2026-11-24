using HandPegApp.Compositor;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// W3-REV-7 (Pass 5.6.4B-Release): the player overlay stays under every owned window (Settings, dialogs, side windows); a ghost-paused
/// play-scrub dwells and primes short of GhostDwell; Play and GhostResume clear the held-drag flag; a cold single-video Play holds its
/// clock briefly for the exact frame; the play-session line names the frame cache cap.
/// </summary>
public static class SyncRev7Tests
{
    private static bool Has(string text, string part) => text.Contains(part, StringComparison.Ordinal);

    public static void Case(Action<bool, string> expect)
    {
        const long f = 10_000_000;
        static long Ms(int ms) => ms * f / 1000;
        var t0 = 100 * f;

        // ----- Overlay z-order among owned windows -----
        var surface = SourceProbe.Src("Controls", "VideoSurface.cs");
        var keep = SourceProbe.Member(surface, "private void KeepUnderOwnedWindows(");
        expect(Has(keep, "foreach (Window other in owner.OwnedWindows)") && Has(keep, "!ReferenceEquals(other, overlay) && other.IsVisible") && !Has(keep, "SideWindow")
               && SourceProbe.InOrder(keep, "Docking.WindowOrder.Lowest(siblings)", "SetWindowPos(new WindowInteropHelper(overlay).Handle, lowest, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);"),
            "the player overlay goes directly below the lowest visible owned window (Settings, dialogs, side windows), never activated");
        expect(SourceProbe.InOrder(SourceProbe.Member(surface, "internal void PlaceOverlay("), "overlay.Show();", "KeepUnderOwnedWindows();")
               && SourceProbe.InOrder(SourceProbe.Member(surface, "private void CreateOverlay("), "_overlayWindow.Owner = owner;", "PlaceOverlay();", "KeepUnderOwnedWindows();")
               && !Has(surface, "KeepUnderSideWindow"),
            "a created or re-shown overlay re-inserts itself under owned windows (the engine starting while Settings is open)");

        // ----- Ghost-paused play-scrub: video dwell and audio prime short of GhostDwell -----
        var ghostMs = GpuPreviewEngine.GhostDwell * 1000;
        expect(DecoderPool.HeldDwellMs(false) == DecoderPool.HeldScrubDwellMs && DecoderPool.HeldScrubDwellMs == 140
               && DecoderPool.HeldDwellMs(true) == DecoderPool.GhostScrubDwellMs && DecoderPool.GhostScrubDwellMs < ghostMs,
            "a paused held drag dwells 140 ms on landing key frames; a ghost-paused play-scrub 35 ms, under GhostDwell (80 ms)");
        expect(DecoderPool.ScrubbingAt(false, false, t0 + Ms(30), t0, 1, f, held: true, ghost: true)
               && !DecoderPool.ScrubbingAt(false, false, t0 + Ms(DecoderPool.GhostScrubDwellMs + 1), t0, 1, f, held: true, ghost: true)
               && DecoderPool.ScrubbingAt(false, false, t0 + Ms(DecoderPool.GhostScrubDwellMs + 1), t0, 1, f, held: true)
               && !DecoderPool.ChaseWaits(false, false, true, true, t0 + Ms(DecoderPool.GhostScrubDwellMs + 1), t0, 1, f, held: true, ghost: true),
            "a ghost-paused play-scrub chases the exact frame 35 ms after the last move; a paused drag still waits");
        expect(WasapiAudioEngine.PrimeQuietMs(false) == WasapiAudioEngine.PausedPrimeQuietMs && WasapiAudioEngine.PausedPrimeQuietMs == 200
               && WasapiAudioEngine.PrimeQuietMs(true) == WasapiAudioEngine.GhostPrimeQuietMs && WasapiAudioEngine.GhostPrimeQuietMs < ghostMs,
            "a paused seek primes the voices after 200 ms of rest; a ghost-paused play-scrub after 45 ms, under GhostDwell");
        var pool = SourceProbe.Src("Compositor", "DecoderPool.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(pool, "public void BeginFrame("), "Volatile.Write(ref _scrubHeld, held);", "Volatile.Write(ref _scrubGhost, held && ghost);")
               && Has(SourceProbe.Member(SourceProbe.Src("Compositor", "DecoderPool.Lanes.cs"), "private void DwellOnLanding("), "Volatile.Read(ref _scrubGhost)")
               && Has(SourceProbe.Src("Compositor", "SceneRenderer.cs"), "ghost: (header.Flags & FrameSceneHeader.FlagGhostScrub) != 0"),
            "the worker reads the ghost flag per scene and the landing dwell honours it");
        var audio = SourceProbe.Src("Services", "Audio", "WasapiAudioEngine.cs");
        var engine = SourceProbe.Src("Services", "GpuPreviewEngine.cs");
        expect(Has(SourceProbe.Member(audio, "private void Commands("), "PrimeQuietMs(Volatile.Read(ref _ghostPrime) != 0)")
               && SourceProbe.InOrder(SourceProbe.Member(engine, "private void ApplyGhostPause("), "if (!_playing || !_ghostPaused)", "_audio.GhostPrime = true;", "PauseCore(")
               && SourceProbe.InOrder(SourceProbe.Member(engine, "public void Play()"), "_ghostPaused = false;", "_audio.GhostPrime = false;", "if (_playing)")
               && SourceProbe.InOrder(SourceProbe.Member(engine, "public void Pause()"), "_ghostPaused = false;", "_audio.GhostPrime = false;", "if (!_playing)"),
            "the pump's ghost pause shortens the audio prime before pausing; Play, GhostResume and Pause restore it");

        // ----- Held-drag flags clear at Play / GhostResume -----
        const int scrub = FrameSceneHeader.FlagScrubbing, ghost = FrameSceneHeader.FlagGhostScrub;
        expect(GpuPreviewEngine.ScrubFlags(false, true, false) == scrub && GpuPreviewEngine.ScrubFlags(false, true, true) == (scrub | ghost)
               && GpuPreviewEngine.ScrubFlags(false, false, true) == 0 && GpuPreviewEngine.ScrubFlags(true, true, true) == 0 && GpuPreviewEngine.ScrubFlags(true, true, false) == 0,
            "a paused held drag flags scrubbing (plus ghost when it holds a play); none once playing though the gate stays held");
        expect(Has(SourceProbe.Member(engine, "private long PublishAt("), "ScrubFlags(playing || _playing, ScrubGate.IsHeld, _ghostPaused)") && (scrub & ghost) == 0,
            "every scene reads the transport, so GhostResume's or Play's first scene already drops FlagScrubbing");

        // ----- Single-video cold resume: brief capped priming hold -----
        expect(GpuPreviewEngine.SingleStartWait == 120 && GpuPreviewEngine.SingleStartWait < GpuPreviewEngine.PlayStartWait,
            "a single video waits at most 120 ms for its exact frame (several videos 350 ms)");
        expect(GpuPreviewEngine.PrimesAtPlay(1, alreadyExact: false) && !GpuPreviewEngine.PrimesAtPlay(1, alreadyExact: true)
               && GpuPreviewEngine.PrimesAtPlay(2, alreadyExact: true) && !GpuPreviewEngine.PrimesAtPlay(0, alreadyExact: false)
               && GpuPreviewEngine.PausedSceneExact(false, 5, true, 5, 1.5, 1.5, true) && !GpuPreviewEngine.PausedSceneExact(true, 5, true, 5, 1.5, 1.5, true)
               && !GpuPreviewEngine.PausedSceneExact(false, 5, true, 4, 1.5, 1.5, true) && !GpuPreviewEngine.PausedSceneExact(false, 5, true, 5, 1.0, 1.5, true)
               && !GpuPreviewEngine.PausedSceneExact(false, 0, true, 5, 1.5, 1.5, true) && !GpuPreviewEngine.PausedSceneExact(false, 5, false, 5, 1.5, 1.5, true)
               && !GpuPreviewEngine.PausedSceneExact(false, 5, true, 5, 1.5, 1.5, false),
            "one video primes only when its playhead frame is not presented exact; several always; none never");
        expect(GpuPreviewEngine.SingleStartReady(true, true, 0) && !GpuPreviewEngine.SingleStartReady(true, false, 60) && !GpuPreviewEngine.SingleStartReady(false, true, 60)
               && GpuPreviewEngine.SingleStartReady(false, false, GpuPreviewEngine.SingleStartWait),
            "the single-video hold releases as soon as the exact frame is presented, else at 120 ms");
        expect(SourceProbe.InOrder(SourceProbe.Member(engine, "public void Play()"), "var seekPending = SeekMailSlot.IsPending(ref _pendingSeekBits);",
                   "var from = SeekMailSlot.ResumeFrom(", "_primingSingle = videos == 1;", "PrimesAtPlay(videos, alreadyExact: !seekPending && PausedSceneExact(from))",
                   "if (_primingSince == 0)", "_audio.Play();")
               && Has(SourceProbe.Member(engine, "public void Play()"), "surfaceW >= 8 && surfaceH >= 8 ? VideosAt(state, from) : 0")
               && Has(SourceProbe.Member(engine, "private long PublishAt("), "NotePublished(sequence, asked, playing);")
               && Has(engine, "var lead = _primingSince != 0 ? 0 : GpuViewport.PlayingLead(")
               && Has(SourceProbe.Src("Services", "GpuPreviewEngine.DrawAhead.cs"), "var lead = _primingSince != 0 ? 0 : AheadLead(")
               && Has(engine, "if (_primingSince != 0 && sequence == 0)")
               && Has(engine, "if (_primingSince != 0 && PrimingReady())")
               && Has(SourceProbe.Member(engine, "private bool PrimingReady("), "_primingSingle ? SingleStartReady(_client, _primingSince, _primingScene) : PlayStartReady("),
            "Play holds the audio clock for a cold single video; the pump releases it on the exact frame or the cap");

        // ----- Play-session line names the frame cache cap -----
        expect(FrameCache.CapOf(true, 1, 0, 0, 0) == FrameCache.CapOff && FrameCache.CapOf(false, 100, long.MaxValue, long.MaxValue, long.MaxValue) == FrameCache.CapAuto
               && FrameCache.CapOf(false, 100, 50, 80, 90) == FrameCache.CapDxgi && FrameCache.CapOf(false, 100, 90, 50, 80) == FrameCache.CapCommit
               && FrameCache.CapOf(false, 100, 90, 80, 50) == FrameCache.CapRam && FrameCache.CapOf(false, 100, 100, 200, 300) == FrameCache.CapAuto,
            "the cap is off, the tightest of DXGI, commit and RAM below the wanted budget, else auto");
        expect(FrameCache.CapName(FrameCache.CapOff) == "off" && FrameCache.CapName(FrameCache.CapDxgi) == "dxgi" && FrameCache.CapName(FrameCache.CapCommit) == "commit"
               && FrameCache.CapName(FrameCache.CapRam) == "ram" && FrameCache.CapName(FrameCache.CapAuto) == "auto",
            "cap names: off, dxgi, commit, ram, auto");
        expect(Has(SourceProbe.Member(SourceProbe.Src("Compositor", "FrameCache.cs"), "private long BudgetLocked("), "Volatile.Write(ref _capReason, CapOf(_disabled, wanted, _windowsRoom, _commitRoom, _physicalRoom));")
               && Has(SourceProbe.Src("Compositor", "CompositorWorker.cs"), "control.CacheBudgetCap = cache.CapReason;")
               && Has(SourceProbe.Src("Services", "GpuPreviewEngine.Tabs.cs"), "frame cache budget {Volatile.Read(ref control.CacheBudgetBytes) >> 20} MB (cap {FrameCache.CapName(Volatile.Read(ref control.CacheBudgetCap))}).")
               && Has(SourceProbe.Src("Services", "CompositorSharedMemory.cs"), "[FieldOffset(320)] public int CacheBudgetCap;"),
            "the worker publishes the cap reason and the play-session line ends with it");
    }
}
