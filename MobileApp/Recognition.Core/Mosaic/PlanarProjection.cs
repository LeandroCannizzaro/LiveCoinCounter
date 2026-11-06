namespace LiveCoinCounter.Mosaic;

/// <summary>Posa rigida (mondo ← oggetto): traslazione in metri e rotazione come quaternione, convenzioni ARCore.</summary>
public readonly record struct Pose(double Tx, double Ty, double Tz, double Qx, double Qy, double Qz, double Qw)
{
    public static Pose Identity => new(0, 0, 0, 0, 0, 0, 1);

    /// <summary>Matrice di rotazione 3x3 (righe).</summary>
    public double[] Rotation()
    {
        var n = Math.Sqrt(Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw);
        double x = Qx / n, y = Qy / n, z = Qz / n, w = Qw / n;
        return new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)
        };
    }

    /// <summary>Quaternione di una rotazione di <paramref name="degrees"/> attorno all'asse (ax, ay, az).</summary>
    public static Pose FromAxisAngle(double ax, double ay, double az, double degrees, double tx = 0, double ty = 0, double tz = 0)
    {
        var n = Math.Sqrt(ax * ax + ay * ay + az * az);
        var half = degrees * Math.PI / 360;
        var s = Math.Sin(half) / n;
        return new Pose(tx, ty, tz, ax * s, ay * s, az * s, Math.Cos(half));
    }
}

/// <summary>Parametri intrinseci della fotocamera per l'immagine CPU (pixel).</summary>
public readonly record struct CameraIntrinsics(double Fx, double Fy, double Cx, double Cy, int Width, int Height);

/// <summary>Posa di un fotogramma ARCore: fotocamera, eventuale piano del tavolo agganciato, stato del tracking.</summary>
public sealed record PlanarPose(CameraIntrinsics Intrinsics, Pose Camera, Pose? Plane, bool CameraTracking);

/// <summary>
/// Geometria fotocamera → piano. Convenzioni ARCore: la fotocamera guarda lungo −Z con Y in alto (le righe dell'immagine
/// crescono verso il basso), il piano ha la normale lungo Y e si estende su X e Z. Le coordinate del piano usate qui sono
/// (X, Z) in metri: visto dall'alto X va a destra e Z verso il basso, quindi l'immagine non risulta specchiata.
/// </summary>
public static class PlanarProjection
{
    /// <summary>Omografia piano (X, Z in metri) → pixel dell'immagine.</summary>
    public static Homography PlaneToImage(CameraIntrinsics k, Pose camera, Pose plane)
    {
        var rc = camera.Rotation();
        var rp = plane.Rotation();

        // assi del piano e origine espressi nel sistema della fotocamera: Rc^T * (Rp * e) e Rc^T * (tp − tc)
        var axisX = MulT(rc, Mul(rp, 1, 0, 0));
        var axisZ = MulT(rc, Mul(rp, 0, 0, 1));
        var origin = MulT(rc, (plane.Tx - camera.Tx, plane.Ty - camera.Ty, plane.Tz - camera.Tz));

        // proiezione: u = fx X/(−Z) + cx, v = cy − fy Y/(−Z)  →  [fx X − cx Z, −fy Y − cy Z, −Z]
        static (double, double, double) Project(CameraIntrinsics k, (double X, double Y, double Z) p) =>
            (k.Fx * p.X - k.Cx * p.Z, -k.Fy * p.Y - k.Cy * p.Z, -p.Z);

        var c1 = Project(k, axisX);
        var c2 = Project(k, axisZ);
        var c3 = Project(k, origin);
        return new Homography(c1.Item1, c2.Item1, c3.Item1, c1.Item2, c2.Item2, c3.Item2, c1.Item3, c2.Item3, c3.Item3).Normalized();
    }

    /// <summary>Omografia pixel dell'immagine → piano (metri); null se il piano non è davanti alla fotocamera.</summary>
    public static Homography? ImageToPlane(CameraIntrinsics k, Pose camera, Pose plane)
    {
        var rc = camera.Rotation();
        var rp = plane.Rotation();
        var origin = MulT(rc, (plane.Tx - camera.Tx, plane.Ty - camera.Ty, plane.Tz - camera.Tz));
        var normal = MulT(rc, Mul(rp, 0, 1, 0));

        // la fotocamera deve stare sopra il piano e guardarlo, non di taglio né verso l'alto:
        // asse ottico (0, 0, −1) contro la normale → normal.Z > 0,2 (meno di circa 78° dalla verticale)
        var dot = origin.X * normal.X + origin.Y * normal.Y + origin.Z * normal.Z;
        if (dot >= 0 || normal.Z < 0.2) return null;

        try
        {
            var h = PlaneToImage(k, camera, plane).Inverse();
            return h.IsFinite ? h : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rotazione del mosaico che fa apparire il primo fotogramma dritto come sullo schermo: la direzione "su" del
    /// fotogramma (dopo <paramref name="rotationDegrees"/>) diventa la direzione −y del mosaico.
    /// </summary>
    public static Homography Alignment(Homography frameToMosaic, int width, int height, int rotationDegrees)
    {
        // "su" sullo schermo, espresso nelle coordinate del fotogramma del sensore
        var (ux, uy) = rotationDegrees switch
        {
            90 => (-1.0, 0.0),
            180 => (0.0, 1.0),
            270 => (1.0, 0.0),
            _ => (0.0, -1.0)
        };
        double cx = width / 2.0, cy = height / 2.0, step = Math.Min(width, height) / 4.0;
        var (x0, y0) = frameToMosaic.Apply(cx, cy);
        var (x1, y1) = frameToMosaic.Apply(cx + ux * step, cy + uy * step);
        var angle = Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI;   // direzione attuale del "su"
        return Homography.Rotation(-90 - angle);                      // portarla a −90° (verso l'alto)
    }

    private static (double X, double Y, double Z) Mul(double[] r, double x, double y, double z) =>
        (r[0] * x + r[1] * y + r[2] * z, r[3] * x + r[4] * y + r[5] * z, r[6] * x + r[7] * y + r[8] * z);

    private static (double X, double Y, double Z) MulT(double[] r, (double X, double Y, double Z) v) =>
        (r[0] * v.X + r[3] * v.Y + r[6] * v.Z, r[1] * v.X + r[4] * v.Y + r[7] * v.Z, r[2] * v.X + r[5] * v.Y + r[8] * v.Z);
}
