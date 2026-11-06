namespace LiveCoinCounter.Mosaic;

/// <summary>Un fotogramma della fotocamera, con la posa ARCore quando disponibile.</summary>
/// <param name="RotationDegrees">Rotazione (0/90/180/270) che porta il fotogramma dritto rispetto allo schermo.</param>
public sealed record CameraFrame(ArgbImage Image, long TimestampNs, int RotationDegrees, PlanarPose? Pose = null);

public enum TrackStatus
{
    /// <summary>In attesa di qualcosa per partire (es. ARCore non ha ancora trovato il piano del tavolo).</summary>
    Initializing,
    Tracking,
    Lost
}

/// <summary>Esito del tracking di un fotogramma.</summary>
/// <param name="FrameToMosaic">Omografia fotogramma (pixel) → mosaico; null se il fotogramma non è stato collocato.</param>
/// <param name="Sharpness">Varianza del laplaciano sul fotogramma ridotto.</param>
/// <param name="PxPerMm">Scala del mosaico in pixel per millimetro, se nota (ARCore).</param>
public sealed record TrackResult(TrackStatus Status, Homography? FrameToMosaic, double Sharpness, string? Hint = null,
    int Inliers = 0, double? PxPerMm = null, TrackDetail? Detail = null);

/// <summary>Un tentativo di agganciare il fotogramma a un fotogramma chiave. <see cref="Outcome"/>: ok, few_matches, ransac_failed, low_ratio, not_better o implausible:motivo.</summary>
public sealed record CandidateAttempt(int Keyframe, int Matches, int Inliers, double InlierRatio, string Outcome);

/// <summary>Perché il tracking ha dato quel risultato: serve al registro diagnostico, non alla logica.</summary>
public sealed record TrackDetail(string Source, int Features, int KeyframeCount, int LostFrames, bool Relocalizing,
    IReadOnlyList<CandidateAttempt> Attempts, string? Reason = null, double? Deviation = null, int Anchors = 0, bool AnchorAdded = false);

/// <summary>
/// Colloca ogni fotogramma nel mosaico. Tre implementazioni, scelte nelle impostazioni: feature (OpenCV o codice puro),
/// ARCore (posa della fotocamera + piano del tavolo) e ibrida.
/// </summary>
public interface IMosaicTracker
{
    string Id { get; }

    TrackResult Process(CameraFrame frame);

    /// <summary>Il fotogramma appena elaborato diventa fotogramma chiave con questa omografia (serve da riferimento ai successivi).</summary>
    void CommitKeyframe(Homography frameToMosaic);
}

public sealed record FeatureTrackerOptions
{
    /// <summary>Lato massimo dell'immagine in grigio usata per le feature.</summary>
    public int TrackingMaxSide { get; init; } = 640;
    public int MaxFeatures { get; init; } = 600;

    /// <summary>
    /// Pixel del mosaico per pixel del primo fotogramma: 0,75 dà un mosaico dettagliato (da ingrandire a tutto schermo) e
    /// resta leggero (il rilevamento usa i fotogrammi originali). Era 0,5.
    /// </summary>
    public double MosaicScale { get; init; } = 0.75;
    public double RansacThreshold { get; init; } = 3.0;
    public int MinInliers { get; init; } = 20;
    public double MinInlierRatio { get; init; } = 0.2;

    /// <summary>Quanti fotogrammi chiave vicini provare per ogni fotogramma.</summary>
    public int CandidateKeyframes { get; init; } = 3;

    /// <summary>Dopo quanti fotogrammi non collocati di fila il tracking si considera perso.</summary>
    public int LostAfter { get; init; } = 4;

    /// <summary>Punti minimi nel primo fotogramma: senza, il mosaico partirebbe da una base su cui non ci si può riagganciare.</summary>
    public int MinInitialFeatures { get; init; } = 40;

    // ---- fotogrammi di aggancio ----
    // Dalle prove sul campo: con un solo fotogramma chiave per decine di secondi gli inlier calano da 350 a 20 in poco più
    // di un secondo (tavolo metallico, riflessi) e il tracking si perde. Il tracker aggiunge da sé fotogrammi di aggancio
    // leggeri (solo punti caratteristici, nessun mosaico né rilevamento) così i frame si confrontano sempre con uno vicino.

