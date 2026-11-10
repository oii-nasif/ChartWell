using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class MedicineDetailPage : ContentPage
{
    private readonly MedicineDetailViewModel _vm;
    private readonly ThemeService _theme;
    private int _medicineId;
    private bool _loading;

    public MedicineDetailPage(MedicineDetailViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        _vm.DoseLogged += OnDoseLogged;
        _vm.ActionCompleted += OnActionCompleted;
        _vm.Deleted += OnDeleted;
    }

    /// <summary>Entry point used by Today, Medicines, History and other modules before pushing the page.</summary>
    public void SetMedicineId(int medicineId)
    {
        _medicineId = medicineId;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        if (_medicineId <= 0 || _loading) return;
        _loading = true;
        try { await _vm.Load(_medicineId); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MedicineDetailPage load failed: {ex}"); }
        finally { _loading = false; }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    // Token-coloured bindings (ThemeColorConverter) resolve at bind time; re-evaluate them on a theme swap.
    private void OnThemeChanged(object? sender, AppTheme e)
        => MainThread.BeginInvokeOnMainThread(_vm.RefreshThemeBindings);

    private async void OnEditClicked(object? sender, EventArgs e)
    {
        if (_medicineId <= 0) return;
        await NavigationHelper.PushAsync<MedicineFormPage>(Navigation, p => p.LoadMedicine(_medicineId));
    }

    private async void OnDoseLogged(object? sender, string medicineName)
    {
        try { await Toast.Make($"{medicineName} logged").Show(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Toast failed: {ex}"); }
    }

    private async void OnActionCompleted(object? sender, string message)
    {
        try { await Toast.Make(message).Show(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Toast failed: {ex}"); }
    }

    private async void OnDeleted(object? sender, EventArgs e)
    {
        try
        {
            await Toast.Make("Medicine deleted").Show();
            await Navigation.PopAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Pop after delete failed: {ex}"); }
    }
}
