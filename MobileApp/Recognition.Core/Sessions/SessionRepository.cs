using System.Globalization;
using SQLite;

namespace LiveCoinCounter.Sessions;

/// <summary>
/// Database SQLite delle sessioni (sqlite-net). Le immagini restano file su disco, qui ci sono percorsi e metadati.
/// Una sola connessione asincrona con WAL: il thread di rilevamento scrive mentre l'interfaccia legge.
/// </summary>
public sealed class SessionRepository : IAsyncDisposable
{
    public const int SchemaVersion = 1;
    public const float UncertainThreshold = 0.55f;

    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _initialized;

    public SessionRepository(string databasePath)
    {
        _db = new SQLiteAsyncConnection(databasePath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache | SQLiteOpenFlags.FullMutex);
    }

    public async Task InitAsync()
    {
        if (_initialized) return;
        await _init.WaitAsync();
        try
        {
            if (_initialized) return;
            try { await _db.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL"); } catch (SQLiteException) { }
            await _db.CreateTableAsync<SchemaInfo>();
            await _db.CreateTableAsync<SessionRecord>();
            await _db.CreateTableAsync<KeyframeRecord>();
            await _db.CreateTableAsync<CoinRecord>();
            await _db.CreateTableAsync<ClassificationRecord>();
            var info = await _db.FindAsync<SchemaInfo>(1);
            if (info is null) await _db.InsertAsync(new SchemaInfo { Id = 1, Version = SchemaVersion });
            // migrazioni future: if (info.Version < 2) { ... }
            _initialized = true;
        }
        finally
        {
            _init.Release();
        }
    }

    // ---- sessioni ----

    public async Task<SessionRecord> CreateSessionAsync(SessionSource source, string trackerId, string locatorId, RetentionMode retention, DateTime now)
    {
        await InitAsync();
        var session = new SessionRecord
        {
            Id = now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture),
            Name = $"Sessione del {now:dd/MM/yyyy HH:mm}",
            StartedAt = now,
            Status = SessionStatus.Acquiring,
            Source = source,
            TrackerId = trackerId,
            LocatorId = locatorId,
            Retention = retention
        };
        await _db.InsertAsync(session);
        return session;
    }

    /// <summary>
    /// La sessione a scatti <paramref name="id"/>: la crea se non c'è. Gli scatti (sessioni con <see cref="SessionRecord.SeriesId"/> = id)
    /// si aggiungono via via; totali e monete sono la loro somma.
    /// </summary>
    public async Task<SessionRecord> EnsureShotSessionAsync(string id, string trackerId, string locatorId, RetentionMode retention, DateTime now)
    {
        await InitAsync();
        if (await _db.FindAsync<SessionRecord>(id) is { } existing) return existing;
        var session = new SessionRecord
        {
            Id = id,
            Name = $"Sessione a scatti del {now:dd/MM/yyyy HH:mm}",
            StartedAt = now,
            Status = SessionStatus.Completed,
            Source = SessionSource.Shots,
            TrackerId = trackerId,
            LocatorId = locatorId,
            Retention = retention
        };
        await _db.InsertAsync(session);
        return session;
    }

    /// <summary>Gli scatti di una sessione a scatti, in ordine.</summary>
    public async Task<List<SessionRecord>> ListShotsAsync(string sessionId)
    {
        await InitAsync();
        return (await _db.Table<SessionRecord>().Where(s => s.SeriesId == sessionId).ToListAsync())
            .OrderBy(s => s.ShotIndex).ThenBy(s => s.StartedAt).ToList();
    }

    public async Task<SessionRecord?> GetSessionAsync(string id)
    {
        await InitAsync();
        return await _db.FindAsync<SessionRecord>(id);
    }

    public async Task<List<SessionRecord>> ListSessionsAsync()
    {
        await InitAsync();
        return await _db.Table<SessionRecord>().OrderByDescending(s => s.StartedAt).ToListAsync();
    }

    /// <summary>Sessioni rimaste a metà (app chiusa durante acquisizione o classificazione).</summary>
    public async Task<List<SessionRecord>> ListInterruptedAsync()
    {
        await InitAsync();
        return await _db.Table<SessionRecord>()
            .Where(s => s.Status == SessionStatus.Acquiring || s.Status == SessionStatus.Classifying)
            .ToListAsync();
    }

    public async Task UpdateSessionAsync(SessionRecord session)
    {
        await InitAsync();
        await _db.UpdateAsync(session);
    }

    public async Task RenameAsync(string id, string name)
    {
        await InitAsync();
        await _db.ExecuteAsync("UPDATE Session SET Name = ? WHERE Id = ?", name.Trim(), id);
    }

    public async Task DeleteSessionAsync(string id)
    {
        await InitAsync();
        await _db.RunInTransactionAsync(c =>
        {
            // una sessione a scatti si porta via i suoi scatti
            foreach (var shot in c.Query<SessionRecord>("SELECT * FROM Session WHERE SeriesId = ?", id))
            {
                c.Execute("DELETE FROM Classification WHERE CoinId IN (SELECT Id FROM Coin WHERE SessionId = ?)", shot.Id);
                c.Execute("DELETE FROM Coin WHERE SessionId = ?", shot.Id);
                c.Execute("DELETE FROM Keyframe WHERE SessionId = ?", shot.Id);
                c.Execute("DELETE FROM Session WHERE Id = ?", shot.Id);
            }
            c.Execute("DELETE FROM Classification WHERE CoinId IN (SELECT Id FROM Coin WHERE SessionId = ?)", id);
            c.Execute("DELETE FROM Coin WHERE SessionId = ?", id);
            c.Execute("DELETE FROM Keyframe WHERE SessionId = ?", id);
            c.Execute("DELETE FROM Session WHERE Id = ?", id);
        });
    }

