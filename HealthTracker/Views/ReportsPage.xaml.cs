using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class ReportsPage : ContentPage
{
    private readonly ReportsViewModel _vm;
    private readonly ThemeService _theme;

    public ReportsPage(ReportsViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        // EmptyView is not guaranteed to inherit the page's BindingContext, so wire it explicitly.
        EmptyState.BindingContext = vm;
        vm.AddRequested += OnAddRequested;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try { await _vm.Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    // Token-coloured bindings (ThemeColorConverter) resolve at bind time; re-evaluate them on a theme swap.
    private void OnThemeChanged(object? sender, AppTheme e)
        => MainThread.BeginInvokeOnMainThread(_vm.RefreshThemeBindings);

    // NavigationHelper / OpenFileAsync / ShareFileAsync catch their own exceptions.
    private async void OnAddRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<AddReportPage>(Navigation);

    private void OnTypeFilterTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is ReportTypeFilter filter) _vm.SelectType(filter);
    }

    private async void OnReportTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is ReportListItem item)
            await NavigationHelper.OpenFileAsync(item.FilePath, item.Title);
    }

    private async void OnMoreClicked(object sender, EventArgs e)
    {
        try
        {
            if ((sender as ImageButton)?.CommandParameter is not ReportListItem item) return;
            var choice = await DisplayActionSheet(item.Title, "Cancel", "Delete", "View", "Share");
            switch (choice)
            {
                case "View": await NavigationHelper.OpenFileAsync(item.FilePath, item.Title); break;
                case "Share": await NavigationHelper.ShareFileAsync(item.FilePath, item.Title); break;
                case "Delete": await ConfirmDelete(item); break;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async void OnDeleteInvoked(object sender, EventArgs e)
    {
        try
        {
            if ((sender as SwipeItem)?.BindingContext is ReportListItem item)
                await ConfirmDelete(item);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async Task ConfirmDelete(ReportListItem item)
    {
        bool confirmed = await DisplayAlert("Delete report",
            $"Delete \"{item.Title}\"? Its files will be removed from this device.", "Delete", "Cancel");
        if (!confirmed) return;

        if (await _vm.Delete(item))
            await Toast.Make("Report deleted").Show();
    }
}
