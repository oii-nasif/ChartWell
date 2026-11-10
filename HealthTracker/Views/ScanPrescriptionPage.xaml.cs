using HealthTracker.Helpers;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class ScanPrescriptionPage : ContentPage
{
    private readonly ScanPrescriptionViewModel _vm;

    public ScanPrescriptionPage(ScanPrescriptionViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        _vm.OpenSettingsRequested += OnOpenSettingsRequested;
        _vm.ReviewRequested += OnReviewRequested;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // The user may have added or removed a Gemini key in Settings while this page was in the stack.
        _vm.RefreshConfiguration();
    }

    private async void OnOpenSettingsRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<SettingsPage>(Navigation);

    /// <summary>
    /// "Review now": if the Medicines page is already below us in the stack, switch it to the review
    /// segment and pop back to it; otherwise push a fresh Medicines page on that segment.
    /// </summary>
    private async void OnReviewRequested(object? sender, EventArgs e)
    {
        try
        {
            var stack = Navigation.NavigationStack;
            var list = stack.Count >= 2 ? stack[^2] as MedicineListPage : null;
            if (list != null)
            {
                list.ShowSegment(MedicineSegment.Pending);
                await Navigation.PopAsync();
                return;
            }
            await NavigationHelper.PushAsync<MedicineListPage>(Navigation, p => p.ShowSegment(MedicineSegment.Pending));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Review navigation failed: {ex}");
        }
    }
}
