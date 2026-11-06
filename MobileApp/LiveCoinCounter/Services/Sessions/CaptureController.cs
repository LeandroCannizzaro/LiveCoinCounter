using LiveCoinCounter.Diagnostics;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>
/// Una sessione dall'inizio alla fine: crea la riga nel database, avvia fotocamera e motore, allo stop salva il mosaico,
/// applica l'archivio scelto e passa le monete al riconoscitore (sul telefono, poi l'AI secondo l'impostazione).
/// </summary>
public sealed class CaptureController
{
    private readonly IFrameSource? _source;
    private readonly AppCaptureSink _sink;
    private readonly JsonlDiagLog _log;

    private CaptureController(SessionRecord session, CaptureEngine engine, IFrameSource? source, AppCaptureSink sink, string? note, JsonlDiagLog log)
    {
        Session = session;
        Engine = engine;
        _source = source;
        _sink = sink;
        Note = note;
        _log = log;
    }

    public SessionRecord Session { get; }
    public CaptureEngine Engine { get; }

    /// <summary>Registro diagnostico della sessione: la schermata vi aggiunge i propri eventi (pulsanti, suggerimenti).</summary>
    public IDiagLog Log => _log;

    /// <summary>L'ultimo fotogramma arrivato dalla fotocamera (per l'anteprima).</summary>
    public CameraFrame? LatestFrame { get; private set; }

    /// <summary>La fotocamera in uso ha la torcia (solo CameraX; ARCore la gestisce da sé).</summary>
    public bool SupportsTorch => _source?.SupportsTorch == true;

    public void SetTorch(bool on)
    {
        _source?.SetTorch(on);
        _log.Event("torch", new { on }, important: true);
    }

    /// <summary>Rimette a fuoco al centro; chi chiama limita la frequenza.</summary>
    public void Refocus()
    {
        _source?.Refocus();
        _log.Event("refocus");
    }

    /// <summary>Avviso sul metodo effettivamente usato (es. ARCore non supportato).</summary>
    public string? Note { get; }

    public static async Task<CaptureController> StartLiveAsync()
    {
        var setup = SessionServices.CreateCaptureSetup(SettingsService.CaptureMethod);
        var locator = SessionServices.CreateLocator();
        var repository = SessionServices.Repository;
        var session = await repository.CreateSessionAsync(SessionSource.Live, setup.Tracker.Id, locator.Id, SettingsService.Retention, DateTime.Now);

        var log = SessionDiagnostics.Open(session.Id, "live");
        log.Event("session_start", new
        {
            session = session.Id, source = "live", requestedMethod = SettingsService.CaptureMethod, effectiveMethod = setup.Method,
            note = setup.Note, tracker = setup.Tracker.Id, frameSource = setup.Source.Id, locator = locator.Id
        }, important: true);

        var sink = new AppCaptureSink(session, repository);
        var engine = new CaptureEngine(setup.Tracker, locator, sink, new MosaicCanvas(), log: log);
        var controller = new CaptureController(session, engine, setup.Source, sink, setup.Note, log);
        engine.Start();

        try
        {
            await setup.Source.StartAsync(frame =>
            {
                controller.LatestFrame = frame;
                engine.PushFrame(frame);
            }, () => engine.WantsFrame);
        }
        catch (Exception ex)
        {
            log.Event("error", new { where = "camera_start", message = ex.Message, stack = ex.ToString() }, important: true);
            await engine.StopAsync();
            await log.DisposeAsync();
            await SessionServices.DeleteSessionAsync(session.Id);
            throw;
        }
        return controller;
    }

