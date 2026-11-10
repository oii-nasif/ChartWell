using CommunityToolkit.Maui.Alerts;
using HealthTracker.Models;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

/// <summary>
/// Add and edit a health metric reading. Callers configure the mode before pushing:
/// <see cref="SetPreselectedType"/> for add (null = let the user pick) or <see cref="LoadMetric"/> for edit.
/// </summary>
public partial class MetricFormPage : ContentPage
{
    private readonly MetricFormViewModel _vm;

    public MetricFormPage(MetricFormViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        _vm.StartAdd();
        _vm.SavedSuccessfully += OnSaved;
    }

    /// <summary>Add mode. Pass a MetricTypes constant to preselect, or null for no selection.</summary>
    public void SetPreselectedType(string? metricType) => _vm.StartAdd(metricType);

    /// <summary>Edit mode for an existing reading.</summary>
    public void LoadMetric(HealthMetric metric) => _vm.LoadForEdit(metric);

    private void OnTypeChipTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is MetricTypeOption option && _vm.SelectTypeCommand.CanExecute(option))
            _vm.SelectTypeCommand.Execute(option);
    }

    private async void OnSaved(object? sender, EventArgs e)
    {
        try
        {
            await Toast.Make(_vm.IsEditMode ? "Reading updated" : "Reading saved").Show();
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Post-save navigation failed: {ex}");
        }
    }
}
