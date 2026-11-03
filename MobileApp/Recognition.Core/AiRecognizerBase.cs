using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiveCoinCounter.Models;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Parte comune dei provider AI: prompt, ridimensionamento foto, retry/fallback tra modelli e parsing del JSON.
/// Le sottoclassi descrivono solo come costruire la richiesta e leggere il testo dalla risposta.
/// </summary>
public abstract class AiRecognizerBase : ICoinRecognizer
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

    protected const string SystemPrompt = """
        Sei un esperto nel riconoscimento di monete. Analizza l'immagine e identifica tutte le monete visibili sul tavolo.

        IMPORTANTE:
        - Considera monete Euro (1ct, 2ct, 5ct, 10ct, 20ct, 50ct, 1€, 2€) come default
        - Se vedi monete di altra valuta, identificale comunque
        - Conta accuratamente anche le monete sovrapposte o parzialmente visibili
        - Rispondi SOLO con un JSON valido, senza altro testo

        Formato risposta JSON richiesto:
        {
          "coins": [
            {"denomination": "2 euro", "value_cents": 200, "count": 3},
            {"denomination": "1 euro", "value_cents": 100, "count": 5},
            {"denomination": "50 centesimi", "value_cents": 50, "count": 2},
            {"denomination": "20 centesimi", "value_cents": 20, "count": 4},
            {"denomination": "10 centesimi", "value_cents": 10, "count": 1},
            {"denomination": "5 centesimi", "value_cents": 5, "count": 0},
            {"denomination": "2 centesimi", "value_cents": 2, "count": 7},
            {"denomination": "1 centesimo", "value_cents": 1, "count": 3}
          ],
          "notes": "eventuali note sul riconoscimento"
        }

        Includi solo denominazioni con count > 0.
        Se non riesci a distinguere una moneta, approssima al valore più probabile.
        """;

    protected const string UserPrompt =
        "Analizza questa immagine e identifica tutte le monete visibili. Rispondi SOLO con JSON.";

    protected const string MimeType = "image/jpeg";

    protected AiRecognizerBase(RecognizerProfile profile, RecognizerConfig config)
    {
        Profile = profile;
        Config = config;
    }

    protected RecognizerProfile Profile { get; }
    protected RecognizerConfig Config { get; }

    /// <summary>Endpoint base: quello configurato, altrimenti il default del profilo.</summary>
    protected string BaseUrl =>
        (string.IsNullOrWhiteSpace(Config.BaseUrl) ? Profile.BaseUrl : Config.BaseUrl).TrimEnd('/');

    public string Id => Profile.Id;
    public string DisplayName => Profile.DisplayName;
    public bool RequiresNetwork => true;

    public bool IsConfigured =>
        (!Profile.RequiresApiKey || !string.IsNullOrWhiteSpace(Config.ApiKey)) &&
        Config.ModelChain(Profile).Count > 0;

    /// <summary>Costruisce la richiesta HTTP per un modello (viene creata di nuovo a ogni tentativo).</summary>
    protected abstract HttpRequestMessage BuildRequest(string model, string base64Jpeg);

    /// <summary>Estrae il testo generato dal corpo JSON della risposta.</summary>
    protected abstract string? ExtractText(JsonNode root);

    public async Task<CoinRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        try
        {
            var base64 = Convert.ToBase64String(ImageShrink.Shrinker(imageBytes));

            HttpResponseMessage? response = null;
            string responseText = string.Empty;
            Exception? lastNetworkError = null;

            foreach (var model in Config.ModelChain(Profile))
            {
                try
                {
                    using var request = BuildRequest(model, base64);
                    response = await Client.SendAsync(request, ct);
                    responseText = await response.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Connessione chiusa dal server (spesso per sovraccarico): prova il modello successivo
                    lastNetworkError = ex;
                    continue;
                }

                lastNetworkError = null;
                var code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode || (code != 503 && code != 429 && code != 404))
                    break;
            }

            if (lastNetworkError is not null)
                return Fail($"Errore di rete: {lastNetworkError.Message}");

            if (response is null)
                return Fail("Nessun modello configurato.");

            if (!response.IsSuccessStatusCode)
                return Fail($"Errore API ({response.StatusCode}): {responseText}");

            return ParseResponse(responseText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("Analisi annullata.");
        }
        catch (TaskCanceledException)
        {
            return Fail("Timeout: la richiesta ha impiegato troppo tempo.");
        }
        catch (Exception ex)
        {
            return Fail($"Errore: {ex.Message}");
        }
    }

    private CoinRecognitionResult ParseResponse(string responseText)
    {
        try
        {
            var root = JsonNode.Parse(responseText);
            var rawContent = root is null ? null : ExtractText(root);

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new CoinRecognitionResult
                {
                    Success = false,
                    ErrorMessage = "Risposta vuota dall'API.",
                    RawResponse = responseText
                };
            }

            var coinsData = JsonNode.Parse(ExtractJson(rawContent));

            var result = new CoinRecognitionResult { Success = true, RawResponse = rawContent };

            if (coinsData?["coins"]?.AsArray() is { } coinsArray)
            {
                foreach (var coinNode in coinsArray)
                {
                    var count = coinNode?["count"]?.GetValue<int>() ?? 0;
                    if (count <= 0) continue;

                    result.Coins.Add(new CoinResult
                    {
                        Denomination = coinNode?["denomination"]?.ToString() ?? "Sconosciuta",
                        ValueInCents = coinNode?["value_cents"]?.GetValue<int>() ?? 0,
                        Count = count
                    });
                }
            }

            result.Coins = result.Coins.OrderByDescending(c => c.ValueInCents).ToList();
            return result;
        }
        catch (Exception ex)
        {
            return new CoinRecognitionResult
            {
                Success = false,
                ErrorMessage = $"Errore nel parsing della risposta: {ex.Message}",
                RawResponse = responseText
            };
        }
    }

    /// <summary>
    /// Estrae JSON da una stringa che potrebbe contenere markdown code blocks.
    /// </summary>
    private static string ExtractJson(string text)
    {
        var match = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```");
        if (match.Success)
            return match.Groups[1].Value.Trim();

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        return text.Trim();
    }

    private static CoinRecognitionResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}
