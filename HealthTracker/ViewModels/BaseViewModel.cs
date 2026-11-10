using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HealthTracker.ViewModels;

public class BaseViewModel : INotifyPropertyChanged
{
    private bool _isBusy;
    private bool _hasError;
    private string _errorMessage = string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); }
    }

    public bool IsNotBusy => !_isBusy;

    public bool HasError
    {
        get => _hasError;
        set { _hasError = value; OnPropertyChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    protected void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
    }

    protected void ClearError()
    {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Re-evaluates every binding on this ViewModel. Token-coloured bindings go through
    /// <c>ThemeColorConverter</c>, which resolves the token at bind time, so a theme swap would
    /// otherwise leave them on the old palette. Pages call this from <c>ThemeService.ThemeChanged</c>.
    /// An empty property name tells the binding engine that all properties changed.
    /// </summary>
    public virtual void RefreshThemeBindings()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
