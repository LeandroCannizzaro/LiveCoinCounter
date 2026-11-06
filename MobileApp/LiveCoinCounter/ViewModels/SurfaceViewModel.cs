using System.Windows.Input;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Sessions;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// La superficie acquisita a tutto schermo: solo l'immagine, da ingrandire. Le opzioni (vista, riquadri, griglia) si scelgono nel
/// riepilogo (<see cref="SurfaceOptions"/>); qui un tocco su una moneta ne mostra i dettagli, e un nuovo tocco su un'altra
/// sostituisce la selezione.
/// </summary>
public sealed class SurfaceViewModel : ObservableObject, IQueryAttributable
{
    private string? _sessionId;
    private SessionRecord? _session;
    private List<CoinRecord> _coins = new();
    private SurfaceScale? _scale;
    private SurfaceOptions? _options;
    private ImageSource? _image;
    private bool _isBusy;
    private bool _loaded;
    private int _version;
    private int? _selected;
    private (int Width, int Height) _imageSize;
    private string _detailText = string.Empty;

    public SurfaceViewModel()
    {
        CloseCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
        EditCommand = new Command(async () =>
        {
            // la stessa pagina di correzione dell'elenco monete: valore errato, doppione, non è una moneta...
            if (_selected is { } number && _coins.FirstOrDefault(c => c.Number == number) is { } coin)
                await Shell.Current.GoToAsync($"coin?id={coin.Id}");
        });
    }

    public ICommand CloseCommand { get; }

    /// <summary>Corregge la moneta selezionata; poi si torna qui e il riquadro ha il colore delle monete modificate.</summary>
    public ICommand EditCommand { get; }

    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>I dati della moneta toccata.</summary>
    public string DetailText
    {
        get => _detailText;
        private set { if (SetProperty(ref _detailText, value)) OnPropertyChanged(nameof(HasDetail)); }
    }

    public bool HasDetail => _detailText.Length > 0;

    /// <summary>
    /// Tocco sulla superficie, in coordinate della vista senza zoom (riquadro di <paramref name="viewWidth"/> × <paramref name="viewHeight"/>).
    /// Seleziona la moneta toccata (deselezionando la precedente); un secondo tocco sulla stessa la deseleziona.
    /// </summary>
    public void TapAt(double x, double y, double viewWidth, double viewHeight)
    {
        if (_session is null || _imageSize.Width <= 0 || viewWidth <= 0 || viewHeight <= 0) return;
        var fit = Math.Min(viewWidth / _imageSize.Width, viewHeight / _imageSize.Height);
        var px = (x - (viewWidth - _imageSize.Width * fit) / 2) / fit;
        var py = (y - (viewHeight - _imageSize.Height * fit) / 2) / fit;
        var mx = px / _session.PreviewScale + _session.PreviewOriginX;
        var my = py / _session.PreviewScale + _session.PreviewOriginY;

        // la moneta il cui centro è più vicino al tocco, se il tocco cade dentro il suo riquadro
        var hit = _coins.Where(c => !c.IsManual && mx >= c.X && mx <= c.X + c.Width && my >= c.Y && my <= c.Y + c.Height)
            .OrderBy(c => Math.Pow(mx - (c.X + c.Width / 2), 2) + Math.Pow(my - (c.Y + c.Height / 2), 2)).FirstOrDefault();
        if (hit is null) return;

        Select(_selected == hit.Number ? null : hit);
        _ = RenderAsync();
    }

    private void Select(CoinRecord? coin)
    {
        _selected = coin?.Number;
        DetailText = coin is null ? string.Empty : Describe(coin);
    }

    private string Describe(CoinRecord c) => CoinDescription.Describe(c, _scale);

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id)) _sessionId = Uri.UnescapeDataString(id.ToString() ?? string.Empty);
    }

    public async Task LoadAsync()
    {
        if (_sessionId is null) return;
        _session = await SessionServices.Repository.GetSessionAsync(_sessionId);
        if (_session is null) return;
        _coins = await SessionServices.Repository.ListCoinsAsync(_sessionId);
        _scale = SessionServices.ScaleOf(_session, _coins);
        _options = SurfaceOptions.For(_sessionId);
        _options.Configure(_scale);
        _loaded = true;

        // al ritorno dalla correzione la moneta selezionata ha dati nuovi (valore, scartata...)
        if (_selected is { } number)
        {
            var coin = _coins.FirstOrDefault(c => c.Number == number);
            if (coin is null) Select(null); else Select(coin);
        }
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (_session is null || _options is null) return;
        var session = _session;
        var coins = _coins;
        // con i riquadri spenti si vede solo la moneta selezionata; con i riquadri accesi tutti, e quella selezionata col contorno più spesso
        var revealed = _selected is { } selected ? new HashSet<int> { selected } : new HashSet<int>();
        var render = _options.ToRender(revealed, _selected);
        var mine = ++_version;
        IsBusy = true;
        try
        {
            var bytes = await Task.Run(() => ImageHelper.DrawSessionMosaic(session, coins, render));
            if (mine != _version) return;          // nel frattempo c'è stato un altro tocco: vale l'ultimo
            _imageSize = bytes is null ? default : ImageHelper.SizeOf(bytes);
            Image = bytes is null ? null : ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        finally
        {
            if (mine == _version) IsBusy = false;
        }
    }
}
