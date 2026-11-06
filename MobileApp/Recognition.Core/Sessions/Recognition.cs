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
/// Controllo incrociato sul colore: il rame (1, 2, 5 cent) si distingue dall'oro (10, 20, 50 cent, e il centro di 2 euro)
/// dalla tinta del ritaglio. Una moneta color oro letta come 1/2/5 cent (o una color rame letta come 10 cent o più) cambia
/// taglio se un'altra ipotesi (secondo candidato, rilevatore) è compatibile con il colore; altrimenti perde confidenza.
/// Le monete di tinta incerta (argento, riflessi) non si controllano.
/// </summary>
public static class ColorCheck
{
    public static bool IsCopperDenomination(int cents) => cents <= 5;

    /// <param name="tint">"copper", "gold" o altro (nessun controllo).</param>
    /// <param name="extra">Altre ipotesi (es. il taglio dato dal rilevatore nel fotogramma intero).</param>
    public static CoinClassification Apply(CoinClassification c, string? tint, IEnumerable<(int Cents, float Score)>? extra = null)
    {
        if (tint is not (CoinColor.Copper or CoinColor.Gold)) return c;
        var copper = tint == CoinColor.Copper;
        bool Fits(int cents) => IsCopperDenomination(cents) == copper;
        if (Fits(c.Cents)) return c;

        var alternative = c.TopK.Concat(extra ?? Array.Empty<(int, float)>())
            .Where(k => k.Cents != c.Cents && k.Score >= 0.05f && Fits(k.Cents) && EuroCoins.IsKnown(k.Cents))
            .OrderByDescending(k => k.Score).Cast<(int Cents, float Score)?>().FirstOrDefault();
        var name = copper ? "rame" : "oro";
        if (alternative is { } alt)
            return c with
            {
                Cents = alt.Cents,
                Confidence = Math.Min(c.Confidence, Math.Max(alt.Score, 0.5f)),
                Note = $"Taglio corretto con il colore ({name})"
            };

        return c with { Confidence = c.Confidence * 0.6f, Note = $"Colore incoerente: la moneta è color {name}" };
    }
}

/// <summary>
/// Controllo sul diametro relativo: nel mosaico tutte le monete hanno la stessa scala, quindi il rapporto tra i diametri
/// misurati deve rispettare quello ufficiale. Una moneta incoerente perde confidenza, o passa al secondo taglio se quello
/// torna con il diametro.
/// </summary>
public static class DiameterCheck
{
    /// <summary>Scala nota (ARCore): tolleranza stretta.</summary>
    public const double Tolerance = 0.07;

    /// <summary>Scala locale (monete vicine): la prospettiva cambia poco da una moneta alla vicina.</summary>
    public const double LocalTolerance = 0.09;

    /// <summary>Scala stimata su tutta la superficie: se il telefono era inclinato il mosaico non ha scala uniforme, serve più margine.</summary>
    public const double GlobalTolerance = 0.12;

    public const float ConfidentThreshold = 0.7f;

    /// <summary>Le monete a meno di questi diametri contano come "vicine" per la scala locale.</summary>
    public const double NeighbourRadiusDiameters = 6;

    /// <param name="X">Centro nel mosaico; NaN se non noto (si usa allora la scala di tutta la sessione).</param>
    /// <param name="Tint">Colore del ritaglio ("copper" o "gold"): il diametro non può riportare a un taglio di un altro colore.</param>
    public sealed record Item(int CoinId, float DiameterPx, CoinClassification Classification, float X = float.NaN, float Y = float.NaN, string? Tint = null);

    public static double? EstimatePxPerMm(IEnumerable<Item> items)
    {
        var ratios = items.Where(i => i.Classification.Confidence >= ConfidentThreshold && i.DiameterPx > 0)
            .Select(i => i.DiameterPx / EuroCoins.DiameterOf(i.Classification.Cents))
            .OrderBy(r => r).ToList();
        return ratios.Count < 3 ? null : ratios[ratios.Count / 2];
    }

