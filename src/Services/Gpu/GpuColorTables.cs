using System.Globalization;
using System.IO;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// The colour tables the GPU engine's effects read, made as FFmpeg makes them from the same card: a .cube file as a 3D LUT
/// (<see cref="GpuResourceKind.Lut3D"/>, RGBA32F N^3, red fastest, as lut3d reads it) and the Curves card as one 256-entry
/// table (<see cref="GpuResourceKind.Curve1D"/>: FFmpeg's curves, a natural cubic spline through each channel's points, its
/// preset's where the card gives none, and the master curve applied after each channel's, as curves composes them).
/// Thread-safe, nothing shared.
/// </summary>
public static class GpuColorTables
{
    /// <summary>The largest cube read (as CompositorClient.ResourceBytes allows).</summary>
    public const int LargestCube = 256;

    /// <summary>A .cube file's 3D table as RGBA32F bytes and its size; null when it is not one (a 1D table, no size, too few entries).</summary>
    public static (byte[] Pixels, int Size)? ReadCube(string path)
    {
        try
        {
            var size = 0;
            float[]? table = null;
            var count = 0;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(line[11..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 2 || size > LargestCube)
                        return null;
                    table = new float[size * size * size * 4];
                    continue;
                }

                if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Write($"A 1D .cube LUT is not read by the GPU engine ({Path.GetFileName(path)}).");
                    return null;
                }

                if (char.IsLetter(line[0]))
                {
                    // TITLE, DOMAIN_MIN / DOMAIN_MAX: the domain is taken as 0..1, as nearly every cube has it.
                    if (line.StartsWith("DOMAIN_", StringComparison.OrdinalIgnoreCase) && !IsUnitDomain(line))
                        AppLog.Write($"A .cube LUT's domain other than 0..1 is read as 0..1 by the GPU engine ({Path.GetFileName(path)}).");
                    continue;
                }

                if (table is null || count >= size * size * size)
                    continue;

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3
                    || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                {
                    continue;
                }

                // Red changes fastest, as the file lists it and the 3D texture's rows lie.
                (table[count * 4], table[count * 4 + 1], table[count * 4 + 2], table[count * 4 + 3]) = (r, g, b, 1);
                count++;
            }

            if (table is null || count < size * size * size)
                return null;

