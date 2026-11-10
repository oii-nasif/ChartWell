using HealthTracker.ViewModels;

namespace HealthTracker.Views;

/// <summary>
/// Modal lock screen pushed by App.CheckPinAndLock. Swallows the back button; the only ways out are a
/// correct PIN, a successful biometric check, or the "Forgot PIN?" full reset.
/// </summary>
public partial class PinLockPage : ContentPage
{
    private readonly PinLockViewModel _vm;
    private bool _initialised;

    public PinLockPage(PinLockViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        vm.UnlockSucceeded += OnUnlockSucceeded;
        vm.WrongPinEntered += OnWrongPinEntered;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (!_initialised)
            {
                _initialised = true;
                await _vm.InitializeAsync();
            }
            await _vm.AutoPromptBiometricAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PinLockPage init failed: {ex}");
        }
    }

    // The lock screen must not be dismissible with the hardware/gesture back button.
    protected override bool OnBackButtonPressed() => true;

    private async void OnUnlockSucceeded(object? sender, EventArgs e)
    {
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
            else if (Navigation.NavigationStack.Contains(this))
                await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unlock navigation failed: {ex}");
        }
    }

    private async void OnWrongPinEntered(object? sender, EventArgs e)
    {
        try
        {
            // Quick horizontal shake of the dots row.
            await DotsRow.TranslateTo(-12, 0, 40);
            await DotsRow.TranslateTo(12, 0, 40);
            await DotsRow.TranslateTo(-8, 0, 40);
            await DotsRow.TranslateTo(8, 0, 40);
            await DotsRow.TranslateTo(0, 0, 40);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shake animation failed: {ex}");
        }
    }

    private async void OnForgotPinClicked(object? sender, EventArgs e)
    {
        try
        {
            var first = await DisplayAlert("Forgot your PIN?",
                "The PIN is stored only on this device and cannot be recovered. The only way to regain access is to erase all of the app's data (medicines, doses, metrics, reports, doctor visits and files). A backup made earlier can be restored afterwards from Settings.",
                "Erase all data", "Cancel");
            if (!first) return;

            var second = await DisplayAlert("Erase everything?",
                "This permanently deletes all health data on this device and removes the PIN. This cannot be undone.",
                "Yes, erase and unlock", "Cancel");
            if (!second) return;

            await _vm.EraseAllDataAndUnlockAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Forgot PIN flow failed: {ex}");
        }
    }
}