    /// <summary>Scala (px/mm) misurata sulle monete sicure vicine a questa (almeno due), o null se non ci sono o le posizioni non sono note.</summary>
    public static double? LocalScale(Item item, IReadOnlyList<Item> items)
    {
        if (float.IsNaN(item.X) || float.IsNaN(item.Y)) return null;
        var radius = NeighbourRadiusDiameters * item.DiameterPx;
        var ratios = items
            .Where(o => o.CoinId != item.CoinId && !float.IsNaN(o.X) && o.Classification.Confidence >= ConfidentThreshold && o.DiameterPx > 0 &&
                        Math.Sqrt((o.X - item.X) * (o.X - item.X) + (o.Y - item.Y) * (o.Y - item.Y)) <= radius)
            .Select(o => o.DiameterPx / EuroCoins.DiameterOf(o.Classification.Cents))
            .OrderBy(r => r).ToList();
        return ratios.Count < 2 ? null : ratios[ratios.Count / 2];
    }

    /// <summary>Con una scala misurata dai marker la tolleranza è stretta: vale vicino al marker.</summary>
    public const double MarkerTolerance = 0.07;

    /// <summary>Il diametro coincide con quello atteso entro questo errore: il marker conferma il taglio e la confidenza sale.</summary>
    public const double MarkerConfirmError = 0.05;

    /// <param name="markers">I marker di scala della sessione: dove ce n'è uno vicino la scala è quella misurata, e vale più di ogni stima.</param>
    public static IReadOnlyList<Item> Apply(IReadOnlyList<Item> items, double? knownPxPerMm = null, IReadOnlyList<MarkerLandmark>? markers = null)
    {
        var global = knownPxPerMm ?? EstimatePxPerMm(items);
        var hasMarkers = markers is { Count: > 0 };
        var hasGlobal = global is > 0;
        if (!hasGlobal && !hasMarkers) return items;
        global ??= 1;

        return items.Select(item =>
        {
            var c = item.Classification;

            // scala nota > scala delle monete vicine > scala di tutta la sessione
            var scale = global.Value;
            var tolerance = knownPxPerMm is null ? GlobalTolerance : Tolerance;
            var fromMarker = false;
            if (hasMarkers && !float.IsNaN(item.X) && MarkerScale.At(item.X, item.Y, markers!) is { } markerScale)
            {
                scale = markerScale;
                tolerance = MarkerTolerance;
                fromMarker = true;
            }
            else if (!hasGlobal)
            {
                return item;                  // niente scala per questa moneta
            }
            else if (knownPxPerMm is null && LocalScale(item, items) is { } local)
            {
                scale = local;
                tolerance = LocalTolerance;
            }

            var mm = item.DiameterPx / scale;
            double Error(int cents) => Math.Abs(mm - EuroCoins.DiameterOf(cents)) / EuroCoins.DiameterOf(cents);
            if (fromMarker && Error(c.Cents) <= MarkerConfirmError && c.Confidence >= 0.3f)
            {
                // il marker conferma la dimensione: più sicuri (senza arrivare a 1)
                return item with
                {
                    Classification = c with
                    {
                        Confidence = 1 - (1 - c.Confidence) * 0.6f,
                        Note = $"Dimensione confermata dal marker ({mm:0.0} mm)"
                    }
                };
            }
            if (Error(c.Cents) <= tolerance) return item;

            // il controllo sul colore ha già scartato i tagli di un altro colore: il diametro non li deve far tornare
            bool ColorFits(int cents) => item.Tint is not (CoinColor.Copper or CoinColor.Gold) || ColorCheck.IsCopperDenomination(cents) == (item.Tint == CoinColor.Copper);
            var alternative = c.TopK.Where(k => k.Cents != c.Cents && k.Score >= 0.15f && Error(k.Cents) <= tolerance * 0.6 && ColorFits(k.Cents))
                .OrderByDescending(k => k.Score).Cast<(int Cents, float Score)?>().FirstOrDefault();
            if (alternative is { } alt)
                return item with
                {
                    Classification = c with
                    {
                        Cents = alt.Cents,
                        Confidence = Math.Min(c.Confidence, Math.Max(alt.Score, 0.5f)),
                        Note = fromMarker ? $"Taglio corretto con il marker ({mm:0.0} mm)" : $"Taglio corretto con il diametro ({mm:0.0} mm)"
                    }
                };

            return item with { Classification = c with { Confidence = c.Confidence * 0.6f, Note = fromMarker ? $"Diametro incoerente con il marker ({mm:0.0} mm)" : $"Diametro incoerente ({mm:0.0} mm)" } };
        }).ToList();
    }
}
