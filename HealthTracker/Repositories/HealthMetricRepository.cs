using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class HealthMetricRepository : BaseRepository<HealthMetric>
{
    public HealthMetricRepository(DatabaseService db) : base(db) { }

    public async Task<HealthMetric?> GetLatestByTypeAsync(string metricType)
    {
        var db = await GetDbAsync();
        return await db.Table<HealthMetric>()
            .Where(m => m.MetricType == metricType)
            .OrderByDescending(m => m.RecordedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<List<HealthMetric>> GetHistoryByTypeAsync(string metricType, int count = 30)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<HealthMetric>(
            "SELECT * FROM HealthMetric WHERE MetricType = ? ORDER BY RecordedAt DESC LIMIT ?",
            metricType, count);
    }

    public async Task<List<HealthMetric>> GetAllOrderedAsync()
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<HealthMetric>("SELECT * FROM HealthMetric ORDER BY RecordedAt DESC");
    }

    public async Task<List<HealthMetric>> GetRecentAsync(int count)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<HealthMetric>("SELECT * FROM HealthMetric ORDER BY RecordedAt DESC LIMIT ?", count);
    }
}
