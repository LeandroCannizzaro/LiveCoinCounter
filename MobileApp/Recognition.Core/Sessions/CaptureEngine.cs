using System.Diagnostics;
using System.Threading.Channels;
using LiveCoinCounter.Diagnostics;
using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Sessions;

public sealed record CaptureOptions
{
    /// <summary>Un fotogramma diventa chiave quando la parte già coperta del mosaico scende sotto questa frazione.</summary>
    public double KeyframeMaxCoverage { get; init; } = 0.65;

    /// <summary>Nitidezza minima rispetto alla mediana recente: i fotogrammi mossi non entrano nel mosaico.</summary>
    public double MinRelativeSharpness { get; init; } = 0.55;
    /// <summary>
    /// Nitidezza minima di ogni fotogramma chiave. I valori sono tarati sulle prove sul campo (10 sessioni): i fotogrammi a
    /// fuoco misurano 160–240, quelli sfocati 13–39.
    /// </summary>
    public double MinAbsoluteSharpness { get; init; } = 30;

    /// <summary>Nitidezza minima del primo fotogramma chiave: è la base di tutto il mosaico, non può essere sfocato.</summary>
    public double FirstKeyframeMinSharpness { get; init; } = 60;

    /// <summary>Nitidezza a cui un fotogramma pesa al massimo nel mosaico (assoluta: un fotogramma più nitido sostituisce uno sfocato).</summary>
    public double SharpnessReference { get; init; } = 150;

    /// <summary>
    /// Inlier minimi per comporre un fotogramma chiave nel mosaico (solo tracker a punti caratteristici; non vale per il primo).
    /// Un aggancio debole (nella prova sul campo: 26 inlier) mette nel mosaico un fotogramma storto e si vede la cucitura;
    /// i fotogrammi di aggancio del tracker intanto tengono il tracking, e il fotogramma buono arriva poco dopo.
    /// </summary>
    public int MinKeyframeInliers { get; init; } = 60;

    /// <summary>Sotto questa nitidezza mediana (ultimi fotogrammi) si suggerisce di allontanarsi / rimettere a fuoco.</summary>
    public double BlurryBelow { get; init; } = 45;

    /// <summary>Sotto questa luminosità media (0–255) si suggerisce la torcia.</summary>
    public double DarkBelow { get; init; } = 55;

    /// <summary>Lato del ritaglio salvato per ogni moneta.</summary>
    public int CropSize { get; init; } = 256;

    /// <summary>Lato massimo di una foto importata quando diventa il mosaico.</summary>
    public int StillMosaicMaxSide { get; init; } = 2048;
}

/// <summary>
/// Stato del tracking per l'interfaccia. <paramref name="LastKnownQuad"/> è l'ultima zona in cui il fotogramma è stato
/// collocato: quando il tracking è perso indica all'operatore dove tornare.
/// </summary>
public sealed record TrackingUpdate(TrackStatus Status, string? Hint, Quad? FrameQuad, int Keyframes, int PendingDetections, bool MosaicFull,
    Quad? LastKnownQuad = null, bool Blurry = false, bool Dark = false);

/// <summary>Una moneta da salvare: <see cref="Crop"/> è valorizzato quando c'è un ritaglio nuovo o migliore.</summary>
public sealed record CoinChange(CoinTrack Track, bool IsNew, ArgbImage? Crop, CoinColor? Color = null);

/// <summary>Dove finiscono i risultati dell'acquisizione: database e file (lo implementa l'app).</summary>
public interface ICaptureSink
{
    Task OnKeyframeAsync(int index, ArgbImage image, Homography frameToMosaic, double sharpness);
    Task OnCoinsAsync(IReadOnlyList<CoinChange> changes);

    /// <summary>Ogni fotogramma elaborato, per la registrazione in modalità debug. Deve tornare subito.</summary>
    void OnFrame(CameraFrame frame, TrackResult result) { }
}

