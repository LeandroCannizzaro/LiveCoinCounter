namespace LiveCoinCounter.Mosaic;

/// <summary>
/// Colore medio di una moneta in tinta (0–360°), saturazione e luminosità (0–1), separato per il centro e per l'anello
/// esterno: le monete bimetalliche (1 e 2 euro) hanno due colori diversi, le altre uno solo. Serve per distinguere i tre
/// gruppi (rame: 1, 2, 5 cent; oro nordico: 10, 20, 50 cent; bimetalliche) e finora si registra soltanto nel diario di debug.
/// </summary>
public sealed record CoinColor(double CoreHue, double CoreSat, double CoreVal, double RingHue, double RingSat, double RingVal)
{
    /// <summary>Gruppo dedotto dal colore, "?" se la tinta non è affidabile (poca saturazione: argento o riflesso).</summary>
    public string Guess
    {
        get
        {
            // bimetalliche: centro e anello hanno saturazione molto diversa (argento contro oro) o tinte diverse
            var hueGap = Math.Abs(CoreHue - RingHue);
            hueGap = Math.Min(hueGap, 360 - hueGap);
            if (Math.Abs(CoreSat - RingSat) > 0.15 || (hueGap > 25 && CoreSat > 0.12 && RingSat > 0.12)) return "bimetal";
            if (CoreSat < 0.12) return "?";
            return CoreHue is < 30 or > 340 ? "copper" : CoreHue <= 65 ? "gold" : "?";
        }
    }
}

public static class CoinColorAnalysis
{
    /// <summary>La moneta riempie il ritaglio quadrato (<see cref="ImageOps.CropSquare"/>): centro = disco interno, anello = corona esterna.</summary>
    public static CoinColor Analyze(ArgbImage crop)
    {
        double cx = crop.Width / 2.0, cy = crop.Height / 2.0, radius = Math.Min(crop.Width, crop.Height) / 2.0 * 0.92;
        double coreR = 0, coreG = 0, coreB = 0, ringR = 0, ringG = 0, ringB = 0;
        long coreN = 0, ringN = 0;

        // un pixel su due in ogni direzione: la media non cambia e costa un quarto
        for (var y = 0; y < crop.Height; y += 2)
        for (var x = 0; x < crop.Width; x += 2)
        {
            var r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / radius;
            if (r > 1) continue;
            var p = crop.Pixels[y * crop.Width + x];
            double pr = (p >> 16) & 0xFF, pg = (p >> 8) & 0xFF, pb = p & 0xFF;
            if (r < 0.5) { coreR += pr; coreG += pg; coreB += pb; coreN++; }
            else if (r is > 0.78 and <= 1) { ringR += pr; ringG += pg; ringB += pb; ringN++; }
        }

        var (ch, cs, cv) = Hsv(coreR, coreG, coreB, coreN);
        var (rh, rs, rv) = Hsv(ringR, ringG, ringB, ringN);
        return new CoinColor(Math.Round(ch), Math.Round(cs, 2), Math.Round(cv, 2), Math.Round(rh), Math.Round(rs, 2), Math.Round(rv, 2));
    }

    private static (double H, double S, double V) Hsv(double sumR, double sumG, double sumB, long n)
    {
        if (n == 0) return (0, 0, 0);
        double r = sumR / n / 255, g = sumG / n / 255, b = sumB / n / 255;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double h;
        if (delta < 1e-9) h = 0;
        else if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * ((b - r) / delta + 2);
        else h = 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360;
        return (h, max < 1e-9 ? 0 : delta / max, max);
    }
}
