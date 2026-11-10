using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class MedicineRepository : BaseRepository<Medicine>
{
    public MedicineRepository(DatabaseService db) : base(db) { }

    public async Task<List<Medicine>> GetAllActiveAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<Medicine>().Where(m => m.IsActive == 1).ToListAsync();
    }

    public async Task<List<Medicine>> GetAllInactiveAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<Medicine>().Where(m => m.IsActive == 0).ToListAsync();
    }

    public new async Task<Medicine?> GetByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.Table<Medicine>().Where(m => m.Id == id).FirstOrDefaultAsync();
    }
}
