namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Crea il recognizer corretto per un profilo.
/// </summary>
public static class RecognizerFactory
{
    public static async Task<ICoinRecognizer> CreateActiveAsync()
    {
        var profile = RecognizerCatalog.Get(SettingsService.ActiveRecognizerId);
        var config = await SettingsService.GetConfigAsync(profile);
        return await CreateWithFallbackAsync(profile, config);
    }

    /// <summary>
    /// Ultimo ripiego per i metodi AI: se il provider scelto non ha una chiave, si usa Gemini
    /// con la chiave salvata o, in mancanza, quella predefinita dell'app.
    /// </summary>
    public static async Task<ICoinRecognizer> CreateWithFallbackAsync(RecognizerProfile profile, RecognizerConfig config)
    {
        var recognizer = Create(profile, config);
        if (recognizer.IsConfigured) return recognizer;

        var gemini = RecognizerCatalog.Get("gemini");
        var geminiConfig = profile.Id == gemini.Id ? config : await SettingsService.GetConfigAsync(gemini);
        if (string.IsNullOrWhiteSpace(geminiConfig.ApiKey))
            geminiConfig = geminiConfig with { ApiKey = SettingsService.FallbackGeminiApiKey };

        return Create(gemini, geminiConfig);
    }

    public static ICoinRecognizer Create(RecognizerProfile profile, RecognizerConfig config) =>
        profile.Kind switch
        {
            RecognizerKind.Gemini => new GeminiRecognizer(profile, config),
            RecognizerKind.Anthropic => new AnthropicRecognizer(profile, config),
            RecognizerKind.OpenAiCompatible => new OpenAiCompatibleRecognizer(profile, config),
            RecognizerKind.Offline => new OfflineRecognizer(),
            RecognizerKind.OnDeviceModel => new OnnxCoinRecognizer(),
            _ => throw new NotSupportedException($"Provider non supportato: {profile.Kind}")
        };
}
