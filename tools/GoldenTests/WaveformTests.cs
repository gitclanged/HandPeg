using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// WaveformData's mip pyramid (4.4): its folds, RangeOf and Range against a brute-force min/max of the pairs, and
/// Envelope's columns against what lies in and about each, over seeded random peaks. The golden file says what was
/// checked; what failed is in the problems. How long Envelope takes is printed, never checked (it would make runs flaky).
/// </summary>
public static class WaveformTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("waveform-mips",
            "# WaveformData's mip pyramid (4.4): RangeOf, Range and Envelope against a brute-force min/max over seeded random peaks.", Mips);
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Mips(StringBuilder text, List<string> problems)
    {
        // Half an hour and a pair of a sound that swells and fades, with a rare loud spike: min/max over a long stretch
        // is then not simply -1 and 1, and the folds have something to get wrong.
        const int count = 36_001;
        var random = new Random(4404);
        var (lows, highs) = (new float[count], new float[count]);
        for (var i = 0; i < count; i++)
        {
            var swell = Math.Pow(Math.Abs(Math.Sin(i / 900.0)), 3);
            highs[i] = (float)(swell * random.NextDouble());
            lows[i] = (float)(-swell * random.NextDouble());
            if (random.Next(5000) == 0)
                highs[i] = (float)(0.9 + 0.1 * random.NextDouble());
        }

        var data = new WaveformData(lows, highs, Brushes.Gray);
        Line(text, $"pairs {data.Count} at {data.PairsPerSecond} a second ({data.Seconds:0.00} s); levels {data.CountAt(0)}, {data.CountAt(1)}, {data.CountAt(2)}, {data.CountAt(3)}");

        // Each pair of each level is the lowest and the highest of the pairs it folds.
        for (var level = 1; level < WaveformData.MipLevels; level++)
        {
            var fold = 1 << (3 * level);
            var (levelLows, levelHighs) = (data.LowsAt(level).ToArray(), data.HighsAt(level).ToArray());
            for (var j = 0; j < levelLows.Length; j++)
            {
                var expected = Brute(lows, highs, j * fold, (j + 1) * fold);
                if (levelLows[j] != expected.Low || levelHighs[j] != expected.High)
                {
                    problems.Add($"level {level} pair {j} is ({levelLows[j]}, {levelHighs[j]}), not ({expected.Low}, {expected.High})");
                    break;
                }
            }
        }

        Line(text, $"folds: each pair of levels 1 to 3 is the lowest and the highest of the pairs it folds");

        Expect(problems, WaveformData.LevelFor(7.99) == 0 && WaveformData.LevelFor(8) == 1 && WaveformData.LevelFor(63.9) == 1
            && WaveformData.LevelFor(64) == 2 && WaveformData.LevelFor(511.9) == 2 && WaveformData.LevelFor(512) == 3, "LevelFor's steps are 8, 64 and 512");
        Line(text, $"LevelFor: 0 below 8 pairs a column, 1 from 8, 2 from 64, 3 from 512");

        // RangeOf: stretches of every length, every tenth of them reaching the last pair (the tail fold), and the odd ones.
        for (var k = 0; k < 2000; k++)
        {
            var length = 1 + (int)Math.Exp(random.NextDouble() * Math.Log(count));
            var first = random.Next(count);
            var end = k % 10 == 0 ? count : Math.Min(first + length, count);
            if (k % 10 == 0)
                first = Math.Max(count - length, 0);
            if (!Same(data.RangeOf(first, end), Brute(lows, highs, first, end)))
            {
                problems.Add($"RangeOf({first}, {end}) is {data.RangeOf(first, end)}, not {Brute(lows, highs, first, end)}");
                break;
            }
        }

        Expect(problems, data.RangeOf(5, 5) == (0f, 0f), "RangeOf of no pairs is (0, 0)");
        Expect(problems, Same(data.RangeOf(-3, 2), Brute(lows, highs, 0, 2)), "RangeOf from before the start reads from the start");
        Expect(problems, Same(data.RangeOf(count - 1, count + 5), Brute(lows, highs, count - 1, count)), "RangeOf past the end reads to the end");
        Line(text, $"RangeOf: 2000 stretches (every tenth up to the last pair) and the empty, before-the-start and past-the-end ones, each the brute-force min/max");

        // Range by fractions: what the pair-by-pair Range of 4.3 gave, exactly.
        for (var k = 0; k < 2000; k++)
        {
            var from = random.NextDouble() * 1.2 - 0.1;
            var to = from + random.NextDouble() * (k % 3 == 0 ? 1 : 0.01);
            if (!Same(data.Range(from, to), OldRange(lows, highs, from, to)))
            {
                problems.Add($"Range({from}, {to}) is {data.Range(from, to)}, not {OldRange(lows, highs, from, to)}");
                break;
            }
        }

        Line(text, $"Range: 2000 pairs of fractions (some before 0 and past 1), each what the pair-by-pair Range of 4.3 gave");

        // Envelope: each column holds nothing from further than a fold beyond it, and misses none of the folds wholly inside it.
        var seconds = data.Seconds;
        for (var k = 0; k < 800; k++)
        {
            var length = Math.Exp(random.NextDouble() * Math.Log(seconds * 1.2 / 0.01)) * 0.01;
            var start = random.NextDouble() * (seconds + 10) - 5 - (k % 7 == 0 ? length : 0);
            var columns = 1 + random.Next(k % 5 == 0 ? 1500 : 300);
            var (columnLows, columnHighs) = (new float[columns], new float[columns]);
            data.Envelope(start, start + length, columnLows, columnHighs);

            var (pairsFrom, pairsTo) = (start * data.PairsPerSecond, (start + length) * data.PairsPerSecond);
            var perColumn = (pairsTo - pairsFrom) / columns;
            var fold = 1 << (3 * WaveformData.LevelFor(perColumn));
            for (var c = 0; c < columns; c++)
            {
                var (from, to) = (pairsFrom + c * perColumn, pairsFrom + (c + 1) * perColumn);
                var (low, high) = (columnLows[c], columnHighs[c]);
                var silent = low == 0 && high == 0;
                var outer = Brute(lows, highs, (int)Math.Floor(from) - fold, (int)Math.Ceiling(to) + fold);
                if (!silent && (low < outer.Low || high > outer.High))
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Envelope({start}, {start + length}) into {columns}: column {c} ({low}, {high}) reaches past the pairs about it ({outer.Low}, {outer.High})"));
                    k = int.MaxValue - 1;
                    break;
                }

                var (innerFirst, innerEnd) = ((int)Math.Ceiling(from / fold) * fold, (int)Math.Floor(to / fold) * fold);
                if (innerEnd > innerFirst && innerFirst >= 0 && innerEnd <= count)
                {
                    var inner = Brute(lows, highs, innerFirst, innerEnd);
                    if (low > inner.Low || high < inner.High)
                    {
                        problems.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Envelope({start}, {start + length}) into {columns}: column {c} ({low}, {high}) misses what lies inside it ({inner.Low}, {inner.High})"));
                        k = int.MaxValue - 1;
                        break;
                    }
                }
            }
        }

        Line(text, $"Envelope: 800 stretches and column counts (some before the start and past the end): each column within the pairs about it, missing none of the folds wholly inside it");

        // A stretch cut out keeps its rate and is folded again.
        var slice = data.Slice(0.25, 0.5);
        Expect(problems, Same(slice.RangeOf(0, slice.Count), Brute(lows, highs, 9000, 18001)) && Same(slice.RangeOf(100, 5000), Brute(lows, highs, 9100, 14000)),
            "a slice's ranges are those of the pairs it was cut from");
        Line(text, $"Slice: the second quarter cut out keeps its rate ({slice.PairsPerSecond} a second), {slice.Count} pairs, and the same lowest and highest");

        // How long Envelope takes to fill a timeline's width with the whole sound: said, not checked.
        var (wideLows, wideHighs) = (new float[1500], new float[1500]);
        var times = new double[101];
        var clock = new Stopwatch();
        for (var r = 0; r < times.Length; r++)
        {
            clock.Restart();
            data.Envelope(0, seconds, wideLows, wideHighs);
            times[r] = clock.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  info  waveform-mips: Envelope of {seconds:0} s into 1500 columns, median {times[times.Length / 2]:0.000} ms"));
    }

    private static void Expect(List<string> problems, bool held, string what)
    {
        if (!held)
            problems.Add("not so: " + what);
    }

    private static bool Same((float Low, float High) a, (float Low, float High) b) => a.Low == b.Low && a.High == b.High;

    /// <summary>The lowest and the highest of the pairs from first up to end, one by one; (0, 0) for none.</summary>
    private static (float Low, float High) Brute(float[] lows, float[] highs, int first, int end)
    {
        (first, end) = (Math.Max(first, 0), Math.Min(end, lows.Length));
        if (end <= first)
            return (0, 0);

        var (low, high) = (float.PositiveInfinity, float.NegativeInfinity);
        for (var i = first; i < end; i++)
        {
            low = Math.Min(low, lows[i]);
            high = Math.Max(high, highs[i]);
        }

        return (low, high);
    }

    /// <summary>WaveformData.Range as 4.3 had it: every pair between two fractions of the sound, one by one.</summary>
    private static (float Low, float High) OldRange(float[] lows, float[] highs, double from, double to)
    {
        if (highs.Length == 0 || to <= 0 || from >= 1)
            return (0, 0);

        var first = Math.Clamp((int)(from * highs.Length), 0, highs.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling(to * highs.Length) - 1, first, highs.Length - 1);
        var (low, high) = (lows[first], highs[first]);
        for (var i = first + 1; i <= last; i++)
        {
            if (lows[i] < low)
                low = lows[i];
            if (highs[i] > high)
                high = highs[i];
        }

        return (low, high);
    }
}
