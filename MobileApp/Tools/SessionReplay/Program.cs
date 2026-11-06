using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveCoinCounter.Diagnostics;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services.Recognition;
using LiveCoinCounter.Sessions;
using OpenCvSharp;
using OpenCvSharp.Dnn;

// Uso: SessionReplay <cartella-sessione>... [--out cartella] [--model modello.onnx] [--sets coin-sets.json] [--set setN]
// Per ogni sessione: i fotogrammi registrati passano, in ordine e tutti, dal motore dell'app; alla fine si confronta il numero di
// monete con quello del set (dalla nota della sessione) e con quello che aveva contato l'app. Con --out si scrive il registro
// diagnostico della riproduzione (stesso formato di quello del telefono).

string Option(string name, string? fallback) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? "";

string? FindUp(string relative)
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, relative))) return Path.Combine(d.FullName, relative);
    return null;
}

var model = Option("--model", FindUp(Path.Combine("MobileApp", "LiveCoinCounter", "Resources", "Raw", "coin_model_s_640.onnx")));
var setsFile = Option("--sets", FindUp(Path.Combine("MachineLearning", "datasets", "coin-sets.json")));
var outDir = Array.IndexOf(args, "--out") >= 0 ? Option("--out", null) : null;
var defaultSet = Array.IndexOf(args, "--set") >= 0 ? Option("--set", null) : null;
var withValue = new HashSet<string> { "--out", "--model", "--sets", "--set" };
var sessions = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (withValue.Contains(args[i])) { i++; continue; }
    if (!args[i].StartsWith("--")) sessions.Add(args[i]);
}
if (sessions.Count == 0) { Console.WriteLine("uso: SessionReplay <cartella-sessione>... [--out cartella] [--model m.onnx] [--sets file] [--set setN]"); return 1; }

var sets = new Dictionary<string, int>();
if (File.Exists(setsFile))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(setsFile));
    foreach (var s in doc.RootElement.EnumerateObject())
        sets[s.Name.ToLowerInvariant()] = s.Value.GetProperty("coins").EnumerateObject().Sum(c => c.Value.GetInt32());
}

using var net = CvDnn.ReadNetFromOnnx(model) ?? throw new InvalidOperationException("Modello non leggibile: " + model);
var inputSize = Regex.Match(Path.GetFileName(model), @"_(\d{3,4})\.onnx$") is { Success: true } mm ? int.Parse(mm.Groups[1].Value) : 640;

int exact = 0, compared = 0, totalError = 0;
foreach (var dir in sessions)
{
    var name = Path.GetFileName(dir.TrimEnd('/', '\\'));
    var framesFile = Path.Combine(dir, "frames", "frames.jsonl");
    if (!File.Exists(framesFile)) { Console.WriteLine($"{name}: nessun fotogramma registrato"); continue; }

    using var sessionDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "session.json")).TrimStart('﻿'));
    var notes = sessionDoc.RootElement.GetProperty("session").TryGetProperty("notes", out var n) ? n.GetString() : null;
    var setName = notes is not null && Regex.Match(notes, @"\bset\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } sm ? "set" + sm.Groups[1].Value : defaultSet;
    int? expected = setName is not null && sets.TryGetValue(setName.ToLowerInvariant(), out var e) ? e : null;
    var appCount = sessionDoc.RootElement.GetProperty("coins").GetArrayLength();

    IDiagLog log = NullDiagLog.Instance;
    JsonlDiagLog? fileLog = null;
    if (outDir is not null)
    {
        Directory.CreateDirectory(outDir);
        log = fileLog = new JsonlDiagLog(Path.Combine(outDir, name + ".jsonl"));
    }

    var tracker = new FeatureMosaicTracker(new OpenCvOrb(), TrackerOptions());
    var engine = new CaptureEngine(tracker, new YoloLocator(net, inputSize), new NullSink(), new MosaicCanvas(), log: log);
    engine.Start();
    var frames = 0;
    foreach (var line in File.ReadLines(framesFile))
    {
        using var f = JsonDocument.Parse(line);
        var file = Path.Combine(dir, "frames", f.RootElement.GetProperty("File").GetString()!);
        if (!File.Exists(file)) continue;
        var image = Images.Load(file);
        // come la fotocamera quando il motore è occupato: si aspetta che sia libero, così passano tutti i fotogrammi registrati
        while (!engine.WantsFrame) await Task.Delay(1);
        engine.PushFrame(new CameraFrame(image, f.RootElement.GetProperty("TimestampNs").GetInt64(), f.RootElement.GetProperty("RotationDegrees").GetInt32()));
        frames++;
    }
    while (!engine.WantsFrame) await Task.Delay(1);
    await engine.StopAsync();
    if (fileLog is not null) await fileLog.DisposeAsync();

    var count = engine.Coins.Count;
    var verdict = expected is { } x ? (count == x ? "OK" : $"{count - x:+0;-0}") : "?";
    if (expected is { } ex2) { compared++; if (count == ex2) exact++; totalError += Math.Abs(count - ex2); }
    Console.WriteLine($"{name} {setName ?? "-"}: {frames} fotogrammi, {engine.KeyframeCount} chiave → {count} monete (vere {expected?.ToString() ?? "?"}, app {appCount}) {verdict}");
}
if (compared > 0) Console.WriteLine($"esatte {exact}/{compared}, errore totale {totalError}");
return 0;

