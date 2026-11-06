using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Sessions;
using Xunit;

namespace Recognition.Core.Tests;

public class MarkerRegistryTests
{
    private static MarkerDetection Square(int id, double x, double y, double side) =>
        new(id, new Quad(new PointD(x, y), new PointD(x + side, y), new PointD(x + side, y + side), new PointD(x, y + side)));

    [Fact]
    public void Scale_is_side_in_mosaic_pixels_over_20_mm()
    {
        var registry = new MarkerRegistry();
        // 100 px di lato nel mosaico = 20 mm: 5 px per mm
        Assert.True(registry.Observe(Square(3, 400, 300, 100), Homography.Identity));
        var landmark = Assert.Single(registry.Landmarks);
        Assert.Equal(3, landmark.Id);
        Assert.Equal(5, landmark.PxPerMm, 3);
        Assert.Equal(450, landmark.X, 3);
        Assert.Equal(350, landmark.Y, 3);
    }

    [Fact]
    public void Frame_to_mosaic_homography_is_applied_to_the_corners()
    {
        // il fotogramma è ridotto a metà nel mosaico: un marker di 120 px nel fotogramma ne fa 60 → 3 px/mm
        var registry = new MarkerRegistry();
        registry.Observe(Square(1, 200, 200, 120), Homography.Scale(0.5));
        Assert.Equal(3, Assert.Single(registry.Landmarks).PxPerMm, 3);
    }

    [Fact]
    public void Image_scale_converts_detection_pixels_to_frame_pixels()
    {
        // rilevato su un'immagine ridotta a metà (scala 0.5): le coordinate vanno raddoppiate
        var registry = new MarkerRegistry();
        registry.Observe(Square(1, 100, 100, 50), Homography.Identity, imageToFrameScale: 2);
        Assert.Equal(5, Assert.Single(registry.Landmarks).PxPerMm, 3);
    }

    [Fact]
    public void A_wildly_deformed_quad_is_rejected()
    {
        var registry = new MarkerRegistry();
        var skewed = new MarkerDetection(2, new Quad(new PointD(0, 0), new PointD(200, 0), new PointD(210, 30), new PointD(0, 30)));
        Assert.False(registry.Observe(skewed, Homography.Identity));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void An_outlier_sighting_does_not_move_the_scale()
    {
        var registry = new MarkerRegistry();
        for (var i = 0; i < 4; i++) registry.Observe(Square(5, 100, 100, 100), Homography.Identity);
        Assert.False(registry.Observe(Square(5, 100, 100, 180), Homography.Identity));       // +80%: sbagliato
        Assert.Equal(5, Assert.Single(registry.Landmarks).PxPerMm, 3);
    }

    [Fact]
    public void The_same_id_twice_in_a_frame_is_two_physical_markers()
    {
        var registry = new MarkerRegistry();
        // due marker con ID 41 nello stesso fotogramma, lontani: due marker fisici (il foglio stampato due volte)
        registry.ObserveFrame(new[] { Square(41, 100, 100, 70), Square(41, 600, 400, 70), Square(4, 300, 300, 70) }, Homography.Identity);
        Assert.Equal(3, registry.Count);

        // più tardi se ne vede uno solo, spostato di molto dal drift del mosaico: va al più vicino, non ne nasce un terzo
        registry.ObserveFrame(new[] { Square(41, 640, 440, 70) }, Homography.Identity);
        Assert.Equal(3, registry.Count);
        Assert.Equal(2, registry.Landmarks.Count(l => l.Id == 41));
        Assert.Equal(2, registry.Landmarks.Single(l => l.Id == 41 && l.X > 400).Observations);
    }

    [Fact]
    public void Ids_outside_the_printed_sheet_are_ignored()
    {
        var registry = new MarkerRegistry();
        Assert.False(registry.Observe(Square(48, 0, 0, 100), Homography.Identity));
        Assert.False(registry.Observe(Square(-1, 0, 0, 100), Homography.Identity));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Local_scale_follows_the_nearest_markers_and_stops_far_away()
    {
        var markers = new[] { new MarkerLandmark(1, 0, 0, 4, 5), new MarkerLandmark(2, 1000, 0, 6, 5) };
        Assert.Equal(4, MarkerScale.At(10, 0, markers)!.Value, 1);
        Assert.Equal(6, MarkerScale.At(990, 0, markers)!.Value, 1);
        Assert.Null(MarkerScale.At(0, 3000, markers));
        var middle = MarkerScale.At(500, 0, markers);        // entrambi a 500 px: 700 px di raggio li comprende
        Assert.NotNull(middle);
        Assert.InRange(middle!.Value, 4.5, 5.5);
    }

    [Fact]
    public void Markers_round_trip_through_the_session_json()
    {
        var landmarks = new[] { new MarkerLandmark(7, 12.5, -40, 4.25, 9) };
        var back = SessionMarkers.Parse(SessionMarkers.Serialize(landmarks));
        Assert.Equal(landmarks, back);
        Assert.Empty(SessionMarkers.Parse(null));
        Assert.Empty(SessionMarkers.Parse("non json"));
    }

    [Fact]
    public void Marker_scale_replaces_the_coin_estimate_in_the_surface_scale()
    {
        var scale = SurfaceScale.From(null, Array.Empty<(float, int, float)>(), new[] { new MarkerLandmark(1, 0, 0, 4, 3) });
        Assert.NotNull(scale);
        Assert.True(scale!.Measured);
        Assert.Equal(4, scale.PxPerMm);
        Assert.Contains("marker", scale.Label);
    }
}

public class DiameterCheckWithMarkerTests
{
    // un marker vicino: 4 px/mm → 1 cent = 65 px, 2 cent = 75 px, 5 cent = 85 px
    private static readonly MarkerLandmark[] Markers = { new(1, 0, 0, 4, 10) };

    private static DiameterCheck.Item Coin(int id, float diameterPx, int cents, float conf, params (int, float)[] topK) =>
        new(id, diameterPx, new CoinClassification(cents, conf, topK.Length == 0 ? new[] { (cents, conf) } : topK, "test"), 100, 100);

    [Fact]
    public void A_matching_size_raises_the_confidence()
    {
        var result = DiameterCheck.Apply(new[] { Coin(1, 65, 1, 0.6f) }, null, Markers).Single().Classification;
        Assert.Equal(1, result.Cents);
        Assert.True(result.Confidence > 0.7f);
        Assert.Contains("marker", result.Note);
    }

    [Fact]
    public void A_wrong_size_switches_to_the_second_choice_that_fits()
    {
        // letto 1 cent (65 px attesi) ma misura 85 px = 5 cent, che era il secondo candidato
        var result = DiameterCheck.Apply(new[] { Coin(1, 85, 1, 0.6f, (1, 0.6f), (5, 0.3f)) }, null, Markers).Single().Classification;
        Assert.Equal(5, result.Cents);
    }

    [Fact]
    public void Without_a_marker_nearby_the_old_rules_apply()
    {
        var far = Coin(1, 65, 1, 0.6f) with { X = 5000, Y = 5000 };
        var result = DiameterCheck.Apply(new[] { far }, null, Markers).Single();
        Assert.Equal(0.6f, result.Classification.Confidence);      // nessuna scala: invariata
    }
}
