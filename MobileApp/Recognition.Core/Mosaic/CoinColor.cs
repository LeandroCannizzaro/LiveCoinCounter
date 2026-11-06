namespace LiveCoinCounter.Mosaic;

/// <summary>
/// Colore medio di una moneta in tinta (0–360°), saturazione e luminosità (0–1), per il centro e per l'anello esterno.
/// Il gruppo si deduce dalla sola tinta del centro: nelle prove sul campo il rame (1, 2, 5 cent) stava tra 19° e 32°, l'oro
/// nordico (10, 20, 50 cent) tra 39° e 43°. L'anello non è affidabile: il ritaglio include un po' di tavolo e di bordo.
/// </summary>
public sealed record CoinColor(double CoreHue, double CoreSat, double CoreVal, double RingHue, double RingSat, double RingVal)
{
    public const string Copper = "copper";
    public const string Gold = "gold";

    /// <summary>"copper", "gold" oppure "?" se la tinta non è affidabile (poca saturazione: argento, riflesso, tinta di mezzo).</summary>
    public string Guess
    {
        get
        {
            if (CoreSat < 0.25) return "?";
            if (CoreHue is < 35 or > 340) return Copper;
            return CoreHue is >= 37 and <= 60 ? Gold : "?";
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
