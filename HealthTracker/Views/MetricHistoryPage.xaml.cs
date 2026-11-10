using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class MetricHistoryPage : ContentPage
{
    private readonly MetricHistoryViewModel _vm;
    private readonly ThemeService _theme;
    private bool _loadedOnce;
    private bool _appearedOnce;

    public MetricHistoryPage(MetricHistoryViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        // EmptyView is not guaranteed to inherit the page's BindingContext, so wire it explicitly.
        EmptyState.BindingContext = vm;
        _vm.AddReadingRequested += OnAddReadingRequested;
    }

    /// <summary>Entry point used by the dashboard (and any other module) before pushing this page.</summary>
    public async Task LoadType(string metricType)
    {
        _loadedOnce = true;
        await _vm.Load(metricType);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try
        {
            if (!_loadedOnce)
            {
                // Pushed without LoadType (defensive): show the default type rather than a blank page.
                await LoadType(_vm.MetricType);
            }
            else if (_appearedOnce)
            {
                // Back from the form: the DB changed, so reload and rebuild chart/stats.
                await _vm.Load(_vm.MetricType);
            }
            else
            {
                _vm.Rebuild();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Metric history refresh failed: {ex}"); }
        finally { _appearedOnce = true; }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    private void OnThemeChanged(object? sender, AppTheme e)
    {
        // Series paints hold resolved SKColors, so re-resolve them for the new theme.
        MainThread.BeginInvokeOnMainThread(_vm.Rebuild);
    }

    private async void OnAddClicked(object sender, EventArgs e)
    {
        await NavigationHelper.PushAsync<MetricFormPage>(Navigation, p => p.SetPreselectedType(_vm.MetricType));
    }

    private async void OnAddReadingRequested(object? sender, EventArgs e)
    {
        await NavigationHelper.PushAsync<MetricFormPage>(Navigation, p => p.SetPreselectedType(_vm.MetricType));
    }

    private async void OnReadingTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not MetricReadingItem item) return;
        await NavigationHelper.PushAsync<MetricFormPage>(Navigation, p => p.LoadMetric(item.Metric));
    }

    private async void OnDeleteInvoked(object sender, EventArgs e)
    {
        try
        {
            if (sender is not SwipeItem { BindingContext: MetricReadingItem item }) return;

            var confirm = await DisplayAlert("Delete reading?",
                $"{item.ValueText} {item.Unit} recorded on {item.DateText} will be removed. This can't be undone.",
                "Delete", "Cancel");
            if (!confirm) return;

            if (_vm.DeleteCommand.CanExecute(item.Metric))
                _vm.DeleteCommand.Execute(item.Metric);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Delete reading failed: {ex}");
        }
    }
}
