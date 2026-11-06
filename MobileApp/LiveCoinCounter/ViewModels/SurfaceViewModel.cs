using System.Windows.Input;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Sessions;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// La superficie acquisita a tutto schermo, da ingrandire per vedere i dettagli. Si può scegliere cosa mostrare:
/// riquadri delle monete, griglia metrica (se la scala è nota) e foto reale del tavolo al posto del piano di colore.
/// </summary>
public sealed class SurfaceViewModel : ObservableObject, IQueryAttributable
{
    private string? _sessionId;
    private SessionRecord? _session;
    private List<CoinRecord> _coins = new();
    private SurfaceScale? _scale;
    private ImageSource? _image;
    private bool _showBoxes = true;
    private bool _showGrid;
    private bool _showPhoto;
    private bool _isBusy;
    private bool _loaded;
    private int _version;

    public SurfaceViewModel()
    {
        CloseCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    public ICommand CloseCommand { get; }

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

    public string Title => _session?.Name ?? string.Empty;

    public bool ShowBoxes
    {
        get => _showBoxes;
        set { if (SetProperty(ref _showBoxes, value) && _loaded) _ = RenderAsync(); }
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set { if (SetProperty(ref _showGrid, value && CanGrid) && _loaded) _ = RenderAsync(); }
    }

    public bool ShowPhoto
    {
        get => _showPhoto;
        set { if (SetProperty(ref _showPhoto, value && HasPhoto) && _loaded) _ = RenderAsync(); }
    }

    /// <summary>La griglia si offre solo con una scala (misurata o stimata dalle monete).</summary>
    public bool CanGrid => _scale is not null;

    public bool HasPhoto => _sessionId is not null && File.Exists(SessionStorage.MosaicPhotoPath(_sessionId));

    /// <summary>Il passo della griglia: quello scelto nelle impostazioni, o 5 cm se lì è spenta.</summary>
    public GridSpacing Spacing => SettingsService.GridSpacing != GridSpacing.Off ? SettingsService.GridSpacing : GridSpacing.Cm5;

    public string GridText => CanGrid ? $"Griglia {(int)Spacing} cm" : "Griglia (scala non nota)";

    public string ScaleText => _scale?.Label ?? "servono almeno 3 monete riconosciute, o ARCore";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id)) _sessionId = Uri.UnescapeDataString(id.ToString() ?? string.Empty);
    }

    public async Task LoadAsync()
    {
        if (_sessionId is null || _loaded) return;
        _session = await SessionServices.Repository.GetSessionAsync(_sessionId);
        if (_session is null) return;
        _coins = await SessionServices.Repository.ListCoinsAsync(_sessionId);
        _scale = SessionServices.ScaleOf(_session, _coins);
        _showGrid = CanGrid && SettingsService.GridSpacing != GridSpacing.Off;
        _showPhoto = !SettingsService.SolidSurface && HasPhoto;
        _loaded = true;

        foreach (var p in new[] { nameof(Title), nameof(CanGrid), nameof(HasPhoto), nameof(GridText), nameof(ScaleText), nameof(ShowGrid), nameof(ShowPhoto) })
            OnPropertyChanged(p);
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (_session is null) return;
        var session = _session;
        var coins = _coins;
        var options = new SurfaceRender(Photo: _showPhoto, Boxes: _showBoxes, Grid: _showGrid ? Spacing : GridSpacing.Off, Scale: _scale);
        var mine = ++_version;
        IsBusy = true;
        try
        {
            var bytes = await Task.Run(() => ImageHelper.DrawSessionMosaic(session, coins, options));
            if (mine != _version) return;          // nel frattempo l'utente ha cambiato un'opzione: vale l'ultima
            Image = bytes is null ? null : ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        finally
        {
            if (mine == _version) IsBusy = false;
        }
    }
}
