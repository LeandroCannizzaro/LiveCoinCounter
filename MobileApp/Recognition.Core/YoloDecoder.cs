namespace LiveCoinCounter.Services.Recognition;

/// <summary>Una moneta trovata dal rilevatore: classe 0-7 (1ct ... 2€), confidenza e riquadro in pixel dell'immagine di input.</summary>
public sealed record Detection(int ClassIndex, float Confidence, float X, float Y, float Width, float Height);

/// <summary>
/// Decodifica l'uscita grezza di un modello YOLO11 esportato in ONNX (forma [1, 4 + classi, ancore], canali per ancora:
/// cx, cy, w, h, punteggio di ogni classe) e applica la soppressione dei non massimi come fa Ultralytics.
/// Codice puro, senza dipendenze dalla piattaforma, così si prova con i test.
/// </summary>
public static class YoloDecoder
{
    public static readonly string[] ClassNames =
        { "1 centesimo", "2 centesimi", "5 centesimi", "10 centesimi", "20 centesimi", "50 centesimi", "1 euro", "2 euro" };

    public static readonly int[] ClassCents = { 1, 2, 5, 10, 20, 50, 100, 200 };

    /// <param name="output">tensore appiattito [canali x ancore]: il canale c dell'ancora a sta in output[c * anchors + a]</param>
    public static List<Detection> Decode(
        ReadOnlySpan<float> output, int anchors, int classes,
        float confidenceThreshold = 0.25f, float iouThreshold = 0.7f, bool classAgnostic = false)
    {
        if (output.Length != (4 + classes) * anchors)
            throw new ArgumentException($"Uscita di {output.Length} valori, attesi {(4 + classes) * anchors}.");

        var candidates = new List<Detection>();
        for (var a = 0; a < anchors; a++)
        {
            var best = 0;
            var bestScore = float.MinValue;
            for (var c = 0; c < classes; c++)
            {
                var score = output[(4 + c) * anchors + a];
                if (score > bestScore) { bestScore = score; best = c; }
            }

            if (bestScore < confidenceThreshold) continue;

            var cx = output[a];
            var cy = output[anchors + a];
            var w = output[2 * anchors + a];
            var h = output[3 * anchors + a];
            candidates.Add(new Detection(best, bestScore, cx - w / 2, cy - h / 2, w, h));
        }

        return NonMaxSuppression(candidates, iouThreshold, classAgnostic);
    }

    /// <summary>
    /// NMS: tiene il riquadro più sicuro e scarta quelli che lo coprono troppo. Con <paramref name="classAgnostic"/> il
    /// confronto vale anche tra classi diverse: la stessa moneta non può essere contata due volte con tagli diversi.
    /// </summary>
    public static List<Detection> NonMaxSuppression(List<Detection> detections, float iouThreshold, bool classAgnostic = false)
    {
        var kept = new List<Detection>();
        foreach (var candidate in detections.OrderByDescending(d => d.Confidence))
        {
            if (!kept.Any(k => (classAgnostic || k.ClassIndex == candidate.ClassIndex) && Iou(k, candidate) > iouThreshold))
                kept.Add(candidate);
        }
        return kept;
    }

    public static float Iou(Detection a, Detection b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

/// <summary>Decide se un risultato del rilevatore merita un avviso all'utente.</summary>
public static class DetectionQuality
{
    public const float LowConfidence = 0.55f;
    public const float OverlapWarning = 0.3f;

    /// <returns>Il testo dell'avviso, oppure null se il risultato sembra affidabile.</returns>
    public static string? Warning(IReadOnlyList<Detection> detections)
    {
        if (detections.Count == 0) return null;

        var reasons = new List<string>();
        var low = detections.Count(d => d.Confidence < LowConfidence);
        if (low > 0)
            reasons.Add(low == 1 ? "una moneta è poco riconoscibile" : $"{low} monete sono poco riconoscibili");

        for (var i = 0; i < detections.Count && reasons.Count < 2; i++)
            for (var j = i + 1; j < detections.Count; j++)
                if (detections[i].ClassIndex != detections[j].ClassIndex && YoloDecoder.Iou(detections[i], detections[j]) > OverlapWarning)
                {
                    reasons.Add("due monete di taglio diverso si sovrappongono");
                    break;
                }

        return reasons.Count == 0 ? null : "Risultato incerto: " + string.Join(", ", reasons) + ".";
    }
}

/// <summary>Come l'immagine originale è stata inserita nel quadrato di input (stessa convenzione "letterbox" di Ultralytics).</summary>
public readonly record struct Letterbox(float Scale, float PadX, float PadY)
{
    public static Letterbox For(int imageWidth, int imageHeight, int inputSize)
    {
        var scale = Math.Min((float)inputSize / imageWidth, (float)inputSize / imageHeight);
        return new Letterbox(scale, (inputSize - imageWidth * scale) / 2, (inputSize - imageHeight * scale) / 2);
    }

    /// <summary>Riporta un riquadro dallo spazio del modello a quello dell'immagine originale.</summary>
    public Detection ToImage(Detection d) =>
        d with { X = (d.X - PadX) / Scale, Y = (d.Y - PadY) / Scale, Width = d.Width / Scale, Height = d.Height / Scale };

    /// <summary>Pixel ARGB (come da Android Bitmap.GetPixels) → tensore float CHW, canali R,G,B in 0..1.</summary>
    public static float[] ToTensor(ReadOnlySpan<int> argb)
    {
        var plane = argb.Length;
        var tensor = new float[3 * plane];
        for (var i = 0; i < plane; i++)
        {
            var p = argb[i];
            tensor[i] = ((p >> 16) & 0xFF) / 255f;
            tensor[plane + i] = ((p >> 8) & 0xFF) / 255f;
            tensor[2 * plane + i] = (p & 0xFF) / 255f;
        }
        return tensor;
    }
}
