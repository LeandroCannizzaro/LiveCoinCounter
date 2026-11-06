using LiveCoinCounter.Services.Recognition;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.Services;

/// <summary>
/// Gestisce le impostazioni persistenti dell'app: metodo di riconoscimento attivo e,
/// per ogni provider, chiave API (SecureStorage), modello ed endpoint.
/// </summary>
public static class SettingsService
{
    private const string LegacyApiKeyPreference = "gemini_api_key";
    private const string CurrencyPreference = "currency_type";
    private const string ActiveRecognizerPreference = "active_recognizer";

    /// <summary>Chiave Gemini usata quando nessun metodo AI ha una chiave configurata.</summary>
    public const string FallbackGeminiApiKey = "AQ.Ab8RN6LGcR2sNSevUBwehTyHWyU7FBeA8u9ZFr_0SwxzUA00_g";

    public static string CurrencyType
    {
        get => Preferences.Default.Get(CurrencyPreference, "EUR");
        set => Preferences.Default.Set(CurrencyPreference, value);
    }

    public static string ActiveRecognizerId
    {
        get => Preferences.Default.Get(ActiveRecognizerPreference, RecognizerCatalog.DefaultId);
        set => Preferences.Default.Set(ActiveRecognizerPreference, value);
    }

    // ---- Riconoscimento continuo (issue #6) ----

    /// <summary>Metodo di composizione del mosaico. Predefinito: OpenCV (funziona su tutti i telefoni).</summary>
    public static CaptureMethod CaptureMethod
    {
        get => Enum.TryParse<CaptureMethod>(Preferences.Default.Get("capture_method", nameof(CaptureMethod.OpenCv)), out var v) ? v : CaptureMethod.OpenCv;
        set => Preferences.Default.Set("capture_method", value.ToString());
    }

    /// <summary>
    /// Aspetto della superficie acquisita: piano di colore con solo le monete in foto (predefinito) oppure la foto intera
    /// del tavolo (utile per controllare le cuciture e il tracking). Il file della foto si salva comunque nella sessione.
    /// </summary>
    public static SurfaceLook SurfaceLook
    {
        get
        {
            if (Preferences.Default.ContainsKey("surface_view"))
                return Enum.TryParse<SurfaceLook>(Preferences.Default.Get("surface_view", nameof(SurfaceLook.Continuous)), out var v) ? v : SurfaceLook.Continuous;
            // chi aveva l'interruttore "foto del tavolo" lo ritrova; chi aveva il piano parte dal piano continuo
            return Preferences.Default.Get("solid_surface", true) ? SurfaceLook.Continuous : SurfaceLook.Photo;
        }
        set => Preferences.Default.Set("surface_view", value.ToString());
    }

    /// <summary>
    /// Griglia metrica a due livelli: passo fine (righe sottili) e passo principale (righe marcate), ciascuno configurabile o
    /// spento. Predefinito: nessuna. Serve una scala nota o stimabile. Chi aveva scelto il vecchio passo unico lo ritrova.
    /// </summary>
    public static GridConfig Grid
    {
        get
        {
            if (!Preferences.Default.ContainsKey("grid_minor") && Preferences.Default.ContainsKey("grid_spacing"))
            {
                var migrated = GridConfig.FromLegacy(Preferences.Default.Get("grid_spacing", "Off"));
                Grid = migrated;
                return migrated;
            }
            return new GridConfig(Preferences.Default.Get("grid_minor", 0), Preferences.Default.Get("grid_major", 0)).Normalized();
        }
        set
        {
            var grid = value.Normalized();
            Preferences.Default.Set("grid_minor", grid.MinorCm);
            Preferences.Default.Set("grid_major", grid.MajorCm);
        }
    }

    /// <summary>Cosa conservare a fine sessione. Predefinito: mosaico e ritagli.</summary>
    public static RetentionMode Retention
    {
        get => Enum.TryParse<RetentionMode>(Preferences.Default.Get("session_retention", nameof(RetentionMode.MosaicAndCrops)), out var v) ? v : RetentionMode.MosaicAndCrops;
        set => Preferences.Default.Set("session_retention", value.ToString());
    }

    /// <summary>Uso dell'AI in rete dopo lo stop. Predefinito: offline, AI su richiesta.</summary>
    public static AiMode AiMode
    {
        get => Enum.TryParse<AiMode>(Preferences.Default.Get("ai_mode", nameof(AiMode.OfflineAiOnRequest)), out var v) ? v : AiMode.OfflineAiOnRequest;
        set => Preferences.Default.Set("ai_mode", value.ToString());
    }

    /// <summary>
    /// Modello sul telefono usato per trovare e riconoscere le monete nelle sessioni: quello scelto come metodo se è un
    /// modello sul dispositivo, altrimenti il predefinito.
    /// </summary>
    public static RecognizerProfile OnDeviceProfile
    {
        get
        {
            var active = RecognizerCatalog.Get(ActiveRecognizerId);
            return active.Kind == RecognizerKind.OnDeviceModel ? active : RecognizerCatalog.Get(RecognizerCatalog.DefaultId);
        }
    }

    public static async Task<RecognizerConfig> GetConfigAsync(RecognizerProfile profile)
    {
        var apiKey = await GetApiKeyAsync(profile.Id);
        var model = Preferences.Default.Get($"model_{profile.Id}", string.Empty);
        var baseUrl = Preferences.Default.Get($"baseurl_{profile.Id}", string.Empty);
        return new RecognizerConfig(apiKey, model, baseUrl);
    }

    public static async Task SaveConfigAsync(RecognizerProfile profile, RecognizerConfig config)
    {
        await SetApiKeyAsync(profile.Id, config.ApiKey);
        Preferences.Default.Set($"model_{profile.Id}", config.Model?.Trim() ?? string.Empty);
        Preferences.Default.Set($"baseurl_{profile.Id}", config.BaseUrl?.Trim() ?? string.Empty);
    }

    private static async Task<string> GetApiKeyAsync(string id)
    {
        try
        {
            var key = await SecureStorage.Default.GetAsync($"apikey_{id}");
            if (!string.IsNullOrEmpty(key)) return key;

            // Migrazione: la vecchia chiave Gemini era salvata in chiaro nelle Preferences
            if (id == "gemini" && Preferences.Default.ContainsKey(LegacyApiKeyPreference))
            {
                var legacy = Preferences.Default.Get(LegacyApiKeyPreference, string.Empty);
                Preferences.Default.Remove(LegacyApiKeyPreference);
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    await SecureStorage.Default.SetAsync($"apikey_{id}", legacy);
                    return legacy;
                }
            }
        }
        catch
        {
            // SecureStorage può non essere disponibile (es. dati del keystore non più validi)
        }

        return string.Empty;
    }

    private static async Task SetApiKeyAsync(string id, string? apiKey)
    {
        var key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            SecureStorage.Default.Remove($"apikey_{id}");
        else
            await SecureStorage.Default.SetAsync($"apikey_{id}", key);
    }
}

/// <summary>Metodo di composizione del mosaico (impostazione "Metodo di composizione").</summary>
public enum CaptureMethod
{
    /// <summary>Punti caratteristici ORB di OpenCV: tutti i telefoni.</summary>
    OpenCv,
    /// <summary>Posa della fotocamera e piano del tavolo da ARCore: telefoni certificati, scala in mm.</summary>
    ArCore,
    /// <summary>ARCore per la posa, OpenCV per affinare l'allineamento.</summary>
    Hybrid
}
