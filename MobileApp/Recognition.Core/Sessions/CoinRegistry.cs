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
}

/// <summary>Una moneta trovata dal rilevatore in un fotogramma chiave.</summary>
public sealed record CoinObservation(RectF FrameBox, RectF MosaicBox, float Confidence, float Quality, int Keyframe,
    int? Cents = null, float ClassConfidence = 0);

public sealed record CoinUpdate(CoinTrack Track, bool IsNew, bool IsBetter);

/// <summary>
/// Deduplica in coordinate del mosaico: la stessa moneta vista in più fotogrammi chiave diventa una sola. Due riquadri
/// sono la stessa moneta se i centri distano meno di mezzo diametro e le dimensioni sono simili.
/// </summary>
public sealed class CoinRegistry
{
    private readonly List<CoinTrack> _tracks = new();
    private readonly object _sync = new();
    private int _nextId = 1;

    public IReadOnlyList<CoinTrack> Tracks { get { lock (_sync) return _tracks.ToList(); } }

    public int Count { get { lock (_sync) return _tracks.Count; } }

    public CoinUpdate Observe(CoinObservation o)
    {
        lock (_sync)
        {
            var score = o.Confidence * o.Quality;
            var match = FindMatch(o.MosaicBox);

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

    private CoinTrack? FindMatch(RectF box)
    {
        CoinTrack? best = null;
        var bestDistance = double.MaxValue;
        foreach (var t in _tracks)
        {
            var d = Math.Sqrt(Math.Pow(t.MosaicBox.CenterX - box.CenterX, 2) + Math.Pow(t.MosaicBox.CenterY - box.CenterY, 2));
            var diameter = Math.Max(t.MosaicBox.Diameter, box.Diameter);
            var sizeRatio = box.Diameter / Math.Max(1e-3f, t.MosaicBox.Diameter);
            if (d < 0.5 * diameter && sizeRatio is > 0.6f and < 1.67f && d < bestDistance)
            {
                best = t;
                bestDistance = d;
            }
        }
        return best;
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
    public static bool IsUsable(RectF box, int frameWidth, int frameHeight, float borderFraction = 0.015f)
    {
        var margin = Math.Max(2, Math.Min(frameWidth, frameHeight) * borderFraction);
        if (box.X < margin || box.Y < margin || box.Right > frameWidth - margin || box.Bottom > frameHeight - margin) return false;
        var aspect = box.Width / Math.Max(1e-3f, box.Height);
        return aspect is > 0.7f and < 1.43f && box.Diameter >= 8;
    }

    /// <summary>Qualità di un'osservazione: più alta per monete vicine al centro del fotogramma.</summary>
    public static float Centrality(RectF box, int frameWidth, int frameHeight)
    {
        var dx = Math.Abs(box.CenterX - frameWidth / 2f) / (frameWidth / 2f);
        var dy = Math.Abs(box.CenterY - frameHeight / 2f) / (frameHeight / 2f);
        return Math.Clamp(1 - 0.5f * Math.Max(dx, dy), 0.3f, 1f);
    }
}
