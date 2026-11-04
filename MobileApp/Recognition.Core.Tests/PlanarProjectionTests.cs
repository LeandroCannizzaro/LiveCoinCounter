using LiveCoinCounter.Mosaic;
using Xunit;

namespace Recognition.Core.Tests;

public class PlanarProjectionTests
{
    private static readonly CameraIntrinsics K = new(500, 500, 320, 240, 640, 480);

    /// <summary>Fotocamera a 0,3 m sopra il piano, che guarda verso il basso (−Y del mondo).</summary>
    private static Pose LookingDown(double x = 0, double z = 0, double yaw = 0)
    {
        // ruota la fotocamera (che guarda −Z) di −90° attorno a X: ora guarda −Y; poi un'eventuale rotazione attorno a Y
        var pitch = Pose.FromAxisAngle(1, 0, 0, -90);
        var turn = Pose.FromAxisAngle(0, 1, 0, yaw);
        var q = Multiply(turn, pitch);
        return q with { Tx = x, Ty = 0.3, Tz = z };
    }

    private static Pose Multiply(Pose a, Pose b) => new(0, 0, 0,
        a.Qw * b.Qx + a.Qx * b.Qw + a.Qy * b.Qz - a.Qz * b.Qy,
        a.Qw * b.Qy - a.Qx * b.Qz + a.Qy * b.Qw + a.Qz * b.Qx,
        a.Qw * b.Qz + a.Qx * b.Qy - a.Qy * b.Qx + a.Qz * b.Qw,
        a.Qw * b.Qw - a.Qx * b.Qx - a.Qy * b.Qy - a.Qz * b.Qz);

    [Fact]
    public void Camera_looking_down_sees_plane_point_below_at_principal_point()
    {
        var h = PlanarProjection.ImageToPlane(K, LookingDown(0.1, 0.2), Pose.Identity);
        Assert.NotNull(h);
        var (x, z) = h!.Value.Apply(320, 240);
        Assert.InRange(x, 0.0999, 0.1001);
        Assert.InRange(z, 0.1999, 0.2001);
    }

    [Fact]
    public void Scale_matches_focal_length_and_height()
    {
        // a 0,3 m con f = 500 px, 1 pixel = 0,6 mm
        var h = PlanarProjection.ImageToPlane(K, LookingDown(), Pose.Identity)!.Value;
        var (x0, _) = h.Apply(320, 240);
        var (x1, _) = h.Apply(420, 240);
        Assert.InRange(Math.Abs(x1 - x0), 0.0599, 0.0601);
    }

    [Fact]
    public void Image_is_not_mirrored_on_the_plane()
    {
        var h = PlanarProjection.ImageToPlane(K, LookingDown(), Pose.Identity)!.Value;
        var quad = h.MapRect(640, 480);
        // orientamento dei vertici conservato: area firmata con lo stesso segno dell'immagine (y verso il basso)
        var signed = (quad.B.X - quad.A.X) * (quad.D.Y - quad.A.Y) - (quad.D.X - quad.A.X) * (quad.B.Y - quad.A.Y);
        Assert.True(signed > 0);
    }

    [Fact]
    public void Plane_behind_camera_is_rejected()
    {
        var upward = Pose.FromAxisAngle(1, 0, 0, 90, ty: 0.3);   // guarda verso l'alto
        Assert.Null(PlanarProjection.ImageToPlane(K, upward, Pose.Identity));
    }

    [Fact]
    public void ArCore_tracker_aligns_first_frame_upright_and_keeps_metric_scale()
    {
        var tracker = new ArCorePlaneTracker(pxPerMm: 2);
        var frame = new ArgbImage(640, 480);
        var pose = new PlanarPose(K, LookingDown(yaw: 37), Pose.Identity, true);

        var result = tracker.Process(new CameraFrame(frame, 0, 0, pose));

        Assert.Equal(TrackStatus.Tracking, result.Status);
        var h = result.FrameToMosaic!.Value;
        var (x0, y0) = h.Apply(320, 240);
        var (x1, y1) = h.Apply(320, 140);       // 100 px verso l'alto nell'immagine
        Assert.InRange(Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI, -90.5, -89.5);
        Assert.InRange(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), 119, 121);   // 60 mm × 2 px/mm
    }

    [Fact]
    public void ArCore_tracker_waits_for_plane()
    {
        var tracker = new ArCorePlaneTracker();
        var result = tracker.Process(new CameraFrame(new ArgbImage(64, 48), 0, 90, new PlanarPose(K, LookingDown(), null, true)));
        Assert.Equal(TrackStatus.Initializing, result.Status);
        Assert.Null(result.FrameToMosaic);
    }
}
