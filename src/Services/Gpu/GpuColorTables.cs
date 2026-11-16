using System.Globalization;
using System.IO;

namespace HandPegApp.Services.Gpu;

/// <summary>
/// Colour tables for GPU effects, built as FFmpeg builds them from the same card: .cube files as 3D LUTs
/// (<see cref="GpuResourceKind.Lut3D"/>, RGBA32F N^3, red fastest, as lut3d reads them) and the Curves card as one 256-entry table
/// (<see cref="GpuResourceKind.Curve1D"/>: FFmpeg curves' natural cubic spline per channel, preset points where the card has none, master
/// applied after each channel). Thread-safe; no shared state.
/// </summary>
public static class GpuColorTables
{
    /// <summary>Largest cube size read (matches CompositorClient.ResourceBytes).</summary>
    public const int LargestCube = 256;

    /// <summary>A .cube file's 3D table as RGBA32F bytes plus its size; null when not a valid 3D cube (1D table, no size, too few entries).</summary>
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
                    // TITLE, DOMAIN_MIN/DOMAIN_MAX: the domain is assumed 0..1, as almost every cube uses.
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

                // Red varies fastest, matching file order and the 3D texture layout.
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
            AppLog.Write($"A .cube LUT for the GPU engine could not be read ({Path.GetFileName(path)})", ex);
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

    // FFmpeg curves presets (vf_curves.c): red, green, blue, master; null where a preset leaves a channel unchanged.
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
    /// The Curves card (preset, master points) as a 256x1 RGBA32F table: R, G, B = channel curve then master (sampled by PSToneCurve),
    /// A = master alone. Null when it is the identity.
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
            // Master applies after each channel (curves: graph[i][j] = graph[master][graph[i][j]]).
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
    /// One channel's 8-bit curve as FFmpeg's curves interpolates it (natural cubic spline through the points, flat outside them);
    /// identity without points, constant with one.
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

        // Tridiagonal system with natural ends (zero second derivative), solved as vf_curves.c does.
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

    // ----- Color Grade curves -----

