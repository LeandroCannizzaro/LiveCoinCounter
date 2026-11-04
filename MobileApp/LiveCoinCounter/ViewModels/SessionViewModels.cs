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

    public static async Task ExportAsync(SessionRecord session)
    {
        var coins = await SessionServices.Repository.ListCoinsAsync(session.Id);
        await SessionStorage.ExportAsync(session, coins);
    }

    public static string Format(int cents) => $"{cents / 100},{cents % 100:D2} €";
}

/// <summary>Una riga dell'elenco delle sessioni.</summary>
public sealed class SessionItem
{
    public SessionItem(SessionRecord s)
    {
        Record = s;
        Thumbnail = s.MosaicPreviewPath is { } p && File.Exists(p) ? ImageSource.FromFile(p) : null;
    }

    public SessionRecord Record { get; }
    public string Id => Record.Id;
    public string Name => Record.Name;
    public string DateText => Record.StartedAt.ToString("ddd d MMM yyyy, HH:mm", SessionActions.Italian);
    public string TotalText => Record.Status == SessionStatus.Completed ? SessionActions.Format(Record.TotalCents) : "—";
    public string DetailText => Record.Status switch
    {
        SessionStatus.Completed => $"{Record.CoinCount} monete" + (Record.UncertainCount > 0 ? $" · {Record.UncertainCount} incerte" : "") +
                                   (Record.Source == SessionSource.Photo ? " · da foto" : ""),
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
        Sessions.Clear();
        foreach (var s in list) Sessions.Add(new SessionItem(s));
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

    public SessionSummaryViewModel()
    {
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

    public string MethodText => _session is null ? string.Empty :
        $"{(_session.Source == SessionSource.Photo ? "Da foto" : $"Mosaico: {_session.TrackerId}, {_session.KeyframeCount} fotogrammi chiave")} · " +
        $"riconoscimento: {_session.ClassifierId ?? "—"}";

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

        Denominations = _coins.Where(c => c.EffectiveCents is not null)
            .GroupBy(c => c.EffectiveCents!.Value)
            .OrderByDescending(g => g.Key)
            .Select(g => new CoinResult { Denomination = EuroCoins.NameOf(g.Key), ValueInCents = g.Key, Count = g.Count() })
            .ToList();

        var session = _session;
        var coins = _coins;
        var annotated = await Task.Run(() => ImageHelper.DrawSessionMosaic(session, coins));
        MosaicImage = annotated is null ? null : ImageSource.FromStream(() => new MemoryStream(annotated));

        foreach (var p in new[] { nameof(Name), nameof(DateText), nameof(TotalText), nameof(CoinCount), nameof(UncertainCount),
                     nameof(HasUncertain), nameof(UncertainText), nameof(CanRetryAi), nameof(MethodText), nameof(Denominations),
                     nameof(IsCompleted), nameof(NeedsCompletion) })
            OnPropertyChanged(p);
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
    public CoinItem(CoinRecord c)
    {
        Record = c;
        Crop = c.CropPath is { } p && File.Exists(p) ? ImageSource.FromFile(p) : null;
    }

    public CoinRecord Record { get; }
    public int Id => Record.Id;
    public ImageSource? Crop { get; }
    public string Title => $"Moneta {Record.Number}";

    public string ValueText => Record.State switch
    {
        CoinState.Rejected => "Non è una moneta",
        CoinState.Detected => "Da riconoscere",
        _ => Record.EffectiveCents is { } c ? EuroCoins.NameOf(c) : "?"
    };

    public double Confidence => Record.State == CoinState.Corrected ? 1 : Record.ClassConfidence;
    public string ConfidenceText => Record.State switch
    {
        CoinState.Corrected => "Corretta a mano",
        CoinState.Rejected => "Esclusa dal conteggio",
        CoinState.Detected => "—",
        _ => $"Confidenza {Record.ClassConfidence:P0}"
    };

    public Color ConfidenceColor => Record.State == CoinState.Corrected ? Color.FromArgb("#2196F3")
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

    public CoinListViewModel()
    {
        OpenCommand = new Command<CoinItem>(async item => await Shell.Current.GoToAsync($"coin?id={item.Id}"));
    }

    public ObservableCollection<CoinItem> Coins { get; } = new();
    public ICommand OpenCommand { get; }

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
        Filter();
        OnPropertyChanged(nameof(SummaryText));
    }

    private void Filter()
    {
        Coins.Clear();
        foreach (var c in _all.Where(c => !OnlyUncertain || c.IsUncertain)
                     .OrderBy(c => c.State == CoinState.Rejected).ThenBy(c => c.ClassConfidence).ThenBy(c => c.Number))
            Coins.Add(new CoinItem(c));
    }
}

/// <summary>Dettaglio di una moneta con correzione manuale del taglio.</summary>
public sealed class CoinDetailViewModel : ObservableObject, IQueryAttributable
{
    public const string NotACoin = "Non è una moneta";

    private int _coinId;
    private CoinRecord? _coin;
    private CoinItem? _item;
    private string? _selectedChoice;
    private string _history = string.Empty;

    public CoinDetailViewModel()
    {
        SaveCommand = new Command(async () => await SaveAsync());
        UndoCommand = new Command(async () => await UndoAsync());
    }

    public ICommand SaveCommand { get; }
    public ICommand UndoCommand { get; }

    public IReadOnlyList<string> Choices { get; } = EuroCoins.All.Select(c => c.Name).Append(NotACoin).ToList();

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

    public string History
    {
        get => _history;
        private set => SetProperty(ref _history, value);
    }

    public bool IsCorrected => _coin?.State is CoinState.Corrected or CoinState.Rejected;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id) && int.TryParse(id.ToString(), out var v)) _coinId = v;
    }

    public async Task LoadAsync()
    {
        _coin = await SessionServices.Repository.GetCoinAsync(_coinId);
        if (_coin is null) return;
        Item = new CoinItem(_coin);
        SelectedChoice = _coin.State == CoinState.Rejected ? NotACoin : _coin.EffectiveCents is { } c ? EuroCoins.NameOf(c) : null;

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
        if (SelectedChoice == NotACoin)
        {
            _coin.State = CoinState.Rejected;
            _coin.CorrectedCents = null;
        }
        else
        {
            var cents = EuroCoins.All.First(c => c.Name == SelectedChoice).Cents;
            _coin.State = CoinState.Corrected;
            _coin.CorrectedCents = cents;
        }

        await SessionServices.Repository.UpdateCoinAsync(_coin);
        await SessionServices.Repository.RefreshTotalsAsync(_coin.SessionId);

        // con la raccolta foto attiva la correzione diventa un'etichetta per il dataset
        if (PhotoCollectionService.Enabled && await SessionServices.LoadCropAsync(_coin) is { } crop)
            PhotoCollectionService.SaveCoinLabel(crop, _coin.SessionId, _coin.Number, _coin.CorrectedCents, predicted, _coin.ClassifierId);

        await Shell.Current.GoToAsync("..");
    }

    private async Task UndoAsync()
    {
        if (_coin is null) return;
        _coin.CorrectedCents = null;
        _coin.State = _coin.Cents is null ? CoinState.Detected : CoinState.Classified;
        await SessionServices.Repository.UpdateCoinAsync(_coin);
        await SessionServices.Repository.RefreshTotalsAsync(_coin.SessionId);
        await LoadAsync();
    }
}
