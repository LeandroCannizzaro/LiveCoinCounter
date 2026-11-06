using LiveCoinCounter.Mosaic;

namespace Recognition.Core.Tests;

/// <summary>
/// Un "tavolo" sintetico ad alta risoluzione (venature di legno + macchie) con monete disegnate come dischi,
/// e una fotocamera virtuale che ne inquadra una parte con un'omografia nota.
/// </summary>
internal sealed class SyntheticScene
{
    public SyntheticScene(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        Table = new ArgbImage(width, height);
        var blobs = Enumerable.Range(0, width * height / 900)
            .Select(_ => (X: random.Next(width), Y: random.Next(height), R: 3 + random.Next(9), V: random.Next(-70, 70)))
            .ToList();

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var grain = 120 + 25 * Math.Sin(x * 0.05 + Math.Sin(y * 0.013) * 3) + 10 * Math.Sin(y * 0.21);
            Table.Pixels[y * width + x] = Gray((int)grain);
        }

        foreach (var b in blobs)
            for (var dy = -b.R; dy <= b.R; dy++)
            for (var dx = -b.R; dx <= b.R; dx++)
            {
                if (dx * dx + dy * dy > b.R * b.R) continue;
                int x = b.X + dx, y = b.Y + dy;
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                var v = ((Table.Pixels[y * width + x] >> 8) & 0xFF) + b.V;
                Table.Pixels[y * width + x] = Gray(Math.Clamp(v, 0, 255));
            }
    }

    public ArgbImage Table { get; }

    public List<(double X, double Y, double R)> Coins { get; } = new();

    public void AddCoin(double cx, double cy, double radius)
    {
        Coins.Add((cx, cy, radius));
        for (var y = (int)(cy - radius); y <= cy + radius; y++)
        for (var x = (int)(cx - radius); x <= cx + radius; x++)
        {
            var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            if (d > radius || x < 0 || y < 0 || x >= Table.Width || y >= Table.Height) continue;
            var ring = d > radius * 0.8 ? 40 : 0;
            Table.Pixels[y * Table.Width + x] = ImageOps.Argb(200 - ring, 170 - ring, 60);
        }
    }

    /// <summary>Fotogramma w x h: il pixel (u, v) del fotogramma mostra il punto frameToTable(u, v) del tavolo.</summary>
    public ArgbImage Render(Homography frameToTable, int w, int h)
    {
        var frame = new ArgbImage(w, h);
        for (var v = 0; v < h; v++)
        for (var u = 0; u < w; u++)
        {
            var (x, y) = frameToTable.Apply(u + 0.5, v + 0.5);
            frame.Pixels[v * w + u] = ImageOps.SampleBilinear(Table, x - 0.5, y - 0.5, Gray(0));
        }
        return frame;
    }

    /// <summary>Inquadratura: fotogramma centrato in (cx, cy) del tavolo, ruotato e con un leggero zoom.</summary>
    public static Homography View(double cx, double cy, int w, int h, double degrees = 0, double zoom = 1) =>
        Homography.Translation(cx, cy) * Homography.Rotation(degrees) * Homography.Scale(zoom) * Homography.Translation(-w / 2.0, -h / 2.0);

    private static int Gray(int v) => ImageOps.Argb(v, v, v);
}
