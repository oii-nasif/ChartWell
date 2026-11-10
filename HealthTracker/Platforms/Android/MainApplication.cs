using Android.App;
using Android.Runtime;
using HealthTracker.Services;

namespace HealthTracker;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override void OnCreate()
	{
		base.OnCreate();

		// Notification action taps ("Take" / "Snooze") can arrive in a cold process where no Activity
		// and therefore no MAUI App exists yet, so the handlers are attached here at process start.
		// The call is idempotent; App.OnStart may call ReminderService.Initialize() as well.
		try
		{
			ReminderService.AttachNotificationHandlers(IPlatformApplication.Current!.Services);
		}
		catch (Exception ex)
		{
			Android.Util.Log.Error("HealthTracker", $"AttachNotificationHandlers failed: {ex.Message}");
		}
	}
}
