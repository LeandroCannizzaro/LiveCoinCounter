using LiveCoinCounter.ViewModels;

namespace LiveCoinCounter.Views;

public partial class CapturePage : ContentPage
{
    private readonly CaptureViewModel _viewModel = new();

    public CapturePage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        _viewModel.AttachView(Mosaic);
        _viewModel.FullScreenChanged += fullScreen => MainThread.BeginInvokeOnMainThread(() => Shell.SetTabBarIsVisible(this, !fullScreen));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Mosaic.Refresh();
        await _viewModel.OnAppearingAsync();
    }
}
