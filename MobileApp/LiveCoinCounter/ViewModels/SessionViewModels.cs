using System.Collections.ObjectModel;
using System.Windows.Input;
using LiveCoinCounter.Models;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Sessions;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>Azioni comuni su una sessione (rinomina, esporta, elimina), usate dall'elenco e dal riepilogo.</summary>
internal static class SessionActions
{
    public static readonly System.Globalization.CultureInfo Italian = new("it-IT");

    public static async Task<bool> RenameAsync(SessionRecord session)
    {
        var page = Shell.Current?.CurrentPage;
        if (page is null) return false;
        var name = await page.DisplayPromptAsync("Rinomina", "Nome della sessione", "Salva", "Annulla", initialValue: session.Name, maxLength: 80);
        if (string.IsNullOrWhiteSpace(name)) return false;
        await SessionServices.Repository.RenameAsync(session.Id, name);
        session.Name = name.Trim();
        return true;
    }

    public static async Task<bool> DeleteAsync(SessionRecord session)
    {
        var page = Shell.Current?.CurrentPage;
        if (page is null) return false;
        var ok = await page.DisplayAlertAsync("Elimina sessione",
            $"Eliminare «{session.Name}»? Si cancellano anche mosaico e ritagli delle monete.", "Elimina", "Annulla");
        if (!ok) return false;
        await SessionServices.DeleteSessionAsync(session.Id);
        return true;
    }

    public static Task ExportAsync(SessionRecord session) => SessionStorage.ExportAsync(session);

    /// <summary>Lo stato di una moneta com'è ora, in forma adatta al registro.</summary>
    public static object Snapshot(CoinRecord c) => new
    {
        state = c.State.ToString(), cents = c.Cents, confidence = c.ClassConfidence, corrected = c.CorrectedCents,
        verified = c.Verified, kind = c.TruthKind, note = c.TruthNote, manual = c.IsManual
    };

    /// <summary>
    /// Dopo ogni modifica dell'utente a una moneta: ricalcola i totali, segna la sessione come verificata, scrive l'evento
    /// (prima e dopo) nel registro e riscrive l'istantanea, così le correzioni arrivano sul PC con il resto dei dati.
    /// </summary>
    public static async Task AfterCoinEditAsync(CoinRecord coin, string action, object? before)
    {
        var repository = SessionServices.Repository;
        var session = await repository.RefreshTotalsAsync(coin.SessionId);
        if (session is not null)
        {
            session.ReviewedAt = DateTime.Now;
            await repository.UpdateSessionAsync(session);
        }
        await SessionDiagnostics.AppendAsync(coin.SessionId, "coin_edit", new { action, coin = coin.Number, before, after = Snapshot(coin) });
        await SessionServices.WriteSnapshotAsync(coin.SessionId);
        // uno scatto: anche la sessione a scatti è verificata e ha l'istantanea aggiornata
        if (session?.SeriesId is { } parent && await repository.GetSessionAsync(parent) is { } shots)
        {
            shots.ReviewedAt = DateTime.Now;
            await repository.UpdateSessionAsync(shots);
            await SessionServices.WriteSnapshotAsync(parent);
        }
    }

    public static string Format(int cents) => $"{cents / 100},{cents % 100:D2} €";
}

/// <summary>Una riga dell'elenco delle sessioni.</summary>
public sealed class SessionItem
{
    public SessionItem(SessionRecord s, int shots = 0)
    {
        Record = s;
        Shots = shots;
        Thumbnail = s.MosaicPreviewPath is { } p && File.Exists(p) ? ImageSource.FromFile(p) : null;
    }

    public SessionRecord Record { get; }

