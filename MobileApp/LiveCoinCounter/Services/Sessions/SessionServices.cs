using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Platforms.Android.Capture;
using LiveCoinCounter.Services.Recognition;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>Punto unico di accesso a database e componenti delle sessioni.</summary>
public static class SessionServices
{
    private static readonly Lazy<SessionRepository> LazyRepository = new(() => new SessionRepository(SessionStorage.DatabasePath));

    public static SessionRepository Repository => LazyRepository.Value;

    public static ICoinLocator CreateLocator() => new OnnxCoinLocator(SettingsService.OnDeviceProfile);

    /// <summary>Il rilevatore dei marker di scala, o null se OpenCV non si carica su questo telefono.</summary>
    public static IMarkerDetector? CreateMarkerDetector() => OpenCvMarkerDetector.TryCreate();

    public static ICoinClassifier CreateOnDeviceClassifier() => new YoloCropClassifier(SettingsService.OnDeviceProfile);

    /// <summary>Il riconoscitore AI: quello scelto se è in rete, altrimenti Gemini (con la chiave salvata o quella predefinita).</summary>
    public static async Task<ICoinClassifier> CreateAiClassifierAsync()
    {
        var active = RecognizerCatalog.Get(SettingsService.ActiveRecognizerId);
        var profile = active.IsOffline ? RecognizerCatalog.Get("gemini") : active;
        var recognizer = await RecognizerFactory.CreateWithFallbackAsync(profile, await SettingsService.GetConfigAsync(profile));
        return new AiCropClassifier(recognizer);
    }

    public sealed record CaptureSetup(IMosaicTracker Tracker, IFrameSource Source, CaptureMethod Method, string? Note);

    /// <summary>
    /// Tracker e sorgente dei fotogrammi per il metodo scelto. ARCore e Ibrido richiedono un telefono supportato:
    /// altrimenti si ripiega su OpenCV (e, se OpenCV non si carica, sul rilevatore di punti in C#).
    /// </summary>
    public static CaptureSetup CreateCaptureSetup(CaptureMethod method)
    {
        string? note = null;
        var context = Platform.AppContext;
        if (method != CaptureMethod.OpenCv && !ArCoreFrameSource.IsSupported(context))
        {
            note = "ARCore non è supportato su questo telefono: uso OpenCV.";
            method = CaptureMethod.OpenCv;
        }

        IFeatureExtractor Features()
        {
            var opencv = OpenCvOrbExtractor.TryCreate();
            if (opencv is not null) return opencv;
            note ??= "OpenCV non disponibile: uso il rilevatore di punti integrato (più lento).";
            return new OrbLite();
        }

        return method switch
        {
            CaptureMethod.ArCore => new CaptureSetup(new ArCorePlaneTracker(), new ArCoreFrameSource(), method, note),
            CaptureMethod.Hybrid => new CaptureSetup(
                new HybridMosaicTracker(new ArCorePlaneTracker(), new FeatureMosaicTracker(Features())), new ArCoreFrameSource(), method, note),
            _ => new CaptureSetup(new FeatureMosaicTracker(Features()), new CameraXFrameSource(), CaptureMethod.OpenCv, note)
        };
    }

    public static async Task<byte[]?> LoadCropAsync(CoinRecord coin) =>
        coin.CropPath is { } path && File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;

    /// <summary>La scala della superficie: misurata (ARCore o marker) o stimata dalle monete riconosciute con sicurezza (almeno 3); null se non si può.</summary>
    public static SurfaceScale? ScaleOf(SessionRecord session, IReadOnlyList<CoinRecord> coins) =>
        SurfaceScale.From(session.PxPerMm, coins
            .Where(c => !c.IsManual && c.State != CoinState.Rejected && c.EffectiveCents is not null)
            .Select(c => (c.Diameter, c.EffectiveCents!.Value, c.State == CoinState.Corrected || c.Verified ? 1f : c.ClassConfidence)),
            SessionMarkers.Parse(session.MarkersJson));

    /// <summary>Riscrive <c>session.json</c> nella cartella della sessione (dopo lo stop, la classificazione e ogni correzione).</summary>
    public static Task WriteSnapshotAsync(string sessionId) => SessionDiagnostics.WriteSnapshotAsync(Repository, sessionId);

    public static async Task DeleteSessionAsync(string sessionId)
    {
        // una sessione a scatti: anche le cartelle dei suoi scatti
        var shots = await Repository.ListShotsAsync(sessionId);
        await Repository.DeleteSessionAsync(sessionId);
        foreach (var shot in shots) SessionStorage.Delete(shot.Id);
        SessionStorage.Delete(sessionId);
    }

    /// <summary>
    /// Scatti senza la loro sessione a scatti (fatti con la versione che li mostrava come sessioni separate): si raccolgono in una
    /// sessione a scatti, altrimenti non comparirebbero più nell'elenco. Restituisce quante sessioni a scatti ha creato.
    /// </summary>
    public static async Task<int> AdoptOrphanShotsAsync(IReadOnlyList<SessionRecord> sessions)
    {
        var ids = sessions.Select(s => s.Id).ToHashSet();
        var created = 0;
        foreach (var group in sessions.Where(s => s.SeriesId is not null && !ids.Contains(s.SeriesId)).GroupBy(s => s.SeriesId!))
        {
            var first = group.OrderBy(s => s.StartedAt).First();
            await Repository.EnsureShotSessionAsync(group.Key, first.TrackerId, first.LocatorId, first.Retention, first.StartedAt);
            await UpdateShotSessionAsync(group.Key);
            created++;
        }
        return created;
    }

    /// <summary>
    /// Elimina uno scatto della sessione a scatti; se era l'ultimo, anche la sessione a scatti (rimasta vuota).
    /// </summary>
    public static async Task DeleteShotAsync(string shotId, string seriesId)
    {
        await DeleteSessionAsync(shotId);
        var shots = await Repository.ListShotsAsync(seriesId);
        if (shots.Count == 0)
        {
            await DeleteSessionAsync(seriesId);
            return;
        }
        // gli scatti rimasti si rinumerano 1..n
        for (var i = 0; i < shots.Count; i++)
        {
            if (shots[i].ShotIndex == i + 1) continue;
            shots[i].ShotIndex = i + 1;
            shots[i].Name = $"Scatto {i + 1}";
            await Repository.UpdateSessionAsync(shots[i]);
        }
        await UpdateShotSessionAsync(seriesId);
    }

    /// <summary>
    /// Dopo uno scatto: la sessione a scatti prende totali, anteprima (il primo scatto), fine e metodo dagli scatti, e riscrive l'istantanea.
    /// </summary>
    public static async Task UpdateShotSessionAsync(string seriesId)
    {
        var session = await Repository.RefreshTotalsAsync(seriesId);
        if (session is null) return;
        var shots = await Repository.ListShotsAsync(seriesId);
        session.MosaicPreviewPath = shots.Select(s => s.MosaicPreviewPath).FirstOrDefault(p => p is not null && File.Exists(p));
        session.EndedAt = shots.Max(s => s.EndedAt) ?? DateTime.Now;
        session.KeyframeCount = shots.Sum(s => s.KeyframeCount);
        session.ClassifierId = shots.Select(s => s.ClassifierId).LastOrDefault(c => c is not null) ?? session.ClassifierId;
        await Repository.UpdateSessionAsync(session);
        await WriteSnapshotAsync(seriesId);
    }
}
