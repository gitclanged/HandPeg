using System.Globalization;

namespace HandPegApp.Services;

/// <summary>Performance grade, lowest to highest: F, D, C, B, A, S.</summary>
public enum TierGrade { F, D, C, B, A, S }

/// <summary>
/// Measured score 0-100 and grade: S &gt;= 75, A 55-74, B 38-54, C 22-37, D 10-21, F &lt; 10.
/// (Not named HardwareTier: that name belongs to the memory tier's golden case.)
/// </summary>
public readonly record struct PerformanceTier(int Score, TierGrade Grade)
{
    public string Letter => Grade.ToString();

    /// <summary>A score (rounded, kept within 0..100) and its grade.</summary>
    public static PerformanceTier Of(double score)
    {
        var whole = (int)Math.Round(Math.Max(0, Math.Min(double.IsFinite(score) ? score : 0, 100)), MidpointRounding.AwayFromZero);
        return new PerformanceTier(whole, PerformanceTiers.GradeOf(whole));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Letter} ({Score})");
}

/// <summary>GPU and NPU role names: keys for role cards, menus and tier pills.</summary>
public static class HardwareRoles
{
    public const string Compositor = "Compositor", Decode = "Decode", Background = "Background", Export = "Export", Npu = "NPU";

    /// <summary>A GPU role's card number for Auto, and Background's Processor Only (AppSettings' own numbers).</summary>
    public const int Auto = -1, ProcessorOnly = -2;
}

/// <summary>
/// Pure S-F performance tiers from benchmark measurements only (never adapter names). Each metric maps through log-interpolated
/// (value, score) anchors so doubling speed is a constant step; anchors and weights live in the arrays below for one-edit recalibration
/// (golden hardware-tier-badges pins reference placements).
/// <para>
/// Rationale: the hardware decode test (serial, latency-bound 1080p60 H.264) barely separates GPUs (GTX 1070 ~608-734 fps, Adreno
/// ~623-687, RX 7900 XTX ~1,161-1,188), so it cannot grade alone. Also weighted: CPU decode (FFmpeg fallback for codecs MF lacks; one
/// serial stream, ~387-782 fps), the all-slots compositor frame (~0.5 to 5 ms), and hardware decode breadth (a codec counts only if the
/// GPU decodes it AND an MF decoder exists, e.g. LTSC lacks HEVC). Breadth scales the decode term (H.264-only keeps
/// <see cref="BreadthFloor"/>). Result: Adreno laptop B, GTX 1070 C, RX 7900 XTX S. Bus speed is shown but unscored (unified-memory
/// readback is a memcpy).
/// </para>
/// </summary>
public static class PerformanceTiers
{
    /// <summary>The grade floors, D, C, B, A, S (F is below the first).</summary>
    internal static readonly int[] GradeFloors = [10, 22, 38, 55, 75];

    /// <summary>Hardware decode, frames per second of the 1080p60 H.264 test clip, against a score.</summary>
    internal static readonly (double Value, double Score)[] HardwareDecodeLadder =
        [(30, 0), (120, 10), (250, 21), (450, 40), (800, 66), (1100, 78), (1400, 84), (3200, 100)];

    /// <summary>The processor's decode of the same clip, frames per second, against a score.</summary>
    internal static readonly (double Value, double Score)[] SoftwareDecodeLadder =
        [(30, 0), (120, 10), (250, 21), (450, 32), (800, 52), (1400, 85), (2400, 100)];

    /// <summary>Compositor all-slots frame anchors (1080p, every shader slot; ms, lower is better).</summary>
    internal static readonly (double Value, double Score)[] CompositorLadder =
        [(0.20, 100), (0.70, 90), (1.5, 50), (2.8, 37), (5, 31), (10, 19), (40, 0)];

    /// <summary>System weights: compositor-adapter hardware decode (breadth-scaled), CPU decode, compositor frame.</summary>
    internal static readonly double[] SystemWeights = [0.35, 0.20, 0.45];

    /// <summary>Fraction of the hardware decode term retained with zero <see cref="BreadthCodecs"/> coverage.</summary>
    internal const double BreadthFloor = 0.4;

    /// <summary>The codecs beyond H.264 a hardware decode's breadth counts, each with its share of the breadth.</summary>
    internal static readonly (string Codec, double Share)[] BreadthCodecs = [("HEVC", 0.5), ("AV1", 0.5)];

    /// <summary>Export pill cap by best hardware encoder: AV1 uncapped, HEVC max A, H.264 max B.</summary>
    internal static readonly (string Codec, double Cap)[] EncoderCaps = [("AV1", 100), ("HEVC", 74), ("H.264", 54)];

