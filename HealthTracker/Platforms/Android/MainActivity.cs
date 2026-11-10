using Android.App;
using Android.Content.PM;
using Android.OS;

namespace HealthTracker;

// SingleTop: tapping a notification while the app is already running re-delivers the intent to the
// existing activity (OnNewIntent) instead of stacking a second MainActivity.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnResume()
    {
        base.OnResume();
        HealthWidget.RequestUpdate(this);
    }
}
