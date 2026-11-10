using HealthTracker.Services;
using SQLite;

namespace HealthTracker.Repositories;

public abstract class BaseRepository<T> where T : new()
{
    private readonly DatabaseService _databaseService;

    protected BaseRepository(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    protected async Task<SQLiteAsyncConnection> GetDbAsync()
        => await _databaseService.GetConnectionAsync();

    public async Task<List<T>> GetAllAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<T>().ToListAsync();
    }

    public async Task<T?> GetByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.FindAsync<T>(id);
    }

    public async Task<int> InsertAsync(T item)
    {
        var db = await GetDbAsync();
        return await db.InsertAsync(item);
    }

    public async Task<int> UpdateAsync(T item)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(item);
    }

    public async Task<int> DeleteAsync(T item)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync(item);
    }
}
