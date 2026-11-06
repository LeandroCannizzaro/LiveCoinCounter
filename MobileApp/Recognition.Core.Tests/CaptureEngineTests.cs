using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Sessions;
using Xunit;

namespace Recognition.Core.Tests;

/// <summary>
/// Prova di tutto il percorso live su un tavolo sintetico: la fotocamera virtuale scorre il tavolo, il motore compone il
/// mosaico e un rilevatore finto (che conosce le monete vere) trova le monete in ogni fotogramma chiave.
/// Ogni moneta deve risultare una sola volta.
/// </summary>
public class CaptureEngineTests
{
    private const int W = 480, H = 360;

    [Fact]
    public async Task Sweep_over_table_counts_every_coin_once()
    {
        var scene = new SyntheticScene(1500, 900, seed: 11);
        var random = new Random(4);
        while (scene.Coins.Count < 14)
        {
            double x = 120 + random.Next(1260), y = 140 + random.Next(520), r = 18 + random.Next(10);
            if (scene.Coins.All(c => Math.Sqrt((c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y)) > c.R + r + 6))
                scene.AddCoin(x, y, r);
        }

        var locator = new OracleLocator(scene);
        var sink = new MemorySink();
        var tracker = new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { MosaicScale = 1 });
        var engine = new CaptureEngine(tracker, locator, sink, new MosaicCanvas(),
            new CaptureOptions { MinAbsoluteSharpness = 0, FirstKeyframeMinSharpness = 0 });
        engine.Start();

        // passata a serpentina: due righe che si sovrappongono di 120 px (più di una moneta), spostamenti di 50 px
        var path = new List<(double X, double Y)>();
        for (var x = 240.0; x <= 1260; x += 50) path.Add((x, 280));
        for (var y = 280.0; y <= 520; y += 40) path.Add((1260, y));
        for (var x = 1260.0; x >= 240; x -= 50) path.Add((x, 520));

        var t = 0L;
        foreach (var (x, y) in path)
        {
            var view = SyntheticScene.View(x, y, W, H, degrees: Math.Sin(t) * 3);
            var image = scene.Render(view, W, H);
            locator.Register(image, view);
            engine.PushFrame(new CameraFrame(image, t++, 0));
            // il test consegna un fotogramma alla volta come una fotocamera lenta
            while (!engine.WantsFrame) await Task.Delay(1);
            await Task.Delay(5);
        }

        await engine.StopAsync();

