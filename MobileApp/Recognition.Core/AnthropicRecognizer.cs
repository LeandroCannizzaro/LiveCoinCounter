using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>Adapter per la Messages API di Anthropic (Claude).</summary>
public sealed class AnthropicRecognizer : AiRecognizerBase
{
    public AnthropicRecognizer(RecognizerProfile profile, RecognizerConfig config) : base(profile, config) { }

    protected override HttpRequestMessage BuildRequest(string model, string base64Jpeg)
    {
        var body = new
        {
            model,
            max_tokens = 2048,
            system = SystemPrompt,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = MimeType, data = base64Jpeg } },
                        new { type = "text", text = UserPrompt }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("x-api-key", Config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    protected override string? ExtractText(JsonNode root)
    {
        if (root["content"]?.AsArray() is not { } blocks) return null;

        foreach (var block in blocks)
            if (block?["type"]?.ToString() == "text")
                return block["text"]?.ToString();

        return null;
    }
}
