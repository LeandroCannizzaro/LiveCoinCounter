using LiveCoinCounter.ViewModels;

namespace LiveCoinCounter.Views;

/// <summary>La superficie acquisita a tutto schermo con zoom (pizzico), spostamento (trascinamento) e doppio tocco.</summary>
public partial class SurfacePage : ContentPage
{
    private const double MaxZoom = 12;
    private const int DoubleTapMs = 300;

    private readonly SurfaceViewModel _viewModel = new();
    private double _scale = 1, _startScale = 1;
    private double _offsetX, _offsetY;
    private CancellationTokenSource? _pendingTap;
    private long _lastTapTick;
    private Point _lastTapPoint;

    public SurfacePage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _startScale = _scale;
                Surface.AnchorX = 0;
                Surface.AnchorY = 0;
                break;

            case GestureStatus.Running:
                // ingrandisce attorno al punto tra le dita (lo schema dei gesti di pizzico di MAUI/Xamarin)
                _scale = Math.Clamp(_scale + (e.Scale - 1) * _startScale, 1, MaxZoom);
                var deltaX = (Surface.X + _offsetX) / Stage.Width;
                var deltaWidth = Stage.Width / (Surface.Width * _startScale);
                var originX = (e.ScaleOrigin.X - deltaX) * deltaWidth;
                var deltaY = (Surface.Y + _offsetY) / Stage.Height;
                var deltaHeight = Stage.Height / (Surface.Height * _startScale);
                var originY = (e.ScaleOrigin.Y - deltaY) * deltaHeight;

                Apply(_scale,
                    _offsetX - originX * Surface.Width * (_scale - _startScale),
                    _offsetY - originY * Surface.Height * (_scale - _startScale));
                break;

            case GestureStatus.Completed:
                _offsetX = Surface.TranslationX;
                _offsetY = Surface.TranslationY;
                break;
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_scale <= 1) return;
        switch (e.StatusType)
        {
            case GestureStatus.Running:
                Apply(_scale, _offsetX + e.TotalX, _offsetY + e.TotalY);
                break;
            case GestureStatus.Completed:
                _offsetX = Surface.TranslationX;
                _offsetY = Surface.TranslationY;
                break;
        }
    }

    /// <summary>Doppio tocco: se è intera ingrandisce 3× sul punto toccato, altrimenti torna intera.</summary>
    private void OnDoubleTap(Point point)
    {
        if (_scale > 1)
        {
            Apply(1, 0, 0);
        }
        else
        {
            const double zoom = 3;
            Surface.AnchorX = 0;
            Surface.AnchorY = 0;
            Apply(zoom, -point.X * (zoom - 1), -point.Y * (zoom - 1));
        }
        _scale = Surface.Scale;
        _offsetX = Surface.TranslationX;
        _offsetY = Surface.TranslationY;
    }

    /// <summary>
    /// Un solo riconoscitore di tocchi: su Android due riconoscitori (singolo e doppio) sulla stessa vista fanno perdere il singolo.
    /// Il doppio tocco si riconosce qui dal tempo e dalla distanza tra due tocchi; il singolo scatta se non ne segue un secondo.
    /// </summary>
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        var p = e.GetPosition(Stage) ?? new Point(Stage.Width / 2, Stage.Height / 2);
        var now = Environment.TickCount64;
        if (_pendingTap is { } pending && now - _lastTapTick < DoubleTapMs &&
            Math.Abs(p.X - _lastTapPoint.X) < 80 && Math.Abs(p.Y - _lastTapPoint.Y) < 80)
        {
            pending.Cancel();
            _pendingTap = null;
            OnDoubleTap(p);
            return;
        }

        _pendingTap?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingTap = cts;
        _lastTapTick = now;
        _lastTapPoint = p;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DoubleTapMs, cts.Token); }
            catch (OperationCanceledException) { return; }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!ReferenceEquals(_pendingTap, cts)) return;
                _pendingTap = null;
                OnSingleTap(p);
            });
        });
    }

    /// <summary>Tocco singolo: con i riquadri spenti mostra o nasconde la moneta toccata (si risale al punto senza zoom).</summary>
    private void OnSingleTap(Point p)
    {
        if (Surface.Width <= 0) return;
        _viewModel.TapAt((p.X - Surface.TranslationX) / _scale, (p.Y - Surface.TranslationY) / _scale, Surface.Width, Surface.Height);
    }

    /// <summary>Applica zoom e spostamento tenendo l'immagine dentro lo schermo (niente bordi vuoti a sinistra o in alto).</summary>
    private void Apply(double scale, double x, double y)
    {
        Surface.Scale = scale;
        Surface.TranslationX = Math.Clamp(x, -Surface.Width * (scale - 1), 0);
        Surface.TranslationY = Math.Clamp(y, -Surface.Height * (scale - 1), 0);
    }
}
