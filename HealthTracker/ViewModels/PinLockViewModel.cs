using System.Collections.ObjectModel;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.Services.Security;

namespace HealthTracker.ViewModels;

/// <summary>One indicator dot above the keypad; filled once the user has typed that many digits.</summary>
public class PinDotItem : BaseViewModel
{
    private bool _isFilled;
    public bool IsFilled
    {
        get => _isFilled;
        set { if (_isFilled == value) return; _isFilled = value; OnPropertyChanged(); }
    }
}

/// <summary>
/// Lock screen state. The stored PIN is read once in <see cref="InitializeAsync"/> so the page can
/// auto-submit when the typed length matches; the PIN itself is never exposed to the view.
/// </summary>
public class PinLockViewModel : BaseViewModel
{
    public const string BiometricPreferenceKey = "biometric_unlock";
    // Persisted so killing and relaunching the app does not reset the brute-force cooldown.
    private const string FailedAttemptsKey = "pin_failed_attempts";
    private const string LockoutUntilKey = "pin_lockout_until_utc";

    private readonly IBiometricService _biometrics;
    private readonly DatabaseService _databaseService;
    private readonly IReminderService _reminderService;

    private int _failedAttempts;
    private string _enteredPin = string.Empty;
    private string? _storedPin;
    private bool _storedPinUnreadable;
    private bool _isCoolingDown;
    private bool _biometricAvailable;
    private bool _biometricPromptShown;
    private bool _isBusyUnlocking;
    private int _cooldownSecondsRemaining;
    private string _prompt = "Enter your PIN";

    public ObservableCollection<PinDotItem> Dots { get; } = new();

    /// <summary>Digits typed so far. Setting it updates the dots.</summary>
    public string EnteredPin
    {
        get => _enteredPin;
        private set
        {
            _enteredPin = value;
            OnPropertyChanged();
            for (int i = 0; i < Dots.Count; i++) Dots[i].IsFilled = i < value.Length;
        }
    }

    public string Prompt
    {
        get => _prompt;
        private set { _prompt = value; OnPropertyChanged(); }
    }

