namespace LiveCoinCounter.Mosaic;

/// <summary>I marker di scala stampati (docs/marker-scala-aruco-2cm.pdf): ArUco 4x4 (DICT_4X4_50 di OpenCV), ID da 0 a 47.</summary>
public static class MarkerSpec
{
    /// <summary>Lato del quadrato nero, in millimetri.</summary>
    public const double SideMm = 20;

    public const int MaxId = 47;
}

/// <summary>Un marker visto in un'immagine: ID e angoli in pixel dell'immagine (alto-sinistra, alto-destra, basso-destra, basso-sinistra del marker).</summary>
public sealed record MarkerDetection(int Id, Quad Corners);

/// <summary>Cerca i marker in un'immagine in grigio (OpenCV su Android).</summary>
public interface IMarkerDetector
{
    IReadOnlyList<MarkerDetection> Detect(GrayImage image);
}

/// <summary>
/// Un marker piazzato sul mosaico: dove sta e quanti pixel del mosaico fa un millimetro lì. Il mosaico non è metrico (la prospettiva lo
/// deforma), quindi la scala vale vicino al marker: serve come scala locale per le monete intorno.
/// </summary>
public sealed record MarkerLandmark(int Id, double X, double Y, double PxPerMm, int Observations);

/// <summary>
/// I marker visti durante l'acquisizione, in coordinate del mosaico. Ogni avvistamento aggiorna posizione e scala del marker con una
/// media; un avvistamento molto diverso dagli altri (marker visto di sbieco, omografia sbagliata) si scarta.
/// </summary>
public sealed class MarkerRegistry
{
    private const double MaxEdgeRatio = 1.6;
    private const double MaxScaleDeviation = 0.25;

    private sealed class Entry
    {
        public double SumX, SumY, SumScale;
        public int Count;
        public int Rejected;
    }

    private readonly Dictionary<int, Entry> _entries = new();
    private readonly object _sync = new();

    /// <summary>Il marker visto nel fotogramma, riportato sul mosaico con l'omografia del fotogramma. Falso se l'avvistamento non è plausibile.</summary>
    public bool Observe(MarkerDetection marker, Homography frameToMosaic, double imageToFrameScale = 1)
    {
        if (marker.Id is < 0 or > MarkerSpec.MaxId) return false;
        if (!TryMeasure(marker.Corners, frameToMosaic, imageToFrameScale, out var centre, out var pxPerMm)) return false;

        lock (_sync)
        {
            if (!_entries.TryGetValue(marker.Id, out var e)) _entries[marker.Id] = e = new Entry();
            if (e.Count >= 3 && Math.Abs(pxPerMm / (e.SumScale / e.Count) - 1) > MaxScaleDeviation)
            {
                e.Rejected++;
                return false;
            }
            e.SumX += centre.X;
            e.SumY += centre.Y;
            e.SumScale += pxPerMm;
            e.Count++;
            return true;
        }
    }

    /// <summary>
    /// Posizione sul mosaico e scala (pixel del mosaico per millimetro) di un marker visto con gli angoli dati.
    /// Falso se il quadrilatero è troppo deforme per essere un quadrato visto da sopra.
    /// </summary>
    public static bool TryMeasure(Quad corners, Homography frameToMosaic, double imageToFrameScale, out PointD centre, out double pxPerMm)
    {
        PointD Map(PointD p)
        {
            var (x, y) = frameToMosaic.Apply(p.X * imageToFrameScale, p.Y * imageToFrameScale);
            return new PointD(x, y);
        }
        var a = Map(corners.A);
        var b = Map(corners.B);
        var c = Map(corners.C);
        var d = Map(corners.D);
        var edges = new[] { a.DistanceTo(b), b.DistanceTo(c), c.DistanceTo(d), d.DistanceTo(a) };
        centre = new PointD((a.X + b.X + c.X + d.X) / 4, (a.Y + b.Y + c.Y + d.Y) / 4);
        pxPerMm = edges.Average() / MarkerSpec.SideMm;
        if (!double.IsFinite(pxPerMm) || pxPerMm <= 0 || edges.Min() <= 0) return false;
        return edges.Max() / edges.Min() <= MaxEdgeRatio;
    }

    /// <summary>I marker visti, con scala e posizione medie.</summary>
    public IReadOnlyList<MarkerLandmark> Landmarks
    {
        get
        {
            lock (_sync)
                return _entries.Where(kv => kv.Value.Count > 0)
                    .Select(kv => new MarkerLandmark(kv.Key, kv.Value.SumX / kv.Value.Count, kv.Value.SumY / kv.Value.Count,
                        kv.Value.SumScale / kv.Value.Count, kv.Value.Count))
                    .OrderBy(m => m.Id).ToList();
        }
    }

    public int Count { get { lock (_sync) return _entries.Count(kv => kv.Value.Count > 0); } }

    /// <summary>Scala mediana dei marker visti (pixel del mosaico per millimetro), o null se non ce n'è nessuno.</summary>
    public double? MedianPxPerMm => MedianOf(Landmarks);

    public static double? MedianOf(IReadOnlyList<MarkerLandmark> landmarks)
    {
        if (landmarks.Count == 0) return null;
        var sorted = landmarks.Select(l => l.PxPerMm).OrderBy(s => s).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>Ripristina i marker salvati con una sessione.</summary>
    public void Restore(IEnumerable<MarkerLandmark> landmarks)
    {
        lock (_sync)
            foreach (var l in landmarks)
                _entries[l.Id] = new Entry { SumX = l.X * l.Observations, SumY = l.Y * l.Observations, SumScale = l.PxPerMm * l.Observations, Count = l.Observations };
    }
}

/// <summary>
/// La scala locale data dai marker: media dei marker vicini pesata con l'inverso del quadrato della distanza. Vale entro
/// <see cref="MaxDistancePx"/> dal marker più vicino; oltre non c'è una scala affidabile (il mosaico si deforma).
/// </summary>
public static class MarkerScale
{
    /// <summary>Raggio massimo d'influenza, in pixel del mosaico (circa 15 cm con la scala tipica).</summary>
    public const double MaxDistancePx = 700;

    public static double? At(double x, double y, IReadOnlyList<MarkerLandmark> landmarks, double maxDistancePx = MaxDistancePx)
    {
        double sumW = 0, sum = 0;
        foreach (var m in landmarks)
        {
            var d = Math.Sqrt((m.X - x) * (m.X - x) + (m.Y - y) * (m.Y - y));
            if (d > maxDistancePx) continue;
            var w = 1.0 / (1.0 + d * d / 10000.0) * Math.Min(m.Observations, 5);
            sumW += w;
            sum += w * m.PxPerMm;
        }
        return sumW <= 0 ? null : sum / sumW;
    }
}
