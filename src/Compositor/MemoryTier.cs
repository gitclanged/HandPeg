namespace HandPegApp.Compositor;

/// <summary>
/// What kind of memory the compositor's card has, and how much of it Instant Seek's frame cache takes when its budget is
/// Auto (Pass 5.2-Arm64AndNpu). Pure: the numbers in, the answer out, so the golden tests check the rule itself
/// (hardware-tier).
/// <para><b>Unified memory.</b> A system-on-chip's graphics (a Snapdragon X's Adreno, an Intel or AMD laptop
/// chip) has no memory of its own: DXGI gives it a token carve-out (128 MB on the Adreno X1-85) and the computer's memory as
/// "shared". The rule of a discrete card (an eighth of its own memory, or half of what is free on it) would hold such a card
/// to 512 MB, a second or two of 1440p; but every frame it keeps is the computer's memory, which is all of it there is. So the
/// cache takes a share of the computer's memory instead: an eighth, held between 1.5 and 2.5 GB on a computer with 12 GB or
/// more (2 GB of 16 GB), and proportionally less below (1 GB of 8 GB), so the editor and everything else keep room.</para>
/// <para>Since 5.4D-TabPerf Auto is adaptive: a <see cref="Baseline"/> grown on demand up to a <see cref="Ceiling"/>, which the
/// user's maximum lowers (<see cref="Maximum"/>). Windows' budget for the process and its commit (FrameCache.CheckWindowsBudget,
/// CheckCommit) still cap whatever this says. The cache fills only as frames are decoded: an idle editor holds none of it.</para>
/// <para>On a big card (<see cref="IsBigCard"/>: a discrete card with more than 4 GB of its own; the TabPerf follow-up, the user's X1)
/// there is no baseline held until it thrashes: the budget is the maximum from the start (<see cref="Budget"/>), filled as frames are
/// decoded, and a tab put behind keeps its frames (CompositorWorker.TrimEmptiesCache). Unified memory and smaller cards keep the
/// baseline, its growth, and the cache emptied on a tab put behind: their frames are the computer's memory.</para>
/// <para>A discrete card that is not big (4 GB or less of its own) keeps to its <see cref="AutoBudget"/> (the 5.4D-TabPerf review): its
/// baseline is at most that, its ceiling at most that too, so a third of the card stays free for the worker's own textures, the 3D view
/// and the Export Quality Preview's encoder, and an eighth is all it takes while something else uses the card. WARP keeps to the
/// <see cref="SmallCardBudget"/>.</para>
/// </summary>
internal static class MemoryTier
{
    private const long Gigabyte = 1L << 30, Megabyte = 1L << 20;

    /// <summary>The unified-memory cache: the least and the most it takes, and the computer's memory from which those bounds hold.</summary>
    public const long UnifiedLeast = 3 * Gigabyte / 2, UnifiedMost = 5 * Gigabyte / 2, UnifiedFullMemory = 12 * Gigabyte;

    /// <summary>The cache of a card with little memory of its own (and of WARP), as before.</summary>
    public const long SmallCardBudget = 512 * Megabyte;

    // A discrete card's Auto: an eighth of its memory, at most 3 GB, while something else uses it; with the card free, up to 80% of
    // its memory, always leaving max(2 GB, a fifth of the card) of what Windows offers free (5.4.1-M4, the user's standing rule).
    private const long MaxAutoBudget = 3 * Gigabyte;
    private const long LeastVramReserve = 2 * Gigabyte;

    // Unified memory's ceiling (5.4.1-M4, across x64 and ARM64): a quarter of the computer's memory up to 32 GB, half above, always
    // leaving max(5 GB, 25%) of it free up to 32 GB and max(12 GB, 30%) above (FrameCache's physical room, from AvailPhys).
    public const long UnifiedLargeMemory = 32 * Gigabyte;
    private const long SmallUnifiedReserve = 5 * Gigabyte, LargeUnifiedReserve = 12 * Gigabyte;

    // A card with no more than this of its own and several times as much shared is a SoC's graphics (DXGI alone, no DXCore).
    private const long MostCarveOut = Gigabyte;
    private const int SharedOverCarveOut = 4;

    /// <summary>
    /// Whether a card shares the computer's memory: DXCore's IsIntegrated when the driver says (<paramref name="integrated"/>),
    /// else DXGI's sizes (a carve-out of at most 1 GB and at least four times as much shared). WARP is not a card.
    /// </summary>
    public static bool IsUnifiedMemory(bool? integrated, long dedicatedBytes, long sharedBytes, bool warp = false) =>
        !warp && (integrated ?? (dedicatedBytes <= MostCarveOut && sharedBytes >= Math.Max(dedicatedBytes, 1) * SharedOverCarveOut));

