using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class LabsPage : ContentPage
{
    private readonly LabsViewModel _vm;
    private readonly ThemeService _theme;

    public LabsPage(LabsViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        // EmptyView is not guaranteed to inherit the page's BindingContext, so wire it explicitly.
        EmptyState.BindingContext = vm;
        _vm.AddRequested += OnAddRequested;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try { await _vm.Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LabsPage.OnAppearing failed: {ex}"); }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
    }

    // Token-coloured bindings resolve at bind time; rebuild the rows on a theme swap.
    private void OnThemeChanged(object? sender, AppTheme e)
        => MainThread.BeginInvokeOnMainThread(async () => await _vm.Load());

    private async void OnAddRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<LabEntryPage>(Navigation);

    private async void OnAddClicked(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<LabEntryPage>(Navigation);

    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not LabRowItem row) return;
        await NavigationHelper.PushAsync<LabTestDetailPage>(Navigation, p => p.SetTest(row.TestCode));
    }

    private async void OnBatchMenuClicked(object? sender, EventArgs e)
    {
        try
        {
            if (sender is not ImageButton { CommandParameter: LabGroup { BatchId: { } batchId } group }) return;
            var choice = await DisplayActionSheet(group.Title, "Cancel", "Delete this set of results");
            if (choice != "Delete this set of results") return;

            var confirmed = await DisplayAlert("Delete results",
                $"Delete every result saved for {group.Title}? This cannot be undone.", "Delete", "Cancel");
            if (confirmed) await _vm.DeleteBatch(batchId);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Batch menu failed: {ex}"); }
    }
}