    /// <summary>Un nuovo fotogramma di aggancio quando il centro dista più di questa frazione del lato del fotogramma dal più vicino.</summary>
    public double AnchorSpacing { get; init; } = 0.3;

    /// <summary>…oppure quando l'aggancio è debole (meno inlier di così), purché siano almeno <see cref="MinAnchorInliers"/>.</summary>
    public int AnchorBelowInliers { get; init; } = 60;
    public int MinAnchorInliers { get; init; } = 25;

    /// <summary>Fotogrammi minimi tra due fotogrammi di aggancio.</summary>
    public int MinAnchorGapFrames { get; init; } = 2;

    /// <summary>Oltre questo numero si scartano i fotogrammi di aggancio più vecchi (quelli chiave restano).</summary>
    public int MaxAnchors { get; init; } = 120;

    /// <summary>Se un candidato ha già almeno questi inlier (e rapporto ≥ 0,5) non si provano gli altri: dimezza il tempo per fotogramma.</summary>
    public int EarlyAcceptInliers { get; init; } = 80;
}

/// <summary>
/// Tracking con punti caratteristici: ogni fotogramma si confronta con i fotogrammi chiave vicini (non solo con il
/// precedente, così l'errore non si accumula) e si colloca con RANSAC. Se si perde, prova a rilocalizzarsi su tutti i
/// fotogrammi chiave a turno, partendo da quelli vicini all'ultima posizione nota.
/// </summary>
public sealed class FeatureMosaicTracker : IMosaicTracker
{
    private sealed record Keyframe(FeatureSet Features, float GrayScale, Homography FrameToMosaic, PointD Center, double AreaScale, bool IsAnchor = false);

    private readonly IFeatureExtractor _extractor;
    private readonly FeatureTrackerOptions _options;
    private readonly List<Keyframe> _keyframes = new();
    private Homography? _last;
    private int _lostFrames;
    private int _relocalizeOffset;
    private (FeatureSet Features, float Scale, int Width, int Height)? _pending;
    private int _framesSinceAnchor = int.MaxValue / 2;

    public FeatureMosaicTracker(IFeatureExtractor extractor, FeatureTrackerOptions? options = null)
    {
        _extractor = extractor;
        _options = options ?? new FeatureTrackerOptions();
    }

    public string Id => "features-" + _extractor.Id;

    public int KeyframeCount => _keyframes.Count;

    public TrackResult Process(CameraFrame frame) => Process(frame, prior: null);

