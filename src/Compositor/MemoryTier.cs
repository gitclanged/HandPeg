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
/// <para>A user's own budget always wins; Windows' budget for the process and its commit (FrameCache.CheckWindowsBudget,
/// CheckCommit) still cap whatever this says. The cache fills only as frames are decoded: an idle editor holds none of it.</para>
/// </summary>
internal static class MemoryTier
{
    private const long Gigabyte = 1L << 30, Megabyte = 1L << 20;

    /// <summary>The unified-memory cache: the least and the most it takes, and the computer's memory from which those bounds hold.</summary>
    public const long UnifiedLeast = 3 * Gigabyte / 2, UnifiedMost = 5 * Gigabyte / 2, UnifiedFullMemory = 12 * Gigabyte;

    /// <summary>The cache of a card with little memory of its own (and of WARP), as before.</summary>
    public const long SmallCardBudget = 512 * Megabyte;

    // A discrete card's Auto: an eighth of its memory, at most 3 GB; with the card free, half of what is free, at most 12 GB,
    // always leaving a third of the card (1 to 8 GB). The numbers FrameCache had since 5.1-TrackEqualityAndVram.
    private const long MaxAutoBudget = 3 * Gigabyte, MaxSuperBudget = 12 * Gigabyte;
    private const long LeastMargin = Gigabyte, MostMargin = 8 * Gigabyte;

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
        return Math.Max(eighth, Math.Min(Math.Min(freeOnCard / 2, MaxSuperBudget), freeOnCard - Math.Clamp(dedicatedBytes / 3, LeastMargin, MostMargin)));
    }
}