    /// <summary>Sessione a scatti: quanti scatti.</summary>
    public int Shots { get; }
    public string Id => Record.Id;
    public string Name => Record.Name;
    public string DateText => Record.StartedAt.ToString("ddd d MMM yyyy, HH:mm", SessionActions.Italian);
    public string TotalText => Record.Status == SessionStatus.Completed ? SessionActions.Format(Record.TotalCents) : "—";
    public string DetailText => Record.Status switch
    {
        SessionStatus.Completed => $"{Record.CoinCount} monete" + (Record.UncertainCount > 0 ? $" · {Record.UncertainCount} incerte" : "") +
                                   (Record.Source == SessionSource.Photo ? " · da foto" : "") +
                                   (Record.Source == SessionSource.Shots ? (Shots == 1 ? " · 1 scatto" : $" · {Shots} scatti") : ""),
        SessionStatus.Aborted => "Annullata",
        _ => "Da completare"
    };
    public ImageSource? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;
}

public sealed class SessionsViewModel : ObservableObject
{
    private bool _isEmpty;

    public SessionsViewModel()
    {
        OpenCommand = new Command<SessionItem>(async item => await Shell.Current.GoToAsync($"session?id={Uri.EscapeDataString(item.Id)}"));
        MoreCommand = new Command<SessionItem>(async item => await MoreAsync(item));
    }

    public ObservableCollection<SessionItem> Sessions { get; } = new();
    public ICommand OpenCommand { get; }
    public ICommand MoreCommand { get; }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    public async Task LoadAsync()
    {
        var list = await SessionServices.Repository.ListSessionsAsync();
        if (await SessionServices.AdoptOrphanShotsAsync(list) > 0) list = await SessionServices.Repository.ListSessionsAsync();
        var shots = list.Where(s => s.SeriesId is not null).GroupBy(s => s.SeriesId!).ToDictionary(g => g.Key, g => g.Count());
        Sessions.Clear();
        foreach (var s in list.Where(s => s.SeriesId is null)) Sessions.Add(new SessionItem(s, shots.GetValueOrDefault(s.Id)));
        IsEmpty = Sessions.Count == 0;
    }

    private async Task MoreAsync(SessionItem item)
    {
        var page = Shell.Current?.CurrentPage;
        if (page is null) return;
        var choice = await page.DisplayActionSheetAsync(item.Name, "Annulla", "Elimina", "Rinomina", "Esporta");
        var changed = choice switch
        {
            "Rinomina" => await SessionActions.RenameAsync(item.Record),
            "Elimina" => await SessionActions.DeleteAsync(item.Record),
            "Esporta" => await ExportAsync(item.Record),
            _ => false
        };
        if (changed) await LoadAsync();
    }

    private static async Task<bool> ExportAsync(SessionRecord session)
    {
        await SessionActions.ExportAsync(session);
        return false;
    }
}

/// <summary>Riepilogo di una sessione: totale, monete per taglio, mosaico con i riquadri.</summary>
public sealed class SessionSummaryViewModel : ObservableObject, IQueryAttributable
{
    private SessionRecord? _session;
    private List<CoinRecord> _coins = new();
    private ImageSource? _mosaicImage;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private SurfaceOptions? _options;
    private int _surfaceVersion;
    private List<SessionRecord> _shots = new();
    private int _shot;

    public SessionSummaryViewModel()
    {
        OpenSurfaceCommand = new Command(async () =>
        {
            if (_sessionId is null) return;
            if (IsShots) await Shell.Current.GoToAsync($"surface?series={Uri.EscapeDataString(_sessionId)}&index={_shot}");
            else await Shell.Current.GoToAsync($"surface?id={Uri.EscapeDataString(_sessionId)}");
        });
        PreviousShotCommand = new Command(async () => await ShowShotAsync(_shot - 1), () => _shot > 0);
        NextShotCommand = new Command(async () => await ShowShotAsync(_shot + 1), () => _shot < _shots.Count - 1);
        AnotherShotCommand = new Command(async () =>
        {
            if (_sessionId is null) return;
            CaptureViewModel.PendingShotSeries = _sessionId;
            await Shell.Current.GoToAsync("//CapturePage");
        });
        AllCorrectCommand = new Command(async () => await AllCorrectAsync(), () => !IsBusy);
        NoteCommand = new Command(async () => await EditNoteAsync());
        ShowCoinsCommand = new Command(async () => await OpenCoinsAsync(false));
        ShowUncertainCommand = new Command(async () => await OpenCoinsAsync(true));
        RetryAiCommand = new Command(async () => await RetryAiAsync(), () => !IsBusy);
        CompleteCommand = new Command(async () => await CompleteAsync(), () => !IsBusy);
        RenameCommand = new Command(async () => { if (_session is not null && await SessionActions.RenameAsync(_session)) OnPropertyChanged(nameof(Name)); });
        ExportCommand = new Command(async () => { if (_session is not null) await SessionActions.ExportAsync(_session); });
        DeleteCommand = new Command(async () =>
        {
            if (_session is not null && await SessionActions.DeleteAsync(_session)) await Shell.Current.GoToAsync("..");
        });
    }

