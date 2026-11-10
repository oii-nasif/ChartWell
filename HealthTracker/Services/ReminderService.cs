using System.Globalization;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services.Notifications;
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;
using Plugin.LocalNotification.EventArgs;

namespace HealthTracker.Services;

/// <summary>
/// Plugin.LocalNotification-backed implementation of <see cref="IReminderService"/>.
///
/// Notification ID bands (see <see cref="IReminderService"/> for the contract ones):
///   schedule.Id                  on-time dose reminder (daily repeat)
///   1_000_000 + schedule.Id      15-minute-early dose reminder (daily repeat)
///   2_000_000 + visit.Id * 2     follow-up, evening before (one-shot)
///   2_000_000 + visit.Id * 2 + 1 follow-up, morning of (one-shot)
///   3_000_000 + medicine.Id      refill nudge (immediate)
///   4_000_000 + schedule.Id      snoozed dose reminder (one-shot, +15 min)
///   5_000_000 + schedule.Id      "dose logged" confirmation (immediate, auto-dismisses)
///
/// Known limitation: Plugin.LocalNotification has no end date for repeating notifications, so a
/// daily dose reminder would keep firing past Medicine.EndDate. This is mitigated three ways:
/// (1) RescheduleAll runs on every launch and skips expired/inactive medicines, (2) MedicineService
/// calls CancelReminders when a course completes or a medicine is deactivated, and (3) the
/// NotificationReceiving pre-show hook suppresses (and cancels) a dose notification that fires for a
/// schedule which is no longer valid. (3) only works while the app process is alive to run the hook,
/// which it is whenever the plugin's alarm receiver delivers the notification.
/// </summary>
public class ReminderService : IReminderService
{
    // MedicineSchedule.Id is a single global AUTOINCREMENT, so it is already unique on its own.
    private const int EarlyIdOffset = 1_000_000;
    private const int FollowUpIdOffset = 2_000_000;
    private const int RefillIdOffset = 3_000_000;
    private const int SnoozeIdOffset = 4_000_000;
    private const int ConfirmIdOffset = 5_000_000;

    public const string DoseChannelId = "dose_reminders";
    public const string AppointmentChannelId = "appointments";

    // Plugin.LocalNotification reserves 100 (tap) and 101 (dismiss) for its own action ids.
    public const int TakeActionId = 1001;
    public const int SnoozeActionId = 1002;

    private static readonly TimeSpan EarlyLead = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SnoozeDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FollowUpEveningBefore = new(18, 0, 0);
    private static readonly TimeSpan FollowUpMorningOf = new(8, 0, 0);
    private static readonly long[] DoseVibration = { 0, 300, 200, 300 };

    private static readonly object InitLock = new();
    private static bool _handlersAttached;
    private static IServiceProvider? _services;

    private readonly DatabaseService _db;
    private readonly MedicineRepository _medicineRepo;
    private readonly MedicineScheduleRepository _scheduleRepo;
    private readonly MedicineLogRepository _logRepo;
    private readonly DoctorVisitRepository _visitRepo;
    private readonly IServiceProvider _serviceProvider;

    /// <remarks>
    /// MedicineService depends on IReminderService, so MedicineService is resolved lazily through the
    /// provider (only inside the "Take" action handler) to avoid a constructor cycle.
    /// </remarks>
    public ReminderService(DatabaseService db, MedicineRepository medicineRepo, MedicineScheduleRepository scheduleRepo,
        MedicineLogRepository logRepo, DoctorVisitRepository visitRepo, IServiceProvider serviceProvider)
    {
        _db = db;
        _medicineRepo = medicineRepo;
        _scheduleRepo = scheduleRepo;
        _logRepo = logRepo;
        _visitRepo = visitRepo;
        _serviceProvider = serviceProvider;
    }

    // ------------------------------------------------------------------
    // Initialization: channels, action category, action handlers
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers the Android notification channels and the Take/Snooze action category and subscribes
    /// to notification events. Idempotent. Call once at process start (MainApplication.OnCreate does
    /// this; App.OnStart may call it again safely).
    /// </summary>
    public void Initialize() => AttachNotificationHandlers(_serviceProvider);

