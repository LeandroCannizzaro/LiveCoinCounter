using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services.Recognition;

namespace LiveCoinCounter.Sessions;

/// <summary>Una moneta trovata in un'immagine. <see cref="Cents"/> è valorizzato solo se il rilevatore dà anche il taglio.</summary>
public sealed record CoinBox(RectF Box, float Confidence, int? Cents = null, float ClassConfidence = 0);

/// <summary>Fase 1: dove sono le monete (nessun riconoscimento del valore richiesto).</summary>
public interface ICoinLocator
{
    string Id { get; }
    Task<IReadOnlyList<CoinBox>> LocateAsync(ArgbImage image, CancellationToken ct = default);
}

/// <summary>Ritaglio di una moneta da riconoscere, con il contesto utile al classificatore.</summary>
/// <param name="Jpeg">Il ritaglio quadrato (JPEG).</param>
/// <param name="DiameterPx">Diametro nel mosaico: tutte le monete di una sessione sono alla stessa scala.</param>
/// <param name="PxPerMm">Scala del mosaico in pixel per millimetro, se nota (ARCore).</param>
public sealed record CoinCrop(int CoinId, byte[] Jpeg, float DiameterPx, double? PxPerMm = null);

public sealed record CoinClassification(int Cents, float Confidence, IReadOnlyList<(int Cents, float Score)> TopK, string ClassifierId, string? Note = null);

/// <summary>Fase 2: il taglio di una singola moneta.</summary>
public interface ICoinClassifier
{
    string Id { get; }
    bool RequiresNetwork { get; }
    Task<CoinClassification?> ClassifyAsync(CoinCrop crop, CancellationToken ct = default);
}

/// <summary>Le monete euro: valore e diametro ufficiale.</summary>
public static class EuroCoins
{
    public static readonly IReadOnlyList<(int Cents, string Name, double DiameterMm)> All = new[]
    {
        (1, "1 centesimo", 16.25), (2, "2 centesimi", 18.75), (5, "5 centesimi", 21.25), (10, "10 centesimi", 19.75),
        (20, "20 centesimi", 22.25), (50, "50 centesimi", 24.25), (100, "1 euro", 23.25), (200, "2 euro", 25.75)
    };

    public static double DiameterOf(int cents) => All.First(c => c.Cents == cents).DiameterMm;

    public static string NameOf(int cents) => All.FirstOrDefault(c => c.Cents == cents).Name ?? $"{cents} ct";

    public static bool IsKnown(int cents) => All.Any(c => c.Cents == cents);

    public static string Format(int cents) => cents >= 100 ? $"{cents / 100},{cents % 100:D2} €" : $"{cents} ct";
}

/// <summary>
/// Classificatore che usa un riconoscitore AI in rete su un singolo ritaglio. L'AI lavora solo qui: non vede mai
/// i fotogrammi né il mosaico. Non dà una confidenza: si usa un valore fisso alto, distinguibile nell'elenco.
/// </summary>
public sealed class AiCropClassifier : ICoinClassifier
{
    public const float AssumedConfidence = 0.9f;
    private readonly ICoinRecognizer _recognizer;

    public AiCropClassifier(ICoinRecognizer recognizer) => _recognizer = recognizer;

    public string Id => "ai:" + _recognizer.Id;
    public bool RequiresNetwork => true;

    public async Task<CoinClassification?> ClassifyAsync(CoinCrop crop, CancellationToken ct = default)
    {
        var result = await _recognizer.RecognizeAsync(crop.Jpeg, ct);
        if (!result.Success) return null;

        var known = result.Coins.Where(c => EuroCoins.IsKnown(c.ValueInCents) && c.Count > 0).ToList();
        if (known.Count == 0) return null;
        var best = known.OrderByDescending(c => c.Count).First();
        var note = known.Count > 1 || best.Count > 1 ? "L'AI ha visto più di una moneta nel ritaglio" : null;
        return new CoinClassification(best.ValueInCents, note is null ? AssumedConfidence : 0.6f,
            new[] { (best.ValueInCents, AssumedConfidence) }, Id, note);
    }
}

/// <summary>
/// Controllo sul diametro relativo: nel mosaico tutte le monete hanno la stessa scala, quindi il rapporto tra i diametri
/// misurati deve rispettare quello ufficiale. Una moneta incoerente perde confidenza, o passa al secondo taglio se quello
/// torna con il diametro.
/// </summary>
public static class DiameterCheck
{
    public const double Tolerance = 0.07;
    public const float ConfidentThreshold = 0.7f;

    public sealed record Item(int CoinId, float DiameterPx, CoinClassification Classification);

    public static double? EstimatePxPerMm(IEnumerable<Item> items)
    {
        var ratios = items.Where(i => i.Classification.Confidence >= ConfidentThreshold && i.DiameterPx > 0)
            .Select(i => i.DiameterPx / EuroCoins.DiameterOf(i.Classification.Cents))
            .OrderBy(r => r).ToList();
        return ratios.Count < 3 ? null : ratios[ratios.Count / 2];
    }

    public static IReadOnlyList<Item> Apply(IReadOnlyList<Item> items, double? knownPxPerMm = null)
    {
        var scale = knownPxPerMm ?? EstimatePxPerMm(items);
        if (scale is null or <= 0) return items;

        return items.Select(item =>
        {
            var c = item.Classification;
            var mm = item.DiameterPx / scale.Value;
            double Error(int cents) => Math.Abs(mm - EuroCoins.DiameterOf(cents)) / EuroCoins.DiameterOf(cents);
            if (Error(c.Cents) <= Tolerance) return item;

            var alternative = c.TopK.Where(k => k.Cents != c.Cents && k.Score >= 0.15f && Error(k.Cents) <= Tolerance * 0.6)
                .OrderByDescending(k => k.Score).Cast<(int Cents, float Score)?>().FirstOrDefault();
            if (alternative is { } alt)
                return item with
                {
                    Classification = c with
                    {
                        Cents = alt.Cents,
                        Confidence = Math.Min(c.Confidence, Math.Max(alt.Score, 0.5f)),
                        Note = $"Taglio corretto con il diametro ({mm:0.0} mm)"
                    }
                };

            return item with { Classification = c with { Confidence = c.Confidence * 0.6f, Note = $"Diametro incoerente ({mm:0.0} mm)" } };
        }).ToList();
    }
}
