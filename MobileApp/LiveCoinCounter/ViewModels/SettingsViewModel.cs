using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Recognition;

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
