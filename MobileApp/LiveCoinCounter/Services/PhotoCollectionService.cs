using System.IO.Compression;
using System.Text.Json;
using LiveCoinCounter.Models;

namespace LiveCoinCounter.Services;

/// <summary>
/// Raccolta facoltativa (spenta di default) delle foto analizzate, per migliorare il modello offline.
/// Le foto restano sul telefono, in una cartella dell'app, finché l'utente non le esporta di sua iniziativa.
/// Accanto a ogni foto si salva il risultato del modello e, se l'utente tocca "Il conteggio è sbagliato" o riprova
/// con l'AI, anche questo: sono i casi difficili e le etichette che servono al prossimo addestramento.
/// </summary>
public static class PhotoCollectionService
{
    private const string EnabledPreference = "collect_photos";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool Enabled
    {
        get => Preferences.Default.Get(EnabledPreference, false);
        set => Preferences.Default.Set(EnabledPreference, value);
    }

    private static string Folder
    {
        get
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "collected");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static int Count => Directory.GetFiles(Folder, "*.jpg", SearchOption.AllDirectories).Length;

    public static string? Save(byte[] image, CoinRecognitionResult result, string methodId)
    {
        if (!Enabled || !result.Success) return null;

        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        File.WriteAllBytes(Path.Combine(Folder, $"{id}.jpg"), ImageHelper.ShrinkForUpload(image, 1600, 88));
        WriteInfo(id, new Dictionary<string, object?>
        {
            ["method"] = methodId,
            ["total_cents"] = result.TotalValueInCents,
            ["coins"] = result.Coins.Select(c => new { c.ValueInCents, c.Count }).ToList(),
            ["detections"] = result.Detections.Select(d => new { d.ValueInCents, d.Confidence, d.X, d.Y, d.Width, d.Height }).ToList(),
            ["warning"] = result.Warning,
            ["flagged_wrong"] = false
        });
        return id;
    }

    /// <summary>
    /// Ritaglio di una moneta corretta a mano in una sessione: un'etichetta pronta per il dataset del classificatore
    /// (issue #5). Si salva solo con la raccolta attiva; resta sul telefono fino all'esportazione.
    /// </summary>
    public static void SaveCoinLabel(byte[] cropJpeg, string sessionId, int number, int? correctCents, int? predictedCents, string? classifierId)
    {
        if (!Enabled) return;
        var folder = Path.Combine(Folder, "coins");
        Directory.CreateDirectory(folder);
        var id = $"{sessionId}-{number:D4}";
        File.WriteAllBytes(Path.Combine(folder, $"{id}.jpg"), cropJpeg);
        File.WriteAllText(Path.Combine(folder, $"{id}.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session"] = sessionId,
            ["coin"] = number,
            ["cents"] = correctCents,          // null = non è una moneta
            ["predicted_cents"] = predictedCents,
            ["classifier"] = classifierId
        }, Json));
    }

    public static void MarkWrong(string id) => Update(id, info => info["flagged_wrong"] = true);

    public static void AttachAiResult(string id, string methodId, CoinRecognitionResult result) =>
        Update(id, info =>
        {
            info["ai_method"] = methodId;
            info["ai_total_cents"] = result.TotalValueInCents;
            info["ai_coins"] = result.Coins.Select(c => new { c.ValueInCents, c.Count }).ToList();
        });

    /// <summary>Comprime la cartella in un file zip e apre la condivisione di Android.</summary>
    public static async Task ExportAsync()
    {
        var zip = Path.Combine(FileSystem.CacheDirectory, $"foto-monete-{DateTime.Now:yyyyMMdd}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(Folder, zip);
        await Share.Default.RequestAsync(new ShareFileRequest("Foto raccolte", new ShareFile(zip)));
    }

    private static void Update(string id, Action<Dictionary<string, object?>> change)
    {
        var path = Path.Combine(Folder, $"{id}.json");
        if (!File.Exists(path)) return;
        var info = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path)) ?? new();
        change(info);
        WriteInfo(id, info);
    }

    private static void WriteInfo(string id, Dictionary<string, object?> info) =>
        File.WriteAllText(Path.Combine(Folder, $"{id}.json"), JsonSerializer.Serialize(info, Json));
}
