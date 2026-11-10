using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class MedicineListPage : ContentPage
{
    private readonly MedicineListViewModel _vm;
    private readonly ThemeService _theme;
    private bool _loading;

    public MedicineListPage(MedicineListViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        // EmptyView is not guaranteed to inherit the page's BindingContext, so wire each one explicitly.
        EmptyStateActive.BindingContext = vm;
        EmptyStatePast.BindingContext = vm;
        EmptyStatePending.BindingContext = vm;
        _vm.ApprovePendingRequested += OnApprovePendingRequested;
        _vm.AddMedicineRequested += OnAddRequested;
        _vm.ScanPrescriptionRequested += OnScanRequested;
    }

    /// <summary>Lets callers (e.g. the scan page's "Review now") open the page on a specific segment.</summary>
    public void ShowSegment(MedicineSegment segment) => _vm.SetSegment(segment);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        if (_loading) return;
        _loading = true;
        try { await _vm.LoadMedicines(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MedicineListPage load failed: {ex}"); }
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

    private async void OnScanClicked(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<ScanPrescriptionPage>(Navigation);

    private async void OnScanRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<ScanPrescriptionPage>(Navigation);

    private async void OnAddClicked(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<MedicineFormPage>(Navigation);

    private async void OnAddRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<MedicineFormPage>(Navigation);

    private async void OnMedicineTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not MedicineItem item) return;
        await NavigationHelper.PushAsync<MedicineDetailPage>(Navigation, p => p.SetMedicineId(item.Medicine.Id));
    }

    private async void OnApprovePendingRequested(object? sender, PendingMedicine pending)
    {
        await NavigationHelper.PushAsync<MedicineFormPage>(Navigation, page =>
        {
            page.PrefillFromPending(pending);
            // Remove the pending entry once the real medicine has been saved. The form awaits this
            // before popping, so the reload in OnAppearing never sees the stale pending row.
            page.Saved = () => _vm.ApprovePending(pending);
        });
    }
}