    /// <summary>Una foto dalla galleria: stessa pipeline, un solo fotogramma chiave.</summary>
    public static async Task<CaptureController> StartPhotoAsync(byte[] photo)
    {
        var locator = SessionServices.CreateLocator();
        var repository = SessionServices.Repository;
        var retention = SettingsService.Retention == RetentionMode.DebugFrames ? RetentionMode.MosaicAndCrops : SettingsService.Retention;
        var session = await repository.CreateSessionAsync(SessionSource.Photo, "photo", locator.Id, retention, DateTime.Now);

        var log = SessionDiagnostics.Open(session.Id, "photo");
        log.Event("session_start", new { session = session.Id, source = "photo", locator = locator.Id, bytes = photo.Length }, important: true);
        var sink = new AppCaptureSink(session, repository);
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite()), locator, sink, new MosaicCanvas(), log: log);
        var controller = new CaptureController(session, engine, null, sink, null, log);
        engine.Start();
        var image = await Task.Run(() => AndroidImaging.Decode(photo, 3000));
        await engine.AddStillAsync(image);
        return controller;
    }

    /// <summary>Ferma la fotocamera, aspetta il rilevamento delle ultime monete e salva il mosaico.</summary>
    public async Task StopAsync()
    {
        if (_source is not null) await _source.StopAsync();
        await Engine.StopAsync();
        await _sink.FlushAsync();

        var repository = SessionServices.Repository;
        var session = await repository.GetSessionAsync(Session.Id) ?? Session;
        // la foto reale si salva sempre; il file mostrato è il piano di colore con le monete (impostazione "Superficie acquisita")
        var coinBoxes = Engine.Coins.Tracks.Select(t => t.MosaicBox).ToList();
        var mosaic = await Task.Run(() => Engine.Canvas.Render(4096, out var scale, out var origin) is { } image
            ? (image, scale, origin)
            : ((ArgbImage, float, PointD)?)null);
        if (mosaic is { } m)
        {
            // tre viste, tutte salvate: la schermata a tutto schermo le alterna senza rifare nulla
            var plane = MosaicCanvas.DefaultSurfaceColor;
            var continuous = await Task.Run(() => Engine.Canvas.Render(4096, out _, out _, plane, coinBoxes, fillGaps: true));
            var frames = await Task.Run(() => Engine.Canvas.Render(4096, out _, out _, plane, coinBoxes, fillGaps: false));
            await AndroidImaging.SaveJpegAsync(m.Item1, SessionStorage.MosaicPhotoPath(session.Id), 88);
            await AndroidImaging.SaveJpegAsync(continuous ?? m.Item1, SessionStorage.MosaicSurfacePath(session.Id), 90);
            await AndroidImaging.SaveJpegAsync(frames ?? m.Item1, SessionStorage.MosaicFramesPath(session.Id), 90);
            var chosen = SettingsService.SurfaceLook switch
            {
                SurfaceLook.Photo => m.Item1,
                SurfaceLook.Frames => frames ?? m.Item1,
                _ => continuous ?? m.Item1
            };
            await AndroidImaging.SaveJpegAsync(chosen, SessionStorage.MosaicPath(session.Id), 90);
            session.MosaicPreviewPath = SessionStorage.MosaicPath(session.Id);
            session.PreviewScale = m.Item2;
            session.PreviewOriginX = m.Item3.X;
            session.PreviewOriginY = m.Item3.Y;
        }

        var bounds = Engine.Canvas.Bounds;
        session.MosaicWidth = (int)bounds.Width;
        session.MosaicHeight = (int)bounds.Height;
        session.KeyframeCount = Engine.KeyframeCount;
        session.PxPerMm = Engine.PxPerMm;
        session.EndedAt = DateTime.Now;
        session.Status = SessionStatus.Classifying;
        await repository.UpdateSessionAsync(session);
        SessionStorage.ApplyRetention(session);
        if (session.Retention != RetentionMode.WithKeyframes) await repository.ClearKeyframeImagesAsync(session.Id);

        _log.Event("session_end", new
        {
            session = session.Id, durationS = (session.EndedAt - session.StartedAt)?.TotalSeconds, keyframes = session.KeyframeCount,
            coins = Engine.Coins.Count, mosaic = new { session.MosaicWidth, session.MosaicHeight }, session.PxPerMm, dropped = _log.Dropped
        }, important: true);
        await _log.DisposeAsync();
        await SessionServices.WriteSnapshotAsync(session.Id);
    }
}

/// <summary>Riconoscimento del valore dopo lo stop, secondo l'impostazione "Riconoscimento dopo lo stop".</summary>
public static class SessionProcessing
{
    public static async Task ClassifyAsync(string sessionId, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        await using (var log = SessionDiagnostics.Open(sessionId, "classify"))
        {
            var classifier = new SessionClassifier(SessionServices.Repository, SessionServices.LoadCropAsync, log: log);
            await classifier.ClassifyAsync(sessionId, SessionServices.CreateOnDeviceClassifier(), progress, ct);
        }
        await SessionServices.WriteSnapshotAsync(sessionId);

        if (SettingsService.AiMode == AiMode.AiAutoUncertain && HasNetwork())
        {
            var session = await SessionServices.Repository.GetSessionAsync(sessionId);
            if (session is { UncertainCount: > 0 })
                await RetryUncertainWithAiAsync(sessionId, progress, ct);
        }
    }

    /// <summary>Solo le monete incerte, solo i loro ritagli: l'AI non vede mai mosaico né fotogrammi.</summary>
    public static async Task<int> RetryUncertainWithAiAsync(string sessionId, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (SettingsService.AiMode == AiMode.OfflineOnly) return 0;
        var ai = await SessionServices.CreateAiClassifierAsync();
        await using var log = SessionDiagnostics.Open(sessionId, "classify_ai");
        var classifier = new SessionClassifier(SessionServices.Repository, SessionServices.LoadCropAsync, log: log);
        var count = await classifier.ClassifyAsync(sessionId, ai, progress, ct, onlyUncertain: true, replace: true);
        await SessionServices.WriteSnapshotAsync(sessionId);
        return count;
    }

    public static bool HasNetwork() => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
}