    /// <summary>
    /// The unified-memory cache for a computer with <paramref name="physicalBytes"/> of memory: an eighth of it held between
    /// <see cref="UnifiedLeast"/> and <see cref="UnifiedMost"/> from <see cref="UnifiedFullMemory"/> up, proportionally less
    /// below (the least scaled by the memory there is, which is an eighth again: 1.5 GB is an eighth of 12 GB, so the rule is
    /// an eighth of the memory, at most 2.5 GB); the small card's 512 MB when the memory is not known.
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
    /// The frame cache's Auto budget, bytes (before Windows' caps): WARP and a small card 512 MB; unified memory
    /// <see cref="UnifiedBudget"/>; a discrete card an eighth of its memory (at most 3 GB) while something else uses it or
    /// before Windows has been asked, else the larger of that and half of what is free on it (at most 12 GB, a third of the
    /// card left).
    /// </summary>
    /// <param name="freeOnCard">What Windows offers beyond what the process holds otherwise; 0 when not asked yet.</param>
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

    /// <summary>What a discrete card always keeps free of the cache (bytes): max(2 GB, a fifth of its own memory).</summary>
    public static long VramReserve(long dedicatedBytes) => Math.Max(LeastVramReserve, dedicatedBytes / 5);

    /// <summary>
    /// Unified memory's ceiling (bytes): a quarter of <paramref name="physicalBytes"/> up to <see cref="UnifiedLargeMemory"/>, half
    /// above; <see cref="SmallCardBudget"/> when the memory is not known.
    /// </summary>
    public static long UnifiedCeiling(long physicalBytes) =>
        physicalBytes <= 0 ? SmallCardBudget : physicalBytes <= UnifiedLargeMemory ? physicalBytes / 4 : physicalBytes / 2;

    /// <summary>What unified memory always keeps free of the computer's available memory (bytes): max(5 GB, 25%) up to 32 GB, max(12 GB, 30%) above.</summary>
    public static long UnifiedReserve(long physicalBytes) =>
        physicalBytes <= UnifiedLargeMemory ? Math.Max(SmallUnifiedReserve, physicalBytes / 4) : Math.Max(LargeUnifiedReserve, physicalBytes / 10 * 3);

    /// <summary>
    /// What a unified-memory cache may hold of the computer's memory (bytes): what it holds now (<paramref name="held"/>, which it could
    /// give back) plus what is available (<paramref name="availablePhysical"/>), less the <see cref="UnifiedReserve"/>.
    /// </summary>
    public static long UnifiedRoom(long held, long availablePhysical, long physicalBytes) =>
        Math.Max(held + availablePhysical - UnifiedReserve(physicalBytes), 0);

    // ----- The adaptive cache (5.4D-TabPerf, the user's U3): a baseline, grown on demand up to the hardware's ceiling -----
    //
    // Auto no longer takes the whole of AutoBudget at once: the cache starts at a baseline (about 2 GB) and grows a gigabyte at a
    // time, at most one step every two seconds, only when it must let go of a frame it still needs (one decoded a moment ago, or one
    // right by a moment being shown or sought: the project's working set is bigger than the cache), and only while Windows' budget
    // for the process and its commit allow it (FrameCache's caps, as before). A tab put behind or a project closed (ClearCache)
    // brings it back to the baseline. The ceiling is the hardware's: a discrete card with more than 4 GB of its own takes today's
    // AutoBudget (up to 12 GB: video memory nothing else wants, the user's U2); a discrete card of 4 GB or less keeps both its baseline
    // and its ceiling within its AutoBudget (the card's margin, and an eighth while something else uses it: the 5.4D-TabPerf review),
    // and 4 GB at most; unified memory keeps to 4 GB at most, and to an eighth of the computer's memory below 32 GB (each frame there is
    // the computer's memory, which the user minds most); WARP to the small card's 512 MB. Since 5.4.1-M4 a discrete card's AutoBudget
    // reaches 80% of its memory (max(2 GB, 20%) kept free) and unified memory's ceiling is UnifiedCeiling (25% up to 32 GB, 50% above). Settings' "Maximum frame cache" (AppSettings.GpuFrameCacheMb) caps the growth: 0 the ceiling, N GB the lower of N
    // and the ceiling. A big card (IsBigCard: the TabPerf follow-up, the user's X1) skips the baseline: its budget is the maximum at once,
    // filled as frames are decoded, and a tab put behind leaves its frames there for the least recently used order to let go of.

    /// <summary>Where Auto starts (bytes): 2 GB; less on unified memory with little of it (its <see cref="UnifiedBudget"/>); 512 MB on WARP or a small card.</summary>
    public const long BaselineBudget = 2 * Gigabyte;

    /// <summary>One step of growth (bytes).</summary>
    public const long GrowStep = Gigabyte;

    /// <summary>The least time between two steps of growth (seconds).</summary>
    public const double GrowEverySeconds = 2;

    /// <summary>A frame decoded this recently (seconds) and already let go of means the cache is too small for what is being shown.</summary>
    public const double ThrashSeconds = 4;

