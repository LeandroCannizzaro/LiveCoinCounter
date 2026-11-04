using Android.Util;
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
        _analysis = new ImageAnalysis.Builder()
            .SetResolutionSelector(resolution)
            .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
            .SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888)
            .Build();
        _analysis.SetAnalyzer(_executor, this);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _provider.UnbindAll();
            _provider.BindToLifecycle(owner, CameraSelector.DefaultBackCamera, _analysis);
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
