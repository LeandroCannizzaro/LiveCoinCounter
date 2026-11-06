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
    private readonly HashSet<int> _revealed = new();
    private int? _selected;
    private (int Width, int Height) _imageSize;
    private string _detailText = string.Empty;

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
        set
        {
            if (!SetProperty(ref _showBoxes, value)) return;
            _revealed.Clear();
            Select(null);
            if (_loaded) _ = RenderAsync();
        }
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set { if (SetProperty(ref _showGrid, value && CanGrid) && _loaded) _ = RenderAsync(); }
    }

    /// <summary>La griglia si offre solo con una scala (misurata o stimata dalle monete).</summary>
    /// <summary>I dati della moneta toccata: si vedono solo quando i riquadri sono spenti e la moneta è stata mostrata.</summary>
    public string DetailText
    {
        get => _detailText;
        private set { if (SetProperty(ref _detailText, value)) OnPropertyChanged(nameof(HasDetail)); }
    }

    public bool HasDetail => _detailText.Length > 0;

    /// <summary>
    /// Tocco sulla superficie, in coordinate della vista senza zoom (riquadro di <paramref name="viewWidth"/> × <paramref name="viewHeight"/>).
    /// Con i riquadri spenti mostra o nasconde la moneta toccata.
    /// </summary>
    public void TapAt(double x, double y, double viewWidth, double viewHeight)
    {
        if (_showBoxes || _session is null || _imageSize.Width <= 0 || viewWidth <= 0 || viewHeight <= 0) return;
        var fit = Math.Min(viewWidth / _imageSize.Width, viewHeight / _imageSize.Height);
        var px = (x - (viewWidth - _imageSize.Width * fit) / 2) / fit;
        var py = (y - (viewHeight - _imageSize.Height * fit) / 2) / fit;
        var mx = px / _session.PreviewScale + _session.PreviewOriginX;
        var my = py / _session.PreviewScale + _session.PreviewOriginY;

        // la moneta il cui centro è più vicino al tocco, se il tocco cade dentro il suo riquadro
        var hit = _coins.Where(c => !c.IsManual && mx >= c.X && mx <= c.X + c.Width && my >= c.Y && my <= c.Y + c.Height)
            .OrderBy(c => Math.Pow(mx - (c.X + c.Width / 2), 2) + Math.Pow(my - (c.Y + c.Height / 2), 2)).FirstOrDefault();
        if (hit is null) return;

        if (_revealed.Add(hit.Number)) Select(hit);          // era nascosta: la mostra con i suoi dati
        else
        {
            _revealed.Remove(hit.Number);                      // era visibile: la nasconde
            if (_selected == hit.Number) Select(null);
        }
        _ = RenderAsync();
    }

    private void Select(CoinRecord? coin)
    {
        _selected = coin?.Number;
        DetailText = coin is null ? string.Empty : Describe(coin);
    }

    private string Describe(CoinRecord c)
    {
        static string Value(int? cents) => cents is null ? "?" : cents >= 100 ? $"{cents / 100} €" : $"{cents} cent";
        var lines = new List<string>
        {
            $"Moneta {c.Number}: {(c.State == CoinState.Rejected ? "scartata" : Value(c.EffectiveCents))}" + (c.CorrectedCents is not null ? " (corretta a mano)" : string.Empty),
            $"Riconoscimento: {Value(c.Cents)} con confidenza {c.ClassConfidence:P0}" + (c.SecondCents is not null ? $" · alternativa {Value(c.SecondCents)} {c.SecondScore:P0}" : string.Empty),
            $"Rilevatore: {Value(c.DetectorCents)} {c.DetectorClassConfidence:P0} · moneta trovata con confidenza {c.DetectionConfidence:P0} in {c.Observations} inquadrature"
        };
        var diameterPx = (c.Width + c.Height) / 2;
        var size = $"Dimensione nel mosaico: {diameterPx:0} px";
        if (_scale is { PxPerMm: > 0 } scale)
        {
            var mm = diameterPx / scale.PxPerMm;
            size += $" ≈ {mm:0.0} mm";
            if (c.EffectiveCents is { } known && EuroCoins.IsKnown(known)) size += $" (ufficiale {EuroCoins.DiameterOf(known):0.##} mm)";
            if (!scale.Measured) size += " · scala stimata";
        }
        lines.Add(size);
        if (!string.IsNullOrEmpty(c.ColorTint) && c.ColorTint != "?")
            lines.Add($"Colore: {(c.ColorTint == "copper" ? "rame" : "oro")} (tinta {c.ColorHue:0}°, saturazione {c.ColorSat:P0})");
        if (!string.IsNullOrWhiteSpace(c.Note)) lines.Add($"Nota: {c.Note}");
        if (!string.IsNullOrEmpty(c.ClassifierId)) lines.Add($"Classificatore: {c.ClassifierId}");
        return string.Join("
", lines);
    }

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
            Grid: _showGrid ? Grid : GridConfig.Off, Scale: _scale,
            Revealed: _revealed.ToHashSet(), Selected: _selected);
        var mine = ++_version;
        IsBusy = true;
        try
        {
            var bytes = await Task.Run(() => ImageHelper.DrawSessionMosaic(session, coins, options));
            if (mine != _version) return;          // nel frattempo l'utente ha cambiato un'opzione: vale l'ultima
            _imageSize = bytes is null ? default : ImageHelper.SizeOf(bytes);
            Image = bytes is null ? null : ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        finally
        {
            if (mine == _version) IsBusy = false;
        }
    }
}
