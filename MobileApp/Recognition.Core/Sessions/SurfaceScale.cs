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

/// <summary>La spaziatura della griglia metrica (impostazione "Griglia metrica").</summary>
public enum GridSpacing
{
    Off = 0,
    Cm1 = 1,
    Cm2 = 2,
    Cm5 = 5,
    Cm10 = 10
}
