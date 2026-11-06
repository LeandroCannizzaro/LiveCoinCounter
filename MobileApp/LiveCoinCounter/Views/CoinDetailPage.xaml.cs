using LiveCoinCounter.ViewModels;

namespace LiveCoinCounter.Views;

public partial class CoinDetailPage : ContentPage
{
    private readonly CoinDetailViewModel _viewModel = new();

    public CoinDetailPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }
}
