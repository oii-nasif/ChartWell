using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;
using NSubstitute;

namespace HealthTracker.Tests.Services;

public class MedicineServiceTests
{
    private static MedicineService CreateService(out DatabaseService db)
    {
        db = new DatabaseService();
        var medicineRepo = new MedicineRepository(db);
        var scheduleRepo = new MedicineScheduleRepository(db);
        var logRepo = new MedicineLogRepository(db);
        var courseRepo = new MedicineCourseRepository(db);
        return new MedicineService(medicineRepo, scheduleRepo, logRepo, courseRepo, new NullReminderService());
    }

    private static MedicineService CreateService() => CreateService(out _);

    private static string Today => DateTimeHelper.ToDateString(DateTime.Today);
    private static string Tomorrow => DateTimeHelper.ToDateString(DateTime.Today.AddDays(1));

    /// <summary>
    /// Pretends the medicine (and its schedule rows) were created at <paramref name="createdAt"/>, so
    /// AutoLogMissedDoses sees past days as reachable (slots before creation are never auto-logged).
    /// </summary>
    private static async Task BackdateCreation(DatabaseService db, int medicineId, DateTime createdAt)
    {
        var conn = await db.GetConnectionAsync();
        await conn.ExecuteAsync("UPDATE Medicine SET CreatedAt = ? WHERE Id = ?",
            DateTimeHelper.ToDateTimeString(createdAt), medicineId);
        await conn.ExecuteAsync("UPDATE MedicineSchedule SET CreatedAt = ? WHERE MedicineId = ?",
            DateTimeHelper.ToDateTimeMinuteString(createdAt), medicineId);
    }

    private static Task<Medicine> AddDefault(MedicineService svc, string name = "Aspirin",
        double stock = 30, int prescribedDays = 10, bool isOngoing = false,
        string? startDate = null, string? endDate = null, List<string>? schedules = null)
        => svc.AddMedicine(name, stock, "Tablet", startDate ?? Today, endDate, null, null,
            prescribedDays, isOngoing, schedules ?? ["08:00", "20:00"]);

    // ── AddMedicine ──────────────────────────────────────────────────────────
    [Fact]
    public async Task AddMedicine_WithValidData_StoresMedicineSchedulesAndCourse()
    {
        var svc = CreateService();

        // Starts tomorrow so every slot of every day is reachable regardless of the current time of day.
        var medicine = await AddDefault(svc, startDate: Tomorrow);

        Assert.True(medicine.Id > 0);
        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.NotNull(stored);
        Assert.Equal("Aspirin", stored.Name);
        Assert.Equal(1, stored.IsActive);

        var storedSchedules = await svc.GetSchedulesForMedicine(medicine.Id);
        Assert.Equal(2, storedSchedules.Count);

        var courses = await svc.GetCoursesForMedicine(medicine.Id);
        var course = Assert.Single(courses);
        Assert.Equal("Active", course.Status);
        Assert.Equal(2, course.SchedulesPerDay);
        Assert.Equal(20, course.TotalScheduled);
        Assert.Equal(0, course.TotalTaken);
    }