    // ---- fotogrammi chiave ----

    public async Task AddKeyframeAsync(KeyframeRecord keyframe)
    {
        await InitAsync();
        await _db.InsertAsync(keyframe);
    }

    public async Task<List<KeyframeRecord>> ListKeyframesAsync(string sessionId)
    {
        await InitAsync();
        return await _db.Table<KeyframeRecord>().Where(k => k.SessionId == sessionId).OrderBy(k => k.Index).ToListAsync();
    }

    public async Task ClearKeyframeImagesAsync(string sessionId)
    {
        await InitAsync();
        await _db.ExecuteAsync("UPDATE Keyframe SET ImagePath = NULL WHERE SessionId = ?", sessionId);
    }

    // ---- monete ----

    /// <summary>Inserisce o aggiorna più monete in una transazione (scritture a lotti dal thread di rilevamento).</summary>
    public async Task UpsertCoinsAsync(string sessionId, IReadOnlyList<CoinRecord> coins)
    {
        if (coins.Count == 0) return;
        await InitAsync();
        await _db.RunInTransactionAsync(c =>
        {
            foreach (var coin in coins)
            {
                coin.SessionId = sessionId;
                var existing = c.Table<CoinRecord>().FirstOrDefault(x => x.SessionId == sessionId && x.Number == coin.Number);
                if (existing is null) c.Insert(coin);
                else
                {
                    coin.Id = existing.Id;
                    c.Update(coin);
                }
            }
        });
    }

    /// <summary>Le monete della sessione; per una sessione a scatti quelle di tutti gli scatti (e le aggiunte a mano), scatto per scatto.</summary>
    public async Task<List<CoinRecord>> ListCoinsAsync(string sessionId)
    {
        await InitAsync();
        var shots = await ListShotsAsync(sessionId);
        if (shots.Count == 0)
            return await _db.Table<CoinRecord>().Where(c => c.SessionId == sessionId).OrderBy(c => c.Number).ToListAsync();

        var order = shots.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i + 1);
        var ids = shots.Select(s => s.Id).Append(sessionId).ToList();
        var coins = await _db.Table<CoinRecord>().Where(c => ids.Contains(c.SessionId)).ToListAsync();
        return coins.OrderBy(c => order.GetValueOrDefault(c.SessionId, int.MaxValue)).ThenBy(c => c.Number).ToList();
    }

    public async Task<CoinRecord?> GetCoinAsync(int id)
    {
        await InitAsync();
        return await _db.FindAsync<CoinRecord>(id);
    }

    public async Task UpdateCoinAsync(CoinRecord coin)
    {
        await InitAsync();
        await _db.UpdateAsync(coin);
    }

    // ---- classificazioni ----

    public async Task AddClassificationAsync(int coinId, CoinClassification c, DateTime now)
    {
        await InitAsync();
        await _db.InsertAsync(new ClassificationRecord
        {
            CoinId = coinId,
            ClassifierId = c.ClassifierId,
            Cents = c.Cents,
            Confidence = c.Confidence,
            TopK = FormatTopK(c.TopK),
            Note = c.Note,
            CreatedAt = now
        });
    }

    public async Task<List<ClassificationRecord>> ListClassificationsAsync(int coinId)
    {
        await InitAsync();
        return await _db.Table<ClassificationRecord>().Where(c => c.CoinId == coinId).OrderBy(c => c.CreatedAt).ToListAsync();
    }

    /// <summary>Ricalcola totale, numero di monete e incerte della sessione (dopo la classificazione o una correzione).</summary>
    public async Task<SessionRecord?> RefreshTotalsAsync(string sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        if (session is null) return null;
        var coins = await ListCoinsAsync(sessionId);
        var counted = coins.Where(c => c.State != CoinState.Rejected).ToList();
        session.CoinCount = counted.Count;
        session.TotalCents = counted.Sum(c => c.EffectiveCents ?? 0);
        session.UncertainCount = counted.Count(c => c.IsUncertain);
        await _db.UpdateAsync(session);
        // uno scatto: anche la sessione a scatti che lo contiene
        if (session.SeriesId is { } parent && parent != session.Id) await RefreshTotalsAsync(parent);
        return session;
    }

    public static string FormatTopK(IEnumerable<(int Cents, float Score)> topK) =>
        string.Join(";", topK.Select(k => $"{k.Cents}:{k.Score.ToString("0.###", CultureInfo.InvariantCulture)}"));

    public static List<(int Cents, float Score)> ParseTopK(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new()
            : text.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(':'))
                .Where(p => p.Length == 2)
                .Select(p => (int.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture)))
                .ToList();

    public static string FormatHomography(Mosaic.Homography h) =>
        string.Join(",", h.ToArray().Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    public static Mosaic.Homography ParseHomography(string text) =>
        new(text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray());

    public async ValueTask DisposeAsync() => await _db.CloseAsync();
}
