using System.Security.Cryptography;
using LiveCoinCounter.Models;
using LiveCoinCounter.Services.Recognition;

namespace CoinLabeler;

/// <summary>
/// Crea i provider AI dal catalogo dell'app. Le chiavi arrivano solo da variabili d'ambiente:
/// <c>{ID}_API_KEY</c>, opzionalmente <c>{ID}_MODEL</c> e <c>{ID}_BASE_URL</c> (es. GEMINI_API_KEY, ANTHROPIC_API_KEY).
/// </summary>
public static class Providers
{
    public static ICoinRecognizer Create(string id, string? cacheDir)
    {
        var profile = RecognizerCatalog.All.FirstOrDefault(p => p.Id == id)
            ?? throw new ArgumentException($"Provider sconosciuto: {id}. Disponibili: " +
                string.Join(", ", RecognizerCatalog.All.Where(p => !p.IsOffline).Select(p => p.Id)));

        if (profile.IsOffline)
            throw new ArgumentException("Il metodo offline non si usa per etichettare.");

        var prefix = id.ToUpperInvariant();
        var key = Environment.GetEnvironmentVariable($"{prefix}_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable($"{prefix}_MODEL") ?? "";
        var baseUrl = Environment.GetEnvironmentVariable($"{prefix}_BASE_URL") ?? "";
        var config = new RecognizerConfig(key, model, baseUrl);

        ICoinRecognizer recognizer = profile.Kind switch
        {
            RecognizerKind.Gemini => new GeminiRecognizer(profile, config),
            RecognizerKind.Anthropic => new AnthropicRecognizer(profile, config),
            RecognizerKind.OpenAiCompatible => new OpenAiCompatibleRecognizer(profile, config),
            _ => throw new NotSupportedException()
        };

        if (!recognizer.IsConfigured)
            throw new InvalidOperationException($"Manca la variabile d'ambiente {prefix}_API_KEY per il provider {id}.");

        var label = $"{id}:{string.Join("|", config.ModelChain(profile))}";
        return cacheDir is null ? new Named(recognizer, label) : new CachingRecognizer(recognizer, label, cacheDir);
    }
}

/// <summary>Aggiunge a un recognizer un'etichetta leggibile (provider e modelli) da scrivere nei JSON.</summary>
public class Named : ICoinRecognizer
{
    protected readonly ICoinRecognizer Inner;

    public Named(ICoinRecognizer inner, string label)
    {
        Inner = inner;
        Label = label;
    }

    public string Label { get; }
    public string Id => Inner.Id;
    public string DisplayName => Inner.DisplayName;
    public bool RequiresNetwork => Inner.RequiresNetwork;
    public bool IsConfigured => Inner.IsConfigured;

    public virtual Task<CoinRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default) =>
        Inner.RecognizeAsync(imageBytes, ct);
}

/// <summary>
/// Salva su disco ogni risposta riuscita, così un'esecuzione interrotta riprende senza rifare (e ripagare) le chiamate.
/// La chiave comprende provider, modelli e contenuto dell'immagine.
/// </summary>
public sealed class CachingRecognizer : Named
{
    private readonly string _dir;

    public CachingRecognizer(ICoinRecognizer inner, string label, string cacheDir) : base(inner, label)
    {
        _dir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public override async Task<CoinRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        var hash = Convert.ToHexString(SHA256.HashData([.. imageBytes, .. System.Text.Encoding.UTF8.GetBytes(Label)]));
        var path = Path.Combine(_dir, hash + ".json");

        if (File.Exists(path))
        {
            var cached = System.Text.Json.JsonSerializer.Deserialize<CoinRecognitionResult>(await File.ReadAllTextAsync(path, ct));
            if (cached is not null) return cached;
        }

        var result = await Inner.RecognizeAsync(imageBytes, ct);
        if (result.Success)
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(result), ct);
        return result;
    }
}
