using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services.Recognition;
using LiveCoinCounter.Sessions;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace CoinLabeler;

/// <summary>
/// Etichetta le sessioni dell'app fatte con un set di monete noto (la nota della sessione dice quale: «set1», «set2»...).
/// <list type="number">
/// <item>Toglie i doppioni del rilevatore (due riquadri con i centri a meno di mezzo diametro nello stesso fotogramma).</item>
/// <item>Se le monete restanti sono quante quelle del set, assegna a ognuna un valore del set (algoritmo ungherese su punteggi del
/// classificatore e del rilevatore, colore e diametro relativo); è «sicura» se ogni altro valore costa almeno <see cref="Margin"/> in più.
/// Le monete confermate a mano nell'app valgono come sicure.</item>
/// <item>Per ogni fotogramma registrato («Fotogrammi in debug»), uno ogni <see cref="MinFrameGapMs"/> ms, rileva le monete con lo stesso
/// modello dell'app e le abbina alle monete note riportate dal mosaico con l'omografia del fotogramma.</item>
/// </list>
/// Un fotogramma è «auto» solo se ogni moneta trovata è abbinata a una moneta sicura, nessuna tocca il bordo e nessuna moneta attesa
/// nell'inquadratura manca; altrimenti è «review». I ritagli delle monete vanno in crops/&lt;valore&gt;/ (o crops/_incerte/).
/// </summary>
public static class SessionLabeler
{
    private static readonly Dictionary<int, double> Diameter = new()
    {
        [1] = 16.25, [2] = 18.75, [5] = 21.25, [10] = 19.75, [20] = 22.25, [50] = 24.25, [100] = 23.25, [200] = 25.75
    };

    private const double Margin = 1.5;
    private const long MinFrameGapMs = 400;
    private const float NewMatchTolerance = 0.35f;

    private sealed record Coin(int Number, double X, double Y, double DiameterPx, Dictionary<int, double> Scores, string? Tint,
        bool Verified, int? AppCents, string? Crop);

    private static string _modelName = "";

