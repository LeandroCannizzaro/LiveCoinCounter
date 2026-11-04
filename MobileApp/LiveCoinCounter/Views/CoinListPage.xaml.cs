using LiveCoinCounter.ViewModels;

namespace LiveCoinCounter.Views;

public partial class CoinListPage : ContentPage
{
    private readonly CoinListViewModel _viewModel = new();

    public CoinListPage()
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
