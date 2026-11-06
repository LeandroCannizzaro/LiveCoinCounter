using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Sessions;

/// <summary>Una scala per la griglia metrica: pixel del mosaico per millimetro e da dove viene.</summary>
public sealed record SurfaceScale(double PxPerMm, bool Measured)
{
    public string Label => Measured ? "scala misurata (ARCore)" : "scala stimata dalle monete: indicativa";

    /// <summary>Scala nota se c'è (ARCore), altrimenti stimata dalle monete sicure (almeno 3); null se non si può.</summary>
    public static SurfaceScale? From(double? knownPxPerMm, IEnumerable<(float DiameterPx, int Cents, float Confidence)> coins)
    {
        if (knownPxPerMm is > 0) return new SurfaceScale(knownPxPerMm.Value, Measured: true);
        var ratios = coins.Where(c => c.Confidence >= DiameterCheck.ConfidentThreshold && c.DiameterPx > 0 && EuroCoins.IsKnown(c.Cents))
            .Select(c => c.DiameterPx / EuroCoins.DiameterOf(c.Cents)).OrderBy(r => r).ToList();
        return ratios.Count < 3 ? null : new SurfaceScale(ratios[ratios.Count / 2], Measured: false);
    }

    /// <summary>Dalle monete in corso di acquisizione: serve il taglio del rilevatore con confidenza alta.</summary>
    public static SurfaceScale? FromTracks(double? knownPxPerMm, IReadOnlyList<CoinTrack> tracks) =>
        From(knownPxPerMm, tracks.Where(t => t.DetectorCents is not null).Select(t => (t.MosaicBox.Diameter, t.DetectorCents!.Value, t.DetectorClassConfidence)));
}

/// <summary>Come si mostra la superficie acquisita (impostazione "Superficie acquisita").</summary>
public enum SurfaceLook
{
    /// <summary>Un piano grigio ardesia continuo e pieno con sopra solo le monete (predefinito).</summary>
    Continuous,
    /// <summary>Il piano solo dove ci sono i fotogrammi acquisiti: si vede quanto e come è stato coperto.</summary>
    Frames,
    /// <summary>La foto reale del tavolo.</summary>
    Photo
}

/// <summary>
/// La griglia metrica a due livelli (impostazioni "Griglia: passo fine" e "Griglia: passo principale"): righe sottili ogni
/// <see cref="MinorCm"/> cm e righe marcate ogni <see cref="MajorCm"/> cm. 0 = quel livello è spento.
/// </summary>
public sealed record GridConfig(int MinorCm, int MajorCm)
{
    public static readonly int[] MinorChoices = { 0, 1, 2, 5 };
    public static readonly int[] MajorChoices = { 0, 5, 10, 20, 50 };

    public static GridConfig Off { get; } = new(0, 0);

    public bool IsOff => MinorCm <= 0 && MajorCm <= 0;

    /// <summary>
    /// Valori validi: non negativi, e il passo principale non più fine del passo fine. Le righe dei due livelli si disegnano
    /// ciascuna sui propri multipli (con 2 cm e 5 cm una riga ogni 10 cm coincide con entrambi).
    /// </summary>
    public GridConfig Normalized()
    {
        var minor = Math.Max(0, MinorCm);
        var major = Math.Max(0, MajorCm);
        if (minor > 0 && major > 0 && major < minor) major = minor;
        return new GridConfig(minor, major);
    }

    /// <summary>La descrizione per la legenda.</summary>
    public string Label => IsOff ? "senza griglia"
        : MinorCm > 0 && MajorCm > 0 ? $"righe ogni {MinorCm} cm, marcate ogni {MajorCm} cm"
        : MinorCm > 0 ? $"righe ogni {MinorCm} cm"
        : $"righe ogni {MajorCm} cm";

    /// <summary>Dalle vecchie impostazioni a un solo livello (1, 2, 5, 10 cm).</summary>
    public static GridConfig FromLegacy(string? legacy) => legacy switch
    {
        "Cm1" => new GridConfig(1, 5),
        "Cm2" => new GridConfig(2, 10),
        "Cm5" => new GridConfig(5, 10),
        "Cm10" => new GridConfig(0, 10),
        _ => Off
    };
}
