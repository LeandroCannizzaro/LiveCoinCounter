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

    private CaptureController(SessionRecord session, CaptureEngine engine, IFrameSource? source, AppCaptureSink sink, string? note)
    {
        Session = session;
        Engine = engine;
        _source = source;
        _sink = sink;
        Note = note;
    }

    public SessionRecord Session { get; }
    public CaptureEngine Engine { get; }

    /// <summary>L'ultimo fotogramma arrivato dalla fotocamera (per l'anteprima).</summary>
    public CameraFrame? LatestFrame { get; private set; }

    /// <summary>Avviso sul metodo effettivamente usato (es. ARCore non supportato).</summary>
    public string? Note { get; }

    public static async Task<CaptureController> StartLiveAsync()
    {
        var setup = SessionServices.CreateCaptureSetup(SettingsService.CaptureMethod);
        var locator = SessionServices.CreateLocator();
        var repository = SessionServices.Repository;
        var session = await repository.CreateSessionAsync(SessionSource.Live, setup.Tracker.Id, locator.Id, SettingsService.Retention, DateTime.Now);

        var sink = new AppCaptureSink(session, repository);
        var engine = new CaptureEngine(setup.Tracker, locator, sink, new MosaicCanvas());
        var controller = new CaptureController(session, engine, setup.Source, sink, setup.Note);
        engine.Start();

        try
        {
            await setup.Source.StartAsync(frame =>
            {
                controller.LatestFrame = frame;
                engine.PushFrame(frame);
            }, () => engine.WantsFrame);
        }
        catch
        {
            await engine.StopAsync();
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

        var sink = new AppCaptureSink(session, repository);
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite()), locator, sink, new MosaicCanvas());
        var controller = new CaptureController(session, engine, null, sink, null);
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
        var mosaic = await Task.Run(() => Engine.Canvas.Render(4096, out var scale, out var origin) is { } image
            ? (image, scale, origin)
            : ((ArgbImage, float, PointD)?)null);
        if (mosaic is { } m)
        {
            await AndroidImaging.SaveJpegAsync(m.Item1, SessionStorage.MosaicPath(session.Id), 88);
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
    }
}

/// <summary>Riconoscimento del valore dopo lo stop, secondo l'impostazione "Riconoscimento dopo lo stop".</summary>
public static class SessionProcessing
{
    public static async Task ClassifyAsync(string sessionId, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var classifier = new SessionClassifier(SessionServices.Repository, SessionServices.LoadCropAsync);
        await classifier.ClassifyAsync(sessionId, SessionServices.CreateOnDeviceClassifier(), progress, ct);

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
        var classifier = new SessionClassifier(SessionServices.Repository, SessionServices.LoadCropAsync);
        return await classifier.ClassifyAsync(sessionId, ai, progress, ct, onlyUncertain: true, replace: true);
    }

    public static bool HasNetwork() => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
}
