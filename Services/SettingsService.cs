namespace LiveCoinCounter.Services;

/// <summary>
/// Gestisce le impostazioni persistenti dell'app (API key, preferenze).
/// </summary>
public static class SettingsService
{
    private const string ApiKeyPreference = "gemini_api_key";
    private const string CurrencyPreference = "currency_type";
    private const string DefaultApiKey = "AQ.Ab8RN6LGcR2sNSevUBwehTyHWyU7FBeA8u9ZFr_0SwxzUA00_g";

    public static string GeminiApiKey
    {
        get => Preferences.Default.Get(ApiKeyPreference, DefaultApiKey);
        set => Preferences.Default.Set(ApiKeyPreference, value);
    }

    public static string CurrencyType
    {
        get => Preferences.Default.Get(CurrencyPreference, "EUR");
        set => Preferences.Default.Set(CurrencyPreference, value);
    }

    public static bool HasApiKey => !string.IsNullOrWhiteSpace(GeminiApiKey);
}

