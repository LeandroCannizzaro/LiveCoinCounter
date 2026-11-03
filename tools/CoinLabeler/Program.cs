using CoinLabeler;
using LiveCoinCounter.Services.Recognition;

const string Usage = """
    CoinLabeler - etichetta foto di monete euro con l'AI e prepara coppie immagine + JSON

    label  <cartella_input> <cartella_output> [--providers gemini,anthropic] [--limit N]
                                              [--source nome] [--hough N] [--no-ai]
    eval   <cartella_output> <dataset_yolo>   confronta i JSON con un dataset YOLO di riferimento
    report <cartella_output>                  rigenera report/index.html

    Le chiavi si passano con variabili d'ambiente: GEMINI_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY...
    (opzionali: {ID}_MODEL, {ID}_BASE_URL). Con --no-ai si ottengono solo i box (nessuna chiamata a pagamento).
    Le risposte dell'AI sono salvate in <cartella_output>/.cache: rilanciando il comando si riprende da dove ci si era fermati.
    """;

if (args.Length == 0) { Console.WriteLine(Usage); return 1; }

ImageShrink.Shrinker = bytes => ImageIO.ShrinkForUpload(bytes);

string Option(string name, string fallback) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;

try
{
    switch (args[0])
    {
        case "label" when args.Length >= 3:
            return await LabelAsync(args[1], args[2]);
        case "eval" when args.Length >= 3:
            Evaluator.Run(args[1], args[2]);
            return 0;
        case "report" when args.Length >= 2:
            HtmlReport.Build(args[1]);
            Console.WriteLine($"Creato {Path.Combine(args[1], "report", "index.html")}");
            return 0;
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

async Task<int> LabelAsync(string input, string output)
{
    var noAi = args.Contains("--no-ai");
    var limit = int.Parse(Option("--limit", "0"));
    var hough = double.Parse(Option("--hough", "45"), System.Globalization.CultureInfo.InvariantCulture);
    var source = Option("--source", "own-phone");

    Directory.CreateDirectory(output);
    var cacheDir = Path.Combine(output, ".cache");

    var providers = noAi
        ? new List<Named>()
        : Option("--providers", "gemini").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => (Named)Providers.Create(id, cacheDir)).ToList();

    var pipeline = new LabelPipeline(providers, source, hough);

    var files = Directory.GetFiles(input).Where(f => ImageIO.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .OrderBy(f => f).ToList();
    if (limit > 0) files = files.Take(limit).ToList();

    Console.WriteLine($"{files.Count} immagini, provider: {(providers.Count == 0 ? "nessuno (solo box)" : string.Join(", ", providers.Select(p => p.Label)))}");

    int auto = 0, review = 0, failed = 0;
    foreach (var file in files)
    {
        var jsonPath = Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".json");
        try
        {
            var (label, jpeg) = await pipeline.ProcessAsync(file, CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(output, label.Image), jpeg);
            await File.WriteAllTextAsync(jsonPath, System.Text.Json.JsonSerializer.Serialize(label, LabelFile.Json));

            if (label.Status == "auto") auto++; else review++;
            Console.WriteLine($"  {Path.GetFileName(file)}: {label.Coins.Count} monete [{label.Status}]");
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"  {Path.GetFileName(file)}: ERRORE {ex.Message}");
        }
    }

    HtmlReport.Build(output);
    Console.WriteLine($"Fatto: {auto} automatiche, {review} da rivedere/solo box, {failed} errori. Report: {Path.Combine(output, "report", "index.html")}");
    return failed == 0 ? 0 : 3;
}
