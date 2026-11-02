using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LiveCoinCounter.Models;
using LiveCoinCounter.Services;

namespace LiveCoinCounter.ViewModels;

/// <summary>
/// ViewModel principale per la pagina di riconoscimento monete.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    #region Fields

    private bool _isAnalyzing;
    private bool _hasResult;
    private string _statusMessage = "Scatta o carica una foto delle monete";
    private string _totalValue = "--,-- €";
    private int _totalCoins;
    private List<CoinResult> _coinResults = new();
    private ImageSource? _capturedImage;
    private byte[]? _capturedImageBytes;
    private CoinRecognitionResult? _lastResult;

    #endregion

    #region Properties

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set => SetProperty(ref _isAnalyzing, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        set => SetProperty(ref _hasResult, value);
    }

    public bool IsIdle => !IsAnalyzing;

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string TotalValue
    {
        get => _totalValue;
        set => SetProperty(ref _totalValue, value);
    }

    public int TotalCoins
    {
        get => _totalCoins;
        set => SetProperty(ref _totalCoins, value);
    }

    public List<CoinResult> CoinResults
    {
        get => _coinResults;
        set => SetProperty(ref _coinResults, value);
    }

    public ImageSource? CapturedImage
    {
        get => _capturedImage;
        set => SetProperty(ref _capturedImage, value);
    }

    #endregion

    #region Commands

    public ICommand TakePhotoCommand { get; }
    public ICommand PickPhotoCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    #endregion

    public MainViewModel()
    {
        TakePhotoCommand = new Command(async () => await TakePhotoAsync(), () => IsIdle);
        PickPhotoCommand = new Command(async () => await PickPhotoAsync(), () => IsIdle);
        AnalyzeCommand = new Command(async () => await AnalyzeAsync(), () => IsIdle && _capturedImageBytes != null);
        ResetCommand = new Command(Reset);
        OpenSettingsCommand = new Command(async () => await OpenSettingsAsync());
    }

    #region Command Handlers

    private async Task TakePhotoAsync()
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported)
            {
                await ShowAlert("Fotocamera non disponibile", "Questo dispositivo non supporta la fotocamera.");
                return;
            }

            var photo = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions
            {
                Title = "Fotografa le monete"
            });

            if (photo is not null)
                await LoadPhotoAsync(photo);
        }
        catch (PermissionException)
        {
            await ShowAlert("Permesso negato", "L'app necessita del permesso per usare la fotocamera.");
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore", $"Impossibile acquisire la foto: {ex.Message}");
        }
    }

    private async Task PickPhotoAsync()
    {
        try
        {
            var photo = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions
            {
                Title = "Seleziona immagine con monete"
            });

            if (photo is not null)
                await LoadPhotoAsync(photo);
        }
        catch (Exception ex)
        {
            await ShowAlert("Errore", $"Impossibile caricare la foto: {ex.Message}");
        }
    }

    private async Task LoadPhotoAsync(FileResult photo)
    {
        HasResult = false;
        CoinResults = new();
        TotalValue = "--,-- €";
        TotalCoins = 0;

        using var stream = await photo.OpenReadAsync();
        _capturedImageBytes = new byte[stream.Length];
        await stream.ReadAsync(_capturedImageBytes.AsMemory(0, (int)stream.Length));

        CapturedImage = ImageSource.FromStream(() => new MemoryStream(_capturedImageBytes));
        StatusMessage = "Foto caricata. Tocca 'Analizza' per riconoscere le monete.";

        ((Command)AnalyzeCommand).ChangeCanExecute();
    }

    private async Task AnalyzeAsync()
    {
        if (_capturedImageBytes is null) return;

        if (!SettingsService.HasApiKey)
        {
            await ShowAlert("API Key mancante",
                "Vai nelle Impostazioni e inserisci la tua chiave API Gemini.\n\n" +
                "Puoi ottenerla gratuitamente su aistudio.google.com");
            await OpenSettingsAsync();
            return;
        }

        IsAnalyzing = true;
        HasResult = false;
        StatusMessage = "🔍 Analisi in corso...";

        RefreshCommandStates();

        try
        {
            var service = new GeminiService(SettingsService.GeminiApiKey);
            var result = await service.RecognizeCoinsAsync(_capturedImageBytes);
            _lastResult = result;

            if (result.Success)
            {
                CoinResults = result.Coins;
                TotalValue = result.TotalValueFormatted;
                TotalCoins = result.TotalCoinCount;
                HasResult = true;
                StatusMessage = result.Coins.Count > 0
                    ? $"✅ Trovate {result.TotalCoinCount} monete"
                    : "⚠️ Nessuna moneta riconosciuta nell'immagine.";
            }
            else
            {
                StatusMessage = $"❌ {result.ErrorMessage}";
                HasResult = false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Errore imprevisto: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            RefreshCommandStates();
        }
    }

    private void Reset()
    {
        _capturedImageBytes = null;
        _lastResult = null;
        CapturedImage = null;
        CoinResults = new();
        HasResult = false;
        TotalValue = "--,-- €";
        TotalCoins = 0;
        StatusMessage = "Scatta o carica una foto delle monete";
        ((Command)AnalyzeCommand).ChangeCanExecute();
    }

    private async Task OpenSettingsAsync()
    {
        await Shell.Current.GoToAsync("//SettingsPage");
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(IsIdle));
        ((Command)TakePhotoCommand).ChangeCanExecute();
        ((Command)PickPhotoCommand).ChangeCanExecute();
        ((Command)AnalyzeCommand).ChangeCanExecute();
    }

    #endregion

    #region Helpers

    private static async Task ShowAlert(string title, string message)
    {
        if (Application.Current?.MainPage is not null)
            await Application.Current.MainPage.DisplayAlert(title, message, "OK");
    }

    #endregion

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

