using Android.Runtime;
using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Platforms.Android.Capture;

/// <summary>
/// Marker di scala (ArUco 4x4) trovati da OpenCV tramite la classe Java <c>com.livecoincounter.cv.MarkerHelper</c>, chiamata via JNI:
/// una chiamata per immagine, con array semplici. Se OpenCV non si carica non c'è il rilevatore (la scala si stima dalle monete).
/// </summary>
public sealed class OpenCvMarkerDetector : IMarkerDetector, IDisposable
{
    private const string ClassName = "com/livecoincounter/cv/MarkerHelper";
    private static IntPtr _class;
    private static IntPtr _init, _ctor, _detect, _getIds, _getCorners;

    private IntPtr _instance;

    private OpenCvMarkerDetector(IntPtr instance) => _instance = instance;

    /// <summary>Crea il rilevatore, o null se OpenCV non è disponibile su questo telefono.</summary>
    public static OpenCvMarkerDetector? TryCreate()
    {
        try
        {
            if (_class == IntPtr.Zero)
            {
                _class = JNIEnv.FindClass(ClassName);
                _init = JNIEnv.GetStaticMethodID(_class, "init", "()Z");
                _ctor = JNIEnv.GetMethodID(_class, "<init>", "()V");
                _detect = JNIEnv.GetMethodID(_class, "detect", "([BII)I");
                _getIds = JNIEnv.GetMethodID(_class, "getIds", "()[I");
                _getCorners = JNIEnv.GetMethodID(_class, "getCorners", "()[F");
            }

            if (!JNIEnv.CallStaticBooleanMethod(_class, _init)) return null;
            var obj = JNIEnv.NewObject(_class, _ctor);
            var global = JNIEnv.NewGlobalRef(obj);
            JNIEnv.DeleteLocalRef(obj);
            return new OpenCvMarkerDetector(global);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Rilevatore di marker non disponibile: {ex}");
            return null;
        }
    }

    public IReadOnlyList<MarkerDetection> Detect(GrayImage image)
    {
        var array = JNIEnv.NewArray(image.Pixels);
        try
        {
            var n = JNIEnv.CallIntMethod(_instance, _detect, new JValue(array), new JValue(image.Width), new JValue(image.Height));
            if (n <= 0) return Array.Empty<MarkerDetection>();

            var idsRef = JNIEnv.CallObjectMethod(_instance, _getIds);
            var cornersRef = JNIEnv.CallObjectMethod(_instance, _getCorners);
            var ids = (int[])JNIEnv.GetArray(idsRef, JniHandleOwnership.TransferLocalRef, typeof(int))!;
            var c = (float[])JNIEnv.GetArray(cornersRef, JniHandleOwnership.TransferLocalRef, typeof(float))!;

            var result = new List<MarkerDetection>(n);
            for (var i = 0; i < n; i++)
            {
                var o = i * 8;
                result.Add(new MarkerDetection(ids[i], new Quad(
                    new PointD(c[o], c[o + 1]), new PointD(c[o + 2], c[o + 3]), new PointD(c[o + 4], c[o + 5]), new PointD(c[o + 6], c[o + 7]))));
            }
            return result;
        }
        finally
        {
            JNIEnv.DeleteLocalRef(array);
        }
    }

    public void Dispose()
    {
        if (_instance == IntPtr.Zero) return;
        JNIEnv.DeleteGlobalRef(_instance);
        _instance = IntPtr.Zero;
    }
}
