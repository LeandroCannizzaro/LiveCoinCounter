using LiveCoinCounter.Views;

namespace LiveCoinCounter;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // pagine aperte dalle sessioni (riepilogo → elenco monete → dettaglio)
        Routing.RegisterRoute("session", typeof(SessionSummaryPage));
        Routing.RegisterRoute("coins", typeof(CoinListPage));
        Routing.RegisterRoute("coin", typeof(CoinDetailPage));
    }
}
