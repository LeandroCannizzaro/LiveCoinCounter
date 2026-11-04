using System.IO.Compression;
using System.Text.Json;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>
/// File delle sessioni nella cartella privata dell'app: <c>sessions/{id}/</c> con mosaico, ritagli delle monete,
/// fotogrammi chiave (se conservati) e fotogrammi registrati (modalità debug).
/// </summary>
public static class SessionStorage
{
    public static string Root
    {
        get
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "sessions");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "livecoincounter.db3");

    public static string Folder(string sessionId) => Path.Combine(Root, sessionId);
    public static string MosaicPath(string sessionId) => Path.Combine(Folder(sessionId), "mosaic.jpg");
    public static string CropPath(string sessionId, int number) => Path.Combine(Folder(sessionId), "coins", $"{number:D4}.jpg");
    public static string KeyframePath(string sessionId, int index) => Path.Combine(Folder(sessionId), "keyframes", $"{index:D4}.jpg");
    public static string FramesFolder(string sessionId) => Path.Combine(Folder(sessionId), "frames");

    public static void Delete(string sessionId)
    {
        var folder = Folder(sessionId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>Pulizia di fine sessione secondo l'impostazione "Archivio delle sessioni".</summary>
    public static void ApplyRetention(SessionRecord session)
    {
        if (session.Retention != RetentionMode.WithKeyframes)
        {
            var keyframes = Path.Combine(Folder(session.Id), "keyframes");
            if (Directory.Exists(keyframes)) Directory.Delete(keyframes, recursive: true);
        }
    }

    public static long SizeOnDisk(string sessionId)
    {
        var folder = Folder(sessionId);
        return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    /// <summary>Esporta la sessione (cartella + JSON con monete e tagli) in uno zip e apre la condivisione.</summary>
    public static async Task ExportAsync(SessionRecord session, IReadOnlyList<CoinRecord> coins)
    {
        var folder = Folder(session.Id);
        Directory.CreateDirectory(folder);
        var info = new
        {
            session.Id, session.Name, session.StartedAt, session.EndedAt, Source = session.Source.ToString(), session.TrackerId,
            session.LocatorId, session.ClassifierId, session.TotalCents, session.CoinCount, session.PxPerMm,
            Coins = coins.Select(c => new
            {
                c.Number, c.X, c.Y, c.Width, c.Height, c.DetectionConfidence, c.Observations, State = c.State.ToString(),
                c.Cents, c.ClassConfidence, c.SecondCents, c.SecondScore, c.CorrectedCents, c.ClassifierId, c.Note,
                Crop = c.CropPath is null ? null : Path.GetRelativePath(folder, c.CropPath)
            })
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));

        var zip = Path.Combine(FileSystem.CacheDirectory, $"sessione-{session.Id}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(folder, zip);
        await Share.Default.RequestAsync(new ShareFileRequest(session.Name, new ShareFile(zip)));
    }
}
