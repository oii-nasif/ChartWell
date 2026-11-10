using System.Globalization;

namespace HealthTracker.Services.Notifications;

/// <summary>
/// The <c>ReturningData</c> carried by every dose notification, so the action handler can log the
/// dose without touching the UI. Wire format: <c>dose|{medicineId}|{scheduleId}|{yyyy-MM-dd}</c>.
/// The date is the day the notification was first armed for; for daily repeats it goes stale after
/// the first fire, so consumers must treat it as a hint and fall back to "today" (see
/// <see cref="ResolveScheduledDate"/>).
/// </summary>
public sealed class DoseNotificationPayload
{
    public const string Prefix = "dose";

    public int MedicineId { get; }
    public int ScheduleId { get; }
    public DateTime ArmedForDate { get; }

    public DoseNotificationPayload(int medicineId, int scheduleId, DateTime armedForDate)
    {
        MedicineId = medicineId;
        ScheduleId = scheduleId;
        ArmedForDate = armedForDate.Date;
    }

    public string Encode() =>
        string.Join('|', Prefix,
            MedicineId.ToString(CultureInfo.InvariantCulture),
            ScheduleId.ToString(CultureInfo.InvariantCulture),
            ArmedForDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static bool TryParse(string? data, out DoseNotificationPayload payload)
    {
        payload = null!;
        if (string.IsNullOrWhiteSpace(data)) return false;

        var parts = data.Split('|');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var medicineId)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var scheduleId)) return false;
        if (!DateTime.TryParseExact(parts[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;

        payload = new DoseNotificationPayload(medicineId, scheduleId, date);
        return true;
    }

    /// <summary>
    /// Works out which calendar day the tapped dose belongs to. Daily repeats carry the date they were
    /// first armed for, so only trust the embedded date when it is today; otherwise assume today, except
    /// for a late-evening slot tapped shortly after midnight, which belongs to yesterday.
    /// </summary>
    public DateTime ResolveScheduledDate(TimeSpan scheduleTime, DateTime now)
    {
        if (ArmedForDate == now.Date) return ArmedForDate;

        var todaySlot = now.Date.Add(scheduleTime);
        return todaySlot - now > TimeSpan.FromHours(12) ? now.Date.AddDays(-1) : now.Date;
    }
}
