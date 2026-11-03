using LiveCoinCounter.Services.Recognition;

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
