namespace HandPegApp.Compositor;

/// <summary>
/// Frame-cache budget policy for the compositor adapter. Pure functions (sizes in, bytes out) so goldens test the rule directly.
/// <para>Unified memory (SoC graphics) reports a token DXGI carve-out plus shared system RAM, so its budget is a share of physical
/// RAM, not of dedicated VRAM.</para>
/// <para>Auto is adaptive: a <see cref="Baseline"/> grown on thrash up to a <see cref="Ceiling"/>, lowered by the Settings cap
/// (<see cref="Maximum"/>) and always bounded by FrameCache's DXGI budget and commit checks. A big discrete adapter
/// (<see cref="IsBigCard"/>) starts at its maximum and keeps frames for background tabs.</para>
/// </summary>
internal static class MemoryTier
{
    private const long Gigabyte = 1L << 30, Megabyte = 1L << 20;

    /// <summary>Unified-memory budget bounds, and the physical RAM size from which they apply.</summary>
    public const long UnifiedLeast = 3 * Gigabyte / 2, UnifiedMost = 5 * Gigabyte / 2, UnifiedFullMemory = 12 * Gigabyte;

    /// <summary>Budget for WARP and for adapters with less than 512 MB of dedicated VRAM.</summary>
    public const long SmallCardBudget = 512 * Megabyte;

    // Discrete Auto: VRAM/8 (max 3 GB) while the adapter is busy; when idle up to 80% of VRAM, always leaving
    // max(2 GB, VRAM/5) of the DXGI budget free.
    private const long MaxAutoBudget = 3 * Gigabyte;
    private const long LeastVramReserve = 2 * Gigabyte;

    // Unified ceiling: RAM/4 up to 32 GB, RAM/2 above; always leaving max(5 GB, 25%) of RAM free up to 32 GB and
    // max(12 GB, 30%) above (checked against AvailPhys by FrameCache).
    public const long UnifiedLargeMemory = 32 * Gigabyte;
    private const long SmallUnifiedReserve = 5 * Gigabyte, LargeUnifiedReserve = 12 * Gigabyte;

    // DXGI-only fallback (no DXCore): <= 1 GB dedicated with >= 4x as much shared is SoC graphics.
    private const long MostCarveOut = Gigabyte;
    private const int SharedOverCarveOut = 4;

    /// <summary>
    /// True for unified memory: DXCore IsIntegrated when reported (<paramref name="integrated"/>), else the DXGI size heuristic
    /// (<= 1 GB dedicated, >= 4x as much shared). Always false for WARP.
    /// </summary>
    public static bool IsUnifiedMemory(bool? integrated, long dedicatedBytes, long sharedBytes, bool warp = false) =>
        !warp && (integrated ?? (dedicatedBytes <= MostCarveOut && sharedBytes >= Math.Max(dedicatedBytes, 1) * SharedOverCarveOut));

    /// <summary>
    /// Unified-memory budget: RAM/8, clamped to [<see cref="UnifiedLeast"/>, <see cref="UnifiedMost"/>] from
    /// <see cref="UnifiedFullMemory"/> up and unclamped below; <see cref="SmallCardBudget"/> when RAM is unknown.
    /// </summary>
    public static long UnifiedBudget(long physicalBytes)
    {
        if (physicalBytes <= 0)
            return SmallCardBudget;
        return physicalBytes >= UnifiedFullMemory
            ? Math.Clamp(physicalBytes / 8, UnifiedLeast, UnifiedMost)
            : physicalBytes / 8;
    }

    /// <summary>
    /// Auto budget before DXGI caps: WARP or small adapter 512 MB; unified <see cref="UnifiedBudget"/>; discrete VRAM/8 (max 3 GB)
    /// while busy or before the budget is queried, else max(VRAM/8, min(80% VRAM, VRAM - reserve, free - reserve)).
    /// </summary>
    /// <param name="freeOnCard">DXGI budget beyond the process's other usage; 0 when not yet queried.</param>
    public static long AutoBudget(bool warp, bool unified, long dedicatedBytes, long physicalBytes, bool cardBusy, long freeOnCard)
    {
        if (warp)
            return SmallCardBudget;
        if (unified)
            return UnifiedBudget(physicalBytes);
        if (dedicatedBytes < SmallCardBudget)
            return SmallCardBudget;
        var eighth = Math.Min(dedicatedBytes / 8, MaxAutoBudget);
        if (cardBusy || freeOnCard == 0)
            return eighth;
        var reserve = VramReserve(dedicatedBytes);
        return Math.Max(eighth, Math.Min(Math.Min(dedicatedBytes / 5 * 4, dedicatedBytes - reserve), freeOnCard - reserve));
    }

