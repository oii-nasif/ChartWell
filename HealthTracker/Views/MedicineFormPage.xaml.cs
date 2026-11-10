using CommunityToolkit.Maui.Alerts;
using HealthTracker.Models;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

/// <summary>Add and Edit medicine in one page. Blank by default; call <see cref="LoadMedicine"/> or <see cref="PrefillFromPending"/> before pushing.</summary>
public partial class MedicineFormPage : ContentPage
{
    private readonly MedicineFormViewModel _vm;

    /// <summary>
    /// Awaited after the medicine was saved and before the page pops, so the caller can finish its own
    /// follow-up work (e.g. removing a pending scan entry) before the previous page reloads on appearing.
    /// </summary>
    public Func<Task>? Saved { get; set; }

    public MedicineFormPage(MedicineFormViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        _vm.SavedSuccessfully += OnSaved;
    }

    /// <summary>Edit mode entry point (replaces EditMedicinePage.LoadMedicine).</summary>
    public Task LoadMedicine(int medicineId) => _vm.Load(medicineId);

    /// <summary>Add mode seeded from a scanned prescription entry.</summary>
    public void PrefillFromPending(PendingMedicine pending) => _vm.PrefillFromPending(pending);

    private async void OnSaved(object? sender, EventArgs e)
    {
        try
        {
            if (Saved is { } saved)
            {
                try { await saved(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MedicineFormPage.Saved hook failed: {ex}"); }
            }
            await Toast.Make(_vm.IsEditMode ? "Changes saved" : "Medicine added").Show();
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MedicineFormPage.OnSaved failed: {ex}");
        }
    }
}
