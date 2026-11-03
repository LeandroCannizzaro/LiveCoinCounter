namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Crea l'<see cref="HttpClient"/> per ogni tentativo di chiamata. Un client nuovo (e quindi nessuna connessione riusata)
/// evita i socket "morti" rimasti aperti su una rete che il telefono ha abbandonato, per esempio nel passaggio
/// tra Wi-Fi e dati mobili.
/// </summary>
public static class NetworkClient
{
    /// <summary>Sostituibile da chi usa la libreria (es. l'app può scegliere il gestore HTTP nativo di Android).</summary>
    public static Func<HttpClient> Factory { get; set; } = CreateDefault;

    public static HttpClient Create() => Factory();

    private static HttpClient CreateDefault() =>
        new(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
}
