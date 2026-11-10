using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class AddReportPage : ContentPage
{
    private readonly AddReportViewModel _vm;
    private readonly ThemeService _theme;

    public AddReportPage(AddReportViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        vm.SavedSuccessfully += OnSaved;
        vm.OpenSettingsRequested += OnOpenSettingsRequested;
    }

    /// <summary>Pre-fills the form for a report attached to a doctor visit. Call before pushing.</summary>
    public void PresetForVisit(int visitId, string? doctorName, string? hospital, string? reportType = null)
        => _vm.Preset(visitId, doctorName, hospital, reportType);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try { await _vm.InitializeAsync(); }
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

    private void OnTypeTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is ReportTypeOption option) _vm.SelectType(option, userAction: true);
    }

    private void OnRemovePageClicked(object sender, EventArgs e)
    {
        if ((sender as ImageButton)?.CommandParameter is ReportPageItem page) _vm.RemovePage(page);
    }

    private async void OnSaved(object? sender, EventArgs e)
    {
        try
        {
            await Toast.Make("Report saved").Show();

            var offer = _vm.LabImport;
            var navigation = Navigation;
            var review = false;
            if (offer is not null)
            {
                var count = offer.Drafts.Count == 1 ? "1 lab value was" : $"{offer.Drafts.Count} lab values were";
                review = await DisplayAlert("Add to Labs?",
                    $"{count} read from this report. Review them and add them to Labs to track how they change.",
                    "Review values", "Not now");
            }

            await navigation.PopAsync();
            if (review && offer is not null)
                await NavigationHelper.PushAsync<LabEntryPage>(navigation,
                    p => p.Prefill(offer.Drafts, offer.ReportDate, offer.LabName, offer.ReportId));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async void OnOpenSettingsRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<SettingsPage>(Navigation);
}
