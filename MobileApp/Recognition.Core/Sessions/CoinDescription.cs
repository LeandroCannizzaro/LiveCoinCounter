namespace LiveCoinCounter.Sessions;

/// <summary>Il testo con le caratteristiche di una moneta rilevata: riconoscimento, rilevatore, dimensione, colore. Lo usano la superficie a tutto schermo e la pagina di modifica.</summary>
public static class CoinDescription
{
    public static string Describe(CoinRecord c, SurfaceScale? scale)
    {
        static string Value(int? cents) => cents is null ? "?" : cents >= 100 ? $"{cents / 100} €" : $"{cents} cent";
        var lines = new List<string>
        {
            $"Moneta {c.Number}: {(c.State == CoinState.Rejected ? "scartata" : Value(c.EffectiveCents))}" + (c.CorrectedCents is not null ? " (corretta a mano)" : string.Empty),
            $"Riconoscimento: {Value(c.Cents)} con confidenza {c.ClassConfidence:P0}" + (c.SecondCents is not null ? $" · alternativa {Value(c.SecondCents)} {c.SecondScore:P0}" : string.Empty),
            $"Rilevatore: {Value(c.DetectorCents)} {c.DetectorClassConfidence:P0} · moneta trovata con confidenza {c.DetectionConfidence:P0} in {c.Observations} inquadrature"
        };
        var diameterPx = (c.Width + c.Height) / 2;
        var size = $"Dimensione nel mosaico: {diameterPx:0} px";
        if (scale is { PxPerMm: > 0 })
        {
            var mm = diameterPx / scale.PxPerMm;
            size += $" ≈ {mm:0.0} mm";
            if (c.EffectiveCents is { } known && EuroCoins.IsKnown(known)) size += $" (ufficiale {EuroCoins.DiameterOf(known):0.##} mm)";
            if (!scale.Measured) size += " · scala stimata";
        }
        lines.Add(size);
        if (!string.IsNullOrEmpty(c.ColorTint) && c.ColorTint != "?")
            lines.Add($"Colore: {(c.ColorTint == "copper" ? "rame" : "oro")} (tinta {c.ColorHue:0}°, saturazione {c.ColorSat:P0})");
        if (!string.IsNullOrWhiteSpace(c.Note)) lines.Add($"Nota: {c.Note}");
        if (!string.IsNullOrEmpty(c.ClassifierId)) lines.Add($"Classificatore: {c.ClassifierId}");
        return string.Join(Environment.NewLine, lines);
    }
}
