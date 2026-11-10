using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class LabResultRepository : BaseRepository<LabResult>
{
    public LabResultRepository(DatabaseService db) : base(db) { }

    /// <summary>Newest collection date first; rows of one day keep their entry order.</summary>
    public async Task<List<LabResult>> GetAllOrderedAsync()
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<LabResult>("SELECT * FROM LabResult ORDER BY CollectedOn DESC, Id DESC");
    }

    public async Task<List<LabResult>> GetByTestAsync(string testCode)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<LabResult>(
            "SELECT * FROM LabResult WHERE TestCode = ? ORDER BY CollectedOn DESC, Id DESC", testCode);
    }

    public async Task<List<LabResult>> GetByBatchAsync(string batchId)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<LabResult>("SELECT * FROM LabResult WHERE BatchId = ? ORDER BY Id", batchId);
    }

    public async Task<List<LabResult>> GetByReportAsync(int reportId)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<LabResult>("SELECT * FROM LabResult WHERE ReportId = ? ORDER BY Id", reportId);
    }

    public async Task InsertAllAsync(IEnumerable<LabResult> rows)
    {
        var db = await GetDbAsync();
        await db.InsertAllAsync(rows, runInTransaction: true);
    }

    public async Task<int> DeleteBatchAsync(string batchId)
    {
        var db = await GetDbAsync();
        return await db.ExecuteAsync("DELETE FROM LabResult WHERE BatchId = ?", batchId);
    }

    /// <summary>Keeps the values but drops the link when the source report is deleted.</summary>
    public async Task<int> DetachReportAsync(int reportId)
    {
        var db = await GetDbAsync();
        return await db.ExecuteAsync("UPDATE LabResult SET ReportId = NULL WHERE ReportId = ?", reportId);
    }

    public async Task<int> CountAsync()
    {
        var db = await GetDbAsync();
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LabResult");
    }
}
