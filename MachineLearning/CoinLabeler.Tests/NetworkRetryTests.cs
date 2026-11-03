using System.Net;
using System.Net.Sockets;
using LiveCoinCounter.Services.Recognition;
using Xunit;

namespace CoinLabeler.Tests;

/// <summary>Simula una rete instabile (es. cambio Wi-Fi / dati mobili): socket chiuso, poi connessione nuova che funziona.</summary>
[Collection("NetworkClient")]
public class NetworkRetryTests : IDisposable
{
    private const string GeminiReply =
        """{"candidates":[{"content":{"parts":[{"text":"{\"coins\":[{\"denomination\":\"2 euro\",\"value_cents\":200,\"count\":1}]}"}]}}]}""";

    private readonly Func<HttpClient> _original = NetworkClient.Factory;

    public void Dispose() => NetworkClient.Factory = _original;

    private sealed class FlakyHandler : HttpMessageHandler
    {
        private readonly int _failures;
        public int Calls;

        public FlakyHandler(int failures) => _failures = failures;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls <= _failures)
                throw new HttpRequestException("Socket closed", new SocketException());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GeminiReply) });
        }
    }

    private static GeminiRecognizer Recognizer()
    {
        var profile = new RecognizerProfile("test", "Test", RecognizerKind.Gemini, "http://example.invalid/models",
            new[] { "m" }, true, "", null, "");
        return new GeminiRecognizer(profile, new RecognizerConfig("key", "", ""));
    }

    [Fact]
    public async Task Retries_with_a_new_connection_after_socket_errors()
    {
        var handlers = new List<FlakyHandler>();
        var shared = new FlakyHandler(failures: 2);
        NetworkClient.Factory = () => { handlers.Add(shared); return new HttpClient(new NoDispose(shared)); };

        var result = await Recognizer().RecognizeAsync(new byte[] { 1, 2, 3 });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(200, result.TotalValueInCents);
        Assert.Equal(3, shared.Calls);
        Assert.Equal(3, handlers.Count); // un client nuovo per ogni tentativo
    }

    [Fact]
    public async Task Reports_unstable_connection_when_every_attempt_fails()
    {
        var shared = new FlakyHandler(failures: int.MaxValue);
        NetworkClient.Factory = () => new HttpClient(new NoDispose(shared));

        var result = await Recognizer().RecognizeAsync(new byte[] { 1, 2, 3 });

        Assert.False(result.Success);
        Assert.Contains("Connessione instabile", result.ErrorMessage);
        Assert.Equal(3, shared.Calls); // 3 tentativi per il solo modello configurato
    }

    /// <summary>Permette di riusare lo stesso handler di test con client diversi (HttpClient lo eliminerebbe).</summary>
    private sealed class NoDispose : DelegatingHandler
    {
        public NoDispose(HttpMessageHandler inner) : base(inner) { }
        protected override void Dispose(bool disposing) { }
    }
}

[CollectionDefinition("NetworkClient", DisableParallelization = true)]
public class NetworkClientCollection { }
