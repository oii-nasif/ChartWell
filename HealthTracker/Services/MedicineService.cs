using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;

namespace HealthTracker.Services;

public class MedicineService
{
    /// <summary>How far back AutoLogMissedDoses will look when the app has not been opened for a while.</summary>
    private const int MaxAutoLogLookbackDays = 30;

    private readonly MedicineRepository _medicineRepo;
    private readonly MedicineScheduleRepository _scheduleRepo;
    private readonly MedicineLogRepository _logRepo;
    private readonly MedicineCourseRepository _courseRepo;
    private readonly IReminderService _reminderService;

    public MedicineService(MedicineRepository medicineRepo, MedicineScheduleRepository scheduleRepo,
        MedicineLogRepository logRepo, MedicineCourseRepository courseRepo, IReminderService reminderService)
    {
        _medicineRepo = medicineRepo;
        _scheduleRepo = scheduleRepo;
        _logRepo = logRepo;
        _courseRepo = courseRepo;
        _reminderService = reminderService;
    }

    // ── Course arithmetic ────────────────────────────────────────────────────

    /// <summary>
    /// EndDate ("yyyy-MM-dd") of a fixed course: start + prescribedDays - 1 + extensionDays.
    /// Null for ongoing medicines or when the inputs cannot be parsed.
    /// </summary>
    public static string? ComputeEndDate(string? startDate, int prescribedDays, bool isOngoing, int extensionDays = 0)
    {
        if (isOngoing || prescribedDays <= 0) return null;
        return DateTimeHelper.TryParseStored(startDate, out var sd)
            ? DateTimeHelper.ToDateString(sd.Date.AddDays(prescribedDays - 1 + Math.Max(0, extensionDays)))
            : null;
    }

    /// <summary>"HH:mm" normalisation for schedule times coming from the UI; unknown shapes pass through.</summary>
    private static string NormalizeTime(string time) =>
        DateTimeHelper.TryParseTime(time, out var t) ? DateTimeHelper.ToTimeString(t) : time.Trim();

    private static List<string> NormalizeTimes(IEnumerable<string> times) =>
        times.Select(NormalizeTime).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();

    // ── Slot reachability (shared with the Today page, detail/list progress and the widget) ──

    /// <summary>
    /// True when the dose slot of <paramref name="schedule"/> on <paramref name="day"/> is one the user
    /// could actually have been expected to take live: at/after the medicine's start date-time, within
    /// the course end, and not earlier than the moment the medicine or that schedule row was created.
    /// Slots before creation are neither shown on the Today page nor auto-logged as missed
    /// (they can still be backfilled explicitly).
    /// </summary>
    public static bool IsSlotReachable(Medicine medicine, MedicineSchedule schedule, DateTime day)
    {
        if (schedule.IsActive == 0) return false;
        if (!DateTimeHelper.TryParseTime(schedule.ScheduleTime, out var time)) return false;
        if (!DateTimeHelper.TryParseStored(medicine.StartDate, out var start)) return false;

        var slot = day.Date.Add(time);
        if (slot < start) return false;
        if (DateTimeHelper.TryParseStored(medicine.EndDate, out var end) && day.Date > end.Date) return false;
        if (DateTimeHelper.TryParseStored(medicine.CreatedAt, out var created) && slot < created) return false;
        if (DateTimeHelper.TryParseStored(schedule.CreatedAt, out var schedCreated) && slot < schedCreated) return false;
        return true;
    }

