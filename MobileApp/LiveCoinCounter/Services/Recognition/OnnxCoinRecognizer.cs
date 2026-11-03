using Android.Graphics;
using LiveCoinCounter.Models;
using LiveCoinCounter.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiveCoinCounter.Services.Recognition;

/// <summary>
/// Riconoscimento sul dispositivo con un modello YOLO addestrato (MachineLearning/training) in formato ONNX.
/// Il file è l'asset indicato dal profilo (RecognizerProfile.ModelAsset) e la risoluzione di ingresso si legge dal modello,
/// quindi aggiungere un modello non richiede codice. Un solo passaggio: il modello trova ogni moneta e ne dice direttamente il taglio.
/// Nessuna rete e nessun dato esce dal telefono.
/// </summary>
public sealed class OnnxCoinRecognizer : ICoinRecognizer
{
    private const int DefaultInputSize = 640;
    private const float ConfidenceThreshold = 0.25f;
    private const float IouThreshold = 0.6f;
    private const bool ClassAgnosticNms = true;   // una moneta non si conta due volte con tagli diversi

    // La sessione costa (caricamento del modello): si tiene solo l'ultima usata, per non occupare memoria con tutti i modelli
    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private static InferenceSession? _session;
    private static string? _sessionAsset;

    private readonly RecognizerProfile _profile;

    public OnnxCoinRecognizer(RecognizerProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ModelAsset))
            throw new ArgumentException($"Il profilo {profile.Id} non indica un file del modello.", nameof(profile));
        _profile = profile;
    }

    public string Id => _profile.Id;
    public string DisplayName => _profile.DisplayName;
    public bool RequiresNetwork => false;
    public bool IsConfigured => true;

    public Task<CoinRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            try
            {
                var session = await GetSessionAsync(_profile.ModelAsset!, ct);
                ct.ThrowIfCancellationRequested();
                return Recognize(session, imageBytes);
            }
            catch (OperationCanceledException)
            {
                return new CoinRecognitionResult { Success = false, ErrorMessage = "Analisi annullata." };
            }
            catch (Exception ex)
            {
                return new CoinRecognitionResult { Success = false, ErrorMessage = $"Errore modello offline: {Describe(ex)}" };
            }
        }, ct);

    /// <summary>Il messaggio esterno di un errore di inizializzazione (es. libreria nativa) non dice la causa: si aggiunge la catena delle eccezioni interne.</summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 4; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" -> ", parts);
    }

    private static async Task<InferenceSession> GetSessionAsync(string modelAsset, CancellationToken ct)
    {
        await LoadLock.WaitAsync(ct);
        try
        {
            if (_session is null || _sessionAsset != modelAsset)
            {
                await using var asset = await FileSystem.OpenAppPackageFileAsync(modelAsset);
                using var memory = new MemoryStream();
                await asset.CopyToAsync(memory, ct);
                var loaded = new InferenceSession(memory.ToArray());
                _session?.Dispose();
                _session = loaded;
                _sessionAsset = modelAsset;
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

        var inputName = session.InputMetadata.Keys.First();
        var dims = session.InputMetadata[inputName].Dimensions;   // [1, 3, altezza, larghezza]
        var inputSize = dims.Length == 4 && dims[2] > 0 ? dims[2] : DefaultInputSize;

        var letterbox = Letterbox.For(source.Width, source.Height, inputSize);
        var pixels = DrawLetterboxed(source, letterbox, inputSize);
        var tensor = new DenseTensor<float>(Letterbox.ToTensor(pixels), new[] { 1, 3, inputSize, inputSize });

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
    private static int[] DrawLetterboxed(Bitmap source, Letterbox letterbox, int inputSize)
    {
        using var canvasBitmap = Bitmap.CreateBitmap(inputSize, inputSize, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(canvasBitmap);
        canvas.DrawColor(Android.Graphics.Color.Rgb(114, 114, 114));

        using var paint = new Android.Graphics.Paint { FilterBitmap = true, AntiAlias = true };
        var matrix = new Matrix();
        matrix.PostScale(letterbox.Scale, letterbox.Scale);
        matrix.PostTranslate(letterbox.PadX, letterbox.PadY);
        canvas.DrawBitmap(source, matrix, paint);

        var pixels = new int[inputSize * inputSize];
        canvasBitmap.GetPixels(pixels, 0, inputSize, 0, 0, inputSize, inputSize);
        return pixels;
    }
}