    /// <summary>La superficie acquisita a tutto schermo, da ingrandire.</summary>
    public ICommand OpenSurfaceCommand { get; }

    /// <summary>Sessione a scatti: la superficie dello scatto precedente / successivo.</summary>
    public ICommand PreviousShotCommand { get; }
    public ICommand NextShotCommand { get; }

    /// <summary>Sessione a scatti: un altro scatto, su un'altra parte del tavolo.</summary>
    public ICommand AnotherShotCommand { get; }

    /// <summary>Una sessione a scatti: le monete sono la somma degli scatti, una superficie per scatto.</summary>
    public bool IsShots => _shots.Count > 0;

    /// <summary>«Superficie k di n».</summary>
    public string ShotPositionText => IsShots ? $"Superficie {_shot + 1} di {_shots.Count}" : string.Empty;

    /// <summary>Le monete dello scatto mostrato.</summary>
    public string ShotTotalText
    {
        get
        {
            if (!IsShots) return string.Empty;
            var shot = _shots[_shot];
            return $"In questo scatto: {shot.CoinCount} monete, {SessionActions.Format(shot.TotalCents)}";
        }
    }

    /// <summary>Swipe sulla superficie: verso sinistra la successiva, verso destra la precedente.</summary>
    public void Swipe(bool towardsLeft)
    {
        var command = towardsLeft ? NextShotCommand : PreviousShotCommand;
        if (IsShots && command.CanExecute(null)) command.Execute(null);
    }

    private async Task ShowShotAsync(int index)
    {
        if (index < 0 || index >= _shots.Count || index == _shot) return;
        _shot = index;
        ConfigureOptions();
        NotifyShots();
        await RenderSurfaceAsync();
    }

    private void NotifyShots()
    {
        foreach (var p in new[] { nameof(IsShots), nameof(ShotPositionText), nameof(ShotTotalText) }) OnPropertyChanged(p);
        ((Command)PreviousShotCommand).ChangeCanExecute();
        ((Command)NextShotCommand).ChangeCanExecute();
    }

    /// <summary>La superficie mostrata: la sessione stessa o lo scatto scelto, con le sue monete.</summary>
    private (SessionRecord Session, List<CoinRecord> Coins)? Surface() =>
        _session is null ? null
        : IsShots ? (_shots[_shot], _coins.Where(c => c.SessionId == _shots[_shot].Id).ToList())
        : (_session, _coins);

    private void ConfigureOptions()
    {
        if (_options is not null && Surface() is { } surface) _options.Configure(SessionServices.ScaleOf(surface.Session, surface.Coins));
    }

    /// <summary>Cosa mostrare della superficie (vista, riquadri, griglia): valgono anche a tutto schermo.</summary>
    public SurfaceOptions? Options => _options;

    /// <summary>La pagina esce di scena: si smette di ascoltare le opzioni (le riprende il prossimo caricamento).</summary>
    public void Detach()
    {
        if (_options is not null) _options.Changed -= OnOptionsChanged;
    }

    private void OnOptionsChanged() => _ = RenderSurfaceAsync();

    private async Task RenderSurfaceAsync()
    {
        if (_options is null || Surface() is not { } surface) return;
        var (session, coins) = surface;
        var render = _options.ToRender();
        var mine = ++_surfaceVersion;
        var annotated = await Task.Run(() => ImageHelper.DrawSessionMosaic(session, coins, render));
        if (mine != _surfaceVersion) return;      // nel frattempo l'utente ha cambiato un'altra opzione: vale l'ultima
        MosaicImage = annotated is null ? null : ImageSource.FromStream(() => new MemoryStream(annotated));
    }