    /// <param name="prior">Stima a priori dell'omografia (modalità ibrida): guida la scelta dei fotogrammi chiave e il primo fotogramma.</param>
    public TrackResult Process(CameraFrame frame, Homography? prior)
    {
        var gray = ImageOps.ToGray(frame.Image, _options.TrackingMaxSide, out var scale);
        var sharpness = ImageOps.Sharpness(gray);
        var features = _extractor.Extract(gray, _options.MaxFeatures);
        _pending = (features, scale, frame.Image.Width, frame.Image.Height);

        if (_keyframes.Count == 0)
        {
            if (features.Count < _options.MinInitialFeatures)
                return new TrackResult(TrackStatus.Initializing, null, sharpness,
                    "Inquadra una zona con le monete e tieni fermo il telefono (serve più luce o più dettaglio)", Inliers: features.Count,
                    Detail: new TrackDetail(Id, features.Count, 0, 0, false, Array.Empty<CandidateAttempt>(), "too_few_features"));

            var initial = prior ?? Initial(frame);
            _last = initial;
            return new TrackResult(TrackStatus.Tracking, initial, sharpness, Inliers: features.Count,
                Detail: new TrackDetail(Id, features.Count, 0, 0, false, Array.Empty<CandidateAttempt>(), "first_frame"));
        }

        var center = new PointD(frame.Image.Width / 2.0, frame.Image.Height / 2.0);
        var predicted = prior ?? _last;
        var predictedCenter = predicted is { } p ? Map(p, center) : _keyframes[^1].Center;

        var ordered = _keyframes.OrderBy(k => k.Center.DistanceTo(predictedCenter)).ToList();
        var candidates = ordered.Take(_options.CandidateKeyframes).ToList();
        var relocalizing = _lostFrames >= _options.LostAfter && ordered.Count > candidates.Count;
        var attempts = new List<CandidateAttempt>();
        if (relocalizing)
        {
            // rilocalizzazione: a ogni fotogramma un gruppo diverso di fotogrammi chiave, dai più vicini ai più lontani
            _relocalizeOffset = (_relocalizeOffset + _options.CandidateKeyframes) % ordered.Count;
            candidates.AddRange(ordered.Skip(_relocalizeOffset).Take(_options.CandidateKeyframes));
            candidates = candidates.Distinct().ToList();
        }

        Homography? best = null;
        var bestInliers = 0;
        foreach (var keyframe in candidates)
        {
            var index = _keyframes.IndexOf(keyframe);
            var matches = FeatureMatcher.Match(features, keyframe.Features);
            var ransac = HomographyRansac.Estimate(matches, _options.RansacThreshold, minInliers: _options.MinInliers);
            if (ransac is null)
            {
                attempts.Add(new CandidateAttempt(index, matches.Count, 0, 0, matches.Count < _options.MinInliers ? "few_matches" : "ransac_failed"));
                continue;
            }
            if (ransac.InlierRatio < _options.MinInlierRatio)
            {
                attempts.Add(new CandidateAttempt(index, matches.Count, ransac.Inliers, ransac.InlierRatio, "low_ratio"));
                continue;
            }
            if (ransac.Inliers <= bestInliers)
            {
                attempts.Add(new CandidateAttempt(index, matches.Count, ransac.Inliers, ransac.InlierRatio, "not_better"));
                continue;
            }

            // da pixel in grigio del fotogramma a pixel in grigio del fotogramma chiave → pixel originali → mosaico
            var frameToKeyframe = Homography.Scale(1 / keyframe.GrayScale) * ransac.Homography * Homography.Scale(scale);
            var candidate = keyframe.FrameToMosaic * frameToKeyframe;
            var problem = Implausible(candidate, frame.Image.Width, frame.Image.Height, keyframe.AreaScale);
            if (problem is not null)
            {
                attempts.Add(new CandidateAttempt(index, matches.Count, ransac.Inliers, ransac.InlierRatio, "implausible:" + problem));
                continue;
            }

            attempts.Add(new CandidateAttempt(index, matches.Count, ransac.Inliers, ransac.InlierRatio, "ok"));
            best = candidate;
            bestInliers = ransac.Inliers;

            // i candidati sono in ordine di vicinanza: se il primo aggancia bene non serve provare gli altri
            if (bestInliers >= _options.EarlyAcceptInliers && ransac.InlierRatio >= 0.5) break;
        }

        if (best is null)
        {
            _lostFrames++;
            var lost = _lostFrames >= _options.LostAfter;
            return new TrackResult(lost ? TrackStatus.Lost : TrackStatus.Tracking, null, sharpness,
                lost ? "Posizione persa: torna dove vedi il riquadro arancione, oppure tocca ↻ per ricominciare" : null,
                Detail: new TrackDetail(Id, features.Count, _keyframes.Count, _lostFrames, relocalizing, attempts, "not_placed"));
        }

        _lostFrames = 0;
        _last = best;
        _framesSinceAnchor++;
        var anchored = TryAddAnchor(best.Value, bestInliers);
        return new TrackResult(TrackStatus.Tracking, best, sharpness, Inliers: bestInliers,
            Detail: new TrackDetail(Id, features.Count, _keyframes.Count, 0, relocalizing, attempts, "placed",
                Anchors: _keyframes.Count(k => k.IsAnchor), AnchorAdded: anchored));
    }