    /// <summary>VRAM a discrete adapter always keeps free of the cache: max(2 GB, VRAM/5).</summary>
    public static long VramReserve(long dedicatedBytes) => Math.Max(LeastVramReserve, dedicatedBytes / 5);

    /// <summary>Unified ceiling: RAM/4 up to <see cref="UnifiedLargeMemory"/>, RAM/2 above; <see cref="SmallCardBudget"/> when RAM is unknown.</summary>
    public static long UnifiedCeiling(long physicalBytes) =>
        physicalBytes <= 0 ? SmallCardBudget : physicalBytes <= UnifiedLargeMemory ? physicalBytes / 4 : physicalBytes / 2;

    /// <summary>Available RAM a unified-memory cache always leaves free: max(5 GB, 25%) up to 32 GB, max(12 GB, 30%) above.</summary>
    public static long UnifiedReserve(long physicalBytes) =>
        physicalBytes <= UnifiedLargeMemory ? Math.Max(SmallUnifiedReserve, physicalBytes / 4) : Math.Max(LargeUnifiedReserve, physicalBytes / 10 * 3);

    /// <summary>Unified-memory headroom: bytes held (<paramref name="held"/>) plus <paramref name="availablePhysical"/>, minus <see cref="UnifiedReserve"/>; never negative.</summary>
    public static long UnifiedRoom(long held, long availablePhysical, long physicalBytes) =>
        Math.Max(held + availablePhysical - UnifiedReserve(physicalBytes), 0);

    // ----- Adaptive cache: baseline grown on thrash up to the hardware ceiling -----
    //
    // Starts at the baseline and grows one GrowStep at most every GrowEverySeconds, only on thrash (IsThrash) and only within
    // FrameCache's DXGI budget and commit caps; ClearCache (tab deactivated, project closed) returns to the baseline. Ceiling:
    // big discrete AutoBudget, other discrete min(4 GB, AutoBudget), unified UnifiedCeiling, WARP 512 MB; GpuFrameCacheMb caps
    // growth (0 = ceiling). A big adapter skips the baseline and keeps background-tab frames for LRU eviction.

    /// <summary>Auto starting budget: 2 GB (less on low-RAM unified memory; 512 MB on WARP or a small adapter).</summary>
    public const long BaselineBudget = 2 * Gigabyte;

    /// <summary>One step of growth (bytes).</summary>
    public const long GrowStep = Gigabyte;

    /// <summary>Minimum interval between growth steps (seconds).</summary>
    public const double GrowEverySeconds = 2;

    /// <summary>Evicting a frame decoded within this many seconds signals thrash (working set larger than the cache).</summary>
    public const double ThrashSeconds = 4;

    /// <summary>Evicting a frame within this many file seconds of a shown, hinted or pre-rolled time also signals thrash.</summary>
    public const double SeekWindowSeconds = 2;

    /// <summary>Growth cap for discrete adapters with 4 GB of VRAM or less (bytes).</summary>
    public const long SmallCeiling = 4 * Gigabyte;

    /// <summary>Dedicated VRAM above which an adapter is big (<see cref="IsBigCard"/>), bytes.</summary>
    public const long BigCardDedicated = 4 * Gigabyte;

    /// <summary>Free-list cap for recycled frames: budget/8, at most 256 MB.</summary>
    public const long MostFree = 256 * Megabyte;

