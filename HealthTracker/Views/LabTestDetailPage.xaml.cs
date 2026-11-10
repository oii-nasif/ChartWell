using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class LabTestDetailPage : ContentPage
{
    private readonly LabTestDetailViewModel _vm;
    private readonly ThemeService _theme;

    public LabTestDetailPage(LabTestDetailViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        EmptyState.BindingContext = vm;
    }

    public void SetTest(string testCode) => _vm.SetTest(testCode);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try { await _vm.Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LabTestDetailPage.OnAppearing failed: {ex}"); }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    // Chart paints hold resolved colours, so rebuild them for the new theme.
    private void OnThemeChanged(object? sender, AppTheme e) => MainThread.BeginInvokeOnMainThread(_vm.Rebuild);

    private async void OnAddClicked(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<LabEntryPage>(Navigation, p => p.PresetTest(_vm.TestCode));

    private async void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not LabRowItem row) return;
        await NavigationHelper.PushAsync<LabEntryPage>(Navigation, p => p.EditResult(row.Id));
    }
}
