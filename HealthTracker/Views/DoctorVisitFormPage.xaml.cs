using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

/// <summary>Add and Edit doctor visit. Push as-is to add; call <see cref="LoadVisit"/> before pushing to edit.</summary>
public partial class DoctorVisitFormPage : ContentPage
{
    private readonly DoctorVisitFormViewModel _vm;

    public DoctorVisitFormPage(DoctorVisitFormViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        vm.SavedSuccessfully += OnSaved;
        vm.OpenSettingsRequested += OnOpenSettingsRequested;
        vm.ToastRequested += OnToastRequested;
    }

    /// <summary>Switches the form to edit mode for the given visit.</summary>
    public Task LoadVisit(int visitId) => _vm.Load(visitId);

    private async void OnSaved(object? sender, EventArgs e)
    {
        try
        {
            await Toast.Make(_vm.IsEditMode ? "Visit updated" : "Visit saved").Show();
            await Navigation.PopAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async void OnToastRequested(object? sender, string message)
    {
        try { await Toast.Make(message).Show(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async void OnOpenSettingsRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<SettingsPage>(Navigation);

    private async void OnImageTapped(object sender, TappedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.PrescriptionImagePath))
            await NavigationHelper.OpenFileAsync(_vm.PrescriptionImagePath, "Prescription");
    }
}