    public bool IsCoolingDown
    {
        get => _isCoolingDown;
        private set { _isCoolingDown = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsKeypadEnabled)); }
    }

    public int CooldownSecondsRemaining
    {
        get => _cooldownSecondsRemaining;
        private set { _cooldownSecondsRemaining = value; OnPropertyChanged(); OnPropertyChanged(nameof(CooldownText)); }
    }

    public string CooldownText => $"Too many attempts. Try again in {CooldownSecondsRemaining}s";

    /// <summary>Fingerprint key is shown only when the user opted in and the device can do it.</summary>
    public bool ShowBiometric
    {
        get => _biometricAvailable;
        private set { _biometricAvailable = value; OnPropertyChanged(); }
    }

    public bool IsKeypadEnabled => !IsCoolingDown && !_isBusyUnlocking;

    public ICommand DigitCommand { get; }
    public ICommand BackspaceCommand { get; }
    public ICommand BiometricCommand { get; }
    public ICommand SubmitCommand { get; }

    /// <summary>Raised when the PIN or biometric check passed. App.xaml.cs pops the modal page on it.</summary>
    public event EventHandler? UnlockSucceeded;

    /// <summary>Raised on a wrong PIN so the page can shake the dots.</summary>
    public event EventHandler? WrongPinEntered;

    public PinLockViewModel(IBiometricService biometrics, DatabaseService databaseService, IReminderService reminderService)
    {
        _biometrics = biometrics;
        _databaseService = databaseService;
        _reminderService = reminderService;

        DigitCommand = new Command<string>(d => _ = OnDigit(d));
        BackspaceCommand = new Command(OnBackspace);
        BiometricCommand = new Command(async () => await TryBiometricAsync(userInitiated: true));
        SubmitCommand = new Command(async () => await Submit());

        // Default to the maximum so the dots render before the stored PIN has been read.
        ResetDots(AppConstants.PinMaxLength);
    }

    /// <summary>Reads the stored PIN once and resolves biometric availability. Safe to call repeatedly.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _storedPin = await SecureStorage.GetAsync("app_pin");
            _storedPinUnreadable = false;
        }
        catch (Exception ex)
        {
            // SecureStorage can throw if the Android keystore entry is corrupt (e.g. after a restore).
            System.Diagnostics.Debug.WriteLine($"PIN read failed: {ex}");
            _storedPin = null;
            _storedPinUnreadable = true;
            SetError("Could not read the stored PIN. Use \"Forgot PIN?\" to reset the app.");
        }

        var length = _storedPin?.Length ?? 0;
        if (length < AppConstants.PinMinLength || length > AppConstants.PinMaxLength) length = AppConstants.PinMaxLength;
        // Keep whatever the user has already typed: the stored PIN may arrive after the first taps.
        ResizeDots(length);

        RestoreLockoutState();

        // The user may have finished typing before the stored PIN was known.
        if (!IsCoolingDown && _storedPin is not null && EnteredPin.Length == Dots.Count)
            await Submit();

        try
        {
            var optedIn = Preferences.Default.Get(BiometricPreferenceKey, false);
            ShowBiometric = optedIn && await _biometrics.IsAvailableAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Biometric availability check failed: {ex}");
            ShowBiometric = false;
        }
    }

    /// <summary>Shows the biometric prompt once per page lifetime, if enabled. Called from OnAppearing.</summary>
    public async Task AutoPromptBiometricAsync()
    {
        if (!ShowBiometric || _biometricPromptShown) return;
        _biometricPromptShown = true;
        await TryBiometricAsync(userInitiated: false);
    }

    private void ResetDots(int count)
    {
        Dots.Clear();
        for (int i = 0; i < count; i++) Dots.Add(new PinDotItem());
        EnteredPin = string.Empty;
    }

    /// <summary>Changes the dot count without discarding typed digits (trims them if they no longer fit).</summary>
    private void ResizeDots(int count)
    {
        if (Dots.Count != count)
        {
            Dots.Clear();
            for (int i = 0; i < count; i++) Dots.Add(new PinDotItem());
        }
        var typed = EnteredPin.Length > count ? EnteredPin[..count] : EnteredPin;
        EnteredPin = typed; // re-applies the fill state to the (possibly new) dots
    }

    /// <summary>Loads the persisted failed-attempt count and resumes a cooldown that was still running.</summary>
    private void RestoreLockoutState()
    {
        try
        {
            _failedAttempts = Preferences.Default.Get(FailedAttemptsKey, 0);
            var lockoutTicks = Preferences.Default.Get(LockoutUntilKey, 0L);
            if (lockoutTicks <= 0) return;

            var until = new DateTime(lockoutTicks, DateTimeKind.Utc);
            var remaining = until - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                if (!IsCoolingDown)
                    _ = RunCooldown(until);
            }
            else
            {
                ClearLockoutState();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Lockout state read failed: {ex}");
        }
    }

    private void PersistFailedAttempts()
    {
        try { Preferences.Default.Set(FailedAttemptsKey, _failedAttempts); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed-attempts write failed: {ex}"); }
    }

    private void ClearLockoutState()
    {
        _failedAttempts = 0;
        try
        {
            Preferences.Default.Remove(FailedAttemptsKey);
            Preferences.Default.Remove(LockoutUntilKey);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Lockout state clear failed: {ex}"); }
    }

    private async Task OnDigit(string? digit)
    {
        if (!IsKeypadEnabled || string.IsNullOrEmpty(digit)) return;
        if (EnteredPin.Length >= Dots.Count) return;
        if (HasError) ClearError();
        EnteredPin += digit;
        if (EnteredPin.Length == Dots.Count) await Submit();
    }

    private void OnBackspace()
    {
        if (!IsKeypadEnabled || EnteredPin.Length == 0) return;
        EnteredPin = EnteredPin[..^1];
    }

    private async Task Submit()
    {
        if (IsCoolingDown || _isBusyUnlocking) return;
        if (_storedPinUnreadable)
        {
            SetError("Could not read the stored PIN. Use \"Forgot PIN?\" to reset the app.");
            EnteredPin = string.Empty;
            return;
        }
        ClearError();

        if (_storedPin is null)
        {
            // Not initialised yet (should not happen); read lazily so a tap still works.
            // InitializeAsync auto-submits when the typed length matches, so don't fall through.
            await InitializeAsync();
            if (_storedPin is null && !_storedPinUnreadable)
            {
                // Still nothing to compare against: don't leave the pad full and unresponsive.
                EnteredPin = string.Empty;
                SetError("Could not read the stored PIN. Please try again.");
            }
            return;
        }

        if (!string.IsNullOrEmpty(EnteredPin) && EnteredPin == _storedPin)
        {
            Unlock();
            return;
        }

        _failedAttempts++;
        PersistFailedAttempts();
        WrongPinEntered?.Invoke(this, EventArgs.Empty);
        // Leave the dots filled for a beat so the shake is visible, then clear.
        await Task.Delay(350);
        EnteredPin = string.Empty;

        if (_failedAttempts >= AppConstants.MaxFailedPinAttempts)
            await StartCooldown();
        else
        {
            var left = AppConstants.MaxFailedPinAttempts - _failedAttempts;
            SetError(left == 1 ? "Incorrect PIN. 1 attempt remaining." : $"Incorrect PIN. {left} attempts remaining.");
        }
    }

    private async Task TryBiometricAsync(bool userInitiated)
    {
        if (!ShowBiometric || _isBusyUnlocking || IsCoolingDown) return;
        _isBusyUnlocking = true;
        OnPropertyChanged(nameof(IsKeypadEnabled));
        try
        {
            var result = await _biometrics.AuthenticateAsync("Unlock Chartwell", "Confirm it's you to open your health data");
            switch (result)
            {
                case BiometricResult.Succeeded:
                    Unlock();
                    break;
                case BiometricResult.NotAvailable:
                case BiometricResult.NotEnrolled:
                    ShowBiometric = false;
                    if (userInitiated) SetError("Biometric unlock isn't available on this device. Enter your PIN.");
                    break;
                case BiometricResult.Failed:
                    if (userInitiated) SetError("Biometric check failed. Enter your PIN.");
                    break;
                case BiometricResult.Cancelled:
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Biometric auth failed: {ex}");
            if (userInitiated) SetError("Biometric check failed. Enter your PIN.");
        }
        finally
        {
            _isBusyUnlocking = false;
            OnPropertyChanged(nameof(IsKeypadEnabled));
        }
    }

    private void Unlock()
    {
        ClearLockoutState();
        EnteredPin = string.Empty;
        ClearError();
        UnlockSucceeded?.Invoke(this, EventArgs.Empty);
    }

    private Task StartCooldown()
    {
        var until = DateTime.UtcNow.AddSeconds(AppConstants.PinCooldownSeconds);
        try { Preferences.Default.Set(LockoutUntilKey, until.Ticks); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Lockout write failed: {ex}"); }
        return RunCooldown(until);
    }

    /// <summary>Counts down to <paramref name="untilUtc"/>; also used to resume a persisted lockout after a relaunch.</summary>
    private async Task RunCooldown(DateTime untilUtc)
    {
        IsCoolingDown = true;
        Prompt = "Locked";
        ClearError();
        EnteredPin = string.Empty;
        while (true)
        {
            var remaining = (int)Math.Ceiling((untilUtc - DateTime.UtcNow).TotalSeconds);
            if (remaining <= 0) break;
            CooldownSecondsRemaining = remaining;
            await Task.Delay(1000);
        }
        ClearLockoutState();
        IsCoolingDown = false;
        Prompt = "Enter your PIN";
    }

    /// <summary>
    /// The only PIN reset path: wipe everything (reminders, database, stored files, PIN) and unlock the
    /// now-empty app. The page shows the two confirmation dialogs before calling this.
    /// </summary>
    public async Task EraseAllDataAndUnlockAsync()
    {
        if (_isBusyUnlocking) return;
        _isBusyUnlocking = true;
        OnPropertyChanged(nameof(IsKeypadEnabled));
        IsBusy = true;
        ClearError();
        try
        {
            try { await _reminderService.CancelAll(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CancelAll failed: {ex}"); }

            await _databaseService.DeleteAllDataAsync();

            var baseFolder = FileStorageHelper.GetBaseFolder();
            if (Directory.Exists(baseFolder))
            {
                try { Directory.Delete(baseFolder, recursive: true); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"File cleanup failed: {ex}"); }
            }

            try { SecureStorage.Default.Remove("app_pin"); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PIN removal failed: {ex}"); }
            Preferences.Default.Remove(BiometricPreferenceKey);
            ClearLockoutState();

            _storedPin = null;
            _storedPinUnreadable = false;
            Unlock();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reset failed: {ex}");
            SetError("Could not erase the app data. Please try again.");
        }
        finally
        {
            IsBusy = false;
            _isBusyUnlocking = false;
            OnPropertyChanged(nameof(IsKeypadEnabled));
        }
    }
}