    /// <summary>The schedules of <paramref name="medicine"/> whose slot on <paramref name="day"/> is reachable, ordered by time.</summary>
    public static List<MedicineSchedule> GetReachableSlotsForDay(Medicine medicine,
        IEnumerable<MedicineSchedule> schedules, DateTime day) =>
        schedules.Where(s => IsSlotReachable(medicine, s, day))
            .OrderBy(s => s.ScheduleTime, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Number of doses a fixed course expects over its PrescribedDays (extension days are not counted:
    /// they only make room for doses that were missed). Slots that passed before the start time, or
    /// before the medicine/schedule was created on its creation day, are excluded; slots on earlier
    /// days remain countable because they can be backfilled. 0 for ongoing medicines.
    /// </summary>
    public static int ComputeTotalScheduled(Medicine medicine, IReadOnlyCollection<MedicineSchedule> schedules)
    {
        if (medicine.IsOngoing == 1 || medicine.PrescribedDays <= 0) return 0;
        if (!DateTimeHelper.TryParseStored(medicine.StartDate, out var start))
            return medicine.PrescribedDays * schedules.Count;

        int count = 0;
        for (int d = 0; d < medicine.PrescribedDays; d++)
        {
            var day = start.Date.AddDays(d);
            foreach (var sched in schedules)
            {
                if (sched.IsActive == 0) continue;
                if (!DateTimeHelper.TryParseTime(sched.ScheduleTime, out var time)) continue;
                var slot = day.Add(time);
                if (slot < start) continue;
                if (PassedBeforeCreation(medicine, sched, slot)) continue;
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// The creation-day rule of <see cref="ComputeTotalScheduled"/>: true when <paramref name="slot"/> lies
    /// on the day the medicine (or that schedule row) was created but before the creation moment.
    /// </summary>
    private static bool PassedBeforeCreation(Medicine medicine, MedicineSchedule sched, DateTime slot)
    {
        if (DateTimeHelper.TryParseStored(medicine.CreatedAt, out var created) && slot.Date == created.Date && slot < created) return true;
        if (DateTimeHelper.TryParseStored(sched.CreatedAt, out var sc) && slot.Date == sc.Date && slot < sc) return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="slot"/> falls inside the first PrescribedDays of a fixed course (the
    /// window <see cref="ComputeTotalScheduled"/> counts) but was left out of TotalScheduled only because it
    /// had already passed when the medicine/schedule was created. Backfilling such a slot adds it back.
    /// </summary>
    private static bool IsUncountedBackfillSlot(Medicine medicine, MedicineSchedule sched, DateTime start, DateTime slot)
    {
        if (medicine.IsOngoing == 1 || medicine.PrescribedDays <= 0) return false;
        if (slot < start || slot.Date >= start.Date.AddDays(medicine.PrescribedDays)) return false;
        return PassedBeforeCreation(medicine, sched, slot);
    }

    // ── Create / read ────────────────────────────────────────────────────────

    public async Task<Medicine> AddMedicine(string name,
        double stock, string stockUnit, string startDate, string? endDate,
        string? instructions, string? notes, int prescribedDays, bool isOngoing, List<string> schedules)
    {
        if (schedules.Count == 0)
            throw new InvalidOperationException("At least one schedule time is required.");

        var times = NormalizeTimes(schedules);
        startDate = DateTimeHelper.NormalizeStartDate(startDate);
        var now = DateTime.Now;

        // Compute end date from prescribed days if not explicitly provided
        var computedEndDate = isOngoing ? null : endDate ?? ComputeEndDate(startDate, prescribedDays, isOngoing);

        var medicine = new Medicine
        {
            Name = name,
            StockCount = Math.Max(0, stock),
            StockUnit = stockUnit,
            StartDate = startDate,
            EndDate = computedEndDate,
            Instructions = instructions,
            Notes = notes,
            PrescribedDays = prescribedDays,
            IsOngoing = isOngoing ? 1 : 0,
            ExtensionDays = 0,
            IsActive = 1,
            CreatedAt = DateTimeHelper.ToDateTimeString(now)
        };

        await _medicineRepo.InsertAsync(medicine);

        // New medicine gets highest SortOrder so it appears first
        var all = await _medicineRepo.GetAllActiveAsync();
        medicine.SortOrder = all.Count > 0 ? all.Max(m => m.SortOrder) + 1 : 1;
        await _medicineRepo.UpdateAsync(medicine);

        var scheduleRows = new List<MedicineSchedule>();
        foreach (var time in times)
        {
            var row = new MedicineSchedule
            {
                MedicineId = medicine.Id,
                ScheduleTime = time,
                IsActive = 1,
                CreatedAt = DateTimeHelper.ToDateTimeMinuteString(now)
            };
            await _scheduleRepo.InsertAsync(row);
            scheduleRows.Add(row);
        }

        // Create first course
        await _courseRepo.InsertAsync(new MedicineCourse
        {
            MedicineId = medicine.Id,
            StartDate = startDate,
            EndDate = computedEndDate,
            PrescribedDays = isOngoing ? 0 : prescribedDays,
            SchedulesPerDay = scheduleRows.Count,
            TotalTaken = 0,
            TotalScheduled = ComputeTotalScheduled(medicine, scheduleRows),
            Status = "Active",
            CreatedAt = DateTimeHelper.ToDateTimeString(now)
        });

        await _reminderService.ScheduleReminders(medicine.Id);
        return medicine;
    }

    public Task<List<Medicine>> GetAllActiveMedicines() =>
        _medicineRepo.GetAllActiveAsync();

    public Task<List<Medicine>> GetAllInactiveMedicines() =>
        _medicineRepo.GetAllInactiveAsync();

    public Task<Medicine?> GetMedicineById(int id) =>
        _medicineRepo.GetByIdAsync(id);

    public Task<List<MedicineSchedule>> GetSchedulesForMedicine(int medicineId) =>
        _scheduleRepo.GetByMedicineIdAsync(medicineId);

    public Task<List<MedicineCourse>> GetCoursesForMedicine(int medicineId) =>
        _courseRepo.GetByMedicineIdAsync(medicineId);

    // ── Courses ──────────────────────────────────────────────────────────────

    public async Task CheckAndCompleteCourses()
    {
        var activeCourses = await _courseRepo.GetAllActiveCoursesAsync();
        foreach (var course in activeCourses)
        {
            bool endedByDate = DateTimeHelper.TryParseStored(course.EndDate, out var endDate)
                && endDate.Date < DateTime.Today;

            // Also complete if all scheduled doses are taken (handles missing EndDate)
            bool endedByDoses = course.TotalScheduled > 0
                && course.TotalTaken >= course.TotalScheduled;

            if (!endedByDate && !endedByDoses) continue;

            // Course has ended — complete it and deactivate medicine
            course.Status = "Completed";
            await _courseRepo.UpdateAsync(course);

            var medicine = await _medicineRepo.GetByIdAsync(course.MedicineId);
            if (medicine is null || medicine.IsActive == 0) continue;
            medicine.IsActive = 0;
            await _medicineRepo.UpdateAsync(medicine);
            await _reminderService.CancelReminders(medicine.Id);
        }
    }

    /// <summary>
    /// Closes the active course (EndDate clamped to no earlier than its StartDate) and starts a new
    /// fixed-length course. A date-only <paramref name="startDate"/> equal to today starts "now", so
    /// slots that already passed today are not counted. The medicine becomes active and non-ongoing.
    /// </summary>
    public async Task StartNewCourse(int medicineId, int prescribedDays, string startDate)
    {
        if (!DateTimeHelper.TryParseStored(startDate, out var start))
            throw new ArgumentException("Start date is not a valid date.", nameof(startDate));
        if (start.TimeOfDay == TimeSpan.Zero && start.Date == DateTime.Today)
            start = DateTime.Now;
        var startStr = DateTimeHelper.ToDateTimeMinuteString(start);
        var now = DateTime.Now;

        // Close current active course
        var current = await _courseRepo.GetActiveCourseAsync(medicineId);
        if (current != null)
        {
            var closeDate = DateTime.Today.AddDays(-1);
            if (DateTimeHelper.TryParseStored(current.StartDate, out var currentStart) && closeDate < currentStart.Date)
                closeDate = currentStart.Date;
            current.Status = "Completed";
            current.EndDate = DateTimeHelper.ToDateString(closeDate);
            await _courseRepo.UpdateAsync(current);
        }

        var medicine = await _medicineRepo.GetByIdAsync(medicineId);
        if (medicine is null) return;

        var schedules = await _scheduleRepo.GetByMedicineIdAsync(medicineId);

        // Update medicine
        medicine.IsActive = 1;
        medicine.IsOngoing = 0;
        medicine.PrescribedDays = prescribedDays;
        medicine.ExtensionDays = 0;
        medicine.StartDate = startStr;
        medicine.EndDate = ComputeEndDate(startStr, prescribedDays, isOngoing: false);
        await _medicineRepo.UpdateAsync(medicine);

        // Create new course
        await _courseRepo.InsertAsync(new MedicineCourse
        {
            MedicineId = medicineId,
            StartDate = startStr,
            EndDate = medicine.EndDate,
            PrescribedDays = prescribedDays,
            SchedulesPerDay = schedules.Count,
            TotalTaken = 0,
            TotalScheduled = ComputeTotalScheduled(medicine, schedules),
            Status = "Active",
            CreatedAt = DateTimeHelper.ToDateTimeString(now)
        });

        await _reminderService.ScheduleReminders(medicineId);
    }

    public async Task DeactivateMedicine(int id)
    {
        var medicine = await _medicineRepo.GetByIdAsync(id);
        if (medicine is null) return;
        medicine.IsActive = 0;
        await _medicineRepo.UpdateAsync(medicine);
        await _reminderService.CancelReminders(id);
    }

    public async Task DeleteMedicine(int id)
    {
        await _reminderService.CancelReminders(id);
        // Includes soft-deleted (IsActive = 0) rows, which GetByMedicineIdAsync filters out.
        foreach (var s in await GetAllScheduleRows(id))
            await _scheduleRepo.DeleteAsync(s);
        var courses = await _courseRepo.GetByMedicineIdAsync(id);
        foreach (var c in courses)
            await _courseRepo.DeleteAsync(c);
        await _logRepo.DeleteByMedicineIdAsync(id);
        var medicine = await _medicineRepo.GetByIdAsync(id);
        if (medicine is null) return;
        await _medicineRepo.DeleteAsync(medicine);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    public async Task UpdateMedicine(Medicine medicine, List<string> schedules)
    {
        var times = NormalizeTimes(schedules);
        medicine.StartDate = DateTimeHelper.NormalizeStartDate(medicine.StartDate);
        medicine.StockCount = Math.Max(0, medicine.StockCount);

        // Cancel reminders while the old schedule rows still exist, so their notification ids are known.
        await _reminderService.CancelReminders(medicine.Id);

        // Recompute EndDate only when the course definition changed, so an explicit end date survives an
        // unrelated edit (e.g. changing the notes). Start dates are compared as instants, not strings,
        // because the UI may hand back a different (but equivalent) stored format.
        var previous = await _medicineRepo.GetByIdAsync(medicine.Id);
        bool courseDefinitionChanged = previous is null
            || !SameInstant(previous.StartDate, medicine.StartDate)
            || previous.PrescribedDays != medicine.PrescribedDays
            || previous.IsOngoing != medicine.IsOngoing;

        bool isOngoing = medicine.IsOngoing == 1;
        medicine.ExtensionDays = isOngoing ? 0 : Math.Clamp(medicine.ExtensionDays, 0, Math.Max(0, medicine.PrescribedDays));
        if (courseDefinitionChanged || isOngoing)
            medicine.EndDate = ComputeEndDate(medicine.StartDate, medicine.PrescribedDays, isOngoing, medicine.ExtensionDays);

        await _medicineRepo.UpdateAsync(medicine);

        var finalSchedules = await SyncSchedules(medicine.Id, times);

        // Update active course schedule info
        var course = await _courseRepo.GetActiveCourseAsync(medicine.Id);
        if (course != null)
        {
            course.StartDate = medicine.StartDate;
            course.PrescribedDays = isOngoing ? 0 : medicine.PrescribedDays;
            course.SchedulesPerDay = finalSchedules.Count;
            course.TotalScheduled = ComputeTotalScheduled(medicine, finalSchedules);
            course.EndDate = isOngoing ? null : medicine.EndDate;
            await _courseRepo.UpdateAsync(course);
        }

        await _reminderService.ScheduleReminders(medicine.Id);
    }

    private static bool SameInstant(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return DateTimeHelper.TryParseStored(a, out var da)
            && DateTimeHelper.TryParseStored(b, out var db)
            && da == db;
    }

    /// <summary>
    /// Reconciles the schedule rows of a medicine with the desired list of times while keeping row ids
    /// (dose logs reference ScheduleId): unchanged times keep their row, changed times are applied by
    /// UPDATE onto the remaining active rows (paired in time order). Rows are never hard-deleted: a
    /// surplus row is soft-deleted (IsActive = 0) so its logs stay linked, and a newly needed time first
    /// reactivates a soft-deleted row with that time (re-stamping CreatedAt, so the days it was off are
    /// not auto-logged as missed) before inserting. Returns the resulting active rows ordered by time.
    /// </summary>
    private async Task<List<MedicineSchedule>> SyncSchedules(int medicineId, List<string> times)
    {
        var allRows = (await GetAllScheduleRows(medicineId))
            .OrderBy(s => s.ScheduleTime, StringComparer.Ordinal).ToList();
        var existing = allRows.Where(s => s.IsActive == 1).ToList();
        var inactive = allRows.Where(s => s.IsActive == 0).ToList();

        var result = new List<MedicineSchedule>();
        var unmatchedRows = new List<MedicineSchedule>();
        var unmatchedTimes = new List<string>(times);

        foreach (var row in existing)
        {
            if (unmatchedTimes.Remove(row.ScheduleTime))
                result.Add(row);
            else
                unmatchedRows.Add(row);
        }

        int pairs = Math.Min(unmatchedRows.Count, unmatchedTimes.Count);
        for (int i = 0; i < pairs; i++)
        {
            var row = unmatchedRows[i];
            row.ScheduleTime = unmatchedTimes[i];
            await _scheduleRepo.UpdateAsync(row);
            result.Add(row);
        }

        for (int i = pairs; i < unmatchedRows.Count; i++)
        {
            unmatchedRows[i].IsActive = 0;
            await _scheduleRepo.UpdateAsync(unmatchedRows[i]);
        }

        var createdAt = DateTimeHelper.ToDateTimeMinuteString(DateTime.Now);
        for (int i = pairs; i < unmatchedTimes.Count; i++)
        {
            var reusable = inactive.FirstOrDefault(s => s.ScheduleTime == unmatchedTimes[i]);
            if (reusable != null)
            {
                inactive.Remove(reusable);
                reusable.IsActive = 1;
                reusable.CreatedAt = createdAt;
                await _scheduleRepo.UpdateAsync(reusable);
                result.Add(reusable);
                continue;
            }

            var row = new MedicineSchedule
            {
                MedicineId = medicineId,
                ScheduleTime = unmatchedTimes[i],
                IsActive = 1,
                CreatedAt = createdAt
            };
            await _scheduleRepo.InsertAsync(row);
            result.Add(row);
        }

        return result.OrderBy(s => s.ScheduleTime, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every schedule row of the medicine, active or soft-deleted (the repository's query returns active rows only).</summary>
    private async Task<List<MedicineSchedule>> GetAllScheduleRows(int medicineId) =>
        (await _scheduleRepo.GetAllAsync()).Where(s => s.MedicineId == medicineId).ToList();

    public async Task UpdateStock(int medicineId, double newStock)
    {
        var medicine = await _medicineRepo.GetByIdAsync(medicineId);
        if (medicine is null) return;
        medicine.StockCount = Math.Max(0, newStock);
        await _medicineRepo.UpdateAsync(medicine);
    }

    public async Task UpdateSortOrder(int medicineId, int order)
    {
        var medicine = await _medicineRepo.GetByIdAsync(medicineId);
        if (medicine is null) return;
        medicine.SortOrder = order;
        await _medicineRepo.UpdateAsync(medicine);
    }

    // ── Missed-dose housekeeping ─────────────────────────────────────────────

    /// <summary>
    /// Writes a "missed" log for every reachable past schedule slot (up to yesterday) that has no log,
    /// covering every day since the app was last opened. Slots before the medicine/schedule was created
    /// are never logged. For fixed-length courses the end date is pushed back by one day per whole day's
    /// worth of missed doses, with the total extension capped at PrescribedDays.
    /// </summary>
    public async Task AutoLogMissedDoses()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var medicines = await _medicineRepo.GetAllActiveAsync();

        foreach (var med in medicines)
            await AutoLogMissedDosesFor(med, yesterday);
    }

    private async Task AutoLogMissedDosesFor(Medicine med, DateTime yesterday)
    {
        if (!DateTimeHelper.TryParseStored(med.StartDate, out var start)) return;
        if (yesterday < start.Date) return;

        var isOngoing = med.IsOngoing == 1;
        var hasEnd = DateTimeHelper.TryParseStored(med.EndDate, out var endDate);
        if (!isOngoing && !hasEnd) return;

        var schedules = await _scheduleRepo.GetByMedicineIdAsync(med.Id);
        if (schedules.Count == 0) return;

        var from = ComputeAutoLogStart(med, start, yesterday);
        if (from > yesterday) return;

        // Logs from `from` to yesterday inclusive; the end may move while we iterate, so fetch the whole range.
        var loggedSlots = await GetLoggedSlotKeys(med.Id, from, yesterday);

        DateTime? end = hasEnd ? endDate.Date : null;
        int baseExtension = med.ExtensionDays;
        int missedCount = 0;

        for (var day = from; day <= yesterday; day = day.AddDays(1))
        {
            if (end.HasValue && day > end.Value) break;

            missedCount += await LogMissedSlotsForDay(med, schedules, day, loggedSlots);

            // Extending the course can bring further past days into range; apply it as we go.
            if (!isOngoing && missedCount > 0)
            {
                var newExtension = CapExtension(med, baseExtension + WholeDays(missedCount, schedules.Count));
                if (newExtension != med.ExtensionDays)
                {
                    end = end!.Value.AddDays(newExtension - med.ExtensionDays);
                    med.ExtensionDays = newExtension;
                    // IsSlotReachable reads the medicine's EndDate, so keep it in step with the moving end.
                    med.EndDate = DateTimeHelper.ToDateString(end.Value);
                }
            }
        }

        if (missedCount == 0 || isOngoing || med.ExtensionDays == baseExtension) return;
        await ApplyCourseExtension(med);
    }

    /// <summary>First day AutoLog may touch: course start, bounded by the lookback window and the creation day.</summary>
    private static DateTime ComputeAutoLogStart(Medicine med, DateTime start, DateTime yesterday)
    {
        var from = start.Date;
        var lookbackLimit = yesterday.AddDays(-(MaxAutoLogLookbackDays - 1));
        if (from < lookbackLimit) from = lookbackLimit;
        if (DateTimeHelper.TryParseStored(med.CreatedAt, out var created) && from < created.Date) from = created.Date;
        return from;
    }

    private async Task<HashSet<(int ScheduleId, string Day)>> GetLoggedSlotKeys(int medicineId, DateTime from, DateTime to)
    {
        var existingLogs = await _logRepo.GetLogsForMedicineAsync(medicineId,
            DateTimeHelper.ToDateTimeString(from),
            DateTimeHelper.ToDateTimeString(to.AddDays(1).AddSeconds(-1)));
        return existingLogs
            .Select(l => (l.ScheduleId, Day: DateTimeHelper.DatePart(l.ScheduledFor)))
            .ToHashSet();
    }

    /// <summary>Inserts a missed log for each reachable, unlogged slot of <paramref name="day"/>; returns how many.</summary>
    private async Task<int> LogMissedSlotsForDay(Medicine med, List<MedicineSchedule> schedules, DateTime day,
        HashSet<(int ScheduleId, string Day)> loggedSlots)
    {
        var dayKey = DateTimeHelper.ToDateString(day);
        int missed = 0;
        foreach (var sched in GetReachableSlotsForDay(med, schedules, day))
        {
            if (loggedSlots.Contains((sched.Id, dayKey))) continue;
            DateTimeHelper.TryParseTime(sched.ScheduleTime, out var time);

            await _logRepo.InsertAsync(new MedicineLog
            {
                MedicineId = med.Id,
                ScheduleId = sched.Id,
                ScheduledFor = DateTimeHelper.ToDateTimeString(day.Add(time)),
                TakenAt = null,
                WasTaken = 0,
                StockBefore = med.StockCount,
                StockAfter = med.StockCount
            });
            loggedSlots.Add((sched.Id, dayKey));
            missed++;
        }
        return missed;
    }

    private static int WholeDays(int missedCount, int schedulesPerDay) =>
        schedulesPerDay <= 0 ? 0 : (int)Math.Ceiling(missedCount / (double)schedulesPerDay);

    private static int CapExtension(Medicine med, int extension) =>
        Math.Clamp(extension, 0, Math.Max(0, med.PrescribedDays));

    /// <summary>Persists the medicine's (already updated) ExtensionDays/EndDate and mirrors the end onto the active course.</summary>
    private async Task ApplyCourseExtension(Medicine med)
    {
        await _medicineRepo.UpdateAsync(med);

        var course = await _courseRepo.GetActiveCourseAsync(med.Id);
        if (course != null)
        {
            course.EndDate = med.EndDate;
            await _courseRepo.UpdateAsync(course);
        }
    }

    // ── Dose logging ─────────────────────────────────────────────────────────

    /// <summary>Stock never blocks a dose; only an inactive medicine cannot be logged.</summary>
    public bool CanLogDose(Medicine medicine) => medicine.IsActive == 1;

    /// <summary>Soft warning: the next dose will be taken from an empty pack (stock is clamped at 0).</summary>
    public bool IsOutOfStock(Medicine medicine) => medicine.StockCount < 1;

    /// <summary>Alias of <see cref="IsOutOfStock"/>; a warning flag, not a blocker.</summary>
    public bool HasInsufficientStock(Medicine medicine) => IsOutOfStock(medicine);

    /// <summary>
    /// Logs the dose of <paramref name="scheduleId"/> for <paramref name="scheduledDate"/> (default today) as
    /// taken. Idempotent: returns null without touching stock when a taken log already exists for that slot
    /// and day; an existing "missed" row is flipped to taken. Stock is decremented and clamped at 0, the active
    /// course's TotalTaken is incremented, and a refill nudge is shown when stock crosses the low threshold.
    /// </summary>
    public async Task<MedicineLog?> LogDoseTaken(int medicineId, int scheduleId, DateTime? scheduledDate = null)
    {
        var medicine = await _medicineRepo.GetByIdAsync(medicineId)
            ?? throw new InvalidOperationException("Medicine not found.");
        var schedule = await _scheduleRepo.GetScheduleByIdAsync(scheduleId)
            ?? throw new InvalidOperationException("Schedule not found.");

        var day = (scheduledDate ?? DateTime.Today).Date;
        var existing = await _logRepo.GetLogForSlotAsync(scheduleId, DateTimeHelper.ToDateString(day));
        if (existing is { WasTaken: 1 }) return null;

        var schedules = await _scheduleRepo.GetByMedicineIdAsync(medicineId);
        bool wasLow = IsLowStock(medicine, schedules);

        var scheduledFor = DateTimeHelper.TryParseTime(schedule.ScheduleTime, out var time)
            ? day.Add(time)
            : day == DateTime.Today ? DateTime.Now : day;

        var log = await WriteTakenLog(medicine, existing, scheduleId, scheduledFor, DateTime.Now);
        await _medicineRepo.UpdateAsync(medicine);
        await IncrementCourseTaken(medicineId, 1);

        if (!wasLow && IsLowStock(medicine, schedules))
            await _reminderService.ShowRefillReminder(medicineId);

        return log;
    }

    /// <summary>
    /// Creates (or flips an existing missed row into) a taken log, decrementing <paramref name="medicine"/>'s
    /// in-memory stock with a clamp at 0. The caller persists the medicine.
    /// </summary>
    private async Task<MedicineLog> WriteTakenLog(Medicine medicine, MedicineLog? existing, int scheduleId,
        DateTime scheduledFor, DateTime takenAt)
    {
        var stockBefore = medicine.StockCount;
        var stockAfter = Math.Max(0, stockBefore - 1);

        var log = existing ?? new MedicineLog { MedicineId = medicine.Id, ScheduleId = scheduleId };
        log.ScheduledFor = DateTimeHelper.ToDateTimeString(scheduledFor);
        log.TakenAt = DateTimeHelper.ToDateTimeString(takenAt);
        log.WasTaken = 1;
        log.StockBefore = stockBefore;
        log.StockAfter = stockAfter;
        log.SkipReason = null;

        if (existing is null) await _logRepo.InsertAsync(log);
        else await _logRepo.UpdateAsync(log);

        medicine.StockCount = stockAfter;
        return log;
    }

    private Task IncrementCourseTaken(int medicineId, int by) => AdjustActiveCourse(medicineId, by, 0);

    /// <summary>Adds <paramref name="takenDelta"/> to TotalTaken and <paramref name="scheduledDelta"/> to TotalScheduled of the active course.</summary>
    private async Task AdjustActiveCourse(int medicineId, int takenDelta, int scheduledDelta)
    {
        if (takenDelta == 0 && scheduledDelta == 0) return;
        var course = await _courseRepo.GetActiveCourseAsync(medicineId);
        if (course != null)
        {
            course.TotalTaken += takenDelta;
            course.TotalScheduled += scheduledDelta;
            await _courseRepo.UpdateAsync(course);
        }
    }

    public async Task<List<MedicineLog>> GetLogsForMedicine(int medicineId, DateTime from, DateTime to)
    {
        return await _logRepo.GetLogsForMedicineAsync(medicineId,
            DateTimeHelper.ToDateTimeString(from),
            DateTimeHelper.ToDateTimeString(to));
    }

    public async Task<(int streak, int missedThisWeek)> GetAdherenceStats(int medicineId)
    {
        var schedules = await GetSchedulesForMedicine(medicineId);
        var schedulesPerDay = schedules.Count;

        // Last 7 calendar days including today
        var logs7d = await GetLogsForMedicine(medicineId, DateTime.Today.AddDays(-6), DateTime.Today.AddDays(1).AddSeconds(-1));
        var missedThisWeek = logs7d.Count(l => l.WasTaken == 0);

        var logs30d = await GetLogsForMedicine(medicineId, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1).AddSeconds(-1));

        var takenPerDay = logs30d
            .Where(l => l.WasTaken == 1)
            .GroupBy(l => DateTimeHelper.DatePart(l.ScheduledFor))
            .ToDictionary(g => g.Key, g => g.Count());

        bool AllTaken(string date) =>
            schedulesPerDay > 0 && takenPerDay.TryGetValue(date, out var count) && count >= schedulesPerDay;

        var today = DateTimeHelper.ToDateString(DateTime.Today);
        int streak = AllTaken(today) ? 1 : 0;
        for (int i = 1; i <= 30; i++)
        {
            if (AllTaken(DateTimeHelper.ToDateString(DateTime.Today.AddDays(-i))))
                streak++;
            else
                break;
        }

        return (streak, missedThisWeek);
    }

    // ── Stock ────────────────────────────────────────────────────────────────

    /// <summary>Days of doses the current stock covers; MaxValue only when there are no schedules.</summary>
    public double GetStockDaysRemaining(Medicine medicine, int schedulesPerDay)
    {
        if (schedulesPerDay <= 0) return double.MaxValue;
        return medicine.StockCount / schedulesPerDay;
    }

    /// <summary>True when stock covers fewer than <see cref="AppConstants.LowStockDays"/> days of doses.</summary>
    public bool IsLowStock(Medicine medicine, IEnumerable<MedicineSchedule> schedules)
    {
        var perDay = schedules.Count(s => s.IsActive == 1);
        return GetStockDaysRemaining(medicine, perDay) < AppConstants.LowStockDays;
    }

    // ── Backfill ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks every past slot between the two dates as taken (future slots are skipped). Slots with no log
    /// get a new taken row; slots already auto-logged as missed are flipped to taken. Stock and the active
    /// course's TotalTaken are adjusted by the number of slots changed, which is returned. A backfilled slot
    /// that <see cref="ComputeTotalScheduled"/> had left out only because it passed before the medicine or
    /// schedule was created is added to TotalScheduled too, so the course cannot complete early.
    /// </summary>
    public async Task<int> BackfillPastDoses(int medicineId, DateTime fromDate, DateTime toDate)
    {
        var medicine = await _medicineRepo.GetByIdAsync(medicineId)
            ?? throw new InvalidOperationException("Medicine not found.");
        var schedules = await _scheduleRepo.GetByMedicineIdAsync(medicineId);
        if (schedules.Count == 0) return 0;

        DateTimeHelper.TryParseStored(medicine.StartDate, out var start);

        int count = 0;
        int uncounted = 0;
        for (var date = fromDate.Date; date <= toDate.Date; date = date.AddDays(1))
        {
            var existingLogs = await GetLogsForMedicine(medicineId, date, date.AddDays(1).AddSeconds(-1));

            foreach (var sched in schedules)
            {
                if (!DateTimeHelper.TryParseTime(sched.ScheduleTime, out var time)) continue;

                var scheduledDateTime = date.Add(time);
                // Skip future slots (today's doses not yet due) and slots before the course started
                if (scheduledDateTime > DateTime.Now) continue;
                if (start != default && scheduledDateTime < start) continue;

                var existing = existingLogs.FirstOrDefault(l => l.ScheduleId == sched.Id);
                if (existing is { WasTaken: 1 }) continue;

                await WriteTakenLog(medicine, existing, sched.Id, scheduledDateTime, scheduledDateTime);
                count++;
                if (start != default && IsUncountedBackfillSlot(medicine, sched, start, scheduledDateTime))
                    uncounted++;
            }
        }

        await _medicineRepo.UpdateAsync(medicine);
        await AdjustActiveCourse(medicineId, count, uncounted);

        return count;
    }
}
