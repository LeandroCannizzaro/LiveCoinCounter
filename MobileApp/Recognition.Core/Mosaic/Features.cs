using System.Numerics;

namespace LiveCoinCounter.Mosaic;

/// <summary>
/// Punti caratteristici di un'immagine in grigio con descrittori binari da 256 bit (4 ulong per punto, stesso formato di ORB).
/// </summary>
public sealed class FeatureSet
{
    public FeatureSet(float[] x, float[] y, ulong[] descriptors)
    {
        if (descriptors.Length != x.Length * 4 || y.Length != x.Length) throw new ArgumentException("Dimensioni incoerenti.");
        X = x;
        Y = y;
        Descriptors = descriptors;
    }

    public static FeatureSet Empty { get; } = new(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<ulong>());

    public float[] X { get; }
    public float[] Y { get; }
    public ulong[] Descriptors { get; }
    public int Count => X.Length;
}

/// <summary>Estrattore di punti caratteristici: OpenCV (ORB) sul telefono, <see cref="OrbLite"/> in codice puro altrove.</summary>
public interface IFeatureExtractor
{
    string Id { get; }
    FeatureSet Extract(GrayImage image, int maxFeatures);
}

public static class FeatureMatcher
{
    /// <summary>
    /// Accoppiamento a forza bruta con distanza di Hamming e test del rapporto di Lowe: si tiene una corrispondenza solo se
    /// è nettamente migliore della seconda scelta (le monete si somigliano tutte: senza questo test i falsi abbondano).
    /// </summary>
    /// <summary>
    /// Accoppiamento guidato per fotogrammi vicini nel tempo: ogni punto cerca solo tra quelli entro <paramref name="radius"/>
    /// pixel dalla stessa posizione (il telefono si è mosso poco). Su un tavolo con graffi e grana ripetitivi il test del
    /// rapporto su tutta l'immagine scarta le corrispondenze valide, perché ce ne sono molte quasi uguali; nella finestra
    /// i candidati sono pochi e si può essere meno severi.
    /// </summary>
    public static List<PointMatch> MatchNear(FeatureSet query, FeatureSet train, float radius, int maxDistance = 80, float ratio = 0.9f)
    {
        var result = new List<PointMatch>();
        if (query.Count == 0 || train.Count < 2) return result;

        var q = query.Descriptors;
        var t = train.Descriptors;
        for (var i = 0; i < query.Count; i++)
        {
            int best = int.MaxValue, second = int.MaxValue, bestIndex = -1;
            ulong a0 = q[i * 4], a1 = q[i * 4 + 1], a2 = q[i * 4 + 2], a3 = q[i * 4 + 3];
            float qx = query.X[i], qy = query.Y[i];
            for (var j = 0; j < train.Count; j++)
            {
                if (Math.Abs(train.X[j] - qx) > radius || Math.Abs(train.Y[j] - qy) > radius) continue;
                var d = BitOperations.PopCount(a0 ^ t[j * 4]) + BitOperations.PopCount(a1 ^ t[j * 4 + 1])
                      + BitOperations.PopCount(a2 ^ t[j * 4 + 2]) + BitOperations.PopCount(a3 ^ t[j * 4 + 3]);
                if (d < best) { second = best; best = d; bestIndex = j; }
                else if (d < second) second = d;
            }

            if (bestIndex >= 0 && best <= maxDistance && (second == int.MaxValue || best < ratio * second))
                result.Add(new PointMatch(query.X[i], query.Y[i], train.X[bestIndex], train.Y[bestIndex], best));
        }
        return result;
    }

    public static List<PointMatch> Match(FeatureSet query, FeatureSet train, int maxDistance = 70, float ratio = 0.8f)
    {
        var result = new List<PointMatch>();
        if (query.Count == 0 || train.Count < 2) return result;

        var q = query.Descriptors;
        var t = train.Descriptors;
        for (var i = 0; i < query.Count; i++)
        {
            int best = int.MaxValue, second = int.MaxValue, bestIndex = -1;
            ulong a0 = q[i * 4], a1 = q[i * 4 + 1], a2 = q[i * 4 + 2], a3 = q[i * 4 + 3];
            for (var j = 0; j < train.Count; j++)
            {
                var d = BitOperations.PopCount(a0 ^ t[j * 4]) + BitOperations.PopCount(a1 ^ t[j * 4 + 1])
                      + BitOperations.PopCount(a2 ^ t[j * 4 + 2]) + BitOperations.PopCount(a3 ^ t[j * 4 + 3]);
                if (d < best) { second = best; best = d; bestIndex = j; }
                else if (d < second) second = d;
            }

            if (bestIndex >= 0 && best <= maxDistance && best < ratio * second)
                result.Add(new PointMatch(query.X[i], query.Y[i], train.X[bestIndex], train.Y[bestIndex], best));
        }
        return result;
    }
}

