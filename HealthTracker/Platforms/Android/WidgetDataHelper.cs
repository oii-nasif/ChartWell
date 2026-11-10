using System.Globalization;
using Android.Content;
using HealthTracker.Constants;
using HealthTracker.Models;
using HealthTracker.Services;
using SQLite;

namespace HealthTracker;

public class WidgetDataRecord
{
    public string ProgressText { get; set; } = "-- of -- doses today";
    public string NextDoseText { get; set; } = "";
    public string HeartRateText { get; set; } = "♥ --";
    public string BloodPressureText { get; set; } = "BP --";
    /// <summary>Doses taken today; drives the widget progress bar. 0 when unknown.</summary>
    public int TakenDoses { get; set; }
    /// <summary>Doses planned for today; 0 hides the progress bar.</summary>
    public int TotalDoses { get; set; }
}

/// <summary>
/// Reads the app database read-only for the home-screen widget. Runs outside the MAUI DI container
/// (the widget receiver may wake a cold process), so it cannot use the services and has to mirror
/// their rules with raw SQL.
/// </summary>
public static class WidgetDataHelper
{
    public static WidgetDataRecord GetWidgetData(Context context)
    {
        var dbPath = System.IO.Path.Combine(context.FilesDir!.AbsolutePath, AppConstants.DatabaseFilename);
        if (!System.IO.File.Exists(dbPath))
            return new WidgetDataRecord { ProgressText = "No data yet", NextDoseText = "Open app to add medicines" };

        SQLiteConnection? db = null;
        try
        {
            db = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadOnly);

            // The app writes all dates/times with the invariant culture; compare with the same format,
            // otherwise devices with a non-Gregorian calendar or a '.' time separator never match.
            var inv = CultureInfo.InvariantCulture;
            var todayStr = DateTime.Today.ToString("yyyy-MM-dd", inv);
            var tomorrowStr = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", inv);
            var nowTimeOfDay = DateTime.Now.TimeOfDay;

            // Raw SQL into the plain model classes (sqlite-net maps by column name; unselected columns keep
            // their defaults), so the widget can apply the very same reachability rule as the Today page.
            var medicines = db.Query<Medicine>(
                "SELECT Id, StartDate, EndDate, CreatedAt, IsActive FROM Medicine WHERE IsActive = 1");
            if (medicines.Count == 0)
                return new WidgetDataRecord { ProgressText = "No active medicines", NextDoseText = "Add medicines in the app" };

            var idList = string.Join(",", medicines.Select(m => m.Id));
            var medicineById = medicines.ToDictionary(m => m.Id);

            var allSchedules = db.Query<MedicineSchedule>(
                $"SELECT Id, MedicineId, ScheduleTime, IsActive, CreatedAt FROM MedicineSchedule WHERE MedicineId IN ({idList}) AND IsActive = 1");

            // Today's plan = the slots MedicineService.IsSlotReachable accepts for today: at/after the
            // medicine's start instant, not past its EndDate, and not before the medicine or the schedule
            // row was created (for a medicine added today, slots that had already passed do not count).
            // IsSlotReachable is a pure static method with no DI/MAUI dependency, so the widget shares it
            // instead of mirroring the rule by hand; it parses the stored dates with the invariant culture
            // ("yyyy-MM-dd" and "yyyy-MM-dd HH:mm" alike).
            var today = DateTime.Today;
            var schedules = allSchedules
                .Where(s => medicineById.TryGetValue(s.MedicineId, out var med) && MedicineService.IsSlotReachable(med, s, today))
                .ToList();

            var scheduleIds = schedules.Select(s => s.Id).ToHashSet();

            var todayLogs = db.Query<LRow>(
                $"SELECT ScheduleId, WasTaken FROM MedicineLog WHERE MedicineId IN ({idList}) AND ScheduledFor >= ? AND ScheduledFor < ?",
                todayStr, tomorrowStr);

            // Only count logs for schedules that are part of today's plan, so taken never exceeds total.
            var taken = todayLogs.Count(l => l.WasTaken == 1 && scheduleIds.Contains(l.ScheduleId));
            var loggedIds = todayLogs.Select(l => l.ScheduleId).ToHashSet();

            var total = schedules.Count;
            var progressText = total > 0 ? $"{taken} of {total} doses taken today" : "No schedules set";

            var nextSchedule = schedules
                .Where(s => !loggedIds.Contains(s.Id)
                            && TimeSpan.TryParse(s.ScheduleTime, inv, out var t)
                            && t > nowTimeOfDay)
                .OrderBy(s => s.ScheduleTime)
                .FirstOrDefault();

            string nextDoseText;
            if (nextSchedule != null)
            {
                var medName = db.Query<NRow>("SELECT Name FROM Medicine WHERE Id = ?", nextSchedule.MedicineId)
                    .FirstOrDefault()?.Name ?? "Medicine";
                nextDoseText = $"Next: {medName} at {nextSchedule.ScheduleTime}";
            }
            else
            {
                nextDoseText = taken >= total && total > 0 ? "✅ All doses taken today!" : "No upcoming doses";
            }

            var hr = db.Query<VRow>("SELECT Value FROM HealthMetric WHERE MetricType = 'HeartRate' ORDER BY RecordedAt DESC LIMIT 1");
            var hrText = hr.Count > 0 ? $"♥ {hr[0].Value:0} bpm" : "♥ --";

            var bp = db.Query<VRow2>("SELECT Value, SecondValue FROM HealthMetric WHERE MetricType = 'BloodPressure' ORDER BY RecordedAt DESC LIMIT 1");
            var bpText = bp.Count > 0 ? $"BP {bp[0].Value:0}/{bp[0].SecondValue:0}" : "BP --";

            return new WidgetDataRecord
            {
                ProgressText = progressText,
                NextDoseText = nextDoseText,
                HeartRateText = hrText,
                BloodPressureText = bpText,
                TakenDoses = taken,
                TotalDoses = total
            };
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("HealthWidget", $"DB error: {ex.Message}");
            return new WidgetDataRecord { ProgressText = "Tap to open app", NextDoseText = "" };
        }
        finally
        {
            db?.Close();
        }
    }

    private class LRow { public int ScheduleId { get; set; } public int WasTaken { get; set; } }
    private class NRow { public string Name { get; set; } = ""; }
    private class VRow { public double Value { get; set; } }
    private class VRow2 { public double Value { get; set; } public double? SecondValue { get; set; } }
}
