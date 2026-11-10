using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class ReportPageRepository : BaseRepository<ReportPage>
{
    public ReportPageRepository(DatabaseService db) : base(db) { }

    public async Task<List<ReportPage>> GetByReportIdAsync(int reportId)
    {
        var db = await GetDbAsync();
        return await db.Table<ReportPage>()
            .Where(p => p.ReportId == reportId)
            .OrderBy(p => p.PageNumber)
            .ToListAsync();
    }

    public async Task DeleteByReportIdAsync(int reportId)
    {
        var db = await GetDbAsync();
        var pages = await db.Table<ReportPage>().Where(p => p.ReportId == reportId).ToListAsync();
        foreach (var page in pages)
            await db.DeleteAsync(page);
    }
}