    /// <summary>
    /// Static entry point usable before the MAUI <c>App</c> exists (e.g. from MainApplication.OnCreate),
    /// so action taps delivered to a cold process are still handled. Idempotent.
    /// </summary>
    public static void AttachNotificationHandlers(IServiceProvider services)
    {
        lock (InitLock)
        {
            _services = services;
            if (_handlersAttached) return;
            _handlersAttached = true;
        }

        try
        {
            RegisterChannels();
            RegisterCategories();
            LocalNotificationCenter.Current.NotificationActionTapped += OnNotificationActionTapped;
            LocalNotificationCenter.Current.NotificationReceiving = OnNotificationReceiving;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReminderService.AttachNotificationHandlers failed: {ex}");
        }
    }

    private static void RegisterChannels()
    {
#if ANDROID
        LocalNotificationCenter.CreateNotificationChannels(new List<NotificationChannelRequest>
        {
            new()
            {
                Id = DoseChannelId,
                Name = "Dose reminders",
                Description = "Reminders to take your medicines on time",
                Importance = AndroidImportance.High,
                EnableVibration = true,
                VibrationPattern = DoseVibration,
                EnableSound = true,
                ShowBadge = true,
                LockScreenVisibility = AndroidVisibilityType.Private
            },
            new()
            {
                Id = AppointmentChannelId,
                Name = "Appointments",
                Description = "Doctor follow-up appointment reminders",
                Importance = AndroidImportance.Default,
                EnableVibration = true,
                EnableSound = true,
                ShowBadge = true,
                LockScreenVisibility = AndroidVisibilityType.Private
            }
        });
#endif
    }

    private static void RegisterCategories()
    {
        var doseCategory = new NotificationCategory(NotificationCategoryType.Reminder)
        {
            ActionList = new HashSet<NotificationAction>
            {
                new(TakeActionId)
                {
                    Title = "Take",
                    Android = new AndroidAction { LaunchAppWhenTapped = false }
                },
                new(SnoozeActionId)
                {
                    Title = "Snooze 15 min",
                    Android = new AndroidAction { LaunchAppWhenTapped = false }
                }
            }
        };

        LocalNotificationCenter.Current.RegisterCategoryList(new HashSet<NotificationCategory> { doseCategory });
    }

