using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class MedicineCourseRepository : BaseRepository<MedicineCourse>
{
    public MedicineCourseRepository(DatabaseService db) : base(db) { }

    public async Task<List<MedicineCourse>> GetByMedicineIdAsync(int medicineId)
    {
        var db = await GetDbAsync();
        return await db.Table<MedicineCourse>()
            .Where(c => c.MedicineId == medicineId)
            .OrderByDescending(c => c.StartDate)
            .ToListAsync();
    }

    public async Task<MedicineCourse?> GetActiveCourseAsync(int medicineId)
    {
        var db = await GetDbAsync();
        return await db.Table<MedicineCourse>()
            .Where(c => c.MedicineId == medicineId && c.Status == "Active")
            .FirstOrDefaultAsync();
    }

    public async Task<List<MedicineCourse>> GetAllActiveCoursesAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<MedicineCourse>()
            .Where(c => c.Status == "Active")
            .ToListAsync();
    }
}
