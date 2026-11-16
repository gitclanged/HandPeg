using System.Globalization;

namespace HandPegApp.Services;

/// <summary>A performance grade (5.6.0), from the lowest to the highest: F, D, C, B, A, S.</summary>
public enum TierGrade { F, D, C, B, A, S }

/// <summary>
/// A measured performance score, 0 to 100, and its grade (5.6.0): S 75 and up, A 55-74, B 38-54, C 22-37, D 10-21, F under 10.
/// (Not named HardwareTier: the memory tier's golden case is called hardware-tier.)
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

/// <summary>The GPU and NPU roles by name (5.6.0): the role cards' keys, the menus' and the tier pills'.</summary>
public static class HardwareRoles
{
    public const string Compositor = "Compositor", Decode = "Decode", Background = "Background", Export = "Export", Npu = "NPU";

    /// <summary>A GPU role's card number for Auto, and Background's Processor Only (AppSettings' own numbers).</summary>
    public const int Auto = -1, ProcessorOnly = -2;
}

/// <summary>
/// The S-F performance tiers (5.6.0), pure: each from measured values only (the hardware benchmark's), never from a card's name.
/// Every metric goes through a ladder of anchors (value, score) interpolated in the logarithm of the value, so that twice as fast is the
/// same step wherever it falls; the anchors and weights are kept in the arrays below, so that recalibrating is one edit (the golden case
/// hardware-tier-badges pins the machines each must place).
/// <para>
/// Why these inputs and weights. The benchmark's hardware decode is a serial, latency-bound run of one 1080p60 H.264 clip: it barely tells
/// cards apart (a GTX 1070 decodes it as fast as a Snapdragon X Elite's Adreno, 608-734 against 623-687 fps; an RX 7900 XTX
/// 1,161-1,188), so it cannot carry the system grade alone. What tells them apart is what HandPeg leans on beside it: the processor's
/// decode (FFmpeg decodes what Media Foundation cannot: the Snapdragon 782 fps, a 7950X3D 657-710 (one serial stream: no measure of its
/// cores), a Ryzen 5 2600 387-481), the all-slots compositor frame (RX 7900 XTX 0.551 ms, GTX 1070 2.84, Adreno 5.09) and the codec breadth
/// of the hardware decode (a codec only counts when the card decodes it AND Media Foundation has a decoder for it here: Windows 11 IoT
/// LTSC has no HEVC decoder, so its HEVC is decoded by the processor). Breadth scales the hardware decode term (a card that decodes H.264
/// alone keeps <see cref="BreadthFloor"/> of it). That places the Snapdragon laptop in B, the GTX 1070 machine in C and the RX 7900 XTX
/// machine in S although the Adreno composites slower than the GTX 1070. Bus speed is shown, never scored: a unified-memory readback is a memcpy.
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

    /// <summary>The all-slots compositor frame (1920 x 1080, every shader slot), milliseconds, against a score: lower is better.</summary>
    internal static readonly (double Value, double Score)[] CompositorLadder =
        [(0.20, 100), (0.70, 90), (1.5, 50), (2.8, 37), (5, 31), (10, 19), (40, 0)];

    /// <summary>The system score's weights: the compositor card's hardware decode (breadth-scaled), the processor's decode, the compositor.</summary>
    internal static readonly double[] SystemWeights = [0.35, 0.20, 0.45];

    /// <summary>The share of the hardware decode term a card keeps that decodes none of <see cref="BreadthCodecs"/> in hardware.</summary>
    internal const double BreadthFloor = 0.4;

    /// <summary>The codecs beyond H.264 a hardware decode's breadth counts, each with its share of the breadth.</summary>
    internal static readonly (string Codec, double Share)[] BreadthCodecs = [("HEVC", 0.5), ("AV1", 0.5)];

    /// <summary>The Export pill's cap by the best hardware encoder of its card: AV1 none, HEVC an A at most, H.264 a B at most.</summary>
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
    /// A ladder read at a value: interpolated in log(value) between the anchors either side, the first or last anchor's score beyond
    /// them (the anchors go up in value; their scores may go either way). NaN for a value that is not positive.
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

    /// <summary>The processor's decode tier; null when it was not measured (the quick check never decodes on the processor).</summary>
    public static PerformanceTier? SoftwareDecode(HardwareBenchmarkResult result) =>
        SoftwareDecodeFps(result) is var fps and > 0 ? PerformanceTier.Of(Ladder(SoftwareDecodeLadder, fps)) : null;

    /// <summary>A card's compositor tier, from its all-slots frame; null when it was not timed.</summary>
    public static PerformanceTier? Compositor(HardwareBenchmarkAdapter card) =>
        card.CompositorMs > 0 ? PerformanceTier.Of(Ladder(CompositorLadder, card.CompositorMs)) : null;

    /// <summary>
    /// A card's hardware decode breadth, 0 to 1: the shares of <see cref="BreadthCodecs"/> it decodes in hardware where Media Foundation
    /// has a decoder for the codec too (when the result lists Media Foundation's decoders; else the card's own list alone).
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

    /// <summary>The card the compositor runs on as the result's roles name it, else the first-listed card with the most video memory.</summary>
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
    /// The system tier: the compositor card's hardware decode scaled by its breadth, the processor's decode and the compositor frame, by
    /// <see cref="SystemWeights"/>, over the metrics the result measured (a quick check has no processor decode; a run with no test clip
    /// has the compositor alone). A hardware decode is counted (0 when the card could not) once the result decoded the clip anywhere.
    /// Null when nothing was measured (an estimate).
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

    /// <summary>The system tier's tooltip: "System B (44): 687 fps GPU, 782 fps CPU, 5.09 ms" (the parts measured).</summary>
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
    /// The Export pill: the card's hardware decode score capped by its best hardware encoder (<see cref="EncoderCaps"/>); with no hardware
    /// encoder, exports encode on the processor: its decode tier stands for it. Null when neither was measured.
    /// </summary>
    public static PerformanceTier? Export(HardwareBenchmarkResult result, HardwareBenchmarkAdapter? card)
    {
        var cap = card is null ? (double?)null : EncoderCaps.Where(c => card.EncodeCodecs.Contains(c.Codec)).Select(c => (double?)c.Cap).FirstOrDefault();
        if (cap is null)
            return SoftwareDecode(result);
        return card!.HardwareDecodeFps > 0 ? PerformanceTier.Of(Math.Min(Ladder(HardwareDecodeLadder, card.HardwareDecodeFps), cap.Value)) : null;
    }

    /// <summary>
    /// A role's pill and its tooltip ("Decode 687 fps · A (59)"), from a result's measurements, for the card the role holds: a card's
    /// number, <see cref="HardwareRoles.Auto"/> (the compositor's card; the Compositor's own Auto, its card), or Background's
    /// <see cref="HardwareRoles.ProcessorOnly"/> (the processor's decode). The NPU has none (nothing is measured on it). Null pill: nothing
    /// measured for that role on that card.
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
