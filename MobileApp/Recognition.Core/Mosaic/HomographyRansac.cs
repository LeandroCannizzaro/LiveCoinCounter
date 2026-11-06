namespace LiveCoinCounter.Mosaic;

/// <summary>Coppia di punti corrispondenti: (X1, Y1) nell'immagine di partenza, (X2, Y2) in quella di arrivo.</summary>
public readonly record struct PointMatch(float X1, float Y1, float X2, float Y2, int Distance = 0);

public sealed record RansacResult(Homography Homography, int Inliers, int Total, double MeanError)
{
    public double InlierRatio => Total == 0 ? 0 : (double)Inliers / Total;
}

/// <summary>
/// Stima robusta dell'omografia: prova molte quaterne di corrispondenze a caso e tiene quella con più punti coerenti
/// (le corrispondenze sbagliate, frequenti tra monete simili, vengono scartate). Poi rifà la stima su tutti i punti coerenti.
/// </summary>
public static class HomographyRansac
{
    public static RansacResult? Estimate(
        IReadOnlyList<PointMatch> matches, double threshold = 3.0, int maxIterations = 500, int minInliers = 12, int seed = 7)
    {
        if (matches.Count < Math.Max(4, minInliers)) return null;

        var random = new Random(seed);
        var thresholdSq = threshold * threshold;
        var bestCount = 0;
        Homography? best = null;
        var iterations = maxIterations;
        var sample = new int[4];
        var src = new PointD[4];
        var dst = new PointD[4];

        for (var it = 0; it < iterations; it++)
        {
            if (!PickSample(matches.Count, random, sample)) break;
            for (var k = 0; k < 4; k++)
            {
                var m = matches[sample[k]];
                src[k] = new PointD(m.X1, m.Y1);
                dst[k] = new PointD(m.X2, m.Y2);
            }
            if (IsDegenerate(src) || IsDegenerate(dst)) continue;

            var h = Homography.Fit(src, dst);
            if (h is null) continue;

            var count = CountInliers(h.Value, matches, thresholdSq);
            if (count > bestCount)
            {
                bestCount = count;
                best = h;

                // numero di iterazioni adattivo (confidenza 99%)
                var ratio = (double)count / matches.Count;
                var denom = Math.Log(1 - Math.Pow(ratio, 4));
                if (denom < 0)
                    iterations = Math.Min(maxIterations, (int)Math.Ceiling(Math.Log(0.01) / denom) + 1);
            }
        }

        if (best is null || bestCount < minInliers) return null;

        // affinamento: due passate ai minimi quadrati sui punti coerenti
        var refined = best.Value;
        for (var pass = 0; pass < 2; pass++)
        {
            var inliers = matches.Where(m => Error(refined, m) < thresholdSq).ToList();
            if (inliers.Count < 4) break;
            var fit = Homography.Fit(inliers.Select(m => new PointD(m.X1, m.Y1)).ToList(),
                inliers.Select(m => new PointD(m.X2, m.Y2)).ToList());
            if (fit is null) break;
            refined = fit.Value;
        }

        var final = matches.Where(m => Error(refined, m) < thresholdSq).ToList();
        if (final.Count < minInliers) return null;
        var meanError = final.Average(m => Math.Sqrt(Error(refined, m)));
        return new RansacResult(refined, final.Count, matches.Count, meanError);
    }

    private static int CountInliers(Homography h, IReadOnlyList<PointMatch> matches, double thresholdSq)
    {
        var count = 0;
        foreach (var m in matches)
            if (Error(h, m) < thresholdSq) count++;
        return count;
    }

    private static double Error(Homography h, PointMatch m)
    {
        var (x, y) = h.Apply(m.X1, m.Y1);
        return (x - m.X2) * (x - m.X2) + (y - m.Y2) * (y - m.Y2);
    }

    private static bool PickSample(int n, Random random, int[] sample)
    {
        if (n < 4) return false;
        for (var k = 0; k < 4; k++)
        {
            int candidate;
            var guard = 0;
            do candidate = random.Next(n);
            while (Array.IndexOf(sample, candidate, 0, k) >= 0 && ++guard < 50);
            sample[k] = candidate;
        }
        return true;
    }

    /// <summary>Tre punti quasi allineati rendono l'omografia instabile.</summary>
    private static bool IsDegenerate(PointD[] p)
    {
        for (var i = 0; i < 4; i++)
        for (var j = i + 1; j < 4; j++)
        for (var k = j + 1; k < 4; k++)
        {
            var area = Math.Abs((p[j].X - p[i].X) * (p[k].Y - p[i].Y) - (p[k].X - p[i].X) * (p[j].Y - p[i].Y));
            if (area < 20) return true;
        }
        return false;
    }
}
