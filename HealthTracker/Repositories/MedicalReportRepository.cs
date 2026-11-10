using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class MedicalReportRepository : BaseRepository<MedicalReport>
{
    public MedicalReportRepository(DatabaseService db) : base(db) { }

    public async Task<List<MedicalReport>> GetAllOrderedAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<MedicalReport>().OrderByDescending(r => r.ReportDate).ToListAsync();
    }

    public async Task<List<MedicalReport>> GetByVisitIdAsync(int visitId)
    {
        var db = await GetDbAsync();
        return await db.Table<MedicalReport>().Where(r => r.DoctorVisitId == visitId).ToListAsync();
    }

    /// <summary>Detaches every report from a doctor visit (used when the visit is deleted).</summary>
    public async Task<int> ClearDoctorVisitAsync(int visitId)
    {
        var db = await GetDbAsync();
        return await db.ExecuteAsync("UPDATE MedicalReport SET DoctorVisitId = NULL WHERE DoctorVisitId = ?", visitId);
    }

    public new async Task<MedicalReport?> GetByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.Table<MedicalReport>().Where(r => r.Id == id).FirstOrDefaultAsync();
    }
}
