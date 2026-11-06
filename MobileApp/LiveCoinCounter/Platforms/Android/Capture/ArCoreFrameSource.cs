using Android.Content;
using Android.Hardware.Camera2;
using Android.Opengl;
using Com.Google.AR.Core;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services.Sessions;
using ArPose = Com.Google.AR.Core.Pose;
using Pose = LiveCoinCounter.Mosaic.Pose;
using CameraIntrinsics = LiveCoinCounter.Mosaic.CameraIntrinsics;

namespace LiveCoinCounter.Platforms.Android.Capture;

/// <summary>
/// Fotogrammi e posa da ARCore. ARCore vuole una texture OpenGL per la fotocamera anche se non si disegna nulla:
/// si crea un contesto EGL fuori schermo su un thread dedicato, dove gira il ciclo <c>Session.Update()</c>.
/// Si usa l'immagine CPU (orientamento del sensore) con la posa della fotocamera fisica e i suoi intrinseci.
/// Il primo piano orizzontale abbastanza grande viene agganciato e la sua posa resta fissa per tutta la sessione.
/// </summary>
public sealed class ArCoreFrameSource : IFrameSource
{
    private const int GlTextureExternalOes = 0x8D65;
    private const float MinPlaneArea = 0.02f;   // m²

    private Thread? _thread;
    private volatile bool _running;
    private Action<CameraFrame>? _onFrame;
    private Func<bool>? _wantsFrame;
    private TaskCompletionSource? _started;
    private Pose? _lockedPlane;

    public string Id => "arcore";

    /// <summary>
    /// Un errore nel ciclo di ARCore (dove: "plane", "frame", "loop"). Prima il ciclo si fermava al primo errore senza dire nulla:
    /// nelle prove con il metodo Ibrido arrivavano 4 fotogrammi e poi più niente.
    /// </summary>
    public Action<string, Exception>? Failed { get; set; }

    private const int MaxConsecutiveErrors = 30;

    /// <summary>ARCore è supportato su questo telefono (anche se non ancora installato)?</summary>
    public static bool IsSupported(Context context)
    {
        try
        {
            var availability = ArCoreApk.Instance.CheckAvailability(context);
            return availability.IsSupported || availability.IsTransient;
        }
        catch
        {
            return false;
        }
    }

    public async Task StartAsync(Action<CameraFrame> onFrame, Func<bool> wantsFrame)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Nessuna activity attiva.");
        var status = await MainThread.InvokeOnMainThreadAsync(() => ArCoreApk.Instance.RequestInstall(activity, true));
        if (status == ArCoreApk.InstallStatus.InstallRequested)
            throw new InvalidOperationException("Installa o aggiorna \"Google Play Services per AR\", poi riprova.");

