using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiveCoinCounter.Models;

namespace LiveCoinCounter.Services;

/// <summary>
/// Servizio che usa l'API Gemini Vision per riconoscere le monete in un'immagine.
/// </summary>
public class GeminiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/models";

    // Provati in ordine: se uno è sovraccarico (503) si passa al successivo.
    private static readonly string[] Models =
    {
        "gemini-3.8-flash",
        "gemini-3.5-flash",
        "gemini-2.5-flash",
        "gemini-3.1-flash-lite"
    };

    private const string SystemPrompt = """
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

    public GeminiService(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    /// <summary>
    /// Analizza un'immagine e restituisce le monete riconosciute.
    /// </summary>
    public async Task<CoinRecognitionResult> RecognizeCoinsAsync(byte[] imageBytes, string mimeType = "image/jpeg")
    {
        try
        {
            var base64Image = Convert.ToBase64String(ImageHelper.ShrinkForUpload(imageBytes));
            mimeType = "image/jpeg";

            var requestBody = new
            {
                system_instruction = new
                {
                    parts = new[] { new { text = SystemPrompt } }
                },
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = mimeType,
                                    data = base64Image
                                }
                            },
                            new
                            {
                                text = "Analizza questa immagine e identifica tutte le monete visibili. Rispondi SOLO con JSON."
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.1,
                    maxOutputTokens = 8192, // i modelli recenti "pensano" prima di rispondere: con pochi token il JSON viene troncato
                    responseMimeType = "application/json"
                }
            };

            var json = JsonSerializer.Serialize(requestBody);

            HttpResponseMessage response = null!;
            string responseText = string.Empty;
            Exception? lastNetworkError = null;

            foreach (var model in Models)
            {
                var url = $"{ApiBase}/{model}:generateContent?key={_apiKey}";
                using var request = new StringContent(json, Encoding.UTF8, "application/json");
                try
                {
                    response = await _httpClient.PostAsync(url, request);
                    responseText = await response.Content.ReadAsStringAsync();
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
            {
                return new CoinRecognitionResult
                {
                    Success = false,
                    ErrorMessage = $"Errore di rete: {lastNetworkError.Message}"
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new CoinRecognitionResult
                {
                    Success = false,
                    ErrorMessage = $"Errore API ({response.StatusCode}): {responseText}"
                };
            }

            return ParseGeminiResponse(responseText);
        }
        catch (TaskCanceledException)
        {
            return new CoinRecognitionResult
            {
                Success = false,
                ErrorMessage = "Timeout: la richiesta ha impiegato troppo tempo."
            };
        }
        catch (Exception ex)
        {
            return new CoinRecognitionResult
            {
                Success = false,
                ErrorMessage = $"Errore: {ex.Message}"
            };
        }
    }

    private static CoinRecognitionResult ParseGeminiResponse(string responseText)
    {
        try
        {
            var root = JsonNode.Parse(responseText);
            var rawContent = root?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new CoinRecognitionResult
                {
                    Success = false,
                    ErrorMessage = "Risposta vuota dall'API.",
                    RawResponse = responseText
                };
            }

            // Estrai JSON anche se è avvolto in markdown code block
            var jsonContent = ExtractJson(rawContent);
            var coinsData = JsonNode.Parse(jsonContent);

            var result = new CoinRecognitionResult
            {
                Success = true,
                RawResponse = rawContent
            };

            var coinsArray = coinsData?["coins"]?.AsArray();
            if (coinsArray is not null)
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

            // Ordina per valore decrescente
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
        // Cerca un blocco ```json ... ```
        var match = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```");
        if (match.Success)
            return match.Groups[1].Value.Trim();

        // Cerca il primo { fino all'ultimo }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        return text.Trim();
    }
}