            var bytes = new byte[table.Length * sizeof(float)];
            Buffer.BlockCopy(table, 0, bytes, 0, bytes.Length);
            return (bytes, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Write($"A .cube LUT for the GPU engine could not be read ({Path.GetFileName(path)}): {ex.GetType().Name}");
            return null;
        }
    }

    private static bool IsUnitDomain(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var unit = line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase) ? 0f : 1f;
        for (var i = 1; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || Math.Abs(v - unit) > 1e-6)
                return false;
        }

        return true;
    }

    // ----- Curves -----

    // FFmpeg's curves presets (vf_curves.c): red, green, blue, master; null where a preset leaves a channel alone.
    private static readonly Dictionary<string, (string? R, string? G, string? B, string? Master)> Presets = new(StringComparer.Ordinal)
    {
        ["color_negative"] = ("0.129/1 0.466/0.498 0.725/0", "0.109/1 0.301/0.498 0.517/0", "0.098/1 0.235/0.498 0.423/0", null),
        ["cross_process"] = ("0/0 0.25/0.156 0.501/0.501 0.686/0.745 1/1", "0/0 0.25/0.188 0.38/0.501 0.745/0.815 1/0.815", "0/0 0.231/0.094 0.709/0.874 1/1", null),
        ["darker"] = (null, null, null, "0/0 0.5/0.4 1/1"),
        ["increase_contrast"] = (null, null, null, "0/0 0.149/0.066 0.831/0.905 0.905/0.98 1/1"),
        ["lighter"] = (null, null, null, "0/0 0.4/0.5 1/1"),
        ["linear_contrast"] = (null, null, null, "0/0 0.305/0.286 0.694/0.713 1/1"),
        ["medium_contrast"] = (null, null, null, "0/0 0.286/0.219 0.639/0.643 1/1"),
        ["negative"] = (null, null, null, "0/1 1/0"),
        ["strong_contrast"] = (null, null, null, "0/0 0.301/0.196 0.592/0.6 0.686/0.737 1/1"),
        ["vintage"] = ("0/0.11 0.42/0.51 1/0.95", "0/0 0.50/0.48 1/1", "0/0.22 0.49/0.44 1/0.8", null),
    };

    /// <summary>
    /// The Curves card (its preset, its master points) as a 256 x 1 RGBA32F table: R, G and B each channel's curve with the
    /// master applied after it (what PSToneCurve looks up), A the master alone. Null when it changes nothing.
    /// </summary>
    public static byte[]? ToneCurve(string? preset, string? master)
    {
        var chosen = preset is { Length: > 0 } && Presets.TryGetValue(preset, out var p) ? p : default;
        var masterPoints = master is { Length: > 0 } && master.Trim().Length > 0 ? master : chosen.Master;
        if (chosen.R is null && chosen.G is null && chosen.B is null && masterPoints is null)
            return null;

        var (r, g, b, m) = (Curve(chosen.R), Curve(chosen.G), Curve(chosen.B), Curve(masterPoints));
        var table = new float[256 * 4];
        for (var x = 0; x < 256; x++)
        {
            // The master is a second pass over each channel's result (curves: graph[i][j] = graph[master][graph[i][j]]).
            table[x * 4] = m[r[x]] / 255f;
            table[x * 4 + 1] = m[g[x]] / 255f;
            table[x * 4 + 2] = m[b[x]] / 255f;
            table[x * 4 + 3] = m[x] / 255f;
        }

        var bytes = new byte[table.Length * sizeof(float)];
        Buffer.BlockCopy(table, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// One channel's curve over 8-bit values, as FFmpeg's curves interpolates it (interpolate(), natural): a natural cubic
    /// spline through the points, flat before the first and after the last; the identity without points, one value with one.
    /// </summary>
    private static int[] Curve(string? text)
    {
        const int scale = 255;
        var y = new int[256];
        var points = Points(text);
        if (points.Count == 0)
        {
            for (var i = 0; i < 256; i++)
                y[i] = i;
            return y;
        }

        static int Clip(double v) => Math.Clamp((int)v, 0, scale);
        if (points.Count == 1)
        {
            Array.Fill(y, Clip(points[0].Y * scale));
            return y;
        }

        var n = points.Count;
        var h = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
            h[i] = points[i + 1].X - points[i].X;

        var r = new double[n];
        for (var i = 1; i < n - 1; i++)
            r[i] = 6 * ((points[i + 1].Y - points[i].Y) / h[i] - (points[i].Y - points[i - 1].Y) / h[i - 1]);

        // The tridiagonal system: natural ends (the second derivative 0 there), solved as vf_curves.c solves it.
        var (below, main, above) = (new double[n], new double[n], new double[n]);
        (main[0], main[n - 1]) = (1, 1);
        for (var i = 1; i < n - 1; i++)
            (below[i], main[i], above[i]) = (h[i - 1], 2 * (h[i - 1] + h[i]), h[i]);
        for (var i = 1; i < n; i++)
        {
            var den = main[i] - below[i] * above[i - 1];
            var k = den != 0 ? 1 / den : 1;
            above[i] *= k;
            r[i] = (r[i] - below[i] * r[i - 1]) * k;
        }

        for (var i = n - 2; i >= 0; i--)
            r[i] -= above[i] * r[i + 1];

        for (var x = 0; x < (int)(points[0].X * scale); x++)
            y[x] = Clip(points[0].Y * scale);
        for (var i = 0; i < n - 1; i++)
        {
            var (yc, yn) = (points[i].Y, points[i + 1].Y);
            var (a, c) = (yc, r[i] / 2);
            var b = (yn - yc) / h[i] - h[i] * r[i] / 2 - h[i] * (r[i + 1] - r[i]) / 6;
            var d = (r[i + 1] - r[i]) / (6 * h[i]);
            var (start, end) = ((int)(points[i].X * scale), (int)(points[i + 1].X * scale));
            for (var x = start; x <= end && x < 256; x++)
            {
                var xx = (x - start) * 1.0 / scale;
                y[x] = Clip((a + b * xx + c * xx * xx + d * xx * xx * xx) * scale);
            }
        }

        for (var x = (int)(points[^1].X * scale); x < 256; x++)
            y[x] = Clip(points[^1].Y * scale);
        return y;
    }

    /// <summary>"x/y x/y ..." from 0 to 1, in the order given (curves wants them ascending; one out of order is left out).</summary>
    private static List<(double X, double Y)> Points(string? text)
    {
        var points = new List<(double X, double Y)>();
        foreach (var pair in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = pair.IndexOf('/');
            if (slash <= 0
                || !double.TryParse(pair[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(pair[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                continue;
            }

            (x, v) = (Math.Clamp(x, 0, 1), Math.Clamp(v, 0, 1));
            if (points.Count > 0 && x <= points[^1].X)
                continue;
            points.Add((x, v));
        }

        return points;
    }
}