    /// <summary>The grade of a whole score.</summary>
    public static TierGrade GradeOf(int score)
    {
        var grade = TierGrade.F;
        for (var i = 0; i < GradeFloors.Length && score >= GradeFloors[i]; i++)
            grade = (TierGrade)(i + 1);
        return grade;
    }

    /// <summary>
    /// Evaluates an anchor ladder: log(value) interpolation, clamped to end scores (values ascend; scores may rise or fall). NaN for
    /// non-positive values.
    /// </summary>
    public static double Ladder(ReadOnlySpan<(double Value, double Score)> ladder, double value)
    {
        if (ladder.Length == 0 || !(value > 0) || !double.IsFinite(value))
            return double.NaN;
        if (value <= ladder[0].Value)
            return ladder[0].Score;
        for (var i = 1; i < ladder.Length; i++)
        {
            if (value <= ladder[i].Value)
            {
                var (low, high) = (ladder[i - 1], ladder[i]);
                var t = Math.Log(value / low.Value) / Math.Log(high.Value / low.Value);
                return low.Score + (high.Score - low.Score) * t;
            }
        }

        return ladder[^1].Score;
    }

    /// <summary>A card's hardware decode tier; null when it decoded nothing in hardware.</summary>
    public static PerformanceTier? HardwareDecode(HardwareBenchmarkAdapter card) =>
        card.HardwareDecodeFps > 0 ? PerformanceTier.Of(Ladder(HardwareDecodeLadder, card.HardwareDecodeFps)) : null;

    /// <summary>The processor's decode as the result measured it (once, on every card's row); 0 when it was not.</summary>
    public static double SoftwareDecodeFps(HardwareBenchmarkResult result) => result.Adapters.Count == 0 ? 0 : result.Adapters.Max(a => a.SoftwareDecodeFps);

    /// <summary>CPU decode tier; null when unmeasured (quick checks skip CPU decode).</summary>
    public static PerformanceTier? SoftwareDecode(HardwareBenchmarkResult result) =>
        SoftwareDecodeFps(result) is var fps and > 0 ? PerformanceTier.Of(Ladder(SoftwareDecodeLadder, fps)) : null;

    /// <summary>A card's compositor tier, from its all-slots frame; null when it was not timed.</summary>
    public static PerformanceTier? Compositor(HardwareBenchmarkAdapter card) =>
        card.CompositorMs > 0 ? PerformanceTier.Of(Ladder(CompositorLadder, card.CompositorMs)) : null;

    /// <summary>
    /// Hardware decode breadth 0..1: weighted share of <see cref="BreadthCodecs"/> decoded in hardware with an MF decoder present (adapter
    /// list alone when MF decoders were not listed).
    /// </summary>
    public static double Breadth(HardwareBenchmarkResult result, HardwareBenchmarkAdapter card)
    {
        var breadth = 0.0;
        foreach (var (codec, share) in BreadthCodecs)
        {
            if (card.DecodeCodecs.Contains(codec) && (result.MediaFoundationDecodeCodecs.Count == 0 || result.MediaFoundationDecodeCodecs.Contains(codec)))
                breadth += share;
        }

        return Math.Min(breadth, 1);
    }

    /// <summary>Compositor adapter from the result's roles, else the first adapter with the most VRAM.</summary>
    public static HardwareBenchmarkAdapter? CompositorCard(HardwareBenchmarkResult result)
    {
        var cards = result.Adapters.Where(a => a.SameCardAs < 0).ToList();
        return cards.FirstOrDefault(a => a.Index == result.Recommended.Compositor && a.Note.Length == 0)
               ?? cards.Where(a => a.Note.Length == 0).OrderByDescending(a => a.DedicatedVideoMemoryMb).ThenBy(a => a.Index).FirstOrDefault();
    }

    /// <summary>A card by its DXGI number (its first listing for a card Windows lists twice); null for none.</summary>
    public static HardwareBenchmarkAdapter? Card(HardwareBenchmarkResult result, int index)
    {
        var card = result.Adapters.FirstOrDefault(a => a.Index == index);
        return card is { SameCardAs: >= 0 } repeat ? result.Adapters.FirstOrDefault(a => a.Index == repeat.SameCardAs) ?? card : card;
    }

