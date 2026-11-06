using Microsoft.ML.OnnxRuntime;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Sessioni ONNX Runtime caricate dagli asset dell'app. Il caricamento costa (decine di MB): si tiene solo l'ultimo
/// modello usato. <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> è thread-safe.
/// </summary>
public static class OnnxModelCache
{
    private const int DefaultInputSize = 640;

    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private static InferenceSession? _session;
    private static string? _asset;

    public static async Task<InferenceSession> GetAsync(string modelAsset, CancellationToken ct = default)
    {
        await LoadLock.WaitAsync(ct);
        try
        {
            if (_session is null || _asset != modelAsset)
            {
                await using var stream = await FileSystem.OpenAppPackageFileAsync(modelAsset);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, ct);
                var loaded = new InferenceSession(memory.ToArray());
                _session?.Dispose();
                _session = loaded;
                _asset = modelAsset;
            }
            return _session;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    /// <summary>Lato dell'ingresso quadrato del modello, letto dal file ([1, 3, lato, lato]).</summary>
    public static int InputSize(InferenceSession session)
    {
        var dims = session.InputMetadata[session.InputMetadata.Keys.First()].Dimensions;
        return dims.Length == 4 && dims[2] > 0 ? dims[2] : DefaultInputSize;
    }

    /// <summary>Il messaggio esterno di un errore di inizializzazione (es. libreria nativa) non dice la causa: si aggiunge la catena delle eccezioni interne.</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 4; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" -> ", parts);
    }
}
