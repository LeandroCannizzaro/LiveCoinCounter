using LiveCoinCounter.Models;
using SkiaSharp;

namespace CoinLabeler;

/// <summary>
/// Per ogni foto: 1) box con Hough; 2) ogni ritaglio classificato da ciascun provider; 3) conteggi dell'immagine intera
/// come controllo incrociato; 4) consenso. L'immagine va in "review" se i provider non concordano o i conteggi non tornano.
/// </summary>
public sealed class LabelPipeline
{
    public static readonly int[] ValidValues = { 1, 2, 5, 10, 20, 50, 100, 200 };

    private readonly List<Named> _providers;
    private readonly string _source;
    private readonly double _houghParam2;

    public LabelPipeline(List<Named> providers, string source, double houghParam2)
    {
        _providers = providers;
        _source = source;
        _houghParam2 = houghParam2;
    }

    public async Task<(LabelFile label, byte[] orientedJpeg)> ProcessAsync(string imagePath, CancellationToken ct)
    {
        var raw = await File.ReadAllBytesAsync(imagePath, ct);
        using var bitmap = ImageIO.LoadOriented(raw);
        var oriented = ImageIO.EncodeJpeg(bitmap, 95);

        var boxes = BoxDetector.Detect(oriented, _houghParam2);

        var label = new LabelFile
        {
            Image = Path.GetFileNameWithoutExtension(imagePath) + ".jpg",
            Width = bitmap.Width,
            Height = bitmap.Height,
            Source = _source,
            Labeler = { Boxes = "hough", Classifiers = _providers.Select(p => p.Label).ToList() },
            Coins = boxes.Select(b => new CoinLabel { Bbox = new[] { Math.Round(b.X, 1), Math.Round(b.Y, 1), Math.Round(b.W, 1), Math.Round(b.H, 1) } }).ToList()
        };

        if (_providers.Count == 0)
        {
            label.Status = "boxes-only";
            return (label, oriented);
        }

        // Controllo incrociato: conteggi dell'immagine intera per ogni provider
        var shrunk = ImageIO.ShrinkForUpload(oriented);
        var wholeImage = new List<Dictionary<int, int>?>();
        foreach (var provider in _providers)
        {
            var result = await provider.RecognizeAsync(shrunk, ct);
            wholeImage.Add(result.Success ? result.Coins.GroupBy(c => c.ValueInCents).ToDictionary(g => g.Key, g => g.Sum(c => c.Count)) : null);
        }

        // Voti per ritaglio
        var votes = new int?[boxes.Count][];
        for (var i = 0; i < boxes.Count; i++)
        {
            var crop = ImageIO.CropJpeg(bitmap, boxes[i]);
            votes[i] = new int?[_providers.Count];
            for (var p = 0; p < _providers.Count; p++)
                votes[i][p] = await ClassifyCropAsync(_providers[p], crop, ct);
        }

        Consensus.Apply(label, _providers.Select(p => p.Label).ToList(), votes, wholeImage);

        return (label, oriented);
    }

    private static async Task<int?> ClassifyCropAsync(Named provider, byte[] crop, CancellationToken ct)
    {
        var result = await provider.RecognizeAsync(crop, ct);
        if (!result.Success || result.Coins.Count != 1 || result.Coins[0].Count != 1) return null;
        var value = result.Coins[0].ValueInCents;
        return ValidValues.Contains(value) ? value : null;
    }

}
