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
        public int Id;
        public double SumX, SumY, SumScale;
        public int Count;
        public int Rejected;
        public double X => SumX / Count;
        public double Y => SumY / Count;
    }

    // un marker fisico = una voce; lo stesso ID può comparire più volte (il foglio stampato due volte, marker ritagliati dallo stesso numero)
    private readonly List<Entry> _entries = new();
    private readonly object _sync = new();

    /// <summary>Un solo marker visto nel fotogramma (vedi <see cref="ObserveFrame"/>).</summary>
    public bool Observe(MarkerDetection marker, Homography frameToMosaic, double imageToFrameScale = 1) =>
        ObserveFrame(new[] { marker }, frameToMosaic, imageToFrameScale)[0];

    /// <summary>
    /// I marker visti in un fotogramma, riportati sul mosaico con l'omografia del fotogramma; per ognuno dice se l'avvistamento è
    /// plausibile e registrato. Due marker con lo stesso ID nello stesso fotogramma sono due marker fisici diversi: si tengono separati;
    /// un ID visto da solo va al marker con quell'ID più vicino (il mosaico può essersi spostato di centinaia di pixel).
    /// </summary>
    public IReadOnlyList<bool> ObserveFrame(IReadOnlyList<MarkerDetection> markers, Homography frameToMosaic, double imageToFrameScale = 1)
    {
        var result = new bool[markers.Count];
        var measured = new (PointD Centre, double Scale)?[markers.Count];
        for (var i = 0; i < markers.Count; i++)
            if (markers[i].Id is >= 0 and <= MarkerSpec.MaxId && TryMeasure(markers[i].Corners, frameToMosaic, imageToFrameScale, out var c, out var s))
                measured[i] = (c, s);

        lock (_sync)
        {
            foreach (var group in Enumerable.Range(0, markers.Count).Where(i => measured[i] is not null).GroupBy(i => markers[i].Id))
            {
                var candidates = _entries.Where(e => e.Id == group.Key && e.Count > 0).ToList();
                var taken = new HashSet<Entry>();
                // le coppie più vicine per prime: ogni marker già noto prende al più un avvistamento per fotogramma
                var pairs = group.SelectMany(i => candidates.Select(e => (I: i, E: e, D: Dist(measured[i]!.Value.Centre, e))))
                    .OrderBy(p => p.D).ToList();
                var assigned = new Dictionary<int, Entry>();
                foreach (var p in pairs)
                    if (!assigned.ContainsKey(p.I) && !taken.Contains(p.E)) { assigned[p.I] = p.E; taken.Add(p.E); }

                foreach (var i in group)
                {
                    var (centre, scale) = measured[i]!.Value;
                    if (!assigned.TryGetValue(i, out var entry))
                    {
                        entry = new Entry { Id = group.Key };
                        _entries.Add(entry);
                    }
                    if (entry.Count >= 3 && Math.Abs(scale / (entry.SumScale / entry.Count) - 1) > MaxScaleDeviation)
                    {
                        entry.Rejected++;
                        continue;
                    }
                    entry.SumX += centre.X;
                    entry.SumY += centre.Y;
                    entry.SumScale += scale;
                    entry.Count++;
                    result[i] = true;
                }
            }
        }
        return result;
    }

    private static double Dist(PointD p, Entry e) => Math.Sqrt((p.X - e.X) * (p.X - e.X) + (p.Y - e.Y) * (p.Y - e.Y));

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
                return _entries.Where(e => e.Count > 0)
                    .Select(e => new MarkerLandmark(e.Id, e.SumX / e.Count, e.SumY / e.Count, e.SumScale / e.Count, e.Count))
                    .OrderBy(m => m.Id).ThenBy(m => m.X).ToList();
        }
    }

    /// <summary>I marker fisici visti (un ID stampato due volte e messo in due posti conta due).</summary>
    public int Count { get { lock (_sync) return _entries.Count(e => e.Count > 0); } }

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
                _entries.Add(new Entry { Id = l.Id, SumX = l.X * l.Observations, SumY = l.Y * l.Observations, SumScale = l.PxPerMm * l.Observations, Count = l.Observations });
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
