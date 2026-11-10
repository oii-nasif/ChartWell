using HealthTracker.Models;
using SQLite;

namespace HealthTracker.Services;

/// <summary>In-memory SQLite replacement for DatabaseService used in tests.</summary>
public class DatabaseService
{
    private SQLiteAsyncConnection? _conn;
    private readonly string _path;

    /// <param name="path">Database file; the default ":memory:" database is pooled by sqlite-net and therefore shared by
    /// every test that uses it. Pass a temp file path for a test that needs a database of its own.</param>
    public DatabaseService(string path = ":memory:") { _path = path; }

    public async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_conn != null) return _conn;
        _conn = new SQLiteAsyncConnection(_path);
        await _conn.CreateTableAsync<Medicine>();
        await _conn.CreateTableAsync<MedicineSchedule>();
        await _conn.CreateTableAsync<MedicineLog>();
        await _conn.CreateTableAsync<MedicineCourse>();
        await _conn.CreateTableAsync<HealthMetric>();
        await _conn.CreateTableAsync<MedicalReport>();
        await _conn.CreateTableAsync<DoctorVisit>();
        await _conn.CreateTableAsync<ReportPage>();
        await _conn.CreateTableAsync<PendingMedicine>();
        await _conn.CreateTableAsync<LabResult>();
        await _conn.CreateTableAsync<UserProfile>();
        return _conn;
    }

    /// <summary>Mirrors the app's DatabaseService.CloseAsync; an in-memory database is simply dropped.</summary>
    public async Task CloseAsync()
    {
        if (_conn is null) return;
        var conn = _conn;
        _conn = null;
        await conn.CloseAsync();
    }

    /// <summary>
    /// Mirrors the app's DatabaseService.ReplaceDatabaseAsync: closes the connection, runs
    /// <paramref name="swap"/>, and leaves the next GetConnectionAsync to open a fresh (empty) in-memory database.
    /// </summary>
    public async Task ReplaceDatabaseAsync(Func<Task> swap)
    {
        await CloseAsync();
        await swap();
        await GetConnectionAsync();
    }
}
