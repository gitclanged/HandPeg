using System.Windows;

namespace HandPegApp.Services;

/// <summary>
/// Outlines of a full-frame bitmap mask's visible regions, so a Cutout masked by an image opens in the Cutout Studio as editable
/// shapes: each island's outer boundary (Moore-neighbour trace on <see cref="MaskStore.ReadGrey"/>'s coverage map, holes not
/// traced), simplified by Douglas-Peucker, in frame fractions.
/// </summary>
public static class MaskTrace
{
    // Clockwise neighbours in screen space (y down), starting west: W, NW, N, NE, E, SE, S, SW.
    private static readonly int[] StepX = [-1, -1, 0, 1, 1, 1, 0, -1], StepY = [0, -1, -1, -1, 0, 1, 1, 1];

    /// <summary>
    /// Outlines of every island (8-connected visible pixels, specks ignored as <see cref="MaskStore.FindIslands"/> does), in raster
    /// order of their first pixel; null when the file cannot be read.
    /// </summary>
    /// <param name="tolerance">Simplification tolerance in coverage-map pixels (the map is at most 960 wide).</param>
    public static List<List<Point>>? Outlines(string path, double tolerance = 1.0)
    {
        if (MaskStore.ReadGrey(path) is not var (pixels, width, height))
            return null;

        var island = new int[pixels.Length];
        var stack = new Stack<int>();
        var smallest = Math.Max(6, width * height / 50_000);
        var outlines = new List<List<Point>>();
        var label = 0;
        for (var start = 0; start < pixels.Length; start++)
        {
            if (island[start] != 0 || pixels[start] < 128)
                continue;

            // Label the whole island first, so the trace follows only its pixels and the scan skips them afterwards.
            label++;
            var count = 0;
            island[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                count++;
                for (var d = 0; d < 8; d++)
                {
                    var (x, y) = (at % width + StepX[d], at / width + StepY[d]);
                    if (x >= 0 && y >= 0 && x < width && y < height && island[y * width + x] == 0 && pixels[y * width + x] >= 128)
                    {
                        island[y * width + x] = label;
                        stack.Push(y * width + x);
                    }
                }
            }

            if (count < smallest)
                continue;

            var boundary = Trace(island, width, height, start, label, count);
            var simple = Simplify(boundary, tolerance);
            if (simple.Count < 3)
                continue;

            // Pixel centres to frame fractions.
            outlines.Add(simple.Select(p => new Point((p.X + 0.5) / width, (p.Y + 0.5) / height)).ToList());
        }

        return outlines;
    }

    /// <summary>
    /// Moore-neighbour boundary of the island holding <paramref name="start"/> (its first pixel in raster order, so its west
    /// neighbour is outside it), clockwise, until the start would be left by its first move again (Jacob's stopping rule).
    /// </summary>
    private static List<Point> Trace(int[] island, int width, int height, int start, int label, int count)
    {
        var (sx, sy) = (start % width, start / width);
        var (cx, cy, back, firstMove) = (sx, sy, 0, -1);
        var boundary = new List<Point> { new(sx, sy) };
        for (var steps = 0; steps < 4 * count + 8; steps++)
        {
            var found = -1;
            for (var k = 1; k <= 8; k++)
            {
                var d = (back + k) % 8;
                var (nx, ny) = (cx + StepX[d], cy + StepY[d]);
                if (nx >= 0 && ny >= 0 && nx < width && ny < height && island[ny * width + nx] == label)
                {
                    found = d;
                    break;
                }
            }

            if (found < 0)
                break;
            if (cx == sx && cy == sy)
            {
                if (firstMove < 0)
                    firstMove = found;
                else if (found == firstMove)
                    break;
            }

            // The neighbour checked just before the hit is outside: it is the backtrack seen from the new pixel.
            var before = (found + 7) % 8;
            var (px, py) = (cx + StepX[found], cy + StepY[found]);
            back = Direction(cx + StepX[before] - px, cy + StepY[before] - py);
            (cx, cy) = (px, py);
            if (cx != sx || cy != sy)
                boundary.Add(new Point(cx, cy));
        }

        return boundary;
    }

    private static int Direction(int dx, int dy)
    {
        for (var d = 0; d < 8; d++)
        {
            if (StepX[d] == dx && StepY[d] == dy)
                return d;
        }

        return 0;
    }

    /// <summary>
    /// Douglas-Peucker on a closed outline: split at the point farthest from the first, then keep every point farther than
    /// <paramref name="tolerance"/> from its range's chord (ranges on an explicit stack: no recursion depth on long boundaries).
    /// </summary>
    private static List<Point> Simplify(List<Point> ring, double tolerance)
    {
        if (ring.Count <= 4)
            return ring;

        var far = 0;
        var most = -1.0;
        for (var i = 1; i < ring.Count; i++)
        {
            var distance = (ring[i] - ring[0]).LengthSquared;
            if (distance > most)
                (most, far) = (distance, i);
        }

        var keep = new bool[ring.Count];
        (keep[0], keep[far]) = (true, true);
        var ranges = new Stack<(int From, int To)>([(0, far), (far, ring.Count)]);
        while (ranges.Count > 0)
        {
            // A range's end index Count stands for ring[0], closing the outline.
            var (from, to) = ranges.Pop();
            var (a, chord) = (ring[from], ring[to % ring.Count] - ring[from]);
            var length = chord.Length;
            var (index, worst) = (-1, tolerance);
            for (var i = from + 1; i < to; i++)
            {
                var offset = ring[i] - a;
                var distance = length > 1e-9 ? Math.Abs(chord.X * offset.Y - chord.Y * offset.X) / length : offset.Length;
                if (distance > worst)
                    (index, worst) = (i, distance);
            }

            if (index < 0)
                continue;

            keep[index] = true;
            ranges.Push((from, index));
            ranges.Push((index, to));
        }

        return ring.Where((_, i) => keep[i]).ToList();
    }
}
