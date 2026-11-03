using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>Adapter per l'API Gemini <c>generateContent</c>.</summary>
public sealed class GeminiRecognizer : AiRecognizerBase
{
    public GeminiRecognizer(RecognizerProfile profile, RecognizerConfig config) : base(profile, config) { }

    protected override HttpRequestMessage BuildRequest(string model, string base64Jpeg)
    {
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = MimeType, data = base64Jpeg } },
                        new { text = UserPrompt }
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

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{model}:generateContent")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("x-goog-api-key", Config.ApiKey);
        return request;
    }

    protected override string? ExtractText(JsonNode root) =>
        root["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
}
