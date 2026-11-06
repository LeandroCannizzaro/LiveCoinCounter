using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Sessions;

/// <summary>Una moneta fisica trovata durante l'acquisizione, con la sua osservazione migliore.</summary>
public sealed class CoinTrack
{
    internal CoinTrack(int id) => Id = id;

    public int Id { get; }

    /// <summary>Riquadro nel mosaico (media pesata delle osservazioni).</summary>
    public RectF MosaicBox { get; internal set; }

    public float DetectionConfidence { get; internal set; }
    public int Observations { get; internal set; }

    /// <summary>Punteggio dell'osservazione migliore: confidenza × nitidezza × distanza dal bordo del fotogramma.</summary>
    public float BestScore { get; internal set; }
    public int BestKeyframe { get; internal set; }
    public RectF BestFrameBox { get; internal set; }

    /// <summary>Taglio proposto dal rilevatore, se ne dà uno (modelli YOLO a 8 classi): classificazione provvisoria.</summary>
    public int? DetectorCents { get; internal set; }
    public float DetectorClassConfidence { get; internal set; }

    /// <summary>"Acquisita": vista in almeno due fotogrammi chiave (altrimenti "in verifica").</summary>
    public bool IsStable => Observations >= 2;

    /// <summary>I fotogrammi chiave in cui è stata vista: due monete dello stesso fotogramma sono sempre monete diverse.</summary>
    internal HashSet<int> Keyframes { get; } = new();
}

/// <summary>Una moneta trovata dal rilevatore in un fotogramma chiave.</summary>
public sealed record CoinObservation(RectF FrameBox, RectF MosaicBox, float Confidence, float Quality, int Keyframe,
    int? Cents = null, float ClassConfidence = 0);

public sealed record CoinUpdate(CoinTrack Track, bool IsNew, bool IsBetter);

/// <summary>
/// Deduplica in coordinate del mosaico: la stessa moneta vista in più fotogrammi chiave diventa una sola. Due riquadri
/// sono la stessa moneta se i centri distano meno di <see cref="MergeDistanceFactor"/> diametri e le dimensioni sono simili,
/// e solo se sono visti in fotogrammi chiave diversi: nello stesso fotogramma monete diverse non si fondono mai, neanche se
/// una è sopra l'altra. Il margine è largo perché il mosaico si deforma (prospettiva accumulata): la stessa moneta può
/// cadere a una distanza di oltre mezzo diametro da un fotogramma all'altro.
/// </summary>
public sealed class CoinRegistry
{
    /// <summary>Distanza massima tra i centri, in diametri, per considerare due osservazioni la stessa moneta.</summary>
    public const double MergeDistanceFactor = 0.75;

    private readonly List<CoinTrack> _tracks = new();
    private readonly object _sync = new();
    private int _nextId = 1;

    public IReadOnlyList<CoinTrack> Tracks { get { lock (_sync) return _tracks.ToList(); } }

    public int Count { get { lock (_sync) return _tracks.Count; } }

    public CoinUpdate Observe(CoinObservation o)
    {
        lock (_sync) return Apply(o, FindMatch(o.MosaicBox, o.Keyframe));
    }

    /// <summary>
    /// Le monete trovate in un fotogramma chiave, tutte insieme. Il mosaico si deforma da un fotogramma chiave all'altro, quindi
    /// prima si stima come il fotogramma va spostato per sovrapporsi alle monete già note (RANSAC su una similitudine, poi
    /// raffinamento affine), e solo dopo si abbina: una moneta a una traccia, la più vicina, senza che due monete dello stesso
    /// fotogramma finiscano sulla stessa traccia. Con poche monete già note si abbina senza allineare.
    /// </summary>
    public IReadOnlyList<CoinUpdate> ObserveKeyframe(IReadOnlyList<CoinObservation> observations)
    {
        lock (_sync)
        {
            var assignment = Assign(observations);
            var updates = new List<CoinUpdate>(observations.Count);
            for (var i = 0; i < observations.Count; i++) updates.Add(Apply(observations[i], assignment[i]));
            return updates;
        }
    }

