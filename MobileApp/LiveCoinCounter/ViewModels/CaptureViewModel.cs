using System.Collections.Concurrent;
using System.Windows.Input;
using LiveCoinCounter.Controls;
using LiveCoinCounter.Mosaic;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Sessions;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// Schermata di acquisizione: ▶ avvia la sessione, ■ la ferma. Durante l'acquisizione mostra il mosaico, i riquadri
/// delle monete e il loro numero (solo il numero: il valore arriva dopo lo stop). Dopo lo stop classifica e apre il riepilogo.
/// </summary>
public sealed class CaptureViewModel : ObservableObject
{
    private enum State { Idle, Starting, Capturing, Stopping, Processing }

    private static bool _checkedInterrupted;

    private readonly ConcurrentQueue<IReadOnlyList<TileKey>> _changedTiles = new();
    private MosaicView? _view;
    private IDispatcherTimer? _timer;
    private CaptureController? _controller;
    private CancellationTokenSource? _processingCts;
    private State _state = State.Idle;
    private volatile TrackingUpdate? _lastTracking;
    private volatile bool _coinsChanged;
    private string? _loggedHint;
    private string? _stickyText;
    private long _stickySince, _stickyLastSeen;
    private bool _hasRecovery;
    private ImageSource? _lastGoodImage;
    private double _lastGoodRotation;
    private long _lastGoodTick;
    private ImageSource? _lastGoodHolder;
    private double _arrowRotation;
    private string _recoveryText = string.Empty;
    private bool _scaleDirty = true;
    private int _lastMarkerCount;
    private int _markerCount;
    private CameraFrame? _watchFrame;
    private long _watchTick;
    private SurfaceScale? _gridScale;
    private bool _torchOn;
    private long _lastRefocusTick;
    private volatile string? _lastError;
    private long _lastErrorTick;
    private CameraFrame? _latestFrame;
    private string? _hint;
    private string _statusText = "Tocca ▶ e muovi lentamente il telefono sopra il tavolo";
    private int _coinCount;
    private double _progress;
    private string _progressText = string.Empty;