/// <summary>
/// Il motore di una sessione live, con due thread separati:
/// 1) tracking: colloca i fotogrammi, sceglie i fotogrammi chiave, compone il mosaico;
/// 2) rilevamento: su ogni fotogramma chiave trova le monete (solo localizzazione), le deduplica nel mosaico e salva i ritagli.
/// Il riconoscimento del valore avviene dopo lo stop (<see cref="SessionClassifier"/>).
/// </summary>
public sealed class CaptureEngine
{
    private sealed record PendingKeyframe(int Index, ArgbImage Image, Homography FrameToMosaic, float Quality);

    private readonly IMosaicTracker _tracker;
    private readonly ICoinLocator _locator;
    private readonly ICaptureSink _sink;
    private readonly CaptureOptions _options;
    private readonly Channel<CameraFrame> _frames = Channel.CreateBounded<CameraFrame>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<PendingKeyframe> _keyframes = Channel.CreateUnbounded<PendingKeyframe>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CoverageGrid _coverage = new();
    private readonly Queue<double> _recentSharpness = new();
    private Task? _trackingTask;
    private Task? _detectionTask;
    private int _keyframeCount;
    private int _pending;
    private Quad? _lastKnownQuad;
    private volatile bool _busy;
    private readonly IDiagLog _log;

    // contatori per il registro diagnostico (statistiche ogni 2 secondi)
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private long _pushed, _skipped, _processed, _trackTicks, _detectTicks, _detections;
    private long _statsPushed, _statsSkipped, _statsProcessed, _statsTrackTicks, _statsDetectTicks, _statsDetections;
    private long _lastStatsMs;
    private int _framesSeen;
    private double _brightnessAverage;

    public CaptureEngine(IMosaicTracker tracker, ICoinLocator locator, ICaptureSink sink, MosaicCanvas canvas, CaptureOptions? options = null,
        IDiagLog? log = null)
    {
        _tracker = tracker;
        _locator = locator;
        _sink = sink;
        Canvas = canvas;
        _options = options ?? new CaptureOptions();
        _log = log ?? NullDiagLog.Instance;
    }

    public MosaicCanvas Canvas { get; }
    public CoinRegistry Coins { get; } = new();
    public int KeyframeCount => _keyframeCount;

    /// <summary>Scala del mosaico in pixel/mm, quando il tracker la conosce (ARCore).</summary>
    public double? PxPerMm { get; private set; }

    /// <summary>Vero se il thread di tracking è libero: chi produce i fotogrammi può saltare la conversione quando è occupato.</summary>
    public bool WantsFrame
    {
        get
        {
            var wants = !_busy && _frames.Reader.Count == 0;
            if (!wants) Interlocked.Increment(ref _skipped);
            return wants;
        }
    }

    public event Action<TrackingUpdate>? TrackingUpdated;
    public event Action<IReadOnlyList<TileKey>>? MosaicChanged;
    public event Action<IReadOnlyList<CoinTrack>>? CoinsChanged;
    public event Action<Exception>? Error;

    public void Start()
    {
        _log.Event("engine_start", new { tracker = _tracker.Id, locator = _locator.Id, options = _options }, important: true);
        _trackingTask ??= Task.Run(TrackingLoopAsync);
        _detectionTask ??= Task.Run(DetectionLoopAsync);
    }

    /// <summary>Consegna un fotogramma; se il tracking è indietro il fotogramma precedente in attesa viene scartato.</summary>
    public bool PushFrame(CameraFrame frame)
    {
        Interlocked.Increment(ref _pushed);
        return _frames.Writer.TryWrite(frame);
    }

    /// <summary>Una foto importata: diventa da sola il mosaico (un solo fotogramma chiave).</summary>
    public async Task AddStillAsync(ArgbImage image)
    {
        var scale = Math.Min(1.0, _options.StillMosaicMaxSide / (double)Math.Max(image.Width, image.Height));
        var h = Homography.Scale(scale);
        await AddKeyframeAsync(image, h, quality: 1, sharpness: 0);
        TrackingUpdated?.Invoke(new TrackingUpdate(TrackStatus.Tracking, null, h.MapRect(image.Width, image.Height), _keyframeCount, _pending, Canvas.IsFull));
    }