    private CoinUpdate Apply(CoinObservation o, CoinTrack? match)
    {
        {
            var score = o.Confidence * o.Quality;

            if (match is null)
            {
                var track = new CoinTrack(_nextId++)
                {
                    MosaicBox = o.MosaicBox,
                    DetectionConfidence = o.Confidence,
                    Observations = 1,
                    BestScore = score,
                    BestKeyframe = o.Keyframe,
                    BestFrameBox = o.FrameBox,
                    DetectorCents = o.Cents,
                    DetectorClassConfidence = o.ClassConfidence
                };
                track.Keyframes.Add(o.Keyframe);
                _tracks.Add(track);
                return new CoinUpdate(track, true, true);
            }

            // media pesata con la confidenza
            var w0 = match.DetectionConfidence * match.Observations;
            var w1 = o.Confidence;
            var b = match.MosaicBox;
            var n = o.MosaicBox;
            match.MosaicBox = new RectF(
                (b.X * w0 + n.X * w1) / (w0 + w1), (b.Y * w0 + n.Y * w1) / (w0 + w1),
                (b.Width * w0 + n.Width * w1) / (w0 + w1), (b.Height * w0 + n.Height * w1) / (w0 + w1));
            match.DetectionConfidence = Math.Max(match.DetectionConfidence, o.Confidence);
            match.Observations++;
            match.Keyframes.Add(o.Keyframe);

            if (o.Cents is not null && o.ClassConfidence > match.DetectorClassConfidence)
            {
                match.DetectorCents = o.Cents;
                match.DetectorClassConfidence = o.ClassConfidence;
            }

            var better = score > match.BestScore;
            if (better)
            {
                match.BestScore = score;
                match.BestKeyframe = o.Keyframe;
                match.BestFrameBox = o.FrameBox;
            }
            return new CoinUpdate(match, false, better);
        }
    }

    private CoinTrack? FindMatch(RectF box, int keyframe)
    {
        CoinTrack? best = null;
        var bestDistance = double.MaxValue;
        foreach (var t in _tracks)
        {
            if (t.Keyframes.Contains(keyframe)) continue;      // stesso fotogramma: è un'altra moneta
            var d = Math.Sqrt(Math.Pow(t.MosaicBox.CenterX - box.CenterX, 2) + Math.Pow(t.MosaicBox.CenterY - box.CenterY, 2));
            var diameter = Math.Max(t.MosaicBox.Diameter, box.Diameter);
            var sizeRatio = box.Diameter / Math.Max(1e-3f, t.MosaicBox.Diameter);
            if (d < MergeDistanceFactor * diameter && sizeRatio is > 0.6f and < 1.67f && d < bestDistance)
            {
                best = t;
                bestDistance = d;
            }
        }
        return best;
    }

    private static (double X, double Y) Centre(RectF box) => (box.CenterX, box.CenterY);

    private static bool CentsCompatible(CoinTrack t, CoinObservation o) => t.DetectorCents is null || o.Cents is null || t.DetectorCents == o.Cents;

