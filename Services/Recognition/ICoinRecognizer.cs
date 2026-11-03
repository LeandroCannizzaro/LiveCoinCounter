using LiveCoinCounter.Models;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Metodo di riconoscimento delle monete (AI cloud o offline).
/// </summary>
public interface ICoinRecognizer
{
    /// <summary>Identificativo del profilo (es. "gemini", "anthropic", "offline").</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>False per i metodi che funzionano senza rete.</summary>
    bool RequiresNetwork { get; }

    /// <summary>True se chiave / configurazione necessarie sono presenti.</summary>
    bool IsConfigured { get; }

    Task<CoinRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default);
}
