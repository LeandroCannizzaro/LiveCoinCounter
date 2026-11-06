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
        return string.Join(Environment.NewLine, lines);
    }

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
        _options = SurfaceOptions.For(_sessionId);
        _options.Configure(_scale);
        _loaded = true;
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
