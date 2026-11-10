namespace HealthTracker.Services;

/// <summary>No-op IReminderService for use in tests.</summary>
public class NullReminderService : IReminderService
{
    public Task ScheduleReminders(int medicineId) => Task.CompletedTask;
    public Task CancelReminders(int medicineId) => Task.CompletedTask;
    public Task CancelDoseReminders(int medicineId, int scheduleId) => Task.CompletedTask;
    public Task RescheduleAll() => Task.CompletedTask;
    public Task CancelAll() => Task.CompletedTask;
    public Task ScheduleFollowUpReminder(int visitId) => Task.CompletedTask;
    public Task CancelFollowUpReminder(int visitId) => Task.CompletedTask;
    public Task ShowRefillReminder(int medicineId) => Task.CompletedTask;
}
