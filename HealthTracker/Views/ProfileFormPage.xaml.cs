using CommunityToolkit.Maui.Alerts;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class ProfileFormPage : ContentPage
{
    private readonly ProfileFormViewModel _vm;

    public ProfileFormPage(ProfileFormViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        _vm.Saved += OnSaved;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await _vm.InitializeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ProfileFormPage.OnAppearing failed: {ex}"); }
    }

    private async void OnSaved(object? sender, EventArgs e)
    {
        try
        {
            await Toast.Make("Profile saved").Show();
            await Navigation.PopAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ProfileFormPage.OnSaved failed: {ex}"); }
    }
}