// opzioni del tracker da variabili d'ambiente, per provare varianti senza ricompilare (REPLAY_EARLY, REPLAY_SHARE, REPLAY_CAND)
static FeatureTrackerOptions TrackerOptions()
{
    var o = new FeatureTrackerOptions();
    if (Environment.GetEnvironmentVariable("REPLAY_EARLY") is { Length: > 0 } early) o = o with { EarlyAcceptInliers = int.Parse(early) };
    if (Environment.GetEnvironmentVariable("REPLAY_SHARE") is { Length: > 0 } share) o = o with { FixedReferenceMinShare = double.Parse(share, System.Globalization.CultureInfo.InvariantCulture) };
    if (Environment.GetEnvironmentVariable("REPLAY_CAND") is { Length: > 0 } cand) o = o with { CandidateKeyframes = int.Parse(cand) };
    if (Environment.GetEnvironmentVariable("REPLAY_DRIFT") is { Length: > 0 } drift) o = o with { DriftOverrideDeviation = double.Parse(drift, System.Globalization.CultureInfo.InvariantCulture) };
    return o;
}

/// <summary>Punti ORB con OpenCV, stessi parametri di OrbHelper.java sul telefono.</summary>
sealed class OpenCvOrb : IFeatureExtractor
{
    private readonly ORB _orb = ORB.Create(600, 1.2f, 4, 31, 0, 2, ORBScoreType.Harris, 31, 15);

    public string Id => "opencv";

    public FeatureSet Extract(GrayImage image, int maxFeatures)
    {
        _orb.MaxFeatures = maxFeatures;
        using var mat = new Mat(image.Height, image.Width, MatType.CV_8UC1);
        mat.SetArray(image.Pixels);
        using var descriptors = new Mat();
        _orb.DetectAndCompute(mat, null, out var keypoints, descriptors);
        var count = Math.Min(keypoints.Length, descriptors.Rows);
        if (count == 0) return FeatureSet.Empty;
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++) { xs[i] = keypoints[i].Pt.X; ys[i] = keypoints[i].Pt.Y; }
        var bytes = new byte[count * 32];
        Marshal.Copy(descriptors.Data, bytes, 0, bytes.Length);
        var words = new ulong[count * 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return new FeatureSet(xs, ys, words);
    }
}

/// <summary>Il rilevatore dell'app (modello ONNX) eseguito con OpenCV: letterbox grigio 114, NMS indipendente dal taglio, soglie dell'app.</summary>
sealed class YoloLocator(Net net, int inputSize) : ICoinLocator
{
    public string Id => "replay-onnx";

    public Task<IReadOnlyList<CoinBox>> LocateAsync(ArgbImage image, CancellationToken ct = default)
    {
        using var bgr = Images.ToMat(image);
        var letterbox = Letterbox.For(image.Width, image.Height, inputSize);
        using var canvas = new Mat(inputSize, inputSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size((int)Math.Round(image.Width * letterbox.Scale), (int)Math.Round(image.Height * letterbox.Scale)));
        var x0 = (int)Math.Round(letterbox.PadX);
        var y0 = (int)Math.Round(letterbox.PadY);
        using (var roi = new Mat(canvas, new Rect(x0, y0, Math.Min(resized.Width, inputSize - x0), Math.Min(resized.Height, inputSize - y0))))
            resized[new Rect(0, 0, roi.Width, roi.Height)].CopyTo(roi);
        using var blob = CvDnn.BlobFromImage(canvas, 1 / 255.0, new Size(inputSize, inputSize), new Scalar(), swapRB: true, crop: false);
        float[] data;
        int anchors, channels;
        lock (net)
        {
            net.SetInput(blob);
            using var output = net.Forward();
            anchors = output.Size(2);
            channels = output.Size(1);
            data = new float[channels * anchors];
            Marshal.Copy(output.Data, data, 0, data.Length);
        }
        IReadOnlyList<CoinBox> boxes = YoloDecoder.Decode(data, anchors, channels - 4, 0.25f, 0.6f, classAgnostic: true)
            .Select(letterbox.ToImage)
            .Select(d =>
            {
                var cents = channels - 4 == YoloDecoder.ClassCents.Length ? YoloDecoder.ClassCents[d.ClassIndex] : (int?)null;
                return new CoinBox(new RectF(d.X, d.Y, d.Width, d.Height), d.Confidence, cents, cents is null ? 0 : d.Confidence);
            })
            .ToList();
        return Task.FromResult(boxes);
    }
}

sealed class NullSink : ICaptureSink
{
    public Task OnKeyframeAsync(int index, ArgbImage image, Homography frameToMosaic, double sharpness) => Task.CompletedTask;
    public Task OnCoinsAsync(IReadOnlyList<CoinChange> changes) => Task.CompletedTask;
}

static class Images
{
    /// <summary>JPEG → immagine ARGB (come le immagini della fotocamera nell'app).</summary>
    public static ArgbImage Load(string path)
    {
        using var mat = Cv2.ImRead(path, ImreadModes.Color);
        var bytes = new byte[mat.Width * mat.Height * 3];
        Marshal.Copy(mat.Data, bytes, 0, bytes.Length);
        var pixels = new int[mat.Width * mat.Height];
        for (int i = 0, j = 0; i < pixels.Length; i++, j += 3)
            pixels[i] = unchecked((int)0xFF000000) | (bytes[j + 2] << 16) | (bytes[j + 1] << 8) | bytes[j];
        return new ArgbImage(mat.Width, mat.Height, pixels);
    }

    public static Mat ToMat(ArgbImage image)
    {
        var bytes = new byte[image.Width * image.Height * 3];
        for (int i = 0, j = 0; i < image.Pixels.Length; i++, j += 3)
        {
            var p = image.Pixels[i];
            bytes[j] = (byte)p;
            bytes[j + 1] = (byte)(p >> 8);
            bytes[j + 2] = (byte)(p >> 16);
        }
        var mat = new Mat(image.Height, image.Width, MatType.CV_8UC3);
        Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
        return mat;
    }
}
