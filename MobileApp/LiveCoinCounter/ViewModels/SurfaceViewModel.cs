using System.Windows.Input;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Sessions;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// La superficie acquisita a tutto schermo: solo l'immagine, da ingrandire; un tocco su una moneta ne mostra i dettagli (un nuovo tocco
/// su un'altra sostituisce la selezione).
/// <list type="bullet">
/// <item>Una sessione (<c>id</c>): le opzioni (vista, riquadri, griglia) si scelgono nel riepilogo (<see cref="SurfaceOptions"/>).</item>
/// <item>Una sessione a scatti (<c>series</c>, eventualmente <c>index</c>): una pila di superfici, una per scatto, da scorrere con le
/// frecce o con uno swipe («Superficie k di n»); le opzioni sono quelle della sessione, la vista si cambia anche qui.</item>
/// </list>
/// </summary>
public sealed class SurfaceViewModel : ObservableObject, IQueryAttributable
{
    private string? _sessionId;
    private string? _seriesId;
    private List<SessionRecord> _series = new();
    private int _index = -1;
    private SessionRecord? _session;
    private List<CoinRecord> _coins = new();
    private SurfaceScale? _scale;
    private SurfaceOptions? _options;
    private ImageSource? _image;
    private bool _isBusy;
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
        PreviousCommand = new Command(async () => await ShowAsync(_index - 1), () => _index > 0);
        NextCommand = new Command(async () => await ShowAsync(_index + 1), () => _index >= 0 && _index < _series.Count - 1);
        CycleViewCommand = new Command(async () => await CycleViewAsync());
        AnotherShotCommand = new Command(async () =>
        {
            // un altro scatto della stessa serie: la schermata di acquisizione riapre subito l'inquadratura
            CaptureViewModel.PendingShotSeries = _seriesId;
            await Shell.Current.GoToAsync("//CapturePage");
        });
        // il riepilogo della sessione a scatti sta sotto questa pagina
        DoneCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    public ICommand CloseCommand { get; }

    /// <summary>Corregge la moneta selezionata; poi si torna qui e il riquadro ha il colore delle monete modificate.</summary>
    public ICommand EditCommand { get; }

    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }

    /// <summary>Serie: cambia la vista della superficie mostrata (continua → frame acquisiti → foto reale).</summary>
    public ICommand CycleViewCommand { get; }

    /// <summary>Serie: un altro scatto, su un'altra parte del tavolo (evitare di riprendere monete già scattate).</summary>
    public ICommand AnotherShotCommand { get; }

    public ICommand DoneCommand { get; }

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

    /// <summary>Si sta scorrendo una serie di scatti.</summary>
    public bool IsSeries => _seriesId is not null;

    /// <summary>«Superficie k di n».</summary>
    public string PositionText => IsSeries && _series.Count > 0 ? $"Superficie {_index + 1} di {_series.Count}" : string.Empty;

    /// <summary>Questa superficie e tutta la serie: monete e valore.</summary>
    public string SeriesTotalText
    {
        get
        {
            if (!IsSeries || _session is null) return string.Empty;
            var coins = _series.Sum(s => s.CoinCount);
            var cents = _series.Sum(s => s.TotalCents);
            return $"Questo scatto: {_session.CoinCount} monete, {SessionActions.Format(_session.TotalCents)} · " +
                   $"sessione: {coins} monete, {SessionActions.Format(cents)}";
        }
    }

    public string ViewText => _options?.SelectedView?.Value switch
    {
        SurfaceLook.Continuous => "Vista: continua",
        SurfaceLook.Frames => "Vista: frame",
        SurfaceLook.Photo => "Vista: foto",
        _ => "Vista"
    };

    /// <summary>Il pannello dei dettagli sta sopra la barra di navigazione della serie.</summary>
    public Thickness DetailMargin => IsSeries ? new Thickness(12, 0, 12, 132) : new Thickness(12, 0, 12, 24);

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

    /// <summary>Swipe sulla superficie (senza zoom): verso sinistra la successiva, verso destra la precedente.</summary>
    public void Swipe(bool towardsLeft)
    {
        if (!IsSeries) return;
        var command = towardsLeft ? NextCommand : PreviousCommand;
        if (command.CanExecute(null)) command.Execute(null);
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
        if (query.TryGetValue("series", out var series)) _seriesId = Uri.UnescapeDataString(series.ToString() ?? string.Empty);
        if (query.TryGetValue("index", out var index) && int.TryParse(index.ToString(), out var i)) _index = i;
    }

    public async Task LoadAsync()
    {
        if (_seriesId is not null)
        {
            // gli scatti della serie, in ordine; si apre sull'ultimo (appena fatto) o si resta dove si era (ritorno da una correzione)
            _series = await SessionServices.Repository.ListShotsAsync(_seriesId);
            if (_series.Count == 0) return;
            if (_index < 0 || _index >= _series.Count) _index = _series.Count - 1;
            _sessionId = _series[_index].Id;
        }
        if (_sessionId is null) return;

        _session = await SessionServices.Repository.GetSessionAsync(_sessionId);
        if (_session is null) return;
        _coins = await SessionServices.Repository.ListCoinsAsync(_sessionId);
        _scale = SessionServices.ScaleOf(_session, _coins);
        // in una sessione a scatti le opzioni (vista, riquadri, griglia) sono quelle della sessione, uguali per tutte le superfici
        _options = SurfaceOptions.For(_seriesId ?? _sessionId);
        _options.Configure(_scale);

        // al ritorno dalla correzione la moneta selezionata ha dati nuovi (valore, scartata...)
        if (_selected is { } number)
        {
            var coin = _coins.FirstOrDefault(c => c.Number == number);
            if (coin is null) Select(null); else Select(coin);
        }
        NotifySeries();
        await RenderAsync();
    }

    private async Task ShowAsync(int index)
    {
        if (index < 0 || index >= _series.Count || index == _index) return;
        _index = index;
        Select(null);
        await LoadAsync();
    }

    private async Task CycleViewAsync()
    {
        if (_options is not { Views.Count: > 1 } options || options.SelectedView is null) return;
        var views = options.Views.ToList();
        var next = views[(views.FindIndex(v => v.Value == options.SelectedView.Value) + 1) % views.Count];
        options.SelectedView = next;
        OnPropertyChanged(nameof(ViewText));
        await RenderAsync();
    }

    private void NotifySeries()
    {
        foreach (var p in new[] { nameof(IsSeries), nameof(PositionText), nameof(SeriesTotalText), nameof(ViewText), nameof(DetailMargin) })
            OnPropertyChanged(p);
        ((Command)PreviousCommand).ChangeCanExecute();
        ((Command)NextCommand).ChangeCanExecute();
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
