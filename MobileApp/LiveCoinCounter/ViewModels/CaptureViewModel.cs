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
        CancelProcessingCommand = new Command(() => _processingCts?.Cancel());
        RestartCommand = new Command(async () => await RestartAsync(), () => _state == State.Capturing);
    }

    public ICommand ToggleCommand { get; }

    /// <summary>Salva la parte acquisita come sessione a sé e ricomincia da capo (utile quando il tracking è perso).</summary>
    public ICommand RestartCommand { get; }
    public bool CanRestart => _state == State.Capturing;
    public ICommand ImportPhotoCommand { get; }
    public ICommand CancelProcessingCommand { get; }

    /// <summary>Il pulsante alterna i simboli start e stop.</summary>
    public string ToggleSymbol => _state is State.Capturing or State.Stopping ? "■" : "▶";

    public bool IsIdle => _state == State.Idle;
    public bool IsCapturing => _state is State.Capturing or State.Stopping or State.Starting;
    public bool IsProcessing => _state is State.Processing or State.Stopping;
    public bool ShowCounter => _state != State.Idle;

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

    public string CoinCountText => CoinCount == 1 ? "1 moneta trovata" : $"{CoinCount} monete trovate";

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
                if (choice == "Elimina") await SessionServices.DeleteSessionAsync(session.Id);
                else if (choice == "Completa la sessione") { await ProcessAndOpenAsync(session.Id); return; }
                else return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Controllo sessioni interrotte: {ex}");
        }
    }

    private async Task ToggleAsync()
    {
        if (_state == State.Idle) await StartAsync();
        else if (_state == State.Capturing) await StopAsync();
    }

    private async Task StartAsync()
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
            _controller = await CaptureController.StartLiveAsync();
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
        Hint = null;
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
        }

        if (_lastTracking is { } update)
        {
            var capturing = _state == State.Capturing;
            _view.FrameQuad = capturing ? update.FrameQuad : null;
            _view.GhostQuad = capturing && update.Status == TrackStatus.Lost ? update.LastKnownQuad : null;
            Hint = update.Hint ?? (update.PendingDetections > 3 ? "Analisi in corso: rallenta un po'" : null);
        }

        // un errore del motore si vede per qualche secondo, invece di lasciare la schermata ferma senza spiegazioni
        if (_lastError is { } error && Environment.TickCount64 - Interlocked.Read(ref _lastErrorTick) < 6000)
            Hint = $"Errore: {error}";

        // anteprima della fotocamera: l'ultimo fotogramma, ridotto (solo durante l'acquisizione)
        var frame = _state == State.Capturing ? _controller.LatestFrame : null;
        if (!ReferenceEquals(frame, _latestFrame))
        {
            _latestFrame = frame;
            _view.Preview = frame is null ? null : ImageOps.Downscale(frame.Image, 240);
            _view.PreviewRotation = frame?.RotationDegrees ?? 0;
        }

        _view.Refresh(changed.Count > 0 ? changed.ToList() : Array.Empty<TileKey>());
    }

    private async Task StopAsync()
    {
        if (_controller is null) return;
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
            await ProcessAndOpenAsync(_controller.Session.Id);
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
    private async Task ProcessAndOpenAsync(string sessionId)
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
        ((Command)RestartCommand).ChangeCanExecute();
        OnPropertyChanged(nameof(CanRestart));
        var fullScreen = state is not State.Idle;
        if (fullScreen != wasFullScreen) FullScreenChanged?.Invoke(fullScreen);
    }

    private static void LockPortrait(bool locked)
    {
        if (Platform.CurrentActivity is { } activity)
            activity.RequestedOrientation = locked ? Android.Content.PM.ScreenOrientation.Portrait : Android.Content.PM.ScreenOrientation.Unspecified;
    }
}
