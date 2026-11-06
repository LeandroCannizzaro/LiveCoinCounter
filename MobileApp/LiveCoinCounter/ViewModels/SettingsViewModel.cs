using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Recognition;
using LiveCoinCounter.Sessions;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// ViewModel per la pagina impostazioni (metodo di riconoscimento, chiavi API, modello).
/// </summary>
public class SettingsViewModel : INotifyPropertyChanged
{
    private RecognizerProfile _selectedProfile;
    private string _apiKey = string.Empty;
    private string _model = string.Empty;
    private string _baseUrl = string.Empty;
    private string _statusText = string.Empty;
    private bool _statusIsError;
    private bool _isBusy;
    private bool _loading;

    public IReadOnlyList<RecognizerProfile> Profiles => RecognizerCatalog.All;

    public RecognizerProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value) || value is null) return;
            SettingsService.ActiveRecognizerId = value.Id;
            OnPropertyChanged(nameof(ShowApiKey));
            OnPropertyChanged(nameof(ShowModel));
            OnPropertyChanged(nameof(ShowBaseUrl));
            OnPropertyChanged(nameof(HasKeyUrl));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(ProviderNote));
            OnPropertyChanged(nameof(ModelSummary));
            OnPropertyChanged(nameof(ApiKeyPlaceholder));
            OnPropertyChanged(nameof(PrivacyText));
            OnPropertyChanged(nameof(FooterText));
            _ = LoadConfigAsync();
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set { if (SetProperty(ref _apiKey, value)) ClearStatus(); }
    }

    public string Model
    {
        get => _model;
        set { if (SetProperty(ref _model, value)) ClearStatus(); }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set { if (SetProperty(ref _baseUrl, value)) ClearStatus(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { SetProperty(ref _statusText, value); OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanTest));
            ((Command)SaveCommand).ChangeCanExecute();
            ((Command)TestCommand).ChangeCanExecute();
        }
    }

    public bool ShowApiKey => !SelectedProfile.IsOffline;
    public bool ShowModel => !SelectedProfile.IsOffline;
    public bool ShowBaseUrl => SelectedProfile.BaseUrlEditable;
    public bool HasKeyUrl => SelectedProfile.KeyUrl is not null;
    public bool CanTest => !IsBusy;

    public string ProviderNote => SelectedProfile.RequiresApiKey
        ? $"{SelectedProfile.Note} Se lasci la chiave vuota, l'app usa la chiave Gemini predefinita."
        : SelectedProfile.Note;
    public string ApiKeyPlaceholder => SelectedProfile.KeyPlaceholder;

    public string ModelSummary => SelectedProfile.IsOffline
        ? SelectedProfile.Kind == RecognizerKind.OnDeviceModel ? "Modello addestrato sul dispositivo" : "Analisi sul dispositivo (sperimentale)"
        : $"{SelectedProfile.DisplayName} · {(string.IsNullOrWhiteSpace(Model) ? SelectedProfile.Models.FirstOrDefault() : Model)}";

    public string PrivacyText => SelectedProfile.IsOffline
        ? "La foto resta sul dispositivo: nessun dato viene inviato in rete."
        : $"Le immagini vengono inviate a {SelectedProfile.DisplayName} per l'analisi e non vengono memorizzate localmente.";

    public string FooterText => $"Conteggio Monete v{AppInfo.VersionString} · {SelectedProfile.DisplayName}";

    public bool CollectPhotos
    {
        get => PhotoCollectionService.Enabled;
        set
        {
            PhotoCollectionService.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollectedSummary));
        }
    }

    public int CollectedCount => PhotoCollectionService.Count;
    public bool HasCollectedPhotos => CollectedCount > 0;
    public string CollectedSummary => CollectPhotos
        ? $"Raccolta attiva: {CollectedCount} foto salvate."
        : $"Raccolta spenta ({CollectedCount} foto salvate in precedenza).";

    // ---- Riconoscimento continuo ----

    public IReadOnlyList<SettingOption<CaptureMethod>> CaptureMethods { get; } = new[]
    {
        new SettingOption<CaptureMethod>(CaptureMethod.OpenCv, "OpenCV (tutti i telefoni)",
            "Allinea i fotogrammi con i punti caratteristici dell'immagine. Funziona ovunque; su tavoli molto uniformi (bianchi, vetro) può perdere la posizione."),
        new SettingOption<CaptureMethod>(CaptureMethod.ArCore, "ARCore",
            "Usa la posizione del telefono calcolata da ARCore e il piano del tavolo: regge i movimenti veloci e misura le monete in millimetri. Solo telefoni compatibili ARCore."),
        new SettingOption<CaptureMethod>(CaptureMethod.Hybrid, "Ibrido (ARCore + OpenCV)",
            "ARCore per la posizione, OpenCV per allineare al pixel i fotogrammi vicini. Solo telefoni compatibili ARCore.")
    };

    public SettingOption<CaptureMethod> SelectedCaptureMethod
    {
        get => CaptureMethods.First(o => o.Value == SettingsService.CaptureMethod);
        set
        {
            if (value is null) return;
            SettingsService.CaptureMethod = value.Value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingOption<bool>> SurfaceStyles { get; } = new[]
    {
        new SettingOption<bool>(true, "Piano di colore",
            "La superficie acquisita è un piano grigio ardesia con sopra solo le monete: niente ombre del telefono né cuciture."),
        new SettingOption<bool>(false, "Foto del tavolo",
            "Mostra la foto reale del tavolo: utile per controllare l'allineamento dei fotogrammi.")
    };

    public SettingOption<bool> SelectedSurfaceStyle
    {
        get => SurfaceStyles.First(o => o.Value == SettingsService.SolidSurface);
        set
        {
            if (value is null) return;
            SettingsService.SolidSurface = value.Value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingOption<GridSpacing>> GridSpacings { get; } = new[]
    {
        new SettingOption<GridSpacing>(GridSpacing.Off, "Nessuna", "Senza griglia."),
        new SettingOption<GridSpacing>(GridSpacing.Cm1, "Ogni 1 cm", "Griglia fitta per misurare le monete; i numeri compaiono ogni 5 cm."),
        new SettingOption<GridSpacing>(GridSpacing.Cm2, "Ogni 2 cm", "Griglia media; i numeri compaiono ogni 10 cm."),
        new SettingOption<GridSpacing>(GridSpacing.Cm5, "Ogni 5 cm", "Griglia larga; i numeri compaiono ogni 10 cm."),
        new SettingOption<GridSpacing>(GridSpacing.Cm10, "Ogni 10 cm", "Solo i riferimenti principali.")
    };

    public SettingOption<GridSpacing> SelectedGridSpacing
    {
        get => GridSpacings.First(o => o.Value == SettingsService.GridSpacing);
        set
        {
            if (value is null) return;
            SettingsService.GridSpacing = value.Value;
            OnPropertyChanged();
        }
    }

    public string GridNote =>
        "La griglia ha la scala esatta con ARCore; con OpenCV la scala si stima dalle monete riconosciute (almeno 3) ed è indicativa: se il telefono era inclinato, cambia da un punto all'altro.";

    public IReadOnlyList<SettingOption<RetentionMode>> RetentionModes { get; } = new[]
    {
        new SettingOption<RetentionMode>(RetentionMode.MosaicAndCrops, "Mosaico e ritagli",
            "Per ogni sessione restano il mosaico ridotto e un ritaglio per moneta: qualche MB."),
        new SettingOption<RetentionMode>(RetentionMode.WithKeyframes, "Anche i fotogrammi chiave",
            "Si conservano anche le foto originali del mosaico (decine di MB per sessione), per rianalizzarle con modelli futuri."),
        new SettingOption<RetentionMode>(RetentionMode.DebugFrames, "Fotogrammi in debug",
            "Come il predefinito, ma registra ogni fotogramma elaborato (circa 1 MB al secondo) con la posa: serve a riprodurre il tracking sul PC.")
    };

    public SettingOption<RetentionMode> SelectedRetention
    {
        get => RetentionModes.First(o => o.Value == SettingsService.Retention);
        set
        {
            if (value is null) return;
            SettingsService.Retention = value.Value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingOption<AiMode>> AiModes { get; } = new[]
    {
        new SettingOption<AiMode>(AiMode.OfflineAiOnRequest, "Sul telefono, AI su richiesta",
            "Il valore si riconosce sul telefono; per le monete incerte puoi chiedere l'AI in rete dal riepilogo."),
        new SettingOption<AiMode>(AiMode.OfflineOnly, "Solo sul telefono",
            "Nessuna immagine esce dal telefono."),
        new SettingOption<AiMode>(AiMode.AiAutoUncertain, "AI automatica sulle incerte",
            "Se c'è rete, i ritagli delle monete incerte vanno subito all'AI configurata sopra.")
    };

    public SettingOption<AiMode> SelectedAiMode
    {
        get => AiModes.First(o => o.Value == SettingsService.AiMode);
        set
        {
            if (value is null) return;
            SettingsService.AiMode = value.Value;
            OnPropertyChanged();
        }
    }

    public string AiPrivacyNote =>
        "L'AI riceve solo i ritagli delle singole monete, per riconoscerne il valore: mai i fotogrammi né il mosaico del tavolo.";

    public ICommand ExportPhotosCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand OpenKeyPageCommand { get; }

    public SettingsViewModel()
    {
        _selectedProfile = RecognizerCatalog.Get(SettingsService.ActiveRecognizerId);
        ExportPhotosCommand = new Command(async () => await PhotoCollectionService.ExportAsync());
        SaveCommand = new Command(async () => await SaveAsync(), () => !IsBusy);
        TestCommand = new Command(async () => await TestAsync(), () => !IsBusy);
        OpenKeyPageCommand = new Command(async () =>
        {
            if (SelectedProfile.KeyUrl is { } url)
                await Launcher.Default.OpenAsync(url);
        });

        _ = LoadConfigAsync();
    }

    private async Task LoadConfigAsync()
    {
        _loading = true;
        try
        {
            var config = await SettingsService.GetConfigAsync(SelectedProfile);
            ApiKey = config.ApiKey;
            Model = string.IsNullOrWhiteSpace(config.Model) ? string.Empty : config.Model;
            BaseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? SelectedProfile.BaseUrl : config.BaseUrl;
            StatusText = string.Empty;
            OnPropertyChanged(nameof(ModelSummary));
        }
        finally
        {
            _loading = false;
        }
    }

    private RecognizerConfig CurrentConfig() =>
        new(ApiKey?.Trim() ?? string.Empty, Model?.Trim() ?? string.Empty, BaseUrl?.Trim() ?? string.Empty);

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            await SettingsService.SaveConfigAsync(SelectedProfile, CurrentConfig());
            OnPropertyChanged(nameof(ModelSummary));
            SetStatus("✅ Impostazioni salvate", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"❌ Salvataggio non riuscito: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestAsync()
    {
        IsBusy = true;
        SetStatus("⏳ Test in corso...", isError: false);
        try
        {
            var recognizer = await RecognizerFactory.CreateWithFallbackAsync(SelectedProfile, CurrentConfig());

            var result = await recognizer.RecognizeAsync(ImageHelper.CreateTestImage());
            if (result.Success)
                SetStatus($"✅ Connessione riuscita ({recognizer.DisplayName})", isError: false);
            else
                SetStatus($"❌ {result.ErrorMessage}", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"❌ {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearStatus()
    {
        if (!_loading && !IsBusy) StatusText = string.Empty;
    }

    private void SetStatus(string text, bool isError)
    {
        StatusIsError = isError;
        StatusText = text;
    }

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}

/// <summary>Una voce di un'impostazione a scelta: valore, nome nel menu e spiegazione.</summary>
public sealed record SettingOption<T>(T Value, string Label, string Description)
{
    public override string ToString() => Label;
}
