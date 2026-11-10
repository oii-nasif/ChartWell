using HealthTracker.Constants;
using HealthTracker.Services;
using HealthTracker.Services.Cloud;
using HealthTracker.Views;

namespace HealthTracker;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private DateTime? _backgroundedAt;

    public App(IServiceProvider services, ThemeService themeService)
    {
        InitializeComponent();
        Services = services;
        themeService.Initialize(this);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }

    protected override async void OnStart()
    {
        base.OnStart();

        // Everything here runs inside an async void; an unhandled exception would kill the app at launch.

        // Lock FIRST so no health data is visible behind the permission prompts that follow.
        await CheckPinAndLock();

        try
        {
            if (Services.GetService<IReminderService>() is ReminderService reminders)
                reminders.Initialize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reminder initialisation failed: {ex}");
        }

        try
        {
            // Request notification permission (Android 13+)
            await Plugin.LocalNotification.LocalNotificationCenter.Current.RequestNotificationPermission();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notification permission request failed: {ex}");
        }

        try
        {
            // On Android 12+, check that exact alarm permission is granted
            await CheckExactAlarmPermission();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Exact alarm check failed: {ex}");
        }

        // Reschedule all reminders on launch (RULE-10)
        try
        {
            var reminderService = Services.GetRequiredService<IReminderService>();
            await reminderService.RescheduleAll();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RescheduleAll failed: {ex}");
        }

        TriggerCloudAutoSync();
    }

    private static async Task CheckExactAlarmPermission()
    {
#if ANDROID
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.S)
        {
            var alarmManager = Android.App.Application.Context
                .GetSystemService(Android.Content.Context.AlarmService) as Android.App.AlarmManager;
            if (alarmManager != null && !alarmManager.CanScheduleExactAlarms())
            {
                // Ask once per install; nagging every launch is worse than slightly late reminders.
                if (Preferences.Default.Get("exact_alarm_prompted", false)) return;
                Preferences.Default.Set("exact_alarm_prompted", true);

                bool goToSettings = await Shell.Current.DisplayAlert(
                    "Enable Reminders",
                    "To receive medicine reminders on time, please allow 'Alarms & Reminders' for Chartwell.",
                    "Open Settings", "Later");
                if (goToSettings)
                {
                    var intent = new Android.Content.Intent(
                        Android.Provider.Settings.ActionRequestScheduleExactAlarm,
                        Android.Net.Uri.FromParts("package", AppInfo.PackageName, null));
                    intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                    Android.App.Application.Context.StartActivity(intent);
                }
            }
        }
#endif
        await Task.CompletedTask;
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        _backgroundedAt = DateTime.Now;
        // The user has just finished editing; a throttled (24h) upload to their Drive is cheapest now.
        TriggerCloudAutoSync();
    }

    protected override async void OnResume()
    {
        base.OnResume();
        if (_backgroundedAt.HasValue &&
            (DateTime.Now - _backgroundedAt.Value).TotalMinutes >= AppConstants.BackgroundResumeThresholdMinutes)
        {
            await CheckPinAndLock();
        }
    }

    private static void TriggerCloudAutoSync()
    {
        try
        {
            var sync = Services.GetService<ICloudSyncService>();
            if (sync is null) return;
            // Fire-and-forget: AutoSyncIfDueAsync never throws and must not block the UI thread.
            // It only uploads once a manual backup or restore has happened on this device (and never an
            // empty database over a real cloud backup), so a fresh install cannot clobber the cloud copy.
            _ = Task.Run(() => sync.AutoSyncIfDueAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cloud auto-sync trigger failed: {ex}");
        }
    }

    private static async Task CheckPinAndLock()
    {
        try
        {
            string? pin;
            try
            {
                pin = await SecureStorage.GetAsync("app_pin");
            }
            catch (Exception ex)
            {
                // A keystore failure must fail closed: show the lock screen, which offers a reset path.
                System.Diagnostics.Debug.WriteLine($"PIN read failed, locking: {ex}");
                pin = "?";
            }
            if (string.IsNullOrEmpty(pin)) return;

            var navigation = Shell.Current?.Navigation;
            if (navigation is null) return;

            // Don't stack a second lock screen on top of one that is already showing.
            if (navigation.ModalStack.Any(p => p is PinLockPage)) return;

            // Modal: hides the tab bar and the nav-bar back arrow; PinLockPage swallows the back button.
            var lockPage = Services.GetRequiredService<PinLockPage>();
            await navigation.PushModalAsync(lockPage, animated: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PIN check failed: {ex}");
        }
    }
}
