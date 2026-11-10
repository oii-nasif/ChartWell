using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class TimelinePage : ContentPage
{
    private readonly TimelineViewModel _vm;
    private readonly ThemeService _theme;

    public TimelinePage(TimelineViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        // EmptyView is not guaranteed to inherit the page's BindingContext, so wire it explicitly.
        EmptyState.BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try { await _vm.Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timeline load failed: {ex}"); }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    // Token-coloured bindings (ThemeColorConverter) resolve at bind time; re-evaluate them on a theme swap.
    private void OnThemeChanged(object? sender, AppTheme e)
        => MainThread.BeginInvokeOnMainThread(_vm.RefreshThemeBindings);

    private async void OnHistoryClicked(object? sender, EventArgs e)
    {
        await NavigationHelper.PushAsync<HistoryPage>(Navigation, p => p.SetCategory("Medicine"));
    }
}
