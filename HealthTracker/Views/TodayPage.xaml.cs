using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class TodayPage : ContentPage
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly TodayViewModel _vm;
    private readonly ThemeService _theme;
    private CancellationTokenSource? _timerCts;
    private bool _loading;

    public TodayPage(TodayViewModel vm, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = vm;
        _vm.DoseLogged += OnDoseLogged;
        _vm.AddMedicineRequested += OnAddMedicineRequested;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _theme.ThemeChanged += OnThemeChanged;
        try
        {
            // Create the timer token BEFORE awaiting Load so a quick disappear/appear cannot leave a
            // stale loop running or cancel the wrong token.
            _timerCts?.Cancel();
            _timerCts = new CancellationTokenSource();
            var ct = _timerCts.Token;

            await LoadOnce();
            if (ct.IsCancellationRequested) return;

            _vm.RefreshWindows();
            _ = RunRefreshLoop(ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TodayPage.OnAppearing failed: {ex}");
        }
    }

    protected override void OnDisappearing()
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnDisappearing();
        _timerCts?.Cancel();
        _timerCts = null;
    }

    // Token-coloured bindings (ThemeColorConverter) resolve at bind time; re-evaluate them on a theme swap.
    private void OnThemeChanged(object? sender, AppTheme e)
        => MainThread.BeginInvokeOnMainThread(_vm.RefreshThemeBindings);

    private async Task LoadOnce()
    {
        if (_loading) return;
        _loading = true;
        try { await _vm.Load(); }
        finally { _loading = false; }
    }

    /// <summary>Re-evaluates due/overdue windows every 30 s and reloads when the calendar day changes.</summary>
    private async Task RunRefreshLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RefreshInterval, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;

            try
            {
                if (_vm.NeedsReloadForNewDay)
                    await MainThread.InvokeOnMainThreadAsync(LoadOnce);
                else
                    await MainThread.InvokeOnMainThreadAsync(_vm.RefreshWindows);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Today refresh tick failed: {ex}");
            }
        }
    }

    private async void OnDoseLogged(object? sender, string medicineName)
    {
        try { await Toast.Make($"{medicineName} logged").Show(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Toast failed: {ex}"); }
    }

    private async void OnAddMedicineRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<MedicineFormPage>(Navigation);

    private async void OnDoseTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not TodayScheduleItem item) return;
        await NavigationHelper.PushAsync<MedicineDetailPage>(Navigation, p => p.SetMedicineId(item.MedicineId));
    }

    private async void OnAddMedicineClicked(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<MedicineFormPage>(Navigation);

    private async void OnManageMedicinesClicked(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<MedicineListPage>(Navigation);

    // ── Quick actions and insight cards ──────────────────────────────────────────────────────

    private async void OnAddVitalTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<MetricFormPage>(Navigation, p => p.SetPreselectedType(null));

    private async void OnAddLabTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<LabEntryPage>(Navigation);

    private async void OnScanTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<ScanPrescriptionPage>(Navigation);

    private async void OnAskAiTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.PushAsync<AskAiPage>(Navigation);

    private async void OnLabAttentionTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.GoToTabAsync("labs");

    private async void OnFollowUpTapped(object? sender, TappedEventArgs e)
        => await NavigationHelper.GoToTabAsync("doctors");
}