    /// <summary>«Tutte le monete sono giuste»: conferma in blocco i riconoscimenti non ancora controllati.</summary>
    public ICommand AllCorrectCommand { get; }
    public ICommand NoteCommand { get; }
    public ICommand ShowCoinsCommand { get; }
    public ICommand ShowUncertainCommand { get; }
    public ICommand RetryAiCommand { get; }
    public ICommand CompleteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand DeleteCommand { get; }

    public string Name => _session?.Name ?? string.Empty;
    public string DateText => _session is null ? string.Empty : _session.StartedAt.ToString("dddd d MMMM yyyy, HH:mm", SessionActions.Italian);
    public string TotalText => _session is null ? "--,-- €" : SessionActions.Format(_session.TotalCents);
    public int CoinCount => _session?.CoinCount ?? 0;
    public int UncertainCount => _session?.UncertainCount ?? 0;
    public bool HasUncertain => UncertainCount > 0;
    public bool IsCompleted => _session?.Status == SessionStatus.Completed;
    public bool NeedsCompletion => _session is not null && !IsCompleted;

    public string UncertainText => UncertainCount == 1
        ? "1 moneta ha un riconoscimento incerto: controllala nell'elenco."
        : $"{UncertainCount} monete hanno un riconoscimento incerto: controllale nell'elenco.";

    public bool CanRetryAi => HasUncertain && SettingsService.AiMode != AiMode.OfflineOnly && !IsBusy;

    /// <summary>Quante monete l'utente ha già controllato, e la sua nota sulla prova.</summary>
    public string ReviewText
    {
        get
        {
            if (_session is null) return string.Empty;
            var verified = _coins.Count(c => c.Verified && !c.IsManual);
            var detected = _coins.Count(c => !c.IsManual);
            var manual = _coins.Count(c => c.IsManual);
            var text = _session.ReviewedAt is { } at
                ? $"Verificata il {at:dd/MM HH:mm}: {verified} di {detected} monete controllate"
                : $"Non ancora verificata ({detected} monete da controllare)";
            if (manual > 0) text += $", {manual} aggiunte a mano";
            if (!string.IsNullOrWhiteSpace(_session.Notes)) text += $"\nNota: {_session.Notes}";
            return text;
        }
    }

    public bool CanMarkAllCorrect => _coins.Any(c => c.State == CoinState.Classified && !c.Verified);

    public string MethodText => _session is null ? string.Empty :
        (_session.Source switch
        {
            SessionSource.Photo => "Da foto",
            SessionSource.Shots => $"{_shots.Count} scatti ({_session.TrackerId}), {_session.KeyframeCount} fotogrammi chiave",
            _ => $"Mosaico: {_session.TrackerId}, {_session.KeyframeCount} fotogrammi chiave"
        }) + $" · riconoscimento: {_session.ClassifierId ?? "—"}";

    public List<CoinResult> Denominations { get; private set; } = new();

    public ImageSource? MosaicImage
    {
        get => _mosaicImage;
        private set { SetProperty(ref _mosaicImage, value); OnPropertyChanged(nameof(HasMosaic)); }
    }

