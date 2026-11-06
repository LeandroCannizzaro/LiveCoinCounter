using LiveCoinCounter.ViewModels;

namespace LiveCoinCounter.Views;

public partial class SessionsPage : ContentPage
{
    private readonly SessionsViewModel _viewModel = new();

    public SessionsPage()
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
