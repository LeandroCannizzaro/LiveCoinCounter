using LiveCoinCounter.ViewModels;

namespace LiveCoinCounter.Views;

public partial class SessionSummaryPage : ContentPage
{
    private readonly SessionSummaryViewModel _viewModel = new();

    public SessionSummaryPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;

        // la superficie ha un riquadro di altezza fissa (quadrato): passando da una superficie all'altra l'immagine nuova arriva
        // dopo un attimo, e con l'altezza presa dall'immagine la pagina si accorciava e scorreva da sola
        SurfaceFrame.SizeChanged += (_, _) =>
        {
            if (SurfaceFrame.Width > 0 && Math.Abs(SurfaceFrame.HeightRequest - SurfaceFrame.Width) > 1)
                SurfaceFrame.HeightRequest = SurfaceFrame.Width;
        };
        SurfaceImage.HandlerChanged += (_, _) => AttachTouch();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();   // anche al ritorno da una correzione
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Detach();
    }

    /// <summary>
    /// Tocco (schermo intero) e swipe orizzontale (superficie precedente / successiva) letti direttamente dagli eventi di Android:
    /// i riconoscitori di MAUI sull'immagine si prendevano anche il trascinamento verticale e la pagina non scorreva più.
    /// Qui il movimento verticale lo intercetta lo ScrollView come sempre; all'immagine resta quello orizzontale.
    /// </summary>
    private void AttachTouch()
    {
#if ANDROID
        if (SurfaceImage.Handler?.PlatformView is Android.Views.View view)
            view.SetOnTouchListener(new SurfaceTouch(view.Context!, OnSurfaceTap, towardsLeft => _viewModel.Swipe(towardsLeft)));
#endif
    }

    private void OnSurfaceTap()
    {
        if (_viewModel.OpenSurfaceCommand.CanExecute(null)) _viewModel.OpenSurfaceCommand.Execute(null);
    }

#if ANDROID
    private sealed class SurfaceTouch : Java.Lang.Object, Android.Views.View.IOnTouchListener
    {
        private readonly Action _tap;
        private readonly Action<bool> _swipe;
        private readonly float _slop;
        private readonly float _swipeMin;
        private float _x, _y;
        private long _downAt;

        public SurfaceTouch(Android.Content.Context context, Action tap, Action<bool> swipe)
        {
            _tap = tap;
            _swipe = swipe;
            _slop = Android.Views.ViewConfiguration.Get(context)!.ScaledTouchSlop;
            _swipeMin = 50 * context.Resources!.DisplayMetrics!.Density;
        }

        public bool OnTouch(Android.Views.View? v, Android.Views.MotionEvent? e)
        {
            if (e is null) return false;
            switch (e.ActionMasked)
            {
                case Android.Views.MotionEventActions.Down:
                    _x = e.RawX;
                    _y = e.RawY;
                    _downAt = e.EventTime;
                    return true;    // senza, i movimenti successivi non arrivano; lo ScrollView si riprende quelli verticali

                case Android.Views.MotionEventActions.Move:
                    // un trascinamento chiaramente orizzontale: lo ScrollView non deve prenderselo
                    if (Math.Abs(e.RawX - _x) > _slop && Math.Abs(e.RawX - _x) > Math.Abs(e.RawY - _y))
                        v?.Parent?.RequestDisallowInterceptTouchEvent(true);
                    return true;

                case Android.Views.MotionEventActions.Up:
                    var dx = e.RawX - _x;
                    var dy = e.RawY - _y;
                    if (Math.Abs(dx) > _swipeMin && Math.Abs(dx) > 1.5f * Math.Abs(dy)) _swipe(dx < 0);
                    else if (Math.Abs(dx) < _slop && Math.Abs(dy) < _slop && e.EventTime - _downAt < 500) _tap();
                    return true;

                default:
                    return true;
            }
        }
    }
#endif
}