    public bool HasMosaic => MosaicImage is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanRetryAi));
            ((Command)RetryAiCommand).ChangeCanExecute();
            ((Command)CompleteCommand).ChangeCanExecute();
            ((Command)AllCorrectCommand).ChangeCanExecute();
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    private string? _sessionId;

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
        _shots = await SessionServices.Repository.ListShotsAsync(_sessionId);
        if (_shot >= _shots.Count) _shot = Math.Max(0, _shots.Count - 1);
        NotifyShots();

        Denominations = _coins.Where(c => c.EffectiveCents is not null)
            .GroupBy(c => c.EffectiveCents!.Value)
            .OrderByDescending(g => g.Key)
            .Select(g => new CoinResult { Denomination = EuroCoins.NameOf(g.Key), ValueInCents = g.Key, Count = g.Count() })
            .ToList();

        Detach();
        _options = SurfaceOptions.For(_sessionId);
        ConfigureOptions();
        _options.Changed += OnOptionsChanged;
        OnPropertyChanged(nameof(Options));
        await RenderSurfaceAsync();

        foreach (var p in new[] { nameof(Name), nameof(DateText), nameof(TotalText), nameof(CoinCount), nameof(UncertainCount),
                     nameof(HasUncertain), nameof(UncertainText), nameof(CanRetryAi), nameof(MethodText), nameof(Denominations),
                     nameof(IsCompleted), nameof(NeedsCompletion), nameof(ReviewText), nameof(CanMarkAllCorrect) })
            OnPropertyChanged(p);
    }

    private async Task AllCorrectAsync()
    {
        if (_sessionId is null) return;
        var repository = SessionServices.Repository;
        var pending = _coins.Where(c => c.State == CoinState.Classified && !c.Verified).ToList();
        if (pending.Count == 0) return;

        foreach (var coin in pending)
        {
            coin.Verified = true;
            coin.TruthKind = TruthKinds.Ok;
            await repository.UpdateCoinAsync(coin);
        }
        var session = await repository.GetSessionAsync(_sessionId);
        if (session is not null)
        {
            session.ReviewedAt = DateTime.Now;
            await repository.UpdateSessionAsync(session);
        }
        await SessionDiagnostics.AppendAsync(_sessionId, "review_all_ok", new { confirmed = pending.Select(c => c.Number).ToArray() });
        await SessionServices.WriteSnapshotAsync(_sessionId);
        await LoadAsync();
    }

    private async Task EditNoteAsync()
    {
        if (_session is null || _sessionId is null) return;
        var page = Shell.Current?.CurrentPage;
        if (page is null) return;
        var note = await page.DisplayPromptAsync("Nota sulla prova",
            "Cosa è successo? (es. «due gruppi di monete distanti», «luce scarsa»)", "Salva", "Annulla", initialValue: _session.Notes ?? string.Empty, maxLength: 400);
        if (note is null) return;

        _session.Notes = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await SessionServices.Repository.UpdateSessionAsync(_session);
        await SessionDiagnostics.AppendAsync(_sessionId, "session_note", new { note = _session.Notes });
        await SessionServices.WriteSnapshotAsync(_sessionId);
        OnPropertyChanged(nameof(ReviewText));
    }

    private async Task OpenCoinsAsync(bool onlyUncertain)
    {
        if (_sessionId is null) return;
        await Shell.Current.GoToAsync($"coins?id={Uri.EscapeDataString(_sessionId)}&uncertain={onlyUncertain}");
    }

    private async Task RetryAiAsync()
    {
        if (_sessionId is null) return;
        if (!SessionProcessing.HasNetwork())
        {
            await ShowAlert("Nessuna rete", "Per riprovare con l'AI serve una connessione a internet.");
            return;
        }

        IsBusy = true;
        BusyText = "Invio dei ritagli incerti all'AI…";
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => BusyText = $"AI: {p.Done} di {p.Total} monete");
            var count = await SessionProcessing.RetryUncertainWithAiAsync(_sessionId, progress, CancellationToken.None);
            await LoadAsync();
            if (count == 0) await ShowAlert("AI", "L'AI non ha restituito risultati utilizzabili.");
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore AI", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sessione interrotta: classifica le monete già trovate.</summary>
    private async Task CompleteAsync()
    {
        if (_sessionId is null) return;
        IsBusy = true;
        BusyText = "Riconoscimento delle monete…";
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => BusyText = $"Monete riconosciute: {p.Done} di {p.Total}");
            await SessionProcessing.ClassifyAsync(_sessionId, progress, CancellationToken.None);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Una moneta nell'elenco: ritaglio, valore attribuito e confidenza.</summary>
public sealed class CoinItem
{
    public CoinItem(CoinRecord c, int shot = 0)
    {
        Record = c;
        Shot = shot;
        Crop = c.CropPath is { } p && File.Exists(p) ? ImageSource.FromFile(p) : null;
    }

