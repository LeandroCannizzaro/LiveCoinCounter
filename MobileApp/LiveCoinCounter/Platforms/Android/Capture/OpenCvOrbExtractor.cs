using Android.Runtime;
using LiveCoinCounter.Mosaic;

namespace LiveCoinCounter.Platforms.Android.Capture;

/// <summary>
/// Punti ORB calcolati da OpenCV (libreria nativa da Maven) tramite la classe Java <c>com.livecoincounter.cv.OrbHelper</c>,
/// chiamata via JNI: una chiamata per fotogramma, con array semplici. Se OpenCV non si carica si usa <see cref="OrbLite"/>.
/// </summary>
public sealed class OpenCvOrbExtractor : IFeatureExtractor, IDisposable
{
    private const string ClassName = "com/livecoincounter/cv/OrbHelper";
    private static IntPtr _class;
    private static IntPtr _init, _ctor, _detect, _getPoints, _getDescriptors;

    private IntPtr _instance;

    private OpenCvOrbExtractor(IntPtr instance) => _instance = instance;

    public string Id => "opencv";

    /// <summary>Crea l'estrattore, o null se OpenCV non è disponibile su questo telefono.</summary>
    public static OpenCvOrbExtractor? TryCreate(int maxFeatures = 600)
    {
        try
        {
            if (_class == IntPtr.Zero)
            {
                // in .NET per Android FindClass restituisce già un riferimento globale
                _class = JNIEnv.FindClass(ClassName);
                _init = JNIEnv.GetStaticMethodID(_class, "init", "()Z");
                _ctor = JNIEnv.GetMethodID(_class, "<init>", "(I)V");
                _detect = JNIEnv.GetMethodID(_class, "detect", "([BIII)I");
                _getPoints = JNIEnv.GetMethodID(_class, "getPoints", "()[F");
                _getDescriptors = JNIEnv.GetMethodID(_class, "getDescriptors", "()[B");
            }

            if (!JNIEnv.CallStaticBooleanMethod(_class, _init)) return null;
            var obj = JNIEnv.NewObject(_class, _ctor, new JValue(maxFeatures));
            var global = JNIEnv.NewGlobalRef(obj);
            JNIEnv.DeleteLocalRef(obj);
            return new OpenCvOrbExtractor(global);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenCV non disponibile: {ex}");
            return null;
        }
    }

    public FeatureSet Extract(GrayImage image, int maxFeatures)
    {
        var array = JNIEnv.NewArray(image.Pixels);
        try
        {
            var n = JNIEnv.CallIntMethod(_instance, _detect,
                new JValue(array), new JValue(image.Width), new JValue(image.Height), new JValue(maxFeatures));
            if (n <= 0) return FeatureSet.Empty;

            var pointsRef = JNIEnv.CallObjectMethod(_instance, _getPoints);
            var descRef = JNIEnv.CallObjectMethod(_instance, _getDescriptors);
            var points = (float[])JNIEnv.GetArray(pointsRef, JniHandleOwnership.TransferLocalRef, typeof(float))!;
            var bytes = (byte[])JNIEnv.GetArray(descRef, JniHandleOwnership.TransferLocalRef, typeof(byte))!;

            var xs = new float[n];
            var ys = new float[n];
            var descriptors = new ulong[n * 4];
            for (var i = 0; i < n; i++)
            {
                xs[i] = points[i * 2];
                ys[i] = points[i * 2 + 1];
            }
            Buffer.BlockCopy(bytes, 0, descriptors, 0, n * 32);
            return new FeatureSet(xs, ys, descriptors);
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