    /// <summary>
    /// Adaptive baseline: <see cref="BaselineBudget"/>, reduced to <see cref="UnifiedBudget"/> on unified memory and to
    /// <see cref="AutoBudget"/> on a non-big discrete adapter; <see cref="SmallCardBudget"/> on WARP.
    /// </summary>
    public static long Baseline(bool warp, bool unified, long dedicatedBytes, long physicalBytes, bool cardBusy, long freeOnCard)
    {
        if (warp)
            return SmallCardBudget;
        if (unified)
            return Math.Min(BaselineBudget, UnifiedBudget(physicalBytes));
        if (IsBigCard(warp, unified, dedicatedBytes))
            return BaselineBudget;
        return Math.Min(BaselineBudget, AutoBudget(warp, unified, dedicatedBytes, physicalBytes, cardBusy, freeOnCard));
    }

    /// <summary>
    /// Growth ceiling before DXGI caps: big discrete <see cref="AutoBudget"/>; other discrete min(<see cref="SmallCeiling"/>, AutoBudget);
    /// WARP <see cref="SmallCardBudget"/>; unified max(<see cref="Baseline"/>, <see cref="UnifiedCeiling"/>).
    /// </summary>
    public static long Ceiling(bool warp, bool unified, long dedicatedBytes, long physicalBytes, bool cardBusy, long freeOnCard)
    {
        if (warp)
            return SmallCardBudget;
        if (!unified)
        {
            var auto = AutoBudget(warp, unified, dedicatedBytes, physicalBytes, cardBusy, freeOnCard);
            return IsBigCard(warp, unified, dedicatedBytes) ? auto : Math.Min(SmallCeiling, auto);
        }

        return Math.Max(Baseline(warp, unified, dedicatedBytes, physicalBytes, cardBusy, freeOnCard), UnifiedCeiling(physicalBytes));
    }

    /// <summary>Effective maximum: <paramref name="ceiling"/>, or the Settings cap <paramref name="requested"/> when lower (0 = Auto).</summary>
    public static long Maximum(long requested, long ceiling) => requested > 0 ? Math.Min(requested, ceiling) : ceiling;

    /// <summary>
    /// <paramref name="baseline"/> plus <paramref name="steps"/> growth steps, capped at <paramref name="maximum"/>;
    /// <paramref name="step"/> overrides <see cref="GrowStep"/> for goldens.
    /// </summary>
    public static long Grown(long baseline, int steps, long maximum, long step = GrowStep) => Math.Min(baseline + Math.Max(steps, 0) * step, maximum);

    /// <summary>
    /// Discrete, non-WARP adapter with more than <see cref="BigCardDedicated"/> of VRAM: the cache takes its full maximum up front
    /// (<see cref="Budget"/>) and keeps frames for background tabs.
    /// </summary>
    public static bool IsBigCard(bool warp, bool unified, long dedicatedBytes) => !warp && !unified && dedicatedBytes > BigCardDedicated;

    /// <summary>Budget before DXGI caps: <paramref name="maximum"/> on a big adapter, else <see cref="Grown"/>.</summary>
    public static long Budget(bool bigCard, long baseline, int steps, long maximum, long step = GrowStep) =>
        bigCard ? maximum : Grown(baseline, steps, maximum, step);

    /// <summary>True while below the maximum and the last growth step is at least <see cref="GrowEverySeconds"/> old.</summary>
    public static bool MayGrow(long baseline, int steps, long maximum, double secondsSinceGrown, long step = GrowStep) =>
        Grown(baseline, steps, maximum, step) < maximum && secondsSinceGrown >= GrowEverySeconds;

    /// <summary>
    /// Thrash test for an evicted frame: decoded under <see cref="ThrashSeconds"/> ago, or within <see cref="SeekWindowSeconds"/>
    /// of a shown/hinted/pre-rolled time (<paramref name="secondsFromInterest"/> is infinity when none).
    /// </summary>
    public static bool IsThrash(double secondsSinceInserted, double secondsFromInterest) =>
        secondsSinceInserted < ThrashSeconds || secondsFromInterest <= SeekWindowSeconds;

    /// <summary>Free-list cap: <paramref name="budget"/>/8, at most <see cref="MostFree"/>.</summary>
    public static long FreeListCap(long budget) => Math.Min(budget / 8, MostFree);
}
