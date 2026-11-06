using Android.Content;
using Android.Hardware.Camera2;
using Android.Util;
using AndroidX.Camera.Camera2.InterOp;
using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Lifecycle;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using Java.Util.Concurrent;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services.Sessions;

namespace LiveCoinCounter.Platforms.Android.Capture;

/// <summary>
/// Fotogrammi continui dalla fotocamera posteriore con CameraX (ImageAnalysis, RGBA, 1280×720, solo l'ultimo fotogramma).
/// Se il motore è occupato il fotogramma si scarta subito, senza convertirlo.
/// </summary>
public sealed class CameraXFrameSource : Java.Lang.Object, IFrameSource, ImageAnalysis.IAnalyzer
{
    private ProcessCameraProvider? _provider;
    private ImageAnalysis? _analysis;
    private IExecutorService? _executor;
    private Action<CameraFrame>? _onFrame;
    private Func<bool>? _wantsFrame;
    private int[]? _buffer;
    private byte[]? _bytes;
    private ICamera? _camera;
    private bool _supportsTorch;

    public string Id => "camerax";

    public async Task StartAsync(Action<CameraFrame> onFrame, Func<bool> wantsFrame)
    {
        _onFrame = onFrame;
        _wantsFrame = wantsFrame;
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Nessuna activity attiva.");
        if (activity is not ILifecycleOwner owner) throw new InvalidOperationException("L'activity non gestisce il ciclo di vita.");

        var future = ProcessCameraProvider.GetInstance(activity);
        var done = new TaskCompletionSource();
        future.AddListener(new Java.Lang.Runnable(() => done.TrySetResult()), ContextCompat.GetMainExecutor(activity));
        await done.Task;
        _provider = (ProcessCameraProvider)future.Get()!;

        _executor = Executors.NewSingleThreadExecutor();
        var resolution = new ResolutionSelector.Builder()
            .SetResolutionStrategy(new ResolutionStrategy(new global::Android.Util.Size(1280, 720), ResolutionStrategy.FallbackRuleClosestHigherThenLower))
            .Build();
        var builder = new ImageAnalysis.Builder()
            .SetResolutionSelector(resolution)
            .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
            .SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888);
        TuneForSharpness(activity, builder);
        _analysis = builder.Build();
        _analysis.SetAnalyzer(_executor, this);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _provider.UnbindAll();
            _camera = _provider.BindToLifecycle(owner, CameraSelector.DefaultBackCamera, _analysis);
            _supportsTorch = _camera?.CameraInfo?.HasFlashUnit == true;
        });
    }

    /// <summary>
    /// Immagini più nitide per il mosaico: autofocus continuo "video" (più stabile, senza "caccia" del fuoco) e frame rate
    /// fisso a 30 (con poca luce la fotocamera abbassa i fotogrammi al secondo e allunga l'esposizione: l'immagine si sfoca).
    /// Se il telefono non lo permette si lasciano le impostazioni predefinite.
    /// </summary>
    private static void TuneForSharpness(Context context, ImageAnalysis.Builder builder)
    {
        try
        {
            var extender = new Camera2Interop.Extender(builder);
            extender.SetCaptureRequestOption(CaptureRequest.ControlAfMode!, Java.Lang.Integer.ValueOf((int)ControlAFMode.ContinuousVideo));
            if (PickFixedFpsRange(context) is { } range)
                extender.SetCaptureRequestOption(CaptureRequest.ControlAeTargetFpsRange!, range);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Impostazioni fotocamera predefinite: {ex.Message}");
        }
    }

    /// <summary>L'intervallo di frame rate con il limite inferiore più alto tra quelli che arrivano a 30 al massimo (idealmente 30–30).</summary>
    private static Java.Lang.Object? PickFixedFpsRange(Context context)
    {
        var manager = (CameraManager)context.GetSystemService(Context.CameraService)!;
        foreach (var id in manager.GetCameraIdList()!)
        {
            var characteristics = manager.GetCameraCharacteristics(id);
            if (characteristics.Get(CameraCharacteristics.LensFacing!) is not Java.Lang.Integer facing || facing.IntValue() != (int)LensFacing.Back) continue;
            var raw = characteristics.Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges!);
            if (raw is null) continue;
            var ranges = global::Android.Runtime.JNIEnv.GetArray<Java.Lang.Object>(raw.Handle);
            if (ranges is null) continue;

            Java.Lang.Object? best = null;
            var bestLower = -1;
            foreach (var item in ranges)
            {
                var range = item is null ? null : global::Android.Runtime.Extensions.JavaCast<global::Android.Util.Range>(item);
                if (range is null) continue;
                var lower = ((Java.Lang.Integer)range.Lower!).IntValue();
                var upper = ((Java.Lang.Integer)range.Upper!).IntValue();
                if (upper is < 24 or > 30 || lower <= bestLower) continue;
                best = range;
                bestLower = lower;
            }
            return best;
        }
        return null;
    }

    public bool SupportsTorch => _supportsTorch;

    public void SetTorch(bool on)
    {
        var control = _camera?.CameraControl;
        if (control is null || !_supportsTorch) return;
        MainThread.BeginInvokeOnMainThread(() => control.EnableTorch(on));
    }

    /// <summary>Rimette a fuoco (e riesponde) al centro: utile quando la nitidezza resta bassa.</summary>
    public void Refocus()
    {
        var control = _camera?.CameraControl;
        if (control is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var factory = new SurfaceOrientedMeteringPointFactory(1f, 1f);
                var point = factory.CreatePoint(0.5f, 0.5f);
                var action = new FocusMeteringAction.Builder(point, FocusMeteringAction.FlagAf | FocusMeteringAction.FlagAe)
                    .DisableAutoCancel()
                    .Build();
                control.StartFocusAndMetering(action);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Messa a fuoco non riuscita: {ex.Message}");
            }
        });
    }

    public void Analyze(IImageProxy image)
    {
        try
        {
            if (_wantsFrame?.Invoke() != true) return;

            var plane = image.GetPlanes()[0];
            var width = image.Width;
            var height = image.Height;
            var buffer = plane.Buffer;
            var length = buffer.Remaining();
            if (_bytes is null || _bytes.Length < length) _bytes = new byte[length];
            buffer.Get(_bytes, 0, length);

            // un nuovo array per fotogramma: il motore lo conserva se diventa fotogramma chiave
            _buffer = new int[width * height];
            AndroidImaging.RgbaToArgb(_bytes.AsSpan(0, length), plane.RowStride, width, height, _buffer);
            var frame = new CameraFrame(new ArgbImage(width, height, _buffer), image.ImageInfo.Timestamp, image.ImageInfo.RotationDegrees);
            _onFrame?.Invoke(frame);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Fotogramma scartato: {ex.Message}");
        }
        finally
        {
            image.Close();
        }
    }

    public async Task StopAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _analysis?.ClearAnalyzer();
            _provider?.UnbindAll();
        });
        _executor?.Shutdown();
        _onFrame = null;
    }
}
