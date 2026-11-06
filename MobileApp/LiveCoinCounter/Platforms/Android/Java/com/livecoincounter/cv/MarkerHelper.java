package com.livecoincounter.cv;

import java.util.ArrayList;
import java.util.List;
import org.opencv.core.CvType;
import org.opencv.core.Mat;
import org.opencv.objdetect.ArucoDetector;
import org.opencv.objdetect.DetectorParameters;
import org.opencv.objdetect.Dictionary;
import org.opencv.objdetect.Objdetect;

/**
 * Ponte minimo verso il rilevatore di marker ArUco di OpenCV (dizionario 4x4, 50 marker): da un'immagine in grigio restituisce
 * gli ID e gli angoli dei marker trovati, con array semplici (una sola chiamata JNI per fotogramma).
 * Non è thread-safe: ogni motore ha il suo.
 */
public final class MarkerHelper {
    private static Boolean loaded;

    private final ArucoDetector detector;
    private int[] ids = new int[0];
    private float[] corners = new float[0];

    /** Carica la libreria nativa di OpenCV (la stessa di OrbHelper); false se non è disponibile. */
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

    public MarkerHelper() {
        Dictionary dictionary = Objdetect.getPredefinedDictionary(Objdetect.DICT_4X4_50);
        detector = new ArucoDetector(dictionary, new DetectorParameters());
    }

    /** Cerca i marker e restituisce quanti ne ha trovati; poi si leggono getIds e getCorners. */
    public int detect(byte[] gray, int width, int height) {
        Mat image = new Mat(height, width, CvType.CV_8UC1);
        Mat idMat = new Mat();
        List<Mat> cornerList = new ArrayList<Mat>();
        try {
            image.put(0, 0, gray);
            detector.detectMarkers(image, cornerList, idMat);
            int n = cornerList.size();
            ids = new int[n];
            corners = new float[n * 8];
            if (n == 0) return 0;
            int[] rawIds = new int[(int) idMat.total()];
            idMat.get(0, 0, rawIds);
            for (int i = 0; i < n; i++) {
                ids[i] = rawIds[i];
                float[] c = new float[8];
                cornerList.get(i).get(0, 0, c);
                System.arraycopy(c, 0, corners, i * 8, 8);
            }
            return n;
        } finally {
            image.release();
            idMat.release();
            for (Mat m : cornerList) m.release();
        }
    }

    /** Un ID per marker. */
    public int[] getIds() {
        return ids;
    }

    /** Otto numeri per marker: x, y di alto-sinistra, alto-destra, basso-destra, basso-sinistra. */
    public float[] getCorners() {
        return corners;
    }
}
