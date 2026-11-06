using System.Text.Json;
using LiveCoinCounter.Diagnostics;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Sessions;
using Xunit;

namespace Recognition.Core.Tests;

public class DiagLogTests : IDisposable
{
    private const int W = 480, H = 360;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "diaglog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Writes_one_json_object_per_line()
    {
        var path = Path.Combine(_folder, "d.jsonl");
        var log = new JsonlDiagLog(path);
        log.Event("a", new { x = 1, y = "due", skip = (string?)null });
        log.Event("b");
        await log.DisposeAsync();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("a", first.RootElement.GetProperty("e").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("d").GetProperty("x").GetInt32());
        Assert.False(first.RootElement.GetProperty("d").TryGetProperty("skip", out _));
    }

    [Fact]
    public async Task Stops_logging_past_the_size_limit_but_keeps_important_events()
    {
        var path = Path.Combine(_folder, "d.jsonl");
        var log = new JsonlDiagLog(path, maxBytes: 300);
        for (var i = 0; i < 50; i++) log.Event("frame", new { i, filler = new string('x', 40) });
        log.Event("error", new { message = "fine" }, important: true);
        await log.DisposeAsync();

        var text = File.ReadAllText(path);
        Assert.Contains("log_truncated", text);
        Assert.Contains("\"e\":\"error\"", text);
        Assert.True(text.Split('\n').Length < 20, "il file doveva fermarsi presto");
    }

    [Fact]
    public async Task Engine_logs_frames_keyframes_detections_and_stats_with_tracker_attempts()
    {
        var scene = new SyntheticScene(1200, 900, seed: 9);
        var other = new SyntheticScene(1200, 900, seed: 77);
        scene.AddCoin(500, 450, 25);
        var path = Path.Combine(_folder, "engine.jsonl");
        var log = new JsonlDiagLog(path);
        var tracker = new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { LostAfter = 2 });
        var engine = new CaptureEngine(tracker, new FixedLocator(), new NullSink(), new MosaicCanvas(),
            new CaptureOptions { MinAbsoluteSharpness = 0, FirstKeyframeMinSharpness = 0 }, log);
        engine.Start();

        var view = SyntheticScene.View(500, 450, W, H);
        await PushAsync(engine, scene.Render(view, W, H), 0);
        for (var i = 1; i <= 3; i++) await PushAsync(engine, other.Render(view, W, H), i);
        await engine.StopAsync();
        await log.DisposeAsync();

        var events = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        string Type(JsonElement e) => e.GetProperty("e").GetString()!;

        Assert.Contains(events, e => Type(e) == "engine_start");
        Assert.Contains(events, e => Type(e) == "keyframe");
        Assert.Contains(events, e => Type(e) == "detect");
        Assert.Contains(events, e => Type(e) == "engine_stop");

        var frames = events.Where(e => Type(e) == "frame").ToList();
        Assert.Equal(4, frames.Count);
        Assert.Equal("keyframe", frames[0].GetProperty("d").GetProperty("decision").GetString());

        // il fotogramma perso racconta perché: i tentativi sui fotogrammi chiave con il loro esito
        var lost = frames.Last().GetProperty("d");
        Assert.Equal("not_placed", lost.GetProperty("decision").GetString());
        var attempts = lost.GetProperty("detail").GetProperty("attempts");
        Assert.True(attempts.GetArrayLength() >= 1);
        Assert.NotEqual("ok", attempts[0].GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(190, 120, 70, "copper")]    // rame: 1, 2, 5 centesimi
    [InlineData(215, 175, 70, "gold")]      // oro nordico: 10, 20, 50 centesimi
    public void Coin_color_separates_copper_from_gold(int r, int g, int b, string expected)
    {
        var coin = SolidCoin(ImageOps.Argb(r, g, b), ImageOps.Argb(r, g, b));
        Assert.Equal(expected, CoinColorAnalysis.Analyze(coin).Guess);
    }

    [Fact]
    public void Coin_color_is_unsure_for_silver_and_for_in_between_hues()
    {
        Assert.Equal("?", CoinColorAnalysis.Analyze(SolidCoin(ImageOps.Argb(190, 195, 200), ImageOps.Argb(190, 195, 200))).Guess);   // argento
        Assert.Equal("gold", CoinColorAnalysis.Analyze(SolidCoin(ImageOps.Argb(215, 175, 70), ImageOps.Argb(185, 190, 195))).Guess);   // 2€: centro oro
    }

    private static ArgbImage SolidCoin(int core, int ring)
    {
        const int size = 128;
        var image = new ArgbImage(size, size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = Math.Sqrt((x - size / 2.0) * (x - size / 2.0) + (y - size / 2.0) * (y - size / 2.0)) / (size / 2.0);
            image.Pixels[y * size + x] = d < 0.62 ? core : ring;
        }
        return image;
    }

    private static async Task PushAsync(CaptureEngine engine, ArgbImage image, int n)
    {
        while (!engine.WantsFrame) await Task.Delay(1);
        engine.PushFrame(new CameraFrame(image, n, 0));
        await Task.Delay(5);
        while (!engine.WantsFrame) await Task.Delay(1);
    }

    private sealed class FixedLocator : ICoinLocator
    {
        public string Id => "fixed";

        public Task<IReadOnlyList<CoinBox>> LocateAsync(ArgbImage image, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CoinBox>>(new[] { new CoinBox(new RectF(200, 150, 50, 50), 0.9f) });
    }

    private sealed class NullSink : ICaptureSink
    {
        public Task OnKeyframeAsync(int index, ArgbImage image, Homography frameToMosaic, double sharpness) => Task.CompletedTask;
        public Task OnCoinsAsync(IReadOnlyList<CoinChange> changes) => Task.CompletedTask;
    }
}