/// <summary>
/// ORB semplificato in codice puro: angoli FAST-9, orientamento dal baricentro dell'intensità, descrittore BRIEF ruotato su
/// immagine sfocata. Serve ai test (deterministico, senza librerie native) e come ripiego se OpenCV non si carica.
/// </summary>
public sealed class OrbLite : IFeatureExtractor
{
    private const int Border = 20;
    private const int PatchRadius = 13;
    private const int OrientationRadius = 12;
    private const int AngleBins = 30;
    private const int Bits = 256;

    private static readonly (int X, int Y)[] Circle =
    {
        (0, -3), (1, -3), (2, -2), (3, -1), (3, 0), (3, 1), (2, 2), (1, 3),
        (0, 3), (-1, 3), (-2, 2), (-3, 1), (-3, 0), (-3, -1), (-2, -2), (-1, -3)
    };

    /// <summary>Coppie di punti del descrittore, una tabella per ogni orientamento discretizzato.</summary>
    private static readonly (sbyte X1, sbyte Y1, sbyte X2, sbyte Y2)[][] RotatedPatterns = BuildPatterns();

    private readonly int _threshold;

    public OrbLite(int fastThreshold = 18) => _threshold = fastThreshold;

    public string Id => "orb-lite";

    public FeatureSet Extract(GrayImage image, int maxFeatures)
    {
        var w = image.Width;
        var h = image.Height;
        if (w <= 2 * Border || h <= 2 * Border) return FeatureSet.Empty;

        var corners = DetectFast(image);
        var selected = SelectSpread(corners, w, h, maxFeatures);
        var smooth = BoxBlur(image);

        var xs = new float[selected.Count];
        var ys = new float[selected.Count];
        var desc = new ulong[selected.Count * 4];

        for (var k = 0; k < selected.Count; k++)
        {
            var (x, y, _) = selected[k];
            xs[k] = x;
            ys[k] = y;
            var bin = AngleBin(smooth, w, x, y);
            var pattern = RotatedPatterns[bin];
            for (var b = 0; b < Bits; b++)
            {
                var (x1, y1, x2, y2) = pattern[b];
                if (smooth[(y + y1) * w + x + x1] < smooth[(y + y2) * w + x + x2])
                    desc[k * 4 + (b >> 6)] |= 1UL << (b & 63);
            }
        }

        return new FeatureSet(xs, ys, desc);
    }

    private List<(int X, int Y, int Score)> DetectFast(GrayImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var p = image.Pixels;
        var offsets = Circle.Select(c => c.Y * w + c.X).ToArray();
        var scores = new int[w * h];
        var t = _threshold;

        for (var y = Border; y < h - Border; y++)
        for (var x = Border; x < w - Border; x++)
        {
            var i = y * w + x;
            int c = p[i];
            int hi = c + t, lo = c - t;

            // scarto rapido sui punti cardinali (1, 5, 9, 13): almeno 2 su 4 devono superare la soglia
            int p0 = p[i + offsets[0]], p4 = p[i + offsets[4]], p8 = p[i + offsets[8]], p12 = p[i + offsets[12]];
            var brighter = (p0 > hi ? 1 : 0) + (p4 > hi ? 1 : 0) + (p8 > hi ? 1 : 0) + (p12 > hi ? 1 : 0);
            var darker = (p0 < lo ? 1 : 0) + (p4 < lo ? 1 : 0) + (p8 < lo ? 1 : 0) + (p12 < lo ? 1 : 0);
            if (brighter < 2 && darker < 2) continue;

            if (HasArc(p, i, offsets, hi, true) || HasArc(p, i, offsets, lo, false))
            {
                var score = 0;
                foreach (var o in offsets) score += Math.Max(0, Math.Abs(p[i + o] - c) - t);
                scores[i] = score;
            }
        }

        // soppressione dei non massimi 3x3
        var result = new List<(int, int, int)>();
        for (var y = Border; y < h - Border; y++)
        for (var x = Border; x < w - Border; x++)
        {
            var i = y * w + x;
            var s = scores[i];
            if (s == 0) continue;
            // vince il massimo; a parità vince il primo in ordine di scansione
            if (s < scores[i - 1] || s < scores[i - w - 1] || s < scores[i - w] || s < scores[i - w + 1] ||
                s <= scores[i + 1] || s <= scores[i + w - 1] || s <= scores[i + w] || s <= scores[i + w + 1])
                continue;
            result.Add((x, y, s));
        }
        return result;
    }

