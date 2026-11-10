namespace HealthTracker.Services;

/// <summary>
/// Local-notification scheduling. All reminder state is rebuilt from the database on every launch
/// via <see cref="RescheduleAll"/>, so nothing here needs to be persisted.
///
/// Notification ID scheme (must stay collision-free):
///   schedule.Id                 on-time dose reminder
///   1_000_000 + schedule.Id     15-minute-early dose reminder
///   2_000_000 + visit.Id * 2    follow-up reminder, day before
///   2_000_000 + visit.Id * 2 + 1 follow-up reminder, on the day
///   3_000_000 + medicine.Id     low-stock refill nudge
///   4_000_000 + schedule.Id     one-shot "snoozed 15 min" dose reminder
///   5_000_000 + schedule.Id     silent "dose logged" confirmation (auto-dismissed)
/// </summary>
public interface IReminderService
{
    /// <summary>
    /// Schedules the daily dose reminders for every active schedule of the medicine.
    /// Honours StartDate (nothing fires before it) and EndDate (nothing fires after it);
    /// inactive medicines get no reminders.
    /// </summary>
    Task ScheduleReminders(int medicineId);

    /// <summary>Cancels every dose reminder for the medicine.</summary>
    Task CancelReminders(int medicineId);

    /// <summary>
    /// Called after a dose is logged as taken: cancels today's still-pending reminders for that slot
    /// and re-arms them from tomorrow.
    /// </summary>
    Task CancelDoseReminders(int medicineId, int scheduleId);

    /// <summary>
    /// Cancels everything and rebuilds reminders for all active medicines. Slots already logged
    /// today are not re-armed for today.
    /// </summary>
    Task RescheduleAll();

    /// <summary>Cancels every notification this app has scheduled.</summary>
    Task CancelAll();

    /// <summary>
    /// Schedules the follow-up appointment reminders for a doctor visit (the evening before and the
    /// morning of). A visit with no follow-up date, or one in the past, has its reminders cancelled.
    /// </summary>
    Task ScheduleFollowUpReminder(int visitId);

    /// <summary>Cancels the follow-up reminders for a doctor visit.</summary>
    Task CancelFollowUpReminder(int visitId);

    /// <summary>
    /// Shows a refill nudge for a medicine whose stock is running low. At most one per medicine per day.
    /// </summary>
    Task ShowRefillReminder(int medicineId);
}
