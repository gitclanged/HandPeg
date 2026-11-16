namespace HandPegApp;

/// <summary>
/// Accounting for the swap chain's frame-latency waitable, which keeps the pump at most one frame ahead of display. DXGI signals once
/// per retired frame, so every queued present must consume one signal; an unmatched present leaves a permanent surplus and the pump runs
/// that many frames ahead for the swap chain's lifetime.
/// <para>Paused seeks/redraws present without waiting, accumulating surplus with each pause; playback then queued that many vsync'd
/// frames, backing up keyed mutexes, decode copies and MF decoder surfaces, so GPU latency grew to 0.2-0.6 s and on-time frames fell
/// from 80 % to 30 %.</para>
/// <para>Fix: a present without a prior wait takes a free signal immediately or records a debt; subsequent waits repay debts first as
/// signals arrive. Presents that queue nothing (DXGI_STATUS_OCCLUDED, failures) consume none. Single-threaded (pump). The wait function
/// is injected (ms timeout, true if acquired): a semaphore in golden frame-latency-turns, the swap chain object in
/// <see cref="GpuViewport"/>.</para>
/// </summary>
internal struct FrameLatencyTurns
{
    /// <summary>A turn waited for and not yet used by a present.</summary>
    public bool Taken;

    /// <summary>Outstanding debt: presents queued without a signal, repaid by later waits before their own.</summary>
    public int Owed;

    /// <summary>
    /// Waits up to <paramref name="milliseconds"/> before a playing frame: first repays available debt, keeps an already-held signal (never
    /// two per frame). True when the next present holds a signal.
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
    /// Records a queued present (S_OK): consumes the held signal, else a free one, else adds debt. Not called for presents that queued
    /// nothing (the signal is kept).
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
