using Android.Graphics;
using LiveCoinCounter.Models;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Prototipo offline (opzione "A" dell'issue #1): visione classica senza librerie esterne.
/// 1) separa le monete dallo sfondo, 2) misura il diametro in pixel e classifica il colore
/// (rame / oro / bicolore), 3) stima la scala px/mm scegliendo l'ipotesi che rende coerenti tutte le monete.
/// È indicativo: confonde monete sovrapposte, sfondi simili al metallo e tagli dal diametro vicino.
/// </summary>
public sealed class OfflineRecognizer : ICoinRecognizer
{
    private enum MetalClass { Copper, Gold, Bimetal }

    private sealed record Denomination(string Name, int Cents, double DiameterMm, MetalClass Metal);

    private static readonly Denomination[] Denominations =
    {
        new("1 centesimo", 1, 16.25, MetalClass.Copper),
        new("2 centesimi", 2, 18.75, MetalClass.Copper),
        new("5 centesimi", 5, 21.25, MetalClass.Copper),
        new("10 centesimi", 10, 19.75, MetalClass.Gold),
        new("20 centesimi", 20, 22.25, MetalClass.Gold),
        new("50 centesimi", 50, 24.25, MetalClass.Gold),
        new("1 euro", 100, 23.25, MetalClass.Bimetal),
        new("2 euro", 200, 25.75, MetalClass.Bimetal)
    };

    private sealed record Blob(double Diameter, MetalClass Metal);

    private const int MaxSide = 640;

    public string Id => "offline";
    public string DisplayName => "Offline (sperimentale)";
    public bool RequiresNetwork => false;
    public bool IsConfigured => true;

    public Task<CoinRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                return Recognize(imageBytes, ct);
            }
            catch (OperationCanceledException)
            {
                return new CoinRecognitionResult { Success = false, ErrorMessage = "Analisi annullata." };
            }
            catch (Exception ex)
            {
                return new CoinRecognitionResult { Success = false, ErrorMessage = $"Errore offline: {ex.Message}" };
            }
        }, ct);

    private static CoinRecognitionResult Recognize(byte[] imageBytes, CancellationToken ct)
    {
        var (pixels, w, h) = Decode(imageBytes);
        ct.ThrowIfCancellationRequested();

        var blobs = FindCoins(pixels, w, h, ct);
        var result = new CoinRecognitionResult { Success = true };

        if (blobs.Count == 0)
        {
            result.RawResponse = "Nessuna moneta rilevata.";
            return result;
        }

        var assigned = AssignDenominations(blobs);
        foreach (var group in assigned.GroupBy(d => d.Cents))
        {
            var d = group.First();
            result.Coins.Add(new CoinResult { Denomination = d.Name, ValueInCents = d.Cents, Count = group.Count() });
        }

        result.Coins = result.Coins.OrderByDescending(c => c.ValueInCents).ToList();
        result.RawResponse = $"Riconoscimento offline sperimentale: {blobs.Count} oggetti rilevati.";
        return result;
    }

    #region Decodifica

    private static (int[] pixels, int w, int h) Decode(byte[] imageBytes)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length, bounds);

        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > MaxSide * 2) sample *= 2;

        using var bitmap = BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length,
            new BitmapFactory.Options { InSampleSize = sample })
            ?? throw new InvalidOperationException("Immagine non decodificabile.");

        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(bitmap.Width, bitmap.Height));
        using var scaled = scale < 1.0
            ? Bitmap.CreateScaledBitmap(bitmap, (int)(bitmap.Width * scale), (int)(bitmap.Height * scale), true)!
            : bitmap;

        var pixels = new int[scaled.Width * scaled.Height];
        scaled.GetPixels(pixels, 0, scaled.Width, 0, 0, scaled.Width, scaled.Height);
        return (pixels, scaled.Width, scaled.Height);
    }

    #endregion

    #region Rilevamento

    private static List<Blob> FindCoins(int[] px, int w, int h, CancellationToken ct)
    {
        // Sfondo = colore mediano della cornice dell'immagine
        var bg = BorderMedian(px, w, h);

        // Distanza di colore dallo sfondo, soglia con il metodo di Otsu
        var dist = new byte[px.Length];
        var histogram = new int[256];
        for (var i = 0; i < px.Length; i++)
        {
            var (r, g, b) = Rgb(px[i]);
            var d = Math.Sqrt((r - bg.r) * (r - bg.r) + (g - bg.g) * (g - bg.g) + (b - bg.b) * (b - bg.b));
            dist[i] = (byte)Math.Min(255, d);
            histogram[dist[i]]++;
        }

        var threshold = Math.Max(OtsuThreshold(histogram, px.Length), 25);

        // Componenti connesse (flood fill iterativo)
        var labels = new bool[px.Length];
        var stack = new Stack<int>();
        var blobs = new List<Blob>();
        var minDiameter = Math.Max(w, h) * 0.04;

        for (var start = 0; start < px.Length; start++)
        {
            if (labels[start] || dist[start] <= threshold) continue;
            ct.ThrowIfCancellationRequested();

            int minX = w, maxX = 0, minY = h, maxY = 0, area = 0;
            stack.Push(start);
            labels[start] = true;

            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int x = p % w, y = p / w;
                area++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (x > 0) Visit(p - 1);
                if (x < w - 1) Visit(p + 1);
                if (y > 0) Visit(p - w);
                if (y < h - 1) Visit(p + w);
            }

            void Visit(int n)
            {
                if (labels[n] || dist[n] <= threshold) return;
                labels[n] = true;
                stack.Push(n);
            }

            double bw = maxX - minX + 1, bh = maxY - minY + 1;
            var diameter = (bw + bh) / 2;

            // Scarta rumore, oggetti enormi, a contatto con il bordo, non tondi o non pieni
            if (diameter < minDiameter) continue;
            if (bw * bh > w * h * 0.4) continue;
            if (minX == 0 || minY == 0 || maxX == w - 1 || maxY == h - 1) continue;
            var aspect = bw / bh;
            if (aspect < 0.75 || aspect > 1.33) continue;
            if (area / (Math.PI / 4 * bw * bh) < 0.6) continue;

            var cx = (minX + maxX) / 2.0;
            var cy = (minY + maxY) / 2.0;
            blobs.Add(new Blob(diameter, ClassifyMetal(px, w, h, cx, cy, diameter / 2)));
        }

        return blobs;
    }

    private static (int r, int g, int b) BorderMedian(int[] px, int w, int h)
    {
        var band = Math.Max(2, Math.Min(w, h) / 25);
        var rs = new List<int>();
        var gs = new List<int>();
        var bs = new List<int>();

        for (var y = 0; y < h; y += 2)
        for (var x = 0; x < w; x += 2)
        {
            if (x >= band && x < w - band && y >= band && y < h - band) continue;
            var (r, g, b) = Rgb(px[y * w + x]);
            rs.Add(r);
            gs.Add(g);
            bs.Add(b);
        }

        return (Median(rs), Median(gs), Median(bs));
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static int OtsuThreshold(int[] histogram, int total)
    {
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)histogram[i];

        double sumB = 0, best = -1;
        long wB = 0;
        var threshold = 0;

        for (var t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;

            sumB += t * (double)histogram[t];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }

        return threshold;
    }

    #endregion

    #region Colore

    private static MetalClass ClassifyMetal(int[] px, int w, int h, double cx, double cy, double radius)
    {
        var center = MeanColor(px, w, h, cx, cy, 0, radius * 0.45);
        var ring = MeanColor(px, w, h, cx, cy, radius * 0.75, radius * 0.95);

        // Bicolore: centro e anello hanno tonalità o luminosità diverse
        var warmDiff = Math.Abs(Warmth(center) - Warmth(ring));
        var colorDist = Math.Sqrt(
            Math.Pow(center.r - ring.r, 2) + Math.Pow(center.g - ring.g, 2) + Math.Pow(center.b - ring.b, 2));
        if (warmDiff > 0.12 || colorDist > 45) return MetalClass.Bimetal;

        // Rame: tonalità più rossastra dell'oro nordico
        var overall = ((center.r + ring.r) / 2, (center.g + ring.g) / 2, (center.b + ring.b) / 2);
        return Warmth(overall) > 1.28 ? MetalClass.Copper : MetalClass.Gold;
    }

    /// <summary>Rapporto rosso/verde: più alto = più rossastro.</summary>
    private static double Warmth((double r, double g, double b) c) => c.r / (c.g + 1);

    private static (double r, double g, double b) MeanColor(
        int[] px, int w, int h, double cx, double cy, double rMin, double rMax)
    {
        double r = 0, g = 0, b = 0;
        var n = 0;
        var x0 = Math.Max(0, (int)(cx - rMax));
        var x1 = Math.Min(w - 1, (int)(cx + rMax));
        var y0 = Math.Max(0, (int)(cy - rMax));
        var y1 = Math.Min(h - 1, (int)(cy + rMax));

        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            if (d < rMin || d > rMax) continue;
            var (pr, pg, pb) = Rgb(px[y * w + x]);
            r += pr;
            g += pg;
            b += pb;
            n++;
        }

        return n == 0 ? (0, 0, 0) : (r / n, g / n, b / n);
    }

    private static (int r, int g, int b) Rgb(int argb) => ((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

    #endregion

    #region Assegnazione dei tagli

    /// <summary>
    /// La scala (px/mm) è ignota. Ogni moneta, a turno, viene ipotizzata di ciascun taglio compatibile con
    /// il suo colore: l'ipotesi con errore totale minore sui diametri di tutte le monete vince.
    /// </summary>
    private static List<Denomination> AssignDenominations(List<Blob> blobs)
    {
        var bestCost = double.MaxValue;
        var best = new List<Denomination>();

        foreach (var anchor in blobs)
        foreach (var hypothesis in Denominations.Where(d => d.Metal == anchor.Metal))
        {
            var pxPerMm = anchor.Diameter / hypothesis.DiameterMm;
            var assignment = new List<Denomination>();
            var cost = 0.0;

            foreach (var blob in blobs)
            {
                var mm = blob.Diameter / pxPerMm;
                var nearest = Denominations
                    .Where(d => d.Metal == blob.Metal)
                    .OrderBy(d => Math.Abs(mm - d.DiameterMm))
                    .First();

                cost += Math.Abs(mm - nearest.DiameterMm) / nearest.DiameterMm;
                assignment.Add(nearest);
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                best = assignment;
            }
        }

        return best;
    }

    #endregion
}