    /// <summary>
    /// Aggiunge il fotogramma appena collocato come fotogramma di aggancio se si è allontanato dai riferimenti noti o se
    /// l'aggancio è debole: i frame successivi avranno sempre un riferimento vicino e simile.
    /// </summary>
    private bool TryAddAnchor(Homography frameToMosaic, int inliers)
    {
        if (_pending is not { } p || inliers < _options.MinAnchorInliers || _framesSinceAnchor < _options.MinAnchorGapFrames) return false;

        var center = Map(frameToMosaic, new PointD(p.Width / 2.0, p.Height / 2.0));
        var quad = frameToMosaic.MapRect(p.Width, p.Height);
        var side = Math.Min(quad.A.DistanceTo(quad.B), quad.A.DistanceTo(quad.D));
        var nearest = _keyframes.Min(k => k.Center.DistanceTo(center));
        if (nearest < _options.AnchorSpacing * side && inliers >= _options.AnchorBelowInliers) return false;

        _keyframes.Add(new Keyframe(p.Features, p.Scale, frameToMosaic, center, frameToMosaic.AreaScaleAt(p.Width / 2.0, p.Height / 2.0), IsAnchor: true));
        _framesSinceAnchor = 0;
        if (_keyframes.Count(k => k.IsAnchor) > _options.MaxAnchors)
            _keyframes.RemoveAt(_keyframes.FindIndex(k => k.IsAnchor));
        return true;
    }

    public void CommitKeyframe(Homography frameToMosaic)
    {
        if (_pending is not { } p) return;
        var center = Map(frameToMosaic, new PointD(p.Width / 2.0, p.Height / 2.0));
        var area = frameToMosaic.AreaScaleAt(p.Width / 2.0, p.Height / 2.0);
        _keyframes.Add(new Keyframe(p.Features, p.Scale, frameToMosaic, center, area));
        _last = frameToMosaic;
    }

    /// <summary>Primo fotogramma: ridotto alla scala del mosaico e ruotato come lo vede l'utente.</summary>
    private Homography Initial(CameraFrame frame) =>
        Homography.Scale(_options.MosaicScale) * Homography.Rotation(frame.RotationDegrees);

    /// <summary>
    /// Scarta omografie assurde: quadrilatero intrecciato, scala molto diversa dal riferimento, prospettiva estrema.
    /// Restituisce il motivo dello scarto, o null se l'omografia è plausibile.
    /// </summary>
    private static string? Implausible(Homography h, int width, int height, double referenceArea)
    {
        if (!h.IsFinite) return "not_finite";
        var quad = h.MapRect(width, height);
        if (!quad.IsConvex) return "not_convex";
        var area = h.AreaScaleAt(width / 2.0, height / 2.0);
        var ratio = area / referenceArea;
        if (ratio < 0.5 || ratio > 2.0) return $"area_ratio={ratio:0.00}";

        // i lati opposti non devono differire troppo (fotocamera quasi parallela al tavolo)
        var top = quad.A.DistanceTo(quad.B);
        var bottom = quad.D.DistanceTo(quad.C);
        var left = quad.A.DistanceTo(quad.D);
        var right = quad.B.DistanceTo(quad.C);
        if (top / bottom is not (> 0.5 and < 2)) return $"sides_h={top / bottom:0.00}";
        if (left / right is not (> 0.5 and < 2)) return $"sides_v={left / right:0.00}";
        return null;
    }

    private static PointD Map(Homography h, PointD p)
    {
        var (x, y) = h.Apply(p.X, p.Y);
        return new PointD(x, y);
    }
}

/// <summary>
/// Tracking con ARCore: la posa della fotocamera e il piano del tavolo danno direttamente l'omografia fotogramma → piano,
/// con scala metrica (pixel del mosaico per millimetro noti). Il primo fotogramma fissa l'orientamento del mosaico.
/// </summary>
public sealed class ArCorePlaneTracker : IMosaicTracker
{
    private readonly double _pxPerMm;
    private Homography? _alignment;

    public ArCorePlaneTracker(double pxPerMm = 2.0) => _pxPerMm = pxPerMm;

    public string Id => "arcore";

    public double PxPerMm => _pxPerMm;