    /// <summary>Per ogni osservazione la traccia a cui appartiene, o null se è una moneta nuova.</summary>
    private CoinTrack?[] Assign(IReadOnlyList<CoinObservation> obs)
    {
        var result = new CoinTrack?[obs.Count];
        if (obs.Count == 0 || _tracks.Count == 0) return result;

        var keyframe = obs[0].Keyframe;
        var free = _tracks.Where(t => !t.Keyframes.Contains(keyframe)).ToList();
        if (free.Count == 0) return result;

        var transform = free.Count >= 6 && obs.Count >= 4 ? Align(obs, free, keyframe) : null;
        (double X, double Y) Position(CoinObservation o) => transform is null ? Centre(o.MosaicBox) : transform(Centre(o.MosaicBox));

        var candidates = new List<(int Obs, int Track, double Cost)>();
        for (var i = 0; i < obs.Count; i++)
        {
            var p = Position(obs[i]);
            for (var j = 0; j < free.Count; j++)
            {
                var t = free[j];
                var c = Centre(t.MosaicBox);
                var d = Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y));
                var diameter = Math.Max(t.MosaicBox.Diameter, obs[i].MosaicBox.Diameter);
                var sizeRatio = obs[i].MosaicBox.Diameter / Math.Max(1e-3f, t.MosaicBox.Diameter);
                if (d >= MergeDistanceFactor * diameter || sizeRatio is <= 0.6f or >= 1.67f) continue;
                candidates.Add((i, j, d / diameter + (CentsCompatible(t, obs[i]) ? 0 : 0.25)));
            }
        }

        // le coppie migliori per prime: una traccia e un'osservazione si abbinano una volta sola
        var usedObs = new HashSet<int>();
        var usedTracks = new HashSet<int>();
        foreach (var c in candidates.OrderBy(c => c.Cost))
        {
            if (usedObs.Contains(c.Obs) || usedTracks.Contains(c.Track)) continue;
            usedObs.Add(c.Obs);
            usedTracks.Add(c.Track);
            result[c.Obs] = free[c.Track];
        }
        return result;
    }

    /// <summary>
    /// Come spostare le osservazioni del fotogramma per sovrapporle alle tracce: una similitudine trovata con RANSAC (coppie con lo
    /// stesso taglio del rilevatore), poi raffinata con una trasformazione affine sulle coppie vicine. Null se non c'è un
    /// allineamento convincente (almeno 5 coppie concordi).
    /// </summary>
    private static Func<(double X, double Y), (double X, double Y)>? Align(IReadOnlyList<CoinObservation> obs, List<CoinTrack> free, int keyframe)
    {
        var pairs = new List<(CoinObservation O, CoinTrack T)>();
        foreach (var o in obs)
            foreach (var t in free)
            {
                var a = Centre(o.MosaicBox);
                var b = Centre(t.MosaicBox);
                if (Dist(a, b) < 2.0 * Math.Max(t.MosaicBox.Diameter, o.MosaicBox.Diameter) && CentsCompatible(t, o)) pairs.Add((o, t));
            }
        if (pairs.Count < 5) return null;

        var random = new Random(keyframe * 7919 + 17);        // fisso: lo stesso fotogramma dà sempre lo stesso risultato
        Func<(double X, double Y), (double X, double Y)>? best = null;
        var bestInliers = 0;
        for (var it = 0; it < 400; it++)
        {
            var p1 = pairs[random.Next(pairs.Count)];
            var p2 = pairs[random.Next(pairs.Count)];
            if (ReferenceEquals(p1.O, p2.O) || ReferenceEquals(p1.T, p2.T)) continue;
            var o1 = Centre(p1.O.MosaicBox); var o2 = Centre(p2.O.MosaicBox);
            var t1 = Centre(p1.T.MosaicBox); var t2 = Centre(p2.T.MosaicBox);
            double vx = o2.X - o1.X, vy = o2.Y - o1.Y, wx = t2.X - t1.X, wy = t2.Y - t1.Y;
            var n = vx * vx + vy * vy;
            if (n < 1e-6) continue;
            var a = (vx * wx + vy * wy) / n;
            var b = (vx * wy - vy * wx) / n;
            var scale = Math.Sqrt(a * a + b * b);
            if (scale is < 0.75 or > 1.33) continue;
            (double X, double Y) T((double X, double Y) p) => (a * (p.X - o1.X) - b * (p.Y - o1.Y) + t1.X, b * (p.X - o1.X) + a * (p.Y - o1.Y) + t1.Y);
            var inliers = pairs.Count(q => Dist(T(Centre(q.O.MosaicBox)), Centre(q.T.MosaicBox)) < 0.45 * Math.Max(q.T.MosaicBox.Diameter, q.O.MosaicBox.Diameter));
            if (inliers > bestInliers) { bestInliers = inliers; best = T; }
        }
        if (best is null || bestInliers < 5) return null;

        // raffinamento: abbinamenti stretti uno a uno, poi trasformazione affine ai minimi quadrati (3 giri)
        var transform = best;
        for (var round = 0; round < 3; round++)
        {
            var matched = new List<(double Ox, double Oy, double Tx, double Ty)>();
            var cand = new List<(int I, int J, double D)>();
            for (var i = 0; i < obs.Count; i++)
            {
                var p = transform(Centre(obs[i].MosaicBox));
                for (var j = 0; j < free.Count; j++)
                {
                    var d = Dist(p, Centre(free[j].MosaicBox));
                    if (d < 0.55 * Math.Max(free[j].MosaicBox.Diameter, obs[i].MosaicBox.Diameter)) cand.Add((i, j, d));
                }
            }
            var uo = new HashSet<int>(); var ut = new HashSet<int>();
            foreach (var c in cand.OrderBy(c => c.D))
            {
                if (uo.Contains(c.I) || ut.Contains(c.J)) continue;
                uo.Add(c.I); ut.Add(c.J);
                var o = Centre(obs[c.I].MosaicBox); var t = Centre(free[c.J].MosaicBox);
                matched.Add((o.X, o.Y, t.X, t.Y));
            }
            if (matched.Count < 6) break;
            var affine = FitAffine(matched);
            if (affine is null) break;
            transform = affine;
        }
        return transform;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Trasformazione affine ai minimi quadrati; null se degenere o troppo deformante (determinante fuori da 0,6–1,7).</summary>
    private static Func<(double X, double Y), (double X, double Y)>? FitAffine(List<(double Ox, double Oy, double Tx, double Ty)> m)
    {
        var a = new double[3, 3];
        var bu = new double[3];
        var bv = new double[3];
        foreach (var (ox, oy, tx, ty) in m)
        {
            var r = new[] { ox, oy, 1.0 };
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++) a[i, j] += r[i] * r[j];
                bu[i] += r[i] * tx;
                bv[i] += r[i] * ty;
            }
        }
        var cu = Solve3(a, bu);
        var cv = Solve3(a, bv);
        if (cu is null || cv is null) return null;
        var det = cu[0] * cv[1] - cu[1] * cv[0];
        if (det is < 0.6 or > 1.7) return null;
        return p => (cu[0] * p.X + cu[1] * p.Y + cu[2], cv[0] * p.X + cv[1] * p.Y + cv[2]);
    }

    private static double[]? Solve3(double[,] a, double[] b)
    {
        var m = new double[3, 4];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++) m[i, j] = a[i, j];
            m[i, 3] = b[i];
        }
        for (var i = 0; i < 3; i++)
        {
            var p = i;
            for (var k = i + 1; k < 3; k++) if (Math.Abs(m[k, i]) > Math.Abs(m[p, i])) p = k;
            if (Math.Abs(m[p, i]) < 1e-9) return null;
            if (p != i) for (var j = 0; j < 4; j++) (m[i, j], m[p, j]) = (m[p, j], m[i, j]);
            for (var k = i + 1; k < 3; k++)
            {
                var f = m[k, i] / m[i, i];
                for (var j = i; j < 4; j++) m[k, j] -= f * m[i, j];
            }
        }
        var x = new double[3];
        for (var i = 2; i >= 0; i--)
        {
            var s = m[i, 3];
            for (var j = i + 1; j < 3; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }

    /// <summary>Ripristina una moneta già salvata (completamento di una sessione interrotta).</summary>
    public CoinTrack Restore(int id, RectF mosaicBox, float confidence, int observations)
    {
        lock (_sync)
        {
            var track = new CoinTrack(id) { MosaicBox = mosaicBox, DetectionConfidence = confidence, Observations = observations, BestScore = 0 };
            _tracks.Add(track);
            _nextId = Math.Max(_nextId, id + 1);
            return track;
        }
    }
}

