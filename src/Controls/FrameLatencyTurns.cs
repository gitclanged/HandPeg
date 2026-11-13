namespace HandPegApp;

/// <summary>
/// The turns of a swap chain's frame-latency waitable object (Pass 5.4.4): what keeps the player's pump at most one frame ahead of the
/// display. DXGI releases the object once for every frame it retires, so every frame queued must take one turn of it; a present that takes
/// none leaves it a count it never loses, and from then on the pump runs that many frames ahead of the display for as long as the swap
/// chain lives.
/// <para>That was the 5.4.4 playback slowdown on the XPS 13 (Adreno X1-85, 60 Hz): the paused pump presents each new frame of a seek or a
/// redraw without a turn (GpuPreviewEngine.Pump, at once, no vsync), so every pause added counts (12 before a session's first play, 40
/// before its sixth), the playing pump then queued as many vsync'd copies on the card, the worker's next frames waited behind them for its
/// output textures' keyed mutexes, its decode lanes' copies behind those, and Media Foundation for the decoder surfaces those copies held:
/// GPU work done 0.2 s (first play) to 0.6 s (sixth) after it was sent, ReadSample 7 ms a frame growing to 30, on-time frames 80 % falling to
/// 30 %. Measured with the play bench: the editor's GPU latency in frames followed the turnless presents made before each play.</para>
/// <para>So each present that finds no turn waited for takes one free at once, and else owes it: the next waits take owed turns back first,
/// as they come free, before they wait for their own. A present the desktop did not queue (DXGI_STATUS_OCCLUDED, a failure) uses no turn.
/// One thread (the pump's). The wait is given (milliseconds to wait, true when a turn was taken): a semaphore in the golden test
/// frame-latency-turns, the swap chain's object in <see cref="GpuViewportControl"/>.</para>
/// </summary>
internal struct FrameLatencyTurns
{
    /// <summary>A turn waited for and not yet used by a present.</summary>
    public bool Taken;

    /// <summary>Frames queued with no turn free: taken back by the next waits, as turns come free, before they wait for their own.</summary>
    public int Owed;

    /// <summary>
    /// Waits for a turn (the playing pump, before each frame), at most <paramref name="milliseconds"/>: turns owed are taken back first, as
    /// many as are free now, and a turn already held is kept (never a second one for the same frame). True when the next present has its turn.
    /// </summary>
    public bool Wait(Func<int, bool> wait, int milliseconds)
    {
        while (Owed > 0 && wait(0))
            Owed--;
        if (!Taken)
            Taken = wait(milliseconds);
        return Taken;
    }

    /// <summary>
    /// A frame was queued (IDXGISwapChain::Present returned S_OK): it uses the turn waited for, else one free now, else it owes one. Not called
    /// for a present that queued nothing, which keeps the turn for the next.
    /// </summary>
    public void Queued(Func<int, bool> wait)
    {
        if (Taken)
            Taken = false;
        else if (!wait(0))
            Owed++;
    }

    /// <summary>A new swap chain (a new waitable object): nothing taken, nothing owed.</summary>
    public void Reset() => (Taken, Owed) = (false, 0);
}
