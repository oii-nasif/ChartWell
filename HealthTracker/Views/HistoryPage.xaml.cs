using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel _vm;
    private readonly ThemeService _theme;

    public HistoryPage(HistoryViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        // EmptyView is not guaranteed to inherit the page's BindingContext, so wire it explicitly.
        EmptyState.BindingContext = vm;
    }

    /// <summary>"Medicine", "Doctor" or "Reports". Call before the page is pushed.</summary>
    public void SetCategory(string category)
    {
        _vm.Category = category;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try { await _vm.Search(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"History search failed: {ex}"); }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    // Token-coloured bindings (ThemeColorConverter) resolve at bind time; re-evaluate them on a theme swap.
    private void OnThemeChanged(object? sender, AppTheme e)
        => MainThread.BeginInvokeOnMainThread(_vm.RefreshThemeBindings);

    private async void OnMedicineTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not MedicineHistoryGroup group) return;
        await NavigationHelper.PushAsync<MedicineDetailPage>(Navigation, p => p.SetMedicineId(group.MedicineId));
    }

    private async void OnDoctorTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not DoctorHistoryItem item) return;
        await NavigationHelper.PushAsync<DoctorVisitDetailPage>(Navigation, p => p.LoadVisit(item.VisitId));
    }

    private async void OnReportTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not ReportHistoryItem item) return;
        await NavigationHelper.OpenFileAsync(item.FilePath, item.Title);
    }
}
