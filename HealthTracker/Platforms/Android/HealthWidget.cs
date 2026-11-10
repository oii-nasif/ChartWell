using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;

namespace HealthTracker;

[BroadcastReceiver(Name = "com.healthapp.healthtracker.HealthWidget", Label = "Health Tracker Widget", Exported = true)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_info")]
public class HealthWidget : AppWidgetProvider
{
    private const string Tag = "HealthWidget";

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context == null || appWidgetManager == null || appWidgetIds == null) return;

        // GoAsync keeps the BroadcastReceiver alive while we do background work
        var pending = GoAsync();
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                foreach (var id in appWidgetIds)
                    UpdateWidget(context, appWidgetManager, id);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error(Tag, $"OnUpdate error: {ex}");
            }
            finally
            {
                try { pending?.Finish(); } catch { /* receiver already finished */ }
            }
        });
    }

    /// <summary>
    /// Refreshes every placed instance of the widget. Safe to call from anywhere (ViewModels included,
    /// under <c>#if ANDROID</c>) after data the widget shows has changed, e.g. after a dose is taken.
    /// Never throws. <paramref name="context"/> defaults to the application context.
    /// </summary>
    public static void RequestUpdate(Context? context = null)
    {
        try
        {
            UpdateAllWidgets(context ?? Android.App.Application.Context);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, $"RequestUpdate error: {ex}");
        }
    }

    public static void UpdateAllWidgets(Context context)
    {
        try
        {
            var manager = AppWidgetManager.GetInstance(context);
            if (manager == null) return;
            var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(HealthWidget))));
            if (ids == null || ids.Length == 0) return;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (var id in ids)
                        UpdateWidget(context, manager, id);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error(Tag, $"UpdateAllWidgets (worker) error: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, $"UpdateAllWidgets error: {ex}");
        }
    }

    public static void UpdateWidget(Context context, AppWidgetManager manager, int widgetId)
    {
        try
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.widget_layout);

            // Tap to open app
            var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
            if (launchIntent != null)
            {
                var pendingIntent = PendingIntent.GetActivity(context, 0, launchIntent,
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                views.SetOnClickPendingIntent(Resource.Id.widget_root, pendingIntent);
            }

            views.SetTextViewText(Resource.Id.widget_date,
                DateTime.Today.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture));

            WidgetDataRecord data;
            try
            {
                data = WidgetDataHelper.GetWidgetData(context);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error(Tag, $"GetWidgetData error: {ex}");
                data = new WidgetDataRecord { ProgressText = "Tap to open app", NextDoseText = "" };
            }

            views.SetTextViewText(Resource.Id.widget_progress, data.ProgressText);
            views.SetTextViewText(Resource.Id.widget_next_dose, data.NextDoseText);
            views.SetTextViewText(Resource.Id.widget_heart_rate, data.HeartRateText);
            views.SetTextViewText(Resource.Id.widget_blood_pressure, data.BloodPressureText);

            // Horizontal ProgressBar is RemoteViews-safe; hide it when there is nothing to count.
            if (data.TotalDoses > 0)
            {
                views.SetViewVisibility(Resource.Id.widget_progress_bar, Android.Views.ViewStates.Visible);
                views.SetProgressBar(Resource.Id.widget_progress_bar, data.TotalDoses, data.TakenDoses, false);
            }
            else
            {
                views.SetViewVisibility(Resource.Id.widget_progress_bar, Android.Views.ViewStates.Gone);
            }

            manager.UpdateAppWidget(widgetId, views);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, $"UpdateWidget error: {ex}");
        }
    }
}
