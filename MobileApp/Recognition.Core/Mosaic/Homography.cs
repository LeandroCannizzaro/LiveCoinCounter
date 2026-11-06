namespace LiveCoinCounter.Mosaic;

/// <summary>
/// Omografia 3x3 (righe M0..M2): porta i punti di un piano su un altro. Due foto dello stesso tavolo sono legate da
/// un'omografia, ed è così che ogni fotogramma si colloca nel mosaico. <c>a * b</c> applica prima b e poi a.
/// </summary>
public readonly struct Homography
{
    private readonly double[] _m;

    public Homography(params double[] m)
    {
        if (m.Length != 9) throw new ArgumentException("Servono 9 valori.", nameof(m));
        _m = m;
    }

    public double this[int row, int col] => M[row * 3 + col];

    private double[] M => _m ?? IdentityValues;

    private static readonly double[] IdentityValues = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    public static Homography Identity => new((double[])IdentityValues.Clone());

    public static Homography Scale(double s) => new(s, 0, 0, 0, s, 0, 0, 0, 1);

    public static Homography Scale(double sx, double sy) => new(sx, 0, 0, 0, sy, 0, 0, 0, 1);

    public static Homography Translation(double tx, double ty) => new(1, 0, tx, 0, 1, ty, 0, 0, 1);

    /// <summary>Rotazione di <paramref name="degrees"/> (senso orario sullo schermo, asse y verso il basso) attorno all'origine.</summary>
    public static Homography Rotation(double degrees)
    {
        var r = degrees * Math.PI / 180;
        double c = Math.Cos(r), s = Math.Sin(r);
        return new Homography(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    public static Homography operator *(Homography a, Homography b)
    {
        var x = a.M;
        var y = b.M;
        var r = new double[9];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            r[i * 3 + j] = x[i * 3] * y[j] + x[i * 3 + 1] * y[3 + j] + x[i * 3 + 2] * y[6 + j];
        return new Homography(r).Normalized();
    }

    public (double X, double Y) Apply(double x, double y)
    {
        var m = M;
        var w = m[6] * x + m[7] * y + m[8];
        if (Math.Abs(w) < 1e-12) w = 1e-12;
        return ((m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w);
    }

    public Homography Inverse()
    {
        var m = M;
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-15) throw new InvalidOperationException("Omografia non invertibile.");
        return new Homography(
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det).Normalized();
    }

    public Homography Normalized()
    {
        var m = M;
        var s = Math.Abs(m[8]) > 1e-12 ? m[8] : 1;
        var r = new double[9];
        for (var k = 0; k < 9; k++) r[k] = m[k] / s;
        return new Homography(r);
    }

    public bool IsFinite => M.All(double.IsFinite);

    /// <summary>Fattore di area locale nel punto (x, y): quanto si ingrandisce un quadratino attorno a quel punto.</summary>
    public double AreaScaleAt(double x, double y)
    {
        var (x0, y0) = Apply(x, y);
        var (x1, y1) = Apply(x + 1, y);
        var (x2, y2) = Apply(x, y + 1);
        return Math.Abs((x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0));
    }

    /// <summary>I quattro angoli di un rettangolo w x h trasformati (ordine: alto-sx, alto-dx, basso-dx, basso-sx).</summary>
    public Quad MapRect(double width, double height) => new(
        Point(Apply(0, 0)), Point(Apply(width, 0)), Point(Apply(width, height)), Point(Apply(0, height)));

    public RectF MapBox(RectF box)
    {
        var q = new Quad(Point(Apply(box.X, box.Y)), Point(Apply(box.Right, box.Y)),
            Point(Apply(box.Right, box.Bottom)), Point(Apply(box.X, box.Bottom)));
        return q.Bounds;
    }

    private static PointD Point((double X, double Y) p) => new(p.X, p.Y);

    public double[] ToArray() => (double[])M.Clone();

    /// <summary>
    /// Stima ai minimi quadrati (DLT normalizzata, h22 = 1) dell'omografia che porta <paramref name="src"/> su <paramref name="dst"/>.
    /// Servono almeno 4 coppie non allineate. Restituisce null se il sistema è degenere.
    /// </summary>
    public static Homography? Fit(IReadOnlyList<PointD> src, IReadOnlyList<PointD> dst)
    {
        var n = src.Count;
        if (n < 4 || dst.Count != n) return null;

        var ts = NormalizationOf(src);
        var td = NormalizationOf(dst);

        var ata = new double[8, 8];
        var atb = new double[8];
        var row = new double[8];

        for (var k = 0; k < n; k++)
        {
            var (x, y) = ts.Apply(src[k].X, src[k].Y);
            var (u, v) = td.Apply(dst[k].X, dst[k].Y);

            // u = (h0 x + h1 y + h2) / (h6 x + h7 y + 1)  ->  h0 x + h1 y + h2 - h6 x u - h7 y u = u
            Fill(row, x, y, 1, 0, 0, 0, -x * u, -y * u);
            Accumulate(ata, atb, row, u);
            Fill(row, 0, 0, 0, x, y, 1, -x * v, -y * v);
            Accumulate(ata, atb, row, v);
        }

        var h = Solve(ata, atb);
        if (h is null) return null;

        var normalized = new Homography(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
        var result = td.Inverse() * normalized * ts;
        return result.IsFinite ? result : null;
    }

    private static void Fill(double[] row, params double[] values) => Array.Copy(values, row, 8);

    private static void Accumulate(double[,] ata, double[] atb, double[] row, double b)
    {
        for (var i = 0; i < 8; i++)
        {
            if (row[i] == 0) continue;
            atb[i] += row[i] * b;
            for (var j = 0; j < 8; j++) ata[i, j] += row[i] * row[j];
        }
    }

    /// <summary>Eliminazione di Gauss con pivot parziale.</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        const int n = 8;
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n] = b[i];
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12) return null;

            if (pivot != col)
                for (var j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);

            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var factor = m[r, col] / m[col, col];
                if (factor == 0) continue;
                for (var j = col; j <= n; j++) m[r, j] -= factor * m[col, j];
            }
        }

        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = m[i, n] / m[i, i];
        return x;
    }

    /// <summary>Trasformazione di Hartley: centra i punti e li scala a distanza media √2.</summary>
    private static Homography NormalizationOf(IReadOnlyList<PointD> points)
    {
        double cx = 0, cy = 0;
        foreach (var p in points) { cx += p.X; cy += p.Y; }
        cx /= points.Count;
        cy /= points.Count;
        double dist = 0;
        foreach (var p in points) dist += Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        dist /= points.Count;
        var s = dist > 1e-9 ? Math.Sqrt(2) / dist : 1;
        return new Homography(s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1);
    }

    public override string ToString() => string.Join(", ", M.Select(v => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)));
}

