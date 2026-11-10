using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class MetricsDashboardPage : ContentPage
{
    private readonly MetricsDashboardViewModel _vm;
    private readonly ThemeService _theme;

    public MetricsDashboardPage(MetricsDashboardViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try { await _vm.Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Metrics dashboard load failed: {ex}"); }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    // Token-coloured bindings (ThemeColorConverter) resolve at bind time; re-evaluate them on a theme swap.
    private void OnThemeChanged(object? sender, AppTheme e)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            // Rows hold token-coloured bindings and sparklines drawn with theme colours: rebuild them.
            _ = _vm.Load();
            BmiGauge.Invalidate(); // the gauge reads theme colours when it draws
        });

    /// <summary>The BMI card's action: height lives in the profile, weight is a vital reading.</summary>
    private async void OnBmiActionClicked(object sender, EventArgs e)
    {
        if (_vm.BmiNeedsHeight)
            await NavigationHelper.PushAsync<ProfileFormPage>(Navigation);
        else
            await NavigationHelper.PushAsync<MetricFormPage>(Navigation, p => p.SetPreselectedType(Constants.MetricTypes.Weight));
    }

    private async void OnBmiCardTapped(object sender, TappedEventArgs e)
    {
        if (_vm.HasBmi)
            await NavigationHelper.PushAsync<MetricHistoryPage>(Navigation, p => p.LoadType(Constants.MetricTypes.Weight));
    }

    private async void OnAddClicked(object sender, EventArgs e)
    {
        await NavigationHelper.PushAsync<MetricFormPage>(Navigation, p => p.SetPreselectedType(null));
    }

    private async void OnMetricTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not MetricSummaryItem item) return;

        if (item.HasData)
            await NavigationHelper.PushAsync<MetricHistoryPage>(Navigation, p => p.LoadType(item.Type));
        else
            await NavigationHelper.PushAsync<MetricFormPage>(Navigation, p => p.SetPreselectedType(item.Type));
    }
}