    private static async void OnNotificationActionTapped(NotificationActionEventArgs e)
    {
        try
        {
            if (e.IsTapped || e.IsDismissed) return; // body tap opens the app; nothing else to do
            if (!DoseNotificationPayload.TryParse(e.Request?.ReturningData, out var payload)) return;

            var service = _services?.GetService(typeof(IReminderService)) as ReminderService;
            if (service is null) return;

            switch (e.ActionId)
            {
                case TakeActionId:
                    await service.HandleTakeAction(payload);
                    break;
                case SnoozeActionId:
                    await service.HandleSnoozeAction(payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReminderService action handling failed: {ex}");
        }
    }

    /// <summary>
    /// Pre-show hook (runs before the notification pops up). Safety net for the "no end date on
    /// repeats" limitation: a dose notification whose schedule is no longer valid is suppressed and
    /// its daily repeat cancelled. Everything else is shown unchanged.
    /// </summary>
    private static async Task<NotificationEventReceivingArgs> OnNotificationReceiving(NotificationRequest request)
    {
        var args = new NotificationEventReceivingArgs { Request = request, Handled = false };
        try
        {
            if (!DoseNotificationPayload.TryParse(request.ReturningData, out var payload)) return args;
            var service = _services?.GetService(typeof(IReminderService)) as ReminderService;
            if (service is null) return args;

            if (await service.IsScheduleStillValid(payload.MedicineId, payload.ScheduleId)) return args;

            LocalNotificationCenter.Current.Cancel(request.NotificationId);
            args.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReminderService receiving-check failed: {ex}");
        }
        return args;
    }

    private async Task<bool> IsScheduleStillValid(int medicineId, int scheduleId)
    {
        var medicine = await _medicineRepo.GetByIdAsync(medicineId);
        if (medicine is null || medicine.IsActive == 0) return false;
        if (DateTimeHelper.TryParse(medicine.EndDate, out var end) && end.Date < DateTime.Today) return false;

        var schedule = await _scheduleRepo.GetScheduleByIdAsync(scheduleId);
        return schedule is not null && schedule.IsActive == 1;
    }

    private async Task HandleTakeAction(DoseNotificationPayload payload)
    {
        var schedule = await _scheduleRepo.GetScheduleByIdAsync(payload.ScheduleId);
        var medicine = await _medicineRepo.GetByIdAsync(payload.MedicineId);
        if (schedule is null || medicine is null) return;

        var scheduledDate = TryParseTime(schedule.ScheduleTime, out var time)
            ? payload.ResolveScheduledDate(time, DateTime.Now)
            : DateTime.Today;

        var medicineService = _serviceProvider.GetService(typeof(MedicineService)) as MedicineService;
        if (medicineService is null) return;

        // Idempotent: returns false when this slot was already logged (e.g. taken from the app first).
        await medicineService.LogDoseTaken(payload.MedicineId, payload.ScheduleId, scheduledDate);

        // Dismiss whatever is in the shade for this slot, then re-arm from tomorrow.
        LocalNotificationCenter.Current.Clear(
            GetNotificationId(payload.ScheduleId),
            GetEarlyNotificationId(payload.ScheduleId),
            GetSnoozeNotificationId(payload.ScheduleId));
        LocalNotificationCenter.Current.Cancel(GetSnoozeNotificationId(payload.ScheduleId));
        await CancelDoseReminders(payload.MedicineId, payload.ScheduleId);

        await ShowAsync(BuildRequest(
            GetConfirmNotificationId(payload.ScheduleId),
            $"{medicine.Name} logged",
            $"Taken at {DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture)}",
            NotificationCategoryType.Status,
            DoseChannelId,
            schedule: null,
            returningData: null,
            silent: true,
            timeoutAfter: TimeSpan.FromSeconds(8)));

#if ANDROID
        HealthWidget.RequestUpdate();
#endif
    }

    private async Task HandleSnoozeAction(DoseNotificationPayload payload)
    {
        var medicine = await _medicineRepo.GetByIdAsync(payload.MedicineId);
        var schedule = await _scheduleRepo.GetScheduleByIdAsync(payload.ScheduleId);
        if (medicine is null || schedule is null) return;

        LocalNotificationCenter.Current.Clear(
            GetNotificationId(payload.ScheduleId),
            GetEarlyNotificationId(payload.ScheduleId),
            GetSnoozeNotificationId(payload.ScheduleId));

        var snoozedPayload = new DoseNotificationPayload(payload.MedicineId, payload.ScheduleId,
            TryParseTime(schedule.ScheduleTime, out var time)
                ? payload.ResolveScheduledDate(time, DateTime.Now)
                : DateTime.Today);

        await ShowAsync(BuildRequest(
            GetSnoozeNotificationId(payload.ScheduleId),
            $"Snoozed: time to take {medicine.Name}",
            $"Scheduled at {FormatTime(schedule.ScheduleTime)}",
            NotificationCategoryType.Reminder,
            DoseChannelId,
            schedule: new NotificationRequestSchedule
            {
                NotifyTime = DateTime.Now.Add(SnoozeDelay),
                RepeatType = NotificationRepeat.No
            },
            returningData: snoozedPayload.Encode()));
    }

    // ------------------------------------------------------------------
    // IReminderService: dose reminders
    // ------------------------------------------------------------------

    public async Task ScheduleReminders(int medicineId)
    {
        var medicine = await _medicineRepo.GetByIdAsync(medicineId);
        if (medicine is null || medicine.IsActive == 0) return;
        if (IsExpired(medicine)) return;

        var loggedToday = await GetScheduleIdsLoggedToday(medicineId);
        var schedules = await _scheduleRepo.GetByMedicineIdAsync(medicineId);
        foreach (var schedule in schedules)
        {
            await ArmDoseReminders(medicine, schedule, skipToday: loggedToday.Contains(schedule.Id));
        }
    }

    public async Task CancelReminders(int medicineId)
    {
        // Schedules can be soft-deleted; cancel for every schedule row that ever belonged to this medicine.
        var db = await _db.GetConnectionAsync();
        var schedules = await db.Table<MedicineSchedule>().Where(s => s.MedicineId == medicineId).ToListAsync();
        foreach (var schedule in schedules)
        {
            CancelDoseNotifications(schedule.Id);
        }
    }

    public Task CancelAll()
    {
        LocalNotificationCenter.Current.CancelAll();
        return Task.CompletedTask;
    }

    public async Task CancelDoseReminders(int medicineId, int scheduleId)
    {
        CancelDoseNotifications(scheduleId);

        var medicine = await _medicineRepo.GetByIdAsync(medicineId);
        if (medicine is null || medicine.IsActive == 0 || IsExpired(medicine)) return;
        var schedule = await _scheduleRepo.GetScheduleByIdAsync(scheduleId);
        if (schedule is null || schedule.IsActive == 0) return;

        await ArmDoseReminders(medicine, schedule, skipToday: true);
    }

    public async Task RescheduleAll()
    {
        LocalNotificationCenter.Current.CancelAll();

        var medicines = await _medicineRepo.GetAllActiveAsync();
        foreach (var medicine in medicines)
        {
            if (IsExpired(medicine)) continue;

            var loggedToday = await GetScheduleIdsLoggedToday(medicine.Id);
            var schedules = await _scheduleRepo.GetByMedicineIdAsync(medicine.Id);
            foreach (var schedule in schedules)
            {
                await ArmDoseReminders(medicine, schedule, skipToday: loggedToday.Contains(schedule.Id));
            }
        }

        var visits = await _visitRepo.GetAllAsync();
        foreach (var visit in visits)
        {
            if (!DateTimeHelper.TryParse(visit.FollowUpDate, out var followUp) || followUp.Date < DateTime.Today)
                continue;
            await ArmFollowUpReminders(visit, followUp.Date);
        }
    }

    /// <summary>
    /// Arms the early and on-time daily repeats for one schedule, honouring StartDate/EndDate.
    /// <paramref name="skipToday"/> moves today's not-yet-fired slot to tomorrow (used once a dose is logged).
    /// </summary>
    private async Task ArmDoseReminders(Medicine medicine, MedicineSchedule schedule, bool skipToday)
    {
        if (!TryParseTime(schedule.ScheduleTime, out var time)) return;

        var now = DateTime.Now;
        var firstDay = DateTime.Today;
        var hasStart = DateTimeHelper.TryParse(medicine.StartDate, out var start);
        if (hasStart && start.Date > firstDay)
            firstDay = start.Date;

        var onTime = firstDay.Add(time);
        // StartDate carries a time of day ("yyyy-MM-dd HH:mm"): a slot earlier than the start instant on
        // the first day was never due (matches MedicineService.IsSlotReachable), so begin on the next day.
        if (hasStart) while (onTime < start) onTime = onTime.AddDays(1);
        if (onTime <= now.AddMinutes(1)) onTime = onTime.AddDays(1); // plugin drops NotifyTime < now + AllowedDelay
        if (skipToday && onTime.Date == DateTime.Today) onTime = onTime.AddDays(1);

        var hasEnd = DateTimeHelper.TryParse(medicine.EndDate, out var end);
        if (hasEnd && onTime.Date > end.Date) return; // nothing left to remind for

        var early = onTime - EarlyLead;
        if (early <= now.AddMinutes(1)) early = early.AddDays(1);
        // early may now sit on the day after onTime's day; drop it if that is past the course end
        var armEarly = !hasEnd || (early + EarlyLead).Date <= end.Date;

        var payload = new DoseNotificationPayload(medicine.Id, schedule.Id, onTime.Date).Encode();
        var timeText = FormatTime(schedule.ScheduleTime);

        if (armEarly)
        {
            await ShowAsync(BuildRequest(
                GetEarlyNotificationId(schedule.Id),
                $"Reminder: {medicine.Name} in 15 minutes",
                $"Scheduled at {timeText}",
                NotificationCategoryType.Reminder,
                DoseChannelId,
                schedule: new NotificationRequestSchedule { NotifyTime = early, RepeatType = NotificationRepeat.Daily },
                returningData: payload));
        }

        await ShowAsync(BuildRequest(
            GetNotificationId(schedule.Id),
            $"Time to take {medicine.Name}",
            string.IsNullOrWhiteSpace(medicine.Instructions)
                ? $"Scheduled at {timeText}"
                : $"Scheduled at {timeText} · {medicine.Instructions}",
            NotificationCategoryType.Reminder,
            DoseChannelId,
            schedule: new NotificationRequestSchedule { NotifyTime = onTime, RepeatType = NotificationRepeat.Daily },
            returningData: payload));
    }

    private static void CancelDoseNotifications(int scheduleId)
    {
        LocalNotificationCenter.Current.Cancel(
            GetNotificationId(scheduleId),
            GetEarlyNotificationId(scheduleId),
            GetSnoozeNotificationId(scheduleId));
    }

    /// <summary>Schedule ids that already have a log row (taken or skipped) with ScheduledFor on today's date.</summary>
    private async Task<HashSet<int>> GetScheduleIdsLoggedToday(int medicineId)
    {
        var dayStart = DateTimeHelper.ToDateTimeString(DateTime.Today);
        var dayEnd = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(1).AddSeconds(-1));
        var logs = await _logRepo.GetLogsForMedicineAsync(medicineId, dayStart, dayEnd);
        return logs.Select(l => l.ScheduleId).ToHashSet();
    }

    // ------------------------------------------------------------------
    // IReminderService: follow-up appointments
    // ------------------------------------------------------------------

    public async Task ScheduleFollowUpReminder(int visitId)
    {
        var visit = await _visitRepo.GetByIdAsync(visitId);
        CancelFollowUpNotifications(visitId);
        if (visit is null) return;
        if (!DateTimeHelper.TryParse(visit.FollowUpDate, out var followUp) || followUp.Date < DateTime.Today) return;

        await ArmFollowUpReminders(visit, followUp.Date);
    }

    public Task CancelFollowUpReminder(int visitId)
    {
        CancelFollowUpNotifications(visitId);
        return Task.CompletedTask;
    }

    private async Task ArmFollowUpReminders(DoctorVisit visit, DateTime followUpDate)
    {
        var now = DateTime.Now;
        var who = string.IsNullOrWhiteSpace(visit.Hospital)
            ? $"Dr. {visit.DoctorName}"
            : $"Dr. {visit.DoctorName} at {visit.Hospital}";
        var dateText = followUpDate.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
        var returning = $"visit|{visit.Id.ToString(CultureInfo.InvariantCulture)}";

        var eveningBefore = followUpDate.AddDays(-1).Add(FollowUpEveningBefore);
        if (eveningBefore > now.AddMinutes(1))
        {
            await ShowAsync(BuildRequest(
                GetFollowUpDayBeforeId(visit.Id),
                "Follow-up tomorrow",
                $"{who} — {dateText}",
                NotificationCategoryType.Event,
                AppointmentChannelId,
                schedule: new NotificationRequestSchedule { NotifyTime = eveningBefore, RepeatType = NotificationRepeat.No },
                returningData: returning));
        }

        var morningOf = followUpDate.Add(FollowUpMorningOf);
        if (morningOf > now.AddMinutes(1))
        {
            await ShowAsync(BuildRequest(
                GetFollowUpOnDayId(visit.Id),
                "Follow-up today",
                $"{who}",
                NotificationCategoryType.Event,
                AppointmentChannelId,
                schedule: new NotificationRequestSchedule { NotifyTime = morningOf, RepeatType = NotificationRepeat.No },
                returningData: returning));
        }
    }

    private static void CancelFollowUpNotifications(int visitId)
    {
        LocalNotificationCenter.Current.Cancel(GetFollowUpDayBeforeId(visitId), GetFollowUpOnDayId(visitId));
    }

    // ------------------------------------------------------------------
    // IReminderService: refill nudge
    // ------------------------------------------------------------------

    public async Task ShowRefillReminder(int medicineId)
    {
        var medicine = await _medicineRepo.GetByIdAsync(medicineId);
        if (medicine is null || medicine.IsActive == 0) return;

        var key = $"refill_shown_{medicineId}";
        var today = DateTimeHelper.ToDateString(DateTime.Today);
        if (Preferences.Default.Get(key, string.Empty) == today) return;

        var stock = medicine.StockCount.ToString("0.##", CultureInfo.InvariantCulture);
        var shown = await ShowAsync(BuildRequest(
            GetRefillNotificationId(medicineId),
            $"Running low on {medicine.Name}",
            $"Only {stock} {medicine.StockUnit} left. Time to refill.",
            NotificationCategoryType.Status,
            DoseChannelId,
            schedule: null,
            returningData: $"refill|{medicineId.ToString(CultureInfo.InvariantCulture)}"));

        if (shown) Preferences.Default.Set(key, today);
    }

    // ------------------------------------------------------------------
    // Builder / helpers
    // ------------------------------------------------------------------

    /// <summary>Single place that shapes every notification this app shows.</summary>
    private static NotificationRequest BuildRequest(int id, string title, string description,
        NotificationCategoryType category, string channelId, NotificationRequestSchedule? schedule,
        string? returningData, bool silent = false, TimeSpan? timeoutAfter = null)
    {
        var request = new NotificationRequest
        {
            NotificationId = id,
            Title = title,
            Description = description,
            CategoryType = category,
            ReturningData = returningData ?? string.Empty,
            Silent = silent,
            Android = new AndroidOptions
            {
                ChannelId = channelId,
                AutoCancel = true,
                Priority = channelId == DoseChannelId ? AndroidPriority.High : AndroidPriority.Default,
                VisibilityType = AndroidVisibilityType.Private,
                LaunchAppWhenTapped = true,
                TimeoutAfter = timeoutAfter
            }
        };

        if (channelId == DoseChannelId && !silent)
            request.Android.VibrationPattern = DoseVibration;

        if (schedule is not null)
            request.Schedule = schedule;

        return request;
    }

    private static async Task<bool> ShowAsync(NotificationRequest request)
    {
        try
        {
            return await LocalNotificationCenter.Current.Show(request);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notification {request.NotificationId} failed: {ex}");
            return false;
        }
    }

    private static bool IsExpired(Medicine medicine) =>
        DateTimeHelper.TryParse(medicine.EndDate, out var end) && end.Date < DateTime.Today;

    private static bool TryParseTime(string? value, out TimeSpan time) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time);

    private static string FormatTime(string? scheduleTime) =>
        TryParseTime(scheduleTime, out var t)
            ? DateTime.Today.Add(t).ToString("h:mm tt", CultureInfo.InvariantCulture)
            : scheduleTime ?? string.Empty;

    private static int GetNotificationId(int scheduleId) => scheduleId;
    private static int GetEarlyNotificationId(int scheduleId) => EarlyIdOffset + scheduleId;
    private static int GetFollowUpDayBeforeId(int visitId) => FollowUpIdOffset + visitId * 2;
    private static int GetFollowUpOnDayId(int visitId) => FollowUpIdOffset + visitId * 2 + 1;
    private static int GetRefillNotificationId(int medicineId) => RefillIdOffset + medicineId;
    private static int GetSnoozeNotificationId(int scheduleId) => SnoozeIdOffset + scheduleId;
    private static int GetConfirmNotificationId(int scheduleId) => ConfirmIdOffset + scheduleId;
}
