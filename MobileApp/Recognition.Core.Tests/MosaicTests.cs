using LiveCoinCounter.Mosaic;
using Xunit;

namespace Recognition.Core.Tests;

public class MosaicTests
{
    private const int W = 480, H = 360;

    [Fact]
    public void OrbLite_matches_a_shifted_view()
    {
        var scene = new SyntheticScene(1200, 900);
        var a = scene.Render(SyntheticScene.View(500, 450, W, H), W, H);
        var b = scene.Render(SyntheticScene.View(560, 470, W, H, degrees: 8), W, H);
        var orb = new OrbLite();
        var fa = orb.Extract(ImageOps.ToGray(a, 640, out _), 600);
        var fb = orb.Extract(ImageOps.ToGray(b, 640, out _), 600);

        Assert.True(fa.Count > 150, $"pochi punti: {fa.Count}");
        var matches = FeatureMatcher.Match(fb, fa);
        var ransac = HomographyRansac.Estimate(matches);

        Assert.NotNull(ransac);
        Assert.True(ransac!.Inliers >= 40, $"inlier: {ransac.Inliers} su {matches.Count}");
    }

    [Fact]
    public void Feature_tracker_places_frames_consistently_with_the_table()
    {
        var scene = new SyntheticScene(1400, 1000, seed: 5);
        var tracker = new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { MosaicScale = 1 });
        var start = SyntheticScene.View(400, 400, W, H);

        // il primo fotogramma fissa il sistema del mosaico: mosaico = fotogramma 0
        var first = tracker.Process(new CameraFrame(scene.Render(start, W, H), 0, 0));
        Assert.NotNull(first.FrameToMosaic);
        tracker.CommitKeyframe(first.FrameToMosaic!.Value);
        var tableToMosaic = first.FrameToMosaic.Value * start.Inverse();

