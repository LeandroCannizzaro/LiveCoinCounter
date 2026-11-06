using LiveCoinCounter.Mosaic;
using Xunit;

namespace Recognition.Core.Tests;

public class GeometryTests
{
    private static void AssertClose(PointD expected, (double X, double Y) actual, double tolerance = 1e-6)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }

    [Fact]
    public void Composition_applies_right_then_left()
    {
        var h = Homography.Translation(10, 0) * Homography.Scale(2);
        AssertClose(new PointD(12, 4), h.Apply(1, 2));
    }

    [Fact]
    public void Inverse_round_trips()
    {
        var h = new Homography(1.1, 0.2, 30, -0.1, 0.9, 12, 0.0004, -0.0002, 1);
        var (x, y) = h.Apply(100, 50);
        AssertClose(new PointD(100, 50), h.Inverse().Apply(x, y), 1e-6);
    }

    [Fact]
    public void Rotation_by_90_turns_right_into_down()
    {
        // asse y verso il basso: +90° porta (1, 0) su (0, 1)
        AssertClose(new PointD(0, 1), Homography.Rotation(90).Apply(1, 0), 1e-9);
    }

    [Fact]
    public void Fit_recovers_perspective_from_four_points()
    {
        var truth = new Homography(0.9, 0.1, 20, -0.05, 1.05, -8, 0.0003, 0.0001, 1);
        var src = new[] { new PointD(0, 0), new PointD(640, 0), new PointD(640, 480), new PointD(0, 480), new PointD(320, 240) };
        var dst = src.Select(p => { var (x, y) = truth.Apply(p.X, p.Y); return new PointD(x, y); }).ToList();

        var fit = Homography.Fit(src, dst);

        Assert.NotNull(fit);
        AssertClose(new PointD(dst[2].X, dst[2].Y), fit!.Value.Apply(640, 480), 1e-4);
        AssertClose(new PointD(truth.Apply(100, 300).X, truth.Apply(100, 300).Y), fit.Value.Apply(100, 300), 1e-4);
    }

    [Fact]
    public void Ransac_ignores_outliers()
    {
        var truth = Homography.Translation(40, -15) * Homography.Rotation(10);
        var random = new Random(3);
        var matches = new List<PointMatch>();
        for (var i = 0; i < 80; i++)
        {
            double x = random.Next(640), y = random.Next(480);
            var (u, v) = truth.Apply(x, y);
            matches.Add(new PointMatch((float)x, (float)y, (float)(u + random.NextDouble() - 0.5), (float)(v + random.NextDouble() - 0.5)));
        }
        for (var i = 0; i < 60; i++)
            matches.Add(new PointMatch(random.Next(640), random.Next(480), random.Next(640), random.Next(480)));

        var result = HomographyRansac.Estimate(matches);

        Assert.NotNull(result);
        Assert.InRange(result!.Inliers, 75, 85);
        AssertClose(new PointD(truth.Apply(300, 200).X, truth.Apply(300, 200).Y), result.Homography.Apply(300, 200), 1.0);
    }

    [Fact]
    public void Quad_contains_and_convexity()
    {
        var q = Homography.Rotation(30).MapRect(100, 50);
        Assert.True(q.IsConvex);
        var c = q.Center;
        Assert.True(q.Contains(c.X, c.Y));
        Assert.False(q.Contains(c.X + 500, c.Y));
        Assert.InRange(q.Area, 4999, 5001);
    }
}
