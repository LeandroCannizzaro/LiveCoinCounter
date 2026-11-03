using OpenCvSharp;

namespace CoinLabeler;

/// <summary>Proposte di box per le monete tramite trasformata di Hough per cerchi (indipendente dall'AI).</summary>
public static class BoxDetector
{
    private const int WorkingSide = 1000;

    public static List<Box> Detect(byte[] orientedJpeg, double param2 = 30)
    {
        using var color = Cv2.ImDecode(orientedJpeg, ImreadModes.Color);
        var scale = Math.Min(1.0, (double)WorkingSide / Math.Max(color.Width, color.Height));

        using var resized = new Mat();
        Cv2.Resize(color, resized, new Size(), scale, scale, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MedianBlur(gray, gray, 5);

        var side = Math.Max(resized.Width, resized.Height);
        var minRadius = (int)(side * 0.03);
        var maxRadius = (int)(side * 0.16);

        var circles = Cv2.HoughCircles(gray, HoughModes.Gradient, dp: 1.2, minDist: minRadius * 1.6,
            param1: 100, param2: param2, minRadius: minRadius, maxRadius: maxRadius);

        var background = BorderMedian(resized);

        // Elimina i cerchi quasi coincidenti (stessa moneta trovata due volte), tenendo il più forte (primo),
        // e quelli il cui interno non si distingue dallo sfondo e non ha dettagli (ombre, venature del tavolo)
        var kept = new List<CircleSegment>();
        foreach (var c in circles)
        {
            if (!LooksLikeCoin(resized, gray, c, background)) continue;

            var duplicate = kept.Any(k =>
                Math.Sqrt(Math.Pow(k.Center.X - c.Center.X, 2) + Math.Pow(k.Center.Y - c.Center.Y, 2)) < Math.Max(k.Radius, c.Radius) * 0.85);
            if (!duplicate) kept.Add(c);
        }

        return kept.Select(c => new Box(
                (c.Center.X - c.Radius) / scale,
                (c.Center.Y - c.Radius) / scale,
                2 * c.Radius / scale,
                2 * c.Radius / scale))
            .Select(b => Clamp(b, color.Width, color.Height))
            .ToList();
    }

    /// <summary>Colore mediano dei bordi dell'immagine, preso come colore dello sfondo.</summary>
    private static Scalar BorderMedian(Mat bgr)
    {
        var band = Math.Max(2, Math.Min(bgr.Width, bgr.Height) / 25);
        var samples = new List<Vec3b>();
        for (var y = 0; y < bgr.Height; y += 3)
        for (var x = 0; x < bgr.Width; x += 3)
        {
            if (x >= band && x < bgr.Width - band && y >= band && y < bgr.Height - band) continue;
            samples.Add(bgr.At<Vec3b>(y, x));
        }

        double Median(Func<Vec3b, byte> f) { var v = samples.Select(f).OrderBy(b => b).ToList(); return v[v.Count / 2]; }
        return new Scalar(Median(v => v.Item0), Median(v => v.Item1), Median(v => v.Item2));
    }

    /// <summary>Una moneta ha un colore diverso dallo sfondo oppure un disegno (varianza alta) all'interno del cerchio.</summary>
    private static bool LooksLikeCoin(Mat bgr, Mat gray, CircleSegment c, Scalar background)
    {
        using var mask = new Mat(bgr.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(mask, (OpenCvSharp.Point)c.Center, (int)(c.Radius * 0.7), Scalar.All(255), -1);

        var mean = Cv2.Mean(bgr, mask);
        var distance = Math.Sqrt(Math.Pow(mean.Val0 - background.Val0, 2) + Math.Pow(mean.Val1 - background.Val1, 2) + Math.Pow(mean.Val2 - background.Val2, 2));

        Cv2.MeanStdDev(gray, out _, out var std, mask);
        return distance > MinColorDistance || std.Val0 > MinStdDev;
    }

    public static double MinColorDistance { get; set; } = 25;
    public static double MinStdDev { get; set; } = 22;

    private static Box Clamp(Box b, int width, int height)
    {
        var x0 = Math.Max(0, b.X);
        var y0 = Math.Max(0, b.Y);
        var x1 = Math.Min(width, b.X + b.W);
        var y1 = Math.Min(height, b.Y + b.H);
        return new Box(x0, y0, x1 - x0, y1 - y0);
    }
}
