using RectF = LiveCoinCounter.Mosaic.RectF;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services.Recognition;
using LiveCoinCounter.Sessions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiveCoinCounter.Services.Sessions;

/// <summary>
/// Fase 1 sul telefono: i modelli YOLO inclusi nell'app usati come rilevatore. Sono a 8 classi: la classe non serve
/// alla localizzazione ma si conserva come classificazione provvisoria (seconda opinione dopo lo stop).
/// Quando ci sarà il rilevatore a una classe (issue #5) basterà un profilo con il nuovo file.
/// </summary>
public sealed class OnnxCoinLocator : ICoinLocator
{
    private readonly RecognizerProfile _profile;

    public OnnxCoinLocator(RecognizerProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ModelAsset)) throw new ArgumentException("Il profilo non ha un modello sul dispositivo.", nameof(profile));
        _profile = profile;
    }

    public string Id => _profile.Id;

    public Task<IReadOnlyList<CoinBox>> LocateAsync(ArgbImage image, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<CoinBox>>(async () =>
        {
            var session = await OnnxModelCache.GetAsync(_profile.ModelAsset!, ct);
            var size = OnnxModelCache.InputSize(session);
            var pixels = ImageOps.Letterbox(image, size, out var letterbox);
            var output = YoloRunner.Run(session, pixels, size);
            var detections = YoloDecoder.Decode(output.Data, output.Anchors, output.Classes,
                OnnxCoinRecognizer.ConfidenceThreshold, OnnxCoinRecognizer.IouThreshold, classAgnostic: true);

            return detections.Select(d =>
            {
                var box = letterbox.ToImage(d);
                var cents = output.Classes == YoloDecoder.ClassCents.Length ? YoloDecoder.ClassCents[d.ClassIndex] : (int?)null;
                return new CoinBox(new RectF(box.X, box.Y, box.Width, box.Height), d.Confidence, cents, cents is null ? 0 : d.Confidence);
            }).ToList();
        }, ct);
}

/// <summary>
/// Fase 2 sul telefono, finché non c'è il classificatore dedicato (issue #5): lo stesso YOLO a 8 classi sul ritaglio.
/// Il ritaglio si disegna piccolo al centro dell'ingresso (<see cref="Fill"/>), perché nelle foto di addestramento le
/// monete occupano una piccola parte dell'immagine: una moneta grande quanto tutto l'ingresso verrebbe riconosciuta male.
/// </summary>
public sealed class YoloCropClassifier : ICoinClassifier
{
    public const float Fill = 0.3f;
    private readonly RecognizerProfile _profile;

    public YoloCropClassifier(RecognizerProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ModelAsset)) throw new ArgumentException("Il profilo non ha un modello sul dispositivo.", nameof(profile));
        _profile = profile;
    }

    public string Id => "crop:" + _profile.Id;
    public bool RequiresNetwork => false;

    public Task<CoinClassification?> ClassifyAsync(CoinCrop crop, CancellationToken ct = default) =>
        Task.Run<CoinClassification?>(async () =>
        {
            var session = await OnnxModelCache.GetAsync(_profile.ModelAsset!, ct);
            var size = OnnxModelCache.InputSize(session);
            var image = AndroidImaging.Decode(crop.Jpeg);
            var pixels = ImageOps.Letterbox(image, size, out _, Fill);
            var output = YoloRunner.Run(session, pixels, size);
            if (output.Classes != YoloDecoder.ClassCents.Length) return null;

            var best = YoloDecoder.BestAnchorScores(output.Data, output.Anchors, output.Classes);
            if (best is null) return null;
            var topK = best.Value.Scores
                .Select((score, i) => (Cents: YoloDecoder.ClassCents[i], Score: score))
                .OrderByDescending(k => k.Score).Take(3).ToList();
            return new CoinClassification(topK[0].Cents, topK[0].Score, topK, Id);
        }, ct);
}

internal static class YoloRunner
{
    public sealed record Output(float[] Data, int Anchors, int Classes);

    public static Output Run(InferenceSession session, int[] argb, int size)
    {
        var inputName = session.InputMetadata.Keys.First();
        var tensor = new DenseTensor<float>(Letterbox.ToTensor(argb), new[] { 1, 3, size, size });
        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = outputs.First().AsTensor<float>();
        var channels = output.Dimensions[1];   // [1, 4 + classi, ancore]
        var anchors = output.Dimensions[2];
        return new Output(output.ToArray(), anchors, channels - 4);
    }
}