        // percorso: spostamenti di 60 px con piccole rotazioni, nuovo fotogramma chiave ogni due passi
        for (var step = 1; step <= 8; step++)
        {
            var view = SyntheticScene.View(400 + step * 60, 400 + step * 20, W, H, degrees: step * 2);
            var result = tracker.Process(new CameraFrame(scene.Render(view, W, H), step, 0));

            Assert.Equal(TrackStatus.Tracking, result.Status);
            Assert.NotNull(result.FrameToMosaic);
            var expected = tableToMosaic * view;
            var (ex, ey) = expected.Apply(W / 2.0, H / 2.0);
            var (ax, ay) = result.FrameToMosaic!.Value.Apply(W / 2.0, H / 2.0);
            Assert.True(Math.Abs(ex - ax) < 3 && Math.Abs(ey - ay) < 3, $"passo {step}: atteso ({ex:0.0}, {ey:0.0}), trovato ({ax:0.0}, {ay:0.0})");

            if (step % 2 == 0) tracker.CommitKeyframe(result.FrameToMosaic.Value);
        }
    }

    [Fact]
    public void Anchors_keep_tracking_along_a_long_path_without_mosaic_keyframes()
    {
        var scene = new SyntheticScene(2400, 1000, seed: 21);
        var tracker = new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { MosaicScale = 1 });
        var start = SyntheticScene.View(400, 400, W, H);
        var first = tracker.Process(new CameraFrame(scene.Render(start, W, H), 0, 0));
        tracker.CommitKeyframe(first.FrameToMosaic!.Value);       // l'unico fotogramma chiave del mosaico
        var tableToMosaic = first.FrameToMosaic.Value * start.Inverse();

        // 1400 px di percorso a passi di 70 px: senza ancoraggi il primo fotogramma chiave si perde dopo pochi passi
        var anchors = 0;
        for (var step = 1; step <= 20; step++)
        {
            var view = SyntheticScene.View(400 + step * 70, 400 + (step % 3) * 10, W, H, degrees: Math.Sin(step) * 2);
            var result = tracker.Process(new CameraFrame(scene.Render(view, W, H), step, 0));

            Assert.True(result.FrameToMosaic is not null, $"perso al passo {step}: {result.Detail?.Reason} {string.Join(" ", result.Detail?.Attempts.Select(a => a.Outcome) ?? Array.Empty<string>())}");
            if (result.Detail?.AnchorAdded == true) anchors++;
            var (ex, ey) = (tableToMosaic * view).Apply(W / 2.0, H / 2.0);
            var (ax, ay) = result.FrameToMosaic!.Value.Apply(W / 2.0, H / 2.0);
            Assert.True(Math.Abs(ex - ax) < 15 && Math.Abs(ey - ay) < 15, $"passo {step}: atteso ({ex:0},{ey:0}), trovato ({ax:0},{ay:0})");
        }
        Assert.True(anchors >= 3, $"ancoraggi aggiunti: {anchors}");
    }

    [Fact]
    public void Guided_matching_survives_a_repetitive_texture_where_global_matching_does_not()
    {
        // un tavolo con la stessa grana ripetuta ogni 64 px: ogni punto ha molti sosia in tutta l'immagine
        var random = new Random(5);
        var tile = new int[64 * 64];
        for (var i = 0; i < tile.Length; i++) tile[i] = 70;
        for (var r = 0; r < 14; r++)
        {
            int x0 = random.Next(4, 52), y0 = random.Next(4, 52), w = random.Next(4, 12), h = random.Next(4, 12), v = random.Next(20, 230);
            for (var y = y0; y < y0 + h; y++) for (var x = x0; x < x0 + w; x++) tile[y * 64 + x] = v;
        }
        ArgbImage Render(int shiftX, int shiftY, int seed)
        {
            var noise = new Random(seed);
            var image = new ArgbImage(W, H);
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var v = Math.Clamp(tile[((y + shiftY) % 64) * 64 + (x + shiftX) % 64] + noise.Next(-3, 4), 0, 255);
                image.Pixels[y * W + x] = ImageOps.Argb(v, v, v);
            }
            return image;
        }

        var orb = new OrbLite();
        var fa = orb.Extract(ImageOps.ToGray(Render(0, 0, 1), 640, out _), 600);
        var fb = orb.Extract(ImageOps.ToGray(Render(18, 9, 2), 640, out _), 600);   // il telefono si è mosso di poco

        var global = HomographyRansac.Estimate(FeatureMatcher.Match(fb, fa));
        var guided = HomographyRansac.Estimate(FeatureMatcher.MatchNear(fb, fa, radius: 40));

        Assert.NotNull(guided);
        Assert.True(guided!.Inliers >= 30, $"inlier guidati: {guided.Inliers}");
        Assert.True(global is null || global.Inliers < guided.Inliers, $"globale {global?.Inliers} contro guidato {guided.Inliers}");
    }

    [Fact]
    public void Chain_from_the_previous_frame_follows_a_path_even_without_anchors()
    {
        var scene = new SyntheticScene(2400, 1000, seed: 21);
        // ancoraggi spenti e un solo fotogramma chiave: il solo confronto con il frame precedente deve bastare
        var tracker = new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { MosaicScale = 1, MinAnchorInliers = 100000 });
        var start = SyntheticScene.View(400, 400, W, H);
        var first = tracker.Process(new CameraFrame(scene.Render(start, W, H), 0, 0));
        tracker.CommitKeyframe(first.FrameToMosaic!.Value);
        var tableToMosaic = first.FrameToMosaic.Value * start.Inverse();

        for (var step = 1; step <= 20; step++)
        {
            var view = SyntheticScene.View(400 + step * 70, 400 + (step % 3) * 10, W, H, degrees: Math.Sin(step) * 2);
            var result = tracker.Process(new CameraFrame(scene.Render(view, W, H), step, 0));

            Assert.True(result.FrameToMosaic is not null, $"perso al passo {step}: {string.Join(" ", result.Detail?.Attempts.Select(a => a.Outcome) ?? Array.Empty<string>())}");
            var (ex, ey) = (tableToMosaic * view).Apply(W / 2.0, H / 2.0);
            var (ax, ay) = result.FrameToMosaic!.Value.Apply(W / 2.0, H / 2.0);
            Assert.True(Math.Abs(ex - ax) < 25 && Math.Abs(ey - ay) < 25, $"passo {step}: atteso ({ex:0},{ey:0}), trovato ({ax:0},{ay:0})");
        }
    }

    [Fact]
    public void Tracker_reports_lost_on_unrelated_image_and_relocalizes()
    {
        var scene = new SyntheticScene(1200, 900, seed: 9);
        var other = new SyntheticScene(1200, 900, seed: 77);
        var tracker = new FeatureMosaicTracker(new OrbLite(), new FeatureTrackerOptions { LostAfter = 2 });
        var view = SyntheticScene.View(500, 450, W, H);
        var first = tracker.Process(new CameraFrame(scene.Render(view, W, H), 0, 0));
        tracker.CommitKeyframe(first.FrameToMosaic!.Value);

        TrackResult result = first;
        for (var i = 0; i < 3; i++)
            result = tracker.Process(new CameraFrame(other.Render(view, W, H), i + 1, 0));
        Assert.Equal(TrackStatus.Lost, result.Status);

        var back = tracker.Process(new CameraFrame(scene.Render(SyntheticScene.View(530, 450, W, H), W, H), 10, 0));
        Assert.Equal(TrackStatus.Tracking, back.Status);
        Assert.NotNull(back.FrameToMosaic);
    }

    [Fact]
    public void Tracker_refuses_to_start_on_a_featureless_frame()
    {
        var tracker = new FeatureMosaicTracker(new OrbLite());
        var flat = new ArgbImage(W, H, Enumerable.Repeat(ImageOps.Argb(30, 30, 30), W * H).ToArray());

        var result = tracker.Process(new CameraFrame(flat, 0, 0));

        Assert.Equal(TrackStatus.Initializing, result.Status);
        Assert.Null(result.FrameToMosaic);
        Assert.NotNull(result.Hint);
    }

    [Fact]
    public void Canvas_writes_each_table_point_once_and_prefers_better_frames()
    {
        var canvas = new MosaicCanvas();
        var red = new ArgbImage(300, 200, Enumerable.Repeat(ImageOps.Argb(255, 0, 0), 300 * 200).ToArray());
        var blue = new ArgbImage(300, 200, Enumerable.Repeat(ImageOps.Argb(0, 0, 255), 300 * 200).ToArray());

        var changed = canvas.Compose(red, Homography.Identity, quality: 0.3f);
        Assert.NotEmpty(changed);
        canvas.Compose(blue, Homography.Translation(150, 0), quality: 1f);

        var image = canvas.Render(4096, out var scale, out var origin)!;
        Assert.Equal(1, scale);
        int At(int x, int y) => image[(int)(x - origin.X), (int)(y - origin.Y)];

        Assert.Equal(ImageOps.Argb(255, 0, 0), At(20, 100));       // solo il rosso
        Assert.Equal(ImageOps.Argb(0, 0, 255), At(250, 100));      // sovrapposizione: vince il fotogramma migliore
        Assert.Equal(ImageOps.Argb(0, 0, 255), At(420, 100));      // solo il blu
        Assert.InRange(canvas.Bounds.Width, 449, 451);
    }

    [Fact]
    public void Surface_mode_shows_a_solid_plane_with_only_the_coins_in_photo()
    {
        var canvas = new MosaicCanvas();
        var photo = ImageOps.Argb(200, 30, 30);
        var plane = ImageOps.Argb(60, 65, 75);
        canvas.Compose(new ArgbImage(300, 200, Enumerable.Repeat(photo, 300 * 200).ToArray()), Homography.Identity, 1f);
        var coins = new[] { new RectF(100, 50, 60, 60) };     // cerchio al centro (130, 80), raggio ~31

        var image = canvas.Render(4096, out _, out var origin, plane, coins)!;
        int At(int x, int y) => image[(int)(x - origin.X), (int)(y - origin.Y)];

        Assert.Equal(photo, At(130, 80));      // dentro la moneta: la foto
        Assert.Equal(plane, At(20, 20));       // fuori: il piano
        Assert.Equal(plane, At(101, 51));      // angolo del riquadro, fuori dal cerchio
        Assert.Equal(photo, canvas.Render(4096, out _, out _)![130 - (int)origin.X, 80 - (int)origin.Y]);   // senza stile resta la foto

        var tile = canvas.SnapshotSurface(null, plane, coins).Single(t => t.Key == new TileKey(0, 0)).Pixels;
        Assert.Equal(photo, tile[80 * MosaicCanvas.TileSize + 130]);
        Assert.Equal(plane, tile[20 * MosaicCanvas.TileSize + 20]);
        Assert.Equal(0, tile[220 * MosaicCanvas.TileSize + 20]);           // fuori dall'area acquisita: trasparente
    }

    [Fact]
    public void Canvas_supports_negative_coordinates()
    {
        var canvas = new MosaicCanvas();
        var img = new ArgbImage(100, 100, Enumerable.Repeat(ImageOps.Argb(10, 20, 30), 10000).ToArray());
        canvas.Compose(img, Homography.Translation(-300, -50), 1);
        Assert.Contains(canvas.Keys, k => k.X < 0 && k.Y < 0);
        Assert.InRange(canvas.Bounds.X, -301, -299);
    }

    [Fact]
    public void Coverage_grid_measures_overlap()
    {
        var grid = new CoverageGrid();
        var a = Homography.Identity.MapRect(320, 320);
        Assert.Equal(0, grid.CoveredFraction(a));
        grid.Mark(a);
        Assert.Equal(1, grid.CoveredFraction(a));
        var half = Homography.Translation(160, 0).MapRect(320, 320);
        Assert.InRange(grid.CoveredFraction(half), 0.45, 0.55);
    }

    [Fact]
    public void Sharpness_drops_on_blur()
    {
        var scene = new SyntheticScene(400, 300);
        var gray = ImageOps.ToGray(scene.Table, 400, out _);
        var small = ImageOps.Downscale(scene.Table, 100);
        var upscaled = new ArgbImage(400, 300);
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 400; x++)
            upscaled.Pixels[y * 400 + x] = ImageOps.SampleBilinear(small, x / 4.0, y / 4.0, 0);
        var blurred = ImageOps.ToGray(upscaled, 400, out _);
        Assert.True(ImageOps.Sharpness(gray) > 3 * ImageOps.Sharpness(blurred));
    }
}
