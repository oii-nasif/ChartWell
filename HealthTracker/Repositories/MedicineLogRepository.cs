using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class MedicineLogRepository : BaseRepository<MedicineLog>
{
    public MedicineLogRepository(DatabaseService db) : base(db) { }

    public async Task<List<MedicineLog>> GetLogsForMedicineAsync(int medicineId, string fromDate, string toDate)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<MedicineLog>(
            "SELECT * FROM MedicineLog WHERE MedicineId = ? AND ScheduledFor >= ? AND ScheduledFor <= ? ORDER BY ScheduledFor DESC",
            medicineId, fromDate, toDate);
    }

    /// <summary>The log (if any) for one schedule slot on one calendar day ("yyyy-MM-dd").</summary>
    public async Task<MedicineLog?> GetLogForSlotAsync(int scheduleId, string dayKey)
    {
        var db = await GetDbAsync();
        var rows = await db.QueryAsync<MedicineLog>(
            "SELECT * FROM MedicineLog WHERE ScheduleId = ? AND ScheduledFor >= ? AND ScheduledFor < ? ORDER BY WasTaken DESC, Id LIMIT 1",
            scheduleId, dayKey, dayKey + "T99");
        return rows.FirstOrDefault();
    }

    public async Task<List<MedicineLog>> GetAllOrderedAsync()
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<MedicineLog>("SELECT * FROM MedicineLog ORDER BY ScheduledFor DESC");
    }

    public async Task<List<MedicineLog>> GetRecentLogsAsync(int count)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<MedicineLog>("SELECT * FROM MedicineLog ORDER BY ScheduledFor DESC LIMIT ?", count);
    }

    public async Task<List<MedicineLog>> GetUnloggedSchedulesBeforeAsync(string beforeDateTime)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<MedicineLog>(
            "SELECT * FROM MedicineLog WHERE WasTaken = 0 AND TakenAt IS NULL AND ScheduledFor < ?",
            beforeDateTime);
    }

    public async Task<int> DeleteByMedicineIdAsync(int medicineId)
    {
        var db = await GetDbAsync();
        return await db.ExecuteAsync("DELETE FROM MedicineLog WHERE MedicineId = ?", medicineId);
    }
}
