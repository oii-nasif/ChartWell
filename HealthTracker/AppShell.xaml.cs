using HealthTracker.Views;

namespace HealthTracker;

public partial class AppShell : Shell
{
    private bool _isNavigating;

    public AppShell()
    {
        InitializeComponent();
        // Routes exist so that GoToAsync works for deep links (notification taps); regular navigation
        // resolves pages from DI and pushes them, see NavigationHelper.
        Routing.RegisterRoute(nameof(MedicineDetailPage), typeof(MedicineDetailPage));
        Routing.RegisterRoute(nameof(MedicineListPage), typeof(MedicineListPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        Routing.RegisterRoute(nameof(AskAiPage), typeof(AskAiPage));
        Routing.RegisterRoute(nameof(PinLockPage), typeof(PinLockPage));
    }

    private async void OnSettingsClicked(object sender, EventArgs e) => await PushGuarded<SettingsPage>();

    private async void OnAskAiClicked(object sender, EventArgs e) => await PushGuarded<AskAiPage>();

    // A double tap on a toolbar item must not push the same page twice.
    private async Task PushGuarded<TPage>() where TPage : Page
    {
        if (_isNavigating) return;
        _isNavigating = true;
        try
        {
            // Already on top (e.g. a second tap after the first push completed): nothing to do.
            if (Current.Navigation.NavigationStack.LastOrDefault() is TPage) return;

            var page = App.Services.GetRequiredService<TPage>();
            await Current.Navigation.PushAsync(page);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation to {typeof(TPage).Name} failed: {ex}");
            await Current.DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            _isNavigating = false;
        }
    }
}