/// <summary>Filtri sui riquadri del rilevatore in un fotogramma chiave.</summary>
public static class DetectionFilter
{
    /// <summary>
    /// Una moneta che tocca il bordo del fotogramma è tagliata: si scarta, comparirà intera in un altro fotogramma chiave.
    /// Si scartano anche riquadri troppo allungati (non sono monete viste dall'alto).
    /// </summary>
    public static bool IsUsable(RectF box, int frameWidth, int frameHeight, float borderFraction = 0.015f) =>
        Reason(box, frameWidth, frameHeight, borderFraction) == "ok";

    /// <summary>Come <see cref="IsUsable"/>, ma dice perché il riquadro si scarta: ok, border, aspect o small.</summary>
    public static string Reason(RectF box, int frameWidth, int frameHeight, float borderFraction = 0.015f)
    {
        var margin = Math.Max(2, Math.Min(frameWidth, frameHeight) * borderFraction);
        if (box.X < margin || box.Y < margin || box.Right > frameWidth - margin || box.Bottom > frameHeight - margin) return "border";
        var aspect = box.Width / Math.Max(1e-3f, box.Height);
        if (aspect is not (> 0.7f and < 1.43f)) return "aspect";
        return box.Diameter >= 8 ? "ok" : "small";
    }

    /// <summary>Qualità di un'osservazione: più alta per monete vicine al centro del fotogramma.</summary>
    public static float Centrality(RectF box, int frameWidth, int frameHeight)
    {
        var dx = Math.Abs(box.CenterX - frameWidth / 2f) / (frameWidth / 2f);
        var dy = Math.Abs(box.CenterY - frameHeight / 2f) / (frameHeight / 2f);
        return Math.Clamp(1 - 0.5f * Math.Max(dx, dy), 0.3f, 1f);
    }
}