        _onFrame = onFrame;
        _wantsFrame = wantsFrame;
        _lockedPlane = null;
        _running = true;
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(activity)) { IsBackground = true, Name = "ARCore" };
        _thread.Start();
        await _started.Task;
    }

    public Task StopAsync()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));
        _onFrame = null;
        return Task.CompletedTask;
    }

    private void Run(Context context)
    {
        EGLDisplay? display = null;
        EGLContext? eglContext = null;
        EGLSurface? surface = null;
        Session? session = null;
        try
        {
            (display, eglContext, surface) = CreateOffscreenContext();
            var textures = new int[1];
            GLES20.GlGenTextures(1, textures, 0);
            GLES20.GlBindTexture(GlTextureExternalOes, textures[0]);

            session = new Session(context);
            var config = new Config(session);
            config.SetPlaneFindingMode(Config.PlaneFindingMode.Horizontal);
            config.SetFocusMode(Config.FocusMode.Auto);
            config.SetUpdateMode(Config.UpdateMode.Blocking);
            session.Configure(config);
            ChooseLargestCpuImage(session);
            session.SetCameraTextureName(textures[0]);
            session.Resume();

            var rotation = SensorOrientation(context, session.CameraConfig.CameraId);
            _started?.TrySetResult();

            var errors = 0;
            var planeFailed = false;
            while (_running)
            {
                try
                {
                    var frame = session.Update();
                    var camera = frame.Camera;
                    var tracking = camera.TrackingState == TrackingState.Tracking;
                    if (tracking && _lockedPlane is null && !planeFailed)
                    {
                        // la ricerca del piano non deve fermare i fotogrammi: se fallisce si prosegue senza piano (e lo si dice)
                        try { _lockedPlane = FindTablePlane(session); }
                        catch (Exception ex) { planeFailed = true; Failed?.Invoke("plane", ex); }
                    }
                    if (_wantsFrame?.Invoke() != true) continue;

                    global::Android.Media.Image? image;
                    try
                    {
                        image = frame.AcquireCameraImage();
                    }
                    catch (Com.Google.AR.Core.Exceptions.NotYetAvailableException)
                    {
                        continue;
                    }

                    // Close() va chiamato esplicitamente: Dispose() di .NET rilascia solo il riferimento JNI, l'immagine resta aperta e
                    // dopo 4 immagini ARCore risponde ResourceExhaustedException (succedeva in tutte le prove con Ibrido e ARCore)
                    using var imageRef = image;
                    try
                    {
                        var argb = ToArgb(image);
                        var intrinsics = camera.ImageIntrinsics!;
                        var focal = intrinsics.GetFocalLength()!;
                        var principal = intrinsics.GetPrincipalPoint()!;
                        var dims = intrinsics.GetImageDimensions()!;
                        // se l'immagine CPU ha una risoluzione diversa da quella degli intrinseci si riscalano
                        var sx = argb.Width / (double)dims[0];
                        var sy = argb.Height / (double)dims[1];
                        var k = new CameraIntrinsics(focal[0] * sx, focal[1] * sy, principal[0] * sx, principal[1] * sy, argb.Width, argb.Height);
                        var pose = new PlanarPose(k, Convert(camera.Pose!), _lockedPlane, tracking);
                        _onFrame?.Invoke(new CameraFrame(argb, frame.Timestamp, rotation, pose));
                    }
                    finally
                    {
                        image.Close();
                    }
                    errors = 0;
                }
                catch (Exception ex)
                {
                    // un fotogramma andato male non ferma tutto; tanti di fila sì
                    if (errors++ == 0) Failed?.Invoke("frame", ex);
                    if (errors >= MaxConsecutiveErrors) throw;
                    Thread.Sleep(20);
                }
            }
        }
        catch (Exception ex)
        {
            if (_started is { Task.IsCompleted: false }) _started.TrySetException(ex);
            else Failed?.Invoke("loop", ex);
        }
        finally
        {
            try { session?.Pause(); session?.Close(); } catch { }
            if (display is not null)
            {
                EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                if (surface is not null) EGL14.EglDestroySurface(display, surface);
                if (eglContext is not null) EGL14.EglDestroyContext(display, eglContext);
                EGL14.EglTerminate(display);
            }
        }
    }

    /// <summary>Il piano orizzontale rivolto verso l'alto più grande tra quelli trovati, se abbastanza esteso.</summary>
    private static Pose? FindTablePlane(Session session)
    {
        Plane? best = null;
        var bestArea = MinPlaneArea;
        foreach (var item in session.GetAllTrackables(Java.Lang.Class.FromType(typeof(Plane))))
        {
            if (item is not Plane plane || plane.TrackingState != TrackingState.Tracking || plane.SubsumedBy is not null) continue;
            if (plane.GetType() != Plane.Type.HorizontalUpwardFacing) continue;
            var area = plane.ExtentX * plane.ExtentZ;
            if (area > bestArea) { best = plane; bestArea = area; }
        }
        return best?.CenterPose is { } p ? Convert(p) : null;
    }

    private static Pose Convert(ArPose p) => new(p.Tx(), p.Ty(), p.Tz(), p.Qx(), p.Qy(), p.Qz(), p.Qw());

    private static ArgbImage ToArgb(global::Android.Media.Image image)
    {
        var planes = image.GetPlanes()!;
        static byte[] Bytes(global::Android.Media.Image.Plane plane)
        {
            var buffer = plane.Buffer!;
            var bytes = new byte[buffer.Remaining()];
            buffer.Get(bytes);
            return bytes;
        }
        var y = Bytes(planes[0]);
        var u = Bytes(planes[1]);
        var v = Bytes(planes[2]);
        return AndroidImaging.YuvToArgb(y, planes[0].RowStride, u, v, planes[1].RowStride, planes[1].PixelStride, image.Width, image.Height);
    }

    /// <summary>Configurazione con l'immagine CPU più grande (di default ARCore dà 640×480): ritagli più nitidi.</summary>
    private static void ChooseLargestCpuImage(Session session)
    {
        try
        {
            var filter = new CameraConfigFilter(session);
            var configs = session.GetSupportedCameraConfigs(filter);
            var best = configs.Where(c => c.ImageSize.Width <= 1920)
                .OrderByDescending(c => c.ImageSize.Width * c.ImageSize.Height).FirstOrDefault();
            if (best is not null) session.CameraConfig = best;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Configurazione ARCore predefinita: {ex.Message}");
        }
    }

    private static int SensorOrientation(Context context, string cameraId)
    {
        try
        {
            var manager = (CameraManager)context.GetSystemService(Context.CameraService)!;
            var value = manager.GetCameraCharacteristics(cameraId).Get(CameraCharacteristics.SensorOrientation!);
            return value is Java.Lang.Integer i ? i.IntValue() : 90;
        }
        catch
        {
            return 90;
        }
    }

    private static (EGLDisplay, EGLContext, EGLSurface) CreateOffscreenContext()
    {
        var display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay)!;
        var version = new int[2];
        if (!EGL14.EglInitialize(display, version, 0, version, 1)) throw new InvalidOperationException("EGL non inizializzato.");

        int[] attributes =
        {
            EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
            EGL14.EglSurfaceType, EGL14.EglPbufferBit,
            EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8,
            EGL14.EglNone
        };
        var configs = new EGLConfig[1];
        var count = new int[1];
        if (!EGL14.EglChooseConfig(display, attributes, 0, configs, 0, 1, count, 0) || count[0] == 0)
            throw new InvalidOperationException("Nessuna configurazione EGL.");

        var context = EGL14.EglCreateContext(display, configs[0], EGL14.EglNoContext, new[] { EGL14.EglContextClientVersion, 2, EGL14.EglNone }, 0)!;
        var surface = EGL14.EglCreatePbufferSurface(display, configs[0], new[] { EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone }, 0)!;
        if (!EGL14.EglMakeCurrent(display, surface, surface, context)) throw new InvalidOperationException("Contesto EGL non attivabile.");
        return (display, context, surface);
    }
}
