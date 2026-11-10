using System.Collections.ObjectModel;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.Services.Ai;
using HealthTracker.Services.Cloud;
using HealthTracker.Services.Security;

namespace HealthTracker.ViewModels;

/// <summary>
/// Settings state. Each long-running operation has its own busy flag so one card can show progress
/// while the rest of the page stays interactive. Confirmation dialogs stay here (they were already);
/// successes are surfaced through <see cref="Notified"/> and shown as a Toast by the page, and a
/// successful restore raises <see cref="RestoreCompleted"/> so the page can reload the whole app.
/// </summary>
public class SettingsViewModel : BaseViewModel
{
    public const string BiometricPreferenceKey = PinLockViewModel.BiometricPreferenceKey;

    private readonly BackupService _backupService;
    private readonly DatabaseService _databaseService;
    private readonly HealthReportService _reportService;
    private readonly IReminderService _reminderService;
    private readonly ThemeService _themeService;
    private readonly IBiometricService _biometrics;
    private readonly IGoogleAuthService _auth;
    private readonly ICloudSyncService _cloud;

    /// <summary>Short success/info message for a Toast.</summary>
    public event EventHandler<string>? Notified;

    /// <summary>A restore (local or cloud) replaced the database; the page must reload the UI.</summary>
    public event EventHandler? RestoreCompleted;

    // ---------------- Appearance ----------------

    public bool IsSystemTheme => _themeService.Mode == AppThemeMode.System;
    public bool IsLightTheme => _themeService.Mode == AppThemeMode.Light;
    public bool IsDarkTheme => _themeService.Mode == AppThemeMode.Dark;
    public ICommand SetThemeCommand { get; }

    // ---------------- Security ----------------

