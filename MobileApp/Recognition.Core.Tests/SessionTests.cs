using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Sessions;
using Xunit;

namespace Recognition.Core.Tests;

public class CoinRegistryTests
{
    [Fact]
    public void Same_coin_seen_twice_is_one_track()
    {
        var registry = new CoinRegistry();
        var a = registry.Observe(new CoinObservation(new RectF(10, 10, 40, 40), new RectF(100, 100, 40, 40), 0.8f, 0.5f, 0));
        var b = registry.Observe(new CoinObservation(new RectF(50, 12, 41, 39), new RectF(104, 98, 41, 39), 0.9f, 0.9f, 1));

        Assert.True(a.IsNew);
        Assert.False(b.IsNew);
        Assert.True(b.IsBetter);
        Assert.Equal(1, registry.Count);
        Assert.Equal(2, b.Track.Observations);
        Assert.True(b.Track.IsStable);
        Assert.Equal(1, b.Track.BestKeyframe);
    }

    [Fact]
    public void Touching_coins_stay_separate()
    {
        var registry = new CoinRegistry();
        registry.Observe(new CoinObservation(default, new RectF(100, 100, 40, 40), 0.8f, 1, 0));
        var other = registry.Observe(new CoinObservation(default, new RectF(140, 100, 40, 40), 0.8f, 1, 0));
        Assert.True(other.IsNew);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void A_shifted_and_rotated_keyframe_is_aligned_before_matching()
    {
        // 8 monete note; il secondo fotogramma chiave le vede ruotate di 8° e spostate di (35, 25): oltre la soglia di abbinamento
        // diretto, ma tutte allineabili con una sola trasformazione. In più c'è una moneta davvero nuova, lontana.
        var registry = new CoinRegistry();
        var points = new List<(float X, float Y, int Cents)>();
        for (var i = 0; i < 8; i++) points.Add((100 + (i % 4) * 110, 100 + (i / 4) * 130, i % 2 == 0 ? 1 : 2));
        registry.ObserveKeyframe(points.Select(p => new CoinObservation(default, new RectF(p.X - 20, p.Y - 20, 40, 40), 0.9f, 1, 0, p.Cents, 0.9f)).ToList());
        Assert.Equal(8, registry.Count);

        const double angle = 8 * Math.PI / 180;
        (float X, float Y) Move(float x, float y)
        {
            double dx = x - 250, dy = y - 165;
            return ((float)(250 + dx * Math.Cos(angle) - dy * Math.Sin(angle) + 35), (float)(165 + dx * Math.Sin(angle) + dy * Math.Cos(angle) + 25));
        }
        var second = points.Select(p => { var m = Move(p.X, p.Y); return new CoinObservation(default, new RectF(m.X - 20, m.Y - 20, 40, 40), 0.9f, 1, 1, p.Cents, 0.9f); }).ToList();
        second.Add(new CoinObservation(default, new RectF(580, 380, 40, 40), 0.9f, 1, 1, 5, 0.9f));
        var updates = registry.ObserveKeyframe(second);

        Assert.Equal(9, registry.Count);
        Assert.Equal(8, updates.Count(u => !u.IsNew));
        Assert.True(updates[^1].IsNew);
    }

    [Fact]
    public void Two_coins_of_a_keyframe_never_share_a_track_when_aligning()
    {
        var registry = new CoinRegistry();
        var kf0 = new[] { (100f, 100f), (210f, 100f), (320f, 100f), (100f, 230f), (210f, 230f), (320f, 230f) };
        registry.ObserveKeyframe(kf0.Select(p => new CoinObservation(default, new RectF(p.Item1 - 20, p.Item2 - 20, 40, 40), 0.9f, 1, 0)).ToList());
        var updates = registry.ObserveKeyframe(kf0.Select(p => new CoinObservation(default, new RectF(p.Item1 - 18, p.Item2 - 17, 40, 40), 0.9f, 1, 1)).ToList());

        Assert.Equal(6, registry.Count);
        Assert.Equal(6, updates.Select(u => u.Track.Id).Distinct().Count());
    }

    [Fact]
    public void Two_boxes_on_the_same_coin_keep_only_the_most_confident()
    {
        // caso reale: stesso 2 cent rilevato due volte (0,68 e 0,55), centri a 0,21 diametri; a fianco una moneta vera che tocca
        var boxes = new[] { new RectF(100, 100, 50, 50), new RectF(110, 102, 52, 50), new RectF(152, 100, 50, 50) };
        var kept = DetectionFilter.SuppressSameCoin(boxes, new[] { 0.55f, 0.68f, 0.9f });
        Assert.Equal(new[] { 1, 2 }, kept);
    }

    [Fact]
    public void Coins_in_the_same_keyframe_are_never_merged_even_if_overlapping()
    {
        var registry = new CoinRegistry();
        registry.Observe(new CoinObservation(default, new RectF(100, 100, 40, 40), 0.8f, 1, 0));
        var onTop = registry.Observe(new CoinObservation(default, new RectF(112, 104, 40, 40), 0.8f, 1, 0));
        Assert.True(onTop.IsNew);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Distorted_mosaic_still_merges_the_same_coin_across_keyframes()
    {
        // lo stesso 5 cent in due fotogrammi: il mosaico lo mette a più di mezzo diametro di distanza (caso reale)
        var registry = new CoinRegistry();
        registry.Observe(new CoinObservation(default, new RectF(100, 100, 135, 135), 0.97f, 1, 1));
        var again = registry.Observe(new CoinObservation(default, new RectF(130, 150, 197, 197), 0.96f, 1, 2));
        Assert.False(again.IsNew);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Adjacent_coins_in_different_keyframes_stay_separate()
    {
        var registry = new CoinRegistry();
        registry.Observe(new CoinObservation(default, new RectF(100, 100, 40, 40), 0.8f, 1, 0));
        var neighbour = registry.Observe(new CoinObservation(default, new RectF(142, 100, 40, 40), 0.8f, 1, 1));
        Assert.True(neighbour.IsNew);
    }

    [Theory]
    [InlineData(0, 100, 40, 40, false)]       // tocca il bordo sinistro
    [InlineData(100, 100, 40, 40, true)]
    [InlineData(100, 100, 80, 30, false)]     // troppo allungato
    [InlineData(600, 100, 40, 40, false)]     // esce a destra (frame 640)
    public void Border_and_shape_filter(float x, float y, float w, float h, bool usable) =>
        Assert.Equal(usable, DetectionFilter.IsUsable(new RectF(x, y, w, h), 640, 480));
}

public class GridConfigTests
{
    [Theory]
    [InlineData(1, 5, 1, 5)]
    [InlineData(2, 5, 2, 5)]
    [InlineData(5, 5, 5, 5)]
    [InlineData(5, 10, 5, 10)]
    [InlineData(5, 0, 5, 0)]       // solo righe fini
    [InlineData(0, 10, 0, 10)]     // solo righe principali
    [InlineData(5, 2, 5, 5)]       // il principale non può essere più fine del fine
    [InlineData(-1, -3, 0, 0)]
    public void Grid_levels_are_normalized(int minor, int major, int expectedMinor, int expectedMajor)
    {
        var grid = new GridConfig(minor, major).Normalized();
        Assert.Equal((expectedMinor, expectedMajor), (grid.MinorCm, grid.MajorCm));
    }

    [Fact]
    public void Old_single_level_setting_becomes_two_levels()
    {
        Assert.Equal(new GridConfig(1, 5), GridConfig.FromLegacy("Cm1"));
        Assert.Equal(new GridConfig(0, 10), GridConfig.FromLegacy("Cm10"));
        Assert.True(GridConfig.FromLegacy("Off").IsOff);
        Assert.True(GridConfig.FromLegacy(null).IsOff);
    }
}

public class ColorCheckTests
{
    private static CoinClassification Read(int cents, float conf, params (int, float)[] topK) =>
        new(cents, conf, topK.Length == 0 ? new[] { (cents, conf) } : topK, "test");

    [Fact]
    public void A_gold_coin_read_as_copper_takes_the_compatible_second_choice()
    {
        // il caso reale: 10 centesimi (oro) letto come 2 centesimi con 0,87; il rilevatore dice 10
        var result = ColorCheck.Apply(Read(2, 0.87f, (2, 0.87f), (5, 0.03f)), "gold", extra: new[] { (10, 0.8f) });
        Assert.Equal(10, result.Cents);
        Assert.Contains("colore", result.Note);
    }

    [Fact]
    public void Without_a_compatible_alternative_confidence_drops()
    {
        var result = ColorCheck.Apply(Read(2, 0.87f), "gold");
        Assert.Equal(2, result.Cents);
        Assert.InRange(result.Confidence, 0.5f, 0.53f);
    }

    [Theory]
    [InlineData(10, "gold")]
    [InlineData(200, "gold")]     // il centro di 2 euro è color oro
    [InlineData(2, "copper")]
    [InlineData(2, "?")]
    [InlineData(2, null)]
    public void Consistent_or_unsure_colors_change_nothing(int cents, string? tint)
    {
        var read = Read(cents, 0.9f);
        Assert.Same(read, ColorCheck.Apply(read, tint));
    }
}

public class DiameterCheckTests
{
    private static DiameterCheck.Item Item(int id, float diameterPx, int cents, float conf, params (int, float)[] topK) =>
        new(id, diameterPx, new CoinClassification(cents, conf, topK.Length == 0 ? new[] { (cents, conf) } : topK, "test"));

    [Fact]
    public void Switches_to_second_choice_when_diameter_disagrees()
    {
        // 2 px/mm: 2€ = 51,5 px, 1ct = 32,5 px, 10ct = 39,5 px, 20ct = 44,5 px
        var items = new[]
        {
            Item(1, 51.5f, 200, 0.95f), Item(2, 32.5f, 1, 0.9f), Item(3, 39.5f, 10, 0.9f),
            Item(4, 44.5f, 10, 0.6f, (10, 0.6f), (20, 0.35f))     // misurata come 20ct ma classificata 10ct
        };

        var result = DiameterCheck.Apply(items);

        Assert.Equal(20, result.Single(i => i.CoinId == 4).Classification.Cents);
        Assert.Equal(200, result.Single(i => i.CoinId == 1).Classification.Cents);
    }

    [Fact]
    public void Diameter_never_brings_back_a_value_of_the_wrong_colour()
    {
        // caso reale: ritaglio di rame letto 50 cent, corretto dal colore a 1 cent; il diametro (gonfiato dalla prospettiva) tornava a 50 cent
        var classification = new CoinClassification(1, 0.6f, new[] { (50, 0.89f), (1, 0.6f) }, "test");
        var withoutColour = new DiameterCheck.Item(1, 236f, classification);
        var copper = new DiameterCheck.Item(1, 236f, classification, Tint: "copper");

        Assert.Equal(50, DiameterCheck.Apply(new[] { withoutColour }, 10.0).Single().Classification.Cents);
        var result = DiameterCheck.Apply(new[] { copper }, 10.0).Single().Classification;
        Assert.Equal(1, result.Cents);
        Assert.True(result.Confidence < 0.6f);
    }

    [Fact]
    public void Lowers_confidence_without_plausible_alternative()
    {
        var items = new[] { Item(1, 51.5f, 200, 0.95f), Item(2, 32.5f, 1, 0.9f), Item(3, 39.5f, 10, 0.9f), Item(4, 44.5f, 1, 0.8f) };
        var c = DiameterCheck.Apply(items).Single(i => i.CoinId == 4).Classification;
        Assert.Equal(1, c.Cents);
        Assert.True(c.Confidence < 0.55f);
        Assert.NotNull(c.Note);
    }

    [Fact]
    public void Uses_the_scale_of_nearby_coins_when_the_phone_was_tilted()
    {
        static DiameterCheck.Item At(int id, float x, float diameterPx, int cents) =>
            new(id, diameterPx, new CoinClassification(cents, 0.95f, new[] { (cents, 0.95f) }, "test"), x, 0);

        // lato sinistro 2 px/mm, lato destro 3 px/mm (il telefono vedeva i due lati a distanze diverse): tutte giuste
        var items = new[]
        {
            At(1, 0, 32.5f, 1), At(2, 90, 37.5f, 2), At(3, 180, 42.5f, 5),
            At(4, 2000, 48.75f, 1), At(5, 2100, 77.25f, 200), At(6, 2200, 59.25f, 10)
        };

        var result = DiameterCheck.Apply(items);

        Assert.All(result, i => Assert.True(i.Classification.Confidence >= 0.9f, $"moneta {i.CoinId}: {i.Classification.Note}"));
    }

    [Fact]
    public void Still_flags_a_coin_that_does_not_fit_its_neighbours()
    {
        static DiameterCheck.Item At(int id, float x, float diameterPx, int cents) =>
            new(id, diameterPx, new CoinClassification(cents, 0.95f, new[] { (cents, 0.95f) }, "test"), x, 0);

        var items = new[] { At(1, 0, 32.5f, 1), At(2, 90, 37.5f, 2), At(3, 180, 42.5f, 5), At(4, 270, 64.5f, 1) };   // l'ultima è grande 2€, non 1 cent
        Assert.True(DiameterCheck.Apply(items).Single(i => i.CoinId == 4).Classification.Confidence < 0.6f);
    }

    [Fact]
    public void Needs_three_confident_coins_without_known_scale()
    {
        var items = new[] { Item(1, 51.5f, 200, 0.95f), Item(2, 44.5f, 1, 0.8f) };
        Assert.Same(items, DiameterCheck.Apply(items));
    }
}

public class RepositoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"coins-{Guid.NewGuid():N}.db3");
    private SessionRepository _repo = null!;

    public Task InitializeAsync()
    {
        _repo = new SessionRepository(_path);
        return _repo.InitAsync();
    }

    public async Task DisposeAsync()
    {
        await _repo.DisposeAsync();
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public async Task Session_lifecycle_with_coins_totals_and_corrections()
    {
        var s = await _repo.CreateSessionAsync(SessionSource.Live, "features", "offline-model", RetentionMode.MosaicAndCrops, new DateTime(2026, 11, 4, 10, 0, 0));
        await _repo.UpsertCoinsAsync(s.Id, new[]
        {
            new CoinRecord { Number = 1, Width = 40, Height = 40, State = CoinState.Classified, Cents = 200, ClassConfidence = 0.9f },
            new CoinRecord { Number = 2, Width = 30, Height = 30, State = CoinState.Classified, Cents = 10, ClassConfidence = 0.4f },
            new CoinRecord { Number = 3, Width = 30, Height = 30, State = CoinState.Detected }
        });
        // un aggiornamento della stessa moneta non la duplica
        await _repo.UpsertCoinsAsync(s.Id, new[] { new CoinRecord { Number = 3, Width = 31, Height = 31, State = CoinState.Rejected } });

        var totals = await _repo.RefreshTotalsAsync(s.Id);
        Assert.Equal(210, totals!.TotalCents);
        Assert.Equal(2, totals.CoinCount);
        Assert.Equal(1, totals.UncertainCount);

        var coins = await _repo.ListCoinsAsync(s.Id);
        Assert.Equal(3, coins.Count);
        var uncertain = coins.Single(c => c.Number == 2);
        uncertain.CorrectedCents = 20;
        uncertain.State = CoinState.Corrected;
        await _repo.UpdateCoinAsync(uncertain);

        totals = await _repo.RefreshTotalsAsync(s.Id);
        Assert.Equal(220, totals!.TotalCents);
        Assert.Equal(0, totals.UncertainCount);

        await _repo.RenameAsync(s.Id, "Tavolo cucina");
        Assert.Equal("Tavolo cucina", (await _repo.GetSessionAsync(s.Id))!.Name);

        await _repo.DeleteSessionAsync(s.Id);
        Assert.Null(await _repo.GetSessionAsync(s.Id));
        Assert.Empty(await _repo.ListCoinsAsync(s.Id));
    }

    [Fact]
    public async Task A_shot_session_sums_its_shots_and_deletes_them_with_it()
    {
        var parent = await _repo.EnsureShotSessionAsync("20261106-220000-000", "features", "offline-model", RetentionMode.MosaicAndCrops, new DateTime(2026, 11, 6, 22, 0, 0));
        Assert.Equal("features", (await _repo.EnsureShotSessionAsync(parent.Id, "x", "y", RetentionMode.MosaicAndCrops, DateTime.Now)).TrackerId);
        Assert.Equal(SessionSource.Shots, parent.Source);

        var shots = new List<SessionRecord>();
        for (var k = 1; k <= 2; k++)
        {
            var s = await _repo.CreateSessionAsync(SessionSource.Live, "features", "offline-model", RetentionMode.MosaicAndCrops, new DateTime(2026, 11, 6, 22, k, 0));
            s.SeriesId = parent.Id;
            s.ShotIndex = k;
            await _repo.UpdateSessionAsync(s);
            shots.Add(s);
        }
        // le monete di ogni scatto hanno i numeri di quello scatto: 1, 2, ...
        await _repo.UpsertCoinsAsync(shots[1].Id, new[] { new CoinRecord { Number = 1, State = CoinState.Classified, Cents = 50, ClassConfidence = 0.9f } });
        await _repo.UpsertCoinsAsync(shots[0].Id, new[]
        {
            new CoinRecord { Number = 1, State = CoinState.Classified, Cents = 200, ClassConfidence = 0.9f },
            new CoinRecord { Number = 2, State = CoinState.Classified, Cents = 10, ClassConfidence = 0.9f }
        });
        await _repo.RefreshTotalsAsync(shots[0].Id);
        await _repo.RefreshTotalsAsync(shots[1].Id);

        var total = (await _repo.GetSessionAsync(parent.Id))!;
        Assert.Equal(260, total.TotalCents);
        Assert.Equal(3, total.CoinCount);
        Assert.Equal(new[] { shots[0].Id, shots[1].Id }, (await _repo.ListShotsAsync(parent.Id)).Select(s => s.Id));

        // le monete della sessione a scatti: scatto per scatto
        var coins = await _repo.ListCoinsAsync(parent.Id);
        Assert.Equal(new[] { 200, 10, 50 }, coins.Select(c => c.Cents!.Value));
        Assert.Equal(2, (await _repo.ListCoinsAsync(shots[0].Id)).Count);

        await _repo.DeleteSessionAsync(parent.Id);
        Assert.Null(await _repo.GetSessionAsync(shots[0].Id));
        Assert.Null(await _repo.GetSessionAsync(shots[1].Id));
        Assert.Empty(await _repo.ListCoinsAsync(shots[0].Id));
    }

    [Fact]
    public async Task Interrupted_sessions_are_listed()
    {
        var a = await _repo.CreateSessionAsync(SessionSource.Live, "t", "l", RetentionMode.MosaicAndCrops, new DateTime(2026, 1, 1));
        var b = await _repo.CreateSessionAsync(SessionSource.Photo, "t", "l", RetentionMode.MosaicAndCrops, new DateTime(2026, 1, 2));
        b.Status = SessionStatus.Completed;
        await _repo.UpdateSessionAsync(b);

        var interrupted = await _repo.ListInterruptedAsync();
        Assert.Equal(a.Id, Assert.Single(interrupted).Id);
    }

    [Fact]
    public void Homography_and_topk_round_trip_as_text()
    {
        var h = new Homography(1.5, 0.1, 3, 0, 2, -4.25, 1e-4, 0, 1);
        Assert.Equal(h.ToArray(), SessionRepository.ParseHomography(SessionRepository.FormatHomography(h)).ToArray());
        var topK = new List<(int, float)> { (200, 0.8f), (100, 0.15f) };
        Assert.Equal(topK, SessionRepository.ParseTopK(SessionRepository.FormatTopK(topK)));
    }

    [Fact]
    public async Task Classifier_fills_values_and_uses_detector_as_second_opinion()
    {
        var s = await _repo.CreateSessionAsync(SessionSource.Photo, "t", "offline-model", RetentionMode.MosaicAndCrops, DateTime.Now);
        await _repo.UpsertCoinsAsync(s.Id, new[]
        {
            new CoinRecord { Number = 1, Width = 50, Height = 50, State = CoinState.Detected, CropPath = "a" },
            new CoinRecord { Number = 2, Width = 40, Height = 40, State = CoinState.Detected, CropPath = "b", DetectorCents = 50, DetectorClassConfidence = 0.95f }
        });

        var classifier = new FixedClassifier(new Dictionary<int, CoinClassification>
        {
            [1] = new(200, 0.8f, new[] { (200, 0.8f) }, "fixed"),
            [2] = new(20, 0.5f, new[] { (20, 0.5f) }, "fixed")
        });
        var sc = new SessionClassifier(_repo, c => Task.FromResult<byte[]?>(new byte[] { 1 }));

        var count = await sc.ClassifyAsync(s.Id, classifier);

        Assert.Equal(2, count);
        var coins = await _repo.ListCoinsAsync(s.Id);
        Assert.Equal(200, coins[0].Cents);
        Assert.Equal(50, coins[1].Cents);         // il rilevatore era più sicuro
        var session = await _repo.GetSessionAsync(s.Id);
        Assert.Equal(SessionStatus.Completed, session!.Status);
        Assert.Equal(250, session.TotalCents);
        Assert.Equal(2, (await _repo.ListClassificationsAsync(coins[1].Id)).Count);
    }

    private sealed class FixedClassifier : ICoinClassifier
    {
        private readonly Dictionary<int, CoinClassification> _results;
        public FixedClassifier(Dictionary<int, CoinClassification> results) => _results = results;
        public string Id => "fixed";
        public bool RequiresNetwork => false;
        public Task<CoinClassification?> ClassifyAsync(CoinCrop crop, CancellationToken ct = default) =>
            Task.FromResult<CoinClassification?>(_results[crop.CoinId]);
    }
}
