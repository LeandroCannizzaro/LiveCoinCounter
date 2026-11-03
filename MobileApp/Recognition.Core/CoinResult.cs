namespace LiveCoinCounter.Models;

/// <summary>
/// Rappresenta una moneta riconosciuta con il suo valore e quantità.
/// </summary>
public class CoinResult
{
    /// <summary>Denominazione della moneta (es. "2 euro", "50 cent")</summary>
    public string Denomination { get; set; } = string.Empty;

    /// <summary>Valore in centesimi per evitare errori floating-point</summary>
    public int ValueInCents { get; set; }

    /// <summary>Numero di monete di questa denominazione trovate</summary>
    public int Count { get; set; }

    /// <summary>Valore totale in centesimi per questa denominazione</summary>
    public int TotalValueInCents => ValueInCents * Count;

    /// <summary>Valore totale in euro formattato</summary>
    public string TotalValueFormatted => FormatCents(TotalValueInCents);

    /// <summary>Valore singola moneta formattato</summary>
    public string DenominationFormatted => FormatCents(ValueInCents);

    private static string FormatCents(int cents)
    {
        if (cents >= 100)
            return $"{cents / 100:D1},{cents % 100:D2} €";
        return $"{cents} ct";
    }

    public override string ToString() =>
        $"{Count}x {DenominationFormatted} = {TotalValueFormatted}";
}

/// <summary>Una singola moneta trovata da un rilevatore sul dispositivo: riquadro in pixel dell'immagine analizzata.</summary>
public record DetectedCoin(string Denomination, int ValueInCents, float Confidence, float X, float Y, float Width, float Height);

/// <summary>
/// Risultato complessivo dell'analisi di un'immagine.
/// </summary>
public class CoinRecognitionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<CoinResult> Coins { get; set; } = new();
    public int TotalValueInCents => Coins.Sum(c => c.TotalValueInCents);

    public string TotalValueFormatted
    {
        get
        {
            int cents = TotalValueInCents;
            return $"{cents / 100},{cents % 100:D2} €";
        }
    }

    public int TotalCoinCount => Coins.Sum(c => c.Count);

    /// <summary>Monete rilevate una per una (solo per i metodi sul dispositivo): servono a disegnare i riquadri sulla foto.</summary>
    public List<DetectedCoin> Detections { get; set; } = new();

    /// <summary>Motivo per cui il risultato potrebbe essere sbagliato, se il rilevatore ha dei dubbi.</summary>
    public string? Warning { get; set; }

    public bool IsUncertain => Warning is not null;

    /// <summary>Testo grezzo restituito dall'AI</summary>
    public string? RawResponse { get; set; }
}

