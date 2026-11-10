using CommunityToolkit.Maui.Alerts;
using HealthTracker.Services.Labs;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class LabEntryPage : ContentPage
{
    private readonly LabEntryViewModel _vm;

    public LabEntryPage(LabEntryViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        _vm.Saved += OnSaved;
        _vm.Deleted += OnDeleted;
        _vm.DeleteRequested += OnDeleteRequested;
        _vm.CustomTestRequested += OnCustomTestRequested;
    }

    /// <summary>Opens the form with one test already in the list (from a test's detail page).</summary>
    public void PresetTest(string testCode) => _vm.PresetTest(testCode);

    /// <summary>Opens the form prefilled with values read from a report.</summary>
    public void Prefill(List<LabDraft> drafts, DateTime? date, string? labName, int? reportId)
        => _vm.Prefill(drafts, date, labName, reportId);

    /// <summary>Opens the form to edit (or delete) one saved result.</summary>
    public void EditResult(int resultId) => _vm.EditResult(resultId);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await _vm.InitializeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LabEntryPage.OnAppearing failed: {ex}"); }
    }

    private async void OnSaved(object? sender, string message)
    {
        try
        {
            await Toast.Make(message).Show();
            await Navigation.PopAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LabEntryPage.OnSaved failed: {ex}"); }
    }

    private async void OnDeleted(object? sender, EventArgs e)
    {
        try
        {
            await Toast.Make("Result deleted").Show();
            await Navigation.PopAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LabEntryPage.OnDeleted failed: {ex}"); }
    }

    private async void OnDeleteRequested(object? sender, EventArgs e)
    {
        try
        {
            if (await DisplayAlert("Delete result", "Delete this result? This cannot be undone.", "Delete", "Cancel"))
                await _vm.DeleteExisting();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LabEntryPage delete failed: {ex}"); }
    }

    private async void OnCustomTestRequested(object? sender, string suggestedName)
    {
        try
        {
            var name = await DisplayPromptAsync("Your own test", "Name of the test as printed on the report",
                "Next", "Cancel", initialValue: suggestedName, maxLength: 60);
            if (string.IsNullOrWhiteSpace(name)) return;

            const string number = "A number (with a unit)";
            const string word = "Positive / Negative";
            var kind = await DisplayActionSheet("How is the result reported?", "Cancel", null, number, word);
            if (kind != number && kind != word) return;

            if (!_vm.AddCustomRow(name, qualitative: kind == word))
                await DisplayAlert("Already in the list", $"{name.Trim()} is already in the list below.", "OK");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LabEntryPage custom test failed: {ex}"); }
    }
}