public readonly record struct PointD(double X, double Y)
{
    public double DistanceTo(PointD o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
}

/// <summary>Quadrilatero (un fotogramma proiettato sul mosaico).</summary>
public readonly record struct Quad(PointD A, PointD B, PointD C, PointD D)
{
    public IEnumerable<PointD> Points { get { yield return A; yield return B; yield return C; yield return D; } }

    public RectF Bounds
    {
        get
        {
            var minX = Points.Min(p => p.X);
            var minY = Points.Min(p => p.Y);
            var maxX = Points.Max(p => p.X);
            var maxY = Points.Max(p => p.Y);
            return RectF.FromBounds((float)minX, (float)minY, (float)maxX, (float)maxY);
        }
    }

    public PointD Center => new((A.X + B.X + C.X + D.X) / 4, (A.Y + B.Y + C.Y + D.Y) / 4);

    public double Area => Math.Abs(Cross(A, B) + Cross(B, C) + Cross(C, D) + Cross(D, A)) / 2;

    private static double Cross(PointD p, PointD q) => p.X * q.Y - q.X * p.Y;

    /// <summary>Vero se il quadrilatero è convesso e non intrecciato (un'omografia plausibile lo mantiene così).</summary>
    public bool IsConvex
    {
        get
        {
            var pts = Points.ToArray();
            var sign = 0;
            for (var i = 0; i < 4; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % 4];
                var r = pts[(i + 2) % 4];
                var z = (q.X - p.X) * (r.Y - q.Y) - (q.Y - p.Y) * (r.X - q.X);
                var s = Math.Sign(z);
                if (s == 0) return false;
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
        }
    }

    public bool Contains(double x, double y)
    {
        var pts = Points.ToArray();
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % 4];
            var z = (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
            var s = Math.Sign(z);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }
}
