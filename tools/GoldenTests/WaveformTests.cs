using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        yield return Case("deadair-from-peaks",
            "# Remove Dead Air's silences from the peaks in memory (5.1-CleanupStudioAndFfmpegPurge), and the peaks read natively, each track in one pass, from a WAV.", DeadAir);
        yield return Case("waveform-combine",
            "# WaveformData.Combine (5.3-Stage2): several sounds as one, each pair the most any reaches at its gain and shift, against a brute force pair by pair.", Combine);
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

    private static void DeadAir(StringBuilder text, List<string> problems)
    {
        // 80 pairs a second: loud, 2 s under the level, loud, 0.5 s under it (too short at 1 s), loud, then under it to the end.
        const double rate = Waveforms.PeaksPerSecond;
        var count = (int)(20 * rate);
        var (lows, highs) = (new float[count], new float[count]);
        for (var i = 0; i < count; i++)
        {
            var at = i / rate;
            var quiet = at is >= 3 and < 5 or >= 8 and < 8.5 or >= 15;
            var level = quiet ? 0.005f : 0.4f;
            (lows[i], highs[i]) = (-level, level);
        }

        var data = new WaveformData(lows, highs, Brushes.Gray);
        var found = data.Silences(-35, 1, 20.25);
        foreach (var (start, end) in found)
            Line(text, $"silence {start:0.000} to {end:0.000}");
        Expect(found.Count == 2 && Math.Abs(found[0].Start - 3) < 0.02 && Math.Abs(found[0].End - 5) < 0.02 && Math.Abs(found[1].Start - 15) < 0.02 && found[1].End == 20.25,
            "the 2 s and the closing silence are found, the 0.5 s is not, and the last runs to the file's end");
        Expect(data.Silences(-60, 1, 20.25).Count == 0, "at -60 dB nothing here is quiet enough");

        var clock = Stopwatch.StartNew();
        var hour = new WaveformData(new float[288_000], new float[288_000], Brushes.Gray);
        _ = hour.Silences(-35, 1, 3600);
        Console.WriteLine(FormattableString.Invariant($"  (an hour's scan took {clock.Elapsed.TotalMilliseconds:0.00} ms)"));

        // The peaks themselves, by Media Foundation: a WAV of 6 s at 44.1 kHz, a tone with a silent 2 s in the middle.
        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-peaks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var wav = Path.Combine(folder, "tone.wav");
            WriteWav(wav, 44100, 6, t => t is >= 2 and < 4 ? 0 : 0.5 * Math.Sin(2 * Math.PI * 440 * t));
            var peaks = Task.Run(() => Waveforms.PeaksOfAsync(wav, 0, CancellationToken.None)).GetAwaiter().GetResult();
            Expect(peaks is not null, "the WAV's peaks are read");
            if (peaks is not null)
            {
                var silences = peaks.Silences(-35, 1, 6);
                foreach (var (start, end) in silences)
                    Line(text, $"read from a WAV: {peaks.Seconds:0.00} s of peaks, silence {start:0.00} to {end:0.00}");
                Expect(Math.Abs(peaks.Seconds - 6) < 0.05 && silences.Count == 1 && Math.Abs(silences[0].Start - 2) < 0.03 && Math.Abs(silences[0].End - 4) < 0.03,
                    "the WAV's silence lies where it was written");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        void Expect(bool holds, string what)
        {
            Line(text, $"{(holds ? "ok" : "NOT")}: {what}");
            if (!holds)
                problems.Add(what);
        }
    }

    private static void WriteWav(string path, int rate, double seconds, Func<double, double> value)
    {
        var frames = (int)(rate * seconds);
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + frames * 2);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)1);
        file.Write(rate);
        file.Write(rate * 2);
        file.Write((short)2);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(frames * 2);
        for (var i = 0; i < frames; i++)
            file.Write((short)(value(i / (double)rate) * 32767));
    }

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

    private static void Combine(StringBuilder text, List<string> problems)
    {
        // Three sounds of uneven lengths, so every part has a ragged tail past its whole vectors (the last shorter than any vector), at
        // gains 1, 0.5 and 2, shifted 3 pairs on, 5 back and not at all. The first two swell and fade with rare loud spikes (2.5, past
        // what gain 2 may reach) and a few highs under 0; the third is written by hand, its loud pairs past MostCombined at gain 2.
        const double rate = Waveforms.PeaksPerSecond;
        var random = new Random(5302);
        WaveformData Sound(int count, double pairsPerSecond)
        {
            var (lows, highs) = (new float[count], new float[count]);
            for (var i = 0; i < count; i++)
            {
                var swell = Math.Pow(Math.Abs(Math.Sin(i / 700.0)), 2);
                highs[i] = (float)(swell * (random.NextDouble() - 0.05));
                lows[i] = (float)(-swell * random.NextDouble());
                if (random.Next(400) == 0)
                    (lows[i], highs[i]) = (-2.5f, 2.5f);
            }

            return new WaveformData(lows, highs, Brushes.White, pairsPerSecond);
        }

        var hand = new WaveformData([-0.2f, -2.5f, -0.1f, -0.3f, -1f, -2.2f, 0.1f], [0.2f, 2.5f, 0.4f, -0.1f, 1f, 0.3f, 3f], Brushes.White);
        var parts = new (WaveformData Data, double GainFactor, double OffsetShift)[] { (Sound(36_001, rate), 1, 3 / rate), (Sound(20_003, rate), 0.5, -5 / rate), (hand, 2, 0) };
        var combined = WaveformData.Combine(parts);
        var (wantLows, wantHighs) = BruteCombine(parts);
        Expect(problems, combined is not null, "three sounds make a combined sound");
        if (combined is null)
            return;

        Line(text, $"parts of {parts[0].Data.Count}, {parts[1].Data.Count} and {parts[2].Data.Count} pairs at gains 1, 0.5 and 2, shifted +3, -5 and 0 pairs: {combined.Count} pairs at {combined.PairsPerSecond} a second; levels {combined.CountAt(1)}, {combined.CountAt(2)}, {combined.CountAt(3)}");
        Expect(problems, combined.Count == wantHighs.Length && combined.Count == 36_001 - 3, "it is as long as its longest reach (the first part less its 3 pairs)");

        // Every pair against the brute force, as the vectors and the tails made it.
        var (lows0, highs0) = (combined.LowsAt(0).ToArray(), combined.HighsAt(0).ToArray());
        var wrong = Enumerable.Range(0, Math.Min(lows0.Length, wantLows.Length)).FirstOrDefault(i => lows0[i] != wantLows[i] || highs0[i] != wantHighs[i], -1);
        Expect(problems, wrong < 0, wrong < 0 ? "every pair" : $"every pair: pair {wrong} is ({lows0[wrong]}, {highs0[wrong]}), not ({wantLows[wrong]}, {wantHighs[wrong]})");
        Line(text, $"every pair is the brute force's: the highest gain x high and the lowest gain x low of the parts playing there, silence (0) where none does");
        Expect(problems, highs0[1] == WaveformData.MostCombined && lows0[1] == -WaveformData.MostCombined && highs0[6] == WaveformData.MostCombined,
            "the hand-written part's loud pairs at gain 2 are held at MostCombined (4)");
        Line(text, $"a pair past {WaveformData.MostCombined} at its gain is held there (2.5 x 2 and 3 x 2 are {highs0[1]} and {highs0[6]})");
        Expect(problems, highs0.Min() >= 0 && lows0.Max() <= 0, "a high under 0 never pulls the silence under it down, nor a low over 0 up");
        Line(text, $"the highs never go under silence and the lows never over it (a part's high under 0, the hand-written part's low over 0)");

        // Its folds and ranges, against the brute force of the pairs it holds.
        for (var level = 1; level < WaveformData.MipLevels; level++)
        {
            var fold = 1 << (3 * level);
            var (levelLows, levelHighs) = (combined.LowsAt(level).ToArray(), combined.HighsAt(level).ToArray());
            for (var j = 0; j < levelLows.Length; j++)
            {
                if (!Same((levelLows[j], levelHighs[j]), Brute(wantLows, wantHighs, j * fold, (j + 1) * fold)))
                {
                    problems.Add($"combined level {level} pair {j} is not the lowest and the highest of what it folds");
                    break;
                }
            }
        }

        for (var k = 0; k < 500; k++)
        {
            var first = random.Next(combined.Count);
            var end = Math.Min(first + 1 + (int)Math.Exp(random.NextDouble() * Math.Log(combined.Count)), combined.Count);
            if (!Same(combined.RangeOf(first, end), Brute(wantLows, wantHighs, first, end)))
            {
                problems.Add($"combined RangeOf({first}, {end}) is {combined.RangeOf(first, end)}, not {Brute(wantLows, wantHighs, first, end)}");
                break;
            }
        }

        Line(text, $"its folds are made as any sound's: each pair of levels 1 to 3, and RangeOf over 500 stretches, the brute force's");

        // One part as it is is that part's own peaks (its tiles drawn again); a part with no gain is left out, so is one at another rate.
        Expect(problems, ReferenceEquals(WaveformData.Combine([(hand, 1, 0)]), hand), "one part at gain 1 and no shift is that part itself");
        Expect(problems, ReferenceEquals(WaveformData.Combine([(hand, 1, 0), (parts[0].Data, 0, 0)]), hand), "a part with no gain is left out (the one left is itself)");
        Expect(problems, WaveformData.Combine([(hand, 1, 0.4 / rate)]) is { } nearly && ReferenceEquals(nearly, hand), "a shift under half a pair is no shift");
        Expect(problems, WaveformData.Combine([(hand, 1, 1 / rate)]) is { } shifted && !ReferenceEquals(shifted, hand) && shifted.Count == 6 && shifted.HighsAt(0)[0] == 2.5f,
            "a part shifted one pair on is a sound of its own, read from its second pair");
        Expect(problems, WaveformData.Combine([]) is null && WaveformData.Combine([(hand, 0, 0)]) is null, "no parts, or none with a gain, make nothing (null)");
        Line(text, $"one part at gain 1 and no shift is itself; a part with no gain is left out; no part left is null");

        var other = Sound(5000, 20);
        var withOther = WaveformData.Combine([.. parts, (other, 1, 0)]);
        var same = withOther is not null && withOther.Count == combined.Count
            && withOther.LowsAt(0).SequenceEqual(combined.LowsAt(0)) && withOther.HighsAt(0).SequenceEqual(combined.HighsAt(0));
        Expect(problems, same, "a part at another rate (20 a second) is left out: the three parts' sound, pair for pair");
        Line(text, $"a part at another rate than the first's is left out: the sound is the other three's, pair for pair");
        Expect(problems, WaveformData.Combine([(other, 1, 0), (hand, 1, 0)]) is { } byRate && ReferenceEquals(byRate, other), "the first part kept sets the rate");

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  info  waveform-combine: {System.Numerics.Vector<float>.Count} floats a vector, accelerated {System.Numerics.Vector.IsHardwareAccelerated}"));
    }

    /// <summary>Combine's sound worked out pair by pair: for each pair, the most each part playing there reaches at its gain (held within MostCombined), from silence.</summary>
    private static (float[] Lows, float[] Highs) BruteCombine(IReadOnlyList<(WaveformData Data, double GainFactor, double OffsetShift)> parts)
    {
        var shifts = parts.Select(p => (int)Math.Round(p.OffsetShift * p.Data.PairsPerSecond)).ToArray();
        var (partLows, partHighs) = (parts.Select(p => p.Data.LowsAt(0).ToArray()).ToArray(), parts.Select(p => p.Data.HighsAt(0).ToArray()).ToArray());
        var count = Enumerable.Range(0, parts.Count).Max(k => partHighs[k].Length - shifts[k]);
        var (lows, highs) = (new float[count], new float[count]);
        for (var i = 0; i < count; i++)
        {
            var (low, high) = (0f, 0f);
            for (var k = 0; k < parts.Count; k++)
            {
                var j = i + shifts[k];
                if (j < 0 || j >= partHighs[k].Length)
                    continue;

                var gain = (float)parts[k].GainFactor;
                low = Math.Min(low, Math.Clamp(partLows[k][j] * gain, -WaveformData.MostCombined, WaveformData.MostCombined));
                high = Math.Max(high, Math.Clamp(partHighs[k][j] * gain, -WaveformData.MostCombined, WaveformData.MostCombined));
            }

            (lows[i], highs[i]) = (low, high);
        }

        return (lows, highs);
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
