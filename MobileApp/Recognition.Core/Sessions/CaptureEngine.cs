using System.Threading.Channels;
using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Sessions;

public sealed record CaptureOptions
{
    /// <summary>Un fotogramma diventa chiave quando la parte già coperta del mosaico scende sotto questa frazione.</summary>
    public double KeyframeMaxCoverage { get; init; } = 0.65;

    /// <summary>Nitidezza minima rispetto alla mediana recente: i fotogrammi mossi non entrano nel mosaico.</summary>
    public double MinRelativeSharpness { get; init; } = 0.55;
    public double MinAbsoluteSharpness { get; init; } = 12;

    /// <summary>Nitidezza minima del primo fotogramma chiave: è la base di tutto il mosaico, non può essere sfocato.</summary>
    public double FirstKeyframeMinSharpness { get; init; } = 20;

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
    Quad? LastKnownQuad = null);

/// <summary>Una moneta da salvare: <see cref="Crop"/> è valorizzato quando c'è un ritaglio nuovo o migliore.</summary>
public sealed record CoinChange(CoinTrack Track, bool IsNew, ArgbImage? Crop);

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

    public CaptureEngine(IMosaicTracker tracker, ICoinLocator locator, ICaptureSink sink, MosaicCanvas canvas, CaptureOptions? options = null)
    {
        _tracker = tracker;
        _locator = locator;
        _sink = sink;
        Canvas = canvas;
        _options = options ?? new CaptureOptions();
    }

    public MosaicCanvas Canvas { get; }
    public CoinRegistry Coins { get; } = new();
    public int KeyframeCount => _keyframeCount;

    /// <summary>Scala del mosaico in pixel/mm, quando il tracker la conosce (ARCore).</summary>
    public double? PxPerMm { get; private set; }

    /// <summary>Vero se il thread di tracking è libero: chi produce i fotogrammi può saltare la conversione quando è occupato.</summary>
    public bool WantsFrame => !_busy && _frames.Reader.Count == 0;

    public event Action<TrackingUpdate>? TrackingUpdated;
    public event Action<IReadOnlyList<TileKey>>? MosaicChanged;
    public event Action<IReadOnlyList<CoinTrack>>? CoinsChanged;
    public event Action<Exception>? Error;

    public void Start()
    {
        _trackingTask ??= Task.Run(TrackingLoopAsync);
        _detectionTask ??= Task.Run(DetectionLoopAsync);
    }

    /// <summary>Consegna un fotogramma; se il tracking è indietro il fotogramma precedente in attesa viene scartato.</summary>
    public bool PushFrame(CameraFrame frame) => _frames.Writer.TryWrite(frame);

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
        var result = _tracker.Process(frame);
        _sink.OnFrame(frame, result);
        if (result.PxPerMm is { } pxPerMm) PxPerMm = pxPerMm;

        var hint = result.Hint;
        Quad? quad = null;

        if (result.FrameToMosaic is { } h)
        {
            quad = h.MapRect(frame.Image.Width, frame.Image.Height);
            _lastKnownQuad = quad;
            var median = MedianSharpness(result.Sharpness);
            var sharpEnough = result.Sharpness >= _options.MinAbsoluteSharpness &&
                              (median <= 0 || result.Sharpness >= median * _options.MinRelativeSharpness);
            var newArea = _coverage.CoveredFraction(quad.Value) < _options.KeyframeMaxCoverage;

            // il primo fotogramma chiave fissa orientamento e scala di tutto il mosaico: deve essere nitido
            if (_keyframeCount == 0 && result.Sharpness < _options.FirstKeyframeMinSharpness)
            {
                sharpEnough = false;
                hint ??= "Immagine sfocata o troppo scura: tieni fermo il telefono su una zona con le monete, aggiungi luce";
            }

            if (newArea && sharpEnough && !Canvas.IsFull)
            {
                _tracker.CommitKeyframe(h);
                _coverage.Mark(quad.Value);
                var quality = (float)Math.Clamp(median > 0 ? result.Sharpness / (2 * median) : 1, 0.2, 1);
                await AddKeyframeAsync(frame.Image, h, quality, result.Sharpness);
            }
            else if (newArea && !sharpEnough)
            {
                hint ??= "Più lentamente: l'immagine è mossa";
            }
        }

        if (Canvas.IsFull) hint = "Superficie massima raggiunta: ferma l'acquisizione";
        TrackingUpdated?.Invoke(new TrackingUpdate(result.Status, hint, quad, _keyframeCount, _pending, Canvas.IsFull, _lastKnownQuad));
    }

    private async Task AddKeyframeAsync(ArgbImage image, Homography h, float quality, double sharpness)
    {
        var index = _keyframeCount++;
        var changed = Canvas.Compose(image, h, quality);
        if (changed.Count > 0) MosaicChanged?.Invoke(changed);
        Interlocked.Increment(ref _pending);
        await _keyframes.Writer.WriteAsync(new PendingKeyframe(index, image, h, quality));
        try
        {
            await _sink.OnKeyframeAsync(index, image, h, sharpness);
        }
        catch (Exception ex)
        {
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
        var image = keyframe.Image;
        var boxes = await _locator.LocateAsync(image);
        var changes = new List<CoinChange>();

        foreach (var coin in boxes)
        {
            if (!DetectionFilter.IsUsable(coin.Box, image.Width, image.Height)) continue;
            var mosaicBox = keyframe.FrameToMosaic.MapBox(coin.Box);
            var quality = DetectionFilter.Centrality(coin.Box, image.Width, image.Height) * keyframe.Quality;
            var update = Coins.Observe(new CoinObservation(coin.Box, mosaicBox, coin.Confidence, quality, keyframe.Index,
                coin.Cents, coin.ClassConfidence));
            var crop = update.IsBetter ? ImageOps.CropSquare(image, coin.Box, _options.CropSize) : null;
            changes.Add(new CoinChange(update.Track, update.IsNew, crop));
        }

        if (changes.Count == 0) return;
        await _sink.OnCoinsAsync(changes);
        CoinsChanged?.Invoke(Coins.Tracks);
    }
}