    /// <summary>
    /// System tier: breadth-scaled compositor-adapter decode, CPU decode and compositor frame, weighted by <see cref="SystemWeights"/>
    /// over measured metrics only (quick checks lack CPU decode; clip-less runs have the compositor only). Hardware decode counts (0 on
    /// failure) once any adapter decoded the clip. Null when nothing was measured (estimate).
    /// </summary>
    public static PerformanceTier? System(HardwareBenchmarkResult result)
    {
        if (result.Error.Length > 0 || CompositorCard(result) is not { } card)
            return null;

        double sum = 0, weights = 0;
        var software = SoftwareDecodeFps(result);
        if (software > 0 || result.Adapters.Any(a => a.HardwareDecodeFps > 0))
        {
            var hardware = card.HardwareDecodeFps > 0 ? Ladder(HardwareDecodeLadder, card.HardwareDecodeFps) * (BreadthFloor + (1 - BreadthFloor) * Breadth(result, card)) : 0;
            (sum, weights) = (sum + SystemWeights[0] * hardware, weights + SystemWeights[0]);
        }

        if (software > 0)
            (sum, weights) = (sum + SystemWeights[1] * Ladder(SoftwareDecodeLadder, software), weights + SystemWeights[1]);
        if (card.CompositorMs > 0)
            (sum, weights) = (sum + SystemWeights[2] * Ladder(CompositorLadder, card.CompositorMs), weights + SystemWeights[2]);
        return weights > 0 ? PerformanceTier.Of(sum / weights) : null;
    }

    /// <summary>System tier tooltip listing measured parts, e.g. "System B (44): 687 fps GPU, 782 fps CPU, 2.84 ms".</summary>
    public static string SystemTip(HardwareBenchmarkResult result, PerformanceTier tier)
    {
        var card = CompositorCard(result);
        var parts = new List<string>();
        if (card is { HardwareDecodeFps: > 0 })
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{card.HardwareDecodeFps:#,0} fps GPU"));
        if (SoftwareDecodeFps(result) is var software and > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{software:#,0} fps CPU"));
        if (card is { CompositorMs: > 0 })
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{card.CompositorMs:0.00} ms"));
        return $"System {tier}" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
    }

    /// <summary>
    /// Export pill: adapter hardware decode score capped by its best encoder (<see cref="EncoderCaps"/>); without a hardware encoder the
    /// CPU decode tier stands in. Null when neither was measured.
    /// </summary>
    public static PerformanceTier? Export(HardwareBenchmarkResult result, HardwareBenchmarkAdapter? card)
    {
        var cap = card is null ? (double?)null : EncoderCaps.Where(c => card.EncodeCodecs.Contains(c.Codec)).Select(c => (double?)c.Cap).FirstOrDefault();
        if (cap is null)
            return SoftwareDecode(result);
        return card!.HardwareDecodeFps > 0 ? PerformanceTier.Of(Math.Min(Ladder(HardwareDecodeLadder, card.HardwareDecodeFps), cap.Value)) : null;
    }

    /// <summary>
    /// Role pill and tooltip ("Decode 687 fps · A (59)") for the role's adapter: an index, <see cref="HardwareRoles.Auto"/> (compositor
    /// adapter), or Background's <see cref="HardwareRoles.ProcessorOnly"/> (CPU decode). NPU has none (unmeasured). Null pill: no measurement.
    /// </summary>
    public static (PerformanceTier? Tier, string Tip) ForRole(HardwareBenchmarkResult? result, string role, int card)
    {
        if (result is null || result.Error.Length > 0 || role == HardwareRoles.Npu)
            return (null, "");

        if (role == HardwareRoles.Background && card == HardwareRoles.ProcessorOnly)
        {
            return SoftwareDecode(result) is { } processor
                ? (processor, string.Create(CultureInfo.InvariantCulture, $"Processor {SoftwareDecodeFps(result):#,0} fps · {processor}"))
                : (null, "");
        }

        var holder = card >= 0 ? Card(result, card) : CompositorCard(result);
        if (holder is null)
            return (null, "");

        switch (role)
        {
            case HardwareRoles.Compositor:
                return Compositor(holder) is { } compositor
                    ? (compositor, string.Create(CultureInfo.InvariantCulture, $"Compositor {holder.CompositorMs:0.00} ms a frame · {compositor}"))
                    : (null, "");
            case HardwareRoles.Export:
            {
                if (Export(result, holder) is not { } export)
                    return (null, "");
                var best = EncoderCaps.Select(c => c.Codec).FirstOrDefault(holder.EncodeCodecs.Contains);
                return (export, best is null
                    ? $"Export in software · {export}"
                    : string.Create(CultureInfo.InvariantCulture, $"Export {best} in hardware, {holder.HardwareDecodeFps:#,0} fps · {export}"));
            }
            default:
                return HardwareDecode(holder) is { } decode
                    ? (decode, string.Create(CultureInfo.InvariantCulture, $"Decode {holder.HardwareDecodeFps:#,0} fps · {decode}"))
                    : (null, "");
        }
    }
}
