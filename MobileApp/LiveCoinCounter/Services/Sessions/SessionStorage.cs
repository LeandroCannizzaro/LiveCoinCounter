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
    /// <summary>Cartella privata dell'app: lì stavano le sessioni nelle versioni precedenti (non leggibile da adb).</summary>
    private static string LegacyRoot => Path.Combine(FileSystem.AppDataDirectory, "sessions");

    /// <summary>
    /// Cartella delle sessioni: lo spazio dell'app sulla memoria condivisa (<c>Android/data/…/files/sessions</c>), che si
    /// legge dal PC con <c>adb pull</c> senza bisogno di root. Se non è disponibile si ripiega sulla cartella privata.
    /// </summary>
    public static string Root
    {
        get
        {
            var external = Platform.AppContext.GetExternalFilesDir(null)?.AbsolutePath;
            var path = external is null ? LegacyRoot : Path.Combine(external, "sessions");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "livecoincounter.db3");

    /// <summary>La cartella di una sessione; le sessioni create prima dello spostamento restano dove sono.</summary>
    public static string Folder(string sessionId)
    {
        var legacy = Path.Combine(LegacyRoot, sessionId);
        return Directory.Exists(legacy) ? legacy : Path.Combine(Root, sessionId);
    }

    public static string MosaicPath(string sessionId) => Path.Combine(Folder(sessionId), "mosaic.jpg");

    /// <summary>Registro diagnostico della sessione: un evento JSON per riga.</summary>
    public static string DiagnosticsPath(string sessionId) => Path.Combine(Folder(sessionId), "diagnostics.jsonl");

    /// <summary>Istantanea completa della sessione (dati, monete, riconoscimenti, correzioni), riscritta a ogni modifica.</summary>
    public static string SnapshotPath(string sessionId) => Path.Combine(Folder(sessionId), "session.json");
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

    /// <summary>Esporta la sessione (cartella con mosaico, ritagli, istantanea e registro) in uno zip e apre la condivisione.</summary>
    public static async Task ExportAsync(SessionRecord session)
    {
        await SessionServices.WriteSnapshotAsync(session.Id);
        var folder = Folder(session.Id);
        var zip = Path.Combine(FileSystem.CacheDirectory, $"sessione-{session.Id}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(folder, zip);
        await Share.Default.RequestAsync(new ShareFileRequest(session.Name, new ShareFile(zip)));
    }
}
