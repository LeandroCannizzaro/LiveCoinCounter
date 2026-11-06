using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Sessions;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// Cosa mostrare della superficie acquisita di una sessione: la vista (superficie continua, frame acquisiti, foto reale), i
/// riquadri delle monete e la griglia metrica. Si scelgono nel riepilogo e valgono anche a tutto schermo: per questo l'oggetto
/// è condiviso tra le due schermate finché si resta sulla stessa sessione.
/// </summary>
public sealed class SurfaceOptions : ObservableObject
{
    /// <summary>La griglia di ripiego quando nelle impostazioni è spenta e qui la si accende.</summary>
    private static readonly GridConfig DefaultGrid = new(1, 5);

    private static SurfaceOptions? _current;

    private SurfaceScale? _scale;
    private bool _showBoxes = true;
    private bool _showGrid;
    private bool _gridChosen;
    private SettingOption<SurfaceLook>? _selectedView;

    private SurfaceOptions(string sessionId) => SessionId = sessionId;

    /// <summary>Le opzioni della sessione: le stesse finché non si passa a un'altra sessione.</summary>
    public static SurfaceOptions For(string sessionId)
    {
        if (_current is null || _current.SessionId != sessionId) _current = new SurfaceOptions(sessionId);
        return _current;
    }

    public string SessionId { get; }

    /// <summary>Un'opzione è cambiata: chi mostra la superficie deve ridisegnarla.</summary>
    public event Action? Changed;

    /// <summary>Le viste disponibili (le sessioni vecchie hanno solo quella salvata allora).</summary>
    public IReadOnlyList<SettingOption<SurfaceLook>> Views { get; private set; } = Array.Empty<SettingOption<SurfaceLook>>();

    public SettingOption<SurfaceLook>? SelectedView
    {
        get => _selectedView;
        set
        {
            if (value is null) return;          // il selettore si svuota quando cambia l'elenco: si tiene la scelta
            if (SetProperty(ref _selectedView, value)) Changed?.Invoke();
        }
    }

    public bool ShowBoxes
    {
        get => _showBoxes;
        set { if (SetProperty(ref _showBoxes, value)) Changed?.Invoke(); }
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            _gridChosen = true;
            if (SetProperty(ref _showGrid, value && CanGrid)) Changed?.Invoke();
        }
    }

    /// <summary>La griglia si offre solo con una scala (misurata o stimata dalle monete).</summary>
    public bool CanGrid => _scale is not null;

    public GridConfig Grid => SettingsService.Grid.IsOff ? DefaultGrid : SettingsService.Grid;

    public string GridText => CanGrid ? $"Griglia: {Grid.Label}" : "Griglia (scala non nota)";

    public string ScaleText => _scale?.Label ?? "servono almeno 3 monete riconosciute, o ARCore";

    public SurfaceScale? Scale => _scale;

    /// <summary>Dopo aver caricato la sessione: la scala e le viste che hanno un file. Le scelte già fatte restano.</summary>
    public void Configure(SurfaceScale? scale)
    {
        _scale = scale;
        var all = new[]
        {
            new SettingOption<SurfaceLook>(SurfaceLook.Continuous, "Superficie continua", string.Empty),
            new SettingOption<SurfaceLook>(SurfaceLook.Frames, "Frame acquisiti", string.Empty),
            new SettingOption<SurfaceLook>(SurfaceLook.Photo, "Foto reale", string.Empty)
        };
        var views = all.Where(v => File.Exists(SessionStorage.MosaicPathFor(SessionId, v.Value))).ToList();
        if (views.Count == 0) views = new List<SettingOption<SurfaceLook>> { all[(int)SettingsService.SurfaceLook] };
        Views = views;

        var wanted = _selectedView?.Value ?? SettingsService.SurfaceLook;
        _selectedView = views.FirstOrDefault(v => v.Value == wanted) ?? views[0];

        // la griglia parte come nelle impostazioni, finché l'utente non la sceglie
        if (!_gridChosen) _showGrid = CanGrid && !SettingsService.Grid.IsOff;
        else if (!CanGrid) _showGrid = false;

        foreach (var p in new[] { nameof(Views), nameof(SelectedView), nameof(CanGrid), nameof(GridText), nameof(ScaleText), nameof(ShowGrid), nameof(ShowBoxes) })
            OnPropertyChanged(p);
    }

    /// <summary>Come disegnare la superficie con queste opzioni; <paramref name="revealed"/> e <paramref name="selected"/> servono a schermo intero.</summary>
    public SurfaceRender ToRender(IReadOnlySet<int>? revealed = null, int? selected = null) =>
        new(View: _selectedView?.Value ?? SurfaceLook.Continuous, Boxes: _showBoxes, Grid: _showGrid ? Grid : GridConfig.Off,
            Scale: _scale, Revealed: revealed, Selected: selected);
}
