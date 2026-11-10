using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class DoctorVisitRepository : BaseRepository<DoctorVisit>
{
    public DoctorVisitRepository(DatabaseService db) : base(db) { }

    public async Task<List<DoctorVisit>> GetAllOrderedAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<DoctorVisit>().OrderByDescending(v => v.VisitDate).ToListAsync();
    }

    public new async Task<DoctorVisit?> GetByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.Table<DoctorVisit>().Where(v => v.Id == id).FirstOrDefaultAsync();
    }
}
