using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCoinCounter.Diagnostics;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>
/// Diagnostica di una sessione: registro a righe (<c>diagnostics.jsonl</c>) e istantanea completa (<c>session.json</c>) nella
/// cartella della sessione, leggibili dal PC con <c>adb pull /sdcard/Android/data/com.LiveCoinCounter.app/files/sessions</c>.
/// </summary>
public static class SessionDiagnostics
{
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Apre (in aggiunta) il registro della sessione e scrive subito chi lo apre, quando e su quale telefono.</summary>
    public static JsonlDiagLog Open(string sessionId, string reason)
    {
        var log = new JsonlDiagLog(SessionStorage.DiagnosticsPath(sessionId));
        log.Event("log_open", new
        {
            reason, session = sessionId, wall = DateTimeOffset.Now.ToString("o"),
            app = AppInfo.VersionString, build = AppInfo.BuildString,
            device = new
            {
                manufacturer = Android.OS.Build.Manufacturer, model = Android.OS.Build.Model, android = Android.OS.Build.VERSION.Release,
                sdk = (int)Android.OS.Build.VERSION.SdkInt, abi = Android.OS.Build.SupportedAbis?.FirstOrDefault()
            },
            settings = new
            {
                captureMethod = SettingsService.CaptureMethod, retention = SettingsService.Retention, aiMode = SettingsService.AiMode,
                recognizer = SettingsService.ActiveRecognizerId, onDeviceProfile = SettingsService.OnDeviceProfile.Id
            }
        }, important: true);
        return log;
    }

    /// <summary>Un solo evento (es. una correzione fatta dall'utente) aggiunto al registro di una sessione già chiusa.</summary>
    public static async Task AppendAsync(string sessionId, string type, object? data)
    {
        try
        {
            await using var log = Open(sessionId, type);
            log.Event(type, data, important: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Registro non aggiornato: {ex.Message}");
        }
    }

    /// <summary>Riscrive <c>session.json</c>: sessione, fotogrammi chiave, monete con tutti i riconoscimenti e la verifica dell'utente.</summary>
    public static async Task WriteSnapshotAsync(SessionRepository repository, string sessionId)
    {
        try
        {
            var session = await repository.GetSessionAsync(sessionId);
            if (session is null) return;
            var coins = await repository.ListCoinsAsync(sessionId);
            var keyframes = await repository.ListKeyframesAsync(sessionId);
            var folder = SessionStorage.Folder(sessionId);
            Directory.CreateDirectory(folder);

            var coinInfos = new List<object>();
            foreach (var c in coins)
            {
                var history = await repository.ListClassificationsAsync(c.Id);
                coinInfos.Add(new
                {
                    number = c.Number,
                    state = c.State,
                    box = new[] { c.X, c.Y, c.Width, c.Height },
                    diameterPx = c.Diameter,
                    detection = new { confidence = c.DetectionConfidence, observations = c.Observations, sourceKeyframe = c.SourceKeyframe },
                    detector = new { cents = c.DetectorCents, confidence = c.DetectorClassConfidence },
                    result = new { cents = c.Cents, confidence = c.ClassConfidence, second = c.SecondCents, secondScore = c.SecondScore, classifier = c.ClassifierId, note = c.Note },
                    effectiveCents = c.EffectiveCents,
                    truth = new { verified = c.Verified, kind = c.TruthKind, correctedCents = c.CorrectedCents, note = c.TruthNote, manual = c.IsManual },
                    history = history.Select(h => new { classifier = h.ClassifierId, cents = h.Cents, confidence = h.Confidence, topK = h.TopK, note = h.Note, at = h.CreatedAt }),
                    crop = c.CropPath is null ? null : Path.GetRelativePath(folder, c.CropPath)
                });
            }

            var snapshot = new
            {
                schema = 1,
                writtenAt = DateTime.Now,
                app = AppInfo.VersionString,
                session,
                summary = new
                {
                    appCoinCount = coins.Count(c => c.State != CoinState.Rejected),
                    detected = coins.Count(c => !c.IsManual),
                    verified = coins.Count(c => c.Verified),
                    wrongValue = coins.Count(c => c.TruthKind == TruthKinds.WrongValue),
                    notCoin = coins.Count(c => c.TruthKind == TruthKinds.NotCoin),
                    duplicate = coins.Count(c => c.TruthKind == TruthKinds.Duplicate),
                    manuallyAdded = coins.Count(c => c.IsManual),
                    totalCents = session.TotalCents
                },
                keyframes = keyframes.Select(k => new { k.Index, k.CapturedAt, k.Sharpness, k.Width, k.Height, homography = k.Homography }),
                coins = coinInfos
            };

            var temp = SessionStorage.SnapshotPath(sessionId) + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, SnapshotOptions));
            File.Move(temp, SessionStorage.SnapshotPath(sessionId), overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Istantanea della sessione non scritta: {ex.Message}");
        }
    }
}