    /// <summary>
    /// The Color Grade card's four curves (Master, Red, Green, Blue as "x/y x/y..." in 0..1) as one 256x1 RGBA32F table in the Curves card's
    /// layout: R, G, B = channel curve then master (sampled by PSColorStudio), A = master alone. Each curve is a monotone cubic Hermite
    /// (Fritsch-Carlson: no overshoot, so rising points give a rising curve), the same one Color Studio draws (<see cref="CurveAt"/>). Null
    /// when every curve is the identity. Takes microseconds; baked when the plan is built.
    /// </summary>
    public static byte[]? ColorCurves(string? master, string? red, string? green, string? blue)
    {
        if (CurveIsIdentity(master) && CurveIsIdentity(red) && CurveIsIdentity(green) && CurveIsIdentity(blue))
            return null;

        var (m, r, g, b) = (Hermite.Of(master), Hermite.Of(red), Hermite.Of(green), Hermite.Of(blue));
        var table = new float[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0;
            table[i * 4] = (float)m.At(r.At(x));
            table[i * 4 + 1] = (float)m.At(g.At(x));
            table[i * 4 + 2] = (float)m.At(b.At(x));
            table[i * 4 + 3] = (float)m.At(x);
        }

        var bytes = new byte[table.Length * sizeof(float)];
        Buffer.BlockCopy(table, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>True when a curve is the identity: no points, or all on the diagonal (ColorGradeMath.CurveIsIdentity).</summary>
    public static bool CurveIsIdentity(string? points) => Models.Effects.ColorGradeMath.CurveIsIdentity(points);

    /// <summary>
    /// Curve points in ascending order with endpoints: (0, 0) prepended and (1, 1) appended when absent, so a single point bends the
    /// diagonal. Empty for the identity.
    /// </summary>
    public static List<(double X, double Y)> CurvePoints(string? text)
    {
        var points = Points(text);
        if (points.Count == 0)
            return points;
        if (points[0].X > 0)
            points.Insert(0, (0, 0));
        if (points[^1].X < 1)
            points.Add((1, 1));
        return points;
    }

    /// <summary>Curve value at <paramref name="x"/> (0..1): the monotone Hermite through <see cref="CurvePoints"/>.</summary>
    public static double CurveAt(string? text, double x) => Hermite.Of(text).At(x);

    /// <summary>Curve sampled at <paramref name="count"/> even steps over 0..1 (what Color Studio draws: the baked table's curve).</summary>
    public static double[] CurveSamples(string? text, int count)
    {
        var curve = Hermite.Of(text);
        var samples = new double[Math.Max(count, 2)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = curve.At(i / (samples.Length - 1.0));
        return samples;
    }

    /// <summary>Formats points as curve text ("x/y x/y...", three decimals, in the given order).</summary>
    public static string FormatCurve(IEnumerable<(double X, double Y)> points) =>
        string.Join(' ', points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(p.X, 3):0.###}/{Math.Round(p.Y, 3):0.###}")));

    /// <summary>Monotone cubic Hermite through a curve's points (Fritsch-Carlson tangents), flat outside them; identity without points.</summary>
    private sealed class Hermite
    {
        private static readonly Hermite Identity = new([0, 1], [0, 1], [1, 1]);

        private readonly double[] _x, _y, _m;

        private Hermite(double[] x, double[] y, double[] m) => (_x, _y, _m) = (x, y, m);

        public static Hermite Of(string? text)
        {
            var points = CurvePoints(text);
            if (points.Count < 2)
                return Identity;

            var n = points.Count;
            var (x, y, m) = (new double[n], new double[n], new double[n]);
            for (var i = 0; i < n; i++)
                (x[i], y[i]) = points[i];

            // Secants, then interior tangents = mean of adjacent secants (0 at local extrema); end tangents = their own secant.
            var d = new double[n - 1];
            for (var i = 0; i < n - 1; i++)
                d[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
            (m[0], m[n - 1]) = (d[0], d[n - 2]);
            for (var i = 1; i < n - 1; i++)
                m[i] = d[i - 1] * d[i] > 0 ? (d[i - 1] + d[i]) / 2 : 0;

            // Fritsch-Carlson: a flat secant zeroes both tangents; tangents too steep for their secant are scaled onto the radius-3 circle,
            // keeping every segment monotone.
            for (var i = 0; i < n - 1; i++)
            {
                if (d[i] == 0)
                {
                    (m[i], m[i + 1]) = (0, 0);
                    continue;
                }

                var (a, b) = (m[i] / d[i], m[i + 1] / d[i]);
                var s = a * a + b * b;
                if (s > 9)
                {
                    var tau = 3 / Math.Sqrt(s);
                    (m[i], m[i + 1]) = (tau * a * d[i], tau * b * d[i]);
                }
            }

            return new Hermite(x, y, m);
        }

        public double At(double t)
        {
            if (!(t > _x[0]))
                return _y[0];
            if (t >= _x[^1])
                return _y[^1];

            var k = 0;
            while (k < _x.Length - 2 && t > _x[k + 1])
                k++;
            var h = _x[k + 1] - _x[k];
            var u = (t - _x[k]) / h;
            var (u2, u3) = (u * u, u * u * u);
            var value = (2 * u3 - 3 * u2 + 1) * _y[k] + (u3 - 2 * u2 + u) * h * _m[k] + (-2 * u3 + 3 * u2) * _y[k + 1] + (u3 - u2) * h * _m[k + 1];
            return Math.Max(0, Math.Min(value, 1));
        }
    }

    // ----- Color Grade last lane -----

    /// <summary>
    /// Encodes the Color Grade card's last lane: LUT mix (0..1), +2 when the layer's curve table belongs to this card, +4 when the layer's LUT
    /// does (one LUT and one curve per layer; another card may own them). Never negative (no PulseMarker).
    /// </summary>
    public static float StudioTablesLane(double mix, bool curves, bool lut) =>
        (float)((double.IsFinite(mix) ? Math.Max(0, Math.Min(mix, 1)) : 0) + (curves ? 2 : 0) + (lut ? 4 : 0));

    /// <summary>Decodes <see cref="StudioTablesLane"/>: LUT bound, curves bound, and LUT mix.</summary>
    public static (bool Lut, bool Curves, float Mix) StudioTablesOf(float lane)
    {
        var lut = lane >= 3.5f;
        if (lut)
            lane -= 4;
        var curves = lane >= 1.5f;
        if (curves)
            lane -= 2;
        return (lut, curves, float.IsFinite(lane) ? Math.Max(0, Math.Min(lane, 1)) : 0);
    }

    /// <summary>Parses "x/y x/y ..." in 0..1 in the given order (must ascend; out-of-order points are skipped).</summary>
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
