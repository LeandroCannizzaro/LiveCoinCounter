using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LiveCoinCounter.Services;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// ViewModel per la pagina impostazioni (API key, preferenze).
/// </summary>
public class SettingsViewModel : INotifyPropertyChanged
{
    private string _apiKey;
    private bool _isSaved;

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            SetProperty(ref _apiKey, value);
            IsSaved = false;
        }
    }

    public bool IsSaved
    {
        get => _isSaved;
        set => SetProperty(ref _isSaved, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand OpenAiStudioCommand { get; }

    public SettingsViewModel()
    {
        _apiKey = SettingsService.GeminiApiKey;
        SaveCommand = new Command(Save);
        OpenAiStudioCommand = new Command(async () =>
            await Launcher.Default.OpenAsync("https://aistudio.google.com/apikey"));
    }

    private void Save()
    {
        SettingsService.GeminiApiKey = ApiKey?.Trim() ?? string.Empty;
        IsSaved = true;
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