    public CoinRecord Record { get; }
    public int Id => Record.Id;
    public ImageSource? Crop { get; }
    /// <summary>Sessione a scatti: lo scatto della moneta (0 per le altre sessioni).</summary>
    public int Shot { get; }
    public string Title => Record.IsManual ? "Aggiunta a mano" : Shot > 0 ? $"Scatto {Shot} · moneta {Record.Number}" : $"Moneta {Record.Number}";

    public string ValueText => Record.State switch
    {
        CoinState.Rejected => Record.TruthKind == TruthKinds.Duplicate ? "Doppione" : "Non è una moneta",
        CoinState.Detected => "Da riconoscere",
        _ => Record.EffectiveCents is { } c ? EuroCoins.NameOf(c) : "?"
    };

    public double Confidence => Record.State == CoinState.Corrected || Record.Verified ? 1 : Record.ClassConfidence;
    public string ConfidenceText => Record.State switch
    {
        _ when Record.IsManual => "Mancante: aggiunta a mano",
        CoinState.Corrected => "Corretta a mano",
        CoinState.Classified when Record.Verified => $"Verificata ✓ ({Record.ClassConfidence:P0})",
        CoinState.Rejected => "Esclusa dal conteggio",
        CoinState.Detected => "—",
        _ => $"Confidenza {Record.ClassConfidence:P0}"
    };

    public Color ConfidenceColor => Record.State == CoinState.Corrected || Record.Verified ? Color.FromArgb("#2196F3")
        : Record.ClassConfidence < SessionRepository.UncertainThreshold ? Color.FromArgb("#FF9800") : Color.FromArgb("#2ECC71");

    public string SecondText => Record.SecondCents is { } s && Record.State == CoinState.Classified && Record.SecondScore >= 0.05f
        ? $"Seconda ipotesi: {EuroCoins.NameOf(s)} ({Record.SecondScore:P0})" : string.Empty;

    public bool HasSecond => SecondText.Length > 0;
    public string? Note => Record.Note;
    public bool HasNote => !string.IsNullOrEmpty(Record.Note);
}

public sealed class CoinListViewModel : ObservableObject, IQueryAttributable
{
    private string? _sessionId;
    private bool _onlyUncertain;
    private List<CoinRecord> _all = new();
    private Dictionary<string, int> _shotOf = new();

    public CoinListViewModel()
    {
        OpenCommand = new Command<CoinItem>(async item => await Shell.Current.GoToAsync($"coin?id={item.Id}"));
        AddMissingCommand = new Command(async () => await AddMissingAsync());
    }

    public ObservableCollection<CoinItem> Coins { get; } = new();
    public ICommand OpenCommand { get; }

    /// <summary>«Moneta mancante»: l'app non ne ha trovata una che c'era sul tavolo.</summary>
    public ICommand AddMissingCommand { get; }

    private async Task AddMissingAsync()
    {
        var page = Shell.Current?.CurrentPage;
        if (page is null || _sessionId is null) return;
        var choice = await page.DisplayActionSheetAsync("Quale moneta non è stata trovata?", "Annulla", null,
            EuroCoins.All.Select(c => c.Name).ToArray());
        if (choice is null || choice == "Annulla") return;
        var note = await page.DisplayPromptAsync("Dov'era?", "Facoltativo: dove si trovava sul tavolo (es. «in basso a destra, vicino alla 2 euro»)",
            "Aggiungi", "Salta", maxLength: 200);

        var repository = SessionServices.Repository;
        var number = (_all.Count == 0 ? 0 : _all.Max(c => c.Number)) + 1;
        var coin = new CoinRecord
        {
            SessionId = _sessionId, Number = number, State = CoinState.Corrected,
            CorrectedCents = EuroCoins.All.First(c => c.Name == choice).Cents, ClassConfidence = 1,
            IsManual = true, Verified = true, TruthKind = TruthKinds.Manual,
            TruthNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), DetectedAt = DateTime.Now
        };
        await repository.UpsertCoinsAsync(_sessionId, new[] { coin });
        await SessionActions.AfterCoinEditAsync(coin, "add_missing", before: null);
        await LoadAsync();
    }

    public bool OnlyUncertain
    {
        get => _onlyUncertain;
        set { if (SetProperty(ref _onlyUncertain, value)) Filter(); }
    }

    public string SummaryText => $"{_all.Count(c => c.State != CoinState.Rejected)} monete · {_all.Count(c => c.IsUncertain)} incerte";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id)) _sessionId = Uri.UnescapeDataString(id.ToString() ?? string.Empty);
        if (query.TryGetValue("uncertain", out var u) && bool.TryParse(u.ToString(), out var b)) _onlyUncertain = b;
        OnPropertyChanged(nameof(OnlyUncertain));
    }

    public async Task LoadAsync()
    {
        if (_sessionId is null) return;
        _all = await SessionServices.Repository.ListCoinsAsync(_sessionId);
        _shotOf = (await SessionServices.Repository.ListShotsAsync(_sessionId)).ToDictionary(s => s.Id, s => s.ShotIndex);
        Filter();
        OnPropertyChanged(nameof(SummaryText));
    }

    private void Filter()
    {
        Coins.Clear();
        foreach (var c in _all.Where(c => !OnlyUncertain || c.IsUncertain)
                     .OrderBy(c => c.State == CoinState.Rejected).ThenBy(c => c.ClassConfidence).ThenBy(c => c.Number))
            Coins.Add(new CoinItem(c, _shotOf.GetValueOrDefault(c.SessionId)));
    }
}

