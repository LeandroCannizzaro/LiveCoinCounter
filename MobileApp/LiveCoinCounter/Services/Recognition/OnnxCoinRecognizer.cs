using Android.Graphics;
using LiveCoinCounter.Models;
using LiveCoinCounter.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Riconoscimento sul dispositivo con il modello YOLO11n addestrato (MachineLearning/training, asset "coin_model.onnx").
/// Un solo passaggio: il modello trova ogni moneta e ne dice direttamente il taglio.
/// Nessuna rete e nessun dato esce dal telefono.
/// </summary>
public sealed class OnnxCoinRecognizer : ICoinRecognizer
{
    private const string ModelAsset = "coin_model.onnx";
    private const int InputSize = 640;
    private const float ConfidenceThreshold = 0.25f;
    private const float IouThreshold = 0.6f;
    private const bool ClassAgnosticNms = true;   // una moneta non si conta due volte con tagli diversi

    // La sessione costa (caricamento del modello): una sola per processo
    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private static InferenceSession? _session;

    public string Id => "offline-model";
    public string DisplayName => "Offline (modello addestrato)";
    public bool RequiresNetwork => false;
    public bool IsConfigured => true;

    public Task<CoinRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            try
            {
                var session = await GetSessionAsync(ct);
                ct.ThrowIfCancellationRequested();
                return Recognize(session, imageBytes);
            }
            catch (OperationCanceledException)
            {
                return new CoinRecognitionResult { Success = false, ErrorMessage = "Analisi annullata." };
            }
            catch (Exception ex)
            {
                return new CoinRecognitionResult { Success = false, ErrorMessage = $"Errore modello offline: {ex.Message}" };
            }
        }, ct);

    private static async Task<InferenceSession> GetSessionAsync(CancellationToken ct)
    {
        if (_session is not null) return _session;

        await LoadLock.WaitAsync(ct);
        try
        {
            if (_session is null)
            {
                await using var asset = await FileSystem.OpenAppPackageFileAsync(ModelAsset);
                using var memory = new MemoryStream();
                await asset.CopyToAsync(memory, ct);
                _session = new InferenceSession(memory.ToArray());
            }
            return _session;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    private static CoinRecognitionResult Recognize(InferenceSession session, byte[] imageBytes)
    {
        using var source = ImageHelper.DecodeUpright(imageBytes);

        var letterbox = Letterbox.For(source.Width, source.Height, InputSize);
        var pixels = DrawLetterboxed(source, letterbox);
        var tensor = new DenseTensor<float>(Letterbox.ToTensor(pixels), new[] { 1, 3, InputSize, InputSize });

        var inputName = session.InputMetadata.Keys.First();
        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = outputs.First().AsTensor<float>();

        // forma [1, 4 + classi, ancore]
        var channels = output.Dimensions[1];
        var anchors = output.Dimensions[2];
        var detections = YoloDecoder.Decode(output.ToArray(), anchors, channels - 4, ConfidenceThreshold, IouThreshold, ClassAgnosticNms);

        var result = new CoinRecognitionResult { Success = true };
        foreach (var group in detections.GroupBy(d => d.ClassIndex).OrderByDescending(g => YoloDecoder.ClassCents[g.Key]))
        {
            result.Coins.Add(new CoinResult
            {
                Denomination = YoloDecoder.ClassNames[group.Key],
                ValueInCents = YoloDecoder.ClassCents[group.Key],
                Count = group.Count()
            });
        }

        result.Detections = detections.Select(d =>
        {
            var box = letterbox.ToImage(d);
            return new DetectedCoin(YoloDecoder.ClassNames[d.ClassIndex], YoloDecoder.ClassCents[d.ClassIndex],
                d.Confidence, box.X, box.Y, box.Width, box.Height);
        }).ToList();
        result.Warning = DetectionQuality.Warning(detections);

        result.RawResponse = detections.Count == 0
            ? "Nessuna moneta rilevata."
            : $"Modello offline: {detections.Count} monete, confidenza minima {detections.Min(d => d.Confidence):P0}.";
        return result;
    }

    /// <summary>Disegna l'immagine al centro di un quadrato grigio (114, come nell'addestramento Ultralytics).</summary>
    private static int[] DrawLetterboxed(Bitmap source, Letterbox letterbox)
    {
        using var canvasBitmap = Bitmap.CreateBitmap(InputSize, InputSize, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(canvasBitmap);
        canvas.DrawColor(Android.Graphics.Color.Rgb(114, 114, 114));

        using var paint = new Android.Graphics.Paint { FilterBitmap = true, AntiAlias = true };
        var matrix = new Matrix();
        matrix.PostScale(letterbox.Scale, letterbox.Scale);
        matrix.PostTranslate(letterbox.PadX, letterbox.PadY);
        canvas.DrawBitmap(source, matrix, paint);

        var pixels = new int[InputSize * InputSize];
        canvasBitmap.GetPixels(pixels, 0, InputSize, 0, 0, InputSize, InputSize);
        return pixels;
    }
}