    public static int Run(string output, string setsFile, string modelPath, string? defaultSet, IReadOnlyList<string> sessions)
    {
        _modelName = Path.GetFileNameWithoutExtension(modelPath);
        var sets = LoadSets(setsFile);
        var inputSize = Regex.Match(Path.GetFileName(modelPath), @"_(\d{3,4})\.onnx$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 640;
        using var net = CvDnn.ReadNetFromOnnx(modelPath) ?? throw new InvalidOperationException("Modello non leggibile: " + modelPath);

        Directory.CreateDirectory(Path.Combine(output, "images"));
        foreach (var dir in sessions)
        {
            try
            {
                Console.WriteLine(LabelSession(dir, output, sets, defaultSet, net, inputSize));
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or OpenCVException)
            {
                Console.Error.WriteLine($"{Path.GetFileName(dir)}: ERRORE {ex.Message}");
            }
        }
        return 0;
    }

    private static Dictionary<string, Dictionary<int, int>> LoadSets(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.EnumerateObject().ToDictionary(
            s => s.Name.ToLowerInvariant(),
            s => s.Value.GetProperty("coins").EnumerateObject().ToDictionary(c => int.Parse(c.Name), c => c.Value.GetInt32()));
    }

    private static string LabelSession(string dir, string output, Dictionary<string, Dictionary<int, int>> sets, string? defaultSet, Net net, int inputSize)
    {
        var name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        using var sessionDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "session.json")).TrimStart('﻿'));
        var root = sessionDoc.RootElement;
        var notes = root.GetProperty("session").TryGetProperty("notes", out var n) ? n.GetString() : null;
        var setName = notes is not null && Regex.Match(notes, @"\bset\s*(\d+)\b", RegexOptions.IgnoreCase) is { Success: true } sm
            ? "set" + sm.Groups[1].Value : defaultSet;
        if (setName is null) return $"{name}: nessun set nella nota della sessione: saltata";
        if (!sets.TryGetValue(setName.ToLowerInvariant(), out var set)) return $"{name}: il set «{setName}» non è nel file dei set: saltata";

        var events = File.ReadLines(Path.Combine(dir, "diagnostics.jsonl")).Select(l => JsonDocument.Parse(l)).ToList();
        try
        {
            // 1) doppioni del rilevatore: tracce viste solo tramite riquadri soppressi
            var kept = new HashSet<int>();
            foreach (var e in events.Where(e => e.RootElement.GetProperty("e").GetString() == "detect"))
            {
                var boxes = e.RootElement.GetProperty("d").GetProperty("boxes").EnumerateArray()
                    .Where(b => b.TryGetProperty("use", out var u) && u.GetString() is "ok" or "same_coin").ToList();
                var keep = DetectionFilter.SuppressSameCoin(boxes.Select(b => ToRect(b.GetProperty("box"))).ToList(),
                    boxes.Select(b => b.GetProperty("conf").GetSingle()).ToList());
                foreach (var i in keep)
                    if (boxes[i].TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Number) kept.Add(t.GetInt32());
            }

            var coins = root.GetProperty("coins").EnumerateArray().Select(ReadCoin).Where(c => kept.Contains(c.Number)).ToList();
            var total = set.Values.Sum();
            if (coins.Count != total) return $"{name} ({setName}): {coins.Count} monete dopo i doppioni, il set ne ha {total}: saltata";

            // 2) assegnazione dei valori del set
            var (labels, sure) = Assign(coins, set);
            CopyCrops(dir, output, name, coins, labels, sure);

            // 3) fotogrammi registrati
            var (auto, review, skipped) = LabelFrames(dir, output, name, events, coins, labels, sure, net, inputSize);
            var changed = coins.Where(c => c.AppCents != labels[c.Number]).Select(c => $"#{c.Number} {c.AppCents}→{labels[c.Number]}").ToList();
            return $"{name} ({setName}): {coins.Count} monete, {sure.Count} sicure; fotogrammi {auto} auto, {review} review, {skipped} scartati; " +
                   $"valori diversi dall'app: {changed.Count}{(changed.Count > 0 ? " (" + string.Join(", ", changed) + ")" : "")}";
        }
        finally
        {
            foreach (var e in events) e.Dispose();
        }
    }

    private static RectF ToRect(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());

    private static Coin ReadCoin(JsonElement c)
    {
        var scores = new Dictionary<int, double>();
        void Add(int cents, double score) => scores[cents] = Math.Max(scores.GetValueOrDefault(cents), score);
        if (c.TryGetProperty("history", out var history))
            foreach (var h in history.EnumerateArray())
                if (h.TryGetProperty("topK", out var topK) && topK.GetString() is { } text)
                    foreach (var (cents, score) in SessionRepository.ParseTopK(text)) Add(cents, score);
        if (c.TryGetProperty("detector", out var det) && det.TryGetProperty("cents", out var dc) && dc.ValueKind == JsonValueKind.Number)
            Add(dc.GetInt32(), det.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0);

        var box = c.GetProperty("box");
        return new Coin(
            c.GetProperty("number").GetInt32(),
            box[0].GetDouble() + box[2].GetDouble() / 2, box[1].GetDouble() + box[3].GetDouble() / 2,
            c.GetProperty("diameterPx").GetDouble(),
            scores,
            c.TryGetProperty("color", out var col) && col.TryGetProperty("tint", out var tint) ? tint.GetString() : null,
            c.TryGetProperty("truth", out var truth) && truth.TryGetProperty("verified", out var v) && v.GetBoolean(),
            c.TryGetProperty("effectiveCents", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null,
            c.TryGetProperty("crop", out var crop) ? crop.GetString() : null);
    }

    /// <summary>Valore di ogni moneta e quali sono sicure.</summary>
    private static (Dictionary<int, int> Labels, HashSet<int> Sure) Assign(List<Coin> coins, Dictionary<int, int> set)
    {
        var slots = set.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();
        double? scale = null;
        double Cost(Coin c, int cents)
        {
            var k = -Math.Log(Math.Max(c.Scores.GetValueOrDefault(cents), 0.01));
            var copper = cents <= 5;
            if (c.Tint == "copper" && !copper) k += 3;
            if (c.Tint == "gold" && copper) k += 3;
            if (scale is { } s)
            {
                var err = (c.DiameterPx / s - Diameter[cents]) / (0.07 * Diameter[cents]);
                k += err * err / 2;
            }
            return k;
        }

        var assignment = new int[coins.Count];
        for (var round = 0; round < 3; round++)
        {
            var cost = new double[coins.Count, slots.Count];
            for (var i = 0; i < coins.Count; i++)
            for (var j = 0; j < slots.Count; j++)
                cost[i, j] = Cost(coins[i], slots[j]);
            var columns = Hungarian(cost);
            for (var i = 0; i < coins.Count; i++) assignment[i] = slots[columns[i]];
            var ratios = coins.Select((c, i) => c.DiameterPx / Diameter[assignment[i]]).OrderBy(r => r).ToList();
            scale = ratios[ratios.Count / 2];
        }

        var labels = new Dictionary<int, int>();
        var sure = new HashSet<int>();
        for (var i = 0; i < coins.Count; i++)
        {
            var c = coins[i];
            if (c.Verified && c.AppCents is { } truth)
            {
                labels[c.Number] = truth;            // confermata a mano nell'app: vale più di ogni stima
                sure.Add(c.Number);
                continue;
            }
            labels[c.Number] = assignment[i];
            if (set.Keys.Where(v => v != assignment[i]).All(v => Cost(c, v) - Cost(c, assignment[i]) >= Margin)) sure.Add(c.Number);
        }
        return (labels, sure);
    }

    /// <summary>Algoritmo ungherese su una matrice quadrata: per ogni riga, la colonna assegnata.</summary>
    private static int[] Hungarian(double[,] cost)
    {
        var n = cost.GetLength(0);
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = double.PositiveInfinity;
                var j1 = 0;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j]) continue;
                    var cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (var j = 0; j <= n; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }
                j0 = j1;
            } while (p[j0] != 0);
            do { var j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0 != 0);
        }
        var result = new int[n];
        for (var j = 1; j <= n; j++) result[p[j] - 1] = j - 1;
        return result;
    }

    private static void CopyCrops(string dir, string output, string name, List<Coin> coins, Dictionary<int, int> labels, HashSet<int> sure)
    {
        foreach (var c in coins.Where(c => c.Crop is not null))
        {
            var source = Path.Combine(dir, c.Crop!);
            if (!File.Exists(source)) continue;
            var folder = sure.Contains(c.Number)
                ? Path.Combine(output, "crops", labels[c.Number].ToString())
                : Path.Combine(output, "crops", "_incerte", labels[c.Number].ToString());
            Directory.CreateDirectory(folder);
            File.Copy(source, Path.Combine(folder, $"{name}_{c.Number:D3}.jpg"), overwrite: true);
        }
    }

    private static (int Auto, int Review, int Skipped) LabelFrames(string dir, string output, string name, List<JsonDocument> events,
        List<Coin> coins, Dictionary<int, int> labels, HashSet<int> sure, Net net, int inputSize)
    {
        var framesFile = Path.Combine(dir, "frames", "frames.jsonl");
        if (!File.Exists(framesFile)) return (0, 0, 0);

        // stato e posizione nel mosaico di ogni fotogramma elaborato, per tempo (ms, come nel registro)
        var poses = new Dictionary<long, (string Status, double[]? Quad, int W, int H)>();
        foreach (var e in events.Where(e => e.RootElement.GetProperty("e").GetString() == "frame"))
        {
            var d = e.RootElement.GetProperty("d");
            var quad = d.TryGetProperty("quad", out var q) ? q.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
            poses[d.GetProperty("ts").GetInt64()] = (d.GetProperty("status").GetString() ?? "", quad, d.GetProperty("w").GetInt32(), d.GetProperty("h").GetInt32());
        }

        int auto = 0, review = 0, skipped = 0;
        long last = long.MinValue;
        foreach (var line in File.ReadLines(framesFile))
        {
            using var f = JsonDocument.Parse(line);
            var file = f.RootElement.GetProperty("File").GetString()!;
            var ts = f.RootElement.GetProperty("TimestampNs").GetInt64() / 1_000_000;
            if (last != long.MinValue && ts - last < MinFrameGapMs) continue;
            if (!poses.TryGetValue(ts, out var pose) || pose.Status != "Tracking" || pose.Quad is not { Length: 8 }) { skipped++; continue; }
            last = ts;

            var result = LabelFrame(Path.Combine(dir, "frames", file), pose.Quad, coins, labels, sure, net, inputSize);
            if (result is null) { skipped++; continue; }
            var (label, status, jpeg) = result.Value;
            var id = $"{name}_{Path.GetFileNameWithoutExtension(file)}";
            label.Image = id + ".jpg";
            File.WriteAllBytes(Path.Combine(output, "images", id + ".jpg"), jpeg);
            File.WriteAllText(Path.Combine(output, "images", id + ".json"), JsonSerializer.Serialize(label, LabelFile.Json));
            if (status == "auto") auto++; else review++;
        }
        return (auto, review, skipped);
    }

    private static (LabelFile Label, string Status, byte[] Jpeg)? LabelFrame(string imagePath, double[] quad, List<Coin> coins, Dictionary<int, int> labels,
        HashSet<int> sure, Net net, int inputSize)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty()) return null;
        int w = image.Width, h = image.Height;

        // monete trovate dal modello, come nell'app: filtro del bordo e un solo riquadro per moneta
        var detections = Detect(net, image, inputSize);
        var usable = new List<(RectF Box, float Conf)>();
        var border = new List<(RectF Box, float Conf)>();
        foreach (var d in detections)
            (DetectionFilter.IsUsable(d.Box, w, h) ? usable : border).Add(d);
        var keep = DetectionFilter.SuppressSameCoin(usable.Select(u => u.Box).ToList(), usable.Select(u => u.Conf).ToList());
        usable = keep.Select(i => usable[i]).ToList();
        if (usable.Count < 3) return null;

        // monete note riportate dal mosaico al fotogramma
        using var toFrame = Cv2.GetPerspectiveTransform(
            new[] { new Point2f((float)quad[0], (float)quad[1]), new Point2f((float)quad[2], (float)quad[3]), new Point2f((float)quad[4], (float)quad[5]), new Point2f((float)quad[6], (float)quad[7]) },
            new[] { new Point2f(0, 0), new Point2f(w, 0), new Point2f(w, h), new Point2f(0, h) });
        var projected = Cv2.PerspectiveTransform(coins.Select(c => new Point2f((float)c.X, (float)c.Y)).ToArray(), toFrame);
        var edge = Cv2.PerspectiveTransform(coins.Select(c => new Point2f((float)(c.X + c.DiameterPx / 2), (float)c.Y)).ToArray(), toFrame);
        var radius = projected.Select((p, i) => Math.Sqrt(Math.Pow(edge[i].X - p.X, 2) + Math.Pow(edge[i].Y - p.Y, 2))).ToArray();

        // spostamento comune (il mosaico non è perfetto): mediana degli scarti tra ogni moneta trovata e la nota più vicina
        var offsets = new List<(double Dx, double Dy)>();
        foreach (var (box, _) in usable)
        {
            var best = Enumerable.Range(0, coins.Count).MinBy(i => Dist(projected[i], box));
            if (Dist(projected[best], box) < box.Diameter) offsets.Add((projected[best].X - box.CenterX, projected[best].Y - box.CenterY));
        }
        if (offsets.Count < 3) return null;
        var dx = offsets.Select(o => o.Dx).OrderBy(x => x).ElementAt(offsets.Count / 2);
        var dy = offsets.Select(o => o.Dy).OrderBy(x => x).ElementAt(offsets.Count / 2);

        // abbinamento uno a uno, i più vicini per primi
        var pairs = new List<(int Det, int Coin, double D)>();
        for (var d = 0; d < usable.Count; d++)
        for (var c = 0; c < coins.Count; c++)
        {
            var dist = Math.Sqrt(Math.Pow(projected[c].X - dx - usable[d].Box.CenterX, 2) + Math.Pow(projected[c].Y - dy - usable[d].Box.CenterY, 2));
            if (dist < NewMatchTolerance * usable[d].Box.Diameter) pairs.Add((d, c, dist));
        }
        var matched = new Dictionary<int, int>();
        var usedCoins = new HashSet<int>();
        foreach (var p in pairs.OrderBy(p => p.D))
            if (!matched.ContainsKey(p.Det) && usedCoins.Add(p.Coin)) matched[p.Det] = p.Coin;
        if (matched.Count < 0.7 * usable.Count) return null;      // il fotogramma non torna con il mosaico: meglio non usarlo

        // monete sicure che dovrebbero essere interamente nell'inquadratura ma il modello non ha trovato
        var missing = Enumerable.Range(0, coins.Count).Where(c =>
            sure.Contains(coins[c].Number) && !usedCoins.Contains(c) &&
            projected[c].X - dx - radius[c] > 0.02 * w && projected[c].X - dx + radius[c] < 0.98 * w &&
            projected[c].Y - dy - radius[c] > 0.02 * h && projected[c].Y - dy + radius[c] < 0.98 * h).ToList();

        var label = new LabelFile
        {
            Width = w, Height = h, Source = "own-phone",
            Labeler = new LabelerInfo { Boxes = "detector:" + _modelName, Classifiers = new() { "set-assignment" } }
        };
        // le monete senza etichetta sicura (incerte, tagliate dal bordo, attese ma non trovate) si coprono di grigio: per l'addestramento
        // non sono né monete né sfondo. Se una copertura toccherebbe una moneta etichettata il fotogramma resta da rivedere.
        var labelled = new List<RectF>();
        var masks = new List<(double X, double Y, double R)>();
        for (var d = 0; d < usable.Count; d++)
        {
            if (matched.TryGetValue(d, out var c) && sure.Contains(coins[c].Number))
            {
                label.Coins.Add(new CoinLabel { Label = labels[coins[c].Number], Bbox = Bbox(usable[d].Box), Confidence = Math.Round(usable[d].Conf, 3) });
                labelled.Add(usable[d].Box);
            }
            else masks.Add((usable[d].Box.CenterX, usable[d].Box.CenterY, 0.55 * usable[d].Box.Diameter));
        }
        foreach (var (box, _) in border) masks.Add((box.CenterX, box.CenterY, 0.55 * Math.Max(box.Width, box.Height)));
        foreach (var c in missing) masks.Add((projected[c].X - dx, projected[c].Y - dy, 1.1 * radius[c]));

        var conflict = masks.Any(m => labelled.Any(b => Math.Sqrt(Math.Pow(m.X - b.CenterX, 2) + Math.Pow(m.Y - b.CenterY, 2)) < m.R + 0.4 * b.Diameter));
        string status;
        if (conflict || labelled.Count == 0)
        {
            // niente coperture: le monete senza etichetta restano (null) e il fotogramma va rivisto a mano
            foreach (var d in Enumerable.Range(0, usable.Count).Where(d => !(matched.TryGetValue(d, out var c) && sure.Contains(coins[c].Number))))
                label.Coins.Add(new CoinLabel { Label = null, Bbox = Bbox(usable[d].Box), Confidence = Math.Round(usable[d].Conf, 3) });
            foreach (var (box, conf) in border) label.Coins.Add(new CoinLabel { Label = null, Bbox = Bbox(box), Confidence = Math.Round(conf, 3) });
            status = "review";
        }
        else
        {
            foreach (var m in masks) Cv2.Circle(image, new Point((int)m.X, (int)m.Y), (int)Math.Ceiling(m.R), new Scalar(114, 114, 114), -1);
            status = "auto";
        }
        label.TotalCents = label.Coins.Sum(x => x.Label ?? 0);
        label.Status = status;
        label.Masked = conflict ? 0 : masks.Count;
        Cv2.ImEncode(".jpg", image, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));
        return (label, status, jpeg);

    }

    private static double Dist(Point2f p, RectF b) => Math.Sqrt(Math.Pow(p.X - b.CenterX, 2) + Math.Pow(p.Y - b.CenterY, 2));

    private static double[] Bbox(RectF b) => new[] { Math.Round(b.X, 1), Math.Round(b.Y, 1), Math.Round(b.Width, 1), Math.Round(b.Height, 1) };

    /// <summary>Il modello YOLO dell'app (ONNX) con OpenCV: stessa preparazione dell'immagine (letterbox grigio 114) e stessa decodifica.</summary>
    private static List<(RectF Box, float Conf)> Detect(Net net, Mat image, int inputSize)
    {
        var letterbox = Letterbox.For(image.Width, image.Height, inputSize);
        using var canvas = new Mat(inputSize, inputSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size((int)Math.Round(image.Width * letterbox.Scale), (int)Math.Round(image.Height * letterbox.Scale)), 0, 0, InterpolationFlags.Linear);
        var x0 = (int)Math.Round(letterbox.PadX);
        var y0 = (int)Math.Round(letterbox.PadY);
        resized.CopyTo(new Mat(canvas, new Rect(x0, y0, Math.Min(resized.Width, inputSize - x0), Math.Min(resized.Height, inputSize - y0))));
        using var blob = CvDnn.BlobFromImage(canvas, 1 / 255.0, new Size(inputSize, inputSize), new Scalar(), swapRB: true, crop: false);
        net.SetInput(blob);
        using var outputMat = net.Forward();
        var anchors = outputMat.Size(2);
        var channels = outputMat.Size(1);
        var data = new float[channels * anchors];
        Marshal.Copy(outputMat.Data, data, 0, data.Length);
        return YoloDecoder.Decode(data, anchors, channels - 4, 0.25f, 0.6f, classAgnostic: true)
            .Select(letterbox.ToImage)
            .Select(d => (new RectF(d.X, d.Y, d.Width, d.Height), d.Confidence))
            .ToList();
    }
}