/// <summary>Dettaglio di una moneta con correzione manuale del taglio.</summary>
public sealed class CoinDetailViewModel : ObservableObject, IQueryAttributable
{
    public const string NotACoin = "Falso rilevamento (non è una moneta)";
    public const string Duplicate = "Doppione (già contata)";

    private int _coinId;
    private CoinRecord? _coin;
    private CoinItem? _item;
    private string? _selectedChoice;
    private string _history = string.Empty;
    private string _truthNote = string.Empty;
    private string _detailsText = string.Empty;

    public CoinDetailViewModel()
    {
        SaveCommand = new Command(async () => await SaveAsync());
        ConfirmCommand = new Command(async () => await ConfirmAsync());
        UndoCommand = new Command(async () => await UndoAsync());
    }

    public ICommand SaveCommand { get; }

    /// <summary>«Il riconoscimento è giusto»: la moneta diventa verificata senza cambiare valore.</summary>
    public ICommand ConfirmCommand { get; }
    public ICommand UndoCommand { get; }

    public IReadOnlyList<string> Choices { get; } = EuroCoins.All.Select(c => c.Name).Append(NotACoin).Append(Duplicate).ToList();

    /// <summary>Nota libera dell'utente su questa moneta (es. «era appoggiata di taglio»).</summary>
    public string TruthNote
    {
        get => _truthNote;
        set => SetProperty(ref _truthNote, value);
    }

    public bool CanConfirm => _coin is { State: CoinState.Classified, Verified: false };

    public CoinItem? Item
    {
        get => _item;
        private set => SetProperty(ref _item, value);
    }

    public string? SelectedChoice
    {
        get => _selectedChoice;
        set => SetProperty(ref _selectedChoice, value);
    }

    /// <summary>Le caratteristiche della rilevazione (le stesse che si vedono nella superficie a tutto schermo).</summary>
    public string DetailsText
    {
        get => _detailsText;
        private set => SetProperty(ref _detailsText, value);
    }

    public string History
    {
        get => _history;
        private set => SetProperty(ref _history, value);
    }