    private static bool HasArc(byte[] p, int i, int[] offsets, int limit, bool brighter)
    {
        var run = 0;
        for (var k = 0; k < 16 + 9; k++)
        {
            int v = p[i + offsets[k % 16]];
            if (brighter ? v > limit : v < limit)
            {
                if (++run >= 9) return true;
            }
            else run = 0;
        }
        return false;
    }

    /// <summary>Distribuisce i punti su una griglia 8x6 per non concentrarli tutti su una zona molto contrastata.</summary>
    private static List<(int X, int Y, int Score)> SelectSpread(List<(int X, int Y, int Score)> corners, int w, int h, int max)
    {
        const int gx = 8, gy = 6;
        var perCell = Math.Max(2, (int)Math.Ceiling(max / (double)(gx * gy)) * 2);
        var counts = new int[gx * gy];
        var result = new List<(int, int, int)>(max);
        foreach (var c in corners.OrderByDescending(c => c.Score))
        {
            var cell = Math.Min(gy - 1, c.Y * gy / h) * gx + Math.Min(gx - 1, c.X * gx / w);
            if (counts[cell] >= perCell) continue;
            counts[cell]++;
            result.Add(c);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static byte[] BoxBlur(GrayImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var integral = new int[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            var rowSum = 0;
            for (var x = 0; x < w; x++)
            {
                rowSum += image.Pixels[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + rowSum;
            }
        }

        var result = new byte[w * h];
        const int r = 2;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(w, x + r + 1), y1 = Math.Min(h, y + r + 1);
            var sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
            result[y * w + x] = (byte)(sum / ((x1 - x0) * (y1 - y0)));
        }
        return result;
    }

    private static int AngleBin(byte[] smooth, int w, int x, int y)
    {
        double m10 = 0, m01 = 0;
        for (var dy = -OrientationRadius; dy <= OrientationRadius; dy++)
        for (var dx = -OrientationRadius; dx <= OrientationRadius; dx++)
        {
            if (dx * dx + dy * dy > OrientationRadius * OrientationRadius) continue;
            int v = smooth[(y + dy) * w + x + dx];
            m10 += dx * v;
            m01 += dy * v;
        }
        var angle = Math.Atan2(m01, m10);
        var bin = (int)Math.Round(angle / (2 * Math.PI) * AngleBins);
        return ((bin % AngleBins) + AngleBins) % AngleBins;
    }

    private static (sbyte, sbyte, sbyte, sbyte)[][] BuildPatterns()
    {
        var random = new Random(31337);
        var basePairs = new (double X1, double Y1, double X2, double Y2)[Bits];
        double Gauss()
        {
            var u1 = 1 - random.NextDouble();
            var u2 = random.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * (PatchRadius / 2.0);
        }
        double Clamp(double v) => Math.Max(-PatchRadius + 3, Math.Min(PatchRadius - 3, v));
        for (var b = 0; b < Bits; b++)
            basePairs[b] = (Clamp(Gauss()), Clamp(Gauss()), Clamp(Gauss()), Clamp(Gauss()));

        var patterns = new (sbyte, sbyte, sbyte, sbyte)[AngleBins][];
        for (var a = 0; a < AngleBins; a++)
        {
            var theta = a * 2 * Math.PI / AngleBins;
            double c = Math.Cos(theta), s = Math.Sin(theta);
            patterns[a] = basePairs.Select(p => (
                (sbyte)Math.Round(c * p.X1 - s * p.Y1), (sbyte)Math.Round(s * p.X1 + c * p.Y1),
                (sbyte)Math.Round(c * p.X2 - s * p.Y2), (sbyte)Math.Round(s * p.X2 + c * p.Y2))).ToArray();
        }
        return patterns;
    }
}
