package com.livecoincounter.cv;

import org.opencv.core.CvType;
import org.opencv.core.Mat;
import org.opencv.core.MatOfKeyPoint;
import org.opencv.features2d.ORB;

/**
 * Ponte minimo verso OpenCV: punti ORB e descrittori da un'immagine in grigio, con array semplici.
 * Una sola chiamata JNI per fotogramma invece di decine con il binding completo di OpenCV.
 * Non è thread-safe: ogni tracker ha il suo.
 */
public final class OrbHelper {
    private static Boolean loaded;

    private final ORB orb;
    private float[] points = new float[0];
    private byte[] descriptors = new byte[0];

    /** Carica la libreria nativa di OpenCV; false se non è disponibile (si usa il ripiego in C#). */
    public static synchronized boolean init() {
        if (loaded == null) {
            try {
                System.loadLibrary("opencv_java4");
                loaded = Boolean.TRUE;
            } catch (Throwable t) {
                loaded = Boolean.FALSE;
            }
        }
        return loaded;
    }

    public OrbHelper(int maxFeatures) {
        orb = ORB.create(maxFeatures, 1.2f, 4, 31, 0, 2, ORB.HARRIS_SCORE, 31, 15);
    }

    /** Elabora l'immagine e restituisce il numero di punti; poi si leggono getPoints e getDescriptors. */
    public int detect(byte[] gray, int width, int height, int maxFeatures) {
        orb.setMaxFeatures(maxFeatures);
        Mat image = new Mat(height, width, CvType.CV_8UC1);
        Mat mask = new Mat();
        MatOfKeyPoint keypoints = new MatOfKeyPoint();
        Mat desc = new Mat();
        try {
            image.put(0, 0, gray);
            orb.detectAndCompute(image, mask, keypoints, desc);
            float[] kp = new float[(int) keypoints.total() * 7];
            if (kp.length > 0) keypoints.get(0, 0, kp);
            int n = Math.min((int) keypoints.total(), desc.rows());
            points = new float[n * 2];
            for (int i = 0; i < n; i++) {
                points[i * 2] = kp[i * 7];
                points[i * 2 + 1] = kp[i * 7 + 1];
            }
            descriptors = new byte[n * 32];
            if (n > 0) desc.get(0, 0, descriptors);
            return n;
        } finally {
            image.release();
            mask.release();
            keypoints.release();
            desc.release();
        }
    }

    /** Coordinate x, y alternate. */
    public float[] getPoints() {
        return points;
    }

    /** 32 byte per punto. */
    public byte[] getDescriptors() {
        return descriptors;
    }
}
