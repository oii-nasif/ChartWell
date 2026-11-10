using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class UserProfileRepository : BaseRepository<UserProfile>
{
    public UserProfileRepository(DatabaseService db) : base(db) { }

    /// <summary>The stored profile, or an empty (unsaved) one when the user has not filled it in yet.</summary>
    public async Task<UserProfile> GetAsync()
    {
        var db = await GetDbAsync();
        return await db.FindAsync<UserProfile>(UserProfile.SingletonId) ?? new UserProfile();
    }

    public async Task SaveAsync(UserProfile profile)
    {
        profile.Id = UserProfile.SingletonId;
        var db = await GetDbAsync();
        await db.InsertOrReplaceAsync(profile);
    }
}
