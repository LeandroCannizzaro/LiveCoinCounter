using System.Text.Json;
using System.Threading.Channels;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>
/// Salva i risultati dell'acquisizione: fotogrammi chiave e monete nel database, ritagli e (secondo l'archivio scelto)
/// immagini su disco. In modalità debug registra anche i fotogrammi (al massimo 3 al secondo) con la posa, per rigiocarli.
/// </summary>
public sealed class AppCaptureSink : ICaptureSink
{
    /// <summary>
    /// In modalità debug si registra (quasi) ogni fotogramma elaborato dal tracker: servono tutti per riprodurre il tracking
    /// sul PC e provare algoritmi diversi sugli stessi dati. A ~4 fotogrammi al secondo sono circa 1 MB al secondo.
    /// </summary>
    private const long DebugFrameIntervalNs = 50_000_000;

    private readonly SessionRecord _session;
    private readonly SessionRepository _repository;
    private readonly HashSet<int> _withCrop = new();
    private readonly Dictionary<int, DateTime> _detectedAt = new();
    private readonly Dictionary<int, CoinColor> _colors = new();
    private readonly Channel<CameraFrame>? _debugFrames;
    private readonly Task? _debugWriter;
    private long _lastDebugFrame = long.MinValue;
    private int _debugIndex;

    public AppCaptureSink(SessionRecord session, SessionRepository repository)
    {
        _session = session;
        _repository = repository;
        if (session.Retention == RetentionMode.DebugFrames)
        {
            _debugFrames = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });
            _debugWriter = Task.Run(WriteDebugFramesAsync);
        }
    }

    public async Task OnKeyframeAsync(int index, ArgbImage image, Homography frameToMosaic, double sharpness)
    {
        string? path = null;
        if (_session.Retention == RetentionMode.WithKeyframes)
        {
            path = SessionStorage.KeyframePath(_session.Id, index);
            await AndroidImaging.SaveJpegAsync(image, path, 90);
        }

        await _repository.AddKeyframeAsync(new KeyframeRecord
        {
            SessionId = _session.Id,
            Index = index,
            CapturedAt = DateTime.Now,
            Homography = SessionRepository.FormatHomography(frameToMosaic),
            Sharpness = sharpness,
            Width = image.Width,
            Height = image.Height,
            ImagePath = path
        });
    }

    public async Task OnCoinsAsync(IReadOnlyList<CoinChange> changes)
    {
        var records = new List<CoinRecord>();
        foreach (var change in changes)
        {
            var t = change.Track;
            if (change.Crop is not null)
            {
                await AndroidImaging.SaveJpegAsync(change.Crop, SessionStorage.CropPath(_session.Id, t.Id), 92);
                _withCrop.Add(t.Id);
            }
            if (!_detectedAt.ContainsKey(t.Id)) _detectedAt[t.Id] = DateTime.Now;
            if (change.Color is { } color) _colors[t.Id] = color;     // il colore è quello del ritaglio salvato
            _colors.TryGetValue(t.Id, out var coinColor);

            records.Add(new CoinRecord
            {
                Number = t.Id,
                X = t.MosaicBox.X,
                Y = t.MosaicBox.Y,
                Width = t.MosaicBox.Width,
                Height = t.MosaicBox.Height,
                DetectionConfidence = t.DetectionConfidence,
                Observations = t.Observations,
                SourceKeyframe = t.BestKeyframe,
                CropPath = _withCrop.Contains(t.Id) ? SessionStorage.CropPath(_session.Id, t.Id) : null,
                State = CoinState.Detected,
                DetectorCents = t.DetectorCents,
                DetectorClassConfidence = t.DetectorClassConfidence,
                DetectedAt = _detectedAt[t.Id],
                ColorTint = coinColor?.Guess,
                ColorHue = coinColor?.CoreHue ?? 0,
                ColorSat = coinColor?.CoreSat ?? 0
            });
        }
        await _repository.UpsertCoinsAsync(_session.Id, records);
    }

    public void OnFrame(CameraFrame frame, TrackResult result)
    {
        if (_debugFrames is null) return;
        if (_lastDebugFrame != long.MinValue && frame.TimestampNs - _lastDebugFrame < DebugFrameIntervalNs) return;
        _lastDebugFrame = frame.TimestampNs;
        _debugFrames.Writer.TryWrite(frame);
    }

    public async Task FlushAsync()
    {
        _debugFrames?.Writer.TryComplete();
        if (_debugWriter is not null) await _debugWriter;
    }

    private async Task WriteDebugFramesAsync()
    {
        var folder = SessionStorage.FramesFolder(_session.Id);
        Directory.CreateDirectory(folder);
        await using var log = new StreamWriter(Path.Combine(folder, "frames.jsonl"), append: true);
        await foreach (var frame in _debugFrames!.Reader.ReadAllAsync())
        {
            try
            {
                var name = $"{_debugIndex++:D5}.jpg";
                await AndroidImaging.SaveJpegAsync(frame.Image, Path.Combine(folder, name), 85);
                await log.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    File = name, frame.TimestampNs, frame.RotationDegrees,
                    Pose = frame.Pose is null ? null : new
                    {
                        frame.Pose.Intrinsics, frame.Pose.Camera, frame.Pose.Plane, frame.Pose.CameraTracking
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Registrazione fotogramma non riuscita: {ex.Message}");
            }
        }
    }
}