    /// <summary>Ferma l'acquisizione e aspetta che il rilevamento abbia elaborato tutti i fotogrammi chiave rimasti.</summary>
    public async Task StopAsync()
    {
        _frames.Writer.TryComplete();
        if (_trackingTask is not null) await _trackingTask;
        _keyframes.Writer.TryComplete();
        if (_detectionTask is not null) await _detectionTask;
        _log.Event("engine_stop", new
        {
            pushed = _pushed, skipped = _skipped, processed = _processed, keyframes = _keyframeCount, coins = Coins.Count,
            tiles = Canvas.TileCount, bounds = DiagData.Box(Canvas.Bounds), full = Canvas.IsFull
        }, important: true);
    }

    private async Task TrackingLoopAsync()
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync())
        {
            _busy = true;
            try
            {
                await ProcessFrameAsync(frame);
            }
            catch (Exception ex)
            {
                _log.Event("error", new { where = "tracking", message = ex.Message, stack = ex.ToString() }, important: true);
                Error?.Invoke(ex);
            }
            finally
            {
                _busy = false;
            }
        }
    }

    private async Task ProcessFrameAsync(CameraFrame frame)
    {
        var timer = Stopwatch.StartNew();
        var result = _tracker.Process(frame);
        var trackMs = timer.Elapsed.TotalMilliseconds;
        Interlocked.Add(ref _trackTicks, timer.ElapsedTicks);
        Interlocked.Increment(ref _processed);
        _sink.OnFrame(frame, result);
        if (result.PxPerMm is { } pxPerMm) PxPerMm = pxPerMm;

        var hint = result.Hint;
        Quad? quad = null;
        var decision = result.FrameToMosaic is null ? "not_placed" : "no_new_area";
        double? coverage = null;

        // nitidezza e luce di tutti i fotogrammi, anche quelli non collocati: servono ai suggerimenti all'operatore
        var median = MedianSharpness(result.Sharpness);
        var brightness = ImageOps.MeanBrightness(frame.Image);
        _brightnessAverage = _framesSeen == 0 ? brightness : _brightnessAverage * 0.85 + brightness * 0.15;
        var warmedUp = _framesSeen >= 8;
        var dark = warmedUp && _brightnessAverage < _options.DarkBelow;
        var blurry = warmedUp && median > 0 && median < _options.BlurryBelow;

        if (result.FrameToMosaic is { } h)
        {
            quad = h.MapRect(frame.Image.Width, frame.Image.Height);
            _lastKnownQuad = quad;
            var sharpEnough = result.Sharpness >= _options.MinAbsoluteSharpness &&
                              (median <= 0 || result.Sharpness >= median * _options.MinRelativeSharpness);
            coverage = _coverage.CoveredFraction(quad.Value);
            var newArea = coverage < _options.KeyframeMaxCoverage;

            // il primo fotogramma chiave fissa orientamento e scala di tutto il mosaico: deve essere nitido
            var firstBlurry = false;
            if (_keyframeCount == 0 && result.Sharpness < _options.FirstKeyframeMinSharpness)
            {
                sharpEnough = false;
                firstBlurry = true;
                hint ??= "Immagine sfocata o troppo scura: tieni fermo il telefono su una zona con le monete, aggiungi luce";
            }

            var weakAlignment = _keyframeCount > 0 && result.Detail?.Source.StartsWith("features", StringComparison.Ordinal) == true &&
                                result.Inliers < _options.MinKeyframeInliers;

            if (newArea && sharpEnough && weakAlignment)
            {
                decision = "weak_alignment";
            }
            else if (newArea && sharpEnough && !Canvas.IsFull)
            {
                decision = "keyframe";
                _tracker.CommitKeyframe(h);
                _coverage.Mark(quad.Value);
                // peso assoluto: un fotogramma nitido sostituisce nel mosaico uno sfocato anche se tutti i precedenti erano sfocati
                var quality = (float)Math.Clamp(result.Sharpness / _options.SharpnessReference, 0.2, 1);
                await AddKeyframeAsync(frame.Image, h, quality, result.Sharpness);
            }
            else if (newArea && !sharpEnough)
            {
                decision = firstBlurry ? "first_blurry" : "blurry";
                hint ??= "Più lentamente: l'immagine è mossa";
            }
            else if (newArea && Canvas.IsFull)
            {
                decision = "mosaic_full";
            }
        }

        // la causa più probabile prima: con poca luce l'otturatore resta aperto più a lungo e l'immagine si sfoca
        if (dark && result.Status != TrackStatus.Lost) hint = "Poca luce: accendi la torcia 🔦 per immagini più nitide";
        else if (hint is null && blurry) hint = "Immagine sfocata: allontana il telefono (25–40 cm) e tienilo fermo";

        if (Canvas.IsFull) hint = "Superficie massima raggiunta: ferma l'acquisizione";
        TrackingUpdated?.Invoke(new TrackingUpdate(result.Status, hint, quad, _keyframeCount, _pending, Canvas.IsFull, _lastKnownQuad, blurry, dark));

        if (_log.Enabled)
        {
            _log.Event("frame", new
            {
                n = _framesSeen,
                ts = frame.TimestampNs / 1_000_000,
                w = frame.Image.Width, h = frame.Image.Height, rot = frame.RotationDegrees,
                status = result.Status.ToString(),
                decision,
                sharp = DiagData.R(result.Sharpness), median = DiagData.R(median), light = DiagData.R(brightness, 0),
                cover = coverage is { } c ? DiagData.R(c, 2) : (double?)null,
                inliers = result.Inliers,
                ms = DiagData.R(trackMs),
                quad = quad is { } q ? DiagData.Quad(q) : null,
                hint,
                pose = frame.Pose is null ? null : new { tracking = frame.Pose.CameraTracking, plane = frame.Pose.Plane is not null },
                detail = result.Detail
            });
            LogStatsIfDue();
        }
        _framesSeen++;
    }

    private void LogStatsIfDue()
    {
        var now = _statsClock.ElapsedMilliseconds;
        if (now - _lastStatsMs < 2000) return;
        var seconds = Math.Max(0.001, (now - _lastStatsMs) / 1000.0);
        _lastStatsMs = now;

        long Delta(ref long current, ref long previous)
        {
            var value = Interlocked.Read(ref current);
            var d = value - previous;
            previous = value;
            return d;
        }

        var pushed = Delta(ref _pushed, ref _statsPushed);
        var skipped = Delta(ref _skipped, ref _statsSkipped);
        var processed = Delta(ref _processed, ref _statsProcessed);
        var trackTicks = Delta(ref _trackTicks, ref _statsTrackTicks);
        var detectTicks = Delta(ref _detectTicks, ref _statsDetectTicks);
        var detections = Delta(ref _detections, ref _statsDetections);
        _log.Event("stats", new
        {
            fpsIn = DiagData.R(pushed / seconds), fpsProcessed = DiagData.R(processed / seconds), skippedBusy = skipped,
            avgTrackMs = processed == 0 ? 0 : DiagData.R(trackTicks * 1000.0 / Stopwatch.Frequency / processed),
            avgDetectMs = detections == 0 ? 0 : DiagData.R(detectTicks * 1000.0 / Stopwatch.Frequency / detections),
            pending = _pending, keyframes = _keyframeCount, coins = Coins.Count, tiles = Canvas.TileCount,
            managedMb = DiagData.R(GC.GetTotalMemory(false) / 1048576.0), gc2 = GC.CollectionCount(2)
        });
    }

    private async Task AddKeyframeAsync(ArgbImage image, Homography h, float quality, double sharpness)
    {
        var index = _keyframeCount++;
        var changed = Canvas.Compose(image, h, quality);
        _log.Event("keyframe", new
        {
            index, h = DiagData.Homography(h), quad = DiagData.Quad(h.MapRect(image.Width, image.Height)),
            sharp = DiagData.R(sharpness), quality = DiagData.R(quality, 2), changedTiles = changed.Count, tiles = Canvas.TileCount,
            bounds = DiagData.Box(Canvas.Bounds), full = Canvas.IsFull
        });
        if (changed.Count > 0) MosaicChanged?.Invoke(changed);
        Interlocked.Increment(ref _pending);
        await _keyframes.Writer.WriteAsync(new PendingKeyframe(index, image, h, quality));
        try
        {
            await _sink.OnKeyframeAsync(index, image, h, sharpness);
        }
        catch (Exception ex)
        {
            _log.Event("error", new { where = "keyframe_sink", message = ex.Message, stack = ex.ToString() }, important: true);
            Error?.Invoke(ex);
        }
    }

    private double MedianSharpness(double value)
    {
        _recentSharpness.Enqueue(value);
        while (_recentSharpness.Count > 30) _recentSharpness.Dequeue();
        var sorted = _recentSharpness.OrderBy(v => v).ToList();
        return sorted.Count < 5 ? 0 : sorted[sorted.Count / 2];
    }

    private async Task DetectionLoopAsync()
    {
        await foreach (var keyframe in _keyframes.Reader.ReadAllAsync())
        {
            try
            {
                await DetectAsync(keyframe);
            }
            catch (Exception ex)
            {
                _log.Event("error", new { where = "detection", keyframe = keyframe.Index, message = ex.Message, stack = ex.ToString() }, important: true);
                Error?.Invoke(ex);
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private async Task DetectAsync(PendingKeyframe keyframe)
    {
        var timer = Stopwatch.StartNew();
        var image = keyframe.Image;
        var boxes = await _locator.LocateAsync(image);
        var locateMs = timer.Elapsed.TotalMilliseconds;
        var changes = new List<CoinChange>();
        var logged = _log.Enabled ? new List<object>() : null;

        foreach (var coin in boxes)
        {
            if (!DetectionFilter.IsUsable(coin.Box, image.Width, image.Height))
            {
                logged?.Add(new { box = DiagData.Box(coin.Box), conf = DiagData.R(coin.Confidence, 2), cents = coin.Cents, use = DetectionFilter.Reason(coin.Box, image.Width, image.Height) });
                continue;
            }
            var mosaicBox = keyframe.FrameToMosaic.MapBox(coin.Box);
            var quality = DetectionFilter.Centrality(coin.Box, image.Width, image.Height) * keyframe.Quality;
            var update = Coins.Observe(new CoinObservation(coin.Box, mosaicBox, coin.Confidence, quality, keyframe.Index,
                coin.Cents, coin.ClassConfidence));
            var crop = update.IsBetter ? ImageOps.CropSquare(image, coin.Box, _options.CropSize) : null;
            var color = crop is not null ? CoinColorAnalysis.Analyze(crop) : null;
            changes.Add(new CoinChange(update.Track, update.IsNew, crop, color));
            logged?.Add(new
            {
                color = color is null ? null : new { color.CoreHue, color.CoreSat, color.CoreVal, color.RingHue, color.RingSat, color.RingVal, guess = color.Guess },
                box = DiagData.Box(coin.Box), mosaic = DiagData.Box(mosaicBox), conf = DiagData.R(coin.Confidence, 2), cents = coin.Cents,
                classConf = DiagData.R(coin.ClassConfidence, 2), use = "ok", track = update.Track.Id, isNew = update.IsNew,
                observations = update.Track.Observations, better = update.IsBetter, quality = DiagData.R(quality, 2)
            });
        }

        Interlocked.Add(ref _detectTicks, timer.ElapsedTicks);
        Interlocked.Increment(ref _detections);
        _log.Event("detect", new
        {
            keyframe = keyframe.Index, locateMs = DiagData.R(locateMs), totalMs = DiagData.R(timer.Elapsed.TotalMilliseconds),
            raw = boxes.Count, accepted = changes.Count, tracks = Coins.Count, boxes = logged
        });

        if (changes.Count == 0) return;
        await _sink.OnCoinsAsync(changes);
        CoinsChanged?.Invoke(Coins.Tracks);
    }
}
