using System.Net;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace CoinLabeler;

/// <summary>Genera report/index.html: miniature con i box disegnati e le etichette, prima le immagini da rivedere.</summary>
public static class HtmlReport
{
    private static readonly SKColor[] Palette =
    {
        SKColors.OrangeRed, SKColors.DodgerBlue, SKColors.LimeGreen, SKColors.Gold,
        SKColors.MediumOrchid, SKColors.Cyan, SKColors.HotPink, SKColors.White
    };

    public static void Build(string outputDir)
    {
        var reportDir = Path.Combine(outputDir, "report");
        Directory.CreateDirectory(reportDir);

        var items = Directory.GetFiles(outputDir, "*.json")
            .Select(p => JsonSerializer.Deserialize<LabelFile>(File.ReadAllText(p), LabelFile.Json))
            .Where(l => l is not null).Select(l => l!)
            .OrderBy(l => l.Status == "review" ? 0 : l.Status == "boxes-only" ? 1 : 2)
            .ThenBy(l => l.Image)
            .ToList();

        var html = new StringBuilder();
        html.Append("<!doctype html><meta charset=utf-8><title>Revisione etichette</title>");
        html.Append("<style>body{font-family:sans-serif;margin:16px;background:#f4f4f4}.grid{display:flex;flex-wrap:wrap;gap:12px}");
        html.Append(".card{background:#fff;border-radius:8px;padding:8px;width:260px}.card img{width:100%;border-radius:4px}");
        html.Append(".review{outline:3px solid #e67e22}.auto{outline:3px solid #2ecc71}small{color:#555}</style>");
        html.Append($"<h1>Revisione etichette</h1><p>{items.Count} immagini - {items.Count(i => i.Status == "review")} da rivedere, ");
        html.Append($"{items.Count(i => i.Status == "auto")} automatiche. Per correggere, modifica il file .json accanto all'immagine.</p><div class=grid>");

        foreach (var item in items)
        {
            var imagePath = Path.Combine(outputDir, item.Image);
            if (!File.Exists(imagePath)) continue;

            var thumbName = Path.GetFileNameWithoutExtension(item.Image) + ".jpg";
            File.WriteAllBytes(Path.Combine(reportDir, thumbName), Render(imagePath, item));

            html.Append($"<div class='card {item.Status}'><img src='{WebUtility.HtmlEncode(thumbName)}'>");
            html.Append($"<b>{WebUtility.HtmlEncode(item.Image)}</b> <small>[{item.Status}]</small><br><small>");
            foreach (var coin in item.Coins)
            {
                var votes = coin.Votes is null ? "" : " " + string.Join("/", coin.Votes.Values.Select(v => v?.ToString() ?? "?"));
                html.Append($"{(coin.Label?.ToString() ?? "?")}{(coin.Agreement == false ? "&#9888;" : "")}{votes}; ");
            }
            html.Append($"</small><br><small>totale {item.TotalCents / 100.0:F2} &euro;</small></div>");
        }

        html.Append("</div>");
        File.WriteAllText(Path.Combine(reportDir, "index.html"), html.ToString(), Encoding.UTF8);
    }

    private static byte[] Render(string imagePath, LabelFile item)
    {
        using var bitmap = SKBitmap.Decode(imagePath);
        using var canvas = new SKCanvas(bitmap);
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, bitmap.Width / 200f), IsAntialias = true };
        using var font = new SKFont { Size = Math.Max(18, bitmap.Width / 30f) };
        using var text = new SKPaint { IsAntialias = true, Color = SKColors.Yellow };

        foreach (var coin in item.Coins)
        {
            var index = coin.Label is null ? -1 : Array.IndexOf(LabelPipeline.ValidValues, coin.Label.Value);
            stroke.Color = index < 0 ? SKColors.Red : Palette[index];
            canvas.DrawRect((float)coin.Bbox[0], (float)coin.Bbox[1], (float)coin.Bbox[2], (float)coin.Bbox[3], stroke);
            canvas.DrawText(coin.Label?.ToString() ?? "?", (float)coin.Bbox[0] + 4, (float)coin.Bbox[1] + font.Size, font, text);
        }

        var scale = Math.Min(1.0, 520.0 / Math.Max(bitmap.Width, bitmap.Height));
        using var small = bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return ImageIO.EncodeJpeg(small ?? bitmap, 80);
    }
}
