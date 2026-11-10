using HealthTracker.Helpers;
using HealthTracker.Models;
using SQLite;

namespace HealthTracker.Services;

public class DatabaseService
{
    private SQLiteAsyncConnection? _connection;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_connection is not null)
            return _connection;

        // App.OnStart (RescheduleAll) and the first page load both hit this concurrently at launch;
        // serialize initialization so CreateTable/Migrate run exactly once.
        await _initLock.WaitAsync();
        try
        {
            if (_connection is not null)
                return _connection;

            var dbPath = FileStorageHelper.GetDatabasePath();
            var conn = new SQLiteAsyncConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);

            // CreateTableAsync also appends any newly mapped column to an existing table; the migrator
            // then backfills those columns and performs the reshaping sqlite-net cannot.
            await conn.CreateTableAsync<Medicine>();
            await conn.CreateTableAsync<MedicineSchedule>();
            await conn.CreateTableAsync<MedicineLog>();
            await conn.CreateTableAsync<MedicineCourse>();
            await conn.CreateTableAsync<HealthMetric>();
            await conn.CreateTableAsync<MedicalReport>();
            await conn.CreateTableAsync<DoctorVisit>();
            await conn.CreateTableAsync<PendingMedicine>();
            await conn.CreateTableAsync<ReportPage>();
            await conn.CreateTableAsync<LabResult>();
            await conn.CreateTableAsync<UserProfile>();

            await DatabaseMigrator.MigrateAsync(conn, FileStorageHelper.ToStoredPath);

            _connection = conn;
            return _connection;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Closes the open connection (if any). The next <see cref="GetConnectionAsync"/> re-opens the file
    /// and re-runs CreateTable/Migrate, which is what a backup restore needs after replacing the .db file.
    /// </summary>
    public async Task CloseAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_connection is null) return;
            var conn = _connection;
            _connection = null;
            await conn.CloseAsync();
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Closes the connection, runs <paramref name="swap"/> (which replaces the .db file on disk) and leaves
    /// the service unopened, all while holding the init lock. Any concurrent <see cref="GetConnectionAsync"/>
    /// therefore waits until the swap is finished and then opens the new file, instead of re-opening the old
    /// one between close and move. Exceptions from <paramref name="swap"/> propagate; the connection stays
    /// closed either way.
    /// </summary>
    public async Task ReplaceDatabaseAsync(Func<Task> swap)
    {
        await _initLock.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                var conn = _connection;
                _connection = null;
                await conn.CloseAsync();
            }
            await swap();
        }
        finally
        {
            _connection = null;
            _initLock.Release();
        }
    }

    public async Task DeleteAllDataAsync()
    {
        var conn = await GetConnectionAsync();
        await conn.DeleteAllAsync<MedicineLog>();
        await conn.DeleteAllAsync<MedicineSchedule>();
        await conn.DeleteAllAsync<MedicineCourse>();
        await conn.DeleteAllAsync<Medicine>();
        await conn.DeleteAllAsync<HealthMetric>();
        await conn.DeleteAllAsync<MedicalReport>();
        await conn.DeleteAllAsync<DoctorVisit>();
        await conn.DeleteAllAsync<PendingMedicine>();
        await conn.DeleteAllAsync<ReportPage>();
        await conn.DeleteAllAsync<LabResult>();
        await conn.DeleteAllAsync<UserProfile>();
    }
}
