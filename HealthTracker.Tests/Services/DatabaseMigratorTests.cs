using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;
using SQLite;

namespace HealthTracker.Tests.Services;

public class DatabaseMigratorTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "HealthTrackerTests", "HealthApp");
    private static string ToStored(string p) => StoredPathHelper.ToStoredPath(p, Base);

    /// <summary>
    /// sqlite-net pools connections by connection string, so ":memory:" would be shared between tests.
    /// Each test gets its own temp file instead.
    /// </summary>
    private static SQLiteAsyncConnection CreateIsolatedDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ht_migrator_{Guid.NewGuid():N}.db");
        return new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
    }

    /// <summary>Builds the pre-versioning schema (DoseAmount / Dosage columns, absolute file paths) with data.</summary>
    private static async Task<SQLiteAsyncConnection> CreateLegacyDatabase()
    {
        var db = CreateIsolatedDatabase();

        await db.ExecuteAsync(@"CREATE TABLE Medicine (
            Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
            Name TEXT NOT NULL,
            Dosage TEXT,
            IsActive INTEGER NOT NULL DEFAULT 1,
            StockCount REAL NOT NULL DEFAULT 0,
            StockUnit TEXT NOT NULL DEFAULT '',
            PrescribedDays INTEGER,
            StartDate TEXT,
            EndDate TEXT,
            Instructions TEXT,
            Notes TEXT,
            CreatedAt TEXT NOT NULL DEFAULT '')");
        await db.ExecuteAsync(@"CREATE TABLE MedicineSchedule (
            Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
            MedicineId INTEGER NOT NULL,
            ScheduleTime TEXT NOT NULL,
            DoseAmount REAL,
            IsActive INTEGER NOT NULL DEFAULT 1)");
        await db.ExecuteAsync(@"CREATE TABLE MedicalReport (
            Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
            DoctorVisitId INTEGER,
            Title TEXT NOT NULL,
            ReportType TEXT NOT NULL,
            FilePath TEXT NOT NULL,
            FileType TEXT NOT NULL,
            ReportDate TEXT NOT NULL,
            DoctorName TEXT, Hospital TEXT, Notes TEXT,
            CreatedAt TEXT NOT NULL)");

        await db.ExecuteAsync("INSERT INTO Medicine (Id, Name, Dosage, StockCount, StockUnit, PrescribedDays, StartDate, CreatedAt) VALUES (1, 'Aspirin', '1 tab', 10, 'Tablet', 5, '2024-01-05', '2024-01-05T09:30:00')");
        await db.ExecuteAsync("INSERT INTO Medicine (Id, Name, Dosage, StockCount, StockUnit, PrescribedDays, StartDate, CreatedAt) VALUES (2, 'Metformin', NULL, 60, 'Tablet', NULL, '2024-02-01', '2024-02-01T20:15:00')");
        await db.ExecuteAsync("INSERT INTO MedicineSchedule (Id, MedicineId, ScheduleTime, DoseAmount) VALUES (11, 1, '08:00', 1.0)");
        await db.ExecuteAsync("INSERT INTO MedicineSchedule (Id, MedicineId, ScheduleTime, DoseAmount) VALUES (12, 1, '20:00', 1.0)");
        await db.ExecuteAsync("INSERT INTO MedicineSchedule (Id, MedicineId, ScheduleTime, DoseAmount) VALUES (13, 2, '09:00', 2.0)");
        await db.ExecuteAsync("INSERT INTO MedicalReport (Id, Title, ReportType, FilePath, FileType, ReportDate, CreatedAt) VALUES (1, 'CBC', 'Lab', ?, 'pdf', '2024-01-10', '2024-01-10T10:00:00')",
            Path.Combine(Base, "reports", "Lab_2024-01-10_abcdef12.pdf"));
        await db.ExecuteAsync("INSERT INTO MedicalReport (Id, Title, ReportType, FilePath, FileType, ReportDate, CreatedAt) VALUES (2, 'Old', 'Scan', ?, 'jpg', '2023-12-01', '2023-12-01T10:00:00')",
            Path.Combine(Path.GetTempPath(), "OldInstall", "files", "HealthApp", "scans", "Scan_2023-12-01_12345678.jpg"));

        return db;
    }

    /// <summary>Mirrors DatabaseService.GetConnectionAsync: sqlite-net table upgrade, then the migrator.</summary>
    private static async Task UpgradeLikeTheApp(SQLiteAsyncConnection db)
    {
        await db.CreateTableAsync<Medicine>();
        await db.CreateTableAsync<MedicineSchedule>();
        await db.CreateTableAsync<MedicineLog>();
        await db.CreateTableAsync<MedicineCourse>();
        await db.CreateTableAsync<HealthMetric>();
        await db.CreateTableAsync<MedicalReport>();
        await db.CreateTableAsync<DoctorVisit>();
        await db.CreateTableAsync<PendingMedicine>();
        await db.CreateTableAsync<ReportPage>();
        await DatabaseMigrator.MigrateAsync(db, ToStored);
    }

    [Fact]
    public async Task Migrate_FromLegacySchema_DropsOldColumnsKeepsRowsAndSetsVersion()
    {
        var db = await CreateLegacyDatabase();

        await UpgradeLikeTheApp(db);

        Assert.Equal(DatabaseMigrator.CurrentVersion, await DatabaseMigrator.GetUserVersionAsync(db));

        var medCols = await DatabaseMigrator.GetColumnsAsync(db, "Medicine");
        Assert.DoesNotContain("Dosage", medCols);
        Assert.Contains("SortOrder", medCols);
        Assert.Contains("IsOngoing", medCols);
        Assert.Contains("ExtensionDays", medCols);

        var schedCols = await DatabaseMigrator.GetColumnsAsync(db, "MedicineSchedule");
        Assert.DoesNotContain("DoseAmount", schedCols);
        Assert.Contains("CreatedAt", schedCols);

        var medicines = await db.Table<Medicine>().OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(["Aspirin", "Metformin"], medicines.Select(m => m.Name));
        Assert.All(medicines, m => Assert.Equal(0, m.ExtensionDays));
        Assert.Equal(0, medicines[1].PrescribedDays); // NULL coalesced

        var schedules = await db.Table<MedicineSchedule>().OrderBy(s => s.Id).ToListAsync();
        Assert.Equal([11, 12, 13], schedules.Select(s => s.Id));
        Assert.Equal("2024-01-05 09:30", schedules[0].CreatedAt);
        Assert.Equal("2024-02-01 20:15", schedules[2].CreatedAt);

        var reports = await db.Table<MedicalReport>().OrderBy(r => r.Id).ToListAsync();
        Assert.Equal("reports/Lab_2024-01-10_abcdef12.pdf", reports[0].FilePath);
        Assert.Equal("scans/Scan_2023-12-01_12345678.jpg", reports[1].FilePath);
    }

    [Fact]
    public async Task Migrate_RunTwice_IsIdempotent()
    {
        var db = await CreateLegacyDatabase();

        await UpgradeLikeTheApp(db);
        var medicinesAfterFirst = await db.Table<Medicine>().OrderBy(m => m.Id).ToListAsync();
        var schedulesAfterFirst = await db.Table<MedicineSchedule>().OrderBy(s => s.Id).ToListAsync();
        var reportsAfterFirst = await db.Table<MedicalReport>().OrderBy(r => r.Id).ToListAsync();

        await UpgradeLikeTheApp(db);

        Assert.Equal(DatabaseMigrator.CurrentVersion, await DatabaseMigrator.GetUserVersionAsync(db));
        var medicines = await db.Table<Medicine>().OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(medicinesAfterFirst.Select(m => (m.Id, m.Name, m.StockCount)), medicines.Select(m => (m.Id, m.Name, m.StockCount)));
        var schedules = await db.Table<MedicineSchedule>().OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(schedulesAfterFirst.Select(s => (s.Id, s.ScheduleTime, s.CreatedAt)), schedules.Select(s => (s.Id, s.ScheduleTime, s.CreatedAt)));
        var reports = await db.Table<MedicalReport>().OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(reportsAfterFirst.Select(r => r.FilePath), reports.Select(r => r.FilePath));
        Assert.Empty(await db.QueryAsync<NameRow>("SELECT name FROM sqlite_master WHERE name LIKE '%_new'"));
    }

    [Fact]
    public async Task Migrate_OnFreshDatabase_JustStampsTheVersion()
    {
        var db = CreateIsolatedDatabase();

        await UpgradeLikeTheApp(db);
        await UpgradeLikeTheApp(db);

        Assert.Equal(DatabaseMigrator.CurrentVersion, await DatabaseMigrator.GetUserVersionAsync(db));
        // A fresh schema can insert straight away (all mapped columns exist)
        await db.InsertAsync(new Medicine { Name = "New", StartDate = "2024-01-01 00:00", CreatedAt = "2024-01-01T00:00:00" });
        await db.InsertAsync(new MedicineSchedule { MedicineId = 1, ScheduleTime = "08:00", CreatedAt = "2024-01-01 00:00" });
        Assert.Equal(1, await db.Table<MedicineSchedule>().CountAsync());
    }

    [Fact]
    public async Task Migrate_RecoversFromAStaleRebuildTableLeftByACrash()
    {
        var db = await CreateLegacyDatabase();
        // Simulate a crash after the old code created the scratch table but before the rename
        await db.ExecuteAsync("CREATE TABLE MedicineSchedule_new (Id INTEGER PRIMARY KEY, Junk TEXT)");

        await UpgradeLikeTheApp(db);

        Assert.Equal(3, await db.Table<MedicineSchedule>().CountAsync());
        Assert.DoesNotContain("Junk", await DatabaseMigrator.GetColumnsAsync(db, "MedicineSchedule"));
    }

    private class NameRow { public string name { get; set; } = ""; }
}
