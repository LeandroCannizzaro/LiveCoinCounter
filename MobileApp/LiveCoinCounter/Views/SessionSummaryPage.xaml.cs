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

    /// <summary>Sessione a scatti: swipe sulla superficie per passare a quella dello scatto successivo o precedente.</summary>
    private void OnSwiped(object? sender, SwipedEventArgs e) => _viewModel.Swipe(towardsLeft: e.Direction == SwipeDirection.Left);

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Detach();
    }
}