    private bool _pinConfigured;
    public bool PinConfigured
    {
        get => _pinConfigured;
        private set
        {
            _pinConfigured = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinStatusText));
            OnPropertyChanged(nameof(PinActionText));
            OnPropertyChanged(nameof(CanUseBiometric));
            OnPropertyChanged(nameof(BiometricCaption));
        }
    }
    public string PinStatusText => PinConfigured ? "PIN is set. Required when the app opens or returns after 2 minutes." : "No PIN set. Anyone with the phone can open the app.";
    public string PinActionText => PinConfigured ? "Change" : "Set PIN";

    private bool _isPinEditorOpen;
    public bool IsPinEditorOpen
    {
        get => _isPinEditorOpen;
        set { _isPinEditorOpen = value; OnPropertyChanged(); }
    }

    private string _newPin = string.Empty;
    public string NewPin
    {
        get => _newPin;
        set { _newPin = value; OnPropertyChanged(); }
    }

    private string _confirmPin = string.Empty;
    public string ConfirmPin
    {
        get => _confirmPin;
        set { _confirmPin = value; OnPropertyChanged(); }
    }

    private bool _biometricAvailable;
    public bool BiometricAvailable
    {
        get => _biometricAvailable;
        private set { _biometricAvailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanUseBiometric)); OnPropertyChanged(nameof(BiometricCaption)); }
    }
    public bool CanUseBiometric => PinConfigured && BiometricAvailable;
    public string BiometricCaption => !BiometricAvailable
        ? "No fingerprint, face or screen lock is set up on this device."
        : PinConfigured ? "Use the device's fingerprint or face unlock instead of typing the PIN." : "Set a PIN first.";

    private bool _biometricEnabled;
    public bool BiometricEnabled
    {
        get => _biometricEnabled;
        set
        {
            if (_biometricEnabled == value) return;
            _biometricEnabled = value;
            OnPropertyChanged();
            Preferences.Default.Set(BiometricPreferenceKey, value);
        }
    }

    public ICommand TogglePinEditorCommand { get; }
    public ICommand SavePinCommand { get; }
    public ICommand RemovePinCommand { get; }

    // ---------------- Google Drive ----------------

    public bool IsCloudConfigured => _auth.IsConfigured;
    public bool IsCloudNotConfigured => !_auth.IsConfigured;

    public bool IsSignedIn => _auth.IsSignedIn;
    public bool IsSignedOut => !_auth.IsSignedIn;
    public string AccountText => _auth.IsSignedIn ? (_auth.AccountEmail ?? "Google account") : "Not signed in";
    public string SignInOutText => _auth.IsSignedIn ? "Sign out" : "Sign in";

    private string _lastBackupText = "Last backup: never";
    public string LastBackupText
    {
        get => _lastBackupText;
        private set { _lastBackupText = value; OnPropertyChanged(); }
    }

    private bool _isSigningIn;
    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set { _isSigningIn = value; OnPropertyChanged(); }
    }

    private bool _isBackingUp;
    public bool IsBackingUp
    {
        get => _isBackingUp;
        private set { _isBackingUp = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCloudIdle)); }
    }

    private bool _isRestoringCloud;
    public bool IsRestoringCloud
    {
        get => _isRestoringCloud;
        private set { _isRestoringCloud = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCloudIdle)); }
    }

    public bool IsCloudIdle => !IsBackingUp && !IsRestoringCloud;

    private double _cloudProgress;
    public double CloudProgress
    {
        get => _cloudProgress;
        private set { _cloudProgress = value; OnPropertyChanged(); }
    }

    private string _cloudProgressText = string.Empty;
    public string CloudProgressText
    {
        get => _cloudProgressText;
        private set { _cloudProgressText = value; OnPropertyChanged(); }
    }

    public bool AutoBackupEnabled
    {
        get => _cloud.AutoBackupEnabled;
        set
        {
            if (_cloud.AutoBackupEnabled == value) return;
            _cloud.AutoBackupEnabled = value;
            OnPropertyChanged();
        }
    }

    public ICommand SignInOutCommand { get; }
    public ICommand BackupToCloudCommand { get; }
    public ICommand RestoreFromCloudCommand { get; }

    // ---------------- Local backup ----------------

    private bool _isExporting;
    public bool IsExporting
    {
        get => _isExporting;
        private set { _isExporting = value; OnPropertyChanged(); }
    }

    private bool _isRestoringLocal;
    public bool IsRestoringLocal
    {
        get => _isRestoringLocal;
        private set { _isRestoringLocal = value; OnPropertyChanged(); }
    }

    public ICommand ExportBackupCommand { get; }
    public ICommand RestoreFromFileCommand { get; }

    // ---------------- AI ----------------

    private bool _aiConfigured;
    public bool AiConfigured
    {
        get => _aiConfigured;
        private set
        {
            _aiConfigured = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AiNotConfigured));
            OnPropertyChanged(nameof(ApiKeyActionText));
        }
    }
    public bool AiNotConfigured => !AiConfigured;
    public string ApiKeyActionText => AiConfigured ? "Replace" : "Set key";

    private string _maskedApiKey = "Not set";
    public string MaskedApiKey
    {
        get => _maskedApiKey;
        private set { _maskedApiKey = value; OnPropertyChanged(); }
    }

    private bool _isApiKeyEditorOpen;
    public bool IsApiKeyEditorOpen
    {
        get => _isApiKeyEditorOpen;
        set { _isApiKeyEditorOpen = value; OnPropertyChanged(); }
    }

    private string _apiKeyInput = string.Empty;
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set { _apiKeyInput = value; OnPropertyChanged(); }
    }

    /// <summary>Filled from the Gemini API with the models this key can use; a built-in list until then.</summary>
    public ObservableCollection<string> AiModels { get; } = new(GeminiClient.FallbackModels);

    private string _selectedAiModel = GeminiClient.Model;
    public string SelectedAiModel
    {
        get => _selectedAiModel;
        set
        {
            if (string.IsNullOrEmpty(value) || _selectedAiModel == value) return;
            _selectedAiModel = value;
            OnPropertyChanged();
            GeminiClient.Model = value;
        }
    }

    public ICommand ToggleApiKeyEditorCommand { get; }
    public ICommand SaveApiKeyCommand { get; }
    public ICommand RemoveApiKeyCommand { get; }

    // ---------------- Health report ----------------

    private bool _isGeneratingReport;
    public bool IsGeneratingReport
    {
        get => _isGeneratingReport;
        private set { _isGeneratingReport = value; OnPropertyChanged(); }
    }

    public ICommand GenerateReportCommand { get; }
    public ICommand GenerateReportWithFilesCommand { get; }

    // ---------------- Danger zone / About ----------------

    private bool _isDeleting;
    public bool IsDeleting
    {
        get => _isDeleting;
        private set { _isDeleting = value; OnPropertyChanged(); }
    }

    public ICommand DeleteAllDataCommand { get; }

    public string VersionText => $"Version {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    public SettingsViewModel(BackupService backupService, DatabaseService databaseService,
        HealthReportService reportService, IReminderService reminderService, ThemeService themeService,
        IBiometricService biometrics, IGoogleAuthService auth, ICloudSyncService cloud)
    {
        _backupService = backupService;
        _databaseService = databaseService;
        _reportService = reportService;
        _reminderService = reminderService;
        _themeService = themeService;
        _biometrics = biometrics;
        _auth = auth;
        _cloud = cloud;

        SetThemeCommand = new Command<string>(SetTheme);
        TogglePinEditorCommand = new Command(() => { ClearError(); IsPinEditorOpen = !IsPinEditorOpen; });
        SavePinCommand = new Command(async () => await SavePin());
        RemovePinCommand = new Command(async () => await RemovePin());
        SignInOutCommand = new Command(async () => await SignInOut());
        BackupToCloudCommand = new Command(async () => await BackupToCloud());
        RestoreFromCloudCommand = new Command(async () => await RestoreFromCloud());
        ExportBackupCommand = new Command(async () => await ExportBackup());
        RestoreFromFileCommand = new Command(async () => await RestoreFromFile());
        ToggleApiKeyEditorCommand = new Command(() => { ClearError(); ApiKeyInput = string.Empty; IsApiKeyEditorOpen = !IsApiKeyEditorOpen; });
        SaveApiKeyCommand = new Command(async () => await SaveApiKey());
        RemoveApiKeyCommand = new Command(async () => await RemoveApiKey());
        GenerateReportCommand = new Command(async () => await GenerateReport(false));
        GenerateReportWithFilesCommand = new Command(async () => await GenerateReport(true));
        DeleteAllDataCommand = new Command(async () => await DeleteAllData());

        _biometricEnabled = Preferences.Default.Get(BiometricPreferenceKey, false);
    }

    /// <summary>Loads everything that needs an await (PIN, biometrics, API key, cloud info). Call from OnAppearing.</summary>
    public async Task LoadAsync()
    {
        await LoadPinStatus();
        await LoadBiometricAvailability();
        await LoadApiKeyStatus();
        RefreshCloudState();
        await RefreshLastBackupText();
    }

    // ---------------- Appearance ----------------

    private void SetTheme(string? mode)
    {
        if (!Enum.TryParse<AppThemeMode>(mode, ignoreCase: true, out var parsed)) return;
        try
        {
            _themeService.SetMode(parsed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Theme change failed: {ex}");
            SetError("Could not change the theme.");
        }
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    // ---------------- Security ----------------

    // True when SecureStorage could not be read, so "no PIN" is unknown rather than confirmed.
    private bool _pinStatusUnknown;

    private async Task LoadPinStatus()
    {
        try
        {
            var pin = await SecureStorage.Default.GetAsync("app_pin");
            PinConfigured = !string.IsNullOrEmpty(pin);
            _pinStatusUnknown = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PIN status read failed: {ex}");
            PinConfigured = false;
            _pinStatusUnknown = true;
        }
    }

    private async Task LoadBiometricAvailability()
    {
        try { BiometricAvailable = await _biometrics.IsAvailableAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Biometric availability failed: {ex}");
            BiometricAvailable = false;
        }
        // Only switch the preference off when the PIN is known to be absent. A transient SecureStorage
        // read error must not silently disable the user's choice.
        if (!CanUseBiometric && BiometricEnabled && !_pinStatusUnknown) BiometricEnabled = false;
    }

    private async Task SavePin()
    {
        ClearError();
        if (!ValidationHelper.IsValidPin(NewPin, out var err)) { SetError(err); return; }
        if (NewPin != ConfirmPin) { SetError("The two PINs do not match."); return; }
        try
        {
            await SecureStorage.Default.SetAsync("app_pin", NewPin);
            var wasConfigured = PinConfigured;
            PinConfigured = true;
            NewPin = string.Empty;
            ConfirmPin = string.Empty;
            IsPinEditorOpen = false;
            Notified?.Invoke(this, wasConfigured ? "PIN changed" : "App lock enabled");
        }
        catch (Exception ex)
        {
            SetError("Could not save the PIN.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task RemovePin()
    {
        ClearError();
        var ok = await Shell.Current.DisplayAlert("Remove app lock?",
            "The app will open without asking for a PIN. Biometric unlock is turned off too.",
            "Remove", "Cancel");
        if (!ok) return;
        try
        {
            SecureStorage.Default.Remove("app_pin");
            PinConfigured = false;
            BiometricEnabled = false;
            IsPinEditorOpen = false;
            NewPin = string.Empty;
            ConfirmPin = string.Empty;
            Notified?.Invoke(this, "App lock removed");
        }
        catch (Exception ex)
        {
            SetError("Could not remove the PIN.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ---------------- Google Drive ----------------

    private void RefreshCloudState()
    {
        OnPropertyChanged(nameof(IsCloudConfigured));
        OnPropertyChanged(nameof(IsCloudNotConfigured));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(SignInOutText));
        OnPropertyChanged(nameof(AutoBackupEnabled));
    }

    private async Task RefreshLastBackupText()
    {
        if (!_auth.IsConfigured || !_auth.IsSignedIn)
        {
            LastBackupText = "Last backup: never";
            return;
        }
        try
        {
            var info = await _cloud.GetLatestBackupInfoAsync();
            if (info is null)
            {
                var local = _cloud.LastBackupUtc;
                LastBackupText = local.HasValue ? $"Last backup: {Relative(local.Value)}" : "Last backup: never";
                return;
            }
            LastBackupText = $"Last backup: {Relative(info.ModifiedUtc)} · {FormatBytes(info.SizeBytes)}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Backup info failed: {ex}");
            var local = _cloud.LastBackupUtc;
            LastBackupText = local.HasValue ? $"Last backup: {Relative(local.Value)}" : "Last backup: unknown";
        }
    }

    private static string Relative(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return (int)span.TotalHours == 1 ? "1 hour ago" : $"{(int)span.TotalHours} hours ago";
        if (span.TotalDays < 7) return (int)span.TotalDays == 1 ? "yesterday" : $"{(int)span.TotalDays} days ago";
        return utc.ToLocalTime().ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }

    private async Task SignInOut()
    {
        if (IsSigningIn || !IsCloudConfigured) return;
        ClearError();
        IsSigningIn = true;
        try
        {
            if (_auth.IsSignedIn)
            {
                var ok = await Shell.Current.DisplayAlert("Sign out of Google?",
                    "Automatic backups stop until you sign in again. The backup already in Drive is kept.",
                    "Sign out", "Cancel");
                if (!ok) return;
                await _auth.SignOutAsync();
                Notified?.Invoke(this, "Signed out");
            }
            else
            {
                var ok = await _auth.SignInAsync();
                if (!ok) SetError("Google sign-in was cancelled or failed. Check your connection and try again.");
                else Notified?.Invoke(this, "Signed in to Google Drive");
            }
        }
        catch (Exception ex)
        {
            SetError("Google sign-in failed. " + ex.Message);
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsSigningIn = false;
            RefreshCloudState();
            await RefreshLastBackupText();
        }
    }

    private async Task BackupToCloud()
    {
        if (!IsCloudIdle || !IsSignedIn) return;
        ClearError();
        IsBackingUp = true;
        CloudProgress = 0;
        CloudProgressText = "Preparing backup...";
        try
        {
            var progress = new Progress<double>(p =>
            {
                CloudProgress = Math.Clamp(p, 0, 1);
                CloudProgressText = p < 0.05 ? "Preparing backup..." : $"Uploading... {(int)(CloudProgress * 100)}%";
            });
            var result = await _cloud.BackupNowAsync(progress);
            if (result.Succeeded) Notified?.Invoke(this, "Backed up to Google Drive");
            else SetError(result.Message);
        }
        catch (Exception ex)
        {
            SetError("Backup failed. " + ex.Message);
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsBackingUp = false;
            CloudProgressText = string.Empty;
            await RefreshLastBackupText();
        }
    }

    private async Task RestoreFromCloud()
    {
        if (!IsCloudIdle || !IsSignedIn) return;
        ClearError();
        var ok = await Shell.Current.DisplayAlert("Restore from Google Drive?",
            "This replaces ALL data on this device with the backup stored in Drive. Anything added since that backup will be lost.",
            "Restore", "Cancel");
        if (!ok) return;

        IsRestoringCloud = true;
        CloudProgress = 0;
        CloudProgressText = "Downloading backup...";
        try
        {
            var progress = new Progress<double>(p =>
            {
                CloudProgress = Math.Clamp(p, 0, 1);
                CloudProgressText = $"Restoring... {(int)(CloudProgress * 100)}%";
            });
            var result = await _cloud.RestoreLatestAsync(progress);
            if (!result.Succeeded)
            {
                SetError(result.Message);
                return;
            }
            await AfterRestore();
        }
        catch (Exception ex)
        {
            SetError("Restore failed. " + ex.Message);
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsRestoringCloud = false;
            CloudProgressText = string.Empty;
        }
    }

    // ---------------- Local backup ----------------

    private async Task ExportBackup()
    {
        if (IsExporting) return;
        ClearError();
        IsExporting = true;
        try { await _backupService.ShareBackupAsync(); }
        catch (Exception ex)
        {
            SetError("Could not create the backup. " + ex.Message);
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsExporting = false; }
    }

    private async Task RestoreFromFile()
    {
        if (IsRestoringLocal) return;
        ClearError();
        var ok = await Shell.Current.DisplayAlert("Restore from a backup file?",
            "This replaces ALL data on this device with the contents of the chosen backup. Anything added since that backup will be lost.",
            "Choose file", "Cancel");
        if (!ok) return;

        IsRestoringLocal = true;
        try
        {
            var result = await _backupService.RestoreFromPickedFileAsync();
            if (!result.Succeeded)
            {
                // "Cancelled" style messages are not errors worth a red banner.
                if (!string.IsNullOrWhiteSpace(result.Message) && !result.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                    SetError(result.Message);
                return;
            }
            await AfterRestore();
        }
        catch (Exception ex)
        {
            SetError("Restore failed. " + ex.Message);
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsRestoringLocal = false; }
    }

    /// <summary>Reminders are rebuilt from the restored DB, then the page reloads the UI.</summary>
    private async Task AfterRestore()
    {
        try { await _reminderService.RescheduleAll(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RescheduleAll after restore failed: {ex}"); }
        RestoreCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- AI ----------------

    private async Task LoadApiKeyStatus()
    {
        try
        {
            var key = await AiParserService.GetApiKeyAsync();
            AiConfigured = !string.IsNullOrWhiteSpace(key);
            MaskedApiKey = AiConfigured ? Mask(key!) : "Not set";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"API key read failed: {ex}");
            AiConfigured = false;
            MaskedApiKey = "Not set";
        }
        _selectedAiModel = GeminiClient.Model;
        OnPropertyChanged(nameof(SelectedAiModel));
        if (AiConfigured) await LoadAiModels();
    }

    /// <summary>Replaces the picker items with the live model list and keeps the selection valid.</summary>
    private async Task LoadAiModels()
    {
        try
        {
            var models = await GeminiClient.ListModelsAsync(refresh: true);
            var current = GeminiClient.Model;
            AiModels.Clear();
            foreach (var m in models) AiModels.Add(m);
            // The saved model may have been retired since it was chosen; fall back to the best one listed.
            if (!AiModels.Contains(current) && AiModels.Count > 0) current = AiModels[0];
            if (!AiModels.Contains(current)) AiModels.Insert(0, current);
            _selectedAiModel = string.Empty;   // force the setter to raise even if the value is unchanged
            SelectedAiModel = current;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Model list refresh failed: {ex}");
        }
    }

    private static string Mask(string key)
        => key.Length > 8 ? key[..4] + new string('•', 8) + key[^4..] : new string('•', 8);

    private async Task SaveApiKey()
    {
        ClearError();
        var key = ApiKeyInput?.Trim() ?? string.Empty;
        if (key.Length < 20 || key.Contains(' '))
        {
            SetError("That doesn't look like a Gemini API key. Paste the full key from AI Studio.");
            return;
        }
        try
        {
            await AiParserService.SaveApiKeyAsync(key);
            AiConfigured = true;
            MaskedApiKey = Mask(key);
            ApiKeyInput = string.Empty;
            IsApiKeyEditorOpen = false;
            Notified?.Invoke(this, "Gemini key saved");
            await LoadAiModels();
        }
        catch (Exception ex)
        {
            SetError("Could not store the API key.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task RemoveApiKey()
    {
        ClearError();
        var ok = await Shell.Current.DisplayAlert("Remove Gemini key?",
            "AI scanning and the assistant switch back to the built-in text parsers.", "Remove", "Cancel");
        if (!ok) return;
        try
        {
            await AiParserService.ClearApiKeyAsync();
            AiConfigured = false;
            MaskedApiKey = "Not set";
            IsApiKeyEditorOpen = false;
            Notified?.Invoke(this, "Gemini key removed");
        }
        catch (Exception ex)
        {
            SetError("Could not remove the API key.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ---------------- Health report ----------------

    private async Task GenerateReport(bool includeFiles)
    {
        if (IsGeneratingReport) return;
        ClearError();
        IsGeneratingReport = true;
        try
        {
            var (filePath, warning) = await _reportService.GenerateAsync(includeFiles);

            if (!string.IsNullOrEmpty(warning))
                await Shell.Current.DisplayAlert("Large file", warning, "OK");

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Health Report",
                File = new ShareFile(filePath, "application/pdf")
            });
        }
        catch (Exception ex)
        {
            SetError("Could not generate the report. " + ex.Message);
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsGeneratingReport = false; }
    }

    // ---------------- Danger zone ----------------

    private async Task DeleteAllData()
    {
        if (IsDeleting) return;
        var first = await Shell.Current.DisplayAlert(
            "Delete all data?",
            "This permanently deletes ALL medicines, lab results, vitals, reports, doctor visits, logs and your profile. This cannot be undone.",
            "Delete everything", "Cancel");
        if (!first) return;

        var second = await Shell.Current.DisplayAlert(
            "Are you sure?",
            "All your health data will be lost forever. Consider exporting a backup first.",
            "Yes, delete all", "Cancel");
        if (!second) return;

        ClearError();
        IsDeleting = true;
        try
        {
            // Reminders for deleted medicines must not keep firing until the next cold start.
            await _reminderService.CancelAll();
            await _databaseService.DeleteAllDataAsync();

            // Remove stored report/prescription files too, otherwise they linger and get backed up.
            var baseFolder = FileStorageHelper.GetBaseFolder();
            if (Directory.Exists(baseFolder))
            {
                try { Directory.Delete(baseFolder, recursive: true); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"File cleanup failed: {ex}"); }
            }

            Notified?.Invoke(this, "All data deleted");
        }
        catch (Exception ex)
        {
            SetError("Could not delete the data. " + ex.Message);
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsDeleting = false; }
    }
}