    public TrackResult Process(CameraFrame frame)
    {
        var gray = ImageOps.ToGray(frame.Image, 640, out _);
        var sharpness = ImageOps.Sharpness(gray);
        var pose = frame.Pose;

        if (pose is null || !pose.CameraTracking)
            return new TrackResult(TrackStatus.Lost, null, sharpness, "Muovi il telefono lentamente sopra il tavolo", PxPerMm: _pxPerMm,
                Detail: PoseDetail(pose is null ? "no_pose" : "camera_not_tracking"));
        if (pose.Plane is null)
            return new TrackResult(TrackStatus.Initializing, null, sharpness, "Inquadra il tavolo finché viene riconosciuto", PxPerMm: _pxPerMm,
                Detail: PoseDetail("no_plane"));

        var imageToPlane = PlanarProjection.ImageToPlane(pose.Intrinsics, pose.Camera, pose.Plane.Value);
        if (imageToPlane is null)
            return new TrackResult(TrackStatus.Lost, null, sharpness, "Tieni il telefono puntato verso il tavolo", PxPerMm: _pxPerMm,
                Detail: PoseDetail("pointing_away"));

        // piano in metri → millimetri → pixel del mosaico
        var toMosaic = Homography.Scale(_pxPerMm * 1000) * imageToPlane.Value;
        _alignment ??= PlanarProjection.Alignment(toMosaic, frame.Image.Width, frame.Image.Height, frame.RotationDegrees);
        var h = _alignment.Value * toMosaic;

        return h.MapRect(frame.Image.Width, frame.Image.Height).IsConvex
            ? new TrackResult(TrackStatus.Tracking, h, sharpness, PxPerMm: _pxPerMm, Detail: PoseDetail("placed"))
            : new TrackResult(TrackStatus.Lost, null, sharpness, "Tieni il telefono più parallelo al tavolo", PxPerMm: _pxPerMm,
                Detail: PoseDetail("not_convex"));
    }

    private TrackDetail PoseDetail(string reason) => new(Id, 0, 0, 0, false, Array.Empty<CandidateAttempt>(), reason);

    public void CommitKeyframe(Homography frameToMosaic) { }
}

/// <summary>
/// Ibrido: la posa ARCore dà una stima robusta anche nei movimenti veloci, le feature la affinano al pixel contro i
/// fotogrammi chiave vicini (cuciture più pulite). Se l'affinamento si discosta troppo dalla posa si tiene la posa.
/// </summary>
public sealed class HybridMosaicTracker : IMosaicTracker
{
    private readonly ArCorePlaneTracker _pose;
    private readonly FeatureMosaicTracker _features;
    private readonly double _maxDeviation;

    /// <param name="maxDeviation">Scostamento massimo medio degli angoli, in frazione della diagonale del fotogramma proiettato.</param>
    public HybridMosaicTracker(ArCorePlaneTracker pose, FeatureMosaicTracker features, double maxDeviation = 0.08)
    {
        _pose = pose;
        _features = features;
        _maxDeviation = maxDeviation;
    }

    public string Id => "hybrid";

    public TrackResult Process(CameraFrame frame)
    {
        var coarse = _pose.Process(frame);
        if (coarse.FrameToMosaic is not { } prior) return coarse;

        var refined = _features.Process(frame, prior);
        var detail = refined.Detail! with { Source = Id };
        if (refined.FrameToMosaic is { } h)
        {
            var deviation = Deviation(h, prior, frame.Image.Width, frame.Image.Height);
            if (deviation <= _maxDeviation)
                return coarse with { FrameToMosaic = h, Inliers = refined.Inliers, Detail = detail with { Reason = "refined", Deviation = deviation } };
            return coarse with { Detail = detail with { Reason = "pose_only:deviation", Deviation = deviation } };
        }

        return coarse with { Detail = detail with { Reason = "pose_only:" + detail.Reason } };
    }

    public void CommitKeyframe(Homography frameToMosaic) => _features.CommitKeyframe(frameToMosaic);

    public static double Deviation(Homography a, Homography b, int width, int height)
    {
        var qa = a.MapRect(width, height);
        var qb = b.MapRect(width, height);
        var diagonal = qb.A.DistanceTo(qb.C);
        var mean = qa.Points.Zip(qb.Points, (p, q) => p.DistanceTo(q)).Average();
        return diagonal <= 0 ? double.MaxValue : mean / diagonal;
    }
}