        Assert.True(engine.KeyframeCount >= 5, $"fotogrammi chiave: {engine.KeyframeCount}");
        Assert.Equal(scene.Coins.Count, engine.Coins.Count);
        Assert.Equal(scene.Coins.Count, sink.Crops.Keys.Count);
        Assert.All(sink.Crops.Values, c => Assert.Equal(256, c.Width));
    }

    private sealed class FixedMarkerDetector : IMarkerDetector
    {
        public int Calls;
        public IReadOnlyList<MarkerDetection> Detect(GrayImage image)
        {
            Calls++;
            return new[] { new MarkerDetection(4, new Quad(new PointD(100, 100), new PointD(160, 100), new PointD(160, 160), new PointD(100, 160))) };
        }
    }

    [Fact]
    public async Task Engine_registers_markers_seen_in_tracked_frames()
    {
        var scene = new SyntheticScene(1200, 900, seed: 5);
        var detector = new FixedMarkerDetector();
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { MosaicScale = 1 }), new OracleLocator(scene),
            new MemorySink(), new MosaicCanvas(), new CaptureOptions { MinAbsoluteSharpness = 0, FirstKeyframeMinSharpness = 0, MarkerEveryNthFrame = 1 },
            markerDetector: detector);
        engine.Start();

        for (var i = 0; i < 6; i++)
        {
            var view = SyntheticScene.View(300 + i * 20, 300, 480, 360, 0);
            engine.PushFrame(new CameraFrame(scene.Render(view, 480, 360), i, 0));
            while (!engine.WantsFrame) await Task.Delay(1);
            await Task.Delay(5);
        }
        await engine.StopAsync();

        Assert.True(detector.Calls >= 3, $"chiamate del rilevatore: {detector.Calls}");
        Assert.True(engine.Markers.Count >= 1);
        Assert.True(engine.Markers.MedianPxPerMm > 0);
    }

    [Fact]
    public async Task Single_shot_takes_several_keyframes_of_the_same_view_and_counts_each_coin_once()
    {
        // telefono fermo: senza scatti ci sarebbe un solo fotogramma chiave; con 3 scatti ce ne sono 3, e le monete restano quelle vere
        var scene = new SyntheticScene(1000, 800, seed: 21);
        var random = new Random(8);
        while (scene.Coins.Count < 6)
        {
            double x = 380 + random.Next(240), y = 300 + random.Next(200), r = 18 + random.Next(8);
            if (scene.Coins.All(c => Math.Sqrt((c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y)) > c.R + r + 6)) scene.AddCoin(x, y, r);
        }
        var locator = new OracleLocator(scene);
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { MosaicScale = 1 }), locator, new MemorySink(),
            new MosaicCanvas(), new CaptureOptions { MinAbsoluteSharpness = 0, FirstKeyframeMinSharpness = 0 });
        engine.Start();
        engine.RequestShots(3);

        for (var i = 0; i < 12; i++)
        {
            var view = SyntheticScene.View(500 + (i % 2), 400, W, H);
            var image = scene.Render(view, W, H);
            locator.Register(image, view);
            engine.PushFrame(new CameraFrame(image, i * 150_000_000L, 0));      // 150 ms tra un fotogramma e l'altro
            while (!engine.WantsFrame) await Task.Delay(1);
            await Task.Delay(5);
        }
        await engine.StopAsync();

        Assert.Equal(0, engine.PendingShots);
        Assert.Equal(3, engine.KeyframeCount);
        Assert.Equal(scene.Coins.Count, engine.Coins.Count);
        Assert.All(engine.Coins.Tracks, t => Assert.True(t.Observations >= 2, $"moneta {t.Id} vista {t.Observations} volte"));
    }

    [Fact]
    public async Task Lost_tracking_reports_the_last_known_position()
    {
        var scene = new SyntheticScene(1200, 900, seed: 9);
        var other = new SyntheticScene(1200, 900, seed: 77);
        var updates = new List<TrackingUpdate>();
        var tracker = new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { LostAfter = 2 });
        var engine = new CaptureEngine(tracker, new OracleLocator(scene), new MemorySink(), new MosaicCanvas(),
            new CaptureOptions { MinAbsoluteSharpness = 0, FirstKeyframeMinSharpness = 0 });
        engine.TrackingUpdated += u => { lock (updates) updates.Add(u); };
        engine.Start();

        var view = SyntheticScene.View(500, 450, W, H);
        await PushAndWaitAsync(engine, new CameraFrame(scene.Render(view, W, H), 0, 0), 0);
        for (var i = 1; i <= 4; i++)
            await PushAndWaitAsync(engine, new CameraFrame(other.Render(view, W, H), i, 0), i);
        await engine.StopAsync();

        TrackingUpdate[] snapshot;
        lock (updates) snapshot = updates.ToArray();
        var lost = Assert.Single(snapshot.Where(u => u.Status == TrackStatus.Lost).Take(1));
        Assert.Null(lost.FrameQuad);
        Assert.NotNull(lost.LastKnownQuad);
        Assert.Contains("riquadro arancione", lost.Hint);
    }

    [Theory]
    [InlineData(100, 0, 90, "a destra")]
    [InlineData(-100, 0, 270, "a sinistra")]
    [InlineData(0, -100, 0, "in alto")]
    [InlineData(0, 100, 180, "in basso")]
    [InlineData(70, -70, 45, "in alto a destra")]
    public void Recovery_guidance_points_the_way_back_in_screen_directions(double vx, double vy, double degrees, string words)
    {
        // telefono non girato rispetto all'inizio: il primo fotogramma ha l'angolo A→B a 90°
        var estimate = new PoseEstimate(Homography.Rotation(90), new PointD(vx, vy), Tracked: true);

        var guidance = RecoveryGuidance.From(estimate, 640, 480)!;

        Assert.InRange(guidance.ArrowDegrees, degrees - 1, degrees + 1);
        Assert.Equal(words, guidance.DirectionWords);
        Assert.InRange(guidance.DistancePx, Math.Sqrt(vx * vx + vy * vy) - 0.01, Math.Sqrt(vx * vx + vy * vy) + 0.01);
        Assert.Equal(480, guidance.FrameSidePx, 1);
    }

    [Fact]
    public async Task Dark_frames_suggest_the_torch()
    {
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite()), new OracleLocator(new SyntheticScene(400, 300)), new MemorySink(), new MosaicCanvas());
        TrackingUpdate? last = null;
        engine.TrackingUpdated += u => last = u;
        engine.Start();

        var dark = new ArgbImage(W, H, Enumerable.Repeat(ImageOps.Argb(8, 8, 8), W * H).ToArray());
        for (var i = 0; i < 12; i++) await PushAndWaitAsync(engine, new CameraFrame(dark, i, 0), i);
        await engine.StopAsync();

        Assert.True(last?.Dark);
        Assert.Contains("torcia", last?.Hint);
    }

    [Fact]
    public async Task Persistently_blurry_frames_suggest_moving_away()
    {
        var scene = new SyntheticScene(1200, 900, seed: 9);
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite()), new OracleLocator(scene), new MemorySink(), new MosaicCanvas(),
            new CaptureOptions { MinAbsoluteSharpness = 0, FirstKeyframeMinSharpness = 0, BlurryBelow = 1e9 });
        TrackingUpdate? last = null;
        engine.TrackingUpdated += u => last = u;
        engine.Start();

        for (var i = 0; i < 12; i++)
            await PushAndWaitAsync(engine, new CameraFrame(scene.Render(SyntheticScene.View(500 + i, 450, W, H), W, H), i, 0), i);
        await engine.StopAsync();

        Assert.True(last?.Blurry);
        Assert.Contains("sfocata", last?.Hint);
    }

    [Fact]
    public async Task Blurry_first_frame_does_not_start_the_mosaic()
    {
        var scene = new SyntheticScene(1200, 900, seed: 9);
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite()), new OracleLocator(scene), new MemorySink(), new MosaicCanvas(),
            new CaptureOptions { FirstKeyframeMinSharpness = 1e9 });
        TrackingUpdate? last = null;
        engine.TrackingUpdated += u => last = u;
        engine.Start();

        await PushAndWaitAsync(engine, new CameraFrame(scene.Render(SyntheticScene.View(500, 450, W, H), W, H), 0, 0), 0);
        await engine.StopAsync();

        Assert.Equal(0, engine.KeyframeCount);
        Assert.Equal(0, engine.Canvas.TileCount);
        Assert.Contains("sfocata", last?.Hint);
    }

    [Fact]
    public async Task Still_photo_is_a_single_keyframe()
    {
        var scene = new SyntheticScene(800, 600, seed: 2);
        scene.AddCoin(200, 200, 30);
        scene.AddCoin(500, 300, 25);
        var locator = new OracleLocator(scene);
        locator.Register(scene.Table, Homography.Identity);
        var sink = new MemorySink();
        var engine = new CaptureEngine(new FeatureMosaicTracker(new OrbLite()), locator, sink, new MosaicCanvas());
        engine.Start();

        await engine.AddStillAsync(scene.Table);
        await engine.StopAsync();

        Assert.Equal(1, engine.KeyframeCount);
        Assert.Equal(2, engine.Coins.Count);
        Assert.True(engine.Canvas.TileCount > 0);
    }

    /// <summary>Consegna un fotogramma e aspetta che il motore l'abbia elaborato.</summary>
    private static async Task PushAndWaitAsync(CaptureEngine engine, CameraFrame frame, int _)
    {
        while (!engine.WantsFrame) await Task.Delay(1);
        engine.PushFrame(frame);
        await Task.Delay(5);
        while (!engine.WantsFrame) await Task.Delay(1);
    }

    /// <summary>Rilevatore perfetto: proietta le monete vere del tavolo nel fotogramma corrente.</summary>
    private sealed class OracleLocator : ICoinLocator
    {
        private readonly SyntheticScene _scene;
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArgbImage, object> _views = new();
        public OracleLocator(SyntheticScene scene) => _scene = scene;
        public string Id => "oracle";

        /// <summary>Ricorda con quale vista è stato prodotto il fotogramma: il rilevamento avviene più tardi, su un altro thread.</summary>
        public void Register(ArgbImage image, Homography frameToTable) => _views.AddOrUpdate(image, frameToTable);

        public Task<IReadOnlyList<CoinBox>> LocateAsync(ArgbImage image, CancellationToken ct = default)
        {
            var tableToFrame = ((Homography)_views.GetValue(image, _ => throw new InvalidOperationException("Fotogramma sconosciuto"))).Inverse();
            var boxes = _scene.Coins.Select(c =>
            {
                var box = tableToFrame.MapBox(new RectF((float)(c.X - c.R), (float)(c.Y - c.R), (float)(2 * c.R), (float)(2 * c.R)));
                return new CoinBox(box, 0.9f);
            }).Where(b => b.Box.Right > 0 && b.Box.Bottom > 0 && b.Box.X < image.Width && b.Box.Y < image.Height).ToList();
            return Task.FromResult<IReadOnlyList<CoinBox>>(boxes);
        }
    }

    private sealed class MemorySink : ICaptureSink
    {
        public Dictionary<int, ArgbImage> Crops { get; } = new();
        public Task OnKeyframeAsync(int index, ArgbImage image, Homography frameToMosaic, double sharpness) => Task.CompletedTask;

        public Task OnCoinsAsync(IReadOnlyList<CoinChange> changes)
        {
            lock (Crops)
                foreach (var c in changes.Where(c => c.Crop is not null))
                    Crops[c.Track.Id] = c.Crop!;
            return Task.CompletedTask;
        }
    }
}
