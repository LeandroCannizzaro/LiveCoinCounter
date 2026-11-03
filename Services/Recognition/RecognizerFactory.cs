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
        return Create(profile, config);
    }

    public static ICoinRecognizer Create(RecognizerProfile profile, RecognizerConfig config) =>
        profile.Kind switch
        {
            RecognizerKind.Gemini => new GeminiRecognizer(profile, config),
            RecognizerKind.Anthropic => new AnthropicRecognizer(profile, config),
            RecognizerKind.OpenAiCompatible => new OpenAiCompatibleRecognizer(profile, config),
            RecognizerKind.Offline => new OfflineRecognizer(),
            _ => throw new NotSupportedException($"Provider non supportato: {profile.Kind}")
        };
}
