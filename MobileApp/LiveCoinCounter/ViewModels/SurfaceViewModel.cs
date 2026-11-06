using System.Windows.Input;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Sessions;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// La superficie acquisita a tutto schermo, da ingrandire per vedere i dettagli. Si sceglie cosa mostrare: la vista
/// (superficie continua, frame acquisiti, foto reale), i riquadri delle monete e la griglia metrica (se la scala è nota).
/// </summary>
public sealed class SurfaceViewModel : ObservableObject, IQueryAttributable
{
    /// <summary>La griglia di ripiego quando nelle impostazioni è spenta e qui la si accende.</summary>
    private static readonly GridConfig DefaultGrid = new(1, 5);

    private string? _sessionId;
    private SessionRecord? _session;
    private List<CoinRecord> _coins = new();
    private SurfaceScale? _scale;
    private ImageSource? _image;
    private SettingOption<SurfaceLook>? _selectedView;
    private bool _showBoxes = true;
    private bool _showGrid;
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

    /// <summary>Le viste disponibili (le sessioni vecchie hanno solo quella salvata allora).</summary>
    public IReadOnlyList<SettingOption<SurfaceLook>> Views { get; private set; } = Array.Empty<SettingOption<SurfaceLook>>();

    public SettingOption<SurfaceLook>? SelectedView
    {
        get => _selectedView;
        set { if (SetProperty(ref _selectedView, value) && _loaded) _ = RenderAsync(); }
    }

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

    /// <summary>La griglia si offre solo con una scala (misurata o stimata dalle monete).</summary>
    public bool CanGrid => _scale is not null;

    /// <summary>I passi della griglia: quelli delle impostazioni, o 1 cm e 5 cm se lì è spenta.</summary>
    public GridConfig Grid => SettingsService.Grid.IsOff ? DefaultGrid : SettingsService.Grid;

    public string GridText => CanGrid ? $"Griglia: {Grid.Label}" : "Griglia (scala non nota)";

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
        _showGrid = CanGrid && !SettingsService.Grid.IsOff;

        // solo le viste che hanno un file (le sessioni salvate prima di questa versione hanno il mosaico unico)
        var all = new[]
        {
            new SettingOption<SurfaceLook>(SurfaceLook.Continuous, "Superficie continua", string.Empty),
            new SettingOption<SurfaceLook>(SurfaceLook.Frames, "Frame acquisiti", string.Empty),
            new SettingOption<SurfaceLook>(SurfaceLook.Photo, "Foto reale", string.Empty)
        };
        Views = all.Where(v => File.Exists(SessionStorage.MosaicPathFor(_sessionId, v.Value))).ToList();
        if (Views.Count == 0) Views = new[] { all[(int)SettingsService.SurfaceLook] };
        _selectedView = Views.FirstOrDefault(v => v.Value == SettingsService.SurfaceLook) ?? Views[0];
        _loaded = true;

        foreach (var p in new[] { nameof(Title), nameof(CanGrid), nameof(GridText), nameof(ScaleText), nameof(ShowGrid), nameof(Views), nameof(SelectedView) })
            OnPropertyChanged(p);
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (_session is null) return;
        var session = _session;
        var coins = _coins;
        var options = new SurfaceRender(View: _selectedView?.Value ?? SurfaceLook.Continuous, Boxes: _showBoxes,
            Grid: _showGrid ? Grid : GridConfig.Off, Scale: _scale);
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