    public bool IsCorrected => _coin is { IsManual: false } && (_coin.State is CoinState.Corrected or CoinState.Rejected || _coin.Verified);

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id) && int.TryParse(id.ToString(), out var v)) _coinId = v;
    }

    public async Task LoadAsync()
    {
        _coin = await SessionServices.Repository.GetCoinAsync(_coinId);
        if (_coin is null) return;
        Item = new CoinItem(_coin);
        var session = await SessionServices.Repository.GetSessionAsync(_coin.SessionId);
        var all = await SessionServices.Repository.ListCoinsAsync(_coin.SessionId);
        DetailsText = CoinDescription.Describe(_coin, session is null ? null : SessionServices.ScaleOf(session, all));
        TruthNote = _coin.TruthNote ?? string.Empty;
        SelectedChoice = _coin.State == CoinState.Rejected ? (_coin.TruthKind == TruthKinds.Duplicate ? Duplicate : NotACoin)
            : _coin.EffectiveCents is { } c ? EuroCoins.NameOf(c) : null;
        OnPropertyChanged(nameof(CanConfirm));

        var history = await SessionServices.Repository.ListClassificationsAsync(_coin.Id);
        History = history.Count == 0
            ? "Nessun riconoscimento registrato."
            : string.Join("\n", history.Select(h =>
                $"• {h.ClassifierId}: {EuroCoins.NameOf(h.Cents)} ({h.Confidence:P0})" +
                (SessionRepository.ParseTopK(h.TopK).Skip(1).FirstOrDefault() is { Cents: > 0, Score: >= 0.05f } second
                    ? $", poi {EuroCoins.NameOf(second.Cents)} ({second.Score:P0})" : "") +
                (string.IsNullOrEmpty(h.Note) ? "" : $" — {h.Note}")));
        OnPropertyChanged(nameof(IsCorrected));
    }

    private async Task SaveAsync()
    {
        if (_coin is null || SelectedChoice is null) return;
        var predicted = _coin.Cents;
        var before = SessionActions.Snapshot(_coin);
        var note = string.IsNullOrWhiteSpace(TruthNote) ? null : TruthNote.Trim();

        string action;
        if (SelectedChoice is NotACoin or Duplicate)
        {
            _coin.State = CoinState.Rejected;
            _coin.CorrectedCents = null;
            _coin.TruthKind = SelectedChoice == Duplicate ? TruthKinds.Duplicate : TruthKinds.NotCoin;
            action = _coin.TruthKind;
        }
        else
        {
            var cents = EuroCoins.All.First(c => c.Name == SelectedChoice).Cents;
            if (predicted == cents && _coin.State != CoinState.Corrected)
            {
                // ha scelto lo stesso valore letto dall'app: è una conferma, non una correzione
                _coin.TruthKind = TruthKinds.Ok;
                _coin.CorrectedCents = null;
                _coin.State = CoinState.Classified;
            }
            else
            {
                _coin.State = CoinState.Corrected;
                _coin.CorrectedCents = cents;
                _coin.TruthKind = TruthKinds.WrongValue;
            }
            action = _coin.TruthKind;
        }
        _coin.Verified = true;
        _coin.TruthNote = note;

        await SessionServices.Repository.UpdateCoinAsync(_coin);
        await SessionActions.AfterCoinEditAsync(_coin, action, before);

        // con la raccolta foto attiva la correzione diventa un'etichetta per il dataset
        if (PhotoCollectionService.Enabled && await SessionServices.LoadCropAsync(_coin) is { } crop)
            PhotoCollectionService.SaveCoinLabel(crop, _coin.SessionId, _coin.Number, _coin.CorrectedCents, predicted, _coin.ClassifierId);

        await Shell.Current.GoToAsync("..");
    }

    private async Task ConfirmAsync()
    {
        if (_coin is not { State: CoinState.Classified }) return;
        var before = SessionActions.Snapshot(_coin);
        _coin.Verified = true;
        _coin.TruthKind = TruthKinds.Ok;
        _coin.TruthNote = string.IsNullOrWhiteSpace(TruthNote) ? null : TruthNote.Trim();
        await SessionServices.Repository.UpdateCoinAsync(_coin);
        await SessionActions.AfterCoinEditAsync(_coin, "confirm", before);
        await Shell.Current.GoToAsync("..");
    }

    private async Task UndoAsync()
    {
        if (_coin is null) return;
        var before = SessionActions.Snapshot(_coin);
        _coin.CorrectedCents = null;
        _coin.Verified = false;
        _coin.TruthKind = null;
        _coin.TruthNote = null;
        _coin.State = _coin.Cents is null ? CoinState.Detected : CoinState.Classified;
        await SessionServices.Repository.UpdateCoinAsync(_coin);
        await SessionActions.AfterCoinEditAsync(_coin, "undo", before);
        await LoadAsync();
    }
}
