using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCoinCounter.ViewModels;

/// <summary>Base comune dei ViewModel: notifica delle proprietà e avvisi.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected static Page? CurrentPage => Shell.Current?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;

    protected static Task ShowAlert(string title, string message) =>
        CurrentPage?.DisplayAlertAsync(title, message, "OK") ?? Task.CompletedTask;
}
