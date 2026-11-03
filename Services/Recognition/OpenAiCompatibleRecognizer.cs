using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Adapter per le API "chat completions" in stile OpenAI: OpenAI, xAI Grok, DeepSeek, Mistral, Groq,
/// OpenRouter e server propri (Ollama, LM Studio, vLLM...).
/// </summary>
public sealed class OpenAiCompatibleRecognizer : AiRecognizerBase
{
    public OpenAiCompatibleRecognizer(RecognizerProfile profile, RecognizerConfig config) : base(profile, config) { }

    protected override HttpRequestMessage BuildRequest(string model, string base64Jpeg)
    {
        // Niente temperature / max_tokens: alcuni modelli li rifiutano o usano un nome di parametro diverso.
        var body = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = UserPrompt },
                        new { type = "image_url", image_url = new { url = $"data:{MimeType};base64,{base64Jpeg}" } }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = JsonContent.Create(body)
        };

        if (!string.IsNullOrWhiteSpace(Config.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

        return request;
    }

    protected override string? ExtractText(JsonNode root) =>
        root["choices"]?[0]?["message"]?["content"]?.ToString();
}
