using System.Diagnostics;
using LiveCoinCounter.Diagnostics;

namespace LiveCoinCounter.Sessions;

/// <summary>
/// Dopo lo stop: passa i ritagli delle monete al classificatore, applica il controllo sul diametro e aggiorna i totali.
/// Le correzioni dell'utente e le monete scartate non vengono toccate.
/// </summary>
public sealed class SessionClassifier
{
    private readonly SessionRepository _repository;
    private readonly Func<CoinRecord, Task<byte[]?>> _loadCrop;
    private readonly Func<DateTime> _now;
    private readonly IDiagLog _log;

    public SessionClassifier(SessionRepository repository, Func<CoinRecord, Task<byte[]?>> loadCrop, Func<DateTime>? now = null, IDiagLog? log = null)
    {
        _repository = repository;
        _loadCrop = loadCrop;
        _now = now ?? (() => DateTime.Now);
        _log = log ?? NullDiagLog.Instance;
    }

    /// <param name="onlyUncertain">Solo le monete già classificate con confidenza bassa (es. "Riprova con l'AI le incerte").</param>
    /// <param name="replace">Il nuovo risultato sostituisce il precedente anche se meno sicuro (vale per l'AI, chiesta apposta).</param>
    /// <returns>Le monete classificate in questa passata.</returns>
    public async Task<int> ClassifyAsync(string sessionId, ICoinClassifier classifier, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default, bool onlyUncertain = false, bool replace = false)
    {
        var session = await _repository.GetSessionAsync(sessionId) ?? throw new InvalidOperationException("Sessione inesistente.");
        var coins = await _repository.ListCoinsAsync(sessionId);
        var todo = coins.Where(c => onlyUncertain ? c.IsUncertain : c.State == CoinState.Detected).ToList();
        var done = 0;
        var results = new Dictionary<int, CoinClassification>();

        _log.Event("classify_start", new { classifier = classifier.Id, onlyUncertain, replace, coins = todo.Count, pxPerMm = session.PxPerMm }, important: true);
        progress?.Report((0, todo.Count));
        foreach (var coin in todo)
        {
            ct.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var jpeg = await _loadCrop(coin);
            CoinClassification? result = null;
            string? failure = null;
            if (jpeg is not null)
            {
                try
                {
                    result = await classifier.ClassifyAsync(new CoinCrop(coin.Number, jpeg, coin.Diameter, session.PxPerMm), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex.Message;
                    _log.Event("error", new { where = "classify", coin = coin.Number, message = ex.Message, stack = ex.ToString() }, important: true);
                    throw;
                }
                if (result is not null) await _repository.AddClassificationAsync(coin.Id, result, _now());
            }
            _log.Event("classify", new
            {
                coin = coin.Number, diameterPx = DiagData.R(coin.Diameter), hasCrop = jpeg is not null, ms = DiagData.R(timer.Elapsed.TotalMilliseconds),
                cents = result?.Cents, conf = result is null ? (double?)null : DiagData.R(result.Confidence, 3),
                topK = result?.TopK.Select(k => new { cents = k.Cents, score = DiagData.R(k.Score, 3) }),
                detectorCents = coin.DetectorCents, detectorConf = DiagData.R(coin.DetectorClassConfidence, 3), failure
            });

            // il taglio dato dal rilevatore nel contesto del fotogramma intero vale come seconda opinione
            if (!onlyUncertain && coin.DetectorCents is { } detCents &&
                (result is null || coin.DetectorClassConfidence > result.Confidence))
            {
                var detector = new CoinClassification(detCents, coin.DetectorClassConfidence,
                    result?.TopK.Count > 0 ? result.TopK : new[] { (detCents, coin.DetectorClassConfidence) },
                    session.LocatorId, "Taglio dal rilevatore");
                await _repository.AddClassificationAsync(coin.Id, detector, _now());
                result = detector;
            }

            if (result is not null) results[coin.Id] = result;
            progress?.Report((++done, todo.Count));
        }

        // controllo del diametro su tutte le monete classificate della sessione, incluse quelle di passate precedenti
        var items = coins.Where(c => c.State != CoinState.Rejected && !c.IsManual)
            .Select(c => results.TryGetValue(c.Id, out var r) ? new DiameterCheck.Item(c.Id, c.Diameter, r)
                : c.Cents is { } cents && c.State != CoinState.Detected
                    ? new DiameterCheck.Item(c.Id, c.Diameter, new CoinClassification(c.CorrectedCents ?? cents,
                        c.State == CoinState.Corrected ? 1f : c.ClassConfidence, Array.Empty<(int, float)>(), c.ClassifierId ?? ""))
                    : null)
            .Where(i => i is not null).Cast<DiameterCheck.Item>().ToList();
        var checkedItems = DiameterCheck.Apply(items, session.PxPerMm).ToDictionary(i => i.CoinId);

        foreach (var coin in todo)
        {
            if (!checkedItems.TryGetValue(coin.Id, out var item) || !results.ContainsKey(coin.Id)) continue;
            var c = item.Classification;
            if (!replace && onlyUncertain && coin.Cents is not null && c.Confidence <= coin.ClassConfidence) continue;
            _log.Event("classify_final", new
            {
                coin = coin.Number, before = results[coin.Id].Cents, after = c.Cents, conf = DiagData.R(c.Confidence, 3), note = c.Note,
                diameterChecked = c.Note is not null
            });

            var second = c.TopK.Where(k => k.Cents != c.Cents).OrderByDescending(k => k.Score).Cast<(int, float)?>().FirstOrDefault();
            coin.Cents = c.Cents;
            coin.ClassConfidence = c.Confidence;
            coin.SecondCents = second?.Item1;
            coin.SecondScore = second?.Item2 ?? 0;
            coin.ClassifierId = c.ClassifierId;
            coin.Note = c.Note;
            coin.State = CoinState.Classified;
            await _repository.UpdateCoinAsync(coin);
        }

        if (!onlyUncertain) session.ClassifierId = classifier.Id;
        session.Status = SessionStatus.Completed;
        session.EndedAt ??= _now();
        await _repository.UpdateSessionAsync(session);
        var refreshed = await _repository.RefreshTotalsAsync(sessionId);
        _log.Event("classify_done", new { classified = results.Count, totalCents = refreshed?.TotalCents, coins = refreshed?.CoinCount, uncertain = refreshed?.UncertainCount }, important: true);
        return results.Count;
    }
}
