using HealthTracker.Helpers;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class ProfilePage : ContentPage
{
    private readonly ProfileViewModel _vm;

    public ProfilePage(ProfileViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await _vm.Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ProfilePage.OnAppearing failed: {ex}"); }
    }

    private async void OnEditProfileTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<ProfileFormPage>(Navigation);

    private async void OnMedicinesTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<MedicineListPage>(Navigation);

    private async void OnHistoryTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<HistoryPage>(Navigation);

    private async void OnAskAiTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<AskAiPage>(Navigation);

    private async void OnSettingsTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<SettingsPage>(Navigation);

    private async void OnLabsTapped(object? sender, TappedEventArgs e) => await NavigationHelper.GoToTabAsync("labs");
    private async void OnReportsTapped(object? sender, TappedEventArgs e) => await NavigationHelper.GoToTabAsync("reports");
    private async void OnVisitsTapped(object? sender, TappedEventArgs e) => await NavigationHelper.GoToTabAsync("doctors");

    private void OnCallClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_vm.CanCall && PhoneDialer.Default.IsSupported) PhoneDialer.Default.Open(_vm.EmergencyPhone);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dial failed: {ex}"); }
    }
}
