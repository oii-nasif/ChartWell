using CommunityToolkit.Maui.Alerts;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        vm.Notified += OnNotified;
        vm.RestoreCompleted += OnRestoreCompleted;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await _vm.LoadAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Settings load failed: {ex}"); }
    }

    private async void OnNotified(object? sender, string message)
    {
        try { await Toast.Make(message).Show(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Toast failed: {ex}"); }
    }

    /// <summary>
    /// Every ViewModel in the app is holding stale data after a restore, so the whole UI is rebuilt
    /// from a fresh AppShell (which also returns to the Today tab).
    /// </summary>
    private async void OnRestoreCompleted(object? sender, EventArgs e)
    {
        try
        {
            await DisplayAlert("Restore complete", "Your data has been restored and reminders were rescheduled.", "OK");
            var app = Application.Current;
            if (app is not null && app.Windows.Count > 0)
                app.Windows[0].Page = new AppShell();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Post-restore reload failed: {ex}");
        }
    }

    private async void OnAiStudioLinkTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            await Browser.Default.OpenAsync("https://aistudio.google.com/apikey", BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Open browser failed: {ex}");
        }
    }
}