    /// <summary>So does a frame this close (seconds of its file) to a moment being shown, hinted or pre-rolled: the seek window.</summary>
    public const double SeekWindowSeconds = 2;

    /// <summary>The most a card of its own that is not big (4 GB or less of its own, within its AutoBudget) ever grows to (bytes).</summary>
    public const long SmallCeiling = 4 * Gigabyte;

    /// <summary>The cards whose ceiling is today's whole Auto budget: more than this of their own (bytes).</summary>
    public const long BigCardDedicated = 4 * Gigabyte;

    /// <summary>The most the free list keeps of retired frames for reuse (bytes): an eighth of the budget, at most 256 MB.</summary>
    public const long MostFree = 256 * Megabyte;

    /// <summary>
    /// The adaptive cache's baseline (bytes): <see cref="BaselineBudget"/>; on unified memory the smaller of that and the
    /// <see cref="UnifiedBudget"/> of <paramref name="physicalBytes"/> (1 GB of 8 GB); on a discrete card that is not big (4 GB or less of
    /// its own) the smaller of that and its <see cref="AutoBudget"/> (an eighth while <paramref name="cardBusy"/> or before Windows has been
    /// asked, a third of the card left otherwise; <see cref="SmallCardBudget"/> below 512 MB of its own); WARP <see cref="SmallCardBudget"/>.
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
    /// The most the cache grows to on this hardware (bytes, before Windows' caps): a discrete card with more than 4 GB of its own,
    /// <see cref="AutoBudget"/> (an eighth while something else uses the card or before Windows has been asked; up to 80% of the card,
    /// max(2 GB, 20%) kept free, once it has been); a discrete card of 4 GB or less the smaller of 4 GB and its <see cref="AutoBudget"/>;
    /// WARP <see cref="SmallCardBudget"/>; unified memory the larger of its <see cref="Baseline"/> and its <see cref="UnifiedCeiling"/>.
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

    /// <summary>
    /// The most the cache may hold (bytes): the <paramref name="ceiling"/>, or the user's maximum (<paramref name="requested"/> bytes,
    /// Settings' "Maximum frame cache") when that is lower. <paramref name="requested"/> 0 is Auto (the ceiling).
    /// </summary>
    public static long Maximum(long requested, long ceiling) => requested > 0 ? Math.Min(requested, ceiling) : ceiling;

    /// <summary>
    /// The budget after <paramref name="steps"/> steps of growth from <paramref name="baseline"/>, never above <paramref name="maximum"/>
    /// (bytes); a step is <see cref="GrowStep"/> unless <paramref name="step"/> says (the goldens' small scale).
    /// </summary>
    public static long Grown(long baseline, int steps, long maximum, long step = GrowStep) => Math.Min(baseline + Math.Max(steps, 0) * step, maximum);

    /// <summary>
    /// A big card (the TabPerf follow-up, the user's X1: "keep scrubbing as smooth as possible if the hardware is there"): a discrete card
    /// with more than 4 GB of its own (<see cref="BigCardDedicated"/>), not WARP. Its frames are video memory nothing else wants: the
    /// cache takes its whole maximum as frames are decoded (<see cref="Budget"/>) and keeps them when a tab is put behind.
    /// </summary>
    public static bool IsBigCard(bool warp, bool unified, long dedicatedBytes) => !warp && !unified && dedicatedBytes > BigCardDedicated;

    /// <summary>
    /// The budget before Windows' caps (bytes): on a big card (<paramref name="bigCard"/>) the <paramref name="maximum"/> from the start,
    /// no growth needed; elsewhere the <paramref name="baseline"/> after <paramref name="steps"/> steps of growth (<see cref="Grown"/>).
    /// </summary>
    public static long Budget(bool bigCard, long baseline, int steps, long maximum, long step = GrowStep) =>
        bigCard ? maximum : Grown(baseline, steps, maximum, step);

    /// <summary>Whether one more step of growth is allowed: below the maximum still, and the last step at least <see cref="GrowEverySeconds"/> ago.</summary>
    public static bool MayGrow(long baseline, int steps, long maximum, double secondsSinceGrown, long step = GrowStep) =>
        Grown(baseline, steps, maximum, step) < maximum && secondsSinceGrown >= GrowEverySeconds;

    /// <summary>
    /// Whether letting go of a frame shows the cache is too small (thrash): it was decoded less than <see cref="ThrashSeconds"/> ago, or
    /// lies within <see cref="SeekWindowSeconds"/> of a moment of interest of its file (shown, hinted, pre-rolled; infinity for none).
    /// </summary>
    public static bool IsThrash(double secondsSinceInserted, double secondsFromInterest) =>
        secondsSinceInserted < ThrashSeconds || secondsFromInterest <= SeekWindowSeconds;

    /// <summary>The most the free list keeps (bytes): an eighth of <paramref name="budget"/>, at most <see cref="MostFree"/>.</summary>
    public static long FreeListCap(long budget) => Math.Min(budget / 8, MostFree);
}
