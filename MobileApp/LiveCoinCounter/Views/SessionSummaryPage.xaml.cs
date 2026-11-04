using LiveCoinCounter.ViewModels;

namespace LiveCoinCounter.Views;

public partial class SessionSummaryPage : ContentPage
{
    private readonly SessionSummaryViewModel _viewModel = new();

    public SessionSummaryPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();   // anche al ritorno da una correzione
    }
}
