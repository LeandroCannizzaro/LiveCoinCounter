using System.Text.Json;

namespace CoinLabeler;

/// <summary>
/// Confronta i JSON prodotti con un dataset YOLO di riferimento (images/ + labels/, classi 0-7 = 1ct…2€).
/// Serve a misurare quanto sono affidabili i box di Hough e l'etichettatura AI su dati con verità nota.
/// </summary>
public static class Evaluator
{
    private static readonly int[] ClassToCents = { 1, 2, 5, 10, 20, 50, 100, 200 };

    public static void Run(string predictionsDir, string yoloDir, double iouThreshold = 0.5)
    {
        int images = 0, gtTotal = 0, predTotal = 0, matched = 0, classCorrect = 0, classEvaluated = 0, totalExact = 0, totalEvaluated = 0;
        var perClass = ClassToCents.ToDictionary(c => c, _ => (hit: 0, total: 0));

        foreach (var jsonPath in Directory.GetFiles(predictionsDir, "*.json"))
        {
            var pred = JsonSerializer.Deserialize<LabelFile>(File.ReadAllText(jsonPath), LabelFile.Json);
            if (pred is null) continue;

            var gtPath = Path.Combine(yoloDir, "labels", Path.GetFileNameWithoutExtension(jsonPath) + ".txt");
            if (!File.Exists(gtPath)) continue;
            images++;

            var gt = File.ReadAllLines(gtPath).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l =>
            {
                var p = l.Split(' ');
                var (cx, cy, w, h) = (double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) * pred.Width,
                    double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) * pred.Height,
                    double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) * pred.Width,
                    double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) * pred.Height);
                return (box: new Box(cx - w / 2, cy - h / 2, w, h), cents: ClassToCents[int.Parse(p[0])]);
            }).ToList();

            var used = new HashSet<int>();
            gtTotal += gt.Count;
            predTotal += pred.Coins.Count;

            foreach (var coin in pred.Coins)
            {
                var box = new Box(coin.Bbox[0], coin.Bbox[1], coin.Bbox[2], coin.Bbox[3]);
                var best = -1;
                var bestIou = iouThreshold;
                for (var g = 0; g < gt.Count; g++)
                {
                    if (used.Contains(g)) continue;
                    var iou = box.Iou(gt[g].box);
                    if (iou >= bestIou) { bestIou = iou; best = g; }
                }

                if (best < 0) continue;
                used.Add(best);
                matched++;

                if (coin.Label.HasValue)
                {
                    classEvaluated++;
                    var hit = coin.Label.Value == gt[best].cents;
                    if (hit) classCorrect++;
                    var (h, t) = perClass[gt[best].cents];
                    perClass[gt[best].cents] = (h + (hit ? 1 : 0), t + 1);
                }
            }

            if (pred.Status != "boxes-only")
            {
                totalEvaluated++;
                if (pred.TotalCents == gt.Sum(g => g.cents) && pred.Coins.Count == gt.Count) totalExact++;
            }
        }

        string Pct(int a, int b) => b == 0 ? "n/d" : $"{100.0 * a / b:F1}% ({a}/{b})";

        Console.WriteLine($"Immagini confrontate: {images}");
        Console.WriteLine($"Box  - richiamo (monete vere trovate): {Pct(matched, gtTotal)}");
        Console.WriteLine($"Box  - precisione (box giusti):        {Pct(matched, predTotal)}");
        if (classEvaluated > 0)
        {
            Console.WriteLine($"Taglio corretto sui box abbinati:      {Pct(classCorrect, classEvaluated)}");
            Console.WriteLine($"Totale esatto per immagine:            {Pct(totalExact, totalEvaluated)}");
            foreach (var (cents, (hit, total)) in perClass.OrderBy(kv => kv.Key))
                Console.WriteLine($"  {cents,4} ct: {Pct(hit, total)}");
        }
    }
}
