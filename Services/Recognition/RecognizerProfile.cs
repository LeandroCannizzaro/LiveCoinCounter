namespace LiveCoinCounter.Services.Recognition;

public enum RecognizerKind
{
    Gemini,
    Anthropic,
    OpenAiCompatible,
    Offline
}

/// <summary>
/// Descrive un provider selezionabile: protocollo, endpoint e modelli di default.
/// </summary>
public sealed record RecognizerProfile(
    string Id,
    string DisplayName,
    RecognizerKind Kind,
    string BaseUrl,
    string[] Models,
    bool RequiresApiKey,
    string KeyPlaceholder,
    string? KeyUrl,
    string Note,
    bool BaseUrlEditable = false)
{
    public bool IsOffline => Kind == RecognizerKind.Offline;
    public override string ToString() => DisplayName;
}

/// <summary>
/// Valori configurati dall'utente per un profilo.
/// </summary>
public sealed record RecognizerConfig(string ApiKey, string Model, string BaseUrl)
{
    /// <summary>Modello scelto dall'utente per primo, poi quelli di fallback del profilo.</summary>
    public IReadOnlyList<string> ModelChain(RecognizerProfile profile)
    {
        var chain = new List<string>();
        if (!string.IsNullOrWhiteSpace(Model)) chain.Add(Model.Trim());
        foreach (var m in profile.Models)
            if (!chain.Contains(m)) chain.Add(m);
        return chain;
    }
}

/// <summary>
/// Elenco dei provider disponibili. Gli adapter sono solo tre (Gemini, Anthropic, OpenAI-compatibile):
/// quasi tutti gli altri provider usano il protocollo "chat completions" di OpenAI.
/// </summary>
public static class RecognizerCatalog
{
    public const string DefaultId = "gemini";

    public static readonly IReadOnlyList<RecognizerProfile> All = new[]
    {
        new RecognizerProfile("gemini", "Google Gemini", RecognizerKind.Gemini,
            "https://generativelanguage.googleapis.com/v1beta/models",
            new[] { "gemini-3.8-flash", "gemini-3.5-flash", "gemini-2.5-flash", "gemini-3.1-flash-lite" },
            true, "AIza...", "https://aistudio.google.com/apikey",
            "Chiave gratuita da Google AI Studio."),

        new RecognizerProfile("anthropic", "Anthropic Claude", RecognizerKind.Anthropic,
            "https://api.anthropic.com",
            new[] { "claude-sonnet-5-5", "claude-haiku-5-5" },
            true, "sk-ant-...", "https://console.anthropic.com/settings/keys",
            "Chiave dalla console Anthropic."),

        new RecognizerProfile("openai", "OpenAI", RecognizerKind.OpenAiCompatible,
            "https://api.openai.com/v1",
            new[] { "gpt-4o", "gpt-4o-mini" },
            true, "sk-...", "https://platform.openai.com/api-keys",
            "Modelli di default da verificare: modificali se non sono più disponibili."),

        new RecognizerProfile("grok", "xAI Grok", RecognizerKind.OpenAiCompatible,
            "https://api.x.ai/v1",
            new[] { "grok-4" },
            true, "xai-...", "https://console.x.ai",
            "Modello di default da verificare: deve supportare le immagini."),

        new RecognizerProfile("deepseek", "DeepSeek", RecognizerKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            new[] { "deepseek-chat" },
            true, "sk-...", "https://platform.deepseek.com/api_keys",
            "Attenzione: i modelli DeepSeek potrebbero non accettare immagini. Se ricevi un errore, scegli un altro provider."),

        new RecognizerProfile("mistral", "Mistral", RecognizerKind.OpenAiCompatible,
            "https://api.mistral.ai/v1",
            new[] { "pixtral-large-latest" },
            true, "...", "https://console.mistral.ai/api-keys",
            "Modello di default da verificare."),

        new RecognizerProfile("openrouter", "OpenRouter", RecognizerKind.OpenAiCompatible,
            "https://openrouter.ai/api/v1",
            new[] { "google/gemini-2.5-flash" },
            true, "sk-or-...", "https://openrouter.ai/keys",
            "Un'unica chiave per molti modelli: scrivi il nome completo del modello (es. provider/modello)."),

        new RecognizerProfile("custom", "Endpoint personalizzato (OpenAI-compatibile)", RecognizerKind.OpenAiCompatible,
            "https://",
            new[] { "" },
            false, "chiave (se richiesta)", null,
            "Per server propri (Ollama, LM Studio, vLLM...). Serve un indirizzo HTTPS: l'app blocca il traffico HTTP in chiaro.",
            BaseUrlEditable: true),

        new RecognizerProfile("offline", "Offline (sperimentale)", RecognizerKind.Offline,
            "", Array.Empty<string>(),
            false, "", null,
            "Funziona senza rete e senza inviare la foto. Metodo sperimentale basato su dimensioni e colore: è meno accurato dell'AI.")
    };

    public static RecognizerProfile Get(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All[0];
}