    [Fact]
    public async Task AddMedicine_WithNoSchedules_ThrowsInvalidOperationException()
    {
        var svc = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AddDefault(svc, schedules: []));
    }

    [Fact]
    public async Task AddMedicine_ComputesEndDateFromPrescribedDays()
    {
        var svc = CreateService();

        var medicine = await AddDefault(svc, prescribedDays: 10, startDate: "2024-01-01");

        Assert.Equal("2024-01-10", medicine.EndDate);
    }

    [Fact]
    public async Task AddMedicine_WhenOngoing_HasNoEndDateAndZeroTotalScheduled()
    {
        var svc = CreateService();

        var medicine = await AddDefault(svc, isOngoing: true, prescribedDays: 0);

        Assert.Null(medicine.EndDate);
        Assert.Equal(1, medicine.IsOngoing);
        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal(0, course.TotalScheduled);
    }

    [Fact]
    public async Task AddMedicine_AssignsIncreasingSortOrder()
    {
        var svc = CreateService();

        var first = await AddDefault(svc, "First");
        var second = await AddDefault(svc, "Second");

        Assert.True(second.SortOrder > first.SortOrder);
    }

    // ── GetAllActiveMedicines ────────────────────────────────────────────────
    [Fact]
    public async Task GetAllActiveMedicines_ReturnsOnlyActiveMedicines()
    {
        var svc = CreateService();

        await AddDefault(svc, "Active");
        var inactive = await AddDefault(svc, "Inactive");
        await svc.DeactivateMedicine(inactive.Id);

        var active = await svc.GetAllActiveMedicines();
        var inactiveList = await svc.GetAllInactiveMedicines();

        Assert.Contains(active, m => m.Name == "Active");
        Assert.DoesNotContain(active, m => m.Name == "Inactive");
        Assert.Contains(inactiveList, m => m.Name == "Inactive");
    }

    // ── DeleteMedicine ───────────────────────────────────────────────────────
    [Fact]
    public async Task DeleteMedicine_RemovesMedicineSchedulesAndCourses()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc);

        await svc.DeleteMedicine(medicine.Id);

        Assert.Null(await svc.GetMedicineById(medicine.Id));
        Assert.Empty(await svc.GetSchedulesForMedicine(medicine.Id));
        Assert.Empty(await svc.GetCoursesForMedicine(medicine.Id));
    }

    // ── UpdateMedicine ───────────────────────────────────────────────────────
    [Fact]
    public async Task UpdateMedicine_ReplacesSchedulesAndUpdatesActiveCourse()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, prescribedDays: 10, startDate: Tomorrow, schedules: ["08:00"]);

        medicine.Name = "Aspirin Updated";
        await svc.UpdateMedicine(medicine, ["07:00", "12:00", "19:00"]);

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal("Aspirin Updated", stored!.Name);

        var schedules = await svc.GetSchedulesForMedicine(medicine.Id);
        Assert.Equal(3, schedules.Count);

        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal(3, course.SchedulesPerDay);
        Assert.Equal(30, course.TotalScheduled);
    }

    // ── Stock helpers ────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.5, true)]
    [InlineData(1.0, false)]
    [InlineData(5.0, false)]
    public void IsOutOfStock_ReturnsTrueOnlyWhenStockBelowOne(double stock, bool expected)
    {
        var svc = CreateService();
        Assert.Equal(expected, svc.IsOutOfStock(new Medicine { StockCount = stock }));
        Assert.Equal(expected, svc.HasInsufficientStock(new Medicine { StockCount = stock }));
    }

    [Fact]
    public void CanLogDose_IsNotBlockedByEmptyStock()
    {
        var svc = CreateService();
        Assert.True(svc.CanLogDose(new Medicine { StockCount = 0, IsActive = 1 }));
        Assert.False(svc.CanLogDose(new Medicine { StockCount = 10, IsActive = 0 }));
    }

    [Fact]
    public void GetStockDaysRemaining_DividesStockBySchedulesPerDay()
    {
        var svc = CreateService();
        Assert.Equal(5.0, svc.GetStockDaysRemaining(new Medicine { StockCount = 10 }, 2));
    }

    [Fact]
    public void GetStockDaysRemaining_ReturnsMaxOnlyWhenThereAreNoSchedules()
    {
        var svc = CreateService();
        // Ongoing medicines run out of stock too; only "no schedules" is unknowable.
        Assert.Equal(5.0, svc.GetStockDaysRemaining(new Medicine { StockCount = 10, IsOngoing = 1 }, 2));
        Assert.Equal(double.MaxValue, svc.GetStockDaysRemaining(new Medicine { StockCount = 10 }, 0));
    }

    [Fact]
    public void IsLowStock_FlagsOngoingMedicinesBelowThreshold()
    {
        var svc = CreateService();
        var schedules = new List<MedicineSchedule> { new() { ScheduleTime = "08:00", IsActive = 1 } };

        Assert.True(svc.IsLowStock(new Medicine { StockCount = AppConstants.LowStockDays - 1, IsOngoing = 1 }, schedules));
        Assert.False(svc.IsLowStock(new Medicine { StockCount = AppConstants.LowStockDays, IsOngoing = 1 }, schedules));
        Assert.False(svc.IsLowStock(new Medicine { StockCount = 0 }, []));
    }

    [Fact]
    public async Task LogDoseTaken_ShowsRefillReminderOnceWhenStockCrossesLowThreshold()
    {
        var db = new DatabaseService();
        var reminders = Substitute.For<IReminderService>();
        var svc = new MedicineService(new MedicineRepository(db), new MedicineScheduleRepository(db),
            new MedicineLogRepository(db), new MedicineCourseRepository(db), reminders);

        // 2 doses/day, stock 10 → exactly 5 days: not low yet
        var medicine = await AddDefault(svc, stock: 2 * AppConstants.LowStockDays, startDate: Today, schedules: ["08:00", "20:00"]);
        var schedules = await svc.GetSchedulesForMedicine(medicine.Id);

        await svc.LogDoseTaken(medicine.Id, schedules[0].Id, DateTime.Today.AddDays(-1)); // 10 → 9 = 4.5 days: crosses
        await svc.LogDoseTaken(medicine.Id, schedules[1].Id, DateTime.Today.AddDays(-1)); // 9 → 8: still low, no second nudge

        await reminders.Received(1).ShowRefillReminder(medicine.Id);

        // Already low before the dose → no crossing, no nudge
        reminders.ClearReceivedCalls();
        var low = await AddDefault(svc, "Low", stock: 2, startDate: Today, schedules: ["08:00"]);
        var lowSchedule = (await svc.GetSchedulesForMedicine(low.Id))[0];
        await svc.LogDoseTaken(low.Id, lowSchedule.Id, DateTime.Today.AddDays(-1));
        await reminders.DidNotReceive().ShowRefillReminder(low.Id);
    }

    // ── LogDoseTaken ─────────────────────────────────────────────────────────
    [Fact]
    public async Task LogDoseTaken_InsertsLogDecrementsStockAndIncrementsCourse()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, stock: 10, schedules: ["08:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;

        var log = await svc.LogDoseTaken(medicine.Id, scheduleId);

        Assert.NotNull(log);
        Assert.Equal(1, log.WasTaken);
        Assert.Equal(10.0, log.StockBefore);
        Assert.Equal(9.0, log.StockAfter);

        var updated = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(9.0, updated!.StockCount);

        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal(1, course.TotalTaken);
    }

    [Fact]
    public async Task LogDoseTaken_WhenStockIsZero_ClampsToZero()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, stock: 0, schedules: ["08:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;

        var log = await svc.LogDoseTaken(medicine.Id, scheduleId);

        Assert.NotNull(log);
        Assert.Equal(0.0, log.StockAfter);
        Assert.Equal(0.0, (await svc.GetMedicineById(medicine.Id))!.StockCount);
    }

    [Fact]
    public async Task LogDoseTaken_IsIdempotentPerScheduleAndDay()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, stock: 10, schedules: ["08:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;

        var first = await svc.LogDoseTaken(medicine.Id, scheduleId);
        var second = await svc.LogDoseTaken(medicine.Id, scheduleId);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(9.0, (await svc.GetMedicineById(medicine.Id))!.StockCount);
        Assert.Equal(1, Assert.Single(await svc.GetCoursesForMedicine(medicine.Id)).TotalTaken);
        Assert.Single(await svc.GetLogsForMedicine(medicine.Id, DateTime.Today, DateTime.Today.AddDays(1)));
    }

    [Fact]
    public async Task LogDoseTaken_WithScheduledDate_LogsThatDayAndFlipsAnExistingMissedRow()
    {
        var svc = CreateService(out var db);
        var yesterday = DateTime.Today.AddDays(-1);
        var medicine = await AddDefault(svc, stock: 10, startDate: DateTimeHelper.ToDateString(yesterday), schedules: ["08:00"]);
        await BackdateCreation(db, medicine.Id, yesterday);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;
        await svc.AutoLogMissedDoses();
        var missed = Assert.Single(await svc.GetLogsForMedicine(medicine.Id, yesterday, yesterday.AddDays(1).AddSeconds(-1)));
        Assert.Equal(0, missed.WasTaken);

        var log = await svc.LogDoseTaken(medicine.Id, scheduleId, yesterday);

        Assert.NotNull(log);
        Assert.Equal(missed.Id, log.Id);
        Assert.StartsWith(DateTimeHelper.ToDateString(yesterday), log.ScheduledFor);
        var only = Assert.Single(await svc.GetLogsForMedicine(medicine.Id, yesterday, yesterday.AddDays(1).AddSeconds(-1)));
        Assert.Equal(1, only.WasTaken);
        Assert.NotNull(only.TakenAt);
        Assert.Equal(9.0, (await svc.GetMedicineById(medicine.Id))!.StockCount);
    }

    [Fact]
    public async Task LogDoseTaken_WithUnknownMedicine_Throws()
    {
        var svc = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LogDoseTaken(999, 1));
    }

    // ── CheckAndCompleteCourses ──────────────────────────────────────────────
    [Fact]
    public async Task CheckAndCompleteCourses_WhenEndDatePassed_CompletesCourseAndDeactivates()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, prescribedDays: 3, startDate: "2024-01-01");

        await svc.CheckAndCompleteCourses();

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(0, stored!.IsActive);
        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal("Completed", course.Status);
    }

    [Fact]
    public async Task CheckAndCompleteCourses_WhenAllDosesTaken_CompletesCourse()
    {
        var svc = CreateService(out var db);
        // Created yesterday, so today's 08:00 slot counts towards TotalScheduled whatever the current time is.
        var medicine = await AddDefault(svc, prescribedDays: 1, schedules: ["08:00"]);
        await BackdateCreation(db, medicine.Id, DateTime.Today.AddDays(-1));
        medicine = (await svc.GetMedicineById(medicine.Id))!;
        await svc.UpdateMedicine(medicine, ["08:00"]); // recompute TotalScheduled with the backdated creation
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;
        Assert.Equal(1, Assert.Single(await svc.GetCoursesForMedicine(medicine.Id)).TotalScheduled);
        await svc.LogDoseTaken(medicine.Id, scheduleId);

        await svc.CheckAndCompleteCourses();

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(0, stored!.IsActive);
    }

    [Fact]
    public async Task CheckAndCompleteCourses_WhenCourseStillRunning_LeavesActive()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, prescribedDays: 10);

        await svc.CheckAndCompleteCourses();

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(1, stored!.IsActive);
    }

    // ── StartNewCourse ───────────────────────────────────────────────────────
    [Fact]
    public async Task StartNewCourse_ClosesOldCourseAndReactivatesMedicine()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, prescribedDays: 3, startDate: "2024-01-01");
        await svc.CheckAndCompleteCourses();

        await svc.StartNewCourse(medicine.Id, 5, Today);

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(1, stored!.IsActive);
        Assert.Equal(5, stored.PrescribedDays);
        Assert.Equal(DateTimeHelper.ToDateString(DateTime.Today.AddDays(4)), stored.EndDate);

        var courses = await svc.GetCoursesForMedicine(medicine.Id);
        Assert.Equal(2, courses.Count);
        var active = Assert.Single(courses, c => c.Status == "Active");
        Assert.Equal(DateTimeHelper.ToDateTimeMinuteString(DateTimeHelper.ParseDateTime(stored.StartDate)), stored.StartDate);
        Assert.Equal(stored.StartDate, active.StartDate);
    }

    [Fact]
    public async Task StartNewCourse_OnOngoingMedicine_MakesItFixedAndClampsClosingCourseEndDate()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, isOngoing: true, prescribedDays: 0, startDate: Today);

        await svc.StartNewCourse(medicine.Id, 7, Today);

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(0, stored!.IsOngoing);
        Assert.Equal(7, stored.PrescribedDays);
        Assert.Equal(0, stored.ExtensionDays);
        Assert.Equal(DateTimeHelper.ToDateString(DateTime.Today.AddDays(6)), stored.EndDate);

        var closed = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id), c => c.Status == "Completed");
        // The old course started today, so its end date is clamped to today rather than yesterday.
        Assert.Equal(Today, closed.EndDate);
        Assert.True(DateTimeHelper.ParseDate(closed.EndDate!) >= DateTimeHelper.ParseDateTime(closed.StartDate).Date);
    }

    // ── AutoLogMissedDoses ───────────────────────────────────────────────────
    [Fact]
    public async Task AutoLogMissedDoses_LogsEveryUnloggedPastDayAndExtendsEndDateByWholeDays()
    {
        var svc = CreateService(out var db);
        var start = DateTimeHelper.ToDateString(DateTime.Today.AddDays(-2));
        var medicine = await AddDefault(svc, prescribedDays: 10, startDate: start, schedules: ["08:00", "20:00"]);
        await BackdateCreation(db, medicine.Id, DateTime.Today.AddDays(-2));
        var originalEnd = DateTimeHelper.ParseDate(medicine.EndDate!);

        await svc.AutoLogMissedDoses();

        // Two whole past days (day -2 and day -1), two slots each → 4 missed logs, none for today
        var allPast = await svc.GetLogsForMedicine(medicine.Id, DateTime.Today.AddDays(-2), DateTime.Today.AddSeconds(-1));
        Assert.Equal(4, allPast.Count);
        Assert.All(allPast, l => { Assert.Equal(0, l.WasTaken); Assert.Null(l.TakenAt); });
        Assert.Empty(await svc.GetLogsForMedicine(medicine.Id, DateTime.Today, DateTime.Today.AddDays(1)));

        // 4 missed doses / 2 per day = 2 extra days, and EndDate stays a plain date string
        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(DateTimeHelper.ToDateString(originalEnd.AddDays(2)), stored!.EndDate);
        Assert.Equal(2, stored.ExtensionDays);

        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal(stored.EndDate, course.EndDate);
    }

    [Fact]
    public async Task AutoLogMissedDoses_NeverLogsSlotsBeforeTheMedicineWasCreated()
    {
        var svc = CreateService();
        // Backdated start, created just now: the past days are backfill territory, not "missed".
        var start = DateTimeHelper.ToDateString(DateTime.Today.AddDays(-3));
        var medicine = await AddDefault(svc, prescribedDays: 10, startDate: start, schedules: ["08:00", "20:00"]);

        await svc.AutoLogMissedDoses();

        Assert.Empty(await svc.GetLogsForMedicine(medicine.Id, DateTime.Today.AddDays(-10), DateTime.Today.AddDays(1)));
        Assert.Equal(0, (await svc.GetMedicineById(medicine.Id))!.ExtensionDays);
    }

    [Fact]
    public async Task AutoLogMissedDoses_CapsTotalExtensionAtPrescribedDays()
    {
        var svc = CreateService(out var db);
        var start = DateTime.Today.AddDays(-12);
        var medicine = await AddDefault(svc, prescribedDays: 3, startDate: DateTimeHelper.ToDateString(start), schedules: ["08:00"]);
        await BackdateCreation(db, medicine.Id, start);

        await svc.AutoLogMissedDoses();
        await svc.AutoLogMissedDoses(); // a second run must not compound the extension

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(3, stored!.ExtensionDays);
        // 3 prescribed days + 3 extension days → ends on day 6 (start + 5)
        Assert.Equal(DateTimeHelper.ToDateString(start.AddDays(5)), stored.EndDate);

        var logs = await svc.GetLogsForMedicine(medicine.Id, start, DateTime.Today);
        Assert.Equal(6, logs.Count);
        Assert.All(logs, l => Assert.True(DateTimeHelper.ParseDateTime(l.ScheduledFor).Date <= start.AddDays(5)));
    }

    [Fact]
    public async Task AutoLogMissedDoses_SkipsSlotsBeforeTheMedicineWasStarted()
    {
        var svc = CreateService(out var db);
        // Started yesterday at 15:00 → only the 20:00 slot of yesterday was actually due
        var start = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(-1).AddHours(15));
        var medicine = await AddDefault(svc, prescribedDays: 10, startDate: start, schedules: ["08:00", "20:00"]);
        await BackdateCreation(db, medicine.Id, DateTime.Today.AddDays(-1).AddHours(15));

        await svc.AutoLogMissedDoses();

        var yesterday = DateTime.Today.AddDays(-1);
        var logs = await svc.GetLogsForMedicine(medicine.Id, yesterday, yesterday.AddDays(1).AddSeconds(-1));
        var log = Assert.Single(logs);
        Assert.EndsWith("T20:00:00", log.ScheduledFor);
    }

    [Fact]
    public async Task AutoLogMissedDoses_IsIdempotent()
    {
        var svc = CreateService(out var db);
        var start = DateTimeHelper.ToDateString(DateTime.Today.AddDays(-2));
        var medicine = await AddDefault(svc, prescribedDays: 10, startDate: start, schedules: ["08:00"]);
        await BackdateCreation(db, medicine.Id, DateTime.Today.AddDays(-2));

        await svc.AutoLogMissedDoses();
        await svc.AutoLogMissedDoses();

        var yesterday = DateTime.Today.AddDays(-1);
        var logs = await svc.GetLogsForMedicine(medicine.Id, yesterday, yesterday.AddDays(1).AddSeconds(-1));
        Assert.Single(logs);
    }

    [Fact]
    public async Task AutoLogMissedDoses_LogsOngoingMedicinesWithoutEndDateButSkipsNotYetStarted()
    {
        var svc = CreateService(out var db);
        var ongoing = await AddDefault(svc, "Ongoing", isOngoing: true, prescribedDays: 0,
            startDate: DateTimeHelper.ToDateString(DateTime.Today.AddDays(-1)), schedules: ["08:00"]);
        await BackdateCreation(db, ongoing.Id, DateTime.Today.AddDays(-1));
        var future = await AddDefault(svc, "Future", prescribedDays: 5,
            startDate: DateTimeHelper.ToDateString(DateTime.Today.AddDays(2)));
        await BackdateCreation(db, future.Id, DateTime.Today.AddDays(-1));

        await svc.AutoLogMissedDoses();

        var yesterday = DateTime.Today.AddDays(-1);
        Assert.Single(await svc.GetLogsForMedicine(ongoing.Id, yesterday, yesterday.AddDays(1)));
        Assert.Null((await svc.GetMedicineById(ongoing.Id))!.EndDate);
        Assert.Empty(await svc.GetLogsForMedicine(future.Id, yesterday, yesterday.AddDays(1)));
    }

    [Fact]
    public async Task AutoLogMissedDoses_DoesNotLogDaysAfterTheCourseEnded()
    {
        var svc = CreateService(out var db);
        // 2-day course that ended 3 days ago; both days missed → extended by the cap (2 days), so 4 logs
        var start = DateTimeHelper.ToDateString(DateTime.Today.AddDays(-4));
        var medicine = await AddDefault(svc, prescribedDays: 2, startDate: start, schedules: ["08:00"]);
        await BackdateCreation(db, medicine.Id, DateTime.Today.AddDays(-4));

        await svc.AutoLogMissedDoses();

        var logs = await svc.GetLogsForMedicine(medicine.Id, DateTime.Today.AddDays(-10), DateTime.Today);
        Assert.Equal(4, logs.Count);
        Assert.All(logs, l => Assert.True(DateTimeHelper.ParseDateTime(l.ScheduledFor).Date <= DateTime.Today.AddDays(-1)));
        Assert.Equal(DateTimeHelper.ToDateString(DateTime.Today.AddDays(-1)), (await svc.GetMedicineById(medicine.Id))!.EndDate);
    }

    // ── UpdateMedicine keeps schedule ids and recomputes EndDate ─────────────
    [Fact]
    public async Task UpdateMedicine_KeepsUnchangedScheduleIdsSoLogsStayLinked()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, schedules: ["08:00", "20:00"]);
        var before = await svc.GetSchedulesForMedicine(medicine.Id);
        var morningId = before.Single(s => s.ScheduleTime == "08:00").Id;
        await svc.LogDoseTaken(medicine.Id, morningId);

        medicine.Notes = "edited";
        await svc.UpdateMedicine(medicine, ["08:00", "14:00"]);

        var after = await svc.GetSchedulesForMedicine(medicine.Id);
        Assert.Equal(2, after.Count);
        Assert.Equal(morningId, after.Single(s => s.ScheduleTime == "08:00").Id);
        Assert.DoesNotContain(after, s => s.ScheduleTime == "20:00");

        var todayLogs = await svc.GetLogsForMedicine(medicine.Id, DateTime.Today, DateTime.Today.AddDays(1));
        Assert.Contains(todayLogs, l => l.ScheduleId == morningId);
    }

    [Fact]
    public async Task UpdateMedicine_SoftDeletesRemovedSchedulesAndReusesThemWhenTheTimeComesBack()
    {
        var svc = CreateService(out var db);
        var medicine = await AddDefault(svc, startDate: Tomorrow, schedules: ["08:00", "20:00"]);
        var eveningId = (await svc.GetSchedulesForMedicine(medicine.Id)).Single(s => s.ScheduleTime == "20:00").Id;
        var conn = await db.GetConnectionAsync();

        await svc.UpdateMedicine(medicine, ["08:00"]);

        // The row survives (dose logs reference it) but is inactive and hidden from the service.
        Assert.Single(await svc.GetSchedulesForMedicine(medicine.Id));
        var rows = await conn.Table<MedicineSchedule>().Where(s => s.MedicineId == medicine.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows.Single(s => s.Id == eveningId).IsActive);

        await svc.UpdateMedicine(medicine, ["08:00", "20:00"]);

        var after = await svc.GetSchedulesForMedicine(medicine.Id);
        Assert.Equal(2, after.Count);
        Assert.Equal(eveningId, after.Single(s => s.ScheduleTime == "20:00").Id);
        Assert.Equal(2, await conn.Table<MedicineSchedule>().Where(s => s.MedicineId == medicine.Id).CountAsync());

        // Deleting the medicine removes the soft-deleted rows as well.
        await svc.UpdateMedicine(medicine, ["08:00"]);
        await svc.DeleteMedicine(medicine.Id);
        Assert.Equal(0, await conn.Table<MedicineSchedule>().Where(s => s.MedicineId == medicine.Id).CountAsync());
    }

    [Fact]
    public async Task UpdateMedicine_ChangingAScheduleTimeKeepsItsIdAndCreatesNoMissedLogs()
    {
        var svc = CreateService(out var db);
        var twoDaysAgo = DateTime.Today.AddDays(-2);
        var medicine = await AddDefault(svc, startDate: DateTimeHelper.ToDateString(twoDaysAgo), schedules: ["08:00", "20:00"]);
        await BackdateCreation(db, medicine.Id, twoDaysAgo);
        var before = await svc.GetSchedulesForMedicine(medicine.Id);
        var idsBefore = before.Select(s => s.Id).OrderBy(i => i).ToList();
        await svc.AutoLogMissedDoses();
        var logsBefore = (await svc.GetLogsForMedicine(medicine.Id, twoDaysAgo, DateTime.Today)).Count;
        Assert.Equal(4, logsBefore);

        // Edit the morning time only
        await svc.UpdateMedicine(medicine, ["09:00", "20:00"]);
        await svc.AutoLogMissedDoses();

        var after = await svc.GetSchedulesForMedicine(medicine.Id);
        Assert.Equal(idsBefore, after.Select(s => s.Id).OrderBy(i => i).ToList());
        Assert.Equal(["09:00", "20:00"], after.Select(s => s.ScheduleTime).OrderBy(t => t).ToList());
        Assert.Equal(logsBefore, (await svc.GetLogsForMedicine(medicine.Id, twoDaysAgo, DateTime.Today)).Count);
    }

    [Fact]
    public async Task UpdateMedicine_StampsCreatedAtOnNewScheduleRowsOnly()
    {
        var svc = CreateService(out var db);
        var medicine = await AddDefault(svc, startDate: Tomorrow, schedules: ["08:00"]);
        await BackdateCreation(db, medicine.Id, DateTime.Today.AddDays(-5));
        var original = Assert.Single(await svc.GetSchedulesForMedicine(medicine.Id));

        await svc.UpdateMedicine(medicine, ["08:00", "20:00"]);

        var after = await svc.GetSchedulesForMedicine(medicine.Id);
        Assert.Equal(2, after.Count);
        Assert.Equal(original.CreatedAt, after.Single(s => s.Id == original.Id).CreatedAt);
        var added = after.Single(s => s.Id != original.Id);
        Assert.True(DateTimeHelper.TryParseStored(added.CreatedAt, out var created));
        Assert.Equal(DateTime.Today, created.Date);
    }

    [Fact]
    public async Task UpdateMedicine_NormalisesStartDateFormatAndKeepsEndDateOnUnrelatedEdit()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, prescribedDays: 7, startDate: "2024-01-01");
        Assert.Equal("2024-01-01 00:00", medicine.StartDate);

        // The edit screen hands back the same instant in the seconds format
        medicine.StartDate = "2024-01-01T00:00:00";
        medicine.ExtensionDays = 2;
        medicine.EndDate = "2024-01-09";
        medicine.Notes = "changed";
        await svc.UpdateMedicine(medicine, ["08:00", "20:00"]);

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal("2024-01-01 00:00", stored!.StartDate);
        Assert.Equal("2024-01-09", stored.EndDate);
        Assert.Equal(2, stored.ExtensionDays);
    }

    // ── Reachable slots / TotalScheduled ─────────────────────────────────────
    [Fact]
    public async Task AddMedicine_TotalScheduledSkipsFirstDaySlotsBeforeTheStartTime()
    {
        var svc = CreateService();
        var start = DateTimeHelper.ToDateTimeMinuteString(DateTime.Today.AddDays(1).AddHours(12));

        var medicine = await AddDefault(svc, prescribedDays: 3, startDate: start, schedules: ["08:00", "20:00"]);

        // Day 1 only has the 20:00 slot → 1 + 2 + 2
        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal(5, course.TotalScheduled);
    }

    [Fact]
    public void ComputeTotalScheduled_SkipsSlotsPassedBeforeCreationOnTheCreationDayOnly()
    {
        var medicine = new Medicine { StartDate = "2024-01-01 00:00", PrescribedDays = 2, CreatedAt = "2024-01-01T10:00:00" };
        var schedules = new List<MedicineSchedule>
        {
            new() { ScheduleTime = "08:00", IsActive = 1 },
            new() { ScheduleTime = "20:00", IsActive = 1 }
        };

        Assert.Equal(3, MedicineService.ComputeTotalScheduled(medicine, schedules));

        // Created days after a backdated start: the earlier days are backfillable and still count
        medicine.CreatedAt = "2024-01-05T10:00:00";
        Assert.Equal(4, MedicineService.ComputeTotalScheduled(medicine, schedules));

        medicine.IsOngoing = 1;
        Assert.Equal(0, MedicineService.ComputeTotalScheduled(medicine, schedules));
    }

    [Fact]
    public void GetReachableSlotsForDay_HonoursStartCreationScheduleCreationAndEnd()
    {
        var medicine = new Medicine
        {
            StartDate = "2024-01-01 09:00", EndDate = "2024-01-03", PrescribedDays = 3,
            CreatedAt = "2024-01-01T10:00:00", IsActive = 1
        };
        var morning = new MedicineSchedule { Id = 1, ScheduleTime = "08:00", IsActive = 1, CreatedAt = "2024-01-01 10:00" };
        var noon = new MedicineSchedule { Id = 2, ScheduleTime = "12:00", IsActive = 1, CreatedAt = "2024-01-02 11:00" };
        var evening = new MedicineSchedule { Id = 3, ScheduleTime = "20:00", IsActive = 1, CreatedAt = "2024-01-01 10:00" };
        var schedules = new List<MedicineSchedule> { evening, noon, morning };

        // Day 1: 08:00 is before start/creation, 12:00 schedule did not exist yet → only 20:00
        Assert.Equal([3], MedicineService.GetReachableSlotsForDay(medicine, schedules, new DateTime(2024, 1, 1)).Select(s => s.Id));
        // Day 2: noon schedule created at 11:00 → 08:00, 12:00, 20:00 all reachable (ordered by time)
        Assert.Equal([1, 2, 3], MedicineService.GetReachableSlotsForDay(medicine, schedules, new DateTime(2024, 1, 2)).Select(s => s.Id));
        // After the end date nothing is reachable
        Assert.Empty(MedicineService.GetReachableSlotsForDay(medicine, schedules, new DateTime(2024, 1, 4)));
        Assert.False(MedicineService.IsSlotReachable(medicine, new MedicineSchedule { ScheduleTime = "bad", IsActive = 1 }, new DateTime(2024, 1, 2)));
    }

    [Fact]
    public async Task UpdateMedicine_RecomputesEndDateWhenPrescribedDaysChange()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, prescribedDays: 7, startDate: "2024-01-01");
        Assert.Equal("2024-01-07", medicine.EndDate);

        medicine.PrescribedDays = 14;
        await svc.UpdateMedicine(medicine, ["08:00", "20:00"]);

        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal("2024-01-14", stored!.EndDate);
        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal("2024-01-14", course.EndDate);
        Assert.Equal(28, course.TotalScheduled);
    }

    [Fact]
    public async Task DeleteMedicine_AlsoRemovesDoseLogs()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, schedules: ["08:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;
        await svc.LogDoseTaken(medicine.Id, scheduleId);

        await svc.DeleteMedicine(medicine.Id);

        Assert.Empty(await svc.GetLogsForMedicine(medicine.Id, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1)));
    }

    // ── BackfillPastDoses ────────────────────────────────────────────────────
    [Fact]
    public async Task BackfillPastDoses_InsertsTakenLogsAndReducesStock()
    {
        var svc = CreateService();
        var start = DateTime.Today.AddDays(-3);
        var medicine = await AddDefault(svc, stock: 20, prescribedDays: 10,
            startDate: DateTimeHelper.ToDateString(start), schedules: ["08:00", "20:00"]);

        var count = await svc.BackfillPastDoses(medicine.Id, start, DateTime.Today.AddDays(-1));

        Assert.Equal(6, count);
        var stored = await svc.GetMedicineById(medicine.Id);
        Assert.Equal(14.0, stored!.StockCount);
        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal(6, course.TotalTaken);
    }

    [Fact]
    public async Task BackfillPastDoses_DoesNotDuplicateExistingLogs()
    {
        var svc = CreateService();
        var start = DateTime.Today.AddDays(-1);
        var medicine = await AddDefault(svc, stock: 20, prescribedDays: 10,
            startDate: DateTimeHelper.ToDateString(start), schedules: ["08:00"]);

        var first = await svc.BackfillPastDoses(medicine.Id, start, start);
        var second = await svc.BackfillPastDoses(medicine.Id, start, start);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task BackfillPastDoses_FlipsAutoLoggedMissedRowsToTaken()
    {
        var svc = CreateService(out var db);
        var start = DateTime.Today.AddDays(-2);
        var medicine = await AddDefault(svc, stock: 20, prescribedDays: 10,
            startDate: DateTimeHelper.ToDateString(start), schedules: ["08:00", "20:00"]);
        await BackdateCreation(db, medicine.Id, start);
        await svc.AutoLogMissedDoses();
        Assert.Equal(4, (await svc.GetLogsForMedicine(medicine.Id, start, DateTime.Today)).Count(l => l.WasTaken == 0));

        var count = await svc.BackfillPastDoses(medicine.Id, start, DateTime.Today.AddDays(-1));

        Assert.Equal(4, count);
        var logs = await svc.GetLogsForMedicine(medicine.Id, start, DateTime.Today);
        Assert.Equal(4, logs.Count); // flipped in place, not duplicated
        Assert.All(logs, l => { Assert.Equal(1, l.WasTaken); Assert.NotNull(l.TakenAt); });
        Assert.Equal(16.0, (await svc.GetMedicineById(medicine.Id))!.StockCount);
        Assert.Equal(4, Assert.Single(await svc.GetCoursesForMedicine(medicine.Id)).TotalTaken);
    }

    [Fact]
    public async Task BackfillPastDoses_AddsSlotsThatPassedBeforeCreationToTotalScheduled()
    {
        var svc = CreateService(out var db);
        var yesterday = DateTime.Today.AddDays(-1);
        var medicine = await AddDefault(svc, stock: 20, prescribedDays: 2,
            startDate: DateTimeHelper.ToDateString(yesterday), schedules: ["08:00", "20:00"]);
        // Created yesterday at noon: yesterday's 08:00 slot had already passed, so TotalScheduled is 3 of 4.
        await BackdateCreation(db, medicine.Id, yesterday.AddHours(12));
        medicine = (await svc.GetMedicineById(medicine.Id))!;
        await svc.UpdateMedicine(medicine, ["08:00", "20:00"]);
        Assert.Equal(3, Assert.Single(await svc.GetCoursesForMedicine(medicine.Id)).TotalScheduled);

        var count = await svc.BackfillPastDoses(medicine.Id, yesterday, yesterday);

        // Both of yesterday's slots are taken; the pre-creation 08:00 one is now counted as scheduled too,
        // so the course is not complete after the two remaining (today's) doses are skipped.
        Assert.Equal(2, count);
        var course = Assert.Single(await svc.GetCoursesForMedicine(medicine.Id));
        Assert.Equal(2, course.TotalTaken);
        Assert.Equal(4, course.TotalScheduled);

        // Re-running changes nothing (already taken slots are skipped).
        Assert.Equal(0, await svc.BackfillPastDoses(medicine.Id, yesterday, yesterday));
        Assert.Equal(4, Assert.Single(await svc.GetCoursesForMedicine(medicine.Id)).TotalScheduled);
    }

    // ── GetAdherenceStats ────────────────────────────────────────────────────
    [Fact]
    public async Task GetAdherenceStats_WithNoLogs_ReturnsZeroStreakAndZeroMissed()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, schedules: ["08:00"]);

        var (streak, missedThisWeek) = await svc.GetAdherenceStats(medicine.Id);

        Assert.Equal(0, streak);
        Assert.Equal(0, missedThisWeek);
    }

    [Fact]
    public async Task GetAdherenceStats_WithTodayTaken_ReturnsStreak1()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, schedules: ["08:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;

        await svc.LogDoseTaken(medicine.Id, scheduleId);

        var (streak, missedThisWeek) = await svc.GetAdherenceStats(medicine.Id);
        Assert.Equal(1, streak);
        Assert.Equal(0, missedThisWeek);
    }

    [Fact]
    public async Task GetAdherenceStats_WithMissedLog_CountsMissedThisWeek()
    {
        var svc = CreateService(out var db);
        var medicine = await AddDefault(svc, schedules: ["08:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;

        var conn = await db.GetConnectionAsync();
        await conn.InsertAsync(new MedicineLog
        {
            MedicineId = medicine.Id,
            ScheduleId = scheduleId,
            ScheduledFor = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(-1).AddHours(8)),
            TakenAt = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(-1).AddHours(8)),
            WasTaken = 0,
            StockBefore = 10,
            StockAfter = 10
        });

        var (_, missedThisWeek) = await svc.GetAdherenceStats(medicine.Id);
        Assert.Equal(1, missedThisWeek);
    }

    [Fact]
    public async Task GetAdherenceStats_WithTwoDayStreak_ReturnsStreak2()
    {
        var svc = CreateService(out var db);
        var medicine = await AddDefault(svc, schedules: ["08:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;

        await svc.LogDoseTaken(medicine.Id, scheduleId);

        var conn = await db.GetConnectionAsync();
        await conn.InsertAsync(new MedicineLog
        {
            MedicineId = medicine.Id,
            ScheduleId = scheduleId,
            ScheduledFor = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(-1).AddHours(8)),
            TakenAt = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(-1).AddHours(8).AddMinutes(5)),
            WasTaken = 1,
            StockBefore = 9,
            StockAfter = 8
        });

        var (streak, _) = await svc.GetAdherenceStats(medicine.Id);
        Assert.Equal(2, streak);
    }

    [Fact]
    public async Task GetAdherenceStats_StreakRequiresAllDailyDoses()
    {
        var svc = CreateService();
        var medicine = await AddDefault(svc, schedules: ["08:00", "20:00"]);
        var scheduleId = (await svc.GetSchedulesForMedicine(medicine.Id))[0].Id;

        await svc.LogDoseTaken(medicine.Id, scheduleId);

        var (streak, _) = await svc.GetAdherenceStats(medicine.Id);
        Assert.Equal(0, streak);
    }
}