    public CaptureViewModel()
    {
        ToggleCommand = new Command(async () => await ToggleAsync(), () => _state is State.Idle or State.Capturing);
        ImportPhotoCommand = new Command(async () => await ImportPhotoAsync(), () => _state == State.Idle);
        SingleShotCommand = new Command(async () => await EnterShotModeAsync(DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture)), () => _state == State.Idle);
        ShutterCommand = new Command(async () => await TakeShotAsync(), () => _shotPreview && !_shotRunning);
        CancelShotCommand = new Command(async () => await CancelShotAsync(), () => _shotPreview && !_shotRunning);
        CancelProcessingCommand = new Command(() => _processingCts?.Cancel());
        RestartCommand = new Command(async () => await RestartAsync(), () => _state == State.Capturing);
        ToggleTorchCommand = new Command(() =>
        {
            if (_controller is null) return;
            _torchOn = !_torchOn;
            _controller.SetTorch(_torchOn);
            OnPropertyChanged(nameof(TorchSymbol));
        });
    }

    /// <summary>Da persi: la freccia (gradi, 0 = in alto, orario) e il testo con la direzione e la distanza stimata.</summary>
    public bool HasRecovery
    {
        get => _hasRecovery;
        private set => SetProperty(ref _hasRecovery, value);
    }

    /// <summary>L'ultima inquadratura agganciata: da persi la si mostra, da cercare a occhio sul tavolo.</summary>
    public ImageSource? LastGoodImage
    {
        get => _lastGoodImage;
        private set => SetProperty(ref _lastGoodImage, value);
    }

    public double LastGoodRotation
    {
        get => _lastGoodRotation;
        private set => SetProperty(ref _lastGoodRotation, value);
    }

    public double ArrowRotation
    {
        get => _arrowRotation;
        private set => SetProperty(ref _arrowRotation, value);
    }

    public string RecoveryText
    {
        get => _recoveryText;
        private set => SetProperty(ref _recoveryText, value);
    }

    /// <summary>Accende e spegne la torcia: con poca luce l'immagine si sfoca e il tracking si perde.</summary>
    public ICommand ToggleTorchCommand { get; }
    public bool CanTorch => _state == State.Capturing && _controller?.SupportsTorch == true;
    public string TorchSymbol => _torchOn ? "🔦" : "🔅";

    public ICommand ToggleCommand { get; }

    /// <summary>Salva la parte acquisita come sessione a sé e ricomincia da capo (utile quando il tracking è perso).</summary>
    public ICommand RestartCommand { get; }
    public bool CanRestart => _state == State.Capturing && !_shotPreview;
    public ICommand ImportPhotoCommand { get; }

    /// <summary>Scatto singolo: apre la telecamera a tutto schermo per inquadrare (una nuova serie di scatti).</summary>
    public ICommand SingleShotCommand { get; }

    /// <summary>Lo scatto: qualche fotogramma della stessa inquadratura, analizzato come nell'acquisizione continua.</summary>
    public ICommand ShutterCommand { get; }

    /// <summary>Esce dall'inquadratura senza scattare.</summary>
    public ICommand CancelShotCommand { get; }

    /// <summary>Si sta inquadrando per uno scatto: telecamera a tutto schermo e pulsante di scatto.</summary>
    public bool IsShotPreview => _shotPreview;

    /// <summary>Il pulsante ▶/■ dell'acquisizione continua (nascosto mentre si inquadra uno scatto).</summary>
    public bool ShowToggle => !_shotPreview;

    /// <summary>
    /// Chiesto dalla pila delle superfici («Altro scatto»): alla prossima apertura della schermata si inquadra un altro scatto della
    /// stessa serie.
    /// </summary>
    public static string? PendingShotSeries { get; set; }
    public ICommand CancelProcessingCommand { get; }

    /// <summary>Il pulsante alterna i simboli start e stop.</summary>
    public string ToggleSymbol => _state is State.Capturing or State.Stopping ? "■" : "▶";

    public bool IsIdle => _state == State.Idle;
    public bool IsCapturing => _state is State.Capturing or State.Stopping or State.Starting;
    public bool IsProcessing => _state is State.Processing or State.Stopping;
    public bool ShowCounter => _state != State.Idle && !_shotPreview;

    public string? Hint
    {
        get => _hint;
        private set { if (SetProperty(ref _hint, value)) OnPropertyChanged(nameof(HasHint)); }
    }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public int CoinCount
    {
        get => _coinCount;
        private set { if (SetProperty(ref _coinCount, value)) OnPropertyChanged(nameof(CoinCountText)); }
    }

    /// <summary>I marker di scala visti finora.</summary>
    public int MarkerCount
    {
        get => _markerCount;
        private set { if (SetProperty(ref _markerCount, value)) OnPropertyChanged(nameof(CoinCountText)); }
    }

    public string CoinCountText => (CoinCount == 1 ? "1 moneta trovata" : $"{CoinCount} monete trovate") + (MarkerCount > 0 ? $" · {MarkerCount} marker" : string.Empty);

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>Notifica alla pagina che deve nascondere o mostrare la barra delle schede.</summary>
    public event Action<bool>? FullScreenChanged;

    public void AttachView(MosaicView view)
    {
        _view = view;
        _view.EmptyText = "Il mosaico del tavolo comparirà qui";
    }

    public async Task OnAppearingAsync()
    {
        if (PendingShotSeries is { } series && _state == State.Idle)
        {
            PendingShotSeries = null;
            await EnterShotModeAsync(series);
            return;
        }
        if (_checkedInterrupted || _state != State.Idle) return;
        _checkedInterrupted = true;
        try
        {
            var interrupted = await SessionServices.Repository.ListInterruptedAsync();
            foreach (var session in interrupted)
            {
                var coins = await SessionServices.Repository.ListCoinsAsync(session.Id);
                var page = CurrentPage;
                if (page is null) return;
                var choice = await page.DisplayActionSheetAsync(
                    $"«{session.Name}» è stata interrotta con {coins.Count} monete trovate. Cosa vuoi fare?",
                    "Più tardi", "Elimina", "Completa la sessione");
                if (choice == "Elimina")
                {
                    if (session.SeriesId is { } parent) await SessionServices.DeleteShotAsync(session.Id, parent);
                    else await SessionServices.DeleteSessionAsync(session.Id);
                }
                else if (choice == "Completa la sessione") { await ProcessAndOpenAsync(session.Id, session.SeriesId); return; }
                else return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Controllo sessioni interrotte: {ex}");
        }
    }

    /// <summary>Quanti fotogrammi fa uno scatto e quanto si aspetta al massimo (fotogrammi sfocati o tracking non ancora partito).</summary>
    private const int SingleShotCount = 3;
    private const int SingleShotTimeoutMs = 6000;
    private bool _shotPreview;
    private bool _shotRunning;
    private string? _shotSeries;

    private void SetShotState(bool preview, bool running)
    {
        _shotPreview = preview;
        _shotRunning = running;
        if (_view is not null) _view.FullPreview = preview;
        OnPropertyChanged(nameof(IsShotPreview));
        OnPropertyChanged(nameof(ShowCounter));
        OnPropertyChanged(nameof(ShowToggle));
        OnPropertyChanged(nameof(CanRestart));
        ((Command)ShutterCommand).ChangeCanExecute();
        ((Command)CancelShotCommand).ChangeCanExecute();
    }

    /// <summary>
    /// Scatto: si apre la telecamera a tutto schermo per inquadrare (il motore è in pausa: niente mosaico finché non si scatta).
    /// Ogni scatto è una sessione della serie <paramref name="seriesId"/>; gli scatti di una serie si vedono poi come pila di superfici.
    /// </summary>
    private async Task EnterShotModeAsync(string seriesId)
    {
        if (_state != State.Idle) return;
        _shotSeries = seriesId;
        SetShotState(preview: true, running: false);
        await StartAsync(seriesId, paused: true);
        if (_state != State.Capturing || _controller is null)
        {
            SetShotState(preview: false, running: false);
            return;
        }
        StatusText = $"Scatto {_controller.Session.ShotIndex}: inquadra le monete e premi ◉";
    }

    /// <summary>
    /// Lo scatto: il motore parte e si chiedono alcuni fotogrammi della stessa inquadratura; ognuno è analizzato e la stessa moneta vista
    /// in più fotogrammi tiene il ritaglio migliore. Poi la telecamera si chiude, si riconoscono i valori e si apre la pila delle superfici.
    /// </summary>
    private async Task TakeShotAsync()
    {
        if (_controller is null || !_shotPreview || _shotRunning) return;
        SetShotState(preview: true, running: true);
        var engine = _controller.Engine;
        _controller.Log.Event("ui_single_shot", new { shots = SingleShotCount, series = _shotSeries }, important: true);
        StatusText = "Tieni fermo il telefono…";
        engine.RequestShots(SingleShotCount);
        engine.Paused = false;
        var started = Environment.TickCount64;
        while (_state == State.Capturing && engine.PendingShots > 0 && Environment.TickCount64 - started < SingleShotTimeoutMs)
            await Task.Delay(100);
        _controller?.Log.Event("ui_single_shot_done", new { missing = engine.PendingShots, ms = Environment.TickCount64 - started }, important: true);
        engine.RequestShots(0);
        SetShotState(preview: false, running: false);
        if (_state == State.Capturing) await StopAsync(openSeries: _shotSeries);
    }

    /// <summary>Esce dall'inquadratura senza scattare: lo scatto vuoto si elimina (e la sessione a scatti, se non ne ha altri).</summary>
    private async Task CancelShotAsync()
    {
        if (_controller is not { } controller || _shotRunning) return;
        SetShotState(preview: false, running: false);
        _controller = null;
        _lastTracking = null;
        Hint = null;
        _timer?.Stop();
        LockPortrait(false);
        DeviceDisplay.Current.KeepScreenOn = false;
        try
        {
            await controller.StopAsync();
            if (controller.Session.SeriesId is { } series) await SessionServices.DeleteShotAsync(controller.Session.Id, series);
            else await SessionServices.DeleteSessionAsync(controller.Session.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Annullamento dello scatto: {ex}");
        }
        _view?.Clear();
        SetState(State.Idle);
        StatusText = "Tocca ▶ e muovi lentamente il telefono sopra il tavolo";
    }

    private async Task ToggleAsync()
    {
        if (_state == State.Idle) await StartAsync(null, paused: false);
        else if (_state == State.Capturing) await StopAsync();
    }

    private async Task StartAsync(string? seriesId = null, bool paused = false)
    {
        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
        {
            await ShowAlert("Permesso negato", "Per contare le monete serve il permesso di usare la fotocamera.");
            return;
        }

        SetState(State.Starting);
        StatusText = "Avvio della fotocamera…";
        _view?.Clear();
        try
        {
            _controller = await CaptureController.StartLiveAsync(seriesId, paused);
            AttachEngine(_controller);
            SetState(State.Capturing);
            StatusText = _controller.Note ?? "Muovi lentamente il telefono, parallelo al tavolo";
            LockPortrait(true);
            DeviceDisplay.Current.KeepScreenOn = true;
        }
        catch (Exception ex)
        {
            _controller = null;
            SetState(State.Idle);
            StatusText = "Tocca ▶ per riprovare";
            await ShowAlert("Impossibile avviare", ex.Message);
        }
    }

    private void AttachEngine(CaptureController controller)
    {
        var engine = controller.Engine;
        CoinCount = 0;
        _lastTracking = null;      // niente avvisi rimasti dalla sessione precedente (es. dallo scatto prima)
        Hint = null;
        _loggedHint = null;
        _lastError = null;
        _gridScale = null;
        _scaleDirty = true;
        MarkerCount = 0;
        _lastMarkerCount = 0;
        _watchTick = 0;
        _torchOn = false;
        OnPropertyChanged(nameof(TorchSymbol));
        while (_changedTiles.TryDequeue(out _)) { }
        if (_view is not null) _view.Source = engine.Canvas;

        engine.TrackingUpdated += u => _lastTracking = u;
        engine.MosaicChanged += tiles => _changedTiles.Enqueue(tiles);
        engine.CoinsChanged += _ => _coinsChanged = true;
        engine.Error += ex =>
        {
            System.Diagnostics.Debug.WriteLine($"Errore motore: {ex}");
            _lastError = ex.Message;
            Interlocked.Exchange(ref _lastErrorTick, Environment.TickCount64);
        };

        _timer ??= Application.Current!.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Aggiornamento dell'interfaccia a ritmo fisso: i motori lavorano su altri thread e non toccano la UI.</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (_view is null || _controller is null) return;
        var engine = _controller.Engine;

        var changed = new HashSet<TileKey>();
        while (_changedTiles.TryDequeue(out var tiles)) changed.UnionWith(tiles);

        if (_coinsChanged)
        {
            _coinsChanged = false;
            var tracks = engine.Coins.Tracks;
            _view.Boxes = tracks.Select(t => new OverlayBox(t.MosaicBox, t.IsStable)).ToList();
            CoinCount = tracks.Count;
            _scaleDirty = true;
        }

        // aspetto della superficie e griglia metrica (impostazioni)
        _view.SurfaceStyle = SettingsService.SurfaceLook;
        UpdateGrid(engine);

        if (_lastTracking is { } update)
        {
            var capturing = _state == State.Capturing;
            _view.FrameQuad = capturing ? update.FrameQuad : null;
            var lost = capturing && update.Status == TrackStatus.Lost;
            _view.GhostQuad = lost ? update.LastKnownQuad : null;
            _view.EstimatedQuad = lost ? update.EstimatedQuad : null;
            UpdateRecovery(lost ? update.Recovery : null, lost && update.Featureless);
            LastGoodImage = lost ? _lastGoodHolder : null;

            var shown = update.Hint ?? (update.PendingDetections > 3 ? "Analisi in corso: rallenta un po'" : null);
            // monete troppo piccole nell'inquadratura: il taglio si legge male (1 cent scambiati per 2 cent)
            if (shown is null && capturing && engine.LastCoinDiameterPx is > 0 and < CaptureEngine.SmallCoinDiameterPx)
                shown = "Avvicina il telefono (circa 25 cm): le monete sono piccole e il valore si legge male";
            if (lost && update.Recovery is not null)
                shown = update.Featureless
                    ? "Tavolo vuoto: torna dove vedi le monete (o tocca ↻ per ricominciare)"
                    : "Posizione persa: segui la freccia e cerca la zona nella foto (o tocca ↻ per ricominciare)";
            if (_shotRunning && !lost) shown = "📷 Tieni fermo il telefono: scatto in corso…";
            Hint = StickyHint(shown, important: lost);

            // immagini sfocate con luce sufficiente: prova a rimettere a fuoco (al massimo ogni 5 secondi)
            if (capturing && update.Blurry && !update.Dark && Environment.TickCount64 - _lastRefocusTick > 5000)
            {
                _lastRefocusTick = Environment.TickCount64;
                _controller.Refocus();
            }
        }

        // la fotocamera ha smesso di mandare immagini (è successo con ARCore): lo si dice invece di lasciare la schermata ferma
        if (_state == State.Capturing)
        {
            var latest = _controller.LatestFrame;
            var now = Environment.TickCount64;
            if (_watchTick == 0 || !ReferenceEquals(latest, _watchFrame))
            {
                _watchFrame = latest;
                _watchTick = now;
            }
            else if (now - _watchTick > 3000)
            {
                Hint = SettingsService.CaptureMethod == CaptureMethod.OpenCv
                    ? "La fotocamera non manda immagini: ferma e riprova"
                    : "ARCore non manda immagini: ferma e scegli il metodo OpenCV nelle impostazioni";
            }
        }

        // un errore del motore si vede per qualche secondo, invece di lasciare la schermata ferma senza spiegazioni
        if (_lastError is { } error && Environment.TickCount64 - Interlocked.Read(ref _lastErrorTick) < 6000)
            Hint = $"Errore: {error}";

        // il registro sa cosa ha visto l'operatore: ogni suggerimento mostrato, e quando è sparito
        if (Hint != _loggedHint)
        {
            _loggedHint = Hint;
            _controller.Log.Event("ui_hint", new { text = Hint, status = _lastTracking?.Status });
        }

        // l'ultima inquadratura agganciata (una al secondo): da persi serve a ritrovare la zona a occhio
        if (_state == State.Capturing && _lastTracking is { Status: TrackStatus.Tracking } && _controller.LatestFrame is { } good &&
            Environment.TickCount64 - _lastGoodTick > 1000)
        {
            _lastGoodTick = Environment.TickCount64;
            var jpeg = AndroidImaging.ToJpeg(ImageOps.Downscale(good.Image, 200), 70);
            _lastGoodHolder = ImageSource.FromStream(() => new MemoryStream(jpeg));
            LastGoodRotation = good.RotationDegrees;
        }

        // anteprima della fotocamera: l'ultimo fotogramma, ridotto (solo durante l'acquisizione)
        var frame = _state == State.Capturing ? _controller.LatestFrame : null;
        if (!ReferenceEquals(frame, _latestFrame))
        {
            _latestFrame = frame;
            // mentre si inquadra lo scatto l'anteprima è a tutto schermo: serve più grande
            _view.Preview = frame is null ? null : ImageOps.Downscale(frame.Image, _shotPreview ? 960 : 240);
            _view.PreviewRotation = frame?.RotationDegrees ?? 0;
        }

        _view.Refresh(changed.Count > 0 ? changed.ToList() : Array.Empty<TileKey>());
    }

    /// <summary>I suggerimenti passeggeri (es. «più lentamente») restano visibili almeno 1,5 s: prima comparivano e sparivano a ogni fotogramma.</summary>
    private string? StickyHint(string? candidate, bool important)
    {
        var now = Environment.TickCount64;
        if (candidate is not null)
        {
            if (candidate != _stickyText) { _stickyText = candidate; _stickySince = now; }
            _stickyLastSeen = now;
            return candidate;
        }
        if (!important && _stickyText is not null && now - _stickySince < 1500 && now - _stickyLastSeen < 1500) return _stickyText;
        _stickyText = null;
        return null;
    }

    /// <summary>La freccia che dice come muovere il telefono per riagganciarsi, con la distanza (in cm se la scala è nota).</summary>
    private void UpdateRecovery(RecoveryGuidance? guidance, bool featureless)
    {
        HasRecovery = guidance is not null || featureless;
        if (guidance is null && !featureless) return;
        if (featureless)
        {
            // niente da riconoscere qui: la freccia non ha senso, meglio dire cosa fare e mostrare dove tornare
            if (guidance is not null) ArrowRotation = guidance.ArrowDegrees;
            RecoveryText = "Qui il tavolo è vuoto: non ci sono punti da riconoscere. Torna indietro verso le monete già riprese (guarda la foto)";
            return;
        }
        if (guidance is null) return;

        ArrowRotation = guidance.ArrowDegrees;
        string distance;
        if (_gridScale is { } scale && scale.PxPerMm > 0)
        {
            var cm = guidance.DistancePx / (scale.PxPerMm * 10);
            distance = cm < 3 ? "di poco" : $"di circa {Math.Round(cm / 5) * 5:0} cm";
        }
        else
        {
            var frames = guidance.DistanceInFrames;
            distance = frames < 0.35 ? "di poco" : frames < 0.9 ? "di circa mezza inquadratura" : frames < 1.6 ? "di circa un'inquadratura" : "di più inquadrature";
        }
        RecoveryText = $"Sposta il telefono {guidance.DirectionWords} {distance}" + (guidance.Tracked ? "" : " · stima incerta");
    }

    /// <summary>
    /// Griglia metrica: serve la scala. Con ARCore è misurata; con OpenCV si stima dalle monete riconosciute (almeno 3) e si
    /// riaggiorna quando ne arrivano di nuove. Finché non è nota la legenda lo dice.
    /// </summary>
    private void UpdateGrid(CaptureEngine engine)
    {
        if (_view is null) return;

        // la scala serve anche alla freccia di ritorno (distanza in cm), non solo alla griglia
        var markerCount = engine.Markers.Count;
        MarkerCount = markerCount;
        if (_scaleDirty || engine.PxPerMm is not null || markerCount != _lastMarkerCount)
        {
            _scaleDirty = false;
            _lastMarkerCount = markerCount;
            _gridScale = SurfaceScale.FromTracks(engine.PxPerMm, engine.Coins.Tracks, engine.Markers.Landmarks);
        }

        var grid = SettingsService.Grid;
        if (grid.IsOff)
        {
            _view.GridPxPerCm = 0;
            _view.GridLabel = string.Empty;
            return;
        }

        _view.GridMinorCm = grid.MinorCm;
        _view.GridMajorCm = grid.MajorCm;
        if (_gridScale is { } scale)
        {
            _view.GridPxPerCm = (float)(scale.PxPerMm * 10);
            _view.GridLabel = $"Griglia: {grid.Label} · {scale.Label}";
        }
        else
        {
            _view.GridPxPerCm = 0;
            _view.GridLabel = "Griglia: scala non ancora nota (servono almeno 3 monete riconosciute)";
        }
    }

    private async Task StopAsync(string? openSeries = null)
    {
        if (_controller is null) return;
        _controller.Log.Event("ui_stop", new { status = _lastTracking?.Status, keyframes = _lastTracking?.Keyframes }, important: true);
        SetState(State.Stopping);
        StatusText = "Ultime monete in analisi…";
        Hint = null;
        LockPortrait(false);
        DeviceDisplay.Current.KeepScreenOn = false;
        try
        {
            await _controller.StopAsync();
            OnTick(null, EventArgs.Empty);
            if (_view is not null) _view.FrameQuad = null;
            await ProcessAndOpenAsync(_controller.Session.Id, openSeries);
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore", $"La sessione non è stata completata: {ex.Message}");
            Finish();
        }
    }

    /// <summary>
    /// «Ricomincia»: ferma e salva la parte acquisita (resta una sessione a sé, riconosciuta in secondo piano) e avvia
    /// subito una nuova acquisizione. Non si perde nulla di quanto già coperto.
    /// </summary>
    private async Task RestartAsync()
    {
        if (_state != State.Capturing || _controller is null) return;
        var previous = _controller;
        previous.Log.Event("ui_restart", new { status = _lastTracking?.Status, keyframes = _lastTracking?.Keyframes, hint = Hint }, important: true);
        SetState(State.Stopping);
        StatusText = "Salvo la parte acquisita…";
        Hint = null;
        try
        {
            await previous.StopAsync();
            OnTick(null, EventArgs.Empty);
            if (_view is not null) { _view.FrameQuad = null; _view.GhostQuad = null; }
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore", $"La sessione non è stata salvata: {ex.Message}");
            Finish();
            return;
        }

        _controller = null;
        _timer?.Stop();
        var partialId = previous.Session.Id;
        _ = Task.Run(async () =>
        {
            try { await SessionProcessing.ClassifyAsync(partialId, null, CancellationToken.None); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Riconoscimento della sessione parziale: {ex}"); }
        });

        SetState(State.Idle);
        await StartAsync();
    }

    private async Task ImportPhotoAsync()
    {
        try
        {
            var photo = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "Foto con le monete" });
            if (photo is null) return;
            await using var stream = await photo.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);

            SetState(State.Stopping);
            StatusText = "Ricerca delle monete nella foto…";
            _view?.Clear();
            _controller = await CaptureController.StartPhotoAsync(memory.ToArray());
            AttachEngine(_controller);
            await _controller.StopAsync();
            OnTick(null, EventArgs.Empty);
            await ProcessAndOpenAsync(_controller.Session.Id);
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore", $"Impossibile analizzare la foto: {ex.Message}");
            Finish();
        }
    }

    /// <summary>Riconoscimento del valore di ogni ritaglio, poi il riepilogo.</summary>
    private async Task ProcessAndOpenAsync(string sessionId, string? openSeries = null)
    {
        SetState(State.Processing);
        _processingCts = new CancellationTokenSource();
        StatusText = "Riconoscimento del valore delle monete…";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            ProgressText = p.Total == 0 ? "Nessuna moneta da riconoscere" : $"Monete riconosciute: {p.Done} di {p.Total}";
        });

        try
        {
            await SessionProcessing.ClassifyAsync(sessionId, progress, _processingCts.Token);
        }
        catch (OperationCanceledException)
        {
            await ShowAlert("Interrotto", "Il riconoscimento è stato interrotto: puoi completarlo dall'elenco delle sessioni.");
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore nel riconoscimento", ex.Message);
        }
        finally
        {
            Finish();
        }

        if (openSeries is not null)
        {
            // lo scatto si somma alla sessione a scatti: si apre il suo riepilogo e sopra la pila delle superfici, sull'ultimo scatto
            await SessionServices.UpdateShotSessionAsync(openSeries);
            await Shell.Current.GoToAsync($"session?id={Uri.EscapeDataString(openSeries)}", false);
            await Shell.Current.GoToAsync($"surface?series={Uri.EscapeDataString(openSeries)}");
        }
        else
            await Shell.Current.GoToAsync($"session?id={Uri.EscapeDataString(sessionId)}");
    }

    private void Finish()
    {
        _timer?.Stop();
        _controller = null;
        _processingCts = null;
        Progress = 0;
        ProgressText = string.Empty;
        SetState(State.Idle);
        StatusText = "Tocca ▶ per una nuova sessione";
    }

    private void SetState(State state)
    {
        var wasFullScreen = _state is not State.Idle;
        _state = state;
        OnPropertyChanged(nameof(ToggleSymbol));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(ShowCounter));
        ((Command)ToggleCommand).ChangeCanExecute();
        ((Command)ImportPhotoCommand).ChangeCanExecute();
        ((Command)SingleShotCommand).ChangeCanExecute();
        ((Command)RestartCommand).ChangeCanExecute();
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanTorch));
        var fullScreen = state is not State.Idle;
        if (fullScreen != wasFullScreen) FullScreenChanged?.Invoke(fullScreen);
    }

    private static void LockPortrait(bool locked)
    {
        if (Platform.CurrentActivity is { } activity)
            activity.RequestedOrientation = locked ? Android.Content.PM.ScreenOrientation.Portrait : Android.Content.PM.ScreenOrientation.Unspecified;
    }
}
