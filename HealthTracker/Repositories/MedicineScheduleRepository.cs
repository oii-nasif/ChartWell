using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class MedicineScheduleRepository : BaseRepository<MedicineSchedule>
{
    public MedicineScheduleRepository(DatabaseService db) : base(db) { }

    public async Task<List<MedicineSchedule>> GetByMedicineIdAsync(int medicineId)
    {
        var db = await GetDbAsync();
        return await db.Table<MedicineSchedule>().Where(s => s.MedicineId == medicineId && s.IsActive == 1).ToListAsync();
    }

    public async Task<MedicineSchedule?> GetScheduleByIdAsync(int scheduleId)
    {
        var db = await GetDbAsync();
        return await db.Table<MedicineSchedule>().Where(s => s.Id == scheduleId).FirstOrDefaultAsync();
    }
}
